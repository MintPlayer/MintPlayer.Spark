using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Owner (warehouse §4.2.5): applies one write-back as the requesting module, through <see cref="ISyncActionHandler"/>'s
/// per-property compare-and-set inside the normal save path (rights incl. attribute rights, validation, interceptors).
/// Idempotent by op id (F15): an applied edit records its outcome in its own commit, a refusal right after, and a
/// replay answers the recorded outcome instead of a false conflict.
/// </summary>
internal interface ISyncApplyService
{
    /// <summary>The caller has been authenticated as <paramref name="module"/>, and its identity is on the request.</summary>
    Task<(int StatusCode, SyncApplyResult Result)> ApplyAsync(string module, SyncApplyRequest request, CancellationToken cancellationToken);
}

internal sealed partial class SyncApplyService : ISyncApplyService
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IReplicaSourceTypes sourceTypes;
    [Inject] private readonly ISyncActionHandler handler;
    [Inject] private readonly ILogger<SyncApplyService> logger;

    public async Task<(int StatusCode, SyncApplyResult Result)> ApplyAsync(string module, SyncApplyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.OpId) || string.IsNullOrEmpty(request.DocumentId)
            || (request.ActionType == SyncActionType.Update && request.Changes.Count == 0)
            || request.Changes.Any(c => string.IsNullOrEmpty(c.Property)))
            return Answer(new SyncApplyResult { Outcome = SyncApplyOutcome.Refused, Reason = "Invalid write-back request" }, badRequest: true);

        if (sourceTypes.Resolve(request.SourceCollection) is not { } entityType)
            return (404, new SyncApplyResult { Outcome = SyncApplyOutcome.Refused, Reason = $"Unknown collection '{request.SourceCollection}'" });

        // F15: a replay answers what the first delivery answered.
        using (var check = documentStore.OpenAsyncSession())
        {
            var inbox = await check.LoadAsync<SyncInboxEntry>(SyncInboxEntry.For(module, request.DocumentId), cancellationToken);
            if (inbox?.Recent.FirstOrDefault(o => o.OpId == request.OpId) is { } seen)
                return Answer(new SyncApplyResult { Outcome = seen.Outcome, Reason = seen.Reason, Attributes = seen.Attributes, Refusal = seen.Refusal });
        }

        var baseValues = request.Changes.ToDictionary(c => c.Property, c => c.Base, StringComparer.OrdinalIgnoreCase);
        SyncEditOutcome outcome;
        var recorded = false;
        if (request.ActionType == SyncActionType.Delete)
        {
            outcome = await handler.ApplyDeleteAsync(new SyncDelete
            {
                EntityType = entityType,
                DocumentId = request.DocumentId,
                BaseValues = baseValues,
                InitiatorId = request.InitiatorId,
            }, cancellationToken);
        }
        else
        {
            outcome = await handler.ApplyEditAsync(new SyncEdit
            {
                EntityType = entityType,
                DocumentId = request.DocumentId,
                Values = request.Changes.ToDictionary(c => c.Property, c => c.Value, StringComparer.OrdinalIgnoreCase),
                BaseValues = baseValues,
                InitiatorId = request.InitiatorId,
                // The op id commits with the edit or not at all.
                Alongside = async session =>
                {
                    await RecordAsync((IAsyncDocumentSession)session, module, request, new SyncApplyResult { Outcome = SyncApplyOutcome.Applied }, cancellationToken);
                    recorded = true;
                },
            }, cancellationToken);
        }

        var result = new SyncApplyResult
        {
            Outcome = outcome.Kind switch
            {
                SyncEditOutcomeKind.Applied => SyncApplyOutcome.Applied,
                SyncEditOutcomeKind.Conflict => SyncApplyOutcome.Conflict,
                _ => SyncApplyOutcome.Refused,
            },
            Reason = outcome.Reason,
            Attributes = [.. outcome.Attributes],
            Refusal = outcome.Refusal switch
            {
                SyncEditRefusal.Forbidden => SyncRefusalKind.Forbidden,
                SyncEditRefusal.Invalid => SyncRefusalKind.Invalid,
                _ => SyncRefusalKind.Other,
            },
        };

        // Nothing was written for a refusal or a conflict (and a delete has no Alongside): record it now, so a replay
        // answers the same. A crash in between makes a replay re-run the compare, which answers the same anyway.
        if (outcome.Kind != SyncEditOutcomeKind.Applied || !recorded)
        {
            using var session = documentStore.OpenAsyncSession();
            await RecordAsync(session, module, request, result, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Write-back {OpId} from '{Module}' on {Collection}/{DocumentId}: {Outcome} {Reason}",
            request.OpId, module, request.SourceCollection, request.DocumentId, result.Outcome, result.Reason);
        return Answer(result);
    }

    /// <summary>200 applied, 409 conflict, 403 forbidden, 422 invalid, 400 otherwise. Never 207 (F12).</summary>
    private static (int, SyncApplyResult) Answer(SyncApplyResult result, bool badRequest = false) => (result.Outcome switch
    {
        _ when badRequest => 400,
        SyncApplyOutcome.Applied => 200,
        SyncApplyOutcome.Conflict => 409,
        _ => result.Refusal switch
        {
            SyncRefusalKind.Forbidden => 403,
            SyncRefusalKind.Invalid => 422,
            _ => 400,
        },
    }, result);

    private static async Task RecordAsync(IAsyncDocumentSession session, string module, SyncApplyRequest request, SyncApplyResult result, CancellationToken cancellationToken)
    {
        var id = SyncInboxEntry.For(module, request.DocumentId);
        var inbox = await session.LoadAsync<SyncInboxEntry>(id, cancellationToken)
            ?? new SyncInboxEntry { Id = id, Module = module, DocumentId = request.DocumentId };
        inbox.Recent.RemoveAll(o => o.OpId == request.OpId);
        inbox.Recent.Add(new SyncInboxOp
        {
            OpId = request.OpId,
            Outcome = result.Outcome,
            Reason = result.Reason,
            Attributes = [.. result.Attributes],
            Refusal = result.Refusal,
        });
        if (inbox.Recent.Count > SyncInboxEntry.RememberedOps)
            inbox.Recent.RemoveRange(0, inbox.Recent.Count - SyncInboxEntry.RememberedOps);
        await session.StoreAsync(inbox, id, cancellationToken);
    }
}
