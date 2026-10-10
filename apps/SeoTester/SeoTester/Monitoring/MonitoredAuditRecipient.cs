using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Settings;
using Raven.Client.Documents.Session;
using SeoTester.Audits;
using SeoTester.Entities;
using SeoTester.Messaging;
using SeoTester.Settings;

namespace SeoTester.Monitoring;

/// <summary>
/// <c>seo-monitor</c> (<c>docs/seo_analyzer_PRD.md</c> F9): starts a monitored site's scheduled audit on behalf of the
/// member who set the schedule (<see cref="Site.MonitoringBy"/>), through <see cref="AuditStarter"/>: it joins an audit
/// of the site already running, and it is refused like a click would be (gate, quota, cool-down). The site and the gate
/// are read again first: monitoring switched off, or the site's verification withdrawn, since the sweep stops the slot.
/// A refusal is logged by the starter and told to the member; a scheduler who left the organization (or became a
/// Viewer) stops the schedule's runs, and its Admins are told.
/// </summary>
public partial class MonitoredAuditRecipient : IRecipient<MonitoredAuditMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly AuditStarter starter;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly ISparkSettings settings;
    [Inject] private readonly SiteAlerter alerter;
    [Inject] private readonly ILogger<MonitoredAuditRecipient> logger;

    public async Task HandleAsync(MonitoredAuditMessage message, CancellationToken cancellationToken = default)
    {
        var site = await session.LoadAsync<Site>(message.SiteId, cancellationToken);
        if (site is null || !MonitoringPlan.IsScheduled(site.Monitoring))
            return;
        var gate = await settings.GetAsync(SeoSettings.Monitoring, cancellationToken);
        if (!gate.Admits(site.IsVerified))
            return;

        var userId = site.MonitoringBy;
        var role = userId is null || site.OrganizationId is null ? null : await organizations.GetRoleAsync(site.OrganizationId, userId, cancellationToken);
        if (userId is null || role is null || role < OrganizationRole.Member)
        {
            logger.LogInformation("Skipped the scheduled audit of {SiteId}: {UserId} is no longer a member who may start one", site.Id, userId);
            var admins = await alerter.MembersAsync(site.OrganizationId, OrganizationRole.Admin, cancellationToken);
            await alerter.NotifyAsync(site, admins, $"The scheduled audit of {site.Origin} was skipped",
                "The member who set the schedule can no longer start audits. Set the schedule again to take it over.", "seo-monitoring-skipped", cancellationToken);
            return;
        }

        try
        {
            await starter.StartAsync(site, userId, fullCrawl: false, scheduled: true, cancellationToken);
        }
        catch (Exception ex) when (ex is SparkThrottledException or SparkValidationException)
        {
            // Not retried: the slot is over, the next one comes on schedule. The starter logged the refusal.
            await alerter.NotifyAsync(site, [userId], $"The scheduled audit of {site.Origin} was skipped", ex.Message, "seo-monitoring-skipped", cancellationToken);
        }
    }
}
