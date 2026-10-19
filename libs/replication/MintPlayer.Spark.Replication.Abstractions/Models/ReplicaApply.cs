using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// A push from an owner to a consumer: <c>POST /spark/replicas/apply</c>, over the owner's module certificate
/// (warehouse WX1). Also the answer of <c>POST /spark/replicas/fetch</c>, which the reconcile applies the same way.
/// </summary>
public class ReplicaApplyRequest
{
    /// <summary>The owner. Must match the presented module certificate; every item is recorded as coming from it.</summary>
    public required string SourceModule { get; set; }

    /// <summary>The contract's lower-kebab name.</summary>
    public required string Contract { get; set; }

    /// <summary>The contract version the payloads have.</summary>
    public required int Version { get; set; }

    public required List<ReplicaItem> Items { get; set; }
}

/// <summary>The state of one document, as the owner holds it now.</summary>
public class ReplicaItem
{
    /// <summary>The document id: the owner's, unique across every source of the contract.</summary>
    public required string Id { get; set; }

    /// <summary>
    /// The owner's version stamp: monotonic per document. The consumer applies an item only when it is
    /// higher than the one it holds (or its tombstone holds), so a late, duplicated or reordered push is a
    /// no-op. Zero on a deletion whose version the owner no longer knows: the consumer then supersedes
    /// whatever it holds.
    /// </summary>
    public long ReplicationVersion { get; set; }

    /// <summary>The owner's change vector, informational (the reconcile shows it).</summary>
    public string? ChangeVector { get; set; }

    /// <summary>Whether the document no longer exists at the owner (a tombstone).</summary>
    public bool Deleted { get; set; }

    /// <summary>The contract record, as JSON in the owner store's serialization; null when <see cref="Deleted"/>.</summary>
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

    /// <summary>
    /// Refused as poison: the id is held by another source, or the payload does not fit the replica type.
    /// The owner dead-letters the push with this reason; the digest mismatch stays visible until fixed.
    /// </summary>
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
/// One page of an owner's digest: <c>GET /spark/replicas/{contract}/digest?version=&amp;after=&amp;take=</c>.
/// Ordered by id; <see cref="Next"/> is the cursor for the following page, null on the last.
/// </summary>
public class ReplicaDigestPage
{
    public List<ReplicaDigestEntry> Items { get; set; } = [];
    public string? Next { get; set; }
}

/// <summary>One document in a digest.</summary>
public class ReplicaDigestEntry
{
    public required string Id { get; set; }
    public long ReplicationVersion { get; set; }
    public string? ChangeVector { get; set; }
}

/// <summary>The documents a reconcile wants in full: <c>POST /spark/replicas/fetch</c>.</summary>
public class ReplicaFetchRequest
{
    public required string RequestingModule { get; set; }
    public required string Contract { get; set; }
    public required int Version { get; set; }

    /// <summary>The ids to fetch; an id that no longer exists comes back as a tombstone.</summary>
    public required List<string> Ids { get; set; }
}
