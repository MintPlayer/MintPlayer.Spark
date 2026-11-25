using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkStore"/> over a relational database (#536 D1, D2): one <see cref="SparkDbContext"/> per session.
/// Reads see every committed write, so there is never anything to wait for.
/// </summary>
public sealed class RelationalSparkStore : ISparkStore
{
    private readonly DbContextOptions<SparkDbContext> options;

    public RelationalSparkStore(SparkContextSetup setup, string connectionString, Action<DbContextOptionsBuilder>? configure = null)
    {
        Setup = setup;
        var builder = new DbContextOptionsBuilder<SparkDbContext>();
        setup.Dialect.UseProvider(builder, connectionString);
        configure?.Invoke(builder);
        options = builder.Options;
    }

    public SparkContextSetup Setup { get; }

    public SparkRelationalModel Model => Setup.Model;

    public string ProviderName => Setup.Dialect.ProviderName;

    public SparkStoreCapabilities Capabilities => SparkStoreCapabilities.None;

    /// <summary>A new context over the store's database; the caller disposes it.</summary>
    public SparkDbContext NewContext() => new(options, Setup);

    public ISparkSession OpenSession() => new RelationalSparkSession(NewContext());

    public string GetCollectionName(Type type) => Model.TableOf(type)?.Name ?? SparkCollectionNames.Of(type);

    public Task<bool> WaitForConsistencyAsync(
        ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
