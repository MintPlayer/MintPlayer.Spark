namespace MintPlayer.Spark.Challenges.Risk;

/// <summary>
/// How evidence becomes a decision (research §6.7), as a pure function so the policy is testable on its own:
/// <list type="bullet">
/// <item>Probabilistic evidence combines by noisy-OR, <c>score = 1 − Π(1 − wᵢ)</c>; negative weights (positive evidence)
/// then lower it, clamped to 0–1.</item>
/// <item>Score &lt; <see cref="SparkRiskOptions.FlagScore"/>: Allow. Up to <see cref="SparkRiskOptions.ChallengeScore"/>: Flag.
/// Up to <see cref="SparkRiskOptions.SlowScore"/>: Challenge where a provider is configured, else Slow. Above: Slow (and the
/// caller may still challenge).</item>
/// <item>A rule's <see cref="SparkRiskEvidence.Minimum"/> raises the action, but only deterministic evidence reaches
/// ShadowQueue or Block.</item>
/// <item><b>Block only on deterministic evidence</b> (D29), or ShadowQueue instead when <see cref="SparkShadowQueueMode.DeterministicOnly"/>.</item>
/// <item><b>GA11:</b> country-based evidence is never deterministic, never applies a minimum, and when the decision without it
/// would be Allow or Flag, the decision is capped at Flag: nobody is refused, slowed or challenged for a country alone.</item>
/// </list>
/// </summary>
public static class SparkRiskScoring
{
    public static SparkRiskAction Decide(
        IReadOnlyList<SparkRiskEvidence> evidence, SparkRiskOptions options, bool challengeAvailable, out double score)
    {
        var action = DecideCore(evidence, options, challengeAvailable, out score);
        if (action > SparkRiskAction.Flag && evidence.Any(e => e.CountryBased))
        {
            var without = DecideCore(evidence.Where(e => !e.CountryBased).ToList(), options, challengeAvailable, out _);
            if (without <= SparkRiskAction.Flag)
                action = SparkRiskAction.Flag;
        }
        return action;
    }

    /// <summary>The noisy-OR score with positive evidence subtracted, clamped to 0–1.</summary>
    public static double Score(IEnumerable<SparkRiskEvidence> evidence)
    {
        var keep = 1.0;
        var lower = 0.0;
        foreach (var e in evidence)
        {
            if (double.IsNaN(e.Weight))
                continue;
            if (e.Weight > 0)
                keep *= 1 - Math.Min(e.Weight, 0.99);
            else
                lower += e.Weight;
        }
        return Math.Clamp(1 - keep + lower, 0, 1);
    }

    private static SparkRiskAction DecideCore(IReadOnlyList<SparkRiskEvidence> evidence, SparkRiskOptions options, bool challengeAvailable, out double score)
    {
        score = Score(evidence);
        var action = score < options.FlagScore ? SparkRiskAction.Allow
            : score < options.ChallengeScore ? SparkRiskAction.Flag
            : score < options.SlowScore ? (challengeAvailable ? SparkRiskAction.Challenge : SparkRiskAction.Slow)
            : SparkRiskAction.Slow;

        foreach (var e in evidence)
        {
            if (e.Minimum is not { } minimum || e.CountryBased)
                continue;
            if (minimum >= SparkRiskAction.ShadowQueue && !e.Deterministic)
                minimum = SparkRiskAction.Slow;
            if (minimum == SparkRiskAction.Challenge && !challengeAvailable)
                minimum = SparkRiskAction.Slow;
            if (minimum > action)
                action = minimum;
        }

        if (evidence.Any(e => e.Deterministic && !e.CountryBased))
            action = options.ShadowQueue == SparkShadowQueueMode.DeterministicOnly ? SparkRiskAction.ShadowQueue : SparkRiskAction.Block;
        return action;
    }
}
