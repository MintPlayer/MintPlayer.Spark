using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.Hosted;

namespace MintPlayer.Spark.Challenges.FriendlyCaptcha;

/// <summary><c>Spark:Challenges:FriendlyCaptcha</c>. The API key goes in user-secrets or the host's secret store.</summary>
public sealed class FriendlyCaptchaOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Challenges:FriendlyCaptcha";

    public string? SiteKey { get; set; }

    /// <summary>The API key, sent as <c>X-API-Key</c>. ⚠️ A secret: never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Use the EU endpoint (<c>https://eu.frcapi.com</c>; Advanced and Enterprise plans, research §6.4) instead of the global one.
    /// </summary>
    public bool UseEuEndpoint { get; set; }

    /// <summary>Overrides the siteverify URL; null uses the global or EU endpoint (documented: <c>/api/v2/captcha/siteverify</c>).</summary>
    public string? VerifyUrl { get; set; }

    /// <summary>The widget SDK script (an ES module). Add its origin and the API origin to the CSP.</summary>
    public string ScriptUrl { get; set; } = "https://cdn.jsdelivr.net/npm/@friendlycaptcha/sdk@0.1.8/site.min.js";

    internal string ResolvedVerifyUrl => VerifyUrl
        ?? (UseEuEndpoint ? "https://eu.frcapi.com/api/v2/captcha/siteverify" : "https://global.frcapi.com/api/v2/captcha/siteverify");
}

/// <summary>
/// Friendly Captcha v2 (a hosted proof of work). Verification per Friendly Captcha's v2 docs: <c>POST</c> JSON
/// <c>{ response, sitekey }</c> with the <c>X-API-Key</c> header; the answer is
/// <c>{ success: true, data: { event_id, challenge: { timestamp, origin } } }</c> or
/// <c>{ success: false, error: { error_code, detail } }</c>.
/// </summary>
public sealed class FriendlyCaptchaChallenge(IHttpClientFactory httpClients, IOptionsMonitor<FriendlyCaptchaOptions> options, ILogger<FriendlyCaptchaChallenge> logger)
    : SparkHostedChallenge(httpClients, logger)
{
    public const string ProviderName = "friendlycaptcha";
    internal const string ClientName = "MintPlayer.Spark.Challenges.FriendlyCaptcha";

    public override string Name => ProviderName;

    protected override string HttpClientName => ClientName;

    public override Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SparkChallengeOffer(ProviderName, request.Point, options.CurrentValue.SiteKey, options.CurrentValue.ScriptUrl));

    protected override HttpRequestMessage BuildVerifyRequest(SparkChallengeRequest request, string response)
    {
        var settings = options.CurrentValue;
        var message = new HttpRequestMessage(HttpMethod.Post, settings.ResolvedVerifyUrl)
        {
            Content = JsonContent.Create(new Dictionary<string, string?> { ["response"] = response, ["sitekey"] = settings.SiteKey }),
        };
        message.Headers.Add("X-API-Key", settings.ApiKey ?? "");
        return message;
    }

    protected override SparkChallengeResult ReadVerifyResponse(SparkChallengeRequest request, JsonElement body)
    {
        if (Bool(body, "success"))
            return SparkChallengeResult.Pass(ProviderName);
        var code = body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? String(error, "error_code") : null;
        return SparkChallengeResult.Fail(ProviderName, code ?? "invalid");
    }
}

public static class SparkFriendlyCaptchaExtensions
{
    /// <summary>Friendly Captcha as the challenge provider, with ALTCHA for visitors who refuse third-party-challenge consent (GA9).</summary>
    public static SparkChallengesBuilder UseFriendlyCaptcha(this SparkChallengesBuilder builder, Action<FriendlyCaptchaOptions>? configure = null)
    {
        builder.Services.AddSparkConfigurationSection<FriendlyCaptchaOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(FriendlyCaptchaChallenge.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        return builder.UseHostedChallenge<FriendlyCaptchaChallenge>(FriendlyCaptchaChallenge.ProviderName, "Friendly Captcha GmbH");
    }
}
