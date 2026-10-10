using System.Net;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.Messaging;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Audits;

/// <summary>
/// StartAudit / StartFullCrawl (<c>docs/seo_analyzer_PRD.md</c> §4.3, §6, D11, D14, D17, D18, D37): the run, its
/// limits and scoring snapshot, the first pipeline message, the cool-down across users, joining, quotas and the gates.
/// The quotas are counted per user across the shared host, so each test that spends one uses a quota of its own by
/// raising the limit or by using a user the others do not.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class StartAuditTests(SeoTesterHostFixture host)
{
    [Fact]
    public async Task Starting_an_audit_creates_the_run_with_its_limits_and_snapshot_and_enqueues_the_probe_in_one_commit()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var site = await SeedSiteAsync(host, Acme);

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);

        status.Should().Be(HttpStatusCode.OK, raw);
        var runId = body.GetProperty("result").GetProperty("runId").GetString()!;
        body.GetProperty("result").GetProperty("joined").GetBoolean().Should().BeFalse();
        var run = (await LoadDocumentAsync<AuditRun>(host, runId))!;
        run.ShouldSatisfy(r => r.SiteId == site.Id && r.OrganizationId == Acme && r.Origin == site.Origin && r.Status == "Queued"
            && r.StartedBy == Max && !r.IsVerified && !r.DeepCrawl && r.MaxPages == 100 && r.WallTimeMinutes == 10 && r.ConnectionsPerHost == 1
            && !r.IntrusiveProbes);
        run.Participants.Should().Equal(Max);
        run.ScoringSnapshot.Should().NotBeNull();
        run.ScoringSnapshot!.Checks.Count.Should().BeGreaterThan(80, "every enabled catalogue entry");
        run.ScoringVersion.Should().Be(run.ScoringSnapshot.ComputeVersion());

        (await LoadDocumentAsync<TargetCooldown>(host, TargetCooldown.IdFor(site.Host)))!.ExpiresAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddHours(6), TimeSpan.FromMinutes(5));
        (await QueuedProbesAsync()).Should().Contain(runId, "the first stage's message is in the outbox with the run");
        (await LogAsync(site.Id!)).Should().Contain(e => e.Kind == AuditLogKinds.AuditStarted && e.RunId == runId && e.UserId == Max
            && e.ExpiresAtUtc > DateTime.UtcNow.AddDays(360));
    }

    [Fact]
    public async Task A_second_member_joins_the_running_audit_instead_of_starting_another()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var site = await SeedSiteAsync(host, Acme);
        var first = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        var runId = first.Body.GetProperty("result").GetProperty("runId").GetString()!;

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Ann);

        status.Should().Be(HttpStatusCode.OK, raw);
        body.GetProperty("result").GetProperty("joined").GetBoolean().Should().BeTrue();
        body.GetProperty("result").GetProperty("runId").GetString().Should().Be(runId);
        (await LoadDocumentAsync<AuditRun>(host, runId))!.Participants.Should().Equal(Max, Ann);
        (await LogAsync(site.Id!)).Should().Contain(e => e.Kind == AuditLogKinds.AuditJoined && e.UserId == Ann);
    }

    /// <summary>D17: the cool-down holds the target across all users; Globex does not join Acme's run (D18: members only).</summary>
    [Fact]
    public async Task Another_organization_neither_joins_nor_starts_while_an_unverified_target_cools_down()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var acmeSite = await SeedSiteAsync(host, Acme);
        var globexSite = await SeedSiteAsync(host, Globex, hostName: acmeSite.Host);
        (await ActionAsync(host, SiteTypeId, "Site", "StartAudit", acmeSite.Id!, Max)).Status.Should().Be(HttpStatusCode.OK);

        var (status, _, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", globexSite.Id!, Bob);

        status.Should().Be(HttpStatusCode.TooManyRequests, raw);
        raw.Should().Contain("audited recently");
        (await LogAsync(globexSite.Id!)).Should().Contain(e => e.Kind == AuditLogKinds.CoolDownRefused && e.UserId == Bob);
    }

    [Fact]
    public async Task A_verified_site_has_no_cool_down_and_gets_the_verified_limits()
    {
        await SetSettingAsync(host, SeoQuotas.VerifiedAudits.Limit.Key, "-1");
        var site = await SeedSiteAsync(host, Acme, verified: true);
        await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        await CompleteRunsAsync(site.Id!);

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);

        status.Should().Be(HttpStatusCode.OK, raw);
        var run = (await LoadDocumentAsync<AuditRun>(host, body.GetProperty("result").GetProperty("runId").GetString()!))!;
        run.ShouldSatisfy(r => r.IsVerified && r.DeepCrawl && r.MaxPages == 2000 && r.ConnectionsPerHost == 4 && r.IntrusiveProbes);
        (await LoadDocumentAsync<TargetCooldown>(host, TargetCooldown.IdFor(site.Host))).Should().BeNull();
    }

    [Fact]
    public async Task The_quota_refuses_with_429_and_the_refusal_is_logged()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "0");
        try
        {
            var site = await SeedSiteAsync(host, Acme);

            var (status, _, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Ann);

            status.Should().Be(HttpStatusCode.TooManyRequests, raw);
            (await LogAsync(site.Id!)).Should().Contain(e => e.Kind == AuditLogKinds.QuotaRefused);
            (await LoadDocumentAsync<TargetCooldown>(host, TargetCooldown.IdFor(site.Host))).Should().BeNull("a refused start leaves no cool-down");
        }
        finally
        {
            await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        }
    }

    [Fact]
    public async Task A_Viewer_cannot_start_and_a_full_crawl_needs_a_verified_site()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var site = await SeedSiteAsync(host, Acme);

        (await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Vic)).Status.Should().Be(HttpStatusCode.NotFound, "Member and up (D23)");
        (await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Bob)).Status.Should().Be(HttpStatusCode.NotFound);
        (await ActionAsync(host, SiteTypeId, "Site", "StartFullCrawl", site.Id!, Max)).Status.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_full_crawl_of_a_verified_site_takes_the_weekly_allowance_and_the_safety_ceiling()
    {
        await SetSettingAsync(host, SeoQuotas.FullCrawls.Limit.Key, "-1");
        var site = await SeedSiteAsync(host, Acme, verified: true);

        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartFullCrawl", site.Id!, Ann);

        status.Should().Be(HttpStatusCode.OK, raw);
        var run = (await LoadDocumentAsync<AuditRun>(host, body.GetProperty("result").GetProperty("runId").GetString()!))!;
        run.ShouldSatisfy(r => r.IsFullCrawl && r.MaxPages == 20_000 && r.WallTimeMinutes == 360);
    }

    [Fact]
    public async Task With_the_deep_crawl_gate_off_a_verified_site_gets_the_unverified_limits()
    {
        await SetSettingAsync(host, SeoQuotas.VerifiedAudits.Limit.Key, "-1");
        await SetSettingAsync(host, SeoSettings.DeepCrawl.Key, "Off");
        try
        {
            var site = await SeedSiteAsync(host, Acme, verified: true);

            var (_, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Ann);

            var run = (await LoadDocumentAsync<AuditRun>(host, body.GetProperty("result").GetProperty("runId").GetString()!))!;
            run.ShouldSatisfy(r => r.IsVerified && !r.DeepCrawl && r.MaxPages == 100);
        }
        finally
        {
            await SetSettingAsync(host, SeoSettings.DeepCrawl.Key, null);
        }
    }

    private async Task CompleteRunsAsync(string siteId)
    {
        using var session = host.Database.OpenAsyncSession();
        foreach (var run in await session.Query<AuditRun>().Customize(c => c.WaitForNonStaleResults()).Where(r => r.SiteId == siteId).ToListAsync())
            run.Status = "Completed";
        session.Advanced.WaitForIndexesAfterSaveChanges();
        await session.SaveChangesAsync();
    }

    private async Task<List<AuditLogEntry>> LogAsync(string siteId)
    {
        using var session = host.Database.OpenAsyncSession();
        return await session.Query<AuditLogEntry>().Customize(c => c.WaitForNonStaleResults()).Where(e => e.SiteId == siteId).ToListAsync();
    }

    /// <summary>The run ids of the <see cref="SiteProbeMessage"/>s in the outbox, whatever their envelope.</summary>
    private async Task<List<string>> QueuedProbesAsync()
    {
        using var session = host.Database.OpenAsyncSession();
        var raw = await session.Advanced.AsyncRawQuery<Newtonsoft.Json.Linq.JObject>("from SparkMessages").WaitForNonStaleResults().ToListAsync();
        return raw.Select(m => m.ToString(Newtonsoft.Json.Formatting.None))
            .Where(json => json.Contains(SeoQueues.Probe, StringComparison.Ordinal))
            .SelectMany(json => System.Text.RegularExpressions.Regex.Matches(json, "AuditRuns/[0-9A-Za-z-]+").Select(m => m.Value))
            .ToList();
    }
}
