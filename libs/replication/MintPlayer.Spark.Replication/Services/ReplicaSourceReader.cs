using System.Reflection;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Reads the current state of published documents as contract records (owner side, HTTP transport). Every push,
/// digest fetch and reconcile goes through it, so what leaves the owner is always the projection of the document as
/// it is now — a burst of writes becomes one push of the latest state.
/// </summary>
internal static class ReplicaSourceReader
{
    private static readonly MethodInfo LoadMethod =
        typeof(ReplicaSourceReader).GetMethod(nameof(LoadTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// The item for <paramref name="id"/>: the projected payload with its version stamp, or a tombstone when the document
    /// is gone — or is not a document of the contract's source collection. An id from a consumer never reads another
    /// collection through a contract (the fetch endpoint takes ids from the caller).
    /// </summary>
    public static async Task<ReplicaItem> ReadAsync(
        IDocumentStore store, IAsyncDocumentSession session, PublishedContract contract, string id, CancellationToken cancellationToken)
    {
        var entity = await (Task<object?>)LoadMethod.MakeGenericMethod(contract.Contract.SourceType)
            .Invoke(null, [session, id, cancellationToken])!;

        if (entity is null)
            return Tombstone(id);

        var metadata = session.Advanced.GetMetadataFor(entity);
        if (!metadata.TryGetValue(Constants.Documents.Metadata.Collection, out var collection)
            || !string.Equals(collection as string, contract.SourceCollection, StringComparison.OrdinalIgnoreCase))
            return Tombstone(id);

        var record = contract.Project(entity)
            ?? throw new InvalidOperationException($"Contract '{contract.Name}' v{contract.Version} projected {id} to null.");

        return new ReplicaItem
        {
            Id = id,
            ReplicationVersion = metadata.TryGetValue(ReplicationMetadata.Version, out var version) ? ReplicationMetadata.Read(version) : 0,
            ChangeVector = session.Advanced.GetChangeVectorFor(entity),
            Payload = ReplicaJson.Serialize(store, record),
        };
    }

    /// <summary>A deletion whose version is unknown here (0): the consumer supersedes what it holds.</summary>
    public static ReplicaItem Tombstone(string id, long version = 0) => new() { Id = id, Deleted = true, ReplicationVersion = version };

    private static async Task<object?> LoadTypedAsync<T>(IAsyncDocumentSession session, string id, CancellationToken cancellationToken)
        => await session.LoadAsync<T>(id, cancellationToken);
}
