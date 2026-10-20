using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Challenges.Risk;

namespace MintPlayer.Spark.Challenges.Deceptive;

/// <summary>The deceptive measures' names, as the decision log stores them.</summary>
public static class SparkDeceptiveMeasureNames
{
    public const string Honeypot = "honeypot";
    public const string Decoys = "decoys";
    public const string FakeSoldOut = "fake-sold-out";
    public const string PoisonedTokens = "poisoned-tokens";
    public const string Tarpit = "tarpit";
    public const string ShadowQueue = "shadow-queue";
}

/// <summary>
/// The deceptive measures (D29, owner's choice; commerce PRD §4.12): each a separate switch, off by default, firing by
/// default only on deterministic evidence, and logged for appeal every time it is applied.
/// </summary>
/// <remarks>
/// ⚠️ <b>Legal exposure.</b> Deceiving a visitor who turns out to be human is a misleading commercial practice toward a
/// consumer (Directive 2005/29/EC Art. 6–7), and refusing service by automated processing alone touches GDPR Art. 22.
/// That is why the default trigger is deterministic evidence (a forged or replayed token, a honeypot or decoy hit), why a
/// score trigger must be chosen explicitly, and why every application is logged (<see cref="ISparkRiskDecisionLog"/>).
/// Both texts were checked against EUR-Lex (02005L0029-20260927, 32016R0679) on 2026-10-20; see
/// <c>docs/guide-challenges.md</c>.
/// </remarks>
public interface ISparkDeceptiveMeasures
{
    /// <summary>Whether any measure is on.</summary>
    bool AnyEnabled { get; }

    /// <summary>Whether <paramref name="measure"/> is on and <paramref name="decision"/> fires its trigger.</summary>
    bool Applies(string measure, SparkRiskDecision decision);

    /// <summary>Applies <paramref name="measure"/> when it <see cref="Applies"/>: logs it and answers true.</summary>
    Task<bool> ApplyAsync(string measure, SparkRiskContext context, SparkRiskDecision decision, CancellationToken cancellationToken = default);

    /// <summary>The tarpit's delay (capped at 30 s).</summary>
    TimeSpan TarpitDelay { get; }
}

internal sealed class SparkDeceptiveMeasures(
    IOptionsMonitor<SparkChallengesOptions> options,
    ISparkRiskDecisionLog log,
    ILogger<SparkDeceptiveMeasures> logger) : ISparkDeceptiveMeasures
{
    public bool AnyEnabled => Measures(options.CurrentValue.Deceptive).Any(m => m.Options.Enabled);

    public TimeSpan TarpitDelay
    {
        get
        {
            var delay = options.CurrentValue.Deceptive.Tarpit.Delay;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
        }
    }

    public bool Applies(string measure, SparkRiskDecision decision)
    {
        var current = options.CurrentValue;
        if (measure == SparkDeceptiveMeasureNames.ShadowQueue)
            return current.Risk.ShadowQueue == SparkShadowQueueMode.DeterministicOnly && decision.IsDeterministic;
        var switch_ = Measures(current.Deceptive).FirstOrDefault(m => m.Name == measure).Options;
        return switch_ is { Enabled: true } && Fires(switch_, decision);
    }

    public async Task<bool> ApplyAsync(string measure, SparkRiskContext context, SparkRiskDecision decision, CancellationToken cancellationToken = default)
    {
        if (!Applies(measure, decision))
            return false;
        try
        {
            await log.WriteAsync(context, decision, measure, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unlogged deception cannot be appealed: do not deceive then.
            logger.LogWarning(ex, "Logging deceptive measure {Measure} failed; the measure is not applied.", measure);
            return false;
        }
        return true;
    }

    internal static bool Fires(SparkDeceptiveMeasureOptions measure, SparkRiskDecision decision) => measure.Trigger switch
    {
        SparkDeceptiveTrigger.Deterministic => decision.HasDeceptiveTrigger,
        SparkDeceptiveTrigger.RiskScore => decision.HasDeceptiveTrigger || (measure.TriggerScore is { } score && decision.Score >= score),
        _ => false,
    };

    internal static IEnumerable<(string Name, SparkDeceptiveMeasureOptions Options)> Measures(SparkDeceptiveOptions deceptive) =>
    [
        (SparkDeceptiveMeasureNames.Honeypot, deceptive.Honeypot),
        (SparkDeceptiveMeasureNames.Decoys, deceptive.Decoys),
        (SparkDeceptiveMeasureNames.FakeSoldOut, deceptive.FakeSoldOut),
        (SparkDeceptiveMeasureNames.PoisonedTokens, deceptive.PoisonedTokens),
        (SparkDeceptiveMeasureNames.Tarpit, deceptive.Tarpit),
    ];
}
