using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Forge;

/// <summary>
/// O12's gate: every check counts unless excluded, required checks cannot be excluded, and nothing passes
/// until everything expected has reported.
/// </summary>
public class CheckGateTests
{
    private const string Sha = "4a5b6c7d8e9f00112233445566778899aabbccdd";

    private static ForgeCheckRun Run(string name, string status = "completed", string? conclusion = "success", long suite = 1)
        => new(name.GetHashCode(), name, status, status == "completed" ? conclusion : null, suite, "github-actions", null);

    private static ForgeCheckSuite Suite(long id, string status = "completed", int runs = 1, string slug = "github-actions")
        => new(id, status, status == "completed" ? "success" : null, slug, runs);

    private static ForgeCheckState State(ForgeCheckRun[]? runs = null, ForgeCommitStatus[]? statuses = null, ForgeCheckSuite[]? suites = null)
        => new(Sha, runs ?? [], statuses ?? [], suites ?? [Suite(1)]);

    private static CheckGateResult Evaluate(ForgeCheckState state, string[]? exclusions = null, string[]? required = null, string[]? expected = null)
        => CheckGate.Evaluate(state, exclusions ?? [], required ?? [], expected ?? []);

    [Fact]
    public void All_green_passes()
    {
        var result = Evaluate(State([Run("build"), Run("test")], [new ForgeCommitStatus("GitGuardian", "success", null)]),
            expected: ["build", "test"]);

        result.Verdict.Should().Be(ECheckVerdict.Passed);
        result.Passed.Should().Equal(["GitGuardian", "build", "test"]);
    }

    [Fact]
    public void A_failure_fails_without_waiting_for_the_rest()
    {
        var result = Evaluate(State([Run("build", conclusion: "failure"), Run("test", "in_progress")]));

        result.Verdict.Should().Be(ECheckVerdict.Failed);
        result.Failed.Should().Equal(["build"]);
        result.Pending.Should().Equal(["test"]);
    }

    [Theory]
    [InlineData("neutral", ECheckVerdict.Passed)]
    [InlineData("skipped", ECheckVerdict.Passed)]
    [InlineData("cancelled", ECheckVerdict.Failed)]
    [InlineData("timed_out", ECheckVerdict.Failed)]
    [InlineData("action_required", ECheckVerdict.Failed)]
    [InlineData("stale", ECheckVerdict.Failed)]
    public void Conclusions_map_like_a_required_check(string conclusion, ECheckVerdict expected)
        => Evaluate(State([Run("build", conclusion: conclusion)])).Verdict.Should().Be(expected);

    [Theory]
    [InlineData("error", ECheckVerdict.Failed)]
    [InlineData("failure", ECheckVerdict.Failed)]
    [InlineData("pending", ECheckVerdict.Pending)]
    public void Commit_statuses_count_too(string state, ECheckVerdict expected)
        => Evaluate(State(statuses: [new ForgeCommitStatus("ci/legacy", state, null)])).Verdict.Should().Be(expected);

    [Fact]
    public void An_excluded_failure_is_listed_but_does_not_fail()
    {
        // The motivating case: GitGuardian flagging a modelHashes.json hash line (O12).
        var result = Evaluate(State([Run("build")], [new ForgeCommitStatus("GitGuardian Security Checks", "failure", null)]),
            exclusions: ["GitGuardian Security Checks"]);

        result.Verdict.Should().Be(ECheckVerdict.Passed);
        result.Excluded.Should().Equal(["GitGuardian Security Checks"]);
    }

    [Fact]
    public void A_required_check_cannot_be_excluded()
    {
        var result = Evaluate(State([Run("build", conclusion: "failure")]), exclusions: ["build"], required: ["build"]);

        result.Verdict.Should().Be(ECheckVerdict.Failed);
        result.Excluded.Should().BeEmpty();
    }

    [Fact]
    public void An_expected_check_that_has_not_reported_keeps_the_gate_pending()
    {
        // The workflow has not started yet: nothing failed, nothing is running, and that is not a pass.
        var result = Evaluate(State([Run("lint")]), expected: ["lint", "test"], required: ["build"]);

        result.Verdict.Should().Be(ECheckVerdict.Pending);
        result.Missing.Should().Equal(["build", "test"]);
    }

    [Fact]
    public void An_excluded_expected_check_is_not_waited_for()
        => Evaluate(State([Run("build")]), exclusions: ["slow-scan"], expected: ["build", "slow-scan"])
            .Verdict.Should().Be(ECheckVerdict.Passed);

    [Fact]
    public void The_queue_check_itself_is_never_counted()
    {
        var result = Evaluate(State([Run("build"), Run(CheckGate.QueueCheckName, "in_progress", suite: 2)], suites: [Suite(1), Suite(2, "in_progress")]),
            expected: [CheckGate.QueueCheckName]);

        result.Verdict.Should().Be(ECheckVerdict.Passed);
        result.Excluded.Should().BeEmpty();
    }

    [Fact]
    public void A_running_suite_keeps_the_gate_pending()
    {
        var result = Evaluate(State([Run("build")], suites: [Suite(1), Suite(7, "in_progress", runs: 3, slug: "other-ci")]));

        result.Verdict.Should().Be(ECheckVerdict.Pending);
        result.PendingSuites.Should().Equal(["other-ci"]);
    }

    [Fact]
    public void A_suite_with_no_runs_is_not_waited_for()
        // GitHub leaves an empty suite queued for every App with checks permission.
        => Evaluate(State([Run("build")], suites: [Suite(1), Suite(9, "queued", runs: 0, slug: "some-app")]))
            .Verdict.Should().Be(ECheckVerdict.Passed);

    [Fact]
    public void A_suite_of_excluded_runs_is_not_waited_for()
        => Evaluate(State([Run("build"), Run("slow-scan", "in_progress", suite: 3)], suites: [Suite(1), Suite(3, "in_progress")]),
                exclusions: ["slow-scan"])
            .Verdict.Should().Be(ECheckVerdict.Passed);

    [Fact]
    public void A_name_green_in_one_place_and_red_in_another_is_red()
    {
        var result = Evaluate(State([Run("ci")], [new ForgeCommitStatus("ci", "failure", null)]));

        result.Verdict.Should().Be(ECheckVerdict.Failed);
        result.Passed.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_reported_and_nothing_expected_passes()
        // A repository with no CI at all: the expected-minimum set (the base's checks) is empty too.
        => Evaluate(State(suites: [])).Verdict.Should().Be(ECheckVerdict.Passed);
}
