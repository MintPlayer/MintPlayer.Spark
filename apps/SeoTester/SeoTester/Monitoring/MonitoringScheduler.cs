using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Settings;

namespace SeoTester.Monitoring;

/// <summary>
/// The monitoring sweep (<c>docs/seo_analyzer_PRD.md</c> §4.3 "Monitoring", F9, D11, D12): the sites whose
/// <see cref="Site.NextMonitoringRunAtUtc"/> has come, among those the monitoring gate admits, each get one
/// <see cref="MonitoredAuditMessage"/> per slot (<c>BroadcastOnceAsync</c>, CodeCoverage's
/// <c>ManifestScanScheduler</c> pattern) and their next slot. The message's recipient starts the audit through
/// <c>AuditStarter</c>, under the same gates, quota and cool-down as a member's click.
/// </summary>
public sealed class MonitoringScheduler(
    IDocumentStore store,
    IMessageBus bus,
    ISparkSettings settings,
    TimeProvider time,
    ILogger<MonitoringScheduler> logger)
{
    /// <summary>Dispatches up to <paramref name="batch"/> due slots; returns how many.</summary>
    public async Task<int> DispatchDueAsync(int batch, CancellationToken cancellationToken)
    {
        var gate = await settings.GetAsync(SeoSettings.Monitoring, cancellationToken);
        if (gate == SeoFeatureScope.Off)
            return 0;

        var now = time.GetUtcNow().UtcDateTime;
        using var session = store.OpenAsyncSession();
        var query = session.Query<Site>()
            // Not stale: a slot advanced by the previous sweep must not be dispatched again (its key would dedupe it, but
            // then the slot would never advance). Untracked: the only write is the patch below.
            .Customize(c => c.NoTracking().WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(s => s.NextMonitoringRunAtUtc <= now && s.Monitoring.In(MonitoringSchedule.Weekly, MonitoringSchedule.Monthly));
        if (gate == SeoFeatureScope.VerifiedOnly)
            query = query.Where(s => s.IsVerified);
        var due = await query.OrderBy(s => s.NextMonitoringRunAtUtc).Take(batch).ToListAsync(cancellationToken);

        foreach (var site in due)
        {
            var slot = site.NextMonitoringRunAtUtc!.Value;
            await bus.BroadcastOnceAsync(new MonitoredAuditMessage(site.Id!, slot), MonitoringPlan.SlotKey(site.Id!, slot), cancellationToken);
            // A patch of the one field: the sweep never writes back a profile edit or a verification it did not read.
            session.Advanced.Patch<Site, DateTime?>(site.Id!, s => s.NextMonitoringRunAtUtc, MonitoringPlan.NextAfter(site.Monitoring, slot, now));
        }
        if (due.Count > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dispatched {Count} scheduled audits", due.Count);
        }
        return due.Count;
    }
}
