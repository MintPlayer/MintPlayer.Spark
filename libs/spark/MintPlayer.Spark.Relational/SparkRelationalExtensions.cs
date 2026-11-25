using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;

namespace MintPlayer.Spark.Relational;

/// <summary>What <c>UseSqlServer()</c> and <c>UsePostgreSql()</c> configure.</summary>
public sealed class SparkRelationalOptions
{
    /// <summary>The connection string; when empty, the configuration's <c>ConnectionStrings:Spark</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The assembly that holds the app's migrations (D33); when empty, the entry assembly, where synchronize writes
    /// them (<c>Migrations/&lt;Provider&gt;</c> in the host project).
    /// </summary>
    public string? MigrationsAssembly { get; set; }

    /// <summary>
    /// How often the change feed's watches poll the database (D6); when empty,
    /// <see cref="RelationalSparkChangeFeed.DefaultPollInterval"/>. Each poll is one query when nothing has committed.
    /// </summary>
    public TimeSpan? ChangeFeedPollInterval { get; set; }

    internal List<Action<ModelBuilder>> ModelConfigurations { get; } = [];

    internal List<Action<DbContextOptionsBuilder>> ContextConfigurations { get; } = [];

    /// <summary>
    /// Adjusts the EF model Spark builds from the Spark model (O9): an index, a column type, a table name. Runs last in
    /// <c>OnModelCreating</c>, so it can override what Spark mapped. The migration synchronize writes follows it.
    /// </summary>
    public SparkRelationalOptions ConfigureModel(Action<ModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ModelConfigurations.Add(configure);
        return this;
    }

    /// <summary>Adjusts the context options: logging, interceptors.</summary>
    public SparkRelationalOptions ConfigureContext(Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ContextConfigurations.Add(configure);
        return this;
    }
}

/// <summary>The registration both dialect packages share (#536 D2, D34).</summary>
public static class SparkRelationalExtensions
{
    /// <summary>Registers the relational store, sessions and query provider for <paramref name="dialect"/>.</summary>
    public static ISparkBuilder UseRelational(this ISparkBuilder builder, SparkRelationalDialect dialect, Action<SparkRelationalOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dialect);
        var options = new SparkRelationalOptions();
        configure?.Invoke(options);
        var connectionString = options.ConnectionString ?? builder.Configuration?.GetConnectionString("Spark");

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(sp =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"Spark store {dialect.ProviderName}: no connection string. Set ConnectionStrings:Spark, or pass one to Use{dialect.ProviderName}().");
            var idOptions = sp.GetService<IOptions<SparkIdOptions>>()?.Value ?? new SparkIdOptions();
            // D32: the views declared in the app's index assemblies, the ones the index catalog lists.
            var views = (sp.GetService<SparkModuleRegistry>()?.ResolveIndexAssemblies() ?? [])
                .SelectMany(Abstractions.Views.SparkViewEntries.ViewTypes).Distinct().Select(Abstractions.Views.SparkViewEntries.Instantiate);
            var storedTypes = sp.GetRequiredService<ISparkStoredTypes>();
            var model = SparkRelationalModel.Build(storedTypes.Types, type => KindOf(type, idOptions), views);
            var setup = new SparkContextSetup(model, dialect, options.ModelConfigurations, storedTypes.AttributeId);
            return new RelationalSparkStore(setup, connectionString, b => options.ContextConfigurations.ForEach(c => c(b)),
                options.MigrationsAssembly ?? Assembly.GetEntryAssembly()?.GetName().Name);
        });
        services.AddSingleton<ISparkSchemaMigrator>(sp => new RelationalSchemaMigrator(sp.GetRequiredService<RelationalSparkStore>()));
        // Synchronize's migration writer (O17): a development dependency core loads by name, never named in code.
        services.AddSingleton(new SparkSchemaDesignAssembly("MintPlayer.Spark.Relational.Design"));
        services.AddSingleton<ISparkStore>(sp => sp.GetRequiredService<RelationalSparkStore>());
        services.AddScoped<ISparkSession>(sp => sp.GetRequiredService<ISparkStore>().OpenSession());
        services.AddScoped<ISparkQueryProvider>(sp => new RelationalSparkQueryProvider(sp.GetRequiredService<ISparkSession>()));
        services.AddSingleton<ISparkAtomicValues>(sp => new RelationalAtomicValues(sp.GetRequiredService<RelationalSparkStore>()));
        services.AddSingleton<ISparkIntegerIdSource>(sp => new RelationalIntegerIdSource(sp.GetRequiredService<ISparkAtomicValues>(), sp.GetRequiredService<RelationalSparkStore>()));
        services.AddSingleton<ISparkDataProtectionKeyStore>(sp => new RelationalDataProtectionKeyStore(sp.GetRequiredService<RelationalSparkStore>()));
        // D6: live updates over the stamps and the tombstones (M4e).
        services.AddSingleton<ISparkChangeFeed>(sp => new RelationalSparkChangeFeed(
            sp.GetRequiredService<RelationalSparkStore>(), options.ChangeFeedPollInterval,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RelationalSparkChangeFeed>>()));
        // D39: subscriptions as a cursor over the same stamps, one runner per name through the store lock.
        services.AddSingleton<ISparkSubscriptions>(sp => new RelationalSparkSubscriptions(
            sp.GetRequiredService<RelationalSparkStore>(), sp.GetRequiredService<ISparkAtomicValues>(),
            sp.GetRequiredService<ISparkStoreLock>(), sp.GetService<ISparkChangeFeed>(), options.ChangeFeedPollInterval));
        services.AddSingleton<ISparkStoreExpiration>(sp => new RelationalExpiration(
            sp.GetRequiredService<RelationalSparkStore>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<RelationalExpiration>>()));
        return builder;
    }

    /// <summary><see cref="ISparkIds.KindOf"/>'s rule: the app model's choice, else the attribute, else Guid.</summary>
    public static SparkIdKind KindOf(Type type, SparkIdOptions options)
        => options.Kinds.TryGetValue(type, out var kind) ? kind : type.GetCustomAttribute<IdKindAttribute>()?.Kind ?? SparkIdKind.Guid;
}
