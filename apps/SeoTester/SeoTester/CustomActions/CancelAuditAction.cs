using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;
using SeoTester.Audits;
using SeoTester.Entities;
using SeoTester.LookupReferences;

namespace SeoTester.CustomActions;

/// <summary>
/// <c>CancelAudit</c> on a running audit (F4; Members of the owning organization, the run's row rule): sets
/// <c>CancelRequested</c> with a patch, so it never overwrites the crawler's progress. The crawler stops at its next
/// batch (or the probe before it starts) and ends the run as Cancelled. A run that already finished is left alone.
/// </summary>
public partial class CancelAuditAction : SparkCustomAction
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IManager manager;
    [Inject] private readonly AuditLog auditLog;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var run = args.Parent?.Id is { Length: > 0 } id ? await session.LoadAsync<AuditRun>(id, cancellationToken) : null;
        if (run is null)
            throw new SparkValidationException("Open the audit first.");
        if (AuditRunStatus.Finished.Contains(run.Status))
            throw new SparkValidationException(manager.GetTranslatedMessage("seo.auditAlreadyFinished", run.Origin));

        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        session.Advanced.Evict(run);
        session.Advanced.Patch<AuditRun, bool>(run.Id!, r => r.CancelRequested, true);
        session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.CancelledBy!, userId!);
        if (run.SiteId is not null && await session.LoadAsync<Site>(run.SiteId, cancellationToken) is { } site)
            await session.StoreAsync(await auditLog.EntryAsync(AuditLogKinds.AuditCancelled, userId, site, run.Id, null, cancellationToken), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        args.SetResult(new { runId = run.Id, cancelRequested = true });
        manager.Client.Notify(manager.GetTranslatedMessage("seo.auditCancelling", run.Origin), NotificationKind.Info);
    }
}
