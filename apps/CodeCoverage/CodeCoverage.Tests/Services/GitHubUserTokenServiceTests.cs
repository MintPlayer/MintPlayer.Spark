using System.Net;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The decision table from docs/reauth-on-401.md §4: fresh / near-expiry /
/// expired / no-refresh-token / refresh-refused / transient-failure, plus the
/// single-flight guarantee (refresh tokens are single-use — N concurrent
/// callers must produce exactly one refresh call).
/// </summary>
public class GitHubUserTokenServiceTests
{
    private static SparkUser NewUser() => new()
    {
        // Unique per test: the single-flight gate is static, keyed by user id.
        Id = $"SparkUsers/{Guid.NewGuid():N}",
        UserName = "pieterjan",
    };

    private static IGitHubUserTokenService CreateService(
        InMemoryUserStore store, StubHttpMessageHandler handler, FixedTimeProvider time)
    {
        // Resolve through DI so the source-generated constructor's parameter
        // order is never load-bearing in tests.
        var services = new ServiceCollection();
        services.AddSingleton(GitHubAuthTestFakes.UserManagerOver(store));
        services.AddSingleton<IHttpClientFactory>(new SingleClientHttpFactory(handler));
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IGitHubUserTokenService, GitHubUserTokenService>();
        return services.BuildServiceProvider().GetRequiredService<IGitHubUserTokenService>();
    }

    private static StubHttpMessageHandler GrantHandler(string newAccess = "ghu_test_new", string newRefresh = "ghr_test_new")
        => new((_, _) => StubHttpMessageHandler.Json(
            HttpStatusCode.OK, GitHubAuthTestFakes.RefreshGrantSuccess(newAccess, newRefresh)));

    [Fact]
    public async Task Fresh_token_is_returned_without_any_network_call()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_fresh")
            .WithToken(user, "refresh_token", "ghr_test")
            .WithToken(user, "expires_at", (time.UtcNow + TimeSpan.FromHours(2)).ToString("o"));
        var handler = GrantHandler();

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user);

        result.Should().Be(new GitHubUserToken("ghu_test_fresh", GitHubTokenState.Ok));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_expiry_means_a_non_expiring_token_and_no_refresh()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_nonexpiring");
        var handler = GrantHandler();

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user);

        result.Should().Be(new GitHubUserToken("ghu_test_nonexpiring", GitHubTokenState.Ok));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Near_expiry_refreshes_and_persists_every_returned_token()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_old")
            .WithToken(user, "refresh_token", "ghr_test_old")
            .WithToken(user, "expires_at", (time.UtcNow + TimeSpan.FromMinutes(2)).ToString("o")); // inside the 5-min skew
        var handler = GrantHandler("ghu_test_new", "ghr_test_new");

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user);

        result.Should().Be(new GitHubUserToken("ghu_test_new", GitHubTokenState.Ok));
        handler.Requests.Should().ContainSingle()
            .Which.Body.Should().Contain("grant_type=refresh_token").And.Contain("ghr_test_old");
        store.StoredToken(user, "access_token").Should().Be("ghu_test_new");
        store.StoredToken(user, "refresh_token").Should().Be("ghr_test_new");
        DateTimeOffset.Parse(store.StoredToken(user, "expires_at")!)
            .Should().Be(time.UtcNow + TimeSpan.FromSeconds(28800));
    }

    [Fact]
    public async Task Force_refresh_ignores_a_comfortable_expiry()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_looks_fine")
            .WithToken(user, "refresh_token", "ghr_test")
            .WithToken(user, "expires_at", (time.UtcNow + TimeSpan.FromHours(7)).ToString("o"));
        var handler = GrantHandler("ghu_test_forced");

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user, forceRefresh: true);

        result.Should().Be(new GitHubUserToken("ghu_test_forced", GitHubTokenState.Ok));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Expired_without_a_refresh_token_is_reauth_required_with_no_network_call()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_expired")
            .WithToken(user, "expires_at", (time.UtcNow - TimeSpan.FromHours(1)).ToString("o"));
        var handler = GrantHandler();

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user);

        result.Should().Be(new GitHubUserToken(null, GitHubTokenState.ReauthRequired));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task No_stored_access_token_at_all_is_reauth_required()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user);
        var handler = GrantHandler();

        var result = await CreateService(store, handler, time).GetAccessTokenAsync(user);

        result.State.Should().Be(GitHubTokenState.ReauthRequired);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_grant_is_reauth_required_and_the_dead_refresh_token_is_not_retried()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_expired")
            .WithToken(user, "refresh_token", "ghr_test_burned")
            .WithToken(user, "expires_at", (time.UtcNow - TimeSpan.FromHours(1)).ToString("o"));
        // GitHub reports grant errors in a 200 body, not a status code.
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(
            HttpStatusCode.OK, """{"error":"bad_refresh_token","error_description":"The refresh token passed is incorrect or expired."}"""));
        var service = CreateService(store, handler, time);

        var first = await service.GetAccessTokenAsync(user);
        var second = await service.GetAccessTokenAsync(user);

        first.State.Should().Be(GitHubTokenState.ReauthRequired);
        second.State.Should().Be(GitHubTokenState.ReauthRequired);
        // The refused ghr_ value is remembered — one grant call total, not two.
        handler.Requests.Should().ContainSingle();
        store.StoredToken(user, "access_token").Should().Be("ghu_test_expired", "a refused grant must not clobber stored tokens");
    }

    [Fact]
    public async Task Transient_grant_failure_is_unavailable_and_a_later_call_retries()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_expired")
            .WithToken(user, "refresh_token", "ghr_test_still_good")
            .WithToken(user, "expires_at", (time.UtcNow - TimeSpan.FromHours(1)).ToString("o"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var service = CreateService(store, handler, time);

        var first = await service.GetAccessTokenAsync(user);
        var second = await service.GetAccessTokenAsync(user);

        first.State.Should().Be(GitHubTokenState.Unavailable);
        second.State.Should().Be(GitHubTokenState.Unavailable);
        // Unlike a refusal, a transient failure leaves the (still-valid)
        // refresh token eligible: both calls hit the grant endpoint.
        handler.Requests.Should().HaveCount(2);
        store.StoredToken(user, "refresh_token").Should().Be("ghr_test_still_good");
    }

    [Fact]
    public async Task Concurrent_callers_produce_exactly_one_refresh_and_share_the_winner_token()
    {
        var time = new FixedTimeProvider();
        var user = NewUser();
        var store = new InMemoryUserStore().Add(user)
            .WithToken(user, "access_token", "ghu_test_expired")
            .WithToken(user, "refresh_token", "ghr_test_single_use")
            .WithToken(user, "expires_at", (time.UtcNow - TimeSpan.FromMinutes(1)).ToString("o"));
        var handler = GrantHandler("ghu_test_winner", "ghr_test_rotated");
        handler.Delay = TimeSpan.FromMilliseconds(150); // hold the winner in-flight so the others queue on the gate
        var service = CreateService(store, handler, time);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => service.GetAccessTokenAsync(user))));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be(GitHubTokenState.Ok);
            r.AccessToken.Should().Be("ghu_test_winner");
        });
        handler.Requests.Should().ContainSingle("refresh tokens are single-use — two spends would burn the session");
    }
}
