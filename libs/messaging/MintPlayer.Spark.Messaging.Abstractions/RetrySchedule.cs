using System.Globalization;

namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// The built-in retry schedules. Shape is chosen by a factory rather than by setting properties, so
/// that contradictory combinations — a ladder <i>and</i> a growth factor, a linear step <i>and</i> an
/// explicit rung list — have no way to be written.
/// </summary>
public static class RetrySchedule
{
    /// <summary>
    /// An explicit list of delays: attempt 1 waits the first, attempt 2 the second, and the attempt
    /// after the last rung is dead-lettered.
    /// </summary>
    /// <remarks>
    /// The attempt limit is <b>derived</b> (<c>rungs + 1</c>) rather than configured separately.
    /// That is what makes the old defect unrepresentable: a five-rung ladder with
    /// <c>MaxAttempts = 5</c> dead-lettered on the fifth failure and so could never reach its fifth
    /// rung. The ladder <i>is</i> the schedule.
    /// </remarks>
    public static IRetrySchedule Ladder(params TimeSpan[] delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        if (delays.Length == 0)
            throw new ArgumentException("A ladder needs at least one delay.", nameof(delays));

        return new LadderSchedule(delays);
    }

    /// <summary>
    /// A ladder written the way it is written in configuration: <c>"5s 30s 2m 10m"</c>.
    /// </summary>
    /// <remarks>
    /// The same grammar in code and in JSON, deliberately — and a <b>scalar string</b> rather than an
    /// array because configuration binds arrays by <i>element position</i>. Measured: a non-empty
    /// default survives binding and is appended to; two configuration layers overlay element-wise, so
    /// a base <c>[1m,5m,1h]</c> overridden by <c>[7s]</c> yields <c>[7s,5m,1h]</c>, a ladder nobody
    /// wrote; and re-binding the same object doubles it. A scalar is replaced by the last source and
    /// is idempotent under re-binding.
    /// </remarks>
    public static IRetrySchedule Ladder(string delays) => Ladder(ParseDelays(delays));

    /// <summary>Waits <paramref name="step"/> more each time, never more than <paramref name="cap"/>.</summary>
    public static IRetrySchedule Linear(TimeSpan step, TimeSpan cap, int attempts)
    {
        if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(step));
        if (cap < step) throw new ArgumentOutOfRangeException(nameof(cap), "The cap must be at least one step.");
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));

        return new ComputedSchedule(attempts, attempt =>
        {
            var delay = step * attempt;
            return delay > cap ? cap : delay;
        });
    }

    /// <summary>Multiplies the wait by <paramref name="factor"/> each time, never past <paramref name="cap"/>.</summary>
    public static IRetrySchedule Exponential(TimeSpan initial, double factor, TimeSpan cap, int attempts)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (factor <= 1) throw new ArgumentOutOfRangeException(nameof(factor), "A factor of 1 or less never grows.");
        if (cap < initial) throw new ArgumentOutOfRangeException(nameof(cap), "The cap must be at least the initial delay.");
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));

        return new ComputedSchedule(attempts, attempt =>
        {
            // Math.Pow on a large attempt count overflows to Infinity; the cap comparison handles it
            // without a special case, because Infinity > cap.
            var seconds = initial.TotalSeconds * Math.Pow(factor, attempt - 1);
            return seconds >= cap.TotalSeconds ? cap : TimeSpan.FromSeconds(seconds);
        });
    }

    /// <summary>Never retries: the first failure is terminal.</summary>
    /// <remarks>
    /// The right choice for a periodic message. Retrying a failed minute races two minutes' work, and
    /// a stuck downstream accumulates a backlog that never drains — the next tick <i>is</i> the retry.
    /// </remarks>
    public static IRetrySchedule None { get; } = new ComputedSchedule(1, _ => TimeSpan.Zero);

    /// <summary>The schedule used when a lane declares none: three reachable rungs, ~2m35s in total.</summary>
    public static IRetrySchedule Default { get; } = Ladder("5s 30s 2m");

    /// <summary>
    /// Parses the shared duration grammar: a space-separated list of <c>5s</c> / <c>2m</c> /
    /// <c>1h</c> / <c>3d</c> tokens, or anything <see cref="TimeSpan.Parse(string)"/> accepts.
    /// </summary>
    public static TimeSpan[] ParseDelays(string delays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delays);

        return [.. delays
            .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseDelay)];
    }

    private static TimeSpan ParseDelay(string token)
    {
        var suffix = token[^1];
        var head = token[..^1];

        if (char.IsDigit(suffix) || !double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new FormatException(
                    $"'{token}' is not a duration. Expected a number followed by s, m, h or d (for example '30s' or '2m'), "
                    + "or a TimeSpan such as '00:00:30'.");
        }

        return char.ToLowerInvariant(suffix) switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new FormatException(
                $"'{token}' has an unknown unit '{suffix}'. Expected s, m, h or d."),
        };
    }

    private sealed class LadderSchedule(TimeSpan[] delays) : IRetrySchedule
    {
        public RetryDecision Next(int attempt) => attempt < 1
            ? throw new ArgumentOutOfRangeException(nameof(attempt))
            : attempt > delays.Length
                // Past the last rung: dead-letter rather than clamp. Clamping is what silently
                // repeated the final delay forever and made the declared last rung unreachable.
                ? new RetryDecision.DeadLetter($"AttemptsExhausted after {delays.Length + 1} attempts")
                : new RetryDecision.RetryAfter(delays[attempt - 1]);

        public override string ToString()
            => $"ladder [{string.Join(" ", delays.Select(Describe))}] · {delays.Length + 1} attempts · worst case {Describe(Total(delays))}";

        private static TimeSpan Total(TimeSpan[] d) => d.Aggregate(TimeSpan.Zero, (a, b) => a + b);
    }

    private sealed class ComputedSchedule(int attempts, Func<int, TimeSpan> delay) : IRetrySchedule
    {
        public RetryDecision Next(int attempt) => attempt < 1
            ? throw new ArgumentOutOfRangeException(nameof(attempt))
            : attempt >= attempts
                ? new RetryDecision.DeadLetter($"AttemptsExhausted after {attempts} attempts")
                : new RetryDecision.RetryAfter(delay(attempt));

        public override string ToString()
        {
            var total = Enumerable.Range(1, Math.Max(attempts - 1, 0))
                .Aggregate(TimeSpan.Zero, (sum, attempt) => sum + delay(attempt));
            return $"{attempts} attempts · worst case {Describe(total)}";
        }
    }

    /// <summary>A duration in the same grammar schedules are written in: <c>5s</c>, <c>2m</c>, <c>3d</c>.</summary>
    public static string Describe(TimeSpan value) => value switch
    {
        { TotalDays: >= 1 } => $"{value.TotalDays:0.##}d",
        { TotalHours: >= 1 } => $"{value.TotalHours:0.##}h",
        { TotalMinutes: >= 1 } => $"{value.TotalMinutes:0.##}m",
        _ => $"{value.TotalSeconds:0.##}s",
    };
}
