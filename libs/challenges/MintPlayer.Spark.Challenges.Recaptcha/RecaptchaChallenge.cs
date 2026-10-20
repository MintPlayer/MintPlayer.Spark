using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.Hosted;

namespace MintPlayer.Spark.Challenges.Recaptcha;

/// <summary><c>Spark:Challenges:Recaptcha</c>. The secret goes in user-secrets or the host's secret store.</summary>
public sealed class RecaptchaOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Challenges:Recaptcha";

    /// <summary><c>3</c> (score, invisible; default) or <c>2</c> (checkbox).</summary>
    public int Version { get; set; } = 3;

    public string? SiteKey { get; set; }

    /// <summary>The secret key for siteverify. ⚠️ A secret: never logged.</summary>
    public string? SecretKey { get; set; }

    /// <summary>v3: the lowest acceptable score (0 = bot … 1 = human). Google's documented starting point is 0.5.</summary>
    public double MinimumScore { get; set; } = 0.5;

    /// <summary>When set, a token issued for another hostname is refused.</summary>
    public string? ExpectedHostname { get; set; }

    /// <summary>Google's siteverify endpoint (documented: <c>https://www.google.com/recaptcha/api/siteverify</c>).</summary>
    public string VerifyUrl { get; set; } = "https://www.google.com/recaptcha/api/siteverify";

    /// <summary>The widget script; <c>{siteKey}</c> is replaced. Add <c>https://www.google.com</c> and <c>https://www.gstatic.com</c> to the CSP.</summary>
    public string ScriptUrl { get; set; } = "https://www.google.com/recaptcha/api.js?render={siteKey}";
}

/// <summary>
/// Google reCAPTCHA. Verification per Google's docs: <c>POST</c> form <c>secret</c>, <c>response</c>, <c>remoteip</c>; the
/// answer is <c>{ success, challenge_ts, hostname, error-codes }</c>, plus <c>score</c> and <c>action</c> for v3. A v3 token
/// below <see cref="RecaptchaOptions.MinimumScore"/> or for another action fails.
/// </summary>
/// <remarks>⚠️ Research §6.12: reCAPTCHA brings consent duties (Google processes the visitor's data); it is consent-gated here (GA9).</remarks>
public sealed class RecaptchaChallenge(IHttpClientFactory httpClients, IOptionsMonitor<RecaptchaOptions> options, ILogger<RecaptchaChallenge> logger)
    : SparkHostedChallenge(httpClients, logger)
{
    public const string ProviderName = "recaptcha";
    internal const string ClientName = "MintPlayer.Spark.Challenges.Recaptcha";

    public override string Name => ProviderName;

    protected override string HttpClientName => ClientName;

    public override Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var script = settings.Version == 3
            ? settings.ScriptUrl.Replace("{siteKey}", Uri.EscapeDataString(settings.SiteKey ?? ""), StringComparison.Ordinal)
            : "https://www.google.com/recaptcha/api.js?render=explicit";
        return Task.FromResult(new SparkChallengeOffer(ProviderName, request.Point, settings.SiteKey, script, Action: ActionFor(request)));
    }

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
        var settings = options.CurrentValue;
        if (!Bool(body, "success"))
            return SparkChallengeResult.Fail(ProviderName, FirstError(body, "error-codes"));
        if (settings.ExpectedHostname is { Length: > 0 } host && !string.Equals(String(body, "hostname"), host, StringComparison.OrdinalIgnoreCase))
            return SparkChallengeResult.Fail(ProviderName, "hostname-mismatch");
        if (settings.Version != 3)
            return SparkChallengeResult.Pass(ProviderName);
        if (String(body, "action") is { } action && !string.Equals(action, ActionFor(request), StringComparison.Ordinal))
            return SparkChallengeResult.Fail(ProviderName, "action-mismatch");
        var score = Number(body, "score");
        return score is { } s && s >= settings.MinimumScore
            ? SparkChallengeResult.Pass(ProviderName, s)
            : new SparkChallengeResult(false, ProviderName, "score", score);
    }

    /// <summary>reCAPTCHA v3 actions allow letters, digits, slashes and underscores: <c>queue-join</c> becomes <c>queue_join</c>.</summary>
    internal static string ActionFor(SparkChallengeRequest request) => request.Point.Replace('-', '_');
}

public static class SparkRecaptchaExtensions
{
    /// <summary>Google reCAPTCHA as the challenge provider, with ALTCHA for visitors who refuse third-party-challenge consent (GA9).</summary>
    public static SparkChallengesBuilder UseRecaptcha(this SparkChallengesBuilder builder, Action<RecaptchaOptions>? configure = null)
    {
        builder.Services.AddSparkConfigurationSection<RecaptchaOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(RecaptchaChallenge.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        return builder.UseHostedChallenge<RecaptchaChallenge>(RecaptchaChallenge.ProviderName, "Google");
    }
}
