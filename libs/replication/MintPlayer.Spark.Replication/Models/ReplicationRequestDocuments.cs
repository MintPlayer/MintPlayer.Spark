using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>The states of a <see cref="SparkReplicationRequest"/>.</summary>
public static class ReplicationRequestStatuses
{
    /// <summary>Waits for an administrator. Data in the last approved shape keeps flowing meanwhile (Q-W29).</summary>
    public const string Pending = "Pending";

    /// <summary>Approved (by an administrator, or automatically by a Development owner) and delivered.</summary>
    public const string Approved = "Approved";

    /// <summary>An administrator rejected it; nothing in this shape is delivered.</summary>
    public const string Rejected = "Rejected";

    /// <summary>A later request of the same consumer and collection replaced it (a newer pending one, or a newer approval).</summary>
    public const string Superseded = "Superseded";
}

/// <summary>
/// One registration request (warehouse §4.2.2): a consumer module asks to read one of this module's collections in one
/// shape (a manifest hash). The approval gate the administrators see in the owner's "Replication requests" program
/// unit, pending ones first, with the readable diff against the shape delivered now; they decide with the
/// <c>ApproveReplication</c> / <c>RejectReplication</c> custom actions. A Development owner approves automatically
/// (<see cref="AutoApproved"/>). Written by the library only; every attribute is read-only.
/// <c>SparkReplicationRequests/{consumer}/{targetCollection}/{hash}</c>, so re-posting a shape is idempotent.
/// </summary>
public class SparkReplicationRequest
{
    public static string For(string consumer, string targetCollection, string hash)
        => $"SparkReplicationRequests/{consumer}/{targetCollection}/{ShortHash(hash)}";

    /// <summary>The first 16 hex digits of a <c>sha256:</c> hash: unique enough per consumer and collection, short in ids.</summary>
    public static string ShortHash(string hash)
    {
        var hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..] : hash;
        return hex.Length > 16 ? hex[..16] : hex;
    }

    public string? Id { get; set; }

    /// <summary>The consumer module.</summary>
    public required string ConsumerModule { get; set; }

    /// <summary>This module's collection the manifest reads.</summary>
    public required string SourceCollection { get; set; }

    /// <summary>The consumer's replica collection.</summary>
    public required string TargetCollection { get; set; }

    /// <summary>One of <see cref="ReplicationRequestStatuses"/>.</summary>
    public string Status { get; set; } = ReplicationRequestStatuses.Pending;

    /// <summary>Sorts pending requests first in the "Replication requests" query (stored: the store serializes get-only properties).</summary>
    public bool IsPending => Status == ReplicationRequestStatuses.Pending;

    /// <summary>The requested manifest (canonical JSON).</summary>
    public required string Manifest { get; set; }

    /// <summary>The requested hash.</summary>
    public required string Hash { get; set; }

    /// <summary>The hash delivered when the request arrived, or null for a first request.</summary>
    public string? ApprovedHash { get; set; }

    /// <summary>The readable difference against the shape delivered when the request arrived.</summary>
    public string? Diff { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    /// <summary>The administrator who decided, or <see cref="SystemApprover"/> for a Development auto-approval.</summary>
    public string? DecidedBy { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>Approved automatically because this (the owner's) host runs in Development (WD16).</summary>
    public bool AutoApproved { get; set; }

    /// <summary>Why it was rejected, or what superseded it.</summary>
    public string? Reason { get; set; }

    /// <summary>The approver recorded for an automatic approval.</summary>
    public const string SystemApprover = "system (Development)";
}

/// <summary>The delivery states of a <see cref="SparkReplicationSubscription"/>.</summary>
public static class ReplicationSubscriptionStates
{
    /// <summary>Delivered in <see cref="SparkReplicationSubscription.ApprovedHash"/> (or waiting for a first approval).</summary>
    public const string Active = "Active";

    /// <summary>The consumer no longer reads this collection (missing from its last registration): delivery removed (F18).</summary>
    public const string Withdrawn = "Withdrawn";

    /// <summary>The consumer lost its grant or a field's read right, or its request was rejected: delivery removed.</summary>
    public const string Suspended = "Suspended";
}

/// <summary>
/// The owner's delivery state for one consumer reading one replica collection (warehouse §4.2.2):
/// <c>SparkReplicationSubscriptions/{consumer}/{targetCollection}</c>. Internal bookkeeping, not a persistent object:
/// the requests (<see cref="SparkReplicationRequest"/>) are what administrators see.
/// </summary>
public class SparkReplicationSubscription
{
    public static string For(string consumer, string targetCollection) => $"SparkReplicationSubscriptions/{consumer}/{targetCollection}";

    public string? Id { get; set; }

    public required string ConsumerModule { get; set; }

    public required string SourceCollection { get; set; }

    public required string TargetCollection { get; set; }

    /// <summary>One of <see cref="ReplicationSubscriptionStates"/>.</summary>
    public string State { get; set; } = ReplicationSubscriptionStates.Active;

    /// <summary>The manifest delivered in; null until a first approval.</summary>
    public string? ApprovedManifest { get; set; }

    public string? ApprovedHash { get; set; }

    public string? ApprovedRequestId { get; set; }

    /// <summary>The request waiting for a decision, if any.</summary>
    public string? PendingRequestId { get; set; }

    public string? PendingHash { get; set; }

    /// <summary>Why it is suspended or withdrawn.</summary>
    public string? Reason { get; set; }

    /// <summary>What is deployed now, so an unchanged deploy is skipped (WD15) and a registry change redeploys.</summary>
    public ReplicationDeployment? Deployed { get; set; }

    /// <summary>Whether data is delivered now: active with an approved manifest.</summary>
    public bool IsDelivering => State == ReplicationSubscriptionStates.Active && ApprovedHash is not null && ApprovedManifest is not null;
}

/// <summary>The deployed state of one subscription.</summary>
public class ReplicationDeployment
{
    public ReplicationTransport Transport { get; set; }

    public string? Hash { get; set; }

    /// <summary>The ETL destination (database @ urls) taken from the consumer's registry entry; null on HTTP.</summary>
    public string? Destination { get; set; }

    public DateTime DeployedAtUtc { get; set; }
}
