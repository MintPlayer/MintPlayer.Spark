using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// A consumer's registration with one owner: <c>POST /spark/replicas/register</c> over the consumer's module
/// certificate (warehouse §4.2.2). It lists <b>every</b> replica the consumer reads from that owner; one missing from
/// the post is withdrawn (F18). The destination database is never sent: the owner reads it from the consumer's
/// registry entry (F19).
/// </summary>
public class ReplicaRegistrationRequest
{
    /// <summary>The consumer. Must be the module whose pinned certificate the caller presents.</summary>
    public required string RequestingModule { get; set; }

    public List<ReplicaRegistrationItem> Items { get; set; } = [];
}

/// <summary>One replica class, as its generated constants state it.</summary>
public class ReplicaRegistrationItem
{
    /// <summary>The consumer's collection (<c>ReplicationTargetCollection</c>): one subscription per consumer and target.</summary>
    public required string TargetCollection { get; set; }

    /// <summary>The owner's collection the manifest reads (<c>ReplicationSourceCollection</c>).</summary>
    public required string SourceCollection { get; set; }

    /// <summary>The canonical manifest JSON (<c>ReplicationManifest</c>). The owner re-renders the script from it.</summary>
    public required string Manifest { get; set; }

    /// <summary>The generated script (<c>ReplicationScript</c>); refused unless it equals the owner's re-render.</summary>
    public required string Script { get; set; }

    /// <summary>The generated hash (<c>ReplicationHash</c>); refused unless it equals the owner's.</summary>
    public required string Hash { get; set; }
}

/// <summary>What the owner did with a registered replica.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReplicaRegistrationStatus>))]
public enum ReplicaRegistrationStatus
{
    /// <summary>Approved now (an administrator earlier, or a Development owner automatically): data follows.</summary>
    Approved,

    /// <summary>The hash is the approved one already; nothing was redeployed (WD15).</summary>
    Unchanged,

    /// <summary>Waiting for an administrator. Data in the last approved shape keeps flowing meanwhile (Q-W29).</summary>
    Pending,

    /// <summary>Refused (<see cref="ReplicaRegistrationItemResult.Reason"/>): no grant, an unreadable field, or a rejection.</summary>
    Refused,
}

/// <summary>The owner's answer to a <see cref="ReplicaRegistrationRequest"/>.</summary>
public class ReplicaRegistrationResult
{
    public List<ReplicaRegistrationItemResult> Items { get; set; } = [];

    /// <summary>The target collections whose subscriptions this post withdrew (missing from it).</summary>
    public List<string> Withdrawn { get; set; } = [];

    /// <summary>Why the whole post was refused, for a 4xx.</summary>
    public string? Error { get; set; }
}

public class ReplicaRegistrationItemResult
{
    public required string TargetCollection { get; set; }

    public ReplicaRegistrationStatus Status { get; set; }

    /// <summary>The hash the owner delivers in now (the approved one), or null when nothing is approved yet.</summary>
    public string? ApprovedHash { get; set; }

    public string? Reason { get; set; }
}
