using System.Net;
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
public class GitHubAccessServiceRefreshTests : CoverageRavenTest
{
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
        UserName = "pieterjan",
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
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, InstallationsJson));

        var visibility = await harness.Access.GetVisibilityAsync();

        visibility.TokenState.Should().Be(GitHubTokenState.Ok);
        visibility.Owners.Should().BeEquivalentTo(["MintPlayer", "pieterjan"]);
        harness.Tokens.ForcedCalls.Should().Be(1);
        harness.Handler.Requests.Should().HaveCount(2, "the stale token 401s once, the refreshed token succeeds");
    }

    [Fact]
    public async Task Refresh_failure_after_401_degrades_to_own_login_with_reauth_required_and_caches_nothing()
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

        first.Owners.Should().BeEquivalentTo(["pieterjan"]);
        first.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        // Degraded results are never cached: the second call re-consults the
        // token service instead of replaying a cached owner list.
        second.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        harness.Tokens.Calls.Should().Be(4, "two visibility calls × (initial + forced) — nothing was cached");
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

        visibility.Owners.Should().BeEquivalentTo(["pieterjan"]);
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

        visibility.Owners.Should().BeEquivalentTo(["pieterjan"]);
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

        visibility.Owners.Should().BeEquivalentTo(["pieterjan"]);
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
            responder: (_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, InstallationsJson));

        var first = await harness.Access.GetVisibilityAsync();
        var second = await harness.Access.GetVisibilityAsync();

        first.TokenState.Should().Be(GitHubTokenState.Ok);
        second.Should().BeEquivalentTo(first);
        harness.Handler.Requests.Should().ContainSingle("the 5-minute owners cache serves the second call");
    }
}
