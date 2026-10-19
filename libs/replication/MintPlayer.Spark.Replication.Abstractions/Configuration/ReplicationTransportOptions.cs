namespace MintPlayer.Spark.Replication.Abstractions.Configuration;

/// <summary>How an owner delivers its contracts (warehouse WD1 refined, WX1). <c>Spark:Replication:Transport</c>.</summary>
public enum ReplicationTransport
{
    /// <summary>
    /// Reads the RavenDB licence at startup (<c>/license/status</c>, WX13): ETL when the licence has
    /// RavenDB ETL, HTTP otherwise — and HTTP when the licence cannot be read.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Outbox → HTTPS push to every consumer, plus the consumers' digest reconcile. Needs no licence
    /// feature and no extra subscription; works across RavenDB servers.
    /// </summary>
    Http = 1,

    /// <summary>
    /// RavenDB ETL tasks, one per consumer, with transforms the owner generates from its contracts.
    /// Refuses startup on a licence without ETL (F18), with a message naming the fix.
    /// </summary>
    Etl = 2,
}

/// <summary>One contract this module reads: <c>Spark:Replication:Consume:{contract}</c>.</summary>
public class ReplicationConsumeOptions
{
    /// <summary>
    /// The modules that publish the contract to this one (<c>Consume:{contract}:Sources</c>). Several
    /// sources may feed one contract: every replica records the module it came from, producer ids must be
    /// unique across modules, and an id already held by another source is refused as poison.
    /// </summary>
    public string[] Sources { get; set; } = [];
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
    /// Attempts per push before it is dead-lettered (the messaging retry budget). An unreachable or 5xx
    /// consumer parks the push with backoff; one past the budget is repaired by the consumer's next
    /// reconcile. A 4xx answer is never retried: it is dead-lettered at once, with its reason.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>How long to wait before retrying an unreachable or 5xx consumer.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Items per push request; the dirty-set flush splits larger sets.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The six-field cron schedule (UTC) of the dirty-set flush. Default every 2 seconds (Q-W25).</summary>
    public string DirtySetFlushSchedule { get; set; } = "*/2 * * * * *";
}

/// <summary>The consumer's digest reconcile: <c>Spark:Replication:Reconcile</c>.</summary>
public class ReplicationReconcileOptions
{
    /// <summary>The cron schedule (UTC). Default hourly (Q-W25). A run also happens at startup: that is the initial load.</summary>
    public string Schedule { get; set; } = "0 * * * *";

    /// <summary>Digest entries per page.</summary>
    public int PageSize { get; set; } = 500;
}

/// <summary>Write-back delivery to an owner (F14): <c>Spark:Replication:WriteBack</c>.</summary>
public class ReplicationWriteBackOptions
{
    /// <summary>Attempts before a request is Failed. A Failed request can be re-driven.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>The first retry delay; each next one doubles, up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest delay between two attempts.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);
}
