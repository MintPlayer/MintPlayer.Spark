using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;

namespace MintPlayer.Spark.Relational;

/// <summary>What <c>UseSqlServer()</c> and <c>UsePostgreSql()</c> configure.</summary>
public sealed class SparkRelationalOptions
{
    /// <summary>The connection string; when empty, the configuration's <c>ConnectionStrings:Spark</c>.</summary>
    public string? ConnectionString { get; set; }

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
            var model = SparkRelationalModel.Build(sp.GetRequiredService<ISparkStoredTypes>().Types, type => KindOf(type, idOptions));
            var setup = new SparkContextSetup(model, dialect, options.ModelConfigurations);
            return new RelationalSparkStore(setup, connectionString, b => options.ContextConfigurations.ForEach(c => c(b)));
        });
        services.AddSingleton<ISparkStore>(sp => sp.GetRequiredService<RelationalSparkStore>());
        services.AddScoped<ISparkSession>(sp => sp.GetRequiredService<ISparkStore>().OpenSession());
        services.AddScoped<ISparkQueryProvider>(sp => new RelationalSparkQueryProvider(sp.GetRequiredService<ISparkSession>()));
        return builder;
    }

    /// <summary><see cref="ISparkIds.KindOf"/>'s rule: the app model's choice, else the attribute, else Guid.</summary>
    public static SparkIdKind KindOf(Type type, SparkIdOptions options)
        => options.Kinds.TryGetValue(type, out var kind) ? kind : type.GetCustomAttribute<IdKindAttribute>()?.Kind ?? SparkIdKind.Guid;
}
