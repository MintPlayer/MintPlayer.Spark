using CodeCoverage.Entities;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// The one way every lost-access path asks for a mirror to go (PRD §5.2): disconnect and uninstall
/// (<c>ForgeEventsRecipient</c>), the nightly reconcile, and <c>DeleteData</c>.
/// </summary>
/// <remarks>
/// Queued rather than done inline: the caller is in the middle of its own unit of work, and a
/// sidecar that is briefly down must cost a retry of the deletion, not the caller's event. Only
/// repositories with a <see cref="RepositoryGraphState"/> are queued, because a mirror never exists
/// without one (see the invariant there) — so the common case costs one load by id and queues
/// nothing.
/// </remarks>
public static class CommitGraphDeletion
{
    public static async Task RequestAsync(
        IAsyncDocumentSession session, IMessageBus messageBus, IReadOnlyCollection<string> repositoryIds, string reason,
        CancellationToken cancellationToken = default)
    {
        var ids = repositoryIds.Where(id => id.StartsWith("Repositories/", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (ids.Count == 0) return;

        var states = await session.LoadAsync<RepositoryGraphState>(ids.Select(RepositoryGraphState.ForRepository), cancellationToken);
        foreach (var id in ids)
        {
            if (states.GetValueOrDefault(RepositoryGraphState.ForRepository(id)) is null) continue;
            await messageBus.BroadcastAsync(new DeleteCommitGraphMirrorMessage { RepositoryId = id, Reason = reason }, cancellationToken);
        }
    }
}
