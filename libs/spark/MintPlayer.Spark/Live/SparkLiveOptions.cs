namespace MintPlayer.Spark.Live;

/// <summary>Where live updates (X4) learn that data changed (D33).</summary>
public enum SparkLiveSource
{
    /// <summary>
    /// Writes through RavenDB sessions of <b>this process</b> (the persistent-object pipeline, custom actions,
    /// message handlers, jobs), seen from the document store's own events, plus explicit
    /// <see cref="Abstractions.Live.ISparkLiveNotifier"/> calls. Instant, no extra connection, blind to other
    /// processes and to patches.
    /// </summary>
    Actions,

    /// <summary>
    /// Every committed write from any process, through the RavenDB Changes API: one multiplexed connection per
    /// database per process, watching only the documents and collections someone is subscribed to. Not a data
    /// subscription, so the Community licence's three are untouched (measured, PRD §12).
    /// </summary>
    Changes,

    /// <summary>Both, de-duplicated by document id and change vector (the default).</summary>
    Both,
}

/// <summary>
/// Live updates (X4, <c>docs/guide-live-updates.md</c>), bound from <c>Spark:Live</c>, then
/// <c>spark.AddLive(o =&gt; …)</c> (code wins). Every value has a default; an app sets none of them for the
/// common case (D34).
/// </summary>
public sealed class SparkLiveOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:Live";

    /// <summary>The change source (D33). Default <see cref="SparkLiveSource.Both"/>.</summary>
    public SparkLiveSource Source { get; set; } = SparkLiveSource.Both;

    /// <summary>
    /// At most one evaluation, and so at most one push, per subscription per this interval; the latest state
    /// wins (D36: 500 ms). The first change after a quiet period is evaluated at once.
    /// </summary>
    public TimeSpan Throttle { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Subscriptions one socket may hold (D36: 50). The next one is refused with <c>limit</c>.</summary>
    public int MaxSubscriptionsPerSocket { get; set; } = 50;

    /// <summary>Sockets one user may hold open at once (D36: 5); anonymous callers count per IP address.</summary>
    public int MaxSocketsPerUser { get; set; } = 5;

    /// <summary>A <c>ping</c> frame after this much silence, below nginx's default 60 s <c>proxy_read_timeout</c>.</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A frame the client has not taken within this long closes the socket as a slow consumer.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every subscription is re-evaluated at least this often even without a change, so a revoked right or a
    /// lost membership takes effect without waiting for the next write.
    /// </summary>
    public TimeSpan Revalidate { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The socket closes with <c>4401</c> at the login ticket's expiry or after this long, whichever is first,
    /// and the client reconnects with its current cookie: the principal a socket froze at its handshake is
    /// never older than this. 30 minutes is ASP.NET Identity's default security-stamp validation interval.
    /// </summary>
    public TimeSpan MaxSocketLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a query evaluation waits for the indexes over a changed collection to catch up before it
    /// re-runs (a failure bound: it ends as soon as they did).
    /// </summary>
    public TimeSpan IndexWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The largest client frame accepted; a larger one closes the socket (1009).</summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;
}
