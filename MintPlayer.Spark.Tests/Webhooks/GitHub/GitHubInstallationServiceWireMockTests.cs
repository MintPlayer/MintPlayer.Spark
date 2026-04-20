using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Tests.Webhooks.GitHub._Infrastructure;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// End-to-end tests for installation token refresh + 401 retry, using a local WireMock
/// server to stand in for api.github.com. Covers the concurrency and retry contract from
/// docs/PRD-GitHubAppClientCache.md that the reflection-based tests in
/// GitHubInstallationServiceTests cannot reach.
/// </summary>
public class GitHubInstallationServiceWireMockTests : IAsyncLifetime
{
    private const long InstallationId = 42L;

    private WireMockServer _server = null!;
    private GitHubInstallationService _service = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        var factory = new WireMockGitHubClientFactory(new Uri(_server.Url!));
        var opts = Options.Create(new GitHubWebhooksOptions
        {
            ClientId = "Iv23liFAKEAPPID",
            PrivateKeyPem = GenerateRsaPrivateKeyPem(),
        });
        _service = new GitHubInstallationService(factory, opts);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _service.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static ConcurrentDictionary<long, AccessToken> GetTokenCache(GitHubInstallationService service)
    {
        var field = typeof(GitHubInstallationService)
            .GetField("_installationTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ConcurrentDictionary<long, AccessToken>)field.GetValue(service)!;
    }

    private static void StubAccessTokenResponse(WireMockServer server, string token, DateTimeOffset expiresAt)
    {
        server
            .Given(Request.Create()
                .WithPath($"/api/v3/app/installations/{InstallationId}/access_tokens")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(
                    "{\"token\":\"" + token +
                    "\",\"expires_at\":\"" + expiresAt.ToString("O") +
                    "\",\"permissions\":{}}"));
    }

    [Fact]
    public async Task Concurrent_refresh_200_callers_result_in_exactly_one_access_token_mint()
    {
        StubAccessTokenResponse(_server, "ghs_fresh", DateTimeOffset.UtcNow.AddHours(1));

        var tasks = Enumerable.Range(0, 200).Select(_ =>
            _service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None)).ToArray();
        var tokens = await Task.WhenAll(tasks);

        tokens.Should().OnlyContain(t => t.Token == "ghs_fresh");
        var mintCalls = _server.FindLogEntries(
            Request.Create().WithPath($"/api/v3/app/installations/{InstallationId}/access_tokens").UsingPost());
        mintCalls.Count.Should().Be(1, "the SemaphoreSlim gate serializes refresh and the re-check inside the gate skips the second mint");
    }

    [Fact]
    public async Task Stale_token_triggers_one_new_mint_and_the_cache_updates_to_the_fresh_token()
    {
        // Seed an about-to-expire token — the service must treat it as stale and mint fresh.
        GetTokenCache(_service)[InstallationId] = new AccessToken("stale", DateTimeOffset.UtcNow.AddSeconds(10));
        StubAccessTokenResponse(_server, "ghs_refreshed", DateTimeOffset.UtcNow.AddHours(1));

        var token = await _service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None);

        token.Token.Should().Be("ghs_refreshed");
        GetTokenCache(_service)[InstallationId].Token.Should().Be("ghs_refreshed");
        var mintCalls = _server.FindLogEntries(
            Request.Create().WithPath($"/api/v3/app/installations/{InstallationId}/access_tokens").UsingPost());
        mintCalls.Count.Should().Be(1);
    }

    [Fact]
    public async Task Fast_path_hit_does_not_mint_a_fresh_token()
    {
        GetTokenCache(_service)[InstallationId] = new AccessToken("ghs_cached", DateTimeOffset.UtcNow.AddHours(1));

        var token = await _service.GetOrCreateInstallationTokenAsync(InstallationId, CancellationToken.None);

        token.Token.Should().Be("ghs_cached");
        var mintCalls = _server.FindLogEntries(
            Request.Create().WithPath($"/api/v3/app/installations/{InstallationId}/access_tokens").UsingPost());
        mintCalls.Count.Should().Be(0, "fast-path cache hit never goes through the refresh gate");
    }


    [Fact]
    public async Task Installation_REST_call_that_returns_401_triggers_a_token_refresh_and_a_single_retry()
    {
        GetTokenCache(_service)[InstallationId] = new AccessToken("stale-token", DateTimeOffset.UtcNow.AddHours(1));
        StubAccessTokenResponse(_server, "fresh-token", DateTimeOffset.UtcNow.AddHours(1));

        // Scenario: first GET → 401. Second GET (after token refresh) → 200.
        _server
            .Given(Request.Create().WithPath("/repos/octocat/hello-world").UsingGet())
            .InScenario("repo-get-retry").WhenStateIs(null).WillSetStateTo("after-401")
            .RespondWith(Response.Create().WithStatusCode(401));
        _server
            .Given(Request.Create().WithPath("/repos/octocat/hello-world").UsingGet())
            .InScenario("repo-get-retry").WhenStateIs("after-401")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":1,\"name\":\"hello-world\",\"full_name\":\"octocat/hello-world\"}"));

        var client = await _service.CreateInstallationClientAsync(InstallationId);
        var repo = await client.Repository.Get("octocat", "hello-world");

        repo.Name.Should().Be("hello-world");

        var requests = _server.FindLogEntries(Request.Create().WithPath("/repos/octocat/hello-world").UsingGet());
        requests.Count.Should().Be(2, "the handler retries exactly once after the 401");
        var mintCalls = _server.FindLogEntries(
            Request.Create().WithPath($"/api/v3/app/installations/{InstallationId}/access_tokens").UsingPost());
        mintCalls.Count.Should().Be(1, "the 401 forces exactly one fresh-token mint");
    }

    [Fact]
    public async Task Persistent_401_after_refresh_does_not_retry_a_second_time()
    {
        GetTokenCache(_service)[InstallationId] = new AccessToken("stale", DateTimeOffset.UtcNow.AddHours(1));
        StubAccessTokenResponse(_server, "still-broken", DateTimeOffset.UtcNow.AddHours(1));

        // Every REST call returns 401 — both the original AND the retry with the fresh token.
        _server
            .Given(Request.Create().WithPath("/repos/octocat/hello-world").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        var client = await _service.CreateInstallationClientAsync(InstallationId);
        var act = async () => await client.Repository.Get("octocat", "hello-world");

        await act.Should().ThrowAsync<AuthorizationException>();

        var requests = _server.FindLogEntries(Request.Create().WithPath("/repos/octocat/hello-world").UsingGet());
        requests.Count.Should().Be(2, "exactly one retry — no infinite loop even when the refresh did not resolve the 401");
    }

    private static string GenerateRsaPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var base64 = Convert.ToBase64String(pkcs8, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{base64}\n-----END PRIVATE KEY-----";
    }
}
