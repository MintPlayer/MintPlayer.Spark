using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using MintPlayer.Spark.Webhooks.GitHub.Services.Internal;
using NSubstitute;
using Octokit;
using Octokit.Internal;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

public class TokenRefreshingDecoratorsTests
{
    private const long InstallationId = 42L;

    private static GitHubInstallationService NewService()
        => new(Options.Create(new GitHubWebhooksOptions()));

    private static ConcurrentDictionary<long, AccessToken> GetCache(GitHubInstallationService service)
    {
        var field = typeof(GitHubInstallationService)
            .GetField("_installationTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ConcurrentDictionary<long, AccessToken>)field.GetValue(service)!;
    }

    private static void Seed(GitHubInstallationService service, long id, string token, DateTimeOffset expiresAt)
        => GetCache(service)[id] = new AccessToken(token, expiresAt);

    // ---- DynamicInstallationCredentialStore --------------------------------

    [Fact]
    public async Task DynamicInstallationCredentialStore_returns_the_currently_cached_token_as_Octokit_Credentials()
    {
        using var service = NewService();
        Seed(service, InstallationId, "cached-abc", DateTimeOffset.UtcNow.AddHours(1));
        var store = new DynamicInstallationCredentialStore(InstallationId, service);

        var credentials = await store.GetCredentials();

        credentials.Password.Should().Be("cached-abc");
    }

    [Fact]
    public async Task DynamicInstallationGraphQLCredentialStore_returns_the_currently_cached_token_string()
    {
        using var service = NewService();
        Seed(service, InstallationId, "graphql-token", DateTimeOffset.UtcNow.AddHours(1));
        var store = new DynamicInstallationGraphQLCredentialStore(InstallationId, service);

        var token = await store.GetCredentials(CancellationToken.None);

        token.Should().Be("graphql-token");
    }

    // ---- TokenRefreshingHttpClient -----------------------------------------

    [Fact]
    public async Task TokenRefreshingHttpClient_passes_200_responses_through_without_invalidating_the_cache()
    {
        using var service = NewService();
        Seed(service, InstallationId, "good-token", DateTimeOffset.UtcNow.AddHours(1));
        var inner = new StubOctokitHttpClient(HttpStatusCode.OK);

        var client = new TokenRefreshingHttpClient(inner, InstallationId, service);
        var response = await client.Send(new StubOctokitRequest(), CancellationToken.None, _ => _);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetCache(service).ContainsKey(InstallationId).Should().BeTrue();
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TokenRefreshingHttpClient_on_401_invalidates_the_cache_before_attempting_a_refresh()
    {
        // Service has no configured PEM/ClientId — the refresh attempt will throw.
        // That's fine: we only care that InvalidateInstallation ran first.
        using var service = NewService();
        Seed(service, InstallationId, "stale", DateTimeOffset.UtcNow.AddHours(1));
        var inner = new StubOctokitHttpClient(HttpStatusCode.Unauthorized);

        var client = new TokenRefreshingHttpClient(inner, InstallationId, service);
        var act = async () => await client.Send(new StubOctokitRequest(), CancellationToken.None, _ => _);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PrivateKeyPem*");
        GetCache(service).ContainsKey(InstallationId).Should()
            .BeFalse("the 401 handler invalidates the cache *before* asking for a fresh token");
    }

    private sealed class StubOctokitHttpClient(HttpStatusCode statusCode) : IHttpClient
    {
        public int CallCount { get; private set; }

        public Task<IResponse> Send(IRequest request, CancellationToken cancellationToken, Func<object, object> preprocessResponseBody)
        {
            CallCount++;
            return Task.FromResult<IResponse>(new StubOctokitResponse(statusCode));
        }

        public void SetRequestTimeout(TimeSpan timeout) { }
        public void Dispose() { }
    }

    private sealed class StubOctokitResponse(HttpStatusCode statusCode) : IResponse
    {
        public object? Body => null;
        public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public ApiInfo ApiInfo => null!;
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string ContentType => "application/json";
    }

    private sealed class StubOctokitRequest : IRequest
    {
        public object? Body { get; set; }
        public Dictionary<string, string> Headers { get; } = new();
        public HttpMethod Method { get; set; } = HttpMethod.Get;
        public Dictionary<string, string> Parameters { get; } = new();
        public Uri BaseAddress { get; set; } = new("https://api.github.com");
        public Uri Endpoint { get; set; } = new("/ping", UriKind.Relative);
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
        public string ContentType { get; set; } = "application/json";
    }

    // ---- TokenRefreshingHandler (GraphQL/.NET HttpClient side) -------------

    [Fact]
    public async Task TokenRefreshingHandler_passes_200_responses_through_without_invalidating_the_cache()
    {
        using var service = NewService();
        Seed(service, InstallationId, "good-token", DateTimeOffset.UtcNow.AddHours(1));

        var inner = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new TokenRefreshingHandler(InstallationId, service) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.github.com/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetCache(service).ContainsKey(InstallationId).Should().BeTrue();
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TokenRefreshingHandler_on_401_invalidates_the_cache_before_attempting_a_refresh()
    {
        using var service = NewService();
        Seed(service, InstallationId, "stale", DateTimeOffset.UtcNow.AddHours(1));

        var inner = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new TokenRefreshingHandler(InstallationId, service) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("https://api.github.com/ping");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PrivateKeyPem*");
        GetCache(service).ContainsKey(InstallationId).Should().BeFalse();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request, cancellationToken));
        }
    }
}
