using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.MergeQueue;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// Segment G: which lines a merge's trial pick tries (the labelled ones, never a promoted current line), and what its
/// conflicts make the merge queue do under <c>backport.trialPick</c>.
/// </summary>
public class BackportTrialPicksTests
{
    private static readonly string BaseSha = new('b', 40);
    private static readonly string Commit = new('c', 40);
    private const string Lines = "versioning:\n  lines:\n    current: promote\n    trigger: manual\n    workflow: release.yml\n";

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly Repository repository = new() { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", GitHubId = 153 };
    private GitWriterBackportTrialRequest? sent;

    public BackportTrialPicksTests()
    {
        writer.IsConfigured.Returns(true);
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        Yml(Lines);
        git.GetBranchHeadAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        foreach (var major in new[] { "9", "10", "11" })
            git.GetBranchHeadAsync(Arg.Any<Repository>(), $"release/dotnet-{major}.x", Arg.Any<CancellationToken>()).Returns(new string('1', 40));
        git.ListBranchesAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(["release/dotnet-9.x", "release/dotnet-10.x", "release/dotnet-11.x"]);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
        Labels("backport: dotnet-9.x", "backport: dotnet-10.x", "backport: dotnet-11.x", "bug");
        Answers(new GitWriterBackportTrialTargetResult("release/dotnet-9.x", GitWriterBackportTrialTargetResult.Clean, []),
            new GitWriterBackportTrialTargetResult("release/dotnet-10.x", GitWriterBackportTrialTargetResult.Conflicted, ["src/A.cs", "src/B.cs"], "a detail"));
    }

    private void Yml(string yml)
        => integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);

    private void Labels(params string[] labels)
        => forge.PullRequest = new(7, "feat: widgets", null, "feat", new string('h', 40), "main", true, false, false, null, labels);

    private void Answers(params GitWriterBackportTrialTargetResult[] targets)
        => writer.VersioningBackportTrialAsync(Arg.Do<GitWriterBackportTrialRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new GitWriterBackportTrialResult(GitWriterBackportTrialResult.Tried, null, targets, null, 5));

    private Task<BackportTrialVerdict?> RunAsync()
        => new BackportTrialPicks(new SingleForgeResolver(integration), [forge], [git], writer, NullLogger<BackportTrialPicks>.Instance)
            .RunAsync(repository, 7, BaseSha, Commit);

    [Fact]
    public async Task The_labelled_past_lines_are_tried_on_the_queued_commit_and_a_conflict_warns()
    {
        var verdict = await RunAsync();

        sent!.Targets.Select(t => t.Branch).Should().Equal(["release/dotnet-9.x", "release/dotnet-10.x"], "dotnet-11.x is the current line, promoted rather than backported to");
        sent.HeadRef.Should().Be("refs/pull/7/head");
        sent.Base.Should().Be(BaseSha);
        sent.Commit.Should().Be(Commit);
        verdict!.Refuse.Should().BeFalse();
        verdict.Paths.Should().Equal("src/A.cs", "src/B.cs");
        verdict.Message.Should().StartWith("Its backport onto `release/dotnet-10.x` will conflict once this merges.")
            .And.Contain("- `src/A.cs`\n").And.Contain("a detail").And.EndWith("Remove `backport: dotnet-10.x` before it merges to leave the line out.");
        await git.Received(1).RevokeWriterAccessAsync(repository, Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Under_refuse_a_conflict_refuses_the_merge()
    {
        Yml(Lines + "backport:\n  trialPick: refuse\n");

        var verdict = await RunAsync();

        verdict!.Refuse.Should().BeTrue();
        verdict.Message.Should().StartWith("its backport onto `release/dotnet-10.x` would conflict, and `backport.trialPick` refuses that.")
            .And.EndWith("Remove `backport: dotnet-10.x` to leave the line out, or change the pull request so it applies there, then queue it again.");
    }

    [Theory]
    [InlineData("backport:\n  trialPicks:\n    dotnet-10.x: refuse\n", true)]
    [InlineData("backport:\n  trialPick: refuse\n  trialPicks:\n    dotnet-10.x: warn\n", false)]
    [InlineData("backport:\n  trialPicks:\n    dotnet-9.x: refuse\n", false)]
    public async Task A_lines_own_mode_decides_for_its_conflict(string backport, bool refuses)
    {
        // Q10: "configurable per line". Only 10.x conflicts; 9.x refusing changes nothing.
        Yml(Lines + backport);

        (await RunAsync())!.Refuse.Should().Be(refuses);
    }

    [Fact]
    public async Task A_line_whose_trial_pick_is_off_is_not_tried()
    {
        Yml(Lines + "backport:\n  trialPicks:\n    dotnet-10.x: off\n");
        Answers(new GitWriterBackportTrialTargetResult("release/dotnet-9.x", GitWriterBackportTrialTargetResult.Clean, []));

        (await RunAsync()).Should().BeNull();
        sent!.Targets.Select(t => t.Branch).Should().Equal(["release/dotnet-9.x"]);
    }

    [Fact]
    public async Task Lines_that_all_apply_say_nothing()
    {
        Answers(new GitWriterBackportTrialTargetResult("release/dotnet-9.x", GitWriterBackportTrialTargetResult.Clean, []),
            new GitWriterBackportTrialTargetResult("release/dotnet-10.x", GitWriterBackportTrialTargetResult.Empty, []));

        (await RunAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Off_tries_nothing()
    {
        Yml(Lines + "backport:\n  trialPick: off\n");

        (await RunAsync()).Should().BeNull();
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportTrialAsync(default!, default);
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("backport: dotnet-11.x")]
    [InlineData("backport: dotnet-12.x")]
    [InlineData("backport: nonsense")]
    public async Task Without_a_labelled_line_to_try_nothing_runs(string label)
    {
        // No label, a promoted current line, a line whose branch is not cut, a label naming no line.
        Labels(label);

        (await RunAsync()).Should().BeNull();
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportTrialAsync(default!, default);
    }

    [Fact]
    public async Task Without_versioning_nothing_runs()
    {
        Yml("backport:\n  trialPick: refuse\n");

        (await RunAsync()).Should().BeNull();
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportTrialAsync(default!, default);
    }

    [Fact]
    public async Task A_trial_the_writer_could_not_run_never_blocks_the_merge()
    {
        Yml(Lines + "backport:\n  trialPick: refuse\n");
        writer.VersioningBackportTrialAsync(Arg.Any<GitWriterBackportTrialRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterBackportTrialResult("head-mismatch", null, [], "refs/pull/7/head is at another commit.", 5));

        (await RunAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_writer_that_refuses_or_does_not_answer_never_blocks_the_merge(bool refused)
    {
        Yml(Lines + "backport:\n  trialPick: refuse\n");
        writer.VersioningBackportTrialAsync(Arg.Any<GitWriterBackportTrialRequest>(), Arg.Any<CancellationToken>())
            .Throws(refused ? new GitWriterRefusedException("backport/trial", "targets", "a rule") : new HttpRequestException("down"));

        (await RunAsync()).Should().BeNull();
        await git.Received(1).RevokeWriterAccessAsync(repository, Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_writer_access_nothing_runs()
    {
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>()).Returns(new ForgePushAccess(null, "no installation"));

        (await RunAsync()).Should().BeNull();
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportTrialAsync(default!, default);
    }
}
