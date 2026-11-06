using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <inheritdoc cref="ISparkOneShotOAuth"/>
internal sealed partial class SparkOneShotOAuth : ISparkOneShotOAuth
{
    /// <summary>The client method <c>ng-spark/auth</c> registers.</summary>
    internal const string ClientMethod = "oauth.authorize";

    /// <summary>The callback's path; the OAuth client's registered callback URL must cover it.</summary>
    internal const string CallbackPath = "/spark/auth/oauth-callback";

    /// <summary>The DataProtection purpose of the <c>state</c> parameter.</summary>
    internal const string StatePurpose = "MintPlayer.Spark.Authorization.OneShotOAuth.State";

    [Inject] private readonly IManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly SparkOneShotOAuthStore store;
    [Inject] private readonly IDataProtectionProvider dataProtection;
    [Inject] private readonly IOptions<SparkOneShotOAuthOptions> options;

    public async Task<SparkOneShotOAuthOutcome> AuthorizeAsync(string client, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Clients.TryGetValue(client, out var registration))
            throw new InvalidOperationException(
                $"No one-shot OAuth client is registered as '{client}'. Register it with spark.AddOneShotOAuthClient(\"{client}\", ...).");

        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Failure(SparkOneShotOAuthErrors.Failed);

        // ⚠️ The attempt is made inside the factory, which runs only while the step is unanswered: the
        // action re-runs from the top on pass 2, and a second attempt there would be one the browser never saw.
        await manager.Retry.Invoke(ClientMethod, () => Task.FromResult<object?>(Begin(client, registration, userId, scopes)));

        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.Value is not { ValueKind: JsonValueKind.Object } value)
            return Failure(SparkOneShotOAuthErrors.Cancelled);

        if (value.TryGetProperty("error", out var error))
            return Failure(error.ValueKind == JsonValueKind.String && error.GetString() == SparkOneShotOAuthErrors.PopupBlocked
                ? SparkOneShotOAuthErrors.PopupBlocked
                : SparkOneShotOAuthErrors.Failed);

        if (!value.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.String)
            return Failure(SparkOneShotOAuthErrors.Failed);

        return Take(handle.GetString()!, userId);
    }

    private object Begin(string clientName, SparkOneShotOAuthClient client, string userId, IReadOnlyCollection<string> scopes)
    {
        var request = httpContextAccessor.HttpContext!.Request;
        var verifier = RandomToken(32);
        var attempt = new SparkOneShotOAuthStore.Attempt
        {
            Handle = RandomToken(32),
            UserId = userId,
            ClientName = clientName,
            CodeVerifier = verifier,
            RedirectUri = $"{request.Scheme}://{request.Host}{request.PathBase}{CallbackPath}",
            // The popup page's hand-off nonce (SparkExternalLoginNonce's shape: 16-64 base64url characters).
            Nonce = RandomToken(24),
            ExpiresAt = store.Now + SparkOneShotOAuthStore.Lifetime,
        };
        store.Add(attempt);

        var state = new SparkOneShotOAuthState(attempt.Handle, attempt.Nonce).Protect(dataProtection);
        var url = QueryHelpers.AddQueryString(client.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = attempt.RedirectUri,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        });

        // The handle comes back as the step's answer; it is worth nothing to anyone but this user.
        return new { url, nonce = attempt.Nonce, handle = attempt.Handle };
    }

    private SparkOneShotOAuthOutcome Take(string handle, string userId)
    {
        var attempt = store.Take(handle);
        if (attempt is null)
            return Failure(SparkOneShotOAuthErrors.Expired);

        SparkOneShotOAuthStore.Completion? result;
        lock (attempt)
            result = attempt.Result;

        // Taken out of the store either way: a handle that was refused once is spent, and a token that may
        // no longer be handed out is revoked now rather than left valid at the provider.
        string? refusal =
            attempt.UserId != userId ? SparkOneShotOAuthErrors.Failed
            : store.Now > attempt.ExpiresAt ? SparkOneShotOAuthErrors.Expired
            : null;

        if (refusal is not null)
        {
            if (result?.AccessToken is { } stale)
                store.StartRevocation(attempt.ClientName, stale);
            return Failure(refusal);
        }

        if (result is null)
            return Failure(SparkOneShotOAuthErrors.Failed);
        if (result.AccessToken is not { } token)
            return Failure(result.Error ?? SparkOneShotOAuthErrors.Failed);

        return new SparkOneShotOAuthOutcome(new Lease(store, attempt.ClientName, token, result.GrantedScope), null);
    }

    private static SparkOneShotOAuthOutcome Failure(string error) => new(null, error);

    internal static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    private sealed class Lease(SparkOneShotOAuthStore store, string clientName, string accessToken, string? grantedScope) : ISparkOneShotOAuthLease
    {
        private int disposed;

        public string AccessToken { get; } = accessToken;

        public string? GrantedScope { get; } = grantedScope;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await store.RevokeAsync(clientName, AccessToken);
        }
    }
}

/// <summary>
/// What the OAuth <c>state</c> carries, protected: the attempt's handle, and the popup's nonce so the
/// callback can still report to the opener after the attempt is gone.
/// </summary>
internal sealed record SparkOneShotOAuthState(string Handle, string Nonce)
{
    public string Protect(IDataProtectionProvider provider)
        => provider.CreateProtector(SparkOneShotOAuth.StatePurpose).Protect(JsonSerializer.Serialize(this));

    /// <summary>The state, or <see langword="null"/> when it is missing, tampered with or not ours.</summary>
    public static SparkOneShotOAuthState? Unprotect(IDataProtectionProvider provider, string? state)
    {
        if (string.IsNullOrEmpty(state))
            return null;
        try
        {
            var json = provider.CreateProtector(SparkOneShotOAuth.StatePurpose).Unprotect(state);
            return JsonSerializer.Deserialize<SparkOneShotOAuthState>(json) is { Handle.Length: > 0, Nonce.Length: > 0 } parsed
                ? parsed
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
