using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>Metadata keys replication writes (never starting with <c>@</c>, which RavenDB reserves).</summary>
internal static class ReplicationMetadata
{
    /// <summary>On an owner's source document: its version stamp (monotonic per document).</summary>
    public const string Version = "Spark-Replication-Version";

    /// <summary>On a consumer's replica: the module it came from.</summary>
    public const string ReplicaSource = "Spark-Replica-Source";

    /// <summary>On a replica: the contract version its payload has.</summary>
    public const string ReplicaContractVersion = "Spark-Replica-Contract-Version";

    /// <summary>On a replica: the source's version stamp it holds.</summary>
    public const string ReplicaVersion = "Spark-Replica-Version";

    /// <summary>On a replica: the source's change vector, informational.</summary>
    public const string ReplicaSourceChangeVector = "Spark-Replica-Source-Change-Vector";

    /// <summary>The fact a before-delete records for the after-commit push: the deletion's version stamp.</summary>
    public const string DeletedVersionFact = "ReplicationVersion";

    /// <summary>
    /// The next version stamp after <paramref name="previous"/>: a hybrid clock, <c>max(previous + 1, now ticks)</c>.
    /// Monotonic per document like a counter, and — unlike a counter — never restarts low when a document is
    /// overwritten without being loaded, so a consumer never mistakes the new state for an old one.
    /// </summary>
    public static long Next(long previous, DateTimeOffset now) => Math.Max(previous + 1, now.UtcTicks);

    public static long Read(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        decimal m => (long)m,
        string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}

/// <summary>Owner side: the consumers subscribed to one contract. <c>SparkReplicaSubscriptions/{contract}</c>.</summary>
public class ReplicaSubscriptionSet
{
    public static string For(string contract) => $"SparkReplicaSubscriptions/{contract}";

    public string? Id { get; set; }
    public required string Contract { get; set; }
    public List<ReplicaConsumer> Consumers { get; set; } = [];
}

/// <summary>One consumer of a contract.</summary>
public class ReplicaConsumer
{
    public required string Module { get; set; }

    /// <summary>The version delivered.</summary>
    public int Version { get; set; }

    /// <summary>The consumer's collection: the ETL load target.</summary>
    public required string TargetCollection { get; set; }

    public ReplicationTransport Transport { get; set; }
    public DateTime SubscribedAtUtc { get; set; }
}

/// <summary>Consumer side: what a source agreed to deliver. <c>SparkReplicaSources/{module}</c>.</summary>
public class ReplicaSourceState
{
    public static string For(string module) => $"SparkReplicaSources/{module}";

    public string? Id { get; set; }
    public required string Module { get; set; }
    public ReplicationTransport Transport { get; set; }
    public List<ReplicaSourceContract> Contracts { get; set; } = [];
    public DateTime SubscribedAtUtc { get; set; }
    public DateTime? LastReconciledAtUtc { get; set; }
}

public class ReplicaSourceContract
{
    public required string Contract { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// Consumer side: a replica its source deleted. Kept so a late, older push cannot resurrect it (warehouse §4.2).
/// <c>SparkReplicaTombstones/{documentId}</c>.
/// </summary>
public class ReplicaTombstone
{
    public static string For(string replicaId) => $"SparkReplicaTombstones/{replicaId}";

    public string? Id { get; set; }
    public required string ReplicaId { get; set; }
    public required string SourceModule { get; set; }
    public required string Contract { get; set; }
    public long Version { get; set; }
    public DateTime DeletedAtUtc { get; set; }
}

/// <summary>
/// Owner side: a document of a dirty-set contract changed since the last flush. <c>SparkReplicaDirty/{contract}/{documentId}</c>,
/// so further changes overwrite the one marker.
/// </summary>
public class ReplicaDirtyMarker
{
    public const string Prefix = "SparkReplicaDirty/";

    public static string For(string contract, string documentId) => $"{Prefix}{contract}/{documentId}";

    public string? Id { get; set; }
    public required string Contract { get; set; }
    public required string DocumentId { get; set; }
    public DateTime MarkedAtUtc { get; set; }
}

/// <summary>
/// Owner side: the last write-back a module applied to one document (F15) — per-document order and op-id
/// idempotency. Written in the same commit as the edit. <c>SparkSyncInbox/{module}/{documentId}</c>.
/// </summary>
public class SyncInboxEntry
{
    /// <summary>How many recent op ids are remembered per document and module.</summary>
    public const int RememberedOps = 32;

    public static string For(string module, string documentId) => $"SparkSyncInbox/{module}/{documentId}";

    public string? Id { get; set; }
    public required string Module { get; set; }
    public required string DocumentId { get; set; }
    public long LastSequence { get; set; }
    public List<string> RecentOpIds { get; set; } = [];
}
