using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #153 M7 (D2): one engine, two hosts. The same repository and the same manifests, run through the server host's
/// <c>/jobs/versioning/run</c> and through the local host's composition (the CLI's <c>run</c>: <see cref="ReleaseRun"/>
/// on the checkout, pushing with the checkout's own remote), give the same tags on the same commits with the same
/// annotations. Two origins are seeded identically; the fixture pins every commit's author and date, so their commit ids
/// are equal too.
/// </summary>
public class VersioningConformanceTests : WriterTestBase
{
    private const string CoreHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ChangedCoreHash = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string UtilHash = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private static Origin Fixture(Origin origin)
    {
        origin.Write("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n");
        origin.Write("package.json", "{ \"private\": true, \"workspaces\": [\"packages/*\"] }\n");
        origin.Write("packages/util/package.json", "{ \"name\": \"@acme/util\", \"version\": \"0.0.0\" }\n");
        origin.Write("README.md", "fixture\n");
        origin.Commit("packages");
        return origin.Push("main");
    }

    private static Origin Change(Origin origin)
    {
        origin.Write("src/Core/Api.cs", "namespace Core; public class Api { public void Changed() { } }\n");
        origin.Commit("change the api");
        return origin.Push("main");
    }

    private static ReleaseManifest Manifest(string commit, string coreHash) => new()
    {
        Commit = commit,
        Packages =
        [
            new ManifestPackage { Id = "Core", Kind = "nuget", ApiHash = coreHash, TargetFrameworks = ["net10.0"], Packed = true },
            new ManifestPackage { Id = "@acme/util", Kind = "npm", ApiHash = UtilHash, Packed = true },
        ],
    };

    private async Task<VersioningRunResponse> ServerAsync(Origin origin, ReleaseManifest manifest)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/run", new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["repoId"] = "1001",
            ["cloneUrl"] = origin.Url.AbsoluteUri,
            ["credential"] = Credential(Token),
            ["branch"] = "main",
            ["defaultBranch"] = "main",
            ["tagger"] = new { name = "coverage-bot", email = "bot@example.invalid" },
            ["manifest"] = manifest,
        }, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VersioningRunResponse>(GitWriterHost.Json))!;
    }

    /// <summary>What the CLI's <c>run</c> command composes, on the origin's own work checkout.</summary>
    private static async Task<ReleaseRunResult> LocalAsync(Origin origin, ReleaseManifest manifest)
    {
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "--tags", "--force", "origin");
        var git = new GitCli(Path.Combine(origin.Work, ".git"), TimeSpan.FromMinutes(5));
        var writer = new TagWriter(new CliTagGit(git, token: null), "origin", ["refs/remotes/origin/main"], new Tagger("github-actions[bot]", "bot@example.invalid"));
        return await ReleaseRun.RunAsync(git, writer, new ReleaseRunRequest(manifest, new VersioningConfig().ToSettings(), OnDefaultBranch: true, BranchMajor: null));
    }

    /// <summary>Every tag of the served repository: its name, its commit and its message. The tagger differs by host.</summary>
    private static string Tags(Origin origin)
        => origin.InBare("for-each-ref", "refs/tags", "--format=%(refname) %(*objectname)%0a%(contents)");

    [Fact]
    public async Task Both_hosts_tag_the_same_versions_on_the_same_commits()
    {
        using var serverOrigin = new Origin(Server, "server");
        using var localOrigin = new Origin(Server, "local");
        var server = Fixture(serverOrigin);
        var local = Fixture(localOrigin);
        server.Head().Should().Be(local.Head(), "the fixture pins every commit, so both origins hold the same history");

        var serverFirst = await ServerAsync(server, Manifest(server.Head(), CoreHash));
        var localFirst = await LocalAsync(local, Manifest(local.Head(), CoreHash));

        serverFirst.Outcome.Should().Be("versioned", serverFirst.Detail);
        localFirst.Succeeded.Should().BeTrue();
        serverFirst.Releases.Select(r => r.Tag).Should().BeEquivalentTo(localFirst.Plan.Releases.Select(r => r.Tag.Name));
        Tags(server).Should().NotBeEmpty().And.Be(Tags(local));

        var serverSecond = await ServerAsync(Change(server), Manifest(server.Head(), ChangedCoreHash));
        var localSecond = await LocalAsync(Change(local), Manifest(local.Head(), ChangedCoreHash));

        serverSecond.Outcome.Should().Be("versioned", serverSecond.Detail);
        localSecond.Succeeded.Should().BeTrue();
        serverSecond.Releases.Select(r => r.Tag).Should().BeEquivalentTo(localSecond.Plan.Releases.Select(r => r.Tag.Name)).And.Contain(t => t.StartsWith("Core/v"));
        Tags(server).Should().Be(Tags(local));
    }
}
