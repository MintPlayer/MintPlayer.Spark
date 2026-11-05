using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>The GitHub half of <see cref="IForgeVersioning"/>, against a scripted REST API.</summary>
public class GitHubForgeVersioningTests
{
    private const string Sha = "4a5b6c7d8e9f00112233445566778899aabbccdd";
    private const string Repo = "/repos/acme/widget";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static readonly Repository Target = new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040), GitHubId = 910040, Name = "widget", FullName = "acme/widget",
        OwnerLogin = "acme", Account = AccountId,
    };

    private static GitHubForgeVersioning Forge(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = 4242 });
        return new GitHubForgeVersioning(github, new SingleClientHttpFactory(github.Rest), session);
    }

    [Fact]
    public async Task A_commit_names_only_its_pull_requests_merged_into_the_branch()
    {
        // The commit's pull requests include an open one that contains it and one merged into a release branch.
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/commits/{Sha}/pulls?per_page=10", HttpStatusCode.OK, """
            [{"number":203,"merged_at":"2026-10-26T09:30:00Z","base":{"ref":"master"}},
             {"number":204,"merged_at":null,"base":{"ref":"master"}},
             {"number":205,"merged_at":"2026-10-26T09:31:00Z","base":{"ref":"release/10.x"}}]
            """);

        var numbers = await Forge(github).MergedPullRequestsAsync(Target, Sha, "master");

        numbers.Should().Equal(203);
    }

    [Fact]
    public async Task A_commit_the_forge_does_not_know_has_none()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/commits/{Sha}/pulls?per_page=10", HttpStatusCode.UnprocessableEntity,
            """{"message":"No commit found for SHA"}""");

        (await Forge(github).MergedPullRequestsAsync(Target, Sha, "master")).Should().BeEmpty();
    }

    /// <summary>Answers <paramref name="status"/> with <paramref name="json"/> and keeps the posted body.</summary>
    private static StubGitHub Capturing(HttpMethod method, string path, HttpStatusCode status, string json, List<string?> bodies)
        => new StubGitHub().On(method, path, (_, body) => { bodies.Add(body); return StubHttpMessageHandler.Json(status, json); });

    private static async Task<T> Throws<T>(Func<Task> act) where T : Exception
        => (await act.Should().ThrowAsync<T>()).Which;

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_merged_bot_pull_request_is_read_with_its_labels_and_merge_commit()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/7", HttpStatusCode.OK, $$$"""
            {"number":7,"title":"Bump x","body":null,"state":"closed","merged":true,"merge_commit_sha":"{{{Sha}}}",
             "user":{"login":"dependabot[bot]","type":"Bot"},"labels":[{"name":"deps"},{"name":"backport release/dotnet-10.x"}],
             "head":{"ref":"dependabot/x","sha":"1111111111111111111111111111111111111111"},"base":{"ref":"master"}}
            """);

        var pull = (await Forge(github).GetPullRequestTextAsync(Target, 7))!;

        pull.Should().BeEquivalentTo(new ForgePullRequestText(7, "Bump x", null, "dependabot/x", "1111111111111111111111111111111111111111", "master",
            IsOpen: false, Merged: true, AuthorIsBot: true, MergeCommitSha: Sha, Labels: ["deps", "backport release/dotnet-10.x"]));
    }

    [Fact]
    public void An_open_pull_request_by_a_person_has_no_merge_commit_and_no_labels()
    {
        using var json = System.Text.Json.JsonDocument.Parse($$$"""
            {"number":8,"title":"Fix","body":"text","state":"open","merged":false,"merge_commit_sha":"{{{Sha}}}",
             "user":{"login":"octocat","type":"User"},"head":{"ref":"fix","sha":"{{{Sha}}}"},"base":{"ref":"release/dotnet-10.x"}}
            """);

        var pull = GitHubForgeVersioning.ReadPullRequest(json.RootElement);

        (pull.IsOpen, pull.Merged, pull.AuthorIsBot, pull.MergeCommitSha, pull.Labels.Count).Should().Be((true, false, false, (string?)null, 0),
            "GitHub fills merge_commit_sha with a test merge before a pull request merges");
    }

    [Fact]
    public async Task A_pull_request_that_does_not_exist_is_null()
        => (await Forge(new StubGitHub()).GetPullRequestTextAsync(Target, 404)).Should().BeNull();

    [Fact]
    public async Task An_issue_is_read_with_its_labels()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/issues/12", HttpStatusCode.OK, """{"number":12,"labels":[{"name":"bug"}]}""");

        (await Forge(github).GetIssueAsync(Target, 12)).Should().BeEquivalentTo(new ForgeIssue(12, ["bug"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"number":13,"pull_request":{"url":"x"}}""")] // the issues API also answers for pull requests
    [InlineData(HttpStatusCode.Gone, """{"message":"This issue was deleted"}""")]
    [InlineData(HttpStatusCode.MovedPermanently, """{"message":"Moved Permanently"}""")]
    [InlineData(HttpStatusCode.NotFound, StubGitHub.NotFoundBody)]
    public async Task An_issue_that_is_a_pull_request_or_gone_is_null(HttpStatusCode status, string json)
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/issues/13", status, json);

        (await Forge(github).GetIssueAsync(Target, 13)).Should().BeNull();
    }

    // ── Writes ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_title_update_patches_only_the_title_and_a_refusal_names_the_permission()
    {
        var bodies = new List<string?>();
        await Forge(Capturing(HttpMethod.Patch, $"{Repo}/pulls/7", HttpStatusCode.OK, "{}", bodies)).UpdatePullRequestTitleAsync(Target, 7, "feat: x");
        bodies.Should().Equal("""{"title":"feat: x"}""");

        var denied = new StubGitHub().On(HttpMethod.Patch, $"{Repo}/pulls/7", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        var thrown = await Throws<ForgeAccessDeniedException>(() => Forge(denied).UpdatePullRequestTitleAsync(Target, 7, "feat: x"));
        (thrown.Message, thrown.RequiredPermission).Should().Be(("Resource not accessible by integration", "pull_requests=write"));
    }

    [Fact]
    public async Task A_check_is_found_by_name_and_patched()
    {
        var bodies = new List<string?>();
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/commits/{Sha}/check-runs?check_name=Package%20versions&filter=latest&per_page=1", HttpStatusCode.OK,
                """{"total_count":1,"check_runs":[{"id":55}]}""")
            .On(HttpMethod.Patch, $"{Repo}/check-runs/55", (_, body) => { bodies.Add(body); return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":55}"""); });

        var id = await Forge(github).PublishCheckAsync(Target, Sha, new ForgeVersioningCheck("Package versions", true, "success", "2 packages", "summary", "details"), null);

        id.Should().Be(55);
        bodies.Should().ContainSingle().Which.Should().Contain("\"conclusion\":\"success\"").And.Contain("\"text\":\"details\"");
    }

    [Fact]
    public async Task A_check_that_does_not_exist_yet_is_created_in_progress_without_text()
    {
        var bodies = new List<string?>();
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/commits/{Sha}/check-runs?check_name=Package%20versions&filter=latest&per_page=1", HttpStatusCode.OK,
                """{"total_count":0,"check_runs":[]}""")
            .On(HttpMethod.Post, $"{Repo}/check-runs", (_, body) => { bodies.Add(body); return StubHttpMessageHandler.Json(HttpStatusCode.Created, """{"id":56}"""); });

        var id = await Forge(github).PublishCheckAsync(Target, Sha, new ForgeVersioningCheck("Package versions", false, "", "Planning", "…", null), null);

        id.Should().Be(56);
        bodies.Should().ContainSingle().Which.Should().Contain("\"status\":\"in_progress\"").And.NotContain("conclusion").And.NotContain("\"text\"");
    }

    [Fact]
    public async Task A_known_check_id_is_patched_without_a_lookup()
    {
        var github = new StubGitHub().On(HttpMethod.Patch, $"{Repo}/check-runs/57", HttpStatusCode.OK, """{"id":57}""");

        (await Forge(github).PublishCheckAsync(Target, Sha, new ForgeVersioningCheck("Package versions", true, "failure", "t", "s", null), 57)).Should().Be(57);
        github.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_release_is_never_made_latest_and_a_second_one_is_an_existing_release()
    {
        var bodies = new List<string?>();
        var created = Capturing(HttpMethod.Post, $"{Repo}/releases", HttpStatusCode.Created, """{"id":1}""", bodies);
        var release = new ForgeRelease("Acme.Core/v10.0.1", "Acme.Core 10.0.1", "notes", Prerelease: false);

        (await Forge(created).CreateReleaseAsync(Target, release)).Should().Be(ForgeWriteOutcome.Created);
        bodies.Should().ContainSingle().Which.Should().Contain("\"make_latest\":\"false\"").And.Contain("\"tag_name\":\"Acme.Core/v10.0.1\"");

        var exists = new StubGitHub().On(HttpMethod.Post, $"{Repo}/releases", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"Release","code":"already_exists","field":"tag_name"}]}""");
        (await Forge(exists).CreateReleaseAsync(Target, release)).Should().Be(ForgeWriteOutcome.Exists);

        var invalid = new StubGitHub().On(HttpMethod.Post, $"{Repo}/releases", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"Release","code":"invalid","field":"tag_name"}]}""");
        (await Throws<ForgeRequestException>(() => Forge(invalid).CreateReleaseAsync(Target, release))).StatusCode.Should().Be(422);
    }

    [Fact]
    public async Task A_release_branch_is_cut_once_and_a_second_cut_finds_it()
    {
        var bodies = new List<string?>();
        var created = Capturing(HttpMethod.Post, $"{Repo}/git/refs", HttpStatusCode.Created, """{"ref":"refs/heads/release/dotnet-10.x"}""", bodies);

        (await Forge(created).CreateBranchAsync(Target, "release/dotnet-10.x", Sha)).Should().Be(ForgeWriteOutcome.Created);
        bodies.Should().Equal($$"""{"ref":"refs/heads/release/dotnet-10.x","sha":"{{Sha}}"}""");

        var exists = new StubGitHub().On(HttpMethod.Post, $"{Repo}/git/refs", HttpStatusCode.UnprocessableEntity, """{"message":"Reference already exists"}""");
        (await Forge(exists).CreateBranchAsync(Target, "release/dotnet-10.x", Sha)).Should().Be(ForgeWriteOutcome.Exists);

        var other = new StubGitHub().On(HttpMethod.Post, $"{Repo}/git/refs", HttpStatusCode.UnprocessableEntity, """{"message":"Object does not exist"}""");
        (await Throws<ForgeRequestException>(() => Forge(other).CreateBranchAsync(Target, "release/dotnet-10.x", Sha))).Message
            .Should().Be("POST repos/acme/widget/git/refs answered 422: Object does not exist");
    }

    [Fact]
    public async Task A_pull_request_is_opened_and_a_redelivery_finds_the_open_one()
    {
        var opened = new StubGitHub().On(HttpMethod.Post, $"{Repo}/pulls", HttpStatusCode.Created, """{"number":31}""");
        (await Forge(opened).OpenPullRequestAsync(Target, "backport/31", "release/dotnet-10.x", "t", "b")).Should().Be(31);

        var redelivered = new StubGitHub()
            .On(HttpMethod.Post, $"{Repo}/pulls", HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"message":"A pull request already exists for acme:backport/31."}]}""")
            .On(HttpMethod.Get, $"{Repo}/pulls?state=open&head=acme%3Abackport%2F31&base=release%2Fdotnet-10.x&per_page=1", HttpStatusCode.OK, """[{"number":31}]""");
        (await Forge(redelivered).OpenPullRequestAsync(Target, "backport/31", "release/dotnet-10.x", "t", "b")).Should().Be(31);
    }

    [Fact]
    public async Task A_refused_pull_request_without_an_open_one_is_an_error()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Post, $"{Repo}/pulls", HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"message":"No commits between release/dotnet-10.x and backport/31"}]}""")
            .On(HttpMethod.Get, $"{Repo}/pulls?state=open&head=acme%3Abackport%2F31&base=release%2Fdotnet-10.x&per_page=1", HttpStatusCode.OK, "[]");

        (await Throws<ForgeRequestException>(() => Forge(github).OpenPullRequestAsync(Target, "backport/31", "release/dotnet-10.x", "t", "b"))).StatusCode.Should().Be(422);
    }

    [Fact]
    public async Task A_comment_and_a_workflow_dispatch_post_their_bodies()
    {
        var comments = new List<string?>();
        await Forge(Capturing(HttpMethod.Post, $"{Repo}/issues/7/comments", HttpStatusCode.Created, """{"id":1}""", comments)).CommentAsync(Target, 7, "Backported in #31.");
        comments.Should().Equal("""{"body":"Backported in #31."}""");

        var dispatches = new List<string?>();
        await Forge(Capturing(HttpMethod.Post, $"{Repo}/actions/workflows/release.yml/dispatches", HttpStatusCode.NoContent, "", dispatches))
            .DispatchWorkflowAsync(Target, "release.yml", "release/dotnet-10.x");
        dispatches.Should().Equal("""{"ref":"release/dotnet-10.x"}""");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repository_without_an_installation_is_refused_before_any_call()
    {
        var github = new StubGitHub();
        var orphan = new Repository { Id = Target.Id, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", Account = null };

        (await Throws<ForgeAccessDeniedException>(() => Forge(github).CommentAsync(orphan, 7, "x"))).Message.Should().Be("No GitHub App installation for acme/widget.");
        github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_is_retryable()
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/issues/7/comments", (_, _) =>
        {
            var response = StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded for installation ID 4242."}""");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return response;
        });

        (await Throws<ForgeRequestException>(() => Forge(github).CommentAsync(Target, 7, "x"))).StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task An_html_error_page_is_reported_by_its_status()
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/actions/workflows/release.yml/dispatches",
            (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") });

        var thrown = await Throws<ForgeRequestException>(() => Forge(github).DispatchWorkflowAsync(Target, "release.yml", "release/dotnet-10.x"));

        (thrown.StatusCode, thrown.Message).Should().Be((502, "POST repos/acme/widget/actions/workflows/release.yml/dispatches answered 502: "));
    }
}
