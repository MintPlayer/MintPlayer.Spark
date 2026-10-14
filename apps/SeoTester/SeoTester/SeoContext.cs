using MintPlayer.Spark;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents.Linq;
using SeoTester.Entities;

namespace SeoTester;

/// <summary>
/// SeoTester's collections (<c>docs/seo_analyzer_PRD.md</c> §4.2). Organizations, settings and notifications are their
/// packages' own documents. What each caller sees of sites, runs and pages is the organizations row policy; the
/// catalogue is public to signed-in users and the audit log is the administrators'.
/// </summary>
public partial class SeoContext : SparkContext
{
    public IRavenQueryable<Site> Sites => Session.Query<Site>();
    public IRavenQueryable<AuditRun> AuditRuns => Session.Query<AuditRun>();
    public IRavenQueryable<CheckDefinition> CheckDefinitions => Session.Query<CheckDefinition>();
    public IRavenQueryable<AuditLogEntry> AuditLogEntries => Session.Query<AuditLogEntry>();

    /// <summary>
    /// The package's organizations, as a plain entity root (<c>docs/default_index_PRD.md</c>, D6): it mints the query
    /// <c>Site.OrganizationId</c>'s query-less <c>[Reference(typeof(Organization))]</c> resolves to, so the site form's
    /// picker lists the caller's organizations. Without it the reference had no query and the picker stayed empty
    /// (SPARK056). Its rows are the package's row policy, the same the package's own <c>Custom.Organizations</c> lists
    /// (SP3). The minted alias steps aside from that query's (<c>organizations-2</c>, D11), and the audit-named
    /// <c>CreatedBy</c> is created hidden by the default <c>spark.ModelAttributes</c> convention (D9).
    /// </summary>
    public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();
}

/// <summary>SeoTester's custom actions (<c>App_Data/actions.json</c>); their class is the name plus <c>Action</c>.</summary>
public static class SeoActionNames
{
    /// <summary>On a site: start an audit, or join the one running for its origin in an organization of the caller's (F4).</summary>
    public const string StartAudit = nameof(StartAudit);

    /// <summary>On a verified site: a full-site crawl, the weekly allowance (D14).</summary>
    public const string StartFullCrawl = nameof(StartFullCrawl);

    /// <summary>On a site: look for the verification token (DNS, meta tag, well-known file) (F3).</summary>
    public const string VerifySite = nameof(VerifySite);

    /// <summary>On a catalogue entry: restore the shipped version (D32).</summary>
    public const string ResetCheckDefinition = nameof(ResetCheckDefinition);

    /// <summary>On a running audit: stop it at the crawler's next batch (F4).</summary>
    public const string CancelAudit = nameof(CancelAudit);

    /// <summary>On a completed audit: the report as PDF in the caller's language (F14, M10).</summary>
    public const string ExportReport = nameof(ExportReport);

    /// <summary>A share link plus the Markdown of its SVG badge (F11, D31; Sites/SiteBadge.cs).</summary>
    public const string ShareBadge = nameof(ShareBadge);
}
