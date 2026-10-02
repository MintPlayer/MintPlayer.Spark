using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using CodeCoverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The 401 → one forced refresh → retry path (docs/reauth-on-401.md M1.2),
/// and the tri-state propagation: degraded results are never cached and never
/// clear anything (extends the 3970d22 "failure is not absence" behavior).
/// Embedded RavenDB backs the success path's installation-id backfill.
/// </summary>
/// <remarks>
/// ⚠️ The viewer's GitHub login comes from <c>GET /user</c> for the viewer's token, never from the
/// Identity user name — which the profile page lets a user edit. Every test's user name is therefore
/// set to an organisation's login (<see cref="EditableUserName"/>): a test that passed while reading
/// the user name would grant that organisation.
/// </remarks>
public class GitHubAccessServiceRefreshTests : CoverageRavenTest
{
    private const long OwnGitHubId = 9629574;
    private const string OwnLogin = "PieterjanDeClippel";
    private const string EditableUserName = "SomeoneElsesOrg";

    private const string UserJson = """{ "id": 9629574, "login": "PieterjanDeClippel", "type": "User" }""";

    private const string InstallationsJson = """
        {
          "total_count": 1,
          "installations": [
            {
              "id": 153409068,
              "target_type": "Organization",
              "suspended_at": null,
              "account": { "login": "MintPlayer", "id": 48772716, "type": "Organization" }
            }
          ]
        }
        """;

    private static SparkUser NewUser() => new()
    {
        Id = $"SparkUsers/{Guid.NewGuid():N}",
        UserName = EditableUserName,
    };

    /// <summary>Answers GET /user and GET /user/installations like GitHub does for a valid token.</summary>
    private static HttpResponseMessage GitHub(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/user" => StubHttpMessageHandler.Json(HttpStatusCode.OK, UserJson),
        "/user/installations" => StubHttpMessageHandler.Json(HttpStatusCode.OK, InstallationsJson),
        var path => throw new InvalidOperationException($"unexpected GitHub call {path}"),
    };

    private sealed record Harness(IGitHubAccessService Access, ScriptedTokenService Tokens, StubHttpMessageHandler Handler, IMemoryCache Cache);

    private static Harness CreateService(IAsyncDocumentSession session, SparkUser user,
        Func<bool, GitHubUserToken> tokenScript,
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        var tokens = new ScriptedTokenService(tokenScript);
        var handler = new StubHttpMessageHandler(responder);
        var cache = new MemoryCache(new MemoryCacheOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor(GitHubAuthTestFakes.PrincipalFor(user)));
        services.AddSingleton(GitHubAuthTestFakes.UserManagerOver(new InMemoryUserStore().Add(user)));
        services.AddSingleton<IGitHubUserTokenService>(tokens);
        services.AddSingleton<IHttpClientFactory>(new SingleClientHttpFactory(handler));
        services.AddSingleton<IMemoryCache>(cache);
        services.AddSingleton(session);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IGitHubAccessService, GitHubAccessService>();
        return new(services.BuildServiceProvider().GetRequiredService<IGitHubAccessService>(), tokens, handler, cache);
    }

    [Fact]
    public async Task Unauthorized_response_forces_one_refresh_and_retries_successfully()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: forced => new(forced ? "ghu_test_refreshed" : "ghu_test_stale", GitHubTokenState.Ok),
            responder: (request, _) => request.Headers.Authorization?.Parameter == "ghu_test_stale"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : GitHub(request));

        var visibility = await harness.Access.GetVisibilityAsync();

        visibility.TokenState.Should().Be(GitHubTokenState.Ok);
        visibility.Owners.Should().BeEquivalentTo(["MintPlayer", OwnLogin]);
        harness.Tokens.ForcedCalls.Should().Be(1);
        harness.Handler.Requests.Should().HaveCount(3, "the stale token 401s on /user, the refreshed one asks /user and /user/installations");
    }

    [Fact]
    public async Task The_editable_identity_user_name_is_never_an_owner()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = CreateService(session, NewUser(),
            tokenScript: _ => new("ghu_test_ok", GitHubTokenState.Ok),
            responder: (request, _) => GitHub(request));

        var visibility = await harness.Access.GetVisibilityAsync();

        // The own login is the one GitHub returned for the token.
        visibility.Owners.Should().BeEquivalentTo(["MintPlayer", OwnLogin]);
        visibility.Owners.Should().NotContain(EditableUserName);
    }

    /// <summary>
    /// A failed lookup with no earlier GitHub answer degrades to <em>no</em> owners, and the
    /// <em>failure</em> is remembered briefly while the degraded owner set is not.
    /// </summary>
    /// <remarks>
    /// This used to degrade to the principal's name, which is the editable Identity user name. With
    /// GitHub unreachable there is nothing that says who the token belongs to, so nothing is granted.
    /// </remarks>
    [Fact]
    public async Task Refresh_failure_after_401_grants_nothing_and_remembers_only_the_failure()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: forced => forced
                ? new(null, GitHubTokenState.ReauthRequired)
                : new("ghu_test_stale", GitHubTokenState.Ok),
            responder: (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var first = await harness.Access.GetVisibilityAsync();
        var second = await harness.Access.GetVisibilityAsync();

        first.Owners.Should().BeEmpty();
        first.TokenState.Should().Be(GitHubTokenState.ReauthRequired);

        // The second call short-circuits on the remembered failure rather than re-attempting:
        // 2 token calls (initial + forced) for the first, none for the second.
        harness.Tokens.Calls.Should().Be(2, "the failure is remembered, so an outage costs one attempt per window");
        harness.Handler.Requests.Should().HaveCount(1, "the short-circuit must not reach GitHub again");

        second.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        second.Owners.Should().BeEmpty();
    }

    [Fact]
    public async Task A_degraded_answer_keeps_the_login_github_returned_earlier()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var reachable = true;
        var harness = CreateService(session, NewUser(),
            tokenScript: _ => new("ghu_test_ok", GitHubTokenState.Ok),
            responder: (request, _) => reachable ? GitHub(request) : new HttpResponseMessage(HttpStatusCode.BadGateway));

        (await harness.Access.GetVisibilityAsync()).TokenState.Should().Be(GitHubTokenState.Ok);
        await harness.Access.InvalidateAsync(); // drops the owners cache, as Resync does
        reachable = false;
        var degraded = await harness.Access.GetVisibilityAsync();

        degraded.TokenState.Should().Be(GitHubTokenState.Unavailable);
        degraded.Owners.Should().BeEquivalentTo([OwnLogin]);
    }

    [Fact]
    public async Task A_401_on_the_freshly_refreshed_token_means_the_authorization_is_gone()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: forced => new(forced ? "ghu_test_refreshed" : "ghu_test_stale", GitHubTokenState.Ok),
            responder: (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)); // refuses BOTH tokens

        var visibility = await harness.Access.GetVisibilityAsync();

        visibility.Owners.Should().BeEmpty();
        visibility.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        harness.Tokens.ForcedCalls.Should().Be(1, "exactly one forced refresh — no retry loops");
        harness.Handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reauth_required_from_the_token_service_short_circuits_without_any_network_call()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: _ => new(null, GitHubTokenState.ReauthRequired),
            responder: (_, _) => throw new InvalidOperationException("no network call expected"));

        var visibility = await harness.Access.GetVisibilityAsync();

        visibility.Owners.Should().BeEmpty();
        visibility.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        harness.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Transient_github_failure_stays_unavailable_and_uncached()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: _ => new("ghu_test_ok", GitHubTokenState.Ok),
            responder: (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var visibility = await harness.Access.GetVisibilityAsync();

        visibility.Owners.Should().BeEmpty();
        visibility.TokenState.Should().Be(GitHubTokenState.Unavailable);
        // 502 is not 401: no forced refresh, no burned refresh token.
        harness.Tokens.ForcedCalls.Should().Be(0);
    }

    [Fact]
    public async Task Success_is_cached_and_served_as_ok_without_requerying()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var user = NewUser();
        var harness = CreateService(session, user,
            tokenScript: _ => new("ghu_test_ok", GitHubTokenState.Ok),
            responder: (request, _) => GitHub(request));

        var first = await harness.Access.GetVisibilityAsync();
        var second = await harness.Access.GetVisibilityAsync();

        first.TokenState.Should().Be(GitHubTokenState.Ok);
        second.Should().BeEquivalentTo(first);
        harness.Handler.Requests.Should().HaveCount(2, "/user and /user/installations once; the 5-minute owners cache serves the second call");
    }

    [Fact]
    public async Task The_own_installation_is_cleared_by_github_id_and_an_account_named_like_the_user_is_untouched()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            // Own account, under a stale login: matched by GitHub id, so it is still found.
            await seed.StoreAsync(new Account { GitHubId = OwnGitHubId, Login = "old-login", InstallationId = 1 },
                Account.DocumentId(EForgeProvider.GitHub, OwnGitHubId));
            // Someone else's account whose login equals the editable user name.
            await seed.StoreAsync(new Account { GitHubId = 777, Login = EditableUserName, InstallationId = 2 },
                Account.DocumentId(EForgeProvider.GitHub, 777));
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        var harness = CreateService(session, NewUser(),
            tokenScript: _ => new("ghu_test_ok", GitHubTokenState.Ok),
            responder: (request, _) => GitHub(request)); // installations list only MintPlayer

        await harness.Access.GetVisibilityAsync();

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, OwnGitHubId))).InstallationId.HasValue.Should().BeFalse();
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, 777))).InstallationId.Should().Be(2);
    }
}
