using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// A push from an owner to a consumer on the HTTP transport: <c>POST /spark/replicas/apply</c>, over the owner's module
/// certificate (warehouse §4.2.3). Each item is the document as the owner holds it now, projected through the approved
/// manifest.
/// </summary>
public class ReplicaApplyRequest
{
    /// <summary>The owner. Must match the presented module certificate and be a source of <see cref="TargetCollection"/>.</summary>
    public required string SourceModule { get; set; }

    /// <summary>The consumer's replica collection.</summary>
    public required string TargetCollection { get; set; }

    /// <summary>
    /// The manifest hash the payloads were projected with: the consumer's current <c>ReplicationHash</c> or the hash
    /// the owner last acknowledged as approved (Q-W29), else <c>409 manifest-mismatch</c>.
    /// </summary>
    public required string Hash { get; set; }

    public List<ReplicaItem> Items { get; set; } = [];
}

/// <summary>The state of one document, as the owner holds it now.</summary>
public class ReplicaItem
{
    /// <summary>The document id: the owner's, unique across every source of the collection.</summary>
    public required string Id { get; set; }

    /// <summary>
    /// The owner's change vector of this state. The consumer applies an item only when it is newer than the one its
    /// replica (or tombstone) holds (<c>SparkChangeVector.Compare</c>), so a late, duplicated or reordered push is a
    /// no-op. Null on a deletion whose version the owner no longer knows: it then supersedes whatever is held.
    /// </summary>
    public string? SourceChangeVector { get; set; }

    /// <summary>Whether the document no longer exists at the owner (a tombstone).</summary>
    public bool Deleted { get; set; }

    /// <summary>The projected replica, as JSON in the owner store's serialization; null when <see cref="Deleted"/>.</summary>
    public JsonElement? Payload { get; set; }
}

/// <summary>What the consumer did with each item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReplicaApplyOutcome>))]
public enum ReplicaApplyOutcome
{
    /// <summary>Stored (or deleted, with a tombstone).</summary>
    Applied,

    /// <summary>Not newer than what the consumer holds: a duplicate or a late push. Answered as success.</summary>
    Stale,

    /// <summary>Neither newer nor older (a conflicted multi-node owner): a no-op; the owner's next state dominates both.</summary>
    Concurrent,

    /// <summary>Refused as poison: the id is held by another source, or the payload does not fit the replica type.</summary>
    Refused,
}

/// <summary>The consumer's answer to a <see cref="ReplicaApplyRequest"/>.</summary>
public class ReplicaApplyResult
{
    public List<ReplicaApplyItemResult> Items { get; set; } = [];
}

public class ReplicaApplyItemResult
{
    public required string Id { get; set; }

    public ReplicaApplyOutcome Outcome { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// One page of a consumer's digest of a replica collection: <c>GET /spark/replicas/digest?collection=&amp;after=&amp;take=</c>,
/// what the owner's reconcile merge-joins with its own documents. Ordered by id; <see cref="Next"/> is the cursor of
/// the following page, null on the last.
/// </summary>
public class ReplicaDigestPage
{
    public List<ReplicaDigestEntry> Items { get; set; } = [];

    public string? Next { get; set; }
}

/// <summary>One replica (or tombstone) the consumer holds from the asking owner.</summary>
public class ReplicaDigestEntry
{
    public required string Id { get; set; }

    public string? SourceChangeVector { get; set; }

    public bool Tombstone { get; set; }
}
