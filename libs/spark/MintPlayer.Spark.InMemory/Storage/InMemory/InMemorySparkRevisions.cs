using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// The in-memory store's <see cref="ISparkRevisions"/> (#536 S8): <see cref="InMemoryDatabase"/> keeps a revision per
/// commit of a configured collection, under the commit's lock. Versions are the store's own (<c>A:n</c>), unique for
/// the process, so a revision's version names it alone.
/// </summary>
[Register(typeof(ISparkRevisions), ServiceLifetime.Singleton)]
internal sealed partial class InMemorySparkRevisions : ISparkRevisions
{
    [Inject] private readonly InMemoryDatabase database;

    public Task<IReadOnlyList<string>> ConfigureAsync(IReadOnlyList<SparkRevisionCollection> collections, CancellationToken cancellationToken = default)
        => Task.FromResult(database.ConfigureRevisions(collections));

    public Task<SparkRevisionLimits?> GetLimitsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SparkRevisionLimits?>(null);

    public Task<IReadOnlyList<SparkRevisionInfo>> ListAsync(Type entityType, string id, int skip, int take, CancellationToken cancellationToken = default)
    {
        var collection = InMemoryConventions.GetCollectionName(entityType);
        IReadOnlyList<SparkRevisionInfo> page = [.. database.RevisionsOf(id)
            .Where(r => string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase))
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .Select(r => new SparkRevisionInfo(r.Version, r.At, r.Json is null))];
        return Task.FromResult(page);
    }

    public Task<IReadOnlyDictionary<string, object>> GetAsync(Type entityType, string id, IReadOnlyCollection<string> versions, CancellationToken cancellationToken = default)
    {
        var collection = InMemoryConventions.GetCollectionName(entityType);
        var wanted = versions.ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, object> found = database.RevisionsOf(id)
            .Where(r => r.Json is not null && wanted.Contains(r.Version)
                && string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase)
                && r.ClrType is not null && entityType.IsAssignableFrom(r.ClrType))
            .DistinctBy(r => r.Version)
            .ToDictionary(r => r.Version, r => SparkDocumentJson.Deserialize(r.Json!, r.ClrType!), StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    public Task EnsureCanDeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<long> DeleteAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => Task.FromResult(database.DeleteRevisions(id));
}
