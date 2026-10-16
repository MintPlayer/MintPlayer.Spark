using CodeCoverage.Git.Dependencies;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// R4's replay (docs/code-coverage/dependency-updates-PRD.md §7.3, P6) on real repositories, without a writer host: the
/// rolling branch's human commits onto a rebuilt bot commit with <c>git merge-tree --merge-base</c>. <c>main</c> holds chalk
/// 4.1.0; <c>bot1</c> is the previous build (4.1.2), <c>rolling</c> = bot1 + the human commit, <c>bot2</c> the new build.
/// </summary>
public class RollingBranchReplayTests
{
    private const string Lock = "package-lock.json";

    private static string Manifest(string chalk, string lodash = "4.17.20") => $"{{\n  \"dependencies\": {{\n    \"chalk\": \"{chalk}\",\n    \"lodash\": \"{lodash}\"\n  }}\n}}\n";

    private static string Lockfile(string chalk) => $"{{\n  \"packages\": {{\n    \"node_modules/chalk\": {{\n      \"version\": \"{chalk}\"\n    }}\n  }}\n}}\n";

    private static readonly IReadOnlyList<ReplayManifestEntry> Entries =
    [
        new("chalk", "chalk", "package.json", "4.1.0", "4.1.3"),
        new("lodash", "lodash", "package.json", "4.17.20", "4.17.21"),
    ];

    private sealed record Fixture(TestRepo Repo, string Bare, string Bot1, string Rolling, string Bot2, string Human) : IDisposable
    {
        public void Dispose() => Repo.Dispose();
    }

    /// <param name="human">What the person changes on the rolling branch.</param>
    /// <param name="bot2Chalk">The new build's chalk.</param>
    private static Fixture Build(Action<TestRepo> human, string bot2Chalk = "4.1.2", string bot2Lodash = "4.17.21", string bot2Lock = "4.1.2")
    {
        var repo = new TestRepo();
        repo.Write("package.json", Manifest("4.1.0")).Write(Lock, Lockfile("4.1.0")).Write("README.md", "seed\n");
        repo.Commit("base");
        repo.Checkout("bot1", "main");
        repo.Write("package.json", Manifest("4.1.2")).Write(Lock, Lockfile("4.1.2"));
        var bot1 = repo.Commit("Update chalk");
        repo.Checkout("rolling", "bot1");
        human(repo);
        var humanSha = repo.Commit("A person's commit");
        repo.Checkout("bot2", "main");
        repo.Write("package.json", Manifest(bot2Chalk, bot2Lodash)).Write(Lock, Lockfile(bot2Lock));
        var bot2 = repo.Commit("Update dependencies");
        return new Fixture(repo, repo.Bare(), bot1, humanSha, bot2, humanSha);
    }

    private static RollingBranchReplay Replay() => new(TestRepo.Runner());

    private static string Show(string bare, string sha, string path) => TestRepo.Git(bare, "show", $"{sha}:{path}");

    [Fact]
    public async Task A_human_commit_outside_the_manifests_is_replayed_onto_the_new_build()
    {
        using var f = Build(r => r.Write("README.md", "fixed by hand\n"));
        var humans = await Replay().HumanCommitsAsync(f.Bare, f.Bot1, f.Rolling, CancellationToken.None);
        humans.Should().Equal([f.Human]);

        var outcome = await Replay().ReplayAsync(f.Bare, f.Bot2, humans!, Entries, CancellationToken.None);

        var replayed = outcome.Should().BeOfType<ReplayOutcome.Replayed>().Which;
        replayed.Commits.Should().ContainSingle();
        TestRepo.Git(f.Bare, "rev-parse", $"{replayed.Tip}^").Trim().Should().Be(f.Bot2);
        Show(f.Bare, replayed.Tip, "README.md").Should().Be("fixed by hand\n");
        Show(f.Bare, replayed.Tip, "package.json").Should().Contain("\"lodash\": \"4.17.21\"", "the new build's other updates stay");
        replayed.HumanValues.Should().BeEmpty();
        TestRepo.Git(f.Bare, "log", "-1", "--format=%an <%ae> %aI", replayed.Tip).Trim()
            .Should().Be(TestRepo.Git(f.Bare, "log", "-1", "--format=%an <%ae> %aI", f.Human).Trim(), "the person stays the author, with their date");
    }

    [Fact]
    public async Task A_human_manifest_value_wins_over_a_conflicting_new_build_and_is_reported()
    {
        using var f = Build(r => r.Write("package.json", Manifest("4.1.1")), bot2Chalk: "4.1.3");

        var outcome = await Replay().ReplayAsync(f.Bare, f.Bot2, [f.Human], Entries, CancellationToken.None);

        var replayed = outcome.Should().BeOfType<ReplayOutcome.Replayed>().Which;
        var manifest = Show(f.Bare, replayed.Tip, "package.json");
        manifest.Should().Contain("\"chalk\": \"4.1.1\"", "the human's value wins");
        manifest.Should().Contain("\"lodash\": \"4.17.21\"", "the bot's other edits in the file stay");
        manifest.Should().NotContain("<<<<<<<");
        var value = replayed.HumanValues.Should().ContainSingle().Which;
        value.DepName.Should().Be("chalk");
        value.HeldValue.Should().Be("4.1.1");
        value.ProposedValue.Should().Be("4.1.3");
        value.Commit.Should().Be(f.Human);
    }

    [Fact]
    public async Task A_lockfile_conflict_is_never_merged_textually()
    {
        using var f = Build(r => r.Write(Lock, Lockfile("4.1.1")), bot2Lock: "4.1.3");

        var outcome = await Replay().ReplayAsync(f.Bare, f.Bot2, [f.Human], Entries, CancellationToken.None);

        outcome.Should().BeOfType<ReplayOutcome.Conflict>().Which.Paths.Should().Equal([Lock]);
    }

    [Fact]
    public async Task A_branch_without_the_bots_commit_was_rewritten_and_is_not_replayed()
    {
        using var f = Build(r => r.Write("README.md", "x\n"));

        (await Replay().HumanCommitsAsync(f.Bare, f.Bot2, f.Rolling, CancellationToken.None)).Should().BeNull();
        (await Replay().HumanCommitsAsync(f.Bare, f.Bot1, f.Bot1, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void The_human_value_is_read_from_the_changed_line_naming_the_dependency()
    {
        const string diff = """
            diff --git a/pom.xml b/pom.xml
            index 1..2 100644
            --- a/pom.xml
            +++ b/pom.xml
            @@ -10,4 +10,4 @@
               <dependency>
                 <artifactId>guava</artifactId>
            -    <version>33.0.0-jre</version>
            +    <version>32.1.3-jre</version>
               </dependency>
            """;

        var values = RollingBranchReplay.ReadHumanValues(diff, "abc", "octocat", [new ReplayManifestEntry("com.google.guava:guava", null, "pom.xml", "32.0.0-jre", "33.0.0-jre")]);

        values.Should().ContainSingle().Which.HeldValue.Should().Be("32.1.3-jre");
    }

    [Fact]
    public void Each_conflict_region_takes_the_humans_side()
    {
        const string merged = "a\n<<<<<<< ours\nbot\n||||||| base\nold\n=======\nhuman\n>>>>>>> theirs\nb\r\n";

        RollingBranchReplay.TakeTheirs(merged).Should().Be("a\nhuman\nb\r\n");
        RollingBranchReplay.TakeTheirs("a\n<<<<<<< ours\nbot\n").Should().BeNull("an unterminated region is not guessed at");

        // Line by line where the sides line up: the human's change on x, the bot's on y.
        RollingBranchReplay.TakeTheirs("<<<<<<< ours\nx1\ny2\n||||||| base\nx0\ny0\n=======\nx3\ny0\n>>>>>>> theirs\n").Should().Be("x3\ny2\n");
        // Sides that do not line up: the human's lines whole.
        RollingBranchReplay.TakeTheirs("<<<<<<< ours\nx1\ny2\n||||||| base\nx0\n=======\nx3\n>>>>>>> theirs\n").Should().Be("x3\n");
    }

    [Theory]
    [InlineData("    \"chalk\": \"4.1.2\",", "    \"chalk\": \"4.1.1\",", "4.1.1")]
    [InlineData("chalk==4.1.2", "chalk==4.2.0", "4.2.0")]
    [InlineData("\"chalk\": \"^4.1.2\"", "\"chalk\": \"~4.1.1\"", "~4.1.1")]
    public void The_difference_is_the_whole_changed_token(string before, string after, string token)
        => RollingBranchReplay.Difference(before, after).Should().Be(token);
}
