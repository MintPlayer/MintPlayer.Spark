using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.Hosted;

namespace MintPlayer.Spark.Challenges.Turnstile;

/// <summary><c>Spark:Challenges:Turnstile</c>. The secret key goes in user-secrets or the host's secret store, never in appsettings.</summary>
public sealed class TurnstileOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Challenges:Turnstile";

    /// <summary>The widget's public site key.</summary>
    public string? SiteKey { get; set; }

    /// <summary>The secret key for siteverify. ⚠️ A secret: never logged, never in a response.</summary>
    public string? SecretKey { get; set; }

    /// <summary>When set, a token issued for another hostname is refused.</summary>
    public string? ExpectedHostname { get; set; }

    /// <summary>Cloudflare's siteverify endpoint (documented: <c>https://challenges.cloudflare.com/turnstile/v0/siteverify</c>).</summary>
    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>The widget script, explicit rendering (documented). Add its origin to the CSP <c>script-src</c> and <c>frame-src</c>.</summary>
    public string ScriptUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";
}

/// <summary>
/// Cloudflare Turnstile (research §6.4: managed, non-interactive or invisible; tokens valid 300 s and verifiable once).
/// Verification per Cloudflare's server-side validation docs: <c>POST</c> form <c>secret</c>, <c>response</c>,
/// <c>remoteip</c>; the answer is <c>{ success, challenge_ts, hostname, error-codes, action, cdata }</c>.
/// </summary>
public sealed class TurnstileChallenge(IHttpClientFactory httpClients, IOptionsMonitor<TurnstileOptions> options, ILogger<TurnstileChallenge> logger)
    : SparkHostedChallenge(httpClients, logger)
{
    public const string ProviderName = "turnstile";
    internal const string ClientName = "MintPlayer.Spark.Challenges.Turnstile";

    public override string Name => ProviderName;

    protected override string HttpClientName => ClientName;

    public override Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SparkChallengeOffer(ProviderName, request.Point, options.CurrentValue.SiteKey, options.CurrentValue.ScriptUrl, Action: ActionFor(request)));

    protected override HttpRequestMessage BuildVerifyRequest(SparkChallengeRequest request, string response)
    {
        var settings = options.CurrentValue;
        var form = new Dictionary<string, string> { ["secret"] = settings.SecretKey ?? "", ["response"] = response };
        if (request.RemoteAddress is { Length: > 0 } ip)
            form["remoteip"] = ip;
        return new HttpRequestMessage(HttpMethod.Post, settings.VerifyUrl) { Content = new FormUrlEncodedContent(form) };
    }

    protected override SparkChallengeResult ReadVerifyResponse(SparkChallengeRequest request, JsonElement body)
    {
        if (!Bool(body, "success"))
            return SparkChallengeResult.Fail(ProviderName, FirstError(body, "error-codes"));
        if (options.CurrentValue.ExpectedHostname is { Length: > 0 } host && !string.Equals(String(body, "hostname"), host, StringComparison.OrdinalIgnoreCase))
            return SparkChallengeResult.Fail(ProviderName, "hostname-mismatch");
        if (String(body, "action") is { Length: > 0 } action && !string.Equals(action, ActionFor(request), StringComparison.Ordinal))
            return SparkChallengeResult.Fail(ProviderName, "action-mismatch");
        return SparkChallengeResult.Pass(ProviderName);
    }

    /// <summary>Turnstile actions allow letters, digits, <c>-</c> and <c>_</c>, up to 32 characters: the point is one.</summary>
    internal static string ActionFor(SparkChallengeRequest request) => request.Point;
}

public static class SparkTurnstileExtensions
{
    /// <summary>
    /// Cloudflare Turnstile as the challenge provider, with ALTCHA for visitors who refuse third-party-challenge consent
    /// (GA9). Startup refuses it without <c>spark.AddConsent()</c>.
    /// </summary>
    public static SparkChallengesBuilder UseTurnstile(this SparkChallengesBuilder builder, Action<TurnstileOptions>? configure = null)
    {
        builder.Services.AddSparkConfigurationSection<TurnstileOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(TurnstileChallenge.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        return builder.UseHostedChallenge<TurnstileChallenge>(TurnstileChallenge.ProviderName, "Cloudflare");
    }
}
