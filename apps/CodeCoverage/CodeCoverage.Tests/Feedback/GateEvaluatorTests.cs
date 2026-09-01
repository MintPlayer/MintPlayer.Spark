using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using FluentAssertions;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The published check-runs' behaviour, pinned pure: a missing baseline or
/// diff is neutral (never red), Blocking off posts numbers without verdicts,
/// thresholds are points of allowed drop, and partial builds are judged on
/// the configured basis.
/// </summary>
public class GateEvaluatorTests
{
    private static CoverageSummary Summary(int covered, int coverable)
        => new() { LinesCovered = covered, LinesCoverable = coverable };

    private static BuildComparer.Result Comparison(
        CoverageSummary? baseCoverage = null, string mode = ResolvedBase.Exact,
        PartialComparison.Result? partial = null, string[]? reasons = null)
        => new(new ResolvedBase("req0000", baseCoverage is null ? null : "base0000", baseCoverage is null ? ResolvedBase.None : mode,
            baseCoverage is null ? null : "builds/1", baseCoverage), partial, reasons ?? []);

    // ---- project ----------------------------------------------------------

    [Fact]
    public void Blocking_ratchet_fails_when_coverage_drops_past_the_threshold()
    {
        var gate = new GateSettings { Blocking = true, ProjectThreshold = 1 };
        var build = new Build { Coverage = Summary(70, 100) };

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(80, 100)));

        verdict.Conclusion.Should().Be("failure");
        verdict.Passed.Should().BeFalse();
    }

    [Fact]
    public void The_threshold_is_allowed_drop_in_points()
    {
        var gate = new GateSettings { Blocking = true, ProjectThreshold = 2 };
        var build = new Build { Coverage = Summary(785, 1000) }; // 78.5% vs base 80%, drop 1.5 <= 2

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(800, 1000)));

        verdict.Conclusion.Should().Be("success");
    }

    [Fact]
    public void Informational_mode_posts_the_same_numbers_but_never_fails()
    {
        var gate = new GateSettings { Blocking = false };
        var build = new Build { Coverage = Summary(10, 100) };

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(90, 100)));

        verdict.Conclusion.Should().Be("neutral");
        verdict.Passed.Should().BeFalse("the raw verdict must survive for anyone reading the summary");
    }

    [Fact]
    public void No_baseline_is_neutral_never_red()
    {
        var gate = new GateSettings { Blocking = true };
        var build = new Build { Coverage = Summary(50, 100) };

        var verdict = GateEvaluator.Project(gate, build, Comparison(baseCoverage: null));

        verdict.Conclusion.Should().Be("neutral");
        verdict.Passed.Should().BeNull();
    }

    [Fact]
    public void Fixed_mode_needs_no_base_at_all()
    {
        var gate = new GateSettings { Blocking = true, ProjectMode = "fixed", ProjectTarget = 75 };
        var build = new Build { Coverage = Summary(80, 100) };

        var verdict = GateEvaluator.Project(gate, build, Comparison(baseCoverage: null));

        verdict.Conclusion.Should().Be("success");
    }

    [Fact]
    public void A_partial_build_on_the_scoped_basis_compares_measured_paths_only()
    {
        var gate = new GateSettings { Blocking = true };
        // Whole-workspace base is 90%, but the measured subset was at 50% and improved to 60%.
        var build = new Build { Partial = true, Coverage = Summary(60, 100) };
        var partial = new PartialComparison.Result(Summary(50, 100), Summary(900, 1000), 1, 0);

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(900, 1000), partial: partial));

        verdict.Conclusion.Should().Be("success", "60% vs the scoped 50%, not vs the whole-workspace 90%");
    }

    [Fact]
    public void A_partial_build_on_the_projection_basis_compares_the_patched_total()
    {
        var gate = new GateSettings { Blocking = true, ProjectBasis = "projection" };
        var build = new Build { Partial = true, Coverage = Summary(10, 100) };
        var partial = new PartialComparison.Result(Summary(5, 100), Summary(905, 1000), 1, 0);

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(900, 1000), partial: partial));

        verdict.Conclusion.Should().Be("success", "projection 90.5% vs base 90.0%");
        verdict.Title.Should().Contain("90.5%");
    }

    [Fact]
    public void A_walked_base_is_disclosed_in_the_summary()
    {
        var gate = new GateSettings();
        var build = new Build { Partial = true, Coverage = Summary(5, 10) };
        var partial = new PartialComparison.Result(Summary(5, 10), Summary(5, 10), 1, 0);

        var verdict = GateEvaluator.Project(gate, build, Comparison(Summary(5, 10), mode: ResolvedBase.Walked, partial: partial, reasons: ["baseWalked"]));

        verdict.Summary.Should().Contain("walked");
        verdict.Summary.Should().Contain("req0000");
    }

    // ---- patch ------------------------------------------------------------

    [Fact]
    public void No_diff_is_neutral()
    {
        var verdict = GateEvaluator.Patch(new GateSettings { Blocking = true, PatchTarget = 80 }, new Build());

        verdict.Conclusion.Should().Be("neutral");
        verdict.Passed.Should().BeNull();
    }

    [Fact]
    public void No_coverable_added_lines_is_neutral()
    {
        var build = new Build { Patch = new PatchCoverage { LinesCoverable = 0, FilesInDiff = 3 } };

        GateEvaluator.Patch(new GateSettings { Blocking = true, PatchTarget = 80 }, build)
            .Conclusion.Should().Be("neutral");
    }

    [Fact]
    public void Patch_target_with_tolerance_gates_the_added_lines()
    {
        var gate = new GateSettings { Blocking = true, PatchTarget = 80, PatchThreshold = 5 };
        var build = new Build { Patch = new PatchCoverage { LinesCovered = 76, LinesCoverable = 100, FilesInDiff = 2, FilesMatched = 2 } };

        GateEvaluator.Patch(gate, build).Conclusion.Should().Be("success", "76% >= 80% - 5 points");

        build.Patch.LinesCovered = 74;
        GateEvaluator.Patch(gate, build).Conclusion.Should().Be("failure");
    }

    [Fact]
    public void No_patch_target_means_informational_numbers()
    {
        var build = new Build { Patch = new PatchCoverage { LinesCovered = 1, LinesCoverable = 10, FilesInDiff = 1, FilesMatched = 1 } };

        var verdict = GateEvaluator.Patch(new GateSettings { Blocking = true }, build);

        verdict.Conclusion.Should().Be("neutral");
        verdict.Title.Should().Contain("10.0%");
    }

    [Fact]
    public void Truncated_diffs_say_so()
    {
        var build = new Build { Patch = new PatchCoverage { LinesCovered = 5, LinesCoverable = 10, FilesInDiff = 300, FilesMatched = 5, DiffTruncated = true } };

        GateEvaluator.Patch(new GateSettings(), build).Summary.Should().Contain("300-file cap");
    }
}
