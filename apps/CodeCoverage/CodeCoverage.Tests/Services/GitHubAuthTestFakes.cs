using System.Collections.Concurrent;
using System.Security.Claims;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Shared fakes for the GitHub token-refresh tests: an in-memory user/token
/// store (real <see cref="UserManager{TUser}"/> on top), a scripted HTTP
/// handler, a fixed clock, and minimal hosting-environment stand-ins.
/// </summary>
internal static class GitHubAuthTestFakes
{
    public static UserManager<SparkUser> UserManagerOver(InMemoryUserStore store) =>
        new(store, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<UserManager<SparkUser>>.Instance);

    public static IConfiguration TestConfiguration(string environmentName = "Development") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Test-only placeholder credentials — not real secrets.
            [$"GitHub:{environmentName}:ClientId"] = "test-client-id",
            [$"GitHub:{environmentName}:ClientSecret"] = "test-client-secret",
        }).Build();

    /// <summary>A GitHub refresh-grant success body (GitHub's real shape, fake values).</summary>
    public static string RefreshGrantSuccess(string accessToken, string refreshToken, int expiresInSeconds = 28800) => $$"""
        {
          "access_token": "{{accessToken}}",
          "expires_in": {{expiresInSeconds}},
          "refresh_token": "{{refreshToken}}",
          "refresh_token_expires_in": 15811200,
          "token_type": "bearer",
          "scope": ""
        }
        """;

    public static ClaimsPrincipal PrincipalFor(SparkUser user) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id!),
            new Claim(ClaimTypes.Name, user.UserName!),
        ], authenticationType: "Test"));
}

internal sealed class InMemoryUserStore : IUserStore<SparkUser>, IUserAuthenticationTokenStore<SparkUser>
{
    private readonly ConcurrentDictionary<string, SparkUser> users = new();
    private readonly ConcurrentDictionary<(string UserId, string Provider, string Name), string?> tokens = new();

    public InMemoryUserStore Add(SparkUser user)
    {
        users[user.Id!] = user;
        return this;
    }

    public InMemoryUserStore WithToken(SparkUser user, string name, string value)
    {
        tokens[(user.Id!, "GitHub", name)] = value;
        return this;
    }

    public string? StoredToken(SparkUser user, string name) =>
        tokens.TryGetValue((user.Id!, "GitHub", name), out var value) ? value : null;

    Task<string> IUserStore<SparkUser>.GetUserIdAsync(SparkUser user, CancellationToken ct) => Task.FromResult(user.Id!);
    Task<string?> IUserStore<SparkUser>.GetUserNameAsync(SparkUser user, CancellationToken ct) => Task.FromResult(user.UserName);
    Task IUserStore<SparkUser>.SetUserNameAsync(SparkUser user, string? userName, CancellationToken ct) => Task.CompletedTask;
    Task<string?> IUserStore<SparkUser>.GetNormalizedUserNameAsync(SparkUser user, CancellationToken ct) => Task.FromResult(user.UserName?.ToUpperInvariant());
    Task IUserStore<SparkUser>.SetNormalizedUserNameAsync(SparkUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
    Task<IdentityResult> IUserStore<SparkUser>.CreateAsync(SparkUser user, CancellationToken ct)
    {
        users[user.Id!] = user;
        return Task.FromResult(IdentityResult.Success);
    }
    Task<IdentityResult> IUserStore<SparkUser>.UpdateAsync(SparkUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    Task<IdentityResult> IUserStore<SparkUser>.DeleteAsync(SparkUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    Task<SparkUser?> IUserStore<SparkUser>.FindByIdAsync(string userId, CancellationToken ct) =>
        Task.FromResult(users.TryGetValue(userId, out var user) ? user : null);
    Task<SparkUser?> IUserStore<SparkUser>.FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        Task.FromResult(users.Values.FirstOrDefault(u => string.Equals(u.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase)));
    void IDisposable.Dispose() { }

    Task IUserAuthenticationTokenStore<SparkUser>.SetTokenAsync(SparkUser user, string loginProvider, string name, string? value, CancellationToken ct)
    {
        tokens[(user.Id!, loginProvider, name)] = value;
        return Task.CompletedTask;
    }
    Task IUserAuthenticationTokenStore<SparkUser>.RemoveTokenAsync(SparkUser user, string loginProvider, string name, CancellationToken ct)
    {
        tokens.TryRemove((user.Id!, loginProvider, name), out _);
        return Task.CompletedTask;
    }
    Task<string?> IUserAuthenticationTokenStore<SparkUser>.GetTokenAsync(SparkUser user, string loginProvider, string name, CancellationToken ct) =>
        Task.FromResult(tokens.TryGetValue((user.Id!, loginProvider, name), out var value) ? value : null);
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> responder;
    public readonly ConcurrentQueue<(Uri? Uri, string? AuthorizationParameter, string? Body)> Requests = new();
    /// <summary>Artificial latency, so concurrency tests actually overlap.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        => this.responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.RequestUri, request.Headers.Authorization?.Parameter, body));
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        return responder(request, body);
    }

    public static HttpResponseMessage Json(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class SingleClientHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class FixedTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "CodeCoverage.Tests";
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FakeHttpContextAccessor(ClaimsPrincipal principal) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = new DefaultHttpContext { User = principal };
}

/// <summary>Scripted <see cref="IGitHubUserTokenService"/> for access-service tests.</summary>
internal sealed class ScriptedTokenService(Func<bool, GitHubUserToken> script) : IGitHubUserTokenService
{
    public int Calls;
    public int ForcedCalls;

    public Task<GitHubUserToken> GetAccessTokenAsync(SparkUser user, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        if (forceRefresh)
            Interlocked.Increment(ref ForcedCalls);
        return Task.FromResult(script(forceRefresh));
    }
}

/// <summary>Scripted <see cref="IGitHubAccessService"/> for controller tests.</summary>
internal sealed class ScriptedAccessService(GitHubVisibility visibility) : IGitHubAccessService
{
    public Task<GitHubVisibility> GetVisibilityAsync(CancellationToken ct = default) => Task.FromResult(visibility);
    public async Task<string[]> GetAllowedOwnersAsync(CancellationToken ct = default) => (await GetVisibilityAsync(ct)).Owners;
    public async Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken ct = default)
        => (await GetVisibilityAsync(ct)).Owners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    public Task InvalidateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
