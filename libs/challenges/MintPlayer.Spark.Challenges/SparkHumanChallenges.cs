using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Claims;

namespace MintPlayer.Spark.Challenges;

/// <summary>
/// Whether a challenge point is switched on now (D29: "challenge points switchable at runtime"). The default reads
/// <see cref="SparkChallengesOptions.Points"/> through <c>IOptionsMonitor</c>, so a reloading configuration source switches
/// a point without a redeploy. A host that keeps operator settings in <c>MintPlayer.Spark.Settings</c> replaces it with a
/// three-line implementation over a <c>SparkSetting&lt;string&gt;</c> (<c>docs/guide-challenges.md</c>).
/// </summary>
public interface ISparkChallengeSwitch
{
    ValueTask<bool> IsEnabledAsync(string point, CancellationToken cancellationToken = default);
}

internal sealed class OptionsChallengeSwitch(IOptionsMonitor<SparkChallengesOptions> options) : ISparkChallengeSwitch
{
    public ValueTask<bool> IsEnabledAsync(string point, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(!string.Equals(point, SparkChallengePoints.Admission, StringComparison.OrdinalIgnoreCase)
            && options.CurrentValue.Points.Contains(point, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The challenges as callers use them (commerce PRD §4.12): offer the configured provider (ALTCHA instead when a hosted
/// provider lacks the visitor's consent, GA9), verify a response once (a replay is refused), and ask for a step-up inside
/// a Spark action through the 449 retry.
/// </summary>
public interface ISparkHumanChallenges
{
    /// <summary>Whether any provider is registered.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether <paramref name="point"/> is switched on (<see cref="ISparkChallengeSwitch"/>). <c>admission</c> never is.</summary>
    ValueTask<bool> IsEnabledAtAsync(string point, CancellationToken cancellationToken = default);

    /// <summary>The challenge to show at <paramref name="point"/> for the current request.</summary>
    /// <exception cref="InvalidOperationException">No provider is registered.</exception>
    /// <exception cref="ArgumentException"><paramref name="point"/> is <c>admission</c>, which is never challenged.</exception>
    Task<SparkChallengeOffer> OfferAsync(string point, string? action = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a response at <paramref name="point"/>: from the configured provider, or from ALTCHA as its consent fallback;
    /// a hosted provider's response without consent is refused (<c>consent</c>); a response already used is refused
    /// (<c>replayed</c>).
    /// </summary>
    Task<SparkChallengeResult> VerifyAsync(string point, SparkChallengeResponse? response, string? action = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A step-up inside a Spark action or hook (research §6.10, "Hold: step-up only … as a 449
    /// <c>Retry.Invoke("spark.challenge", …)</c>"): when <paramref name="point"/> is switched on and the decision asks for a
    /// challenge or its score reaches <see cref="SparkChallengesOptions.StepUpScore"/>, the browser runs the challenge (the
    /// client method <c>spark.challenge</c> of <c>@mintplayer/ng-spark/challenges</c>) and the action re-runs from the top
    /// with the answer. Call it before anything with side effects (the hold), so no time to live is lost.
    /// </summary>
    /// <returns>True when no challenge was needed or it passed; false when it failed or was cancelled (refuse the action).</returns>
    Task<bool> StepUpAsync(IRetryAccessor retry, string point, SparkRiskDecision? decision, string? action = null, CancellationToken cancellationToken = default);
}

/// <summary>The providers by name, and which registration came last (the default <see cref="SparkChallengesOptions.Provider"/>).</summary>
internal sealed class SparkChallengeProviderRegistry
{
    public List<string> Order { get; } = [];

    public void Add(string name)
    {
        Order.Remove(name);
        Order.Add(name);
    }
}

internal sealed class SparkHumanChallenges(
    IEnumerable<ISparkHumanChallenge> providers,
    SparkChallengeProviderRegistry registry,
    ISparkChallengeSwitch challengeSwitch,
    ISparkClaims claims,
    IOptionsMonitor<SparkChallengesOptions> options,
    IHttpContextAccessor accessor,
    IServiceProvider services,
    TimeProvider time,
    ILogger<SparkHumanChallenges> logger) : ISparkHumanChallenges
{
    /// <summary>The client method a step-up invokes (registered by <c>provideSparkChallenges()</c>).</summary>
    public const string ClientMethod = "spark.challenge";

    internal static readonly SparkClaimKind<SparkClaimHold> UsedResponses = SparkClaimKinds.Holds("challenges/used/");

    private readonly Dictionary<string, ISparkHumanChallenge> byName = providers
        .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => byName.Count > 0;

    public ValueTask<bool> IsEnabledAtAsync(string point, CancellationToken cancellationToken = default)
        => IsConfigured ? challengeSwitch.IsEnabledAsync(point, cancellationToken) : ValueTask.FromResult(false);

    public async Task<SparkChallengeOffer> OfferAsync(string point, string? action = null, CancellationToken cancellationToken = default)
    {
        EnsurePoint(point);
        var (provider, fallback) = await ChooseAsync(cancellationToken);
        var offer = await provider.OfferAsync(Request(point, action), cancellationToken);
        return fallback ? offer with { ConsentFallback = true } : offer;
    }

    public async Task<SparkChallengeResult> VerifyAsync(string point, SparkChallengeResponse? response, string? action = null, CancellationToken cancellationToken = default)
    {
        EnsurePoint(point);
        if (response is null || string.IsNullOrWhiteSpace(response.Provider) || string.IsNullOrWhiteSpace(response.Payload))
            return SparkChallengeResult.Fail(response?.Provider ?? "", "missing");

        if (!IsConfigured)
            throw new InvalidOperationException("No challenge provider is registered.");
        if (!byName.TryGetValue(response.Provider, out var provider))
            return SparkChallengeResult.Fail(response.Provider, "wrong-provider");
        // Only the configured provider, or ALTCHA standing in for it, is accepted: a host that switched to Turnstile does
        // not keep accepting a weaker provider just because it is still registered.
        if (!string.Equals(provider.Name, PrimaryName(), StringComparison.OrdinalIgnoreCase) && provider.Name != AltchaChallenge.ProviderName)
            return SparkChallengeResult.Fail(provider.Name, "wrong-provider");
        if (provider.RequiresConsent && !await services.AllowsAsync(SparkConsentPurposes.ThirdPartyChallenge, cancellationToken))
            return SparkChallengeResult.Fail(provider.Name, "consent");

        var request = Request(point, action);
        var result = await provider.VerifyAsync(request, response.Payload, cancellationToken);
        if (!result.Passed)
            return result;

        // Single use: the first verification of a response claims its marker until the replay window ends.
        var key = UsedResponses.Key(provider.Name + "/" + SparkChallengeHashing.Sha256Hex(provider.ReplayKey(response.Payload)));
        var expires = (time.GetUtcNow() + options.CurrentValue.ReplayWindow).UtcDateTime;
        var claim = await claims.ReserveHoldAsync(UsedResponses, key, Guid.NewGuid().ToString("N"), expires, cancellationToken: cancellationToken);
        if (claim.Status != SparkClaimStatus.Reserved)
        {
            logger.LogInformation("A {Provider} response was presented again at {Point}; refused as a replay.", provider.Name, point);
            return SparkChallengeResult.Fail(provider.Name, "replayed");
        }
        return result;
    }

    public async Task<bool> StepUpAsync(IRetryAccessor retry, string point, SparkRiskDecision? decision, string? action = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retry);
        EnsurePoint(point);
        if (!await IsEnabledAtAsync(point, cancellationToken))
            return true;
        var needed = decision is null
            || decision.Action == SparkRiskAction.Challenge
            || decision.Score >= options.CurrentValue.StepUpScore;
        if (!needed)
            return true;

        await retry.Invoke(ClientMethod, async () => await OfferAsync(point, action, cancellationToken));
        if (retry.Result is not { Value: { } value } || retry.Result.Option == RetryResult.CancelOption)
            return false;
        SparkChallengeResponse? response;
        try
        {
            response = value.Deserialize<SparkChallengeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return false;
        }
        return (await VerifyAsync(point, response, action, cancellationToken)).Passed;
    }

    /// <summary>The configured provider, or ALTCHA in its place when it needs consent the visitor has not given.</summary>
    private async Task<(ISparkHumanChallenge Provider, bool ConsentFallback)> ChooseAsync(CancellationToken cancellationToken)
    {
        if (byName.Count == 0)
            throw new InvalidOperationException("No challenge provider is registered: call spark.AddChallenges(c => c.UseAltcha()) (or another provider).");
        var name = PrimaryName();
        var provider = byName[name];
        if (provider.RequiresConsent && !await services.AllowsAsync(SparkConsentPurposes.ThirdPartyChallenge, cancellationToken))
        {
            if (byName.TryGetValue(AltchaChallenge.ProviderName, out var altcha))
                return (altcha, true);
            throw new InvalidOperationException($"Challenge provider '{name}' needs consent and no ALTCHA fallback is registered.");
        }
        return (provider, false);
    }

    /// <summary>The configured provider (<see cref="SparkChallengesOptions.Provider"/>), else the last one registered.</summary>
    private string PrimaryName()
        => options.CurrentValue.Provider is { Length: > 0 } configured && byName.ContainsKey(configured)
            ? byName[configured].Name
            : registry.Order.LastOrDefault(byName.ContainsKey) ?? byName.Keys.First();

    private SparkChallengeRequest Request(string point, string? action)
    {
        var http = accessor.HttpContext;
        return new SparkChallengeRequest(point, http?.Features.Get<ISparkVisitor>()?.IdentityKey, http?.Connection.RemoteIpAddress?.ToString(), action);
    }

    private static void EnsurePoint(string point)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(point);
        if (string.Equals(point, SparkChallengePoints.Admission, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Admission out of a waiting room is never challenged: the queue position is the proof (commerce PRD §4.12).", nameof(point));
    }
}
