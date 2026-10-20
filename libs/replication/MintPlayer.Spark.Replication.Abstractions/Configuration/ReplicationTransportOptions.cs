namespace MintPlayer.Spark.Replication.Abstractions.Configuration;

/// <summary>How an owner delivers approved replicas (warehouse WD17, WX1). <c>Spark:Replication:Transport</c>.</summary>
public enum ReplicationTransport
{
    /// <summary>
    /// Reads the RavenDB licence at startup (<c>/license/status</c>, WX13): ETL when the licence has
    /// RavenDB ETL, HTTP otherwise — and HTTP when the licence cannot be read.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The owner projects the approved manifest itself and pushes it over Spark messaging and the mTLS channel, and a
    /// cron job reconciles. Needs no licence feature and no extra subscription; works across RavenDB servers.
    /// </summary>
    Http = 1,

    /// <summary>
    /// RavenDB ETL tasks, one per consumer, with the approved scripts as transforms.
    /// Refuses startup on a licence without ETL (F18), with a message naming the fix.
    /// </summary>
    Etl = 2,
}

/// <summary>The <c>SparkModules</c> registry (WX4): <c>Spark:Replication:Registry</c>.</summary>
public class ReplicationRegistryOptions
{
    /// <summary>
    /// Operator-managed mode. The module never writes the registry: an operator creates each module's
    /// entry (name, URL, database, pinned thumbprint), and the module only reads. Closes the registry
    /// trust root — with write access, a compromised module could rewrite another module's pinned
    /// thumbprint and URL, and every destination is taken from the registry (F19). A missing or
    /// different entry for this module is logged as an error; replication still starts, so the
    /// endpoints answer and the operator can fix the entry without a restart loop.
    /// </summary>
    public bool ReadOnly { get; set; }
}

/// <summary>Push delivery on the HTTP transport: <c>Spark:Replication:Push</c>.</summary>
public class ReplicationPushOptions
{
    /// <summary>
    /// The retry ladder of an unreachable, 5xx or 429 consumer (each push waits this long before the next try; the
    /// last entry repeats). Default the reference system's 1 m / 5 m / 30 m / 2 h. A 4xx answer is never retried.
    /// Empty here on purpose (a configured array appends to a non-empty initializer); read
    /// <see cref="ResolvedRetryLadder"/>.
    /// </summary>
    public TimeSpan[] RetryLadder { get; set; } = [];

    /// <summary>The ladder used when none is configured.</summary>
    public static readonly TimeSpan[] DefaultRetryLadder =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    /// <summary><see cref="RetryLadder"/>, or <see cref="DefaultRetryLadder"/> when none is configured.</summary>
    public TimeSpan[] ResolvedRetryLadder => RetryLadder.Length > 0 ? RetryLadder : DefaultRetryLadder;

    /// <summary>
    /// How long a push keeps retrying before it is dead-lettered. The next reconcile repairs what a dead push missed.
    /// </summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Documents per push request; the dirty-set flush and the reconcile split larger sets.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The six-field cron schedule (UTC) of the dirty-set flush. Default every 2 seconds (Q-W25).</summary>
    public string DirtySetFlushSchedule { get; set; } = "*/2 * * * * *";
}

/// <summary>The owner's reconcile on the HTTP transport: <c>Spark:Replication:Reconcile</c>.</summary>
public class ReplicationReconcileOptions
{
    /// <summary>The cron schedule (UTC). Default hourly (Q-W25). One also runs right after an approval: the initial load.</summary>
    public string Schedule { get; set; } = "0 * * * *";

    /// <summary>Digest entries per page.</summary>
    public int PageSize { get; set; } = 500;
}

/// <summary>Write-back to an owner (§4.2.5, F14): <c>Spark:Replication:WriteBack</c>.</summary>
public class ReplicationWriteBackOptions
{
    /// <summary>
    /// How long a save of a write-back replica waits for the owner's answer within the request. Past it, or when the
    /// owner is unreachable, the edit is queued in the same commit and the user is told it is pending at the owner.
    /// </summary>
    public TimeSpan SynchronousTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The retry ladder of a queued edit; the last entry repeats. Empty: <see cref="ReplicationPushOptions.DefaultRetryLadder"/>.</summary>
    public TimeSpan[] RetryLadder { get; set; } = [];

    /// <summary><see cref="RetryLadder"/>, or the default ladder when none is configured.</summary>
    public TimeSpan[] ResolvedRetryLadder => RetryLadder.Length > 0 ? RetryLadder : ReplicationPushOptions.DefaultRetryLadder;

    /// <summary>
    /// How long a queued edit keeps retrying an unreachable owner before it gives up (GaveUp: the initiator is
    /// notified, and mailed when mail is configured). A GaveUp edit can be re-driven with <c>RetrySyncAction</c>.
    /// </summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromHours(48);
}
