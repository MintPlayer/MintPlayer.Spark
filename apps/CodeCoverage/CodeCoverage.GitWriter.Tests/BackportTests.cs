using System.Net;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #153 M10: a merged pull request applied to release branches (D10). Every target is merged in memory first; one
/// conflicting target means nothing is pushed anywhere.
/// </summary>
public class BackportTests : WriterTestBase
{
    private static Dictionary<string, object?> Request(Origin origin, string commit, string[] subjects, params string[] lines) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["sourceBranch"] = "main",
        ["commit"] = commit,
        ["pullRequest"] = 7,
        ["title"] = "fix: guard against null",
        ["subjects"] = subjects,
        ["committer"] = new { name = "coverage-bot", email = "bot@example.invalid" },
        ["targets"] = lines.Select(l => new { branch = $"release/{l}", head = $"backport/7-{l}" }).ToArray(),
    };

    /// <summary>main at the seed, with <c>release/1.x</c> and <c>release/2.x</c> cut from it.</summary>
    private Origin Lines()
    {
        var origin = Seed();
        origin.Checkout("main");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Push("release/1.x");
        origin.Checkout("release/2.x", "main").Push("release/2.x");
        origin.Checkout("main");
        return origin;
    }

    [Fact]
    public async Task A_squash_merge_lands_on_every_line_with_its_trailer()
    {
        var origin = Lines();
        origin.Write("fix.txt", "fixed\n");
        var squash = origin.Commit("fix: guard against null (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["wip", "fix"], "1.x", "2.x"));

        response.Outcome.Should().Be("backported", response.Detail);
        response.Mode.Should().Be("squash");
        response.Targets.Select(t => t.Push).Should().Equal("created", "created");
        foreach (var line in new[] { "1.x", "2.x" })
        {
            var head = origin.Ref($"refs/heads/backport/7-{line}")!;
            origin.InBare("rev-parse", head + "^").Trim().Should().Be(origin.Ref($"refs/heads/release/{line}"));
            origin.InBare("cat-file", "blob", head + ":fix.txt").Should().Be("fixed\n");
            var commit = origin.InBare("cat-file", "commit", head);
            commit.Should().Contain("fix: guard against null (#7)").And.Contain($"(cherry picked from commit {squash})")
                .And.Contain("author Fixture Author <author@example.invalid>").And.Contain("committer coverage-bot <bot@example.invalid>");
        }
    }

    [Fact]
    public async Task One_conflicting_line_pushes_nothing_anywhere()
    {
        var origin = Lines();
        origin.Checkout("release/2.x").Write("README.md", "two\n");
        origin.Commit("2.x: readme");
        origin.Push("release/2.x");
        origin.Checkout("main").Write("README.md", "fixed\n");
        var squash = origin.Commit("fix: readme (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["fix"], "1.x", "2.x"));

        response.Outcome.Should().Be("conflicted", response.Detail);
        response.Targets.Single(t => t.Branch == "release/1.x").Conflicts.Should().BeEmpty();
        response.Targets.Single(t => t.Branch == "release/2.x").Conflicts.Should().Equal("README.md");
        response.Targets.Select(t => t.Push).Should().AllSatisfy(p => p.Should().BeNull());
        origin.Ref("refs/heads/backport/7-1.x").Should().BeNull();
        origin.Ref("refs/heads/backport/7-2.x").Should().BeNull();
    }

    [Fact]
    public async Task A_rebase_merge_takes_the_whole_run_as_one_commit()
    {
        var origin = Lines();
        var before = origin.Ref("refs/heads/main")!;
        origin.Write("a.txt", "a\n");
        var first = origin.Commit("feat: a");
        origin.Write("b.txt", "b\n");
        var last = origin.Commit("feat: b");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, last, ["feat: a", "feat: b"], "1.x"));

        response.Outcome.Should().Be("backported", response.Detail);
        response.Mode.Should().Be("rebase");
        response.Base.Should().Be(before);
        var head = origin.Ref("refs/heads/backport/7-1.x")!;
        origin.InBare("ls-tree", "--name-only", head).Should().Contain("a.txt").And.Contain("b.txt");
        origin.InBare("cat-file", "commit", head).Should().Contain("fix: guard against null (#7)")
            .And.Contain($"(cherry picked from commit {first})").And.Contain($"(cherry picked from commit {last})");
    }

    [Fact]
    public async Task A_line_that_already_holds_the_change_gets_no_branch()
    {
        var origin = Lines();
        origin.Write("fix.txt", "fixed\n");
        var squash = origin.Commit("fix: thing (#7)");
        origin.Push("main");
        origin.Checkout("release/1.x").Write("fix.txt", "fixed\n");
        origin.Commit("1.x: same fix by hand");
        origin.Push("release/1.x");

        var response = await Writer.BackportAsync(Request(origin, squash, ["fix"], "1.x"));

        response.Targets.Single().Push.Should().Be(BackportService.Empty);
        origin.Ref("refs/heads/backport/7-1.x").Should().BeNull();
    }

    [Theory]
    [InlineData("targets")]
    [InlineData("head")]
    [InlineData("source")]
    [InlineData("title")]
    public async Task A_malformed_request_is_refused_before_any_git(string broken)
    {
        var origin = Lines();
        var request = Request(origin, origin.Ref("refs/heads/main")!, [], "1.x");
        switch (broken)
        {
            case "targets": request["targets"] = Array.Empty<object>(); break;
            case "head": request["targets"] = new[] { new { branch = "release/1.x", head = "main" } }; break;
            case "source": request["targets"] = new[] { new { branch = "main", head = "backport/7-main" } }; break;
            case "title": request["title"] = "two\nlines"; break;
        }

        var response = await Writer.PostBackportAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
