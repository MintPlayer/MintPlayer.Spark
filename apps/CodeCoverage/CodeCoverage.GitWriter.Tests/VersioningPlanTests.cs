using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #153 M11 (D3 trigger B): <c>/jobs/versioning/plan</c> answers whether a run on a commit would release anything,
/// without a build and without writing a tag. A commit that touches no package's inputs since its last tag plans
/// nothing; one that does names the package.
/// </summary>
public class VersioningPlanTests : WriterTestBase
{
    private async Task<T> PostAsync<T>(string path, Dictionary<string, object?> body, Origin origin)
    {
        body["provider"] = "github";
        body["repoId"] = "1001";
        body["cloneUrl"] = origin.Url.AbsoluteUri;
        body["credential"] = Credential(Token);
        body["branch"] = "main";
        body["defaultBranch"] = "main";
        var response = await Writer.Client.PostAsJsonAsync(path, body, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(GitWriterHost.Json))!;
    }

    private Task<VersioningPlanResponse> PlanAsync(Origin origin)
        => PostAsync<VersioningPlanResponse>("/jobs/versioning/plan", new() { ["commit"] = origin.Head() }, origin);

    [Fact]
    public async Task A_commit_that_changes_no_package_plans_nothing_and_one_that_does_names_it()
    {
        using var origin = new Origin(Server, "plan");
        origin.Write("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n");
        origin.Write("README.md", "fixture\n");
        origin.Commit("packages");
        origin.Push("main");

        var first = await PlanAsync(origin);
        first.Outcome.Should().Be(JobOutcomes.Planned, first.Detail);
        first.Releases.Should().Equal("Core");

        var run = await PostAsync<VersioningRunResponse>("/jobs/versioning/run", new()
        {
            ["tagger"] = new { name = "coverage-bot", email = "bot@example.invalid" },
            ["manifest"] = new ReleaseManifest
            {
                Commit = origin.Head(),
                Packages = [new ManifestPackage { Id = "Core", Kind = "nuget", ApiHash = "sha256:" + new string('1', 64), TargetFrameworks = ["net10.0"], Packed = true }],
            },
        }, origin);
        run.Outcome.Should().Be("versioned", run.Detail);

        origin.Write("README.md", "only the docs\n");
        origin.Commit("docs");
        origin.Push("main");
        var docs = await PlanAsync(origin);
        docs.Outcome.Should().Be(JobOutcomes.Planned, docs.Detail);
        docs.Releases.Should().BeEmpty();
        origin.InBare("for-each-ref", "refs/tags", "--format=%(refname)").Trim().Split('\n').Should().HaveCount(1, "a plan writes no tag");

        origin.Write("src/Core/Api.cs", "namespace Core; public class Api { }\n");
        origin.Commit("change the api");
        origin.Push("main");
        (await PlanAsync(origin)).Releases.Should().Equal("Core");
    }

    [Fact]
    public async Task With_notes_the_plan_composes_each_package_s_upcoming_notes_from_the_commits_since_its_tag()
    {
        // #693 Q12: the upcoming view. The run tags Core; the two commits after it are what its next release would list.
        using var origin = new Origin(Server, "plan-notes");
        origin.Write("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n");
        origin.Commit("feat: the first package (#1)");
        origin.Push("main");
        var run = await PostAsync<VersioningRunResponse>("/jobs/versioning/run", new()
        {
            ["tagger"] = new { name = "coverage-bot", email = "bot@example.invalid" },
            ["manifest"] = new ReleaseManifest
            {
                Commit = origin.Head(),
                Packages = [new ManifestPackage { Id = "Core", Kind = "nuget", ApiHash = "sha256:" + new string('1', 64), TargetFrameworks = ["net10.0"], Packed = true }],
            },
        }, origin);
        run.Outcome.Should().Be("versioned", run.Detail);

        origin.Write("src/Core/Api.cs", "namespace Core; public class Api { }\n");
        origin.Commit("fix: a crash on start (#2)");
        origin.Write("src/Core/Docs.cs", "namespace Core; public class Docs { }\n");
        origin.Commit("docs: the guide (#3)");
        origin.Push("main");

        var plain = await PlanAsync(origin);
        var noted = await PostAsync<VersioningPlanResponse>("/jobs/versioning/plan", new()
        {
            ["commit"] = origin.Head(),
            ["notes"] = true,
            ["settings"] = new VersioningSettingsWire { ChangelogInclude = ["docs"] },
        }, origin);

        plain.Upcoming.Should().BeNull("notes are composed only when asked for");
        noted.Outcome.Should().Be(JobOutcomes.Planned, noted.Detail);
        noted.Upcoming.Should().ContainSingle().Which.Should().Be(new UpcomingNotesWire("Core", "### Fixes\n- a crash on start (#2)\n\n### Changes\n- the guide (#3)"));
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("release/10.x")]   // a #153 release branch is no line once lines are on (#568 D1)
    public async Task With_lines_on_a_branch_that_is_neither_default_nor_a_line_is_refused(string branch)
    {
        using var origin = new Origin(Server, "plan-not-a-line");
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/plan", new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["repoId"] = "1001",
            ["cloneUrl"] = origin.Url.AbsoluteUri,
            ["credential"] = Credential(Token),
            ["branch"] = branch,
            ["defaultBranch"] = "main",
            ["commit"] = new string('a', 40),
            ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
        }, GitWriterHost.Json);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var refused = (await response.Content.ReadFromJsonAsync<RefusedResponse>(GitWriterHost.Json))!;
        refused.Field.Should().Be("branch");
    }
}
