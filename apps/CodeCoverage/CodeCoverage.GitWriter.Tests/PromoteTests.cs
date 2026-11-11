using System.Net;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// Segment P (<c>docs/release_lines_recovery_plan.md</c> §2b): a line is promoted to a green default-branch commit by one
/// merge commit with that commit's tree (Q-A = B), and refused when it holds changes the default branch does not (P1).
/// </summary>
public class PromoteTests : WriterTestBase
{
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n";

    private static Dictionary<string, object?> Request(Origin origin, string commit, params (string Line, bool OnlyIfChanged)[] lines) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["defaultBranch"] = "main",
        ["commit"] = commit,
        ["committer"] = new { name = "coverage-bot", email = "bot@example.invalid" },
        ["lines"] = lines.Select(l => new { branch = $"release/{l.Line}", ecosystem = l.Line.Split('-')[0], onlyIfChanged = l.OnlyIfChanged }).ToArray(),
        ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
    };

    private static Dictionary<string, object?> Request(Origin origin, string commit, params string[] lines)
        => Request(origin, commit, lines.Select(l => (l, false)).ToArray());

    /// <summary>main at the seed, with <c>release/dotnet-1.x</c> cut from it.</summary>
    private Origin Line()
    {
        var origin = Seed();
        origin.Checkout("main");
        origin.Checkout("release/dotnet-1.x", "main").Push("release/dotnet-1.x");
        origin.Checkout("main");
        return origin;
    }

    [Fact]
    public async Task A_line_gets_the_commits_tree_with_its_own_tip_as_first_parent()
    {
        var origin = Line();
        var tip = origin.Ref("refs/heads/release/dotnet-1.x")!;
        origin.Write("fix.txt", "fixed\n");
        var commit = origin.Commit("fix: guard (#7)");
        origin.Push("main");

        var response = await Writer.PromoteAsync(Request(origin, commit, "dotnet-1.x"));

        response.Outcome.Should().Be(JobOutcomes.Promoted, response.Detail);
        var line = response.Lines.Single();
        line.Outcome.Should().Be(PromoteService.Promoted);
        origin.Ref("refs/heads/release/dotnet-1.x").Should().Be(line.Commit);
        origin.InBare("rev-parse", line.Commit + "^{tree}").Should().Be(origin.InBare("rev-parse", commit + "^{tree}"));
        origin.InBare("rev-parse", line.Commit + "^1").Trim().Should().Be(tip);
        origin.InBare("rev-parse", line.Commit + "^2").Trim().Should().Be(commit);
        origin.InBare("cat-file", "commit", line.Commit).Should().Contain("committer coverage-bot <bot@example.invalid>")
            .And.Contain($"Promote release/dotnet-1.x to main {commit[..12]}");
    }

    [Fact]
    public async Task A_line_that_holds_the_commit_is_left_as_it_is()
    {
        var origin = Line();
        var commit = origin.Ref("refs/heads/main")!;
        var tip = origin.Ref("refs/heads/release/dotnet-1.x")!;

        var response = await Writer.PromoteAsync(Request(origin, commit, "dotnet-1.x"));

        response.Lines.Single().Outcome.Should().Be(PromoteService.Contained, response.Detail);
        origin.Ref("refs/heads/release/dotnet-1.x").Should().Be(tip);
    }

    [Fact]
    public async Task A_change_only_the_line_holds_refuses_the_line_and_names_it()
    {
        // P1: the promotion would drop the line's README edit, so nothing is pushed.
        var origin = Line();
        origin.Checkout("release/dotnet-1.x").Write("README.md", "line only\n");
        var lineCommit = origin.Commit("line: readme");
        origin.Push("release/dotnet-1.x");
        origin.Checkout("main").Write("fix.txt", "fixed\n");
        var commit = origin.Commit("fix: guard (#7)");
        origin.Push("main");

        var response = await Writer.PromoteAsync(Request(origin, commit, "dotnet-1.x"));

        var line = response.Lines.Single();
        line.Outcome.Should().Be(PromoteService.LineOnly, response.Detail);
        line.Paths.Should().Equal("README.md");
        line.Commits.Should().Equal($"{lineCommit} line: readme");
        origin.Ref("refs/heads/release/dotnet-1.x").Should().Be(lineCommit);
    }

    [Fact]
    public async Task An_older_copy_of_a_change_the_default_branch_changed_again_is_no_line_only_change()
    {
        // A backport of main's first README edit, which main has since edited again: the promotion loses nothing.
        var origin = Line();
        origin.Write("README.md", "first\n");
        origin.Commit("docs: first (#5)");
        origin.Write("gone.txt", "temporary\n");
        origin.Commit("feat: gone (#6)");
        origin.Push("main");
        origin.Checkout("release/dotnet-1.x").Write("README.md", "first\n");
        origin.Commit("docs: first (#5) (cherry picked)");
        origin.Push("release/dotnet-1.x");
        origin.Checkout("main").Write("README.md", "second\n");
        File.Delete(Path.Combine(origin.Work, "gone.txt"));
        var commit = origin.Commit("docs: second (#8)");
        origin.Push("main");

        var response = await Writer.PromoteAsync(Request(origin, commit, "dotnet-1.x"));

        response.Lines.Single().Outcome.Should().Be(PromoteService.Promoted, response.Detail);
        origin.InBare("cat-file", "blob", "refs/heads/release/dotnet-1.x:README.md").Should().Be("second\n");
    }

    [Fact]
    public async Task A_line_whose_ecosystem_did_not_change_stays_unless_it_was_named()
    {
        // P4: only a README change since the cut, so the dotnet line is unchanged; naming it promotes it anyway.
        using var origin = new Origin(Server, "promote-ecosystems");
        origin.Write("src/Core/Core.csproj", Csproj);
        origin.Write("README.md", "fixture\n");
        origin.Commit("packages");
        origin.Push("main");
        origin.Checkout("release/dotnet-1.x", "main").Push("release/dotnet-1.x");
        var tip = origin.Ref("refs/heads/release/dotnet-1.x")!;
        origin.Checkout("main").Write("README.md", "docs\n");
        var commit = origin.Commit("docs: readme (#9)");
        origin.Push("main");

        var unchanged = await Writer.PromoteAsync(Request(origin, commit, ("dotnet-1.x", true)));
        unchanged.Lines.Single().Outcome.Should().Be(PromoteService.Unchanged, unchanged.Detail);
        origin.Ref("refs/heads/release/dotnet-1.x").Should().Be(tip);

        var named = await Writer.PromoteAsync(Request(origin, commit, ("dotnet-1.x", false)));
        named.Lines.Single().Outcome.Should().Be(PromoteService.Promoted, named.Detail);
    }

    [Fact]
    public async Task A_commit_that_is_not_on_the_default_branch_is_refused()
    {
        var origin = Line();
        origin.Checkout("feature", "main").Write("x.txt", "x\n");
        var stray = origin.Commit("stray");
        origin.Push("feature");

        var response = await Writer.PromoteAsync(Request(origin, stray, "dotnet-1.x"));

        response.Outcome.Should().Be(JobOutcomes.Failed);
        response.Reason.Should().Be(JobReasons.Unplannable);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/Dotnet-1.x")]
    public async Task A_line_that_is_the_default_branch_or_has_a_bad_ecosystem_is_refused(string branch)
    {
        var origin = Line();
        var request = Request(origin, origin.Ref("refs/heads/main")!, "dotnet-1.x");
        request["lines"] = new[] { new { branch, ecosystem = branch == "main" ? "dotnet" : "Dotnet", onlyIfChanged = false } };

        var response = await Writer.PostPromoteAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
