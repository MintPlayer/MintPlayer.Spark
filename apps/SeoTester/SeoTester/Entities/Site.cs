using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Sharing;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using SeoTester.LookupReferences;

namespace SeoTester.Entities;

/// <summary>
/// A website an organization audits (<c>docs/seo_analyzer_PRD.md</c> F2, F3, §4.2). Row security is the organization's
/// (D18, D21, D23): Viewers read, Members edit the profile and start audits, Admins add, verify, delete and share.
/// </summary>
/// <remarks>
/// Only the profile (<see cref="PwaProfile"/>, <see cref="Language"/>, <see cref="Monitoring"/>, <see cref="AlertScoreDrop"/>) is the user's. The
/// origin and the organization are fixed at creation, and the verification and latest-run fields are the server's:
/// <c>SiteSaveInterceptor</c> keeps them on every persistent-object write, so only server code (a raw session)
/// changes them. Entities live in the host, not in <c>SeoTester.Library</c>: <see cref="IOrganizationOwned"/>,
/// <see cref="ISparkShareable"/> and <c>IExpiring</c> are in web assemblies, which an entity library refuses
/// (SPARKLIB001; PRD §12 "M8").
/// </remarks>
[RequiresOrganizationRole("New", OrganizationRole.Admin)]
[RequiresOrganizationRole("Edit", OrganizationRole.Member)]
[RequiresOrganizationRole("Delete", OrganizationRole.Admin)]
[RequiresOrganizationRole(SeoActionNames.StartAudit, OrganizationRole.Member)]
[RequiresOrganizationRole(SeoActionNames.StartFullCrawl, OrganizationRole.Member)]
[RequiresOrganizationRole(SeoActionNames.VerifySite, OrganizationRole.Admin)]
[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole("RevokeShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole(SeoActionNames.ShareBadge, OrganizationRole.Admin)]
public class Site : IOrganizationOwned, ISparkShareable
{
    public string? Id { get; set; }

    /// <summary>The owning organization; the creator's personal organization when the form leaves it empty (D21).</summary>
    [Reference(typeof(Organization))]
    public string? OrganizationId { get; set; }

    /// <summary><c>https://example.com</c>: scheme, lower-case ASCII host and a non-default port only. Fixed after creation.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The host of <see cref="Origin"/> (the TXT record is <c>_seotester.{Host}</c>, the cool-down key).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Audit the PWA checks too (opt-in profile, PRD §3.1).</summary>
    public bool PwaProfile { get; set; }

    /// <summary>The report's language, <c>en</c> or <c>nl</c>.</summary>
    public string? Language { get; set; }

    [LookupReference(typeof(MonitoringSchedule))]
    public string? Monitoring { get; set; } = MonitoringSchedule.Off;

    /// <summary>
    /// Alert when the score falls by at least this many points between two runs (F10); empty = the administrator's
    /// default (<c>SeoTester:Alerts:ScoreDrop</c>), 0 = no score-drop alerts. The other alerts (a new critical issue,
    /// the certificate or the domain about to expire) are always sent while monitoring is on.
    /// </summary>
    public int? AlertScoreDrop { get; set; }

    // ---- Scheduled monitoring (F9, D12). Server-owned: set when the schedule changes. -----------------------------

    /// <summary>When the monitoring sweep starts the next scheduled run; empty while monitoring is off.</summary>
    public DateTime? NextMonitoringRunAtUtc { get; set; }

    /// <summary>
    /// The member who set the schedule: scheduled runs are started on their behalf and count against their
    /// <c>verified-audits</c> quota (§6). Leaving the organization stops the runs until another member sets the schedule.
    /// </summary>
    public string? MonitoringBy { get; set; }

    // ---- Ownership verification (F3). Server-owned. ----------------------------------------------------------------

    /// <summary>The secret the organization publishes to prove control; random, made at creation.</summary>
    public string? VerificationToken { get; set; }

    /// <summary>Whether this organization proved control of the site; re-checked periodically.</summary>
    public bool IsVerified { get; set; }

    [LookupReference(typeof(VerificationMethod))]
    public string? VerificationMethod { get; set; }

    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>The last check, by the Verify action or the re-verification sweep.</summary>
    public DateTime? VerificationCheckedAtUtc { get; set; }

    /// <summary>Re-checks failed in a row; the verification is withdrawn at the configured count.</summary>
    public int VerificationFailures { get; set; }

    // ---- The latest run (written by the pipeline, M9). Server-owned. -----------------------------------------------

    [Reference(typeof(AuditRun))]
    public string? LatestRunId { get; set; }
    public int? LatestScore { get; set; }
    public string? LatestGrade { get; set; }
    public DateTime? LatestRunAtUtc { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime? CreatedAtUtc { get; set; }

    /// <summary>X6: the share link's hash, the framework's (never a model attribute).</summary>
    [IgnoreProperty]
    public string? ShareTokenHash { get; set; }
}
