using System.Net;
using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Dependencies;

/// <summary>
/// <see cref="RepositoryManifestScanner"/> end to end through the real GitHub adapter: Octokit talks
/// to <see cref="StubGitHub"/>, which answers the branch, git-tree and contents endpoints with
/// GitHub's documented REST shapes, and the manifests it serves are real files from the fixtures.
/// </summary>
/// <remarks>
/// ⚠️ Each test uses its own repository id: the content cache is per instance, but the stub routes
/// are keyed by id and a shared id would let one test's routes answer another's.
/// </remarks>
public class RepositoryManifestScannerTests : CoverageRavenTest
{
    private const long InstallationId = 4242;
    private const string CommitSha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
    private const string TreeSha = "9fb037999f264ba9a7fc6274d15fa3ae2ab98312";

    private static string BranchJson(long repositoryId, string branch = "main") => $$"""
        {
          "name": "{{branch}}",
          "commit": {
            "sha": "{{CommitSha}}",
            "node_id": "C_kwDOAbCdEf",
            "commit": {
              "author": { "name": "Example Author", "email": "author@example.invalid", "date": "2026-09-01T12:00:00Z" },
              "committer": { "name": "Example Author", "email": "author@example.invalid", "date": "2026-09-01T12:00:00Z" },
              "message": "Change",
              "tree": { "sha": "{{TreeSha}}", "url": "https://api.github.com/repos/acme/widget/git/trees/{{TreeSha}}" },
              "url": "https://api.github.com/repos/acme/widget/git/commits/{{CommitSha}}",
              "comment_count": 0,
              "verification": { "verified": false, "reason": "unsigned", "signature": null, "payload": null }
            },
            "url": "https://api.github.com/repos/acme/widget/commits/{{CommitSha}}",
            "html_url": "https://github.com/acme/widget/commit/{{CommitSha}}",
            "comments_url": "https://api.github.com/repos/acme/widget/commits/{{CommitSha}}/comments",
            "author": null,
            "committer": null,
            "parents": []
          },
          "_links": {
            "self": "https://api.github.com/repositories/{{repositoryId}}/branches/{{branch}}",
            "html": "https://github.com/acme/widget/tree/{{branch}}"
          },
          "protected": false,
          "protection": { "enabled": false, "required_status_checks": { "enforcement_level": "off", "contexts": [], "checks": [] } },
          "protection_url": "https://api.github.com/repositories/{{repositoryId}}/branches/{{branch}}/protection"
        }
        """;

    private sealed record Item(string Path, string Type = "blob", long Size = 512);

    private static string TreeJson(IEnumerable<Item> items, bool truncated = false, string treeSha = TreeSha) => $$"""
        {
          "sha": "{{treeSha}}",
          "url": "https://api.github.com/repos/acme/widget/git/trees/{{treeSha}}",
          "tree": [
            {{string.Join(",\n", items.Select(i => i.Type == "blob"
                ? $$"""{ "path": "{{i.Path}}", "mode": "100644", "type": "blob", "sha": "3d21ec53a331a6f037a91c368710b99387d012c1", "size": {{i.Size}}, "url": "https://api.github.com/repos/acme/widget/git/blobs/3d21ec53a331a6f037a91c368710b99387d012c1" }"""
                : $$"""{ "path": "{{i.Path}}", "mode": "040000", "type": "tree", "sha": "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "url": "https://api.github.com/repos/acme/widget/git/trees/4b825dc642cb6eb9a060e54bf8d69288fbee4904" }"""))}}
          ],
          "truncated": {{(truncated ? "true" : "false")}}
        }
        """;

    private static string ContentJson(string path, string text) => $$"""
        {
          "type": "file",
          "encoding": "base64",
          "size": {{text.Length}},
          "name": "{{path.Split('/')[^1]}}",
          "path": "{{path}}",
          "content": "{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}}",
          "sha": "3d21ec53a331a6f037a91c368710b99387d012c1",
          "url": "https://api.github.com/repos/acme/widget/contents/{{path}}",
          "git_url": "https://api.github.com/repos/acme/widget/git/blobs/3d21ec53a331a6f037a91c368710b99387d012c1",
          "html_url": "https://github.com/acme/widget/blob/main/{{path}}",
          "download_url": "https://raw.githubusercontent.com/acme/widget/main/{{path}}",
          "_links": { "self": "https://api.github.com/repos/acme/widget/contents/{{path}}", "git": "https://api.github.com/repos/acme/widget/git/blobs/3d21ec53a331a6f037a91c368710b99387d012c1", "html": "https://github.com/acme/widget/blob/main/{{path}}" }
        }
        """;

    private static StubGitHub Stub(long repositoryId, IEnumerable<Item> items, bool truncated = false)
        => new StubGitHub()
            .On(HttpMethod.Get, $"/repositories/{repositoryId}/branches/main", HttpStatusCode.OK, BranchJson(repositoryId))
            .On(HttpMethod.Get, $"/repositories/{repositoryId}/git/trees/{CommitSha}", HttpStatusCode.OK, TreeJson(items, truncated));

    private static StubGitHub Serve(StubGitHub github, long repositoryId, string path, string content)
        => github.On(HttpMethod.Get, $"/repositories/{repositoryId}/contents/{path}", HttpStatusCode.OK, ContentJson(path, content));

    private static async Task SeedAsync(IDocumentStore store, long repositoryId, long? installationId = InstallationId,
        string? defaultBranch = "main", bool archived = false, bool isPrivate = true)
    {
        using var seed = store.OpenAsyncSession();
        var accountId = Account.DocumentId(EForgeProvider.GitHub, 77);
        await seed.StoreAsync(new Account { GitHubId = 77, Login = "acme", InstallationId = installationId }, accountId);
        await seed.StoreAsync(new Repository
        {
            GitHubId = repositoryId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", IsPrivate = isPrivate,
            DefaultBranch = defaultBranch, Archived = archived, Account = accountId,
        }, Repository.DocumentId(EForgeProvider.GitHub, repositoryId));
        await seed.SaveChangesAsync();
    }

    private static RepositoryManifestScanner Scanner(IAsyncDocumentSession session, StubGitHub github)
    {
        var client = new GitHubForgeClient(
            new GitHubDiffService(github, new SingleClientHttpFactory(github.Rest), NullLogger<GitHubDiffService>.Instance),
            new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance),
            session, github, NullLogger<GitHubForgeClient>.Instance);
        var forge = new GitHubForgeIntegration(
            Substitute.For<IForgeAccessService>(), client, Substitute.For<IForgeFeedbackPublisher>(), Substitute.For<IGitHubStateReconciler>());
        return new RepositoryManifestScanner(session, new SingleForgeResolver(forge), NullLogger<RepositoryManifestScanner>.Instance);
    }

    private static async Task<RepositoryManifest?> LoadManifestAsync(IDocumentStore store, long repositoryId)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<RepositoryManifest>(RepositoryManifest.DocumentId(EForgeProvider.GitHub, repositoryId));
    }

    private static async Task<EManifestScanOutcome> ScanAsync(IDocumentStore store, StubGitHub github, long repositoryId)
    {
        using var session = store.OpenAsyncSession();
        return await Scanner(session, github).ScanAsync(Repository.DocumentId(EForgeProvider.GitHub, repositoryId));
    }

    private static int ContentCalls(StubGitHub github) => github.Calls.Count(c => c.Contains("/contents/"));

    [Fact]
    public async Task A_scan_reads_only_manifests_and_records_what_the_repository_produces_and_consumes()
    {
        const long repositoryId = 930001;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var github = Stub(repositoryId,
        [
            new("libs", Type: "tree"),
            new("libs/node_packages/ng-spark/package.json"),
            new("libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj"),
            new(".github/workflows/pull-request.yml"),
            new("apps/CodeCoverage/docker-compose.yml"),
            new("node_modules/@angular/core/package.json"),
            new("src/huge/package.json", Size: RepositoryManifestScanner.MaxFileBytes + 1),
            new("README.md"),
        ]);
        Serve(github, repositoryId, "libs/node_packages/ng-spark/package.json", Fixture.Read("Manifests/ng-spark.package.json.txt"));
        Serve(github, repositoryId, "libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", Fixture.Read("Manifests/MintPlayer.Spark.csproj.txt"));
        Serve(github, repositoryId, ".github/workflows/pull-request.yml", Fixture.Read("Manifests/pull-request.workflow.yml.txt"));
        Serve(github, repositoryId, "apps/CodeCoverage/docker-compose.yml", Fixture.Read("Manifests/codecoverage.docker-compose.yml.txt"));

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Scanned, string.Join(", ", github.Calls));

        var manifest = await LoadManifestAsync(store, repositoryId);
        manifest.Should().NotBeNull();
        manifest!.Repository.Should().Be(Repository.DocumentId(EForgeProvider.GitHub, repositoryId));
        manifest.OwnerKey.Should().Be("github:acme");
        manifest.TreeSha.Should().Be(TreeSha);
        manifest.ScanError.Should().BeNull();
        manifest.ScannedAt.HasValue.Should().BeTrue();
        manifest.Truncated.Should().BeFalse();
        manifest.Produces.Select(p => $"{p.Ecosystem}:{p.Name}").OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal("npm:@mintplayer/ng-spark", "nuget:MintPlayer.Spark");
        manifest.Consumes.Any(d => d.Ecosystem == "actions" && d.Name == "actions/checkout").Should().BeTrue();
        manifest.Consumes.Any(d => d.Ecosystem == "docker" && d.Name == "ghcr.io/mintplayer/codecoverage").Should().BeTrue();
        manifest.Consumes.Any(d => d.Ecosystem == "nuget" && d.Name == "RavenDB.Client").Should().BeTrue();

        // Four manifests fetched, at the listed commit; vendored, oversized and non-manifest files never.
        ContentCalls(github).Should().Be(4);
        github.Calls.Where(c => c.Contains("/contents/")).Should().OnlyContain(c => c.Contains($"ref={CommitSha}"));
        github.Calls.Any(c => c.Contains("node_modules") || c.Contains("huge") || c.Contains("README")).Should().BeFalse();
        github.InstallationClients.Should().OnlyContain(id => id == InstallationId);
    }

    [Fact]
    public async Task An_unchanged_tree_is_not_read_again()
    {
        const long repositoryId = 930002;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var github = Stub(repositoryId, [new("package.json")]);
        Serve(github, repositoryId, "package.json", Fixture.Read("Manifests/ng-spark.package.json.txt"));

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Scanned);
        var first = await LoadManifestAsync(store, repositoryId);
        var callsAfterFirst = ContentCalls(github);

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Unchanged);

        ContentCalls(github).Should().Be(callsAfterFirst, "an unchanged tree stops after the listing");
        (await LoadManifestAsync(store, repositoryId))!.ScannedAt!.Value.Should().Be(first!.ScannedAt!.Value, "nothing is written either");
    }

    [Fact]
    public async Task A_truncated_listing_is_recorded_as_truncated()
    {
        const long repositoryId = 930003;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var github = Stub(repositoryId, [new("package.json")], truncated: true);
        Serve(github, repositoryId, "package.json", Fixture.Read("Manifests/ng-spark.package.json.txt"));

        await ScanAsync(store, github, repositoryId);

        var manifest = await LoadManifestAsync(store, repositoryId);
        manifest!.Truncated.Should().BeTrue();
        manifest.Produces.Should().ContainSingle();
    }

    [Fact]
    public async Task More_manifests_than_the_cap_are_read_up_to_the_cap_and_marked_truncated()
    {
        const long repositoryId = 930004;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var paths = Enumerable.Range(0, RepositoryManifestScanner.MaxManifestFiles + 1).Select(i => $"pkgs/p{i:D3}/package.json").ToList();
        var github = Stub(repositoryId, paths.Select(p => new Item(p)));
        foreach (var path in paths)
            Serve(github, repositoryId, path, $$"""{ "name": "@acme/{{path.Split('/')[1]}}" }""");

        await ScanAsync(store, github, repositoryId);

        var manifest = await LoadManifestAsync(store, repositoryId);
        manifest!.Truncated.Should().BeTrue();
        manifest.Produces.Count.Should().Be(RepositoryManifestScanner.MaxManifestFiles);
        ContentCalls(github).Should().Be(RepositoryManifestScanner.MaxManifestFiles);
        manifest.ScanError.Should().BeNull();
    }

    /// <summary>A file that cannot be read keeps what was read, records why, and leaves the tree to be retried.</summary>
    [Fact]
    public async Task An_unreadable_manifest_is_an_error_and_the_tree_is_not_remembered()
    {
        const long repositoryId = 930005;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var github = Stub(repositoryId, [new("a/package.json"), new("b/package.json")]);
        Serve(github, repositoryId, "a/package.json", """{ "name": "@acme/a" }""");
        // b/package.json is not served: GitHub's 404, and the repository is private so there is no raw fallback.

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Failed);

        var manifest = await LoadManifestAsync(store, repositoryId);
        manifest!.ScanError.Should().Be("1 of 2 manifest files could not be read.");
        manifest.TreeSha.Should().BeNull();
        manifest.Produces.Should().ContainSingle().Which.Name.Should().Be("@acme/a");
    }

    [Fact]
    public async Task Without_an_installation_the_failure_is_recorded_not_thrown()
    {
        const long repositoryId = 930006;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId, installationId: null);
        var github = Stub(repositoryId, [new("package.json")]);

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Failed);

        var manifest = await LoadManifestAsync(store, repositoryId);
        manifest!.ScanError.Should().NotBeNull();
        manifest.ScannedAt.HasValue.Should().BeTrue();
        github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_branch_github_cannot_find_is_a_recorded_failure()
    {
        const long repositoryId = 930007;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);

        (await ScanAsync(store, new StubGitHub(), repositoryId)).Should().Be(EManifestScanOutcome.Failed);

        (await LoadManifestAsync(store, repositoryId))!.ScanError.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, "main")]
    [InlineData(false, null)]
    public async Task Archived_repositories_and_ones_without_a_default_branch_are_skipped(bool archived, string? defaultBranch)
    {
        const long repositoryId = 930008;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId, archived: archived, defaultBranch: defaultBranch);
        var github = Stub(repositoryId, [new("package.json")]);

        (await ScanAsync(store, github, repositoryId)).Should().Be(EManifestScanOutcome.Skipped);

        (await LoadManifestAsync(store, repositoryId)).Should().BeNull();
        github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_repository_is_skipped()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        (await Scanner(session, new StubGitHub()).ScanAsync("Repositories/github/1")).Should().Be(EManifestScanOutcome.Skipped);
    }

    /// <summary>The adapter on its own: blobs only, with sizes, and the commit the branch resolved to.</summary>
    [Fact]
    public async Task The_github_tree_lists_blobs_with_sizes_at_the_branch_head()
    {
        const long repositoryId = 930009;
        using var store = GetDocumentStore();
        await SeedAsync(store, repositoryId);
        var github = Stub(repositoryId, [new("src", Type: "tree"), new("src/a.ts", Size: 42)], truncated: true);

        using var session = store.OpenAsyncSession();
        var client = new GitHubForgeClient(
            new GitHubDiffService(github, new SingleClientHttpFactory(github.Rest), NullLogger<GitHubDiffService>.Instance),
            new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance),
            session, github, NullLogger<GitHubForgeClient>.Instance);
        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, repositoryId));

        var tree = await client.GetTreeAsync(repository, "main");

        tree.Should().NotBeNull(string.Join(", ", github.Calls));
        tree!.CommitSha.Should().Be(CommitSha);
        tree.TreeSha.Should().Be(TreeSha);
        tree.Truncated.Should().BeTrue();
        var entry = tree.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be("src/a.ts");
        entry.Size.Should().Be(42L);
        github.Calls.Should().Contain($"GET /repositories/{repositoryId}/git/trees/{CommitSha}?recursive=1");
    }
}
