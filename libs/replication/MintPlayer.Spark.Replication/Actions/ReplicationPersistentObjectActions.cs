using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Replication.CustomActions;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Actions;

/// <summary>
/// The owner's "Replication requests" (warehouse §4.2.2): pending requests first, then the newest. Reached through the
/// library's program unit, which a user without the query right does not see; every request is also readable from its
/// detail page with the diff. Approve and Reject are offered only on a pending request.
/// </summary>
public partial class SparkReplicationRequestActions : DefaultPersistentObjectActions<SparkReplicationRequest>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>Every request, pending first.</summary>
    public IQueryable<SparkReplicationRequest> ReplicationRequests()
        => session.Query<SparkReplicationRequest>()
            .OrderByDescending(r => r.IsPending)
            .ThenByDescending(r => r.RequestedAtUtc);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is SparkReplicationRequest { Status: not ReplicationRequestStatuses.Pending })
            target.DisableActions(ReplicationActionNames.Approve, ReplicationActionNames.Reject);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A consumer's queued write-backs (warehouse §4.2.5): each row is its initiator's alone (the row rule, on every read
/// path), so "My pending changes" lists the caller's waiting and failed changes until they dismiss them. Retry is
/// offered on a change that gave up, Dismiss on one that ended.
/// </summary>
public partial class SparkSyncActionActions : DefaultPersistentObjectActions<SparkSyncAction>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public override Task<Expression<Func<SparkSyncAction, bool>>?> GetRowFilterAsync(string action)
    {
        var me = currentUser is { IsAuthenticated: true, Id: { Length: > 0 } id } ? id : "\0";
        return Task.FromResult<Expression<Func<SparkSyncAction, bool>>?>(a => a.InitiatorId == me);
    }

    /// <summary>The caller's changes that wait at an owner, or ended without being applied and were not dismissed.</summary>
    public IQueryable<SparkSyncAction> MyPendingChanges()
    {
        var me = currentUser is { IsAuthenticated: true, Id: { Length: > 0 } id } ? id : "\0";
        return session.Query<SparkSyncAction>()
            .Where(a => a.InitiatorId == me && !a.Dismissed && a.Status != SyncActionStatuses.Completed)
            .OrderByDescending(a => a.CreatedAtUtc);
    }

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is SparkSyncAction action)
        {
            if (action.Status != SyncActionStatuses.GaveUp)
                target.DisableActions(ReplicationActionNames.Retry);
            if (action.Status == SyncActionStatuses.Pending)
                target.DisableActions(ReplicationActionNames.Dismiss);
        }
        return Task.CompletedTask;
    }
}
