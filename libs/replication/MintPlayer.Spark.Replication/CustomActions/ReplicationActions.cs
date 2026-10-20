using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.CustomActions;

/// <summary>
/// The custom actions replication ships (warehouse §4.2.2, §4.2.5). Custom-action names are first-wins across
/// assemblies, so each names its library. Rights are in the library's <c>security.json</c>: the decisions are granted
/// to the <c>replication:approvers</c> slot the application binds (typically its administrators); retry and dismiss are
/// the initiator's (their own rows only, by the row rule).
/// </summary>
public static class ReplicationActionNames
{
    public const string Approve = "ApproveReplication";
    public const string Reject = "RejectReplication";
    public const string Retry = "RetrySyncAction";
    public const string Dismiss = "DismissSyncAction";
}

/// <summary>Approves the selected pending replication requests, or the one on the page: the delivery is redeployed.</summary>
internal sealed partial class ApproveReplicationAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ReplicationDecisionOutcome>();
        foreach (var id in ReplicationActionTargets.Ids(args))
            outcomes.Add(await subscriptions.ApproveAsync(id, currentUser.Id, cancellationToken));
        ReplicationActionTargets.Report(manager, outcomes, "replication.approvedNotice");
    }
}

/// <summary>Rejects the selected pending replication requests: the consumer's subscription is suspended.</summary>
internal sealed partial class RejectReplicationAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ReplicationDecisionOutcome>();
        foreach (var id in ReplicationActionTargets.Ids(args))
            outcomes.Add(await subscriptions.RejectAsync(id, currentUser.Id, reason: null, cancellationToken));
        ReplicationActionTargets.Report(manager, outcomes, "replication.rejectedNotice");
    }
}

/// <summary>Re-drives a write-back that gave up (F14): it is queued again with a fresh retry budget.</summary>
internal sealed partial class RetrySyncActionAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var settings = options.Value.WriteBack;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var done = 0;
        var blocked = 0;
        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        foreach (var id in ReplicationActionTargets.Ids(args))
        {
            if (await session.LoadAsync<SparkSyncAction>(id, cancellationToken) is not { Status: SyncActionStatuses.GaveUp } action)
                continue;

            // One pending change per row (Q-W28): a newer change of the row waits already.
            var markerId = SyncPendingMarker.For(action.DocumentId);
            if (await session.LoadAsync<SyncPendingMarker>(markerId, cancellationToken) is { SyncActionId: not null })
            {
                blocked++;
                continue;
            }

            action.Status = SyncActionStatuses.Pending;
            action.Redrives++;
            action.Reason = null;
            action.CompletedAtUtc = null;
            action.Dismissed = false;
            action.CreatedAtUtc = now;
            action.GiveUpAtUtc = now + settings.GiveUpAfter;
            await session.StoreAsync(new SyncPendingMarker
            {
                Id = markerId,
                DocumentId = action.DocumentId,
                OwnerModule = action.OwnerModule,
                SyncActionId = action.Id,
                InitiatorId = action.InitiatorId,
                CreatedAtUtc = now,
            }, markerId, cancellationToken);
            await outbox.EnqueueAsync(session, new SyncActionDispatchMessage { SyncActionId = action.Id! }, new BroadcastOptions
            {
                MaxAttempts = RetryLadder.Budget(settings.ResolvedRetryLadder, settings.GiveUpAfter),
                ExpiresAtUtc = now + settings.GiveUpAfter + TimeSpan.FromHours(1),
            }, cancellationToken);
            done++;
        }
        await session.SaveChangesAsync(cancellationToken);

        if (done > 0)
            manager.Client.Notify(manager.GetTranslatedMessage("replication.retriedNotice", done), NotificationKind.Success);
        if (blocked > 0)
            manager.Client.Notify(manager.GetTranslatedMessage("replication.retryBlocked", blocked), NotificationKind.Warning);
        manager.Client.RefreshQuery("my-pending-changes");
    }
}

/// <summary>Removes ended write-backs from the initiator's "My pending changes".</summary>
internal sealed partial class DismissSyncActionAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IDocumentStore documentStore;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        foreach (var id in ReplicationActionTargets.Ids(args))
        {
            if (await session.LoadAsync<SparkSyncAction>(id, cancellationToken) is { } action && action.Status != SyncActionStatuses.Pending)
                action.Dismissed = true;
        }
        await session.SaveChangesAsync(cancellationToken);
        manager.Client.RefreshQuery("my-pending-changes");
    }
}

internal static class ReplicationActionTargets
{
    /// <summary>The page's object, else the selected rows (server-loaded and row-checked by the framework).</summary>
    public static IReadOnlyList<string> Ids(CustomActionArgs args)
        => args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(item => item.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();

    public static void Report(IManager manager, IReadOnlyList<ReplicationDecisionOutcome> outcomes, string doneKey)
    {
        var done = outcomes.Count(o => o == ReplicationDecisionOutcome.Done);
        if (done > 0)
            manager.Client.Notify(manager.GetTranslatedMessage(doneKey, done), NotificationKind.Success);
        foreach (var problem in outcomes.Where(o => o != ReplicationDecisionOutcome.Done).GroupBy(o => o))
        {
            var key = problem.Key switch
            {
                ReplicationDecisionOutcome.NotPending => "replication.notPending",
                ReplicationDecisionOutcome.NotGranted => "replication.notGranted",
                _ => "replication.gone",
            };
            manager.Client.Notify(manager.GetTranslatedMessage(key, problem.Count()), NotificationKind.Warning);
        }
        manager.Client.RefreshQuery("replication-requests");
    }
}
