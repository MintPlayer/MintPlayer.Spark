using System.Net;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #153 M10: a merged pull request applied to release branches. Each target is merged in memory on its own, after the
/// platform values are normalized to the line's (#570 S2, D8), and gets its own outcome (#570 D6).
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
    public async Task A_conflicting_line_does_not_hold_back_a_clean_one()
    {
        // #570 D6: lines are independent; the source pull request is merged already, so blocking 1.x buys nothing.
        var origin = Lines();
        origin.Checkout("release/2.x").Write("README.md", "two\n");
        origin.Commit("2.x: readme");
        origin.Push("release/2.x");
        origin.Checkout("main").Write("README.md", "fixed\n");
        var squash = origin.Commit("fix: readme (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["fix"], "1.x", "2.x"));

        response.Outcome.Should().Be("backported", response.Detail);
        response.Targets.Single(t => t.Branch == "release/1.x").Push.Should().Be(BackportService.Created);
        var conflicted = response.Targets.Single(t => t.Branch == "release/2.x");
        conflicted.Push.Should().Be(BackportService.Conflicted);
        conflicted.Conflicts.Should().Equal("README.md");
        conflicted.Commit.Should().BeNull();
        origin.InBare("cat-file", "blob", origin.Ref("refs/heads/backport/7-1.x") + ":README.md").Should().Be("fixed\n");
        origin.Ref("refs/heads/backport/7-2.x").Should().BeNull();
    }

    private static string Project(string tfm, string nullable = "enable")
        => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{tfm}</TargetFramework>\n    <Nullable>{nullable}</Nullable>\n  </PropertyGroup>\n</Project>\n";

    /// <summary>A project on net10.0, <c>release/1.x</c> cut there, then main moved to net11.0 (the #442 shape).</summary>
    private Origin PlatformLines()
    {
        var origin = Seed();
        origin.Checkout("main").Write("Lib/Lib.csproj", Project("net10.0"));
        origin.Commit("lib");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Push("release/1.x");
        origin.Checkout("main").Write("Lib/Lib.csproj", Project("net11.0"));
        origin.Commit("feat: net11 (#6)");
        origin.Push("main");
        return origin;
    }

    [Fact]
    public async Task An_edit_next_to_the_framework_lands_on_the_older_line_with_its_framework()
    {
        // #570 S2 scenario B: today this conflicts on the TargetFramework line next to the edit.
        var origin = PlatformLines();
        origin.Write("Lib/Lib.csproj", Project("net11.0", "disable"));
        var squash = origin.Commit("fix: nullable off (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["fix"], "1.x"));

        var target = response.Targets.Single();
        target.Push.Should().Be(BackportService.Created, string.Join(", ", target.Conflicts));
        target.PlatformChanges.Should().BeEmpty();
        origin.InBare("cat-file", "blob", origin.Ref("refs/heads/backport/7-1.x") + ":Lib/Lib.csproj").Should().Be(Project("net10.0", "disable"));
    }

    [Fact]
    public async Task A_change_of_the_line_own_platform_conflicts_and_says_so()
    {
        var origin = PlatformLines();
        origin.Write("Lib/Lib.csproj", Project("net10.0;net11.0"));
        var squash = origin.Commit("feat: multi-target (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["feat"], "1.x"));

        var target = response.Targets.Single();
        target.Push.Should().Be(BackportService.Conflicted);
        target.Conflicts.Should().Equal("Lib/Lib.csproj");
        target.PlatformChanges.Should().Equal([new("Lib/Lib.csproj", "TargetFramework", "net11.0", "net10.0;net11.0")]);
    }

    [Fact]
    public async Task A_platform_move_above_the_line_is_already_there()
    {
        var origin = PlatformLines();
        origin.Write("Lib/Lib.csproj", Project("net12.0"));
        var squash = origin.Commit("feat: net12 (#7)");
        origin.Push("main");

        var response = await Writer.BackportAsync(Request(origin, squash, ["feat"], "1.x"));

        response.Targets.Single().Push.Should().Be(BackportService.Empty);
        origin.Ref("refs/heads/backport/7-1.x").Should().BeNull();
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
