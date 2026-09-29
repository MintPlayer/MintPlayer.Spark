namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Settings for one queue, keyed by queue name in <see cref="SparkMessagingOptions.Queues"/> and
/// bound from <c>Spark:Messaging:Queues:{name}</c>. Every property is optional: an unset one falls
/// back to the global <see cref="SparkMessagingOptions"/> value, or means "no limit".
/// <para>
/// <b>Configuration beats code here.</b> A library or app may declare a queue's defaults in code
/// (<c>AddMessaging(o =&gt; o.Queues["mail-bulk"] = …)</c> or any <c>Configure&lt;SparkMessagingOptions&gt;</c>),
/// and <c>Spark:Messaging:Queues</c> — appsettings, environment variables
/// (<c>Spark__Messaging__Queues__mail-bulk__MaxPerInterval=20</c>), user secrets — is applied over
/// them afterwards, property by property. An operator can therefore retune a lane without a redeploy.
/// </para>
/// </summary>
/// <remarks>
/// <b>Throttling</b> (<see cref="MaxPerInterval"/> / <see cref="Interval"/>, and
/// <see cref="BatchSize"/> / <see cref="MinDelayBetweenBatches"/>) is admission, not waiting: a message
/// over budget is written back once with <c>NextAttemptAtUtc</c> = its reserved slot and leaves the
/// lane at once, so a throttled queue never blocks another queue. Slots are reserved GCRA-style in
/// memory: each throttled message is deferred once, not re-queued on every poll. The long-run rate is
/// exact; bursts are quantised by <see cref="SparkMessagingOptions.FallbackPollInterval"/> (the sweeper
/// wakes due messages on that tick). After a restart the reservations are gone and a deferred message
/// is deferred once more.
/// </remarks>
public class SparkQueueOptions
{
    /// <summary>
    /// Start rate: <c>MaxPerInterval</c> per <see cref="Interval"/>, i.e. one every
    /// <c>Interval / MaxPerInterval</c>, with a burst allowance of <c>MaxPerInterval</c> for a queue
    /// that has been idle (GCRA). 0 = unlimited.
    /// <para>
    /// This is a rate with a burst, not a hard cap per window: a burst followed by the steady rate can
    /// put up to about <c>2 × MaxPerInterval</c> starts into one sliding <see cref="Interval"/>, and the
    /// sweeper's tick adds clumping. For a cap with no burst, express it as the rate alone —
    /// <c>MaxPerInterval = 1</c>, <c>Interval = 3s</c> instead of 20 per minute.
    /// </para>
    /// </summary>
    public int MaxPerInterval { get; set; }

    /// <summary>The window for <see cref="MaxPerInterval"/>. Default one minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Pacing window: after this many messages start back to back, wait
    /// <see cref="MinDelayBetweenBatches"/> before the next one. 0 = no batching. A batch is a pacing
    /// window only — each message keeps its own status and retries.
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>The pause between batches (see <see cref="BatchSize"/>).</summary>
    public TimeSpan MinDelayBetweenBatches { get; set; }

    /// <summary>
    /// Messages of this queue handled at once. Default 1, which is what makes a queue FIFO; above 1
    /// the queue gives up ordering for throughput. <see cref="ESubscriptionMode.SingleSubscription"/>
    /// only — in <see cref="ESubscriptionMode.SubscriptionPerQueue"/> mode it is ignored with a warning.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Attempts per handler for messages published to this queue. Null = the global value.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>
    /// Retry delays for this queue, indexed by attempt. Empty = the global schedule. Read it through
    /// <see cref="ResolveBackoff"/>. Empty by default for the same binder reason as
    /// <see cref="SparkMessagingOptions.BackoffDelays"/>; a configured schedule replaces a
    /// code-declared one rather than being appended to it.
    /// </summary>
    public TimeSpan[] Backoff { get; set; } = [];

    /// <summary>This queue's schedule, or <paramref name="global"/> when none is set.</summary>
    public TimeSpan[] ResolveBackoff(TimeSpan[] global) => Backoff.Length > 0 ? Backoff : global;

    /// <summary>
    /// Which queues the single feeder serves first (#460, M16/M16b). Default <see cref="SparkQueuePriority.Normal"/>.
    /// <para>
    /// Strict across the whole queue: on each wake-up the feeder takes the top
    /// <see cref="SparkMessagingOptions.FeederBatchSize"/> claimable messages by priority, then by
    /// server-assigned enqueue order, so a High message published behind any Low backlog is served on
    /// the next wake-up. Low is never starved: every message the subscription delivers is served in
    /// that batch too, so a Low message waits at most for the subscription to reach it. Stamped on the
    /// message at publish, so a changed setting applies to messages published after it.
    /// <see cref="ESubscriptionMode.SingleSubscription"/> only: in
    /// <see cref="ESubscriptionMode.SubscriptionPerQueue"/> mode each queue has its own subscription
    /// and nothing is shared to prioritise.
    /// </para>
    /// </summary>
    public SparkQueuePriority Priority { get; set; } = SparkQueuePriority.Normal;

    /// <summary>Whether any admission limit is configured.</summary>
    internal bool IsThrottled => (MaxPerInterval > 0 && Interval > TimeSpan.Zero) || BatchSize > 0;
}

/// <summary>
/// A queue's priority in the single feeder (<see cref="SparkQueueOptions.Priority"/>). Bound by name or
/// number from <c>Spark:Messaging:Queues:{name}:Priority</c>.
/// </summary>
public enum SparkQueuePriority
{
    /// <summary>Served after the other priorities: bulk and campaign traffic.</summary>
    Low = -1,

    /// <summary>The default for a queue without a setting.</summary>
    Normal = 0,

    /// <summary>Served first: a message somebody is waiting for (password reset, confirmation).</summary>
    High = 1,
}
