using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;

namespace MintPlayer.Spark.Challenges.Risk;

/// <summary>
/// The risk pipeline (commerce PRD §4.12, research §6.7): asks every <see cref="ISparkRiskSignal"/>, combines the
/// evidence (<see cref="SparkRiskScoring"/>), logs any decision that had evidence, and answers what to do.
/// </summary>
public interface ISparkRiskPipeline
{
    /// <summary>The decision for one request.</summary>
    Task<SparkRiskDecision> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records evidence found outside the pipeline against an identity (a replayed admission token, a decoy hit) and logs
    /// it. Deterministic evidence then counts at the identity's next evaluation for <see cref="SparkRiskOptions.RecordedEvidenceLifetime"/>.
    /// </summary>
    Task RecordAsync(SparkRiskContext context, SparkRiskEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>The deterministic evidence recorded against <paramref name="identityKey"/> that still counts (a poisoned-token decision reads it).</summary>
    IReadOnlyList<SparkRiskEvidence> RecordedAgainst(string identityKey);

    /// <summary>A context for the current request: its identity key (X1) and address key (X10).</summary>
    SparkRiskContext ContextFor(HttpContext httpContext, string point, string? subject = null, IReadOnlyDictionary<string, string?>? facts = null);
}

internal sealed class SparkRiskPipeline(
    IEnumerable<ISparkRiskSignal> signals,
    SparkRiskMemory memory,
    ISparkRiskDecisionLog log,
    IOptionsMonitor<SparkChallengesOptions> options,
    IServiceProvider services,
    ILogger<SparkRiskPipeline> logger) : ISparkRiskPipeline
{
    public async Task<SparkRiskDecision> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken = default)
    {
        var risk = options.CurrentValue.Risk;
        var evidence = new List<SparkRiskEvidence>();
        foreach (var signal in signals)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(risk.SignalTimeout);
                var found = await signal.EvaluateAsync(context, timeout.Token).AsTask().WaitAsync(risk.SignalTimeout, cancellationToken);
                if (found is not null)
                    evidence.Add(found);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Risk signal {Signal} took longer than {Timeout}; skipped.", signal.Name, risk.SignalTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Risk signal {Signal} failed; skipped.", signal.Name);
            }
        }

        var challenges = services.GetService<ISparkHumanChallenges>();
        var action = SparkRiskScoring.Decide(evidence, risk, challenges?.IsConfigured == true, out var score);
        TimeSpan? retryAfter = action == SparkRiskAction.Slow ? Jitter(risk.SlowRetryAfter) : null;
        var decision = new SparkRiskDecision(action, score, evidence, retryAfter);
        if (evidence.Count > 0 || risk.LogAllows || action != SparkRiskAction.Allow)
        {
            try
            {
                decision = decision with { DecisionId = await log.WriteAsync(context, decision, action == SparkRiskAction.ShadowQueue ? Deceptive.SparkDeceptiveMeasureNames.ShadowQueue : null, cancellationToken) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Writing a risk decision failed; the decision stands unlogged.");
            }
        }
        return decision;
    }

    public async Task RecordAsync(SparkRiskContext context, SparkRiskEvidence evidence, CancellationToken cancellationToken = default)
    {
        if (evidence.CountryBased)
            evidence = evidence with { Deterministic = false };
        if (evidence.Deterministic || evidence.DeceptiveTrigger)
            memory.Record(context.IdentityKey, evidence, options.CurrentValue.Risk.RecordedEvidenceLifetime);
        var action = SparkRiskScoring.Decide([evidence], options.CurrentValue.Risk, challengeAvailable: false, out var score);
        await log.WriteAsync(context, new SparkRiskDecision(action, score, [evidence]), null, cancellationToken);
    }

    public IReadOnlyList<SparkRiskEvidence> RecordedAgainst(string identityKey) => memory.Recorded(identityKey);

    public SparkRiskContext ContextFor(HttpContext httpContext, string point, string? subject = null, IReadOnlyDictionary<string, string?>? facts = null)
    {
        var visitor = httpContext.Features.Get<ISparkVisitor>();
        var address = SparkClientAddress.Key(httpContext.Connection.RemoteIpAddress);
        return new SparkRiskContext(point, visitor?.IdentityKey ?? "ip:" + address, address, subject)
        {
            Facts = facts ?? new Dictionary<string, string?>(),
            Http = httpContext,
        };
    }

    /// <summary>An honest slow-down with jitter (50–100 %), so a crowd that was slowed together does not return together.</summary>
    private static TimeSpan Jitter(TimeSpan baseline)
        => TimeSpan.FromMilliseconds(baseline.TotalMilliseconds * (0.5 + RandomNumberGenerator.GetInt32(0, 501) / 1000.0));
}
