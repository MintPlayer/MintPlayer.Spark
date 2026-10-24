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
}
