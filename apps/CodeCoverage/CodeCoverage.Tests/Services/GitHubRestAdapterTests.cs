using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The GitHub REST adapters, driven through real Octokit clients over <see cref="StubGitHub"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every adapter here was previously only ever replaced by a fake of itself, so none of Octokit's
/// deserialization, its status-to-exception mapping, or the adapters' own error arms had run. The
/// bodies below follow GitHub's documented REST shapes (field names and nesting from
/// docs.github.com/rest), trimmed to what the adapters read plus enough of the rest to stay
/// recognisable.
/// </para>
/// <para>
/// ⚠️ <see cref="GitHubDiffService"/> caches comparisons and parents in a <b>static</b> cache keyed
/// by repository id and refs, so every test that reaches it uses its own repository id.
/// </para>
/// </remarks>
public class GitHubRestAdapterTests : CoverageRavenTest
{
    private const string Head = "4a5b6c7d8e9f00112233445566778899aabbccdd";
    private const string Base = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";

    private static Repository Repo(long gitHubId, bool isPrivate = false, string? account = null) => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, gitHubId),
        GitHubId = gitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", IsPrivate = isPrivate, Account = account,
    };

    private const string ComparePatch = "@@ -1,2 +1,4 @@\n line one\n+added two\n+added three\n line four";

    private static string CompareJson(int fileCount = 1) => $$"""
        {
          "url": "https://api.github.com/repos/acme/widget/compare/{{Base}}...{{Head}}",
          "html_url": "https://github.com/acme/widget/compare/{{Base}}...{{Head}}",
          "permalink_url": "https://github.com/acme/widget/compare/acme:{{Base}}...acme:{{Head}}",
          "diff_url": "https://github.com/acme/widget/compare/{{Base}}...{{Head}}.diff",
          "patch_url": "https://github.com/acme/widget/compare/{{Base}}...{{Head}}.patch",
          "base_commit": { "sha": "{{Base}}", "url": "https://api.github.com/repos/acme/widget/commits/{{Base}}" },
          "merge_base_commit": { "sha": "{{Base}}", "url": "https://api.github.com/repos/acme/widget/commits/{{Base}}" },
          "status": "ahead",
          "ahead_by": 1,
          "behind_by": 0,
          "total_commits": 1,
          "commits": [],
          "files": [
            {{string.Join(",", Enumerable.Range(0, fileCount).Select(i => $$"""
            {
              "sha": "bbcd538c8e72b8c175046e27cc8f907076331401",
              "filename": "src/file{{i}}.ts",
              "status": "modified",
              "additions": 2,
              "deletions": 0,
              "changes": 2,
              "blob_url": "https://github.com/acme/widget/blob/{{Head}}/src/file{{i}}.ts",
              "raw_url": "https://github.com/acme/widget/raw/{{Head}}/src/file{{i}}.ts",
              "contents_url": "https://api.github.com/repos/acme/widget/contents/src/file{{i}}.ts?ref={{Head}}",
              "patch": "@@ -1,2 +1,4 @@\n line one\n+added two\n+added three\n line four"
            }
            """))}}
          ]
        }
        """;

    private static string CommitJson(string sha, params string[] parents) => $$"""
        {
          "sha": "{{sha}}",
          "node_id": "C_kwDOAbCdEf",
          "url": "https://api.github.com/repos/acme/widget/commits/{{sha}}",
          "html_url": "https://github.com/acme/widget/commit/{{sha}}",
          "commit": {
            "message": "Change",
            "author": { "name": "Example Author", "email": "author@example.invalid", "date": "2026-09-01T12:00:00Z" },
            "committer": { "name": "Example Author", "email": "author@example.invalid", "date": "2026-09-01T12:00:00Z" },
            "tree": { "sha": "1111111111111111111111111111111111111111", "url": "https://api.github.com/repos/acme/widget/git/trees/1111111111111111111111111111111111111111" }
          },
          "parents": [{{string.Join(",", parents.Select(p => $$"""{ "sha": "{{p}}", "url": "https://api.github.com/repos/acme/widget/commits/{{p}}", "html_url": "https://github.com/acme/widget/commit/{{p}}" }"""))}}],
          "files": []
        }
        """;

    private static GitHubDiffService Diff(StubGitHub github)
        => new(github, new SingleClientHttpFactory(github.Rest), NullLogger<GitHubDiffService>.Instance);

    // ------------------------------------------------------------------------------------------
    // GitHubDiffService
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_installation_compare_maps_files_merge_base_and_added_lines_and_is_cached()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", HttpStatusCode.OK, CompareJson());
        var repository = Repo(910001, isPrivate: true);

        var comparison = await Diff(github).CompareAsync(repository, installationId: 5, Base, Head);

        comparison.Should().NotBeNull(string.Join(", ", github.Calls));
        comparison!.MergeBaseSha.Should().Be(Base);
        var file = comparison.Files.Should().ContainSingle().Which;
        file.Path.Should().Be("src/file0.ts");
        file.Status.Should().Be("modified");
        file.AddedLines.Should().Equal(2, 3);
        comparison.Truncated.Should().BeFalse();
        github.InstallationClients.Should().Equal(5L);

        // The second ask is served from the process-wide cache — no second request.
        await Diff(github).CompareAsync(repository, installationId: 5, Base, Head);
        github.Calls.Count.Should().Be(1);
    }

    /// <summary>GitHub caps a compare at 300 files; reaching the cap is reported, never silently read as the whole diff.</summary>
    [Fact]
    public async Task A_compare_at_GitHubs_file_cap_is_reported_truncated()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", HttpStatusCode.OK, CompareJson(300));

        var comparison = await Diff(github).CompareAsync(Repo(910002, isPrivate: true), 5, Base, Head);

        comparison!.Files.Count.Should().Be(300);
        comparison.Truncated.Should().BeTrue();
    }

    /// <summary>
    /// A public repository falls back to the anonymous API when the installation cannot answer; a
    /// private one does not, because an anonymous request can never see it.
    /// </summary>
    [Fact]
    public async Task A_failed_installation_compare_falls_back_to_the_anonymous_API_for_a_public_repository()
    {
        var github = new StubGitHub { Throws = new Octokit.AuthorizationException() }
            .On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", HttpStatusCode.OK, CompareJson());

        var comparison = await Diff(github).CompareAsync(Repo(910003), installationId: 5, Base, Head);

        comparison!.Files.Single().AddedLines.Should().Equal(2, 3);
        comparison.Files.Single().PreviousPath.Should().BeNull();
        github.Rest.Requests.Single().AuthorizationParameter.Should().BeNull("the fallback is anonymous");
    }

    [Fact]
    public async Task A_private_repository_without_an_installation_answer_gets_no_comparison()
    {
        var github = new StubGitHub { Throws = new InvalidOperationException("no credential") }
            .On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", HttpStatusCode.OK, CompareJson());

        (await Diff(github).CompareAsync(Repo(910004, isPrivate: true), installationId: 5, Base, Head)).Should().BeNull();
        github.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, StubGitHub.NotFoundBody)]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    public async Task An_anonymous_compare_that_fails_yields_no_comparison(HttpStatusCode status, string body)
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", status, body);

        (await Diff(github).CompareAsync(Repo(910005 + (int)status), installationId: null, Base, Head)).Should().BeNull();
    }

    [Fact]
    public async Task The_first_parent_comes_from_the_installation_and_is_cached()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/commits/{Head}", HttpStatusCode.OK, CommitJson(Head, Base, "ffffffffffffffffffffffffffffffffffffffff"));
        var repository = Repo(910010, isPrivate: true);

        (await Diff(github).GetFirstParentAsync(repository, 5, Head)).Should().Be(Base);
        (await Diff(github).GetFirstParentAsync(repository, 5, Head)).Should().Be(Base);
        github.Calls.Count.Should().Be(1);
    }

    [Fact]
    public async Task The_first_parent_of_a_public_repository_falls_back_to_the_anonymous_API()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/commits/{Head}", HttpStatusCode.OK, CommitJson(Head, Base));

        (await Diff(github).GetFirstParentAsync(Repo(910011), installationId: null, Head)).Should().Be(Base);
    }

    /// <summary>A root commit has no parent; that is not cached, and neither is a failed lookup.</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, true)]      // a root commit: parents []
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.OK, false)]     // unparseable
    public async Task An_anonymous_parent_lookup_that_finds_nothing_yields_null(HttpStatusCode status, bool rootCommit)
    {
        var body = status == HttpStatusCode.NotFound ? StubGitHub.NotFoundBody : rootCommit ? CommitJson(Head) : "<html>";
        var github = new StubGitHub().On(HttpMethod.Get, $"/repos/acme/widget/commits/{Head}", status, body);

        (await Diff(github).GetFirstParentAsync(Repo(910020 + (int)status + (rootCommit ? 1 : 0)), installationId: null, Head)).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------
    // GitHubContentService
    // ------------------------------------------------------------------------------------------

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

    [Fact]
    public async Task File_content_comes_from_the_installation_and_is_cached()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/repositories/910030/contents/src/a.ts", HttpStatusCode.OK, ContentJson("src/a.ts", "export const a = 1;\n"));
        var cache = new SourceContentCache();
        var service = new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), cache, NullLogger<GitHubContentService>.Instance);

        (await service.GetFileContentAsync(Repo(910030, isPrivate: true), 5, Head, "src/a.ts")).Should().Be("export const a = 1;\n", string.Join(", ", github.Calls));
        (await service.GetFileContentAsync(Repo(910030, isPrivate: true), 5, Head, "src/a.ts")).Should().Be("export const a = 1;\n");
        github.Calls.Count.Should().Be(1);
    }

    [Fact]
    public async Task File_content_of_a_public_repository_falls_back_to_raw_githubusercontent()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"/acme/widget/{Head}/src/dir%20with%20space/a.ts",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("raw text") });
        var service = new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance);

        (await service.GetFileContentAsync(Repo(910031), installationId: null, Head, "src/dir with space/a.ts"))
            .Should().Be("raw text", string.Join(", ", github.Calls));
    }

    [Fact]
    public async Task File_content_that_cannot_be_found_is_null_and_not_cached()
    {
        var github = new StubGitHub();
        var service = new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance);

        (await service.GetFileContentAsync(Repo(910032), 5, Head, "missing.ts")).Should().BeNull();
        (await service.GetFileContentAsync(Repo(910032, isPrivate: true), null, Head, "missing.ts")).Should().BeNull();
    }

    /// <summary>The cache skips an entry too large to be worth holding, rather than evicting everything else for it.</summary>
    [Fact]
    public void An_oversized_source_is_not_cached()
    {
        ISourceContentCache cache = new SourceContentCache();

        cache.Set("small", "x", TimeSpan.FromMinutes(1));
        cache.Set("huge", new string('x', 2 * 1024 * 1024 + 1), TimeSpan.FromMinutes(1));

        cache.TryGet("small", out var small).Should().BeTrue();
        small.Should().Be("x");
        cache.TryGet("huge", out _).Should().BeFalse();
        ((IDisposable)cache).Dispose();
    }

    /// <summary>
    /// Concurrent requests for the same uncached file share ONE fetch (an anonymous browse could
    /// otherwise make the app fetch a file from GitHub once per parallel request), and a caller that
    /// gives up does not fail the others.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_for_one_uncached_key_share_a_single_fetch()
    {
        ISourceContentCache cache = new SourceContentCache();
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        Task<string?> Fetch() { Interlocked.Increment(ref fetches); return release.Task; }

        using var giveUp = new CancellationTokenSource();
        var first = cache.GetOrFetchAsync("k", Fetch, TimeSpan.FromMinutes(1));
        var second = cache.GetOrFetchAsync("k", Fetch, TimeSpan.FromMinutes(1));
        var abandoned = cache.GetOrFetchAsync("k", Fetch, TimeSpan.FromMinutes(1), giveUp.Token);
        giveUp.Cancel();
        release.SetResult("body");

        (await first).Should().Be("body");
        (await second).Should().Be("body");
        var waitAbandoned = async () => await abandoned;
        await waitAbandoned.Should().ThrowAsync<OperationCanceledException>();
        fetches.Should().Be(1);

        // Cached now: no further fetch.
        (await cache.GetOrFetchAsync("k", Fetch, TimeSpan.FromMinutes(1))).Should().Be("body");
        fetches.Should().Be(1);
        ((IDisposable)cache).Dispose();
    }

    [Fact]
    public async Task A_missing_file_is_not_cached_by_the_shared_fetch()
    {
        ISourceContentCache cache = new SourceContentCache();
        var fetches = 0;
        Task<string?> Fetch() { Interlocked.Increment(ref fetches); return Task.FromResult<string?>(null); }

        (await cache.GetOrFetchAsync("missing", Fetch, TimeSpan.FromMinutes(1))).Should().BeNull();
        (await cache.GetOrFetchAsync("missing", Fetch, TimeSpan.FromMinutes(1))).Should().BeNull();
        fetches.Should().Be(2);
        ((IDisposable)cache).Dispose();
    }

    // ------------------------------------------------------------------------------------------
    // GitHubForgeClient
    // ------------------------------------------------------------------------------------------

    private const long InstallationId = 4242;

    private static async Task<Repository> SeedInstalledAsync(IDocumentStore store, long gitHubId, long? installationId = InstallationId)
    {
        using var seed = store.OpenAsyncSession();
        var accountId = Account.DocumentId(EForgeProvider.GitHub, 77);
        await seed.StoreAsync(new Account { GitHubId = 77, Login = "acme", InstallationId = installationId }, accountId);
        var repository = Repo(gitHubId, account: accountId);
        await seed.StoreAsync(repository, repository.Id);
        await seed.SaveChangesAsync();
        return repository;
    }

    private static GitHubForgeClient ForgeClient(IAsyncDocumentSession session, StubGitHub github)
        => new(Diff(github),
            new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance),
            session, github, NullLogger<GitHubForgeClient>.Instance);

    private static string PullJson(int number, long? headRepoId, string state = "open") => $$"""
        {
          "id": 1000{{number}},
          "node_id": "PR_kwDOAbCdEf",
          "number": {{number}},
          "state": "{{state}}",
          "title": "A change",
          "html_url": "https://github.com/acme/widget/pull/{{number}}",
          "user": { "login": "contributor", "id": 9, "type": "User" },
          "head": {
            "label": "contributor:feature",
            "ref": "feature",
            "sha": "{{Head}}",
            "user": { "login": "contributor", "id": 9, "type": "User" },
            "repo": {{(headRepoId is { } id ? $$"""{ "id": {{id}}, "name": "widget", "full_name": "contributor/widget", "default_branch": "main", "private": false, "owner": { "login": "contributor", "id": 9, "type": "User" } }""" : "null")}}
          },
          "base": {
            "label": "acme:main",
            "ref": "main",
            "sha": "{{Base}}",
            "user": { "login": "acme", "id": 77, "type": "Organization" },
            "repo": { "id": 910040, "name": "widget", "full_name": "acme/widget", "default_branch": "trunk", "private": false, "owner": { "login": "acme", "id": 77, "type": "Organization" } }
          }
        }
        """;

    [Theory]
    [InlineData(777L)]
    [InlineData(null)] // the fork was deleted after the pull request was opened
    public async Task A_pull_request_maps_its_head_base_and_state(long? headRepositoryId)
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910040);
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/acme/widget/pulls/42", HttpStatusCode.OK, PullJson(42, headRepositoryId));
        using var session = store.OpenAsyncSession();

        var pull = await ForgeClient(session, github).GetPullRequestAsync(repository, 42);

        pull.Should().NotBeNull(string.Join(", ", github.Calls));
        pull!.Number.Should().Be(42);
        pull.HeadSha.Should().Be(Head);
        pull.HeadRef.Should().Be("feature");
        pull.HeadRepositoryId.Should().Be(headRepositoryId);
        pull.BaseRef.Should().Be("main");
        pull.BaseRepositoryId.Should().Be(910040);
        pull.BaseRepositoryDefaultBranch.Should().Be("trunk");
        pull.IsOpen.Should().BeTrue();
        pull.IsFromFork.Should().BeTrue();
        github.InstallationClients.Should().Equal(InstallationId);
    }

    [Fact]
    public async Task A_closed_pull_request_is_read_as_not_open()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910041);
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/acme/widget/pulls/7", HttpStatusCode.OK, PullJson(7, 910041, state: "closed"));
        using var session = store.OpenAsyncSession();

        (await ForgeClient(session, github).GetPullRequestAsync(repository, 7))!.IsOpen.Should().BeFalse();
    }

    /// <summary>Null is a refusal, never an absence: missing, unreachable and rate-limited all answer null, so the fork path refuses.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_pull_request_GitHub_will_not_return_is_null(HttpStatusCode status)
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910042);
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/acme/widget/pulls/42", status,
            status == HttpStatusCode.NotFound ? StubGitHub.NotFoundBody : """{"message":"Server Error"}""");
        using var session = store.OpenAsyncSession();

        (await ForgeClient(session, github).GetPullRequestAsync(repository, 42)).Should().BeNull();
    }

    /// <summary>Without an installation there is no credential to ask with, and access is reported unavailable with a reason.</summary>
    [Fact]
    public async Task Without_an_installation_nothing_is_asked_and_access_is_unavailable()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910043, installationId: null);
        var github = new StubGitHub();
        using var session = store.OpenAsyncSession();
        var client = ForgeClient(session, github);

        (await client.GetPullRequestAsync(repository, 1)).Should().BeNull();
        var access = await client.CheckAccessAsync(repository);
        access.Available.Should().BeFalse();
        access.UnavailableReason.Should().Be("No GitHub App installation for this repository.");
        await client.DeleteBranchAsync(repository, "feature");
        github.InstallationClients.Should().BeEmpty();
        client.Provider.Should().Be(EForgeProvider.GitHub);
    }

    /// <summary>The installation is resolved once per repository per scope, then memoized.</summary>
    [Fact]
    public async Task The_forge_client_delegates_to_the_diff_and_content_services_with_the_installation()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910044);
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"/repos/acme/widget/compare/{Base}...{Head}", HttpStatusCode.OK, CompareJson())
            .On(HttpMethod.Get, $"/repos/acme/widget/commits/{Head}", HttpStatusCode.OK, CommitJson(Head, Base))
            .On(HttpMethod.Get, "/repositories/910044/contents/src/a.ts", HttpStatusCode.OK, ContentJson("src/a.ts", "text"));
        using var session = store.OpenAsyncSession();
        var client = ForgeClient(session, github);

        (await client.CheckAccessAsync(repository)).Available.Should().BeTrue();
        (await client.CompareAsync(repository, Base, Head))!.Files.Should().ContainSingle();
        (await client.GetFirstParentAsync(repository, Head)).Should().Be(Base);
        (await client.GetFileContentAsync(repository, Head, "src/a.ts")).Should().Be("text");
        session.Advanced.NumberOfRequests.Should().Be(1, "the installation is looked up once and memoized");
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest/git/refs#delete-a-reference"}""")]
    [InlineData(HttpStatusCode.NotFound, StubGitHub.NotFoundBody)]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":"Reference does not exist","documentation_url":"https://docs.github.com/rest/git/refs#delete-a-reference"}""")]
    public async Task Deleting_a_branch_never_throws_whatever_GitHub_answers(HttpStatusCode status, string body)
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910045);
        var github = new StubGitHub().On(HttpMethod.Delete, "/repos/acme/widget/git/refs/heads/feature/x",
            (_, _) => status == HttpStatusCode.NoContent ? new HttpResponseMessage(status) : StubHttpMessageHandler.Json(status, body));
        using var session = store.OpenAsyncSession();

        await ForgeClient(session, github).DeleteBranchAsync(repository, "feature/x");

        github.Calls.Should().ContainSingle(string.Join(", ", github.Calls));
        github.Calls.Single().Should().StartWith("DELETE /repos/acme/widget/git/refs/heads/feature");
    }

    // ------------------------------------------------------------------------------------------
    // GitHubForgeFeedbackPublisher
    // ------------------------------------------------------------------------------------------

    private static string CheckRunJson(long id, string name) => $$"""
        {
          "id": {{id}},
          "head_sha": "{{Head}}",
          "node_id": "CR_kwDOAbCdEf",
          "external_id": "",
          "url": "https://api.github.com/repos/acme/widget/check-runs/{{id}}",
          "html_url": "https://github.com/acme/widget/runs/{{id}}",
          "details_url": "https://coverage.example.invalid",
          "status": "completed",
          "conclusion": "success",
          "started_at": "2026-09-01T12:00:00Z",
          "completed_at": "2026-09-01T12:00:01Z",
          "output": { "title": "85.0%", "summary": "Coverage", "text": null, "annotations_count": 0, "annotations_url": "https://api.github.com/repos/acme/widget/check-runs/{{id}}/annotations" },
          "name": "{{name}}",
          "check_suite": { "id": 5 },
          "app": { "id": 4567511, "slug": "coverage", "name": "Coverage" },
          "pull_requests": []
        }
        """;

    private static GitHubForgeFeedbackPublisher Publisher(IAsyncDocumentSession session, StubGitHub github, IPullRequestCommentPublisher? comments = null)
        => new(github, comments ?? Substitute.For<IPullRequestCommentPublisher>(), session);

    [Theory]
    [InlineData(EForgeOutcome.Success, "success")]
    [InlineData(EForgeOutcome.Failure, "failure")]
    [InlineData(EForgeOutcome.Neutral, "neutral")]
    public async Task A_first_publish_creates_a_completed_check_run(EForgeOutcome outcome, string conclusion)
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910050);
        string? posted = null;
        var github = new StubGitHub().On(HttpMethod.Post, "/repos/acme/widget/check-runs",
            (_, body) => { posted = body; return StubHttpMessageHandler.Json(HttpStatusCode.Created, CheckRunJson(555, "coverage/project")); });
        using var session = store.OpenAsyncSession();

        var id = await Publisher(session, github).PublishStatusAsync(repository, Head, "coverage/project",
            new ForgeVerdict(outcome, "85.0%", "Coverage"), existingId: null);

        id.Should().Be(555);
        posted.Should().Contain("\"name\":\"coverage/project\"");
        posted.Should().Contain($"\"head_sha\":\"{Head}\"");
        posted.Should().Contain($"\"conclusion\":\"{conclusion}\"");
        posted.Should().Contain("\"status\":\"completed\"");
    }

    [Fact]
    public async Task A_republish_updates_the_existing_check_run_instead_of_duplicating_it()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910051);
        var github = new StubGitHub().On(HttpMethod.Patch, "/repos/acme/widget/check-runs/555", HttpStatusCode.OK, CheckRunJson(555, "coverage/patch"));
        using var session = store.OpenAsyncSession();

        var id = await Publisher(session, github).PublishStatusAsync(repository, Head, "coverage/patch",
            new ForgeVerdict(EForgeOutcome.Failure, "40%", "Below the gate"), existingId: 555);

        id.Should().Be(555);
        github.Calls.Should().Equal("PATCH /repos/acme/widget/check-runs/555");
    }

    /// <summary>An installation that has not accepted checks:write cannot be helped by a retry; callers see the forge-neutral exception.</summary>
    [Fact]
    public async Task A_403_becomes_a_forge_access_denied()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910052);
        var github = new StubGitHub().On(HttpMethod.Post, "/repos/acme/widget/check-runs", HttpStatusCode.Forbidden,
            """{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest/checks/runs#create-a-check-run"}""");
        using var session = store.OpenAsyncSession();

        var ex = await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => Publisher(session, github).PublishStatusAsync(
            repository, Head, "coverage/project", new ForgeVerdict(EForgeOutcome.Success, "t", "s"), existingId: null));

        ex.Message.Should().Contain("Resource not accessible by integration");
    }

    [Fact]
    public async Task Publishing_without_an_installation_is_a_programming_error_not_a_silent_skip()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910053, installationId: null);
        using var session = store.OpenAsyncSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(session, new StubGitHub()).PublishStatusAsync(
            repository, Head, "coverage/project", new ForgeVerdict(EForgeOutcome.Success, "t", "s"), existingId: null));
    }

    [Fact]
    public async Task A_comment_is_handed_to_the_comment_publisher_with_the_installation()
    {
        using var store = GetDocumentStore();
        var repository = await SeedInstalledAsync(store, 910054);
        var comments = Substitute.For<IPullRequestCommentPublisher>();
        using var session = store.OpenAsyncSession();
        var publisher = Publisher(session, new StubGitHub(), comments);

        await publisher.PublishCommentAsync(repository, 17, Head, "body");

        await comments.Received(1).PublishAsync(repository, InstallationId, 17, Head, "body", Arg.Any<CancellationToken>());
        publisher.Provider.Should().Be(EForgeProvider.GitHub);
    }

    // ------------------------------------------------------------------------------------------
    // GitHubPullRequestCommentGateway
    // ------------------------------------------------------------------------------------------

    private static string CommentJson(long id, string body, string userType) => $$"""
        {
          "id": {{id}},
          "node_id": "IC_kwDOAbCdEf",
          "url": "https://api.github.com/repos/acme/widget/issues/comments/{{id}}",
          "html_url": "https://github.com/acme/widget/pull/17#issuecomment-{{id}}",
          "body": "{{body}}",
          "user": { "login": "{{(userType == "Bot" ? "coverage[bot]" : "reviewer")}}", "id": {{id}}, "type": "{{userType}}" },
          "created_at": "2026-09-01T12:00:00Z",
          "updated_at": "2026-09-01T12:00:00Z",
          "author_association": "NONE"
        }
        """;

    [Fact]
    public async Task Comments_are_listed_with_whether_the_app_wrote_them()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/acme/widget/issues/17/comments", HttpStatusCode.OK,
            $"[{CommentJson(1, "Coverage report", "Bot")},{CommentJson(2, "LGTM", "User")}]");

        var comments = await new GitHubPullRequestCommentGateway(github).ListAsync(Repo(910060), 5, 17, default);

        comments.Should().Equal(new ExistingComment(1, "Coverage report", true), new ExistingComment(2, "LGTM", false));
    }

    [Fact]
    public async Task A_comment_is_created_and_later_updated_in_place()
    {
        string? created = null, updated = null;
        var github = new StubGitHub()
            .On(HttpMethod.Post, "/repos/acme/widget/issues/17/comments",
                (_, body) => { created = body; return StubHttpMessageHandler.Json(HttpStatusCode.Created, CommentJson(99, "new", "Bot")); })
            .On(HttpMethod.Patch, "/repos/acme/widget/issues/comments/99",
                (_, body) => { updated = body; return StubHttpMessageHandler.Json(HttpStatusCode.OK, CommentJson(99, "edited", "Bot")); });
        var gateway = new GitHubPullRequestCommentGateway(github);

        (await gateway.CreateAsync(Repo(910061), 5, 17, "new", default)).Should().Be(99);
        await gateway.UpdateAsync(Repo(910061), 5, 99, "edited", default);

        created.Should().Contain("\"body\":\"new\"");
        updated.Should().Contain("\"body\":\"edited\"");
    }

    // ------------------------------------------------------------------------------------------
    // InstallationRepositories
    // ------------------------------------------------------------------------------------------

    private static string InstallationRepositoriesJson(int total, IEnumerable<int> ids) => $$"""
        {
          "total_count": {{total}},
          "repository_selection": "selected",
          "repositories": [{{string.Join(",", ids.Select(i => $$"""
            { "id": {{i}}, "node_id": "R_{{i}}", "name": "repo{{i}}", "full_name": "acme/repo{{i}}", "private": {{(i % 2 == 0 ? "true" : "false")}},
              "owner": { "login": "acme", "id": 77, "type": "Organization" }, "default_branch": "main", "archived": false }
            """))}}]
        }
        """;

    /// <summary>
    /// Paged until the list is complete. A truncated list would make the reconciler read the
    /// missing page as "we lost access" and disconnect a whole organization at once.
    /// </summary>
    [Fact]
    public async Task Installation_repositories_are_read_page_by_page_until_complete()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/installation/repositories", (uri, _) =>
        {
            var page = System.Web.HttpUtility.ParseQueryString(uri.Query)["page"] ?? "1";
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, page == "1"
                ? InstallationRepositoriesJson(150, Enumerable.Range(1, 100))
                : InstallationRepositoriesJson(150, Enumerable.Range(101, 50)));
        });

        var repositories = await new InstallationRepositories(github).ListAsync(5, max: 1024);

        repositories.Should().HaveCount(150, string.Join(", ", github.Calls));
        repositories[0].Should().Be(new InstallationRepository(1, "repo1", "acme/repo1", "acme", false, "main", false));
        repositories[1].IsPrivate.Should().BeTrue();
        github.Calls.Count.Should().Be(2);
    }

    [Fact]
    public async Task Installation_repositories_stop_at_the_requested_maximum()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
            InstallationRepositoriesJson(500, Enumerable.Range(1, 100)));

        (await new InstallationRepositories(github).ListAsync(5, max: 100)).Should().HaveCount(100);
        github.Calls.Count.Should().Be(1);
    }

    [Fact]
    public async Task An_installation_with_no_repositories_is_an_empty_list()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
            InstallationRepositoriesJson(0, []));

        (await new InstallationRepositories(github).ListAsync(5, max: 100)).Should().BeEmpty();
    }

    /// <summary>A vanished installation surfaces as Octokit's NotFoundException, which is what the reconciler reads as "gone".</summary>
    [Fact]
    public async Task A_vanished_installation_throws_not_found_for_the_reconciler_to_classify()
    {
        var github = new StubGitHub();

        await Assert.ThrowsAsync<Octokit.NotFoundException>(() => new InstallationRepositories(github).ListAsync(5, max: 100));
    }
}
