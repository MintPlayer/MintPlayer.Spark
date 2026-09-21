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

    /// <summary>
    /// A failed lookup degrades to the viewer's own login, and the <em>failure</em> is remembered
    /// briefly while the degraded owner set is not.
    /// </summary>
    /// <remarks>
    /// ⚠️ This test previously asserted that nothing at all was cached, and the negative cache added
    /// with <c>IForgeAccessService</c> (D6c) made that false without anyone noticing — the two halves
    /// were committed in the same milestone and the suite was not run between them. The distinction
    /// that actually matters is preserved and is what this now pins: the failure <em>state</em> is
    /// cached so an outage costs one attempt per window rather than one per request, while the
    /// degraded owner set is recomputed from the principal every time, so nothing stale is ever
    /// served as though it were authoritative.
    /// </remarks>
    [Fact]
    public async Task Refresh_failure_after_401_degrades_to_own_login_and_remembers_only_the_failure()
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

        // The second call short-circuits on the remembered failure rather than re-attempting:
        // 2 token calls (initial + forced) for the first, none for the second.
        harness.Tokens.Calls.Should().Be(2, "the failure is remembered, so an outage costs one attempt per window");
        harness.Handler.Requests.Should().HaveCount(1, "the short-circuit must not reach GitHub again");

        // But the answer is rebuilt, not replayed: the same degraded state and the same own-login
        // set, derived from the principal rather than served from a cache entry.
        second.TokenState.Should().Be(GitHubTokenState.ReauthRequired);
        // ...and the degraded set is recomputed, never cached.
        second.Owners.Should().BeEquivalentTo(["pieterjan"]);
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
