using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Challenges.Contracts;

namespace MintPlayer.Spark.Challenges.Hosted;

/// <summary>
/// The shared shape of a hosted challenge provider (Turnstile, hCaptcha, reCAPTCHA, Friendly Captcha): the widget runs a
/// vendor script in the browser, and the server verifies its token with one POST to the vendor's <c>siteverify</c>
/// endpoint (research §6.10: no third-party NuGet is needed). Every hosted provider processes data on the vendor's
/// servers, so <see cref="RequiresConsent"/> is true (GA9).
/// </summary>
/// <remarks>
/// A verification that cannot reach the vendor fails closed as <c>unavailable</c>; the caller decides (a waiting room
/// offers ALTCHA again). The secret and the token are never logged.
/// </remarks>
public abstract class SparkHostedChallenge(IHttpClientFactory httpClients, ILogger logger) : ISparkHumanChallenge
{
    public abstract string Name { get; }

    public bool RequiresConsent => true;

    /// <summary>The named <see cref="HttpClient"/> the adapter registered.</summary>
    protected abstract string HttpClientName { get; }

    public abstract Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken);

    /// <summary>The verification request: URL and body, per the vendor's documentation.</summary>
    protected abstract HttpRequestMessage BuildVerifyRequest(SparkChallengeRequest request, string response);

    /// <summary>Reads the vendor's answer into a result.</summary>
    protected abstract SparkChallengeResult ReadVerifyResponse(SparkChallengeRequest request, JsonElement body);

    public async Task<SparkChallengeResult> VerifyAsync(SparkChallengeRequest request, string response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > 8192)
            return SparkChallengeResult.Fail(Name, "missing");
        try
        {
            using var message = BuildVerifyRequest(request, response);
            using var answer = await httpClients.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
            await using var stream = await answer.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ReadVerifyResponse(request, json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Provider} verification could not be completed ({Error}); failing closed.", Name, ex.GetType().Name);
            return SparkChallengeResult.Fail(Name, "unavailable");
        }
    }

    /// <summary>The first entry of a JSON string array property (<c>error-codes</c>), or <paramref name="fallback"/>.</summary>
    protected static string FirstError(JsonElement body, string property, string fallback = "invalid")
        => body.TryGetProperty(property, out var codes) && codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0
            && codes[0].ValueKind == JsonValueKind.String ? codes[0].GetString() ?? fallback : fallback;

    /// <summary>A boolean property, false when absent.</summary>
    protected static bool Bool(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A string property, or null.</summary>
    protected static string? String(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A number property, or null.</summary>
    protected static double? Number(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
