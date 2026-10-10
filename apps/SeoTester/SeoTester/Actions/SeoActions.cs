using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using SeoTester.Entities;

namespace SeoTester.Actions;

/// <summary>
/// Sites. Who sees which is the organizations row policy (<c>IOrganizationOwned</c> + the role attributes on
/// <see cref="Site"/>); this class only withholds the full-site crawl from an unverified site.
/// </summary>
public partial class SiteActions : DefaultPersistentObjectActions<Site>
{
    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is Site { IsVerified: false })
            target.DisableActions(SeoActionNames.StartFullCrawl);
        return Task.CompletedTask;
    }
}

/// <summary>Runs; backs <c>Custom.SiteRuns</c>, a site's runs (newest first). Rows: the organizations row policy.</summary>
public partial class AuditRunActions : DefaultPersistentObjectActions<AuditRun>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// M11: a finished run cannot be cancelled, and only a completed one has a report to print, so the page offers
    /// neither button where it would only answer with an error.
    /// </summary>
    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is AuditRun run)
        {
            if (!LookupReferences.AuditRunStatus.InProgress.Contains(run.Status))
                target.DisableActions(SeoActionNames.CancelAudit);
            if (run.Status != LookupReferences.AuditRunStatus.Completed)
                target.DisableActions(SeoActionNames.ExportReport);
        }
        return Task.CompletedTask;
    }

    public IQueryable<AuditRun> SiteRuns(CustomQueryArgs args)
        => args.Parent?.Id is { Length: > 0 } siteId && string.Equals(args.ParentType, nameof(Site), StringComparison.OrdinalIgnoreCase)
            ? session.Query<AuditRun>().Where(r => r.SiteId == siteId)
            : Enumerable.Empty<AuditRun>().AsQueryable();
}

/// <summary>Pages; backs <c>Custom.RunPages</c>, a run's pages. Rows: the organizations row policy; expired pages: X1.</summary>
public partial class PageResultActions : DefaultPersistentObjectActions<PageResult>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<PageResult> RunPages(CustomQueryArgs args)
        => args.Parent?.Id is { Length: > 0 } runId && string.Equals(args.ParentType, nameof(AuditRun), StringComparison.OrdinalIgnoreCase)
            ? session.Query<PageResult>().Where(p => p.RunId == runId)
            : Enumerable.Empty<PageResult>().AsQueryable();
}

/// <summary>The check catalogue: every signed-in user reads all of it, administrators edit it (D32).</summary>
public partial class CheckDefinitionActions : DefaultPersistentObjectActions<CheckDefinition>, ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "The check catalogue is the same public reference text for every user (docs/seo_analyzer_PRD.md D32): titles, "
        + "explanations, how-to-fix snippets, weights. It holds nothing per user or organization, so no row is scoped. Changing it is granted to "
        + "Administrators only, in apps/SeoTester/SeoTester/App_Data/security.json.";
}
