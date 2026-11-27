using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// A relational test database (#536 D35), shared by <c>MintPlayer.Spark.Testing.SqlServer</c> and <c>.PostgreSql</c>
/// (linked source). A host on it creates its schema from the model instead of from migrations: the test has none. Each
/// later host on the same database applies only what its model changes against the one before.
/// </summary>
internal sealed partial class RelationalSparkTestDatabase : ISparkTestDatabase
{
    private readonly Action<ISparkBuilder, string> useProvider;
    private readonly Func<CancellationToken, Task> drop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IRelationalModel? applied;

    public RelationalSparkTestDatabase(string providerName, string connectionString, Action<ISparkBuilder, string> useProvider, Func<CancellationToken, Task> drop)
    {
        ProviderName = providerName;
        ConnectionString = connectionString;
        this.useProvider = useProvider;
        this.drop = drop;
    }

    public string ProviderName { get; }

    /// <summary>This database's connection string.</summary>
    public string ConnectionString { get; }

    public void UseProvider(ISparkBuilder spark) => useProvider(spark, ConnectionString);

    public void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<ISparkSchemaMigrator>();
        services.AddSingleton<ISparkSchemaMigrator>(sp => new ModelMigrator(this, sp.GetRequiredService<RelationalSparkStore>()));
    }

    public Task WaitForConsistencyAsync(IServiceProvider services, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await drop(CancellationToken.None);
        gate.Dispose();
    }

    /// <summary>The names this harness gives its databases, the only ones it splices into DROP DATABASE.</summary>
    public static string NewName() => "spark_t_" + Guid.NewGuid().ToString("N")[..12];

    /// <exception cref="ArgumentException">Not a name <see cref="NewName"/> gave.</exception>
    public static string Checked(string name)
        => NamePattern().IsMatch(name) ? name : throw new ArgumentException($"Not a test database name: {name}", nameof(name));

    [GeneratedRegex("^spark_t_[0-9a-f]{12}$")]
    private static partial Regex NamePattern();

    private async Task ApplyAsync(RelationalSparkStore store, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (applied is null)
                await RelationalSchema.ApplyFrameworkAsync(store, cancellationToken);
            await using var app = store.NewContext();
            applied = await RelationalSchema.ApplyModelDifferencesAsync(app, applied, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The host's startup migration: the model's schema, as <c>RelationalSchema.CreateFromModelAsync</c> builds it.</summary>
    private sealed class ModelMigrator(RelationalSparkTestDatabase database, RelationalSparkStore store) : ISparkSchemaMigrator
    {
        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task ApplyAsync(CancellationToken cancellationToken = default) => database.ApplyAsync(store, cancellationToken);

        public Task WriteScriptAsync(TextWriter output, string? fromMigration, string? toMigration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A test database is built from the model; it has no migrations to script.");
    }
}
