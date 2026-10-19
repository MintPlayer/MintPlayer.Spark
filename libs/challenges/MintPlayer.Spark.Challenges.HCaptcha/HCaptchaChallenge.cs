using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.Hosted;

namespace MintPlayer.Spark.Challenges.HCaptcha;

/// <summary><c>Spark:Challenges:HCaptcha</c>. The secret goes in user-secrets or the host's secret store.</summary>
public sealed class HCaptchaOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Challenges:HCaptcha";

    /// <summary>The widget's site key (also sent to siteverify, which then refuses a token for another site).</summary>
    public string? SiteKey { get; set; }

    /// <summary>The account secret for siteverify. ⚠️ A secret: never logged.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Enterprise only: the highest acceptable risk score (hCaptcha scores 0 = safe … 1 = risky). Null: ignore scores.
    /// </summary>
    public double? MaximumScore { get; set; }

    /// <summary>hCaptcha's siteverify endpoint (documented: <c>https://api.hcaptcha.com/siteverify</c>).</summary>
    public string VerifyUrl { get; set; } = "https://api.hcaptcha.com/siteverify";

    /// <summary>The widget script, explicit rendering. Add <c>https://hcaptcha.com</c> and <c>https://*.hcaptcha.com</c> to the CSP.</summary>
    public string ScriptUrl { get; set; } = "https://js.hcaptcha.com/1/api.js?render=explicit";
}

/// <summary>
/// hCaptcha. Verification per hCaptcha's docs: <c>POST</c> form <c>secret</c>, <c>response</c>, <c>remoteip</c>,
/// <c>sitekey</c>; the answer is <c>{ success, challenge_ts, hostname, error-codes }</c>, with <c>score</c> and
/// <c>score_reason</c> on Enterprise.
/// </summary>
public sealed class HCaptchaChallenge(IHttpClientFactory httpClients, IOptionsMonitor<HCaptchaOptions> options, ILogger<HCaptchaChallenge> logger)
    : SparkHostedChallenge(httpClients, logger)
{
    public const string ProviderName = "hcaptcha";
    internal const string ClientName = "MintPlayer.Spark.Challenges.HCaptcha";

    public override string Name => ProviderName;

    protected override string HttpClientName => ClientName;

    public override Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SparkChallengeOffer(ProviderName, request.Point, options.CurrentValue.SiteKey, options.CurrentValue.ScriptUrl));

    protected override HttpRequestMessage BuildVerifyRequest(SparkChallengeRequest request, string response)
    {
        var settings = options.CurrentValue;
        var form = new Dictionary<string, string> { ["secret"] = settings.Secret ?? "", ["response"] = response };
        if (settings.SiteKey is { Length: > 0 } siteKey)
            form["sitekey"] = siteKey;
        if (request.RemoteAddress is { Length: > 0 } ip)
            form["remoteip"] = ip;
        return new HttpRequestMessage(HttpMethod.Post, settings.VerifyUrl) { Content = new FormUrlEncodedContent(form) };
    }

    protected override SparkChallengeResult ReadVerifyResponse(SparkChallengeRequest request, JsonElement body)
    {
        if (!Bool(body, "success"))
            return SparkChallengeResult.Fail(ProviderName, FirstError(body, "error-codes"));
        var score = Number(body, "score");
        if (options.CurrentValue.MaximumScore is { } maximum && score is { } s && s > maximum)
            return new SparkChallengeResult(false, ProviderName, "score", 1 - s);
        // hCaptcha's score grows with risk; the result reports the human-likeness (1 − score) like reCAPTCHA's.
        return SparkChallengeResult.Pass(ProviderName, score is { } risk ? 1 - risk : null);
    }
}

public static class SparkHCaptchaExtensions
{
    /// <summary>hCaptcha as the challenge provider, with ALTCHA for visitors who refuse third-party-challenge consent (GA9).</summary>
    public static SparkChallengesBuilder UseHCaptcha(this SparkChallengesBuilder builder, Action<HCaptchaOptions>? configure = null)
    {
        builder.Services.AddSparkConfigurationSection<HCaptchaOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(HCaptchaChallenge.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        return builder.UseHostedChallenge<HCaptchaChallenge>(HCaptchaChallenge.ProviderName, "Intuition Machines (hCaptcha)");
    }
}
