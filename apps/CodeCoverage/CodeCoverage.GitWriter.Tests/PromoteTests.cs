using System.Net;
using System.Net.Http.Json;
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

    /// <summary>main with a .NET package <c>Core</c> tagged 1.2.0, and <c>release/dotnet-1.x</c> cut from it; returns the next main commit.</summary>
    private string PackageLine(Origin origin)
    {
        origin.Write("src/Core/Core.csproj", Csproj);
        origin.Write("README.md", "fixture\n");
        var first = origin.Commit("packages");
        origin.Push("main");
        origin.InBare("tag", "Core/v1.2.0", first);
        origin.Checkout("release/dotnet-1.x", "main").Push("release/dotnet-1.x");
        origin.Checkout("main").Write("src/Core/Guard.cs", "namespace Core; public class Guard { }\n");
        var commit = origin.Commit("feat: guard (#9)");
        origin.Push("main");
        return commit;
    }

    private static Dictionary<string, object?> WithVersions(Dictionary<string, object?> request, params (string Id, string Version)[] versions)
    {
        request["lines"] = new[] { new { branch = "release/dotnet-1.x", ecosystem = "dotnet", major = 1, onlyIfChanged = true } };
        request["versions"] = versions.Select(v => new { packageId = v.Id, version = v.Version }).ToArray();
        return request;
    }

    [Fact]
    public async Task A_version_goes_on_the_promotion_commit_as_a_lightweight_tag_in_the_same_push()
    {
        using var origin = new Origin(Server, "promote-version");
        var commit = PackageLine(origin);

        var response = await Writer.PromoteAsync(WithVersions(Request(origin, commit, "dotnet-1.x"), ("Core", "1.4.0")));

        var line = response.Lines.Single();
        line.Outcome.Should().Be(PromoteService.Promoted, line.Detail ?? response.Detail);
        line.Tags.Should().Equal("Core/v1.4.0");
        origin.Ref("refs/tags/Core/v1.4.0").Should().Be(line.Commit);
        origin.InBare("cat-file", "-t", "refs/tags/Core/v1.4.0").Trim().Should().Be("commit", "a lightweight tag is a hand tag to the engine");
    }

    [Theory]
    [InlineData("2.0.0", "does not have release/dotnet-1.x's major 1")]
    [InlineData("1.2.0", "is not above Core/v1.2.0, the tag it follows")]
    public async Task A_version_the_line_cannot_release_refuses_the_line(string version, string reason)
    {
        using var origin = new Origin(Server, "promote-bad-version-" + version.Replace('.', '-'));
        var commit = PackageLine(origin);
        var tip = origin.Ref("refs/heads/release/dotnet-1.x");

        var response = await Writer.PromoteAsync(WithVersions(Request(origin, commit, "dotnet-1.x"), ("Core", version)));

        var line = response.Lines.Single();
        line.Outcome.Should().Be(PromoteService.VersionRefused);
        line.Detail.Should().Contain(reason);
        origin.Ref("refs/heads/release/dotnet-1.x").Should().Be(tip);
        origin.Ref($"refs/tags/Core/v{version}").Should().Be(version == "1.2.0" ? origin.Ref("refs/tags/Core/v1.2.0") : null);
    }

    [Fact]
    public async Task A_version_of_no_package_is_reported_and_the_line_still_moves()
    {
        using var origin = new Origin(Server, "promote-unknown-version");
        var commit = PackageLine(origin);

        var response = await Writer.PromoteAsync(WithVersions(Request(origin, commit, "dotnet-1.x"), ("Nope", "1.0.0")));

        response.Unplaced.Should().ContainSingle().Which.PackageId.Should().Be("Nope");
        response.Lines.Single().Outcome.Should().Be(PromoteService.Promoted, "Core changed since the cut");
    }

    [Fact]
    public async Task A_recorded_version_the_line_released_past_is_reported_and_does_not_hold_the_line()
    {
        using var origin = new Origin(Server, "promote-stale-recorded");
        var commit = PackageLine(origin);
        var request = WithVersions(Request(origin, commit, "dotnet-1.x"));
        request["versions"] = new[] { new { packageId = "Core", version = "1.1.0", recorded = true } };

        var response = await Writer.PromoteAsync(request);

        var line = response.Lines.Single();
        line.Outcome.Should().Be(PromoteService.Promoted, line.Detail ?? response.Detail);
        line.Tags.Should().BeEmpty();
        response.Unplaced.Should().ContainSingle().Which.Detail.Should().Contain("is not above Core/v1.2.0, the tag it follows");
        origin.Ref("refs/tags/Core/v1.1.0").Should().BeNull();
    }

    private static Dictionary<string, object?> HandTag(Origin origin, string tag, string tagObject, bool promotes = true, params int[] majors) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["tag"] = tag,
        ["tagObject"] = tagObject,
        ["lines"] = (majors.Length == 0 ? [1] : majors).Select(m => new { branch = $"release/dotnet-{m}.x", ecosystem = "dotnet", major = m }).ToArray(),
        ["promotes"] = promotes,
        ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
    };

    private async Task<HandTagResponse> HandTagAsync(object request)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/hand-tag", request, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HandTagResponse>(GitWriterHost.Json))!;
    }

    [Theory]
    [InlineData("Core/v1.3.0", PromoteService.HandTagRecorded, null)]
    [InlineData("Core/v1.1.0", PromoteService.HandTagRefused, "Core/v1.1.0 is not above Core/v1.2.0, the tag it follows")]
    [InlineData("Core/v3.0.0", PromoteService.HandTagRefused, "does not keep the major of Core/v1.2.0")]
    public async Task A_hand_tag_for_a_promoted_line_is_checked_and_always_deleted(string tag, string outcome, string? reason)
    {
        using var origin = new Origin(Server, "hand-" + tag.Replace('/', '-').Replace('.', '-'));
        var commit = PackageLine(origin);
        origin.InBare("tag", tag, commit);

        var response = await HandTagAsync(HandTag(origin, tag, commit));

        response.Outcome.Should().Be(JobOutcomes.Planned, response.Detail);
        response.Tag.Should().Be(outcome);
        response.Commit.Should().Be(commit);
        if (reason is not null) response.Detail.Should().Contain(reason);
        origin.Ref("refs/tags/" + tag).Should().BeNull();
        origin.Ref("refs/tags/Core/v1.2.0").Should().NotBeNull("only the pushed tag is deleted");
    }

    [Theory]
    [InlineData("Core/v1.3.0", PromoteService.HandTagAccepted, null)]
    [InlineData("Core/v1.0.5", PromoteService.HandTagRefused, "is not above Core/v1.2.0")]
    [InlineData("Core/v2.0.0", PromoteService.HandTagRefused, "does not keep the major of Core/v1.2.0")]
    public async Task Without_promotion_a_valid_hand_tag_stays_and_an_invalid_one_is_deleted(string tag, string outcome, string? reason)
    {
        // Segment T3: a backport repository's tags are checked against the tag they follow too.
        using var origin = new Origin(Server, "hand-backport-" + tag.Replace('/', '-').Replace('.', '-'));
        var commit = PackageLine(origin);
        origin.InBare("tag", tag, commit);

        var response = await HandTagAsync(HandTag(origin, tag, commit, promotes: false));

        response.Tag.Should().Be(outcome, response.Detail);
        if (reason is not null) response.Detail.Should().Contain(reason);
        origin.Ref("refs/tags/" + tag).Should().Be(outcome == PromoteService.HandTagAccepted ? commit : null);
    }

    [Fact]
    public async Task A_tag_for_the_promoted_line_follows_the_lines_tip_as_well()
    {
        // The next promotion commit has the line's tip as a parent, so a tag on the tip is the one this version follows.
        using var origin = new Origin(Server, "hand-follows-tip");
        var commit = PackageLine(origin);
        // A commit only the line has (a past promotion), so its tag is not in the main commit's history.
        origin.Checkout("release/dotnet-1.x").Write("src/Core/Line.cs", "namespace Core; public class Line { }\n");
        var lineTip = origin.Commit("line: promotion");
        origin.Push("release/dotnet-1.x");
        origin.Checkout("main");
        origin.InBare("tag", "Core/v1.4.0", lineTip);
        origin.InBare("tag", "Core/v1.3.0", commit);

        var promoted = await HandTagAsync(HandTag(origin, "Core/v1.3.0", commit));
        promoted.Tag.Should().Be(PromoteService.HandTagRefused);
        promoted.Detail.Should().Contain("is not above Core/v1.4.0");

        origin.InBare("tag", "Core/v1.3.0", commit);
        var backported = await HandTagAsync(HandTag(origin, "Core/v1.3.0", commit, promotes: false));
        backported.Tag.Should().Be(PromoteService.HandTagAccepted, "without promotion only the commit's own history counts");
    }

    [Fact]
    public async Task A_tag_for_a_past_line_of_a_promoted_repository_stays_in_place()
    {
        using var origin = new Origin(Server, "hand-past-line");
        var commit = PackageLine(origin);
        origin.Checkout("release/dotnet-2.x", "main").Push("release/dotnet-2.x");
        origin.InBare("tag", "Core/v1.3.0", commit);

        var response = await HandTagAsync(HandTag(origin, "Core/v1.3.0", commit, true, 1, 2));

        response.Tag.Should().Be(PromoteService.HandTagAccepted, response.Detail);
        response.Branch.Should().Be("release/dotnet-1.x");
        origin.Ref("refs/tags/Core/v1.3.0").Should().Be(commit);
    }

    [Fact]
    public async Task A_first_tag_needs_a_release_lines_major()
    {
        using var origin = new Origin(Server, "hand-first-tag");
        origin.Write("src/Core/Core.csproj", Csproj);
        var commit = origin.Commit("packages");
        origin.Push("main");
        origin.Checkout("release/dotnet-1.x", "main").Push("release/dotnet-1.x");
        origin.InBare("tag", "Core/v5.0.0", commit);

        var response = await HandTagAsync(HandTag(origin, "Core/v5.0.0", commit, promotes: false));

        response.Tag.Should().Be(PromoteService.HandTagRefused);
        response.Detail.Should().Contain("follows no tag of Core, and no release line has its major 5");
        origin.Ref("refs/tags/Core/v5.0.0").Should().BeNull();
    }

    [Fact]
    public async Task A_tag_of_no_package_or_one_that_moved_is_left_alone()
    {
        using var origin = new Origin(Server, "hand-left-alone");
        var commit = PackageLine(origin);
        origin.InBare("tag", "schemas/v3.0.0", commit);
        origin.InBare("tag", "Core/v1.5.0", commit);

        (await HandTagAsync(HandTag(origin, "schemas/v3.0.0", commit))).Tag.Should().Be(PromoteService.HandTagNotAPackage);
        (await HandTagAsync(HandTag(origin, "Core/v1.5.0", new string('1', 40)))).Tag.Should().Be(PromoteService.HandTagMoved);
        origin.Ref("refs/tags/schemas/v3.0.0").Should().Be(commit);
        origin.Ref("refs/tags/Core/v1.5.0").Should().Be(commit);
    }

    [Fact]
    public async Task A_commit_that_is_not_on_the_default_branch_is_refused()
    {
        var origin = Line();
        origin.Checkout("stray-work", "main").Write("x.txt", "x\n");
        var stray = origin.Commit("stray");
        origin.Push("stray-work");

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
