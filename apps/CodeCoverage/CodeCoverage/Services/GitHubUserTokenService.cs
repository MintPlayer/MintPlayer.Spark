using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Services;

[Register(typeof(IGitHubUserTokenService), ServiceLifetime.Scoped)]
public partial class GitHubUserTokenService : IGitHubUserTokenService
{
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<GitHubUserTokenService> logger;

    private const string Provider = "GitHub";
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string TokenTypeName = "token_type";
    private const string ExpiresAtName = "expires_at";

    /// <summary>Refresh this far before the stored expiry, so a token never dies mid-request.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    // Refresh tokens are SINGLE-USE (GitHub rotates them): two concurrent
    // requests must not both spend the same ghr_ value. One gate per user id;
    // the gate also remembers the winner's fresh tokens, because the loser's
    // RavenDB session was opened before the winner saved and would read stale
    // values back. The service is scoped, so this state must be static.
    private static readonly ConcurrentDictionary<string, UserTokenGate> gates = new();

    private sealed class UserTokenGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public string? AccessToken;
        public string? RefreshToken;
        /// <summary>The refresh token GitHub refused — remembered so we don't hammer the grant endpoint with a value we know is dead.</summary>
        public string? RefusedRefreshToken;
    }

    public async Task<GitHubUserToken> GetAccessTokenAsync(SparkUser user, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var storedAccess = await userManager.GetAuthenticationTokenAsync(user, Provider, AccessTokenName);
        if (string.IsNullOrEmpty(storedAccess))
            return new(null, GitHubTokenState.ReauthRequired);

        if (!forceRefresh && !IsExpiredOrNear(await userManager.GetAuthenticationTokenAsync(user, Provider, ExpiresAtName)))
            return new(storedAccess, GitHubTokenState.Ok);

        var gate = gates.GetOrAdd(user.Id!, _ => new UserTokenGate());
        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            // Someone else refreshed while we waited: their token is newer than
            // the one we (or our caller's failed request) were holding.
            if (gate.AccessToken is not null && gate.AccessToken != storedAccess)
                return new(gate.AccessToken, GitHubTokenState.Ok);

            var refreshToken = gate.RefreshToken
                ?? await userManager.GetAuthenticationTokenAsync(user, Provider, RefreshTokenName);
            if (string.IsNullOrEmpty(refreshToken))
            {
                logger.LogWarning("GitHub token for user {UserId} needs refresh but no refresh token is stored — reauth required", user.Id);
                return new(null, GitHubTokenState.ReauthRequired);
            }

            // This exact value already came back refused (revoked authorization,
            // or burned by a lost race with another instance). Only a new
            // sign-in — which overwrites the stored tokens — clears this.
            if (refreshToken == gate.RefusedRefreshToken)
                return new(null, GitHubTokenState.ReauthRequired);

            return await RefreshAsync(user, gate, refreshToken, cancellationToken);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    private async Task<GitHubUserToken> RefreshAsync(SparkUser user, UserTokenGate gate, string refreshToken, CancellationToken cancellationToken)
    {
        var envPrefix = environment.EnvironmentName;
        var clientId = configuration[$"GitHub:{envPrefix}:ClientId"];
        var clientSecret = configuration[$"GitHub:{envPrefix}:ClientSecret"];
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogError("Cannot refresh GitHub token for user {UserId}: GitHub:{Env}:ClientId/ClientSecret not configured", user.Id, envPrefix);
            return new(null, GitHubTokenState.Unavailable);
        }

        JsonDocument payload;
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                }),
            };
            request.Headers.Accept.ParseAdd("application/json");

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 5xx/edge failures are transient: report "don't know", let a
                // later request try the same (still-valid) refresh token again.
                logger.LogWarning("GitHub refresh grant returned {StatusCode} for user {UserId}", response.StatusCode, user.Id);
                return new(null, GitHubTokenState.Unavailable);
            }

            payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GitHub refresh grant failed for user {UserId}", user.Id);
            return new(null, GitHubTokenState.Unavailable);
        }

        using (payload)
        {
            // GitHub's OAuth token endpoint reports errors in a 200 body
            // ({"error":"bad_refresh_token", ...}), not via status codes.
            if (payload.RootElement.TryGetProperty("error", out var errorNode))
            {
                gate.RefusedRefreshToken = refreshToken;
                logger.LogWarning("GitHub refused the refresh grant for user {UserId}: {Error} — reauth required", user.Id, errorNode.GetString());
                return new(null, GitHubTokenState.ReauthRequired);
            }

            var newAccess = payload.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(newAccess))
            {
                logger.LogWarning("GitHub refresh grant returned no access token for user {UserId}", user.Id);
                return new(null, GitHubTokenState.Unavailable);
            }

            var newRefresh = payload.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var tokenType = payload.RootElement.TryGetProperty("token_type", out var tt) ? tt.GetString() : null;
            string? expiresAt = null;
            if (payload.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var expiresIn))
                expiresAt = (timeProvider.GetUtcNow() + TimeSpan.FromSeconds(expiresIn)).ToString("o");

            // Persist BEFORE returning: the old refresh token is already burned,
            // so the new pair must survive this request (same "o"-formatted
            // names/values the OAuth handler writes with SaveTokens).
            await userManager.SetAuthenticationTokenAsync(user, Provider, AccessTokenName, newAccess);
            if (newRefresh is not null)
                await userManager.SetAuthenticationTokenAsync(user, Provider, RefreshTokenName, newRefresh);
            if (tokenType is not null)
                await userManager.SetAuthenticationTokenAsync(user, Provider, TokenTypeName, tokenType);
            if (expiresAt is not null)
                await userManager.SetAuthenticationTokenAsync(user, Provider, ExpiresAtName, expiresAt);

            gate.AccessToken = newAccess;
            gate.RefreshToken = newRefresh;
            gate.RefusedRefreshToken = null;

            logger.LogInformation("Silently refreshed the GitHub user token for user {UserId}", user.Id);
            return new(newAccess, GitHubTokenState.Ok);
        }
    }

    private bool IsExpiredOrNear(string? expiresAt)
    {
        // No stored expiry means the App issues non-expiring tokens — nothing to refresh.
        if (string.IsNullOrEmpty(expiresAt) || !DateTimeOffset.TryParse(expiresAt, out var expiry))
            return false;
        return timeProvider.GetUtcNow() >= expiry - ExpirySkew;
    }
}
