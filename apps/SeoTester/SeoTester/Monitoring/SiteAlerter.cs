using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using SeoTester.Catalogue;
using SeoTester.Crawler;
using SeoTester.Entities;
using SeoTester.Scoring;
using SeoTester.Settings;

namespace SeoTester.Monitoring;

/// <summary>
/// The alerts of a monitored site (<c>docs/seo_analyzer_PRD.md</c> F10, D20, D44): after a run completes, the rules of
/// <see cref="AlertRules"/> decide; each alert is one <see cref="ISparkNotifier"/> call to every member of the site's
/// organization, whose preferences for that organization choose in-app, mail and Web Push. Alerts follow the
/// monitoring gate (D11: "scheduled monitoring + alerts", verified-only by default) and the site's schedule: a site
/// without monitoring gets the run's ordinary completion notice only.
/// </summary>
public sealed class SiteAlerter(
    IDocumentStore store,
    ISparkSettings settings,
    ISparkNotifier notifier,
    ILogger<SiteAlerter> logger)
{
    /// <summary>The notification category of an alert (<c>seo-alert-score-drop</c>, …); also the Web Push topic.</summary>
    public static string CategoryOf(SiteAlertKind kind) => kind switch
    {
        SiteAlertKind.ScoreDrop => "seo-alert-score-drop",
        SiteAlertKind.NewCriticalIssue => "seo-alert-critical",
        SiteAlertKind.CertificateExpiring => "seo-alert-certificate",
        SiteAlertKind.DomainExpiring => "seo-alert-domain",
        _ => "seo-alert",
    };

    /// <summary>
    /// Alerts the members of <paramref name="run"/>'s site about what it found, compared with <paramref name="previous"/>
    /// (the site's completed run before it, or null). Never throws: the run is over either way.
    /// </summary>
    public async Task<IReadOnlyList<SiteAlert>> AlertAsync(AuditRun run, AuditRun? previous, ScoringSnapshot snapshot, int? score, IReadOnlyList<RunCheckResult> results, CancellationToken cancellationToken)
    {
        try
        {
            if (run.SiteId is not { } siteId)
                return [];
            using var session = store.OpenAsyncSession();
            var site = await session.LoadAsync<Site>(siteId, cancellationToken);
            if (site is null || !MonitoringPlan.IsScheduled(site.Monitoring))
                return [];
            var gate = await settings.GetAsync(SeoSettings.Monitoring, cancellationToken);
            if (!gate.Admits(site.IsVerified))
                return [];

            var threshold = site.AlertScoreDrop ?? await settings.GetAsync(SeoSettings.AlertScoreDrop, cancellationToken);
            var severities = snapshot.Checks.GroupBy(c => c.CheckId).ToDictionary(g => g.Key, g => g.First().Severity, StringComparer.Ordinal);
            var checks = results
                .Where(r => severities.ContainsKey(r.CheckId) && Enum.TryParse<CheckStatus>(r.Status, out _))
                .Select(r => new AlertCheck(r.CheckId, Enum.Parse<CheckStatus>(r.Status), r.Delta, severities[r.CheckId]));
            var sameScoring = previous is not null && previous.ScoringVersion == snapshot.Version;
            var alerts = AlertRules.Evaluate(previous?.Score, score, threshold, sameScoring, checks);
            if (alerts.Count == 0)
                return alerts;

            var members = await MembersAsync(site.OrganizationId, OrganizationRole.Viewer, cancellationToken);
            foreach (var alert in alerts)
            {
                var (title, body) = Text(alert, site.Origin);
                await notifier.NotifyAsync(new SparkNotification
                {
                    UserIds = members,
                    Title = title,
                    Body = body,
                    Url = RunPipeline.RunUrl(run.Id!),
                    OrganizationId = site.OrganizationId,
                    Category = CategoryOf(alert.Kind),
                    Urgent = alert.Kind != SiteAlertKind.ScoreDrop,
                }, cancellationToken);
            }
            return alerts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alerting the members of the site of {RunId} failed", run.Id);
            return [];
        }
    }

    /// <summary>Tells <paramref name="userIds"/> about a monitored site (a skipped scheduled run). Never throws.</summary>
    public async Task NotifyAsync(Site site, IReadOnlyCollection<string> userIds, string title, string? body, string category, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return;
        try
        {
            await notifier.NotifyAsync(new SparkNotification
            {
                UserIds = userIds,
                Title = title.Length > 200 ? title[..200] : title,
                Body = body,
                Url = "/po/site/" + Uri.EscapeDataString(site.Id!),
                OrganizationId = site.OrganizationId,
                Category = category,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notifying about the monitoring of {SiteId} failed", site.Id);
        }
    }

    /// <summary>The members of <paramref name="organizationId"/> with at least <paramref name="role"/>.</summary>
    public async Task<IReadOnlyList<string>> MembersAsync(string? organizationId, OrganizationRole role, CancellationToken cancellationToken)
    {
        if (organizationId is null)
            return [];
        using var session = store.OpenAsyncSession();
        var memberships = await session.Query<OrganizationMembership>()
            .Where(m => m.OrganizationId == organizationId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        return memberships.Where(m => m.Role >= role && m.UserId is not null).Select(m => m.UserId!).Distinct().ToList();
    }

    /// <summary>The alert's headline and text (English, like the pipeline's other notices; the run page explains in the reader's language).</summary>
    public static (string Title, string Body) Text(SiteAlert alert, string origin) => alert.Kind switch
    {
        SiteAlertKind.ScoreDrop => ($"The SEO score of {origin} dropped from {alert.PreviousScore} to {alert.Score}",
            "The latest scheduled check scored lower than the one before. The report lists what is new."),
        SiteAlertKind.NewCriticalIssue => ($"New critical issue on {origin}",
            $"Failing since the last run: {string.Join(", ", alert.CheckIds ?? [])}. The report says how to fix it."),
        SiteAlertKind.CertificateExpiring => ($"The certificate of {origin} expires within 14 days",
            "Renew it before visitors get a security warning."),
        SiteAlertKind.DomainExpiring => ($"The domain of {origin} expires within 30 days",
            "Renew the registration or switch on auto-renew; an expired domain takes the site and its mail offline."),
        _ => ($"Alert for {origin}", null!),
    };
}
