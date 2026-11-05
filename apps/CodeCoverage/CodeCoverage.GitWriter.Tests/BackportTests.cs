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

    [Fact]
    public async Task A_draft_line_gets_its_conflict_pushed_with_the_markers()
    {
        // #570 D7: onConflict draft. The clean line is untouched by it.
        var origin = Lines();
        origin.Checkout("release/2.x").Write("README.md", "two\n");
        origin.Commit("2.x: readme");
        origin.Push("release/2.x");
        origin.Checkout("main").Write("README.md", "fixed\n");
        var squash = origin.Commit("fix: readme (#7)");
        origin.Push("main");
        var request = Request(origin, squash, ["fix"], "1.x", "2.x");
        request["targets"] = new[] { new { branch = "release/1.x", head = "backport/7-1.x", draft = true }, new { branch = "release/2.x", head = "backport/7-2.x", draft = true } };

        var response = await Writer.BackportAsync(request);

        response.Targets.Single(t => t.Branch == "release/1.x").Push.Should().Be(BackportService.Created);
        var drafted = response.Targets.Single(t => t.Branch == "release/2.x");
        drafted.Push.Should().Be(BackportService.Drafted);
        drafted.Conflicts.Should().Equal("README.md");
        var head = origin.Ref("refs/heads/backport/7-2.x")!;
        head.Should().Be(drafted.Commit);
        origin.InBare("rev-parse", head + "^").Trim().Should().Be(origin.Ref("refs/heads/release/2.x"));
        origin.InBare("cat-file", "blob", head + ":README.md").Should().Contain("<<<<<<<").And.Contain("two").And.Contain("fixed").And.Contain(">>>>>>>");
        origin.InBare("cat-file", "commit", head).Should().Contain($"(cherry picked from commit {squash})");
    }

    [Fact]
    public async Task A_configured_package_is_normalized_like_a_framework()
    {
        // #570 D8: backport.markers.packages, a first-party platform-anchored package.
        static string Package(string ng, string extra = "") => "{\n  \"dependencies\": {\n    \"@mintplayer/ng-bootstrap\": \"" + ng + "\",\n" + extra + "    \"rxjs\": \"~7.8.0\"\n  }\n}\n";
        const string Added = "    \"date-fns\": \"^4.1.0\",\n";
        var origin = Seed();
        origin.Checkout("main").Write("package.json", Package("^21.4.0"));
        origin.Commit("app");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Push("release/1.x");
        origin.Checkout("main").Write("package.json", Package("^22.34.0"));
        origin.Commit("feat: angular 22 (#6)");
        origin.Push("main");
        origin.Write("package.json", Package("^22.36.0", Added));
        var squash = origin.Commit("feat: date-fns (#7)");
        origin.Push("main");

        var plain = await Writer.BackportAsync(Request(origin, squash, ["feat"], "1.x"));
        plain.Targets.Single().Push.Should().Be(BackportService.Conflicted, "without the option the package is ordinary text");

        var request = Request(origin, squash, ["feat"], "1.x");
        request["markers"] = new { packages = new[] { "@mintplayer/*" } };
        var response = await Writer.BackportAsync(request);

        response.Targets.Single().Push.Should().Be(BackportService.Created, string.Join(", ", response.Targets.Single().Conflicts));
        origin.InBare("cat-file", "blob", origin.Ref("refs/heads/backport/7-1.x") + ":package.json").Should().Be(Package("^21.4.0", Added));
    }

    // --- M6: regenerated paths (D9: lockfiles are never merged; take the line's side, then regenerate) ---

    private const string Lock = "app/package-lock.json";
    private const string Lockfiles = "**/package-lock.json";

    /// <summary>main with a lockfile, <c>release/1.x</c> cut there, then the pull request: a lockfile bump and a fix.</summary>
    private (Origin Origin, string Squash) LockfileChange()
    {
        var origin = Seed();
        origin.Checkout("main").Write(Lock, "{ \"lockfileVersion\": 3, \"line\": true }\n");
        origin.Commit("lock");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Push("release/1.x");
        origin.Checkout("main").Write(Lock, "{ \"lockfileVersion\": 3, \"main\": true }\n").Write("fix.txt", "fixed\n");
        var squash = origin.Commit("fix: bump (#7)");
        origin.Push("main");
        return (origin, squash);
    }

    private static Dictionary<string, object?> RegeneratingRequest(Origin origin, string squash)
    {
        var request = Request(origin, squash, ["fix"], "1.x");
        request["targets"] = new[] { new { branch = "release/1.x", head = "backport/7-1.x", regeneratedPaths = new[] { Lockfiles } } };
        return request;
    }

    [Fact]
    public async Task A_regenerated_path_keeps_the_line_side_and_is_listed_for_regeneration()
    {
        var (origin, squash) = LockfileChange();

        var response = await Writer.BackportAsync(RegeneratingRequest(origin, squash));

        var target = response.Targets.Single();
        target.Push.Should().Be(BackportService.Created, string.Join(", ", target.Conflicts));
        target.Regenerate.Should().Equal(Lock);
        var head = origin.Ref("refs/heads/backport/7-1.x")!;
        origin.InBare("cat-file", "blob", head + ":" + Lock).Should().Be("{ \"lockfileVersion\": 3, \"line\": true }\n", "a lockfile is never merged");
        origin.InBare("cat-file", "blob", head + ":fix.txt").Should().Be("fixed\n");

        var plain = await Writer.BackportAsync(Request(origin, squash, ["fix"], "1.x"));
        plain.Targets.Single().Regenerate.Should().BeEmpty("without regenerated paths nothing is taken from the line");
    }

    private async Task<(Origin Origin, string Head, string Patch)> RegeneratedAsync(string path = Lock)
    {
        var (origin, squash) = LockfileChange();
        var head = (await Writer.BackportAsync(RegeneratingRequest(origin, squash))).Targets.Single().Commit!;
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", "backport/7-1.x");
        origin.Checkout("regen", head).Write(path, "{ \"lockfileVersion\": 3, \"regenerated\": true }\n");
        origin.Commit("regenerate");
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", head, "HEAD");
        origin.Checkout("main");
        return (origin, head, patch);
    }

    private static Dictionary<string, object?> PatchRequest(Origin origin, string expected, string patch) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["head"] = "backport/7-1.x",
        ["expectedSha"] = expected,
        ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)),
        ["regeneratedPaths"] = new[] { Lockfiles },
        ["committer"] = new { name = "coverage-bot", email = "bot@example.invalid" },
    };

    [Fact]
    public async Task The_regenerate_patch_is_folded_into_the_backport_commit()
    {
        var (origin, head, patch) = await RegeneratedAsync();

        var response = await Writer.BackportRegeneratedAsync(PatchRequest(origin, head, patch));

        response.Outcome.Should().Be("regenerated", response.Detail);
        response.Paths.Should().Equal(Lock);
        var commit = origin.Ref("refs/heads/backport/7-1.x")!;
        commit.Should().Be(response.Commit);
        origin.InBare("rev-parse", commit + "^").Trim().Should().Be(origin.Ref("refs/heads/release/1.x"), "one commit on the line, as before");
        origin.InBare("cat-file", "blob", commit + ":" + Lock).Should().Be("{ \"lockfileVersion\": 3, \"regenerated\": true }\n");
        origin.InBare("cat-file", "blob", commit + ":fix.txt").Should().Be("fixed\n");
        origin.InBare("log", "-1", "--format=%B", commit).Should().Be(origin.InBare("log", "-1", "--format=%B", head), "the trailer stays");
        origin.InBare("log", "-1", "--format=%an", commit).Should().Be(origin.InBare("log", "-1", "--format=%an", head));
    }

    [Fact]
    public async Task A_regenerate_patch_is_refused_outside_its_paths_and_on_a_moved_branch()
    {
        var (origin, head, patch) = await RegeneratedAsync("fix.txt");

        (await Writer.BackportRegeneratedAsync(PatchRequest(origin, head, patch))).Outcome.Should().Be("patch-rejected");
        // T4 comes first; an empty patch passes it, so this one reaches the head check.
        (await Writer.BackportRegeneratedAsync(PatchRequest(origin, new string('a', 40), ""))).Outcome.Should().Be("branch-moved");
        origin.Ref("refs/heads/backport/7-1.x").Should().Be(head);
    }

    [Fact]
    public async Task An_empty_patch_leaves_the_backport_as_it_is()
    {
        var (origin, head, _) = await RegeneratedAsync();

        var response = await Writer.BackportRegeneratedAsync(PatchRequest(origin, head, ""));

        response.Outcome.Should().Be("regenerated");
        response.Commit.Should().Be(head);
        response.Paths.Should().BeEmpty();
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

    [Fact]
    public async Task A_conflict_the_line_own_rules_allow_is_resolved_with_the_line_side()
    {
        // #570 M5, PRD §5.3 step 2: the merge queue's resolver with the line's autoResolve rules; stage 2 is the line.
        const string Hashes = "App/App_Data/modelHashes.json";
        var origin = Seed();
        origin.Checkout("main").Write(Hashes, "{\n  \"a\": \"seed\"\n}\n");
        origin.Commit("hashes");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Write(Hashes, "{\n  \"a\": \"line\"\n}\n");
        origin.Commit("1.x: regenerate");
        origin.Push("release/1.x");
        origin.Checkout("main").Write(Hashes, "{\n  \"a\": \"main\"\n}\n").Write("fix.txt", "fixed\n");
        var squash = origin.Commit("fix: thing (#7)");
        origin.Push("main");

        var request = Request(origin, squash, ["fix"], "1.x");
        var unruled = await Writer.BackportAsync(request);
        unruled.Targets.Single().Push.Should().Be(BackportService.Conflicted);
        unruled.Targets.Single().Conflicts.Should().Equal(Hashes);

        request["targets"] = new[] { new { branch = "release/1.x", head = "backport/7-1.x", autoResolve = new[] { new { path = "**/App_Data/modelHashes.json", anyLine = true } } } };
        var response = await Writer.BackportAsync(request);

        var target = response.Targets.Single();
        target.Push.Should().Be(BackportService.Created, target.Detail);
        target.Resolutions.Select(r => (r.Path, r.Strategy)).Should().Equal((Hashes, "take-base"));
        var head = origin.Ref("refs/heads/backport/7-1.x")!;
        origin.InBare("cat-file", "blob", head + ":" + Hashes).Should().Be("{\n  \"a\": \"line\"\n}\n");
        origin.InBare("cat-file", "blob", head + ":fix.txt").Should().Be("fixed\n");
    }

    [Theory]
    [InlineData("targets")]
    [InlineData("head")]
    [InlineData("source")]
    [InlineData("title")]
    [InlineData("rules")]
    [InlineData("disable")]
    [InlineData("packages")]
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
            case "rules": request["targets"] = new[] { new { branch = "release/1.x", head = "backport/7-1.x", autoResolve = new[] { new { path = "a.json", strategy = "guess" } } } }; break;
            case "disable": request["markers"] = new { disable = new[] { "docker" } }; break;
            case "packages": request["markers"] = new { packages = new[] { "*" } }; break;
        }

        var response = await Writer.PostBackportAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
