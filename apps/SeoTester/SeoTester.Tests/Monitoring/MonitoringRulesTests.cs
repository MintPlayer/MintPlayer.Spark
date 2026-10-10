using SeoTester.Catalogue;
using SeoTester.Monitoring;
using SeoTester.Scoring;

namespace SeoTester.Tests.Monitoring;

/// <summary>
/// The pure halves of scheduled monitoring and alerts (<c>docs/seo_analyzer_PRD.md</c> F9, F10, D12): when the next
/// slot is due, and what a finished run alerts about.
/// </summary>
public class MonitoringRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_schedule_switched_on_runs_one_period_after_the_latest_run_or_now()
    {
        MonitoringPlan.FirstSlot(MonitoringPlan.Weekly, Now.AddDays(-2), Now).Should().Be(Now.AddDays(5), "audited two days ago: the week starts then");
        MonitoringPlan.FirstSlot(MonitoringPlan.Monthly, Now.AddDays(-2), Now).Should().Be(Now.AddDays(-2).AddMonths(1));
        MonitoringPlan.FirstSlot(MonitoringPlan.Weekly, null, Now).Should().Be(Now, "never audited: the next sweep");
        MonitoringPlan.FirstSlot(MonitoringPlan.Weekly, Now.AddDays(-30), Now).Should().Be(Now, "the period passed long ago: not in the past");
        MonitoringPlan.FirstSlot(MonitoringPlan.Off, Now.AddDays(-2), Now).HasValue.Should().BeFalse();
        MonitoringPlan.FirstSlot(null, null, Now).HasValue.Should().BeFalse();
    }

    [Fact]
    public void The_next_slot_skips_the_slots_missed_while_the_app_was_down()
    {
        var due = Now.AddHours(-1);
        MonitoringPlan.NextAfter(MonitoringPlan.Weekly, due, Now).Should().Be(due.AddDays(7));
        MonitoringPlan.NextAfter(MonitoringPlan.Monthly, new DateTime(2026, 1, 31, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 2, 28, 8, 0, 0, DateTimeKind.Utc), "a month after the 31st is the month's last day");
        var longAgo = Now.AddDays(-20);
        MonitoringPlan.NextAfter(MonitoringPlan.Weekly, longAgo, Now).Should().Be(longAgo.AddDays(21), "three weeks missed: one run now, the next a whole period on");
        MonitoringPlan.NextAfter(MonitoringPlan.Off, due, Now).HasValue.Should().BeFalse();
    }

    [Fact]
    public void A_slot_key_names_the_site_and_the_moment()
    {
        MonitoringPlan.SlotKey("Sites/1-A", new DateTime(2026, 10, 10, 12, 0, 5, DateTimeKind.Utc)).Should().Be("Sites/1-A:20261010T120005Z");
    }

    private static AlertCheck Check(string id, CheckStatus status, string? delta, CheckSeverity severity = CheckSeverity.Medium) => new(id, status, delta, severity);

    [Fact]
    public void A_drop_of_at_least_the_threshold_alerts_when_both_runs_were_scored_alike()
    {
        AlertRules.Evaluate(80, 75, 5, sameScoring: true, []).Should().ContainSingle()
            .Which.Should().Be(new SiteAlert(SiteAlertKind.ScoreDrop, 80, 75));
        AlertRules.Evaluate(80, 76, 5, sameScoring: true, []).Should().BeEmpty("4 points is under the threshold");
        AlertRules.Evaluate(80, 60, 5, sameScoring: false, []).Should().BeEmpty("an administrator changed the weights: not the site's doing");
        AlertRules.Evaluate(80, 60, 0, sameScoring: true, []).Should().BeEmpty("0 switches score alerts off");
        AlertRules.Evaluate(null, 60, 5, sameScoring: true, []).Should().BeEmpty("no previous score");
        AlertRules.Evaluate(60, 90, 5, sameScoring: true, []).Should().BeEmpty("a rise is no alert");
    }

    [Fact]
    public void Only_a_new_critical_failure_alerts_and_each_once()
    {
        var alerts = AlertRules.Evaluate(70, 70, 5, true,
        [
            Check("security.https", CheckStatus.Fail, "New", CheckSeverity.Critical),
            Check("seo.title-present", CheckStatus.Fail, "New", CheckSeverity.Critical),
            Check("mobile.viewport", CheckStatus.Fail, "Unchanged", CheckSeverity.Critical),
            Check("seo.noindex-conflict", CheckStatus.Warn, "New", CheckSeverity.Critical),
            Check("seo.meta-description", CheckStatus.Fail, "New", CheckSeverity.High),
            Check("technical.robots-blocks-all", CheckStatus.Fail, null, CheckSeverity.Critical),
        ]);

        alerts.Should().ContainSingle();
        alerts[0].Kind.Should().Be(SiteAlertKind.NewCriticalIssue);
        alerts[0].CheckIds.Should().Equal("security.https", "seo.title-present");
    }

    [Fact]
    public void An_expiring_certificate_or_domain_alerts_on_every_run_while_it_fails()
    {
        var alerts = AlertRules.Evaluate(70, 70, 5, true,
        [
            Check(AlertRules.CertificateExpiryCheck, CheckStatus.Fail, "Unchanged", CheckSeverity.High),
            Check(AlertRules.DomainExpiryCheck, CheckStatus.Fail, "Unchanged"),
        ]);
        alerts.Select(a => a.Kind).Should().Equal(SiteAlertKind.CertificateExpiring, SiteAlertKind.DomainExpiring);

        AlertRules.Evaluate(70, 70, 5, true,
        [
            Check(AlertRules.CertificateExpiryCheck, CheckStatus.Warn, "New", CheckSeverity.High),
            Check(AlertRules.DomainExpiryCheck, CheckStatus.Pass, "Fixed"),
        ]).Should().BeEmpty("under 30 days for the certificate is a warning in the report, not an alert (F10: 14 days)");
    }

    [Fact]
    public void A_gate_admits_sites_by_its_scope()
    {
        SeoFeatureScope.Off.Admits(true).Should().BeFalse();
        SeoFeatureScope.VerifiedOnly.Admits(true).Should().BeTrue();
        SeoFeatureScope.VerifiedOnly.Admits(false).Should().BeFalse();
        SeoFeatureScope.AllSites.Admits(false).Should().BeTrue();
    }
}
