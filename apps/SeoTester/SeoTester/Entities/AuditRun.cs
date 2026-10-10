using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Sharing;
using MintPlayer.Spark.Organizations;
using SeoTester.LookupReferences;
using SeoTester.Scoring;

namespace SeoTester.Entities;

/// <summary>
/// One audit of a site (<c>docs/seo_analyzer_PRD.md</c> F4, §4.2): kept for good (D8). Created by <c>StartAudit</c>
/// and written by the crawl pipeline (M9), never by a form, so the only rights are read and share. It carries the
/// scoring snapshot it was made with (D32), so its scores stay explainable after the catalogue changes.
/// </summary>
[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole("RevokeShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole(SeoActionNames.CancelAudit, OrganizationRole.Member)]
public class AuditRun : IOrganizationOwned, ISparkShareable
{
    public string? Id { get; set; }

    [Reference(typeof(Site))]
    public string? SiteId { get; set; }

    public string? OrganizationId { get; set; }

    public string Origin { get; set; } = string.Empty;

    [LookupReference(typeof(AuditRunStatus))]
    public string Status { get; set; } = AuditRunStatus.Queued;

    // ---- Progress (written at most every 2 s by the crawler, pushed live: §4.4). --------------------------------------

    /// <summary>The pipeline stage (probe, crawl, render, psi, aggregate).</summary>
    public string? Phase { get; set; }
    public int PagesDone { get; set; }
    public int PagesDiscovered { get; set; }
    public string? CurrentUrl { get; set; }

    /// <summary>Set by Cancel; the crawler stops at its next batch.</summary>
    public bool CancelRequested { get; set; }

    // ---- Who and when. -------------------------------------------------------------------------------------------

    /// <summary>The user who started the run.</summary>
    public string? StartedBy { get; set; }

    /// <summary>Everyone who started or joined it (D17, D18). Not shown: user ids.</summary>
    [IgnoreProperty]
    public List<string> Participants { get; set; } = [];

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // ---- What the run was allowed (§6, D11, D14), fixed at the start. ------------------------------------------------

    /// <summary>The organization had verified the site when the run started.</summary>
    public bool IsVerified { get; set; }

    /// <summary>The run got the verified (deep-crawl) limits: the DeepCrawl gate said so.</summary>
    public bool DeepCrawl { get; set; }

    /// <summary>A full-site crawl (D14): the weekly allowance, the safety ceiling instead of the page limit.</summary>
    public bool IsFullCrawl { get; set; }

    /// <summary>Probes for exposed files may run (the IntrusiveProbes gate).</summary>
    public bool IntrusiveProbes { get; set; }

    public int MaxPages { get; set; }
    public int WallTimeMinutes { get; set; }
    public int ConnectionsPerHost { get; set; }
    public int RenderedSample { get; set; }
    public int PsiSample { get; set; }

    // ---- Scores (§7). ---------------------------------------------------------------------------------------------

    /// <summary>The weights and severities the run is scored with (D32). Server-only: the version below names it.</summary>
    [IgnoreProperty]
    public ScoringSnapshot? ScoringSnapshot { get; set; }

    /// <summary>The hash of <see cref="ScoringSnapshot"/>; the trend chart marks a change.</summary>
    public string? ScoringVersion { get; set; }

    public int? Score { get; set; }
    public string? Grade { get; set; }
    public List<RunCategoryScore> CategoryScores { get; set; } = [];
    public List<RunCheckResult> CheckResults { get; set; } = [];

    public string? Error { get; set; }

    /// <summary>
    /// The pipeline stages that finished (<c>crawl</c>; <c>render</c> and <c>psi</c> join in M10). The last one to finish
    /// enqueues the aggregate (§4.3). Server-only.
    /// </summary>
    [IgnoreProperty]
    public List<string> StagesDone { get; set; } = [];

    /// <summary>The user who asked to cancel. Server-only.</summary>
    [IgnoreProperty]
    public string? CancelledBy { get; set; }

    /// <summary>X6: the share link's hash, the framework's.</summary>
    [IgnoreProperty]
    public string? ShareTokenHash { get; set; }
}

/// <summary>A category's score on a run.</summary>
[ValueObject]
public partial class RunCategoryScore
{
    [ValueKey]
    [LookupReference(typeof(CheckCategoryLookup))]
    public string Category { get; set; } = string.Empty;

    /// <summary>0–100; empty when nothing in the category was measured.</summary>
    public int? Score { get; set; }
    public string? Grade { get; set; }

    /// <summary>A failing critical check held the category at an F.</summary>
    public bool Capped { get; set; }
}

/// <summary>One check's result on a run: what the report and the fix list show once the page details expired (D8).</summary>
[ValueObject]
public partial class RunCheckResult
{
    [ValueKey]
    public string CheckId { get; set; } = string.Empty;

    /// <summary>Pass, Warn, Fail, NotApplicable or Error (SeoTester.Scoring.CheckStatus).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Pages that failed or warned.</summary>
    public int Affected { get; set; }

    /// <summary>At most five of them.</summary>
    public List<string> SampleUrls { get; set; } = [];

    /// <summary>New, Fixed or Unchanged since the previous run.</summary>
    public string? Delta { get; set; }
}
