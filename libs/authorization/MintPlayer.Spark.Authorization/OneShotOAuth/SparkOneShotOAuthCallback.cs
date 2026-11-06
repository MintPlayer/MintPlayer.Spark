using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <summary>What the callback page reports: the popup's nonce (when the state named one) and the error, if any.</summary>
internal sealed record SparkOneShotOAuthCallbackResult(string? Nonce, string? Error);

/// <summary>
/// The provider's redirect back (#554 f1): checks the state and the user, exchanges the code with the
/// PKCE verifier, and leaves the token in <see cref="SparkOneShotOAuthStore"/> for the action's pass 2.
/// Never signs anyone in, and never sends the token to the browser.
/// </summary>
internal sealed partial class SparkOneShotOAuthCallback
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly SparkOneShotOAuthStore store;
    [Inject] private readonly IDataProtectionProvider dataProtection;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly IOptions<SparkOneShotOAuthOptions> options;
    [Inject] private readonly ILogger<SparkOneShotOAuthCallback> logger;

    public async Task<SparkOneShotOAuthCallbackResult> CompleteAsync(string? state, string? code, string? error, CancellationToken cancellationToken = default)
    {
        var parsed = SparkOneShotOAuthState.Unprotect(dataProtection, state);
        if (parsed is null)
            return new(null, SparkOneShotOAuthErrors.Failed);

        var nonce = parsed.Nonce;
        var attempt = store.Find(parsed.Handle);
        if (attempt is null)
            return new(nonce, SparkOneShotOAuthErrors.Expired);

        // Bound to the user who started it: a link handed to someone else exchanges nothing.
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != attempt.UserId)
            return new(nonce, SparkOneShotOAuthErrors.Failed);
        if (store.Now > attempt.ExpiresAt)
            return new(nonce, SparkOneShotOAuthErrors.Expired);
        if (!attempt.TryClaimCallback())
            return new(nonce, SparkOneShotOAuthErrors.Failed);

        var completion = await ExchangeAsync(attempt, code, error, cancellationToken);
        store.Complete(attempt, completion);
        return new(nonce, completion.Error);
    }

    private async Task<SparkOneShotOAuthStore.Completion> ExchangeAsync(
        SparkOneShotOAuthStore.Attempt attempt, string? code, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
            return new(null, null, error == "access_denied" ? SparkOneShotOAuthErrors.Denied : SparkOneShotOAuthErrors.Failed);
        if (string.IsNullOrEmpty(code) || !options.Value.Clients.TryGetValue(attempt.ClientName, out var client))
            return new(null, null, SparkOneShotOAuthErrors.Failed);

        try
        {
            using var http = httpClients.CreateClient(SparkOneShotOAuthStore.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, client.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = attempt.RedirectUri,
                    ["client_id"] = client.ClientId,
                    ["client_secret"] = client.ClientSecret ?? string.Empty,
                    ["code_verifier"] = attempt.CodeVerifier,
                }),
            };
            // GitHub answers form-encoded unless asked for JSON.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            // GitHub reports a refused exchange as a 200 with an "error" member, so the status alone says nothing.
            if (response.IsSuccessStatusCode
                && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(token.GetString()))
            {
                var scope = json.RootElement.TryGetProperty("scope", out var granted) && granted.ValueKind == JsonValueKind.String
                    ? granted.GetString()
                    : null;
                return new(token.GetString(), scope, null);
            }

            logger.LogWarning("The one-shot OAuth code exchange of client {Client} was refused with status {Status}.",
                attempt.ClientName, (int)response.StatusCode);
            return new(null, null, SparkOneShotOAuthErrors.Failed);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "The one-shot OAuth code exchange of client {Client} failed.", attempt.ClientName);
            return new(null, null, SparkOneShotOAuthErrors.Failed);
        }
    }
}
