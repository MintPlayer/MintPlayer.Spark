using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// Changelog × release lines R1 (docs/coverage_changelog_release_lines_PRD.md, spike S1): with promote lines the default
/// branch releases nothing, so the upcoming view plans each current line <em>as if</em> <c>/release</c> promoted the
/// default branch's head now. The writer plans the line at a commit that lives on the default branch, not on the line.
/// </summary>
public class VersioningPlanPromotedTests : WriterTestBase
{
    private const string Line = "release/dotnet-10.x";
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n";

    private async Task<HttpResponseMessage> PlanAsync(Origin origin, string branch, string commit, bool promoted)
        => await Writer.Client.PostAsJsonAsync("/jobs/versioning/plan", new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["repoId"] = "1001",
            ["cloneUrl"] = origin.Url.AbsoluteUri,
            ["credential"] = Credential(Token),
            ["branch"] = branch,
            ["defaultBranch"] = "main",
            ["commit"] = commit,
            ["promoted"] = promoted,
            ["notes"] = true,
            ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
        }, GitWriterHost.Json);

    private static void PushTag(Origin origin, string tag, string sha)
    {
        FixtureGit.Run(origin.Work, "tag", tag, sha);
        FixtureGit.Run(origin.Work, "push", "--quiet", "origin", "refs/tags/" + tag);
        FixtureGit.Run(origin.Work, "tag", "--delete", tag);
    }

    /// <summary>What <c>PromoteService</c> writes: the default branch's tree on top of the line, master's sha as second parent.</summary>
    private static string Promote(Origin origin, string lineTip, string master)
    {
        var merge = FixtureGit.Run(origin.Work, "-c", "user.name=coverage-bot", "-c", "user.email=bot@example.invalid",
            "commit-tree", master + "^{tree}", "-p", lineTip, "-p", master, "-m", "Promote main into " + Line).Trim();
        FixtureGit.Run(origin.Work, "push", "--quiet", "--force", "origin", $"{merge}:refs/heads/{Line}");
        return merge;
    }

    /// <summary>
    /// main: #1 (released as 10.0.0 when the line was cut), #2 (promoted and released as 10.0.1), #3 (not promoted yet).
    /// </summary>
    private static (string First, string Head) Fixture(Origin origin)
    {
        origin.Write("src/Core/Core.csproj", Csproj);
        var first = origin.Commit("feat: the first package (#1)");
        origin.Push("main");
        FixtureGit.Run(origin.Work, "push", "--quiet", "origin", $"{first}:refs/heads/{Line}");
        PushTag(origin, "Core/v10.0.0", first);

        origin.Write("src/Core/Api.cs", "namespace Core; public class Api { }\n");
        var second = origin.Commit("fix: a crash on start (#2)");
        origin.Push("main");
        PushTag(origin, "Core/v10.0.1", Promote(origin, first, second));

        origin.Write("src/Core/Docs.cs", "namespace Core; public class Docs { }\n");
        var head = origin.Commit("fix: a leak on stop (#3)");
        origin.Push("main");
        return (first, head);
    }

    [Fact]
    public async Task A_line_planned_at_the_default_branch_s_head_lists_what_the_next_promotion_would_release()
    {
        using var origin = new Origin(Server, "plan-promoted");
        var (_, head) = Fixture(origin);

        var response = await PlanAsync(origin, Line, head, promoted: true);

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<VersioningPlanResponse>(GitWriterHost.Json))!;
        plan.Outcome.Should().Be(JobOutcomes.Planned, plan.Detail);
        plan.Releases.Should().Equal("Core");
        plan.Upcoming.Should().ContainSingle().Which.Should().Be(new UpcomingNotesWire("Core", "### Fixes\n- a leak on stop (#3)"),
            "#2 shipped with the last promotion; only #3 is pending");
        origin.InBare("for-each-ref", "refs/tags", "--format=%(refname)").Trim().Split('\n').Should().HaveCount(2, "a plan writes no tag");
        origin.Ref("refs/heads/" + Line).Should().NotBe(head, "a plan promotes nothing");
    }

    [Fact]
    public async Task Without_promoted_a_commit_off_the_line_is_still_refused()
    {
        using var origin = new Origin(Server, "plan-promoted-off-line");
        var (_, head) = Fixture(origin);

        var response = await PlanAsync(origin, Line, head, promoted: false);

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<VersioningPlanResponse>(GitWriterHost.Json))!;
        plan.Outcome.Should().NotBe(JobOutcomes.Planned, "a line's own plan still plans only the line's commits");
    }

    [Fact]
    public async Task Promoted_with_a_commit_that_is_not_on_the_default_branch_is_not_planned()
    {
        using var origin = new Origin(Server, "plan-promoted-off-default");
        var (first, _) = Fixture(origin);
        origin.Checkout("feature/x", first);
        origin.Write("src/Core/Feature.cs", "namespace Core; public class Feature { }\n");
        var stray = origin.Commit("feat: not merged (#4)");
        origin.Push("feature/x");

        var response = await PlanAsync(origin, Line, stray, promoted: true);

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<VersioningPlanResponse>(GitWriterHost.Json))!;
        plan.Outcome.Should().NotBe(JobOutcomes.Planned, "only the default branch's commits can be promoted");
    }

    [Fact]
    public async Task Promoted_on_the_default_branch_itself_is_refused()
    {
        using var origin = new Origin(Server, "plan-promoted-default");
        var response = await PlanAsync(origin, "main", new string('a', 40), promoted: true);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var refused = (await response.Content.ReadFromJsonAsync<RefusedResponse>(GitWriterHost.Json))!;
        refused.Field.Should().Be("promoted");
    }
}
