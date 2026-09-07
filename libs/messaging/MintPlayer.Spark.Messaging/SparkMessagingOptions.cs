namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Durable-messaging settings, bound from the <c>Spark:Messaging</c> configuration section and then
/// overridable in code.
/// </summary>
/// <remarks>
/// <b>Never give a collection property a non-empty initializer here.</b> The rule is about
/// <i>collections</i>, not about initializers in general — the binder treats the two kinds
/// differently:
/// <list type="bullet">
/// <item>
/// <b>Scalars</b> (<c>int</c>, <c>TimeSpan</c>, <c>string</c>) — the binder <b>replaces</b> the value.
/// Initialize them freely; <c>MaxAttempts = 5</c> below is correct and a configured <c>1</c> wins.
/// </item>
/// <item>
/// <b>Collections</b> (<c>T[]</c>, <c>List&lt;T&gt;</c>) — the binder <b>appends</b> to whatever is
/// already there. A non-empty initializer therefore survives binding and stays <b>first</b>, which is
/// the position that usually decides behaviour.
/// </item>
/// </list>
/// Note that <c>= []</c> on a collection is not optional politeness: without it the property is
/// <see langword="null"/> and the <c>Resolved*</c> member below would throw.
/// <para>
/// That is not a theoretical concern. It shipped once already, as <b>F14</b>: a configured
/// <c>SparkModulesUrls</c> landed behind the hardcoded <c>http://localhost:8080</c>, and since
/// <c>DocumentStore</c> connects to the first URL, every deployment that configured where its module
/// registry lived was still talking to localhost — with no error and a config file that said
/// otherwise. It was found from the far end, days later, by two processes that could not see each
/// other's data.
/// </para>
/// <para>
/// The pattern to follow instead is <see cref="BackoffDelays"/>: default the property to empty, put
/// the real default in a separate <c>Default*</c> constant, and expose a <c>Resolved*</c> member that
/// picks one. Consumers read the resolved member; nothing reads the raw property.
/// </para>
/// </remarks>
public class SparkMessagingOptions
{
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How often <c>MessageRetrySweeper</c> wakes up messages whose retry backoff or
    /// broadcast delay has elapsed, by setting their <c>WakeUp</c> gate so the queue
    /// subscriptions re-evaluate and redeliver them. This is the redelivery granularity:
    /// a due message is picked up at most this long after <c>NextAttemptAtUtc</c>.
    /// </summary>
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before each retry, indexed by attempt. Read it through
    /// <see cref="ResolvedBackoffDelays"/>, never directly.
    /// </summary>
    /// <remarks>
    /// Empty by default <b>on purpose</b>, and the reason is the same one that produced F14 in the
    /// replication options: .NET's configuration binder does not replace a collection that already has
    /// elements, it <i>appends</i> to it. A hardcoded initializer here would survive binding and stay
    /// first, so an app configuring a faster schedule would still wait the default five seconds on its
    /// first retry — silently, with a config file that said otherwise. Applying the default at the
    /// point of use is what keeps a configured value from queueing up behind it.
    /// </remarks>
    public TimeSpan[] BackoffDelays { get; set; } = [];

    /// <summary>The schedule used when nothing is configured: 5s, 30s, 2m, 10m, 1h.</summary>
    public static readonly TimeSpan[] DefaultBackoffDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>The delays to actually use: what was configured, or the default when nothing was.</summary>
    public TimeSpan[] ResolvedBackoffDelays =>
        BackoffDelays.Length > 0 ? BackoffDelays : DefaultBackoffDelays;

    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// How long a pump's claim on a message stays valid before <c>MessageRetrySweeper</c> treats the
    /// message as abandoned and returns it to <see cref="Models.EMessageStatus.Pending"/>.
    /// <para>
    /// Renewed at <see cref="ClaimRenewInterval"/> while a handler is running, so this bounds how
    /// long an <i>abandoned</i> message waits — not how long a handler may take. It must still
    /// exceed the container's <c>terminationGracePeriodSeconds</c>, or a pod stopped mid-handler has
    /// its message reclaimed while it is still draining.
    /// </para>
    /// </summary>
    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a held claim is renewed while its handler runs. Must be comfortably shorter than
    /// <see cref="ClaimTtl"/>; a third of it is a reasonable ratio.
    /// </summary>
    public TimeSpan ClaimRenewInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether each queue gets its own RavenDB data subscription, or all queues share one.
    /// </summary>
    public ESubscriptionMode SubscriptionMode { get; set; } = ESubscriptionMode.SingleSubscription;

    /// <summary>
    /// How long a single message may occupy its queue's pump before it is cancelled and parked.
    /// <para>
    /// This bounds the one failure mode that a retry budget cannot: a handler that <b>hangs</b>. A
    /// handler that throws is parked and the pump moves straight on to the next message, so
    /// failures interleave rather than blocking the head of the lane, and
    /// <see cref="MaxAttempts"/> eventually dead-letters them. But a handler that never returns —
    /// an HTTP call with no timeout, a deadlock, an unbounded loop — holds its lane for ever, and
    /// nothing else rescues it: one message is in flight at a time by design, and the claim is
    /// renewed while it runs, so even the sweeper's reclaim never fires.
    /// </para>
    /// <para>
    /// Generous by default, because the cost of cutting a legitimately slow handler short is worse
    /// than a stuck lane: report parsing and commit assembly are minutes-long by nature. Raise it
    /// for a workload with a genuinely longer tail rather than lowering it to catch hangs sooner —
    /// a hang blocks one queue, while a too-short timeout corrupts every slow message on it.
    /// </para>
    /// <para>
    /// Cancellation is cooperative: it cancels the token the handler was given. A handler that
    /// ignores its <see cref="CancellationToken"/> cannot be interrupted, which is worth knowing
    /// before assuming this makes lanes unblockable.
    /// </para>
    /// </summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// How the messaging host maps queues onto RavenDB data subscriptions.
/// </summary>
public enum ESubscriptionMode
{
    /// <summary>
    /// One subscription (<c>SparkMessaging</c>) for every queue, with per-queue FIFO provided by
    /// in-process pumps. The default, because RavenDB caps subscriptions per database — 3 on a
    /// Community licence — so one-per-queue turned "how many queues may this app have?" into a
    /// licensing question, and exceeding it killed queues silently.
    /// </summary>
    SingleSubscription = 0,

    /// <summary>
    /// One subscription per queue name (<c>SparkMessaging-{queue}</c>), the behaviour before the
    /// single-subscription rework. Costs one subscription per queue, and is worth it only where the
    /// licence has headroom and server-side per-queue isolation is genuinely wanted — a queue whose
    /// documents are never even delivered to this process cannot be delayed by a busy feeder.
    /// </summary>
    SubscriptionPerQueue = 1,
}
