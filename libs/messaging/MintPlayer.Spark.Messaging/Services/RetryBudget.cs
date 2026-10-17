using MintPlayer.Spark.Messaging.Models;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// A handler's retry budget (dependency-updates PRD §13.13.3, R30 and P18): the one place that decides
/// when a handler has used up <see cref="SparkMessage.MaxAttempts"/>.
/// <para>
/// Three things spend it. A genuine failure (<see cref="HandlerExecution.AttemptCount"/>) costs one
/// unit. A reclaim (<see cref="SparkMessage.ReclaimCount"/>: the host died mid-handler) costs one unit
/// for every handler that had not finished, because none of them could complete in that round. A
/// rate-limit park (<see cref="HandlerExecution.RateLimitParks"/>) costs
/// <c>1 / RateLimitParksPerAttempt</c> of a unit, counted with integer division, so with the defaults
/// 5 errors, 25 parks, or 2 errors and 15 parks exhaust it, and 4 errors with 4 parks, or 24 parks, do not.
/// </para>
/// </summary>
internal static class RetryBudget
{
    /// <summary>Units of <see cref="SparkMessage.MaxAttempts"/> the handler has spent.</summary>
    public static int Spent(HandlerExecution handler, int reclaims, int parksPerAttempt)
        => handler.AttemptCount + reclaims + handler.RateLimitParks / Math.Max(1, parksPerAttempt);

    /// <summary>Whether the handler may not run again.</summary>
    public static bool IsExhausted(HandlerExecution handler, int reclaims, int maxAttempts, int parksPerAttempt)
        => Spent(handler, reclaims, parksPerAttempt) >= maxAttempts;

    /// <summary>
    /// Whether rate-limit parks made up most of what the handler spent: more park units (exactly, not
    /// rounded down) than genuine failures and reclaims together.
    /// </summary>
    public static bool MostlyRateLimited(HandlerExecution handler, int reclaims, int parksPerAttempt)
        => handler.RateLimitParks > (handler.AttemptCount + reclaims) * (long)Math.Max(1, parksPerAttempt);

    /// <summary>
    /// The reason a message whose handlers all dead-lettered ends with: <see cref="EDeadLetterReason.NonRetryable"/>
    /// when no handler exhausted its budget (each was refused outright), <see cref="EDeadLetterReason.RateLimited"/>
    /// when every exhausted handler was mostly rate-limited, otherwise <see cref="EDeadLetterReason.MaxAttempts"/>.
    /// </summary>
    public static EDeadLetterReason ReasonFor(SparkMessage message, int parksPerAttempt)
    {
        var exhausted = message.Handlers
            .Where(h => h.Status == EHandlerStatus.DeadLettered
                        && IsExhausted(h, message.ReclaimCount, message.MaxAttempts, parksPerAttempt))
            .ToList();

        if (exhausted.Count == 0)
            return EDeadLetterReason.NonRetryable;

        return exhausted.All(h => MostlyRateLimited(h, message.ReclaimCount, parksPerAttempt))
            ? EDeadLetterReason.RateLimited
            : EDeadLetterReason.MaxAttempts;
    }
}
