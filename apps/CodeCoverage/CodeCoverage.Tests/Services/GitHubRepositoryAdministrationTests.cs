using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// <see cref="GitHubRepositoryAdministration"/> (dependency-updates PRD R21, R32, R33, R35) and <see cref="GitHubSetupRedirect"/>
/// (R36) over <see cref="StubGitHub"/>.
/// </summary>
/// <remarks>
/// The GraphQL and REST collaborator answers are live captures (<c>Fixtures/GitHub/Onboarding/SOURCE.md</c>, user token).
/// The <c>/user/installations…</c> bodies are docs-derived (docs.github.com/rest/apps/installations): they need a GitHub
/// App user token, which the capturing CLI does not hold.
/// </remarks>
public class GitHubRepositoryAdministrationTests
{
    private const long InstallationId = 4242;

    private static Account Acme() => new() { Id = Account.DocumentId(EForgeProvider.GitHub, 77), GitHubId = 77, Login = "acme", InstallationId = InstallationId };

    private static Repository Repo(long id, string name) => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, id), GitHubId = id, Name = name, FullName = $"acme/{name}", OwnerLogin = "acme",
    };

    private static GitHubRepositoryAdministration Administration(StubGitHub github, string? login = "octocat", string? userId = "users/1",
        Func<bool, GitHubUserToken>? tokens = null)
    {
        var store = new InMemoryUserStore();
        if (userId is not null)
            store.Add(new SparkUser { Id = userId, UserName = "someone" });
        return new GitHubRepositoryAdministration(
            new FakeSparkCurrentUser(userId),
            GitHubAuthTestFakes.UserManagerOver(store),
            new ScriptedTokenService(tokens ?? (_ => new GitHubUserToken("user-token", GitHubTokenState.Ok))),
            new ScriptedAccessService(new GitHubVisibility([], GitHubTokenState.Ok), new GitHubVerifiedLogin(login, login is not null, GitHubTokenState.Ok)),
            github, new SingleClientHttpFactory(github.Rest), NullLogger<GitHubRepositoryAdministration>.Instance);
    }

    private static string Onboarding(string name) => Fixture.Read($"GitHub/Onboarding/{name}");

    // ── The execution-time check: GraphQL, per alias, with the REST fallback ─────────────────────

    [Fact]
    public async Task One_aliased_query_answers_and_a_refused_alias_falls_back_to_rest()
    {
        string? posted = null;
        var github = new StubGitHub()
            .On(HttpMethod.Post, "/graphql", (_, body) => { posted = body; return StubHttpMessageHandler.Json(HttpStatusCode.OK, Onboarding("graphql-collaborators-partial.json")); })
            .On(HttpMethod.Get, "/repos/acme/kernel/collaborators/octocat/permission", HttpStatusCode.OK, Onboarding("rest-collaborator-permission-admin.json"));

        var answer = await Administration(github).CheckAdministratorAsync(Acme(), [Repo(1, "widget"), Repo(2, "kernel")], "octocat");

        answer[Repo(1, "widget").Id!].Should().BeTrue("r0's edge says ADMIN");
        answer[Repo(2, "kernel").Id!].Should().BeTrue("r1 was FORBIDDEN in GraphQL and REST says admin");
        github.Calls.Should().Equal(["POST /graphql", "GET /repos/acme/kernel/collaborators/octocat/permission"]);

        // Every value is a variable: no owner, name or login is spliced into the query text.
        using var request = JsonDocument.Parse(posted!);
        var query = request.RootElement.GetProperty("query").GetString()!;
        query.Should().NotContain("octocat").And.NotContain("widget").And.Contain("r1: repository(owner: $o1, name: $n1)");
        request.RootElement.GetProperty("variables").GetProperty("n1").GetString().Should().Be("kernel");
    }

    [Fact]
    public async Task An_error_naming_no_alias_asks_every_repository_over_rest()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Post, "/graphql", HttpStatusCode.OK, """{"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by integration"}]}""")
            .On(HttpMethod.Get, "/repos/acme/widget/collaborators/octocat/permission", HttpStatusCode.OK, Onboarding("rest-collaborator-permission-admin.json"));

        var answer = await Administration(github).CheckAdministratorAsync(Acme(), [Repo(1, "widget"), Repo(2, "kernel")], "octocat");

        answer[Repo(1, "widget").Id!].Should().BeTrue();
        answer[Repo(2, "kernel").Id!].Should().BeFalse("REST answered 404: not a collaborator, fail closed");
    }

    [Theory]
    [InlineData("MAINTAIN")]
    [InlineData("WRITE")]
    public async Task Only_the_admin_role_counts(string permission)
    {
        var github = new StubGitHub().On(HttpMethod.Post, "/graphql", HttpStatusCode.OK,
            """{"data":{"r0":{"collaborators":{"edges":[{"permission":"PERMISSION"}]}}}}""".Replace("PERMISSION", permission, StringComparison.Ordinal));

        (await Administration(github).CheckAdministratorAsync(Acme(), [Repo(1, "widget")], "octocat"))[Repo(1, "widget").Id!].Should().BeFalse("R33");
    }

    [Fact]
    public async Task A_graphql_rate_limit_is_retried_later_not_answered_over_rest()
    {
        var github = new StubGitHub().On(HttpMethod.Post, "/graphql", HttpStatusCode.OK, """{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded"}]}""");

        var act = () => Administration(github).CheckAdministratorAsync(Acme(), [Repo(1, "widget")], "octocat");

        ForgeRateLimitedException.Find((await act.Should().ThrowAsync<ForgeRequestException>()).Which)!.Kind.Should().Be(EForgeRateLimitKind.GraphQL);
        github.Calls.Should().Equal(["POST /graphql"]);
    }

    [Fact]
    public async Task A_malformed_login_or_no_installation_answers_no_without_asking()
    {
        var github = new StubGitHub();

        (await Administration(github).CheckAdministratorAsync(Acme(), [Repo(1, "widget")], "x\" } evil"))[Repo(1, "widget").Id!].Should().BeFalse();
        (await Administration(github).CheckAdministratorAsync(new Account { Login = "acme" }, [Repo(1, "widget")], "octocat"))[Repo(1, "widget").Id!].Should().BeFalse();
        github.Calls.Should().BeEmpty();
    }

    // ── The listing: the viewer's own token ──────────────────────────────────────────────────────

    // docs: GET /user/installations/{installation_id}/repositories
    private const string InstallationRepositories = """
        {"total_count":2,"repositories":[
          {"id":910040,"node_id":"R_1","name":"widget","full_name":"acme/widget","private":true,
           "owner":{"login":"acme","id":77,"type":"Organization"},"permissions":{"admin":true,"maintain":true,"push":true,"triage":true,"pull":true}},
          {"id":910041,"node_id":"R_2","name":"gadget","full_name":"acme/gadget","private":false,
           "owner":{"login":"acme","id":77,"type":"Organization"},"permissions":{"admin":false,"maintain":true,"push":true,"triage":true,"pull":true}}]}
        """;

    [Fact]
    public async Task One_installation_lists_its_repositories_with_the_admin_flag()
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/user/installations/4242/repositories", HttpStatusCode.OK, InstallationRepositories);

        var listing = await Administration(github).ListViewerRepositoriesAsync("4242");

        listing.State.Should().Be(EForgeCredentialState.Ok);
        listing.Login.Should().Be("octocat");
        listing.Repositories.Select(r => (r.FullName, r.IsAdmin, r.AccountForgeId, r.InstallationKey)).Should().Equal(
            [("acme/widget", true, 77L, "4242"), ("acme/gadget", false, 77L, "4242")]);
        github.Rest.Requests.Should().OnlyContain(r => r.AuthorizationParameter == "user-token", "the listing is the viewer's own token, never the App's");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_foreign_or_unknown_installation_lists_nothing_like_a_missing_one(HttpStatusCode status)
    {
        var github = new StubGitHub().On(HttpMethod.Get, "/user/installations/999/repositories", status, StubGitHub.NotFoundBody);

        var listing = await Administration(github).ListViewerRepositoriesAsync("999");

        listing.Repositories.Should().BeEmpty();
        listing.State.Should().Be(EForgeCredentialState.Ok, "no existence oracle: the same answer as an installation that does not exist");
        (await Administration(new StubGitHub()).ListViewerRepositoriesAsync("not-a-number")).Repositories.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_key_every_installation_of_the_viewer_is_listed()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, "/user/installations", HttpStatusCode.OK, """{"total_count":1,"installations":[{"id":4242,"account":{"login":"acme","id":77},"suspended_at":null}]}""")
            .On(HttpMethod.Get, "/user/installations/4242/repositories", HttpStatusCode.OK, InstallationRepositories);

        (await Administration(github).ListViewerRepositoriesAsync(null)).Repositories.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once()
    {
        var calls = 0;
        var github = new StubGitHub().On(HttpMethod.Get, "/user/installations/4242/repositories",
            (_, _) => ++calls == 1 ? StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""") : StubHttpMessageHandler.Json(HttpStatusCode.OK, InstallationRepositories));

        var listing = await Administration(github, tokens: forced => new GitHubUserToken(forced ? "fresh" : "stale", GitHubTokenState.Ok)).ListViewerRepositoriesAsync("4242");

        listing.Repositories.Should().HaveCount(2);
        github.Rest.Requests.Select(r => r.AuthorizationParameter).Should().Equal(["stale", "fresh"]);
    }

    [Fact]
    public async Task A_viewer_without_a_github_login_lists_nothing()
        => (await Administration(new StubGitHub(), login: null).ListViewerRepositoriesAsync(null)).Repositories.Should().BeEmpty();

    // ── R36: the setup redirect ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("install", EForgeSetupAction.Install)]
    [InlineData("update", EForgeSetupAction.Update)]
    [InlineData("request", EForgeSetupAction.Request)]
    [InlineData("something", EForgeSetupAction.Unknown)]
    public void The_setup_redirect_reads_the_installation_and_the_hint(string action, EForgeSetupAction expected)
    {
        var read = new GitHubSetupRedirect().Read(new Dictionary<string, string?> { ["installation_id"] = "4242", ["setup_action"] = action });

        read.Should().Be(new ForgeSetupRedirect(EForgeProvider.GitHub, "4242", expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("42; drop")]
    public void A_setup_redirect_without_a_usable_installation_is_none(string id)
        => new GitHubSetupRedirect().Read(new Dictionary<string, string?> { ["installation_id"] = id }).Should().BeNull();
}
