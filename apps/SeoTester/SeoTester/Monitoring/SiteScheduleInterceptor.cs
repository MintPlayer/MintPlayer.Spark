using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Settings;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;

namespace SeoTester.Monitoring;

/// <summary>
/// A site's monitoring schedule on every persistent-object write (<c>docs/seo_analyzer_PRD.md</c> F9, F10, D11, D12),
/// beside <c>SiteSaveInterceptor</c> (which keeps the other server-owned fields):
/// <list type="bullet">
/// <item>switching monitoring on (or from weekly to monthly) needs the monitoring gate to admit the site (verified-only
/// by default) and makes the writer the member the scheduled runs are started for (<see cref="Site.MonitoringBy"/>),
/// with a first slot one period after the latest run (<see cref="MonitoringPlan.FirstSlot"/>);</item>
/// <item>an unchanged schedule keeps the stored slot and member, whatever the client posted;</item>
/// <item>switching it off clears both;</item>
/// <item>the score-drop threshold is 0–100 points.</item>
/// </list>
/// </summary>
public sealed partial class SiteScheduleInterceptor : IBeforeSave<Site>
{
    [Inject] private readonly ISparkSettings settings;
    [Inject] private readonly IManager manager;
    [Inject] private readonly TimeProvider time;

    public async ValueTask OnBeforeSaveAsync(Site site, SaveContext context)
    {
        if (site.AlertScoreDrop is < 0 or > 100)
            throw new SparkValidationException(manager.GetTranslatedMessage("seo.alertScoreDropRange"));

        var stored = context.Before as Site;
        site.Monitoring ??= MonitoringSchedule.Off;
        if (!MonitoringPlan.IsScheduled(site.Monitoring))
        {
            site.Monitoring = MonitoringSchedule.Off;
            site.MonitoringBy = null;
            site.NextMonitoringRunAtUtc = null;
            return;
        }

        if (stored is not null && stored.Monitoring == site.Monitoring && stored.MonitoringBy is not null)
        {
            site.MonitoringBy = stored.MonitoringBy;
            site.NextMonitoringRunAtUtc = stored.NextMonitoringRunAtUtc;
            return;
        }

        // The stored state, never the posted one: a new site is unverified, and a Member cannot post IsVerified.
        var verified = stored?.IsVerified ?? false;
        var gate = await settings.GetAsync(SeoSettings.Monitoring);
        if (!gate.Admits(verified))
            throw new SparkValidationException(manager.GetTranslatedMessage(gate == SeoFeatureScope.Off ? "seo.monitoringOff" : "seo.monitoringNeedsVerified"));

        site.MonitoringBy = context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new SparkValidationException(manager.GetTranslatedMessage("seo.monitoringNeedsVerified"));
        site.NextMonitoringRunAtUtc = MonitoringPlan.FirstSlot(site.Monitoring, stored?.LatestRunAtUtc, time.GetUtcNow().UtcDateTime);
    }
}
