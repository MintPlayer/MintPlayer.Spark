using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #570 M8b (PRD D5): <c>/jobs/versioning/ecosystems</c> names the ecosystems a pull request touches, by the engine's
/// changed-inputs test between the merge base and the pull request's head. The default labels and the "Backport
/// targets" check are built from it.
/// </summary>
public class VersioningEcosystemsTests : WriterTestBase
{
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n";

    private async Task<VersioningEcosystemsResponse> EcosystemsAsync(Origin origin, int number, string headSha)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/ecosystems", new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["repoId"] = "1001",
            ["cloneUrl"] = origin.Url.AbsoluteUri,
            ["credential"] = Credential(Token),
            ["baseBranch"] = "main",
            ["headRef"] = $"refs/pull/{number}/head",
            ["headSha"] = headSha,
            ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
        }, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VersioningEcosystemsResponse>(GitWriterHost.Json))!;
    }

    [Fact]
    public async Task A_pull_request_touches_the_ecosystems_of_the_packages_it_changes_since_the_merge_base()
    {
        using var origin = new Origin(Server, "ecosystems");
        origin.Write("src/Core/Core.csproj", Csproj);
        origin.Write("libs/ng-acme/package.json", """{ "name": "@acme/ng-acme", "peerDependencies": { "@angular/core": ">=21.0.0" } }""");
        origin.Write("libs/ng-acme/ng-package.json", """{ "dest": "../../dist/libs/ng-acme" }""");
        origin.Write("libs/ng-acme/index.ts", "export {};\n");
        origin.Write("README.md", "fixture\n");
        origin.Commit("packages");
        origin.Push("main");

        origin.Checkout("fix", "main");
        origin.Write("src/Core/Guard.cs", "namespace Core; public class Guard { }\n");
        origin.Write("README.md", "docs too\n");
        var fix = origin.Commit("fix: guard");
        origin.PullRequest(7, "fix");

        // The default branch moves on: its own change to the Angular library is no part of the pull request.
        origin.Checkout("main");
        origin.Write("libs/ng-acme/index.ts", "export const x = 1;\n");
        origin.Commit("feat: x");
        origin.Push("main");

        var touched = await EcosystemsAsync(origin, 7, fix);
        touched.Outcome.Should().Be(JobOutcomes.Planned, touched.Detail);
        touched.Ecosystems.Should().Equal(["dotnet"]);
    }

    [Fact]
    public async Task A_head_that_moved_is_not_answered_for()
    {
        using var origin = new Origin(Server, "ecosystems-moved");
        origin.Write("src/Core/Core.csproj", Csproj);
        origin.Commit("packages");
        origin.Push("main");
        origin.Checkout("fix", "main");
        origin.Write("src/Core/Guard.cs", "namespace Core; public class Guard { }\n");
        origin.Commit("fix: guard");
        origin.PullRequest(8, "fix");

        var stale = await EcosystemsAsync(origin, 8, new string('a', 40));
        stale.Outcome.Should().Be(JobOutcomes.HeadMismatch);
        stale.Ecosystems.Should().BeEmpty();
    }
}
