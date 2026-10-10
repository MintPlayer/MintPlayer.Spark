using SeoTester.Catalogue;
using SeoTester.Scoring;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Scoring;

/// <summary>
/// The scoring engine (<c>docs/seo_analyzer_PRD.md</c> §7, D15, D32) on hand-made fixtures: every number below is
/// worked out in the test's comment, so a change to a rule shows up as a changed expectation.
/// </summary>
public class ScoreCalculatorTests
{
    private static readonly Dictionary<CheckCategory, int> Weights = new()
    {
        [CheckCategory.Seo] = 25, [CheckCategory.Technical] = 20, [CheckCategory.Performance] = 20, [CheckCategory.Security] = 15,
        [CheckCategory.Accessibility] = 10, [CheckCategory.Social] = 5, [CheckCategory.Mobile] = 5,
    };

    private static ScoredCheck Check(string id, CheckCategory category, CheckSeverity severity, int? weight = null, bool enabled = true)
        => new() { CheckId = id, Category = category, Severity = severity, Weight = weight ?? CheckSeverityDefaults.Weight(severity), Enabled = enabled };

    private static ScoringSnapshot Snapshot(params ScoredCheck[] checks) => ScoringSnapshot.Create(Weights, checks);

    [Fact]
    public void A_category_is_the_weight_weighted_mean_of_its_checks_and_warnings_count_half()
    {
        var snapshot = Snapshot(
            Check("seo.a", CheckCategory.Seo, CheckSeverity.High),      // 6
            Check("seo.b", CheckCategory.Seo, CheckSeverity.Medium));   // 3

        // a: 8 pass, 2 warn, 0 fail over 10 pages → (8 + 1) / 10 = 0.9; b: fails its one site-level check → 0.
        // (6 × 0.9 + 3 × 0) / 9 × 100 = 60.
        var card = ScoreCalculator.Score(snapshot, [new CheckOutcome("seo.a", 8, 2, 0), CheckOutcome.Site("seo.b", CheckStatus.Fail)]);

        var seo = card.Categories.Single(c => c.Category == CheckCategory.Seo);
        seo.Score.Should().Be(60);
        seo.Grade.Should().Be("D");
        card.Overall.Should().Be(60, "the only measured category");
        card.Checks.Single(c => c.CheckId == "seo.a").Status.Should().Be(CheckStatus.Warn);
        card.Checks.Single(c => c.CheckId == "seo.a").Affected.Should().Be(2);
    }

    [Fact]
    public void Not_applicable_and_errors_are_left_out_of_the_denominator()
    {
        var snapshot = Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.High), Check("seo.b", CheckCategory.Seo, CheckSeverity.High));

        // a: 4 pass, 6 N/A, 3 errors → 4/4 = 1. b: only errors → not measured, so it does not drag the category down.
        var card = ScoreCalculator.Score(snapshot, [new CheckOutcome("seo.a", 4, 0, 0, NotApplicable: 6, Error: 3), new CheckOutcome("seo.b", 0, 0, 0, Error: 2)]);

        card.Categories.Single(c => c.Category == CheckCategory.Seo).Score.Should().Be(100);
        card.Checks.Single(c => c.CheckId == "seo.b").ShouldSatisfy(c => c.Status == CheckStatus.Error && c.Ratio == null);
    }

    [Fact]
    public void A_failing_critical_check_caps_its_category_at_an_F()
    {
        var snapshot = Snapshot(
            Check("seo.title", CheckCategory.Seo, CheckSeverity.Critical),   // 10
            Check("seo.other", CheckCategory.Seo, CheckSeverity.High));       // 6

        // title fails on 6 of 10 pages → 0.4 (< 0.5); other passes. Mean = (10 × 0.4 + 6) / 16 × 100 = 62.5 → capped to 49.
        var capped = ScoreCalculator.Score(snapshot, [new CheckOutcome("seo.title", 4, 0, 6), new CheckOutcome("seo.other", 10, 0, 0)]);
        // title fails on 4 of 10 pages → 0.6: no cap. (10 × 0.6 + 6) / 16 × 100 = 75.
        var uncapped = ScoreCalculator.Score(snapshot, [new CheckOutcome("seo.title", 6, 0, 4), new CheckOutcome("seo.other", 10, 0, 0)]);

        capped.Categories.Single(c => c.Category == CheckCategory.Seo).ShouldSatisfy(c => c.Score == 49 && c.Capped && c.Grade == "F");
        uncapped.Categories.Single(c => c.Category == CheckCategory.Seo).ShouldSatisfy(c => c.Score == 75 && !c.Capped);
    }

    [Fact]
    public void The_headline_is_the_category_weighted_mean_of_measured_categories_and_Domain_and_Info_never_count()
    {
        var snapshot = Snapshot(
            Check("seo.a", CheckCategory.Seo, CheckSeverity.Medium),
            Check("security.a", CheckCategory.Security, CheckSeverity.Medium),
            Check("domain.a", CheckCategory.Domain, CheckSeverity.Medium),
            Check("seo.info", CheckCategory.Seo, CheckSeverity.Info));

        // SEO 100 (weight 25), Security 0 (weight 15), Domain 0 (weight 0: shown, not counted), Info failing (weight 0).
        // (25 × 100 + 15 × 0) / 40 = 62.5 → 63 (half away from zero). Performance etc. are not measured and drop out.
        var card = ScoreCalculator.Score(snapshot,
        [
            CheckOutcome.Site("seo.a", CheckStatus.Pass), CheckOutcome.Site("security.a", CheckStatus.Fail),
            CheckOutcome.Site("domain.a", CheckStatus.Fail), CheckOutcome.Site("seo.info", CheckStatus.Fail),
        ]);

        card.Overall.Should().Be(63);
        card.Grade.Should().Be("D");
        card.Categories.Single(c => c.Category == CheckCategory.Domain).ShouldSatisfy(c => c.Score == 0 && c.Weight == 0);
        card.Categories.Single(c => c.Category == CheckCategory.Seo).Score.Should().Be(100, "an Info check is shown but never scored");
        card.Categories.Single(c => c.Category == CheckCategory.Performance).Score.HasValue.Should().BeFalse("not measured");
    }

    [Fact]
    public void Nothing_measured_gives_no_headline_and_unknown_or_disabled_checks_are_ignored()
    {
        var snapshot = Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.High), Check("seo.off", CheckCategory.Seo, CheckSeverity.High, enabled: false));

        var card = ScoreCalculator.Score(snapshot, [CheckOutcome.Site("seo.off", CheckStatus.Fail), CheckOutcome.Site("seo.unknown", CheckStatus.Fail), CheckOutcome.Site("seo.a", CheckStatus.NotApplicable)]);

        card.Overall.HasValue.Should().BeFalse();
        card.Grade.Should().BeNull();
        card.Checks.Select(c => c.CheckId).Should().Equal("seo.a");
        snapshot.Checks.Select(c => c.CheckId).Should().Equal("seo.a");
    }

    [Theory]
    [InlineData(100, "A")]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(80, "B")]
    [InlineData(79, "C")]
    [InlineData(70, "C")]
    [InlineData(69, "D")]
    [InlineData(50, "D")]
    [InlineData(49, "F")]
    [InlineData(0, "F")]
    public void Grades_follow_D15(int score, string grade) => ScoreCalculator.GradeOf(score).Should().Be(grade);

    [Fact]
    public void Scoring_is_deterministic_whatever_the_order_of_checks_and_outcomes()
    {
        ScoredCheck[] checks =
        [
            Check("seo.a", CheckCategory.Seo, CheckSeverity.High), Check("security.b", CheckCategory.Security, CheckSeverity.Critical),
            Check("performance.c", CheckCategory.Performance, CheckSeverity.Low),
        ];
        CheckOutcome[] outcomes = [new("seo.a", 3, 1, 1), new("security.b", 1, 0, 0), new("performance.c", 0, 2, 5), new("seo.a", 1, 0, 0)];

        var forward = ScoreCalculator.Score(ScoringSnapshot.Create(Weights, checks), outcomes);
        var backward = ScoreCalculator.Score(ScoringSnapshot.Create(Weights.Reverse(), checks.Reverse()), outcomes.Reverse());

        backward.Overall.Should().Be(forward.Overall);
        backward.ScoringVersion.Should().Be(forward.ScoringVersion);
        backward.Categories.Select(c => (c.Category, c.Score)).Should().Equal(forward.Categories.Select(c => (c.Category, c.Score)));
        forward.Checks.Single(c => c.CheckId == "seo.a").Affected.Should().Be(2, "outcomes of one check add up (4 + 1 pass, 1 warn, 1 fail)");
    }

    [Fact]
    public void The_scoring_version_changes_with_a_weight_a_severity_or_a_category_weight_and_with_nothing_else()
    {
        var baseline = Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.High));

        Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.High)).Version.Should().Be(baseline.Version);
        Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.High, weight: 5)).Version.Should().NotBe(baseline.Version);
        Snapshot(Check("seo.a", CheckCategory.Seo, CheckSeverity.Medium, weight: 6)).Version.Should().NotBe(baseline.Version);
        ScoringSnapshot.Create(new Dictionary<CheckCategory, int>(Weights) { [CheckCategory.Seo] = 30 }, [Check("seo.a", CheckCategory.Seo, CheckSeverity.High)])
            .Version.Should().NotBe(baseline.Version);
        baseline.Version.Should().MatchRegex("^[0-9a-f]{16}$");
        baseline.ComputeVersion().Should().Be(baseline.Version, "a stored snapshot recomputes to its version");
    }
}
