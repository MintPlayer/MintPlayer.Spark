using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// #568 D13 (coverage_backport_PRD §8.4): each supported release line gets a dependency run of its own, on its own branch,
/// with its own bot branches and documents, configured from the default branch's coverage.yml.
/// </summary>
public class DependencyLineRunTests : CoverageRavenTest
{
    private const string Line = "dotnet-10.x";
    private const string LineBranch = "release/dotnet-10.x";

    private const string LinesYml = Yml + """

        versioning:
          lines: {}
        backport:
          lines:
            dotnet-10.x: keep
        """;

    private async Task<DependencyHarness> HarnessAsync(string yml = LinesYml)
    {
        var harness = new DependencyHarness(GetDocumentStore());
        harness.SetYml(yml);
        harness.Queue.BranchHeads[LineBranch] = BaseSha;
        await harness.SeedAsync();
        return harness;
    }

    private static Git.Dependencies.LookupDocument Chalk() => Lookup(Npm("chalk", "4.1.0", "4.1.2", Git.Dependencies.EUpdateType.Patch));

    [Fact]
    public async Task D13_a_line_run_looks_up_and_applies_on_the_line_s_branch_with_its_own_bot_branches()
    {
        var h = await HarnessAsync();
        h.Line = Line;

        await h.RunToApplyAsync(Chalk());

        h.Queue.Dispatches[0].Workflow.Should().Be("dependency-lookup.yml");
        h.Queue.Dispatches[0].Branch.Should().Be(LineBranch, "the lookup reads the line's manifests");
        h.Queue.Dispatches[0].Inputs["run_id"].Should().Contain(Line, "the line's artifacts never collide with the default branch's");
        var planCommit = h.Writer.Requests("work-ref").Should().ContainSingle().Which;
        planCommit.Line.Should().Be(Line);
        planCommit.BaseBranch.Should().Be(LineBranch);
        h.Queue.Dispatches[1].Branch.Should().Be("mintplayer/dependencies-work-dotnet-10.x");

        await h.FinishApplyAsync();

        h.Writer.Requests("rolling").Should().ContainSingle().Which.Line.Should().Be(Line);
        var pull = h.Updates.Created.Should().ContainSingle().Which;
        pull.Head.Should().Be("mintplayer/dependencies-dotnet-10.x");
        pull.Base.Should().Be(LineBranch);
        pull.Title.Should().StartWith("[dotnet-10.x] ");

        var run = await h.RunAsync();
        run.Id.Should().Be(DependencyUpdateRun.ForRepository(RepositoryId, Line));
        run.Line.Should().Be(Line);
        run.DefaultBranch.Should().Be(LineBranch);
        run.Onboarding.Should().Be(EDependencyOnboarding.None, "onboarding belongs to the default branch's run");
        h.Line = null;
        (await h.RunAsync()).Should().BeNull("a line's run leaves the default branch's run alone");
    }

    [Fact]
    public async Task D13_a_line_run_plans_under_its_ceiling_with_the_default_branch_s_configuration()
    {
        var h = await HarnessAsync();
        h.Line = Line;
        // The line's own commit says nothing about dependencies; only the default branch's coverage.yml counts.
        h.Forge.Files[$"{BaseSha}/{CodeCoverage.Feedback.CoverageYml.FileName}"] = "dependencies:\n  enabled: false\n";

        await h.RunToApplyAsync(Lookup(Npm("chalk", "4.1.0", "4.2.0"), Npm("lodash", "4.17.20", "4.17.21", Git.Dependencies.EUpdateType.Patch)));

        var plan = Git.Dependencies.DependencyContract.Deserialize<Git.Dependencies.DependencyPlan>(h.Writer.Requests("work-ref").Single().PlanJson!)!;
        plan.Groups.Select(g => g.Group).Should().Equal(["lodash"], "a line takes patches by default (dependencies.lines: patch)");
    }

    [Fact]
    public async Task D13_the_default_branch_s_run_records_the_supported_lines_for_the_scheduler()
    {
        var h = await HarnessAsync();

        await h.RunToApplyAsync(Chalk());

        (await h.RunAsync()).FileLines.Should().Equal([Line]);
    }

    [Fact]
    public async Task D13_lines_off_records_no_lines_and_ends_a_line_s_run()
    {
        var h = await HarnessAsync(LinesYml.Replace("  autoMerge:", "  lines: off\n  autoMerge:", StringComparison.Ordinal));

        await h.RunToApplyAsync(Chalk());
        (await h.RunAsync()).FileLines.Should().BeEmpty();

        h.Line = Line;
        await h.TriggerAsync();
        await h.DrainAsync();

        var run = await h.RunAsync();
        run.Outcome.Should().Be("disabled");
        run.State.Should().Be(EDependencyRunState.Idle);
    }

    [Fact]
    public async Task D13_a_line_without_a_branch_ends_its_run()
    {
        var h = await HarnessAsync();
        h.Queue.BranchHeads.Remove(LineBranch);
        h.Line = Line;

        await h.TriggerAsync();
        await h.DrainAsync();

        (await h.RunAsync()).Outcome.Should().Be("line-missing");
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task D13_a_line_signal_never_onboards()
    {
        var h = await HarnessAsync();

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Line = Line, Kind = EDependencySignal.Onboard });

        h.Line = Line;
        (await h.RunAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("mintplayer/dependencies", true, null)]
    [InlineData("mintplayer/dependencies-dotnet-10.x", true, "dotnet-10.x")]
    [InlineData("mintplayer/dependencies-work", false, null)]
    [InlineData("mintplayer/dependencies-work-dotnet-10.x", false, null)]
    [InlineData("mintplayer/dependencies-onboarding", false, null)]
    [InlineData("feature/x", false, null)]
    public void D13_a_rolling_branch_names_its_line(string branch, bool rolling, string? line)
    {
        DependencyUpdateRun.IsRollingBranch(branch, out var parsed).Should().Be(rolling);
        parsed.Should().Be(line);
    }
}
