using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Notifications.Models;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Monitoring;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using SeoTester.Tests.Crawler;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Monitoring;

/// <summary>
/// Scheduled monitoring and its alerts end to end (<c>docs/seo_analyzer_PRD.md</c> F9, F10, D11, D12, D20, M12): the
/// schedule on the site form, the 15-minute sweep starting runs through the pipeline, and the alerts a finished run
/// sends to the organization's members. On the pipeline's fast host.
/// </summary>
[Collection(SeoPipelineHostCollection.Name)]
public class MonitoringTests(SeoPipelineHostFixture host)
{
    private async Task AllowRunsAsync()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        await SetSettingAsync(host, SeoQuotas.VerifiedAudits.Limit.Key, "-1");
    }

    /// <summary>A two-page site whose home page is linked to /a.</summary>
    private string BuildSite(string title = "Home of the monitored shop")
    {
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        host.Web.Page(o + "/robots.txt", "User-agent: *\nAllow: /\n");
        host.Web.Html(o + "/", FakeSite.Page(title, "/a"));
        host.Web.Html(o + "/a", FakeSite.Page(title + ", page a", "/"));
        FakeSite.Registries(host.Web);
        return h;
    }

    private async Task<Site> MonitoredSiteAsync(string hostName, string schedule = MonitoringSchedule.Weekly, bool verified = true, DateTime? next = null, int? scoreDrop = null)
    {
        var site = await SeedSiteAsync(host, Acme, verified, hostName);
        using var session = host.Database.OpenAsyncSession();
        var stored = await session.LoadAsync<Site>(site.Id);
        stored.Monitoring = schedule;
        stored.MonitoringBy = Max;
        stored.NextMonitoringRunAtUtc = next ?? DateTime.UtcNow.AddDays(30);
        stored.AlertScoreDrop = scoreDrop;
        await session.SaveChangesAsync();
        return stored;
    }

    private async Task<List<AuditRun>> RunsOfAsync(string siteId)
    {
        using var session = host.Database.OpenAsyncSession();
        return await session.Query<AuditRun>().Customize(c => c.WaitForNonStaleResults()).Where(r => r.SiteId == siteId).ToListAsync();
    }

    /// <summary>A form save: the required origin is always posted, as the SPA does.</summary>
    private Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body, string Raw)> UpdateSiteAsync(Site site, string user, params (string Name, object? Value)[] values)
        => UpdateAsync(host, SiteTypeId, "Site", site.Id!, user, [("Origin", site.Origin), .. values]);

    private async Task<int> DispatchAsync()
    {
        using var scope = host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringScheduler>().DispatchDueAsync(200, CancellationToken.None);
    }

    [Fact]
    public async Task Switching_monitoring_on_needs_a_verified_site_and_schedules_the_writer()
    {
        var unverified = await SeedSiteAsync(host, Acme, hostName: FakeSite.NewHost());
        var refused = await UpdateSiteAsync(unverified, Max, ("Monitoring", MonitoringSchedule.Weekly));
        refused.Status.Should().Be(HttpStatusCode.BadRequest, refused.Raw);
        refused.Raw.Should().Contain("verified site");
        (await LoadDocumentAsync<Site>(host, unverified.Id!))!.Monitoring.Should().Be(MonitoringSchedule.Off);

        var verified = await SeedSiteAsync(host, Acme, verified: true, hostName: FakeSite.NewHost());
        var on = await UpdateSiteAsync(verified, Max, ("Monitoring", MonitoringSchedule.Weekly), ("AlertScoreDrop", 3));
        on.Status.Should().Be(HttpStatusCode.OK, on.Raw);
        var stored = (await LoadDocumentAsync<Site>(host, verified.Id!))!;
        stored.MonitoringBy.Should().Be(Max);
        stored.AlertScoreDrop.Should().Be(3);
        stored.NextMonitoringRunAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "never audited: the next sweep runs it");

        // An unchanged schedule keeps its member and slot whatever is posted; another member's change takes it over.
        var kept = await UpdateSiteAsync(verified, Ann, ("Monitoring", MonitoringSchedule.Weekly), ("MonitoringBy", Ann), ("NextMonitoringRunAtUtc", "2030-01-01T00:00:00Z"));
        kept.Status.Should().Be(HttpStatusCode.OK, kept.Raw);
        var after = (await LoadDocumentAsync<Site>(host, verified.Id!))!;
        after.MonitoringBy.Should().Be(Max);
        after.NextMonitoringRunAtUtc!.Value.Should().Be(stored.NextMonitoringRunAtUtc!.Value);

        (await UpdateSiteAsync(verified, Ann, ("Monitoring", MonitoringSchedule.Monthly))).Status.Should().Be(HttpStatusCode.OK);
        (await LoadDocumentAsync<Site>(host, verified.Id!))!.MonitoringBy.Should().Be(Ann);

        (await UpdateSiteAsync(verified, Max, ("Monitoring", MonitoringSchedule.Off))).Status.Should().Be(HttpStatusCode.OK);
        var off = (await LoadDocumentAsync<Site>(host, verified.Id!))!;
        off.MonitoringBy.Should().BeNull();
        off.NextMonitoringRunAtUtc.HasValue.Should().BeFalse();

        var negative = await UpdateSiteAsync(verified, Max, ("AlertScoreDrop", -1));
        negative.Status.Should().Be(HttpStatusCode.BadRequest, negative.Raw);
    }

    [Fact]
    public async Task The_sweep_starts_each_due_slot_of_a_verified_site_once_for_the_member_who_scheduled_it()
    {
        await AllowRunsAsync();
        var slot = DateTime.UtcNow.AddMinutes(-1);
        var due = await MonitoredSiteAsync(BuildSite(), next: slot);
        var unverified = await MonitoredSiteAsync(BuildSite(), verified: false, next: slot);
        var notDue = await MonitoredSiteAsync(BuildSite(), next: DateTime.UtcNow.AddDays(3));

        await DispatchAsync();
        await DispatchAsync();

        var run = (await FakeSite.WaitForAsync(host.Database, await SingleRunIdAsync(due.Id!), _ => true, TimeSpan.FromSeconds(30)));
        run.StartedBy.Should().Be(Max, "scheduled runs are started for the member who set the schedule");
        (await FakeSite.FinishedAsync(host.Database, run.Id!)).Status.Should().Be(AuditRunStatus.Completed);
        (await RunsOfAsync(due.Id!)).Should().ContainSingle("two sweeps, one slot, one run");
        (await RunsOfAsync(unverified.Id!)).Should().BeEmpty("monitoring is verified-only by default (D11)");
        (await RunsOfAsync(notDue.Id!)).Should().BeEmpty();
        (await LoadDocumentAsync<Site>(host, due.Id!))!.NextMonitoringRunAtUtc.Should().Be(slot.AddDays(7));

        using var session = host.Database.OpenAsyncSession();
        (await session.Query<AuditLogEntry>().Customize(c => c.WaitForNonStaleResults()).Where(e => e.RunId == run.Id && e.Kind == AuditLogKinds.AuditStarted).ToListAsync())
            .Should().ContainSingle().Which.Detail.Should().Be("scheduled");
    }

    private async Task<string> SingleRunIdAsync(string siteId)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if ((await RunsOfAsync(siteId)).FirstOrDefault() is { } run)
                return run.Id!;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No scheduled run of {siteId} started.");
    }

    [Fact]
    public async Task A_run_of_a_monitored_site_alerts_every_member_about_a_new_critical_issue_and_a_score_drop()
    {
        await AllowRunsAsync();
        var hostName = BuildSite();
        var monitored = await MonitoredSiteAsync(hostName, scoreDrop: 1);
        var otherHost = BuildSite();
        var unmonitored = await SeedSiteAsync(host, Acme, verified: true, hostName: otherHost);

        var first = await FinishedRunAsync(monitored);
        var firstOther = await FinishedRunAsync(unmonitored);
        first.Status.Should().Be(AuditRunStatus.Completed, first.Error);
        firstOther.Status.Should().Be(AuditRunStatus.Completed, firstOther.Error);

        // Both sites lose every page title: seo.title-present (Critical) fails where it passed.
        foreach (var h in new[] { hostName, otherHost })
        {
            host.Web.Html($"https://{h}/", FakeSite.Page("Home of the monitored shop", "/a").Replace("<title>Home of the monitored shop</title>", ""));
            host.Web.Html($"https://{h}/a", FakeSite.Page("Home of the monitored shop, page a", "/").Replace("<title>Home of the monitored shop, page a</title>", ""));
        }
        var second = await FinishedRunAsync(monitored);
        var secondOther = await FinishedRunAsync(unmonitored);
        second.CheckResults.Single(r => r.CheckId == "seo.title-present").ShouldSatisfy(r => r.Status == "Fail" && r.Delta == "New");
        second.Score!.Value.Should().BeLessThan(first.Score!.Value);

        var alerts = await AlertsAsync(second.Id!, expected: 6);
        foreach (var member in new[] { Ann, Max, Vic })
        {
            alerts.Should().Contain(n => n.UserId == member && n.Category == "seo-alert-critical" && n.Title.Contains(second.Origin), $"{member} is a member of Acme");
            alerts.Should().Contain(n => n.UserId == member && n.Category == "seo-alert-score-drop");
        }
        alerts.Should().NotContain(n => n.UserId == Bob, "Bob is not in Acme");
        (await AlertsAsync(secondOther.Id!, expected: 0)).Should().BeEmpty("a site without monitoring gets the completion notice only");
    }

    private async Task<AuditRun> FinishedRunAsync(Site site)
    {
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        return await FakeSite.FinishedAsync(host.Database, body.GetProperty("result").GetProperty("runId").GetString()!);
    }

    /// <summary>
    /// The alerts about a run. They are sent right after the run's completion is committed, so this reads until
    /// <paramref name="expected"/> arrived (a failure bound), or for 3 s when none should.
    /// </summary>
    private async Task<List<Notification>> AlertsAsync(string runId, int expected)
    {
        var url = "/po/audit-run/" + Uri.EscapeDataString(runId);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bound = expected == 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(20);
        while (true)
        {
            using var session = host.Database.OpenAsyncSession();
            var alerts = await session.Query<Notification>().Customize(c => c.WaitForNonStaleResults())
                .Where(n => n.Url == url && n.Category!.StartsWith("seo-alert"))
                .ToListAsync();
            if ((expected > 0 && alerts.Count >= expected) || watch.Elapsed > bound)
                return alerts;
            await Task.Delay(200);
        }
    }
}
