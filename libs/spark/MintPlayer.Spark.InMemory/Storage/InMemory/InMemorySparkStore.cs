using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// <see cref="ISparkStore"/> over the process's <see cref="InMemoryDatabase"/>. Writes are visible the moment they
/// commit, so there is never anything to wait for, and the change feed sees every commit (one process).
/// </summary>
[Register(typeof(ISparkStore), ServiceLifetime.Singleton)]
internal sealed partial class InMemorySparkStore : ISparkStore
{
    [Inject] private readonly InMemoryDatabase database;

    public string ProviderName => "InMemory";

    public SparkStoreCapabilities Capabilities => SparkStoreCapabilities.ChangeNotifications;

    public ISparkSession OpenSession() => new InMemorySparkSession(database);

    public string GetCollectionName(Type type) => InMemoryConventions.GetCollectionName(type);

    public Task<bool> WaitForConsistencyAsync(
        ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
