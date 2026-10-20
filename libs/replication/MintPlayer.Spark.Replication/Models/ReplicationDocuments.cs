using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>
/// Consumer side: a replica its source deleted. Kept so a late, older push cannot resurrect it (warehouse §4.2.3);
/// expires after <see cref="Lifetime"/> (longer than a push's retry budget plus a reconcile period), through RavenDB
/// document expiration. <c>SparkReplicaTombstones/{documentId}</c>.
/// </summary>
public class ReplicaTombstone
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static string For(string replicaId) => $"SparkReplicaTombstones/{replicaId}";

    public string? Id { get; set; }

    public required string ReplicaId { get; set; }

    public required string TargetCollection { get; set; }

    public required string SourceModule { get; set; }

    /// <summary>The owner's change vector of the deletion, when it knew one.</summary>
    public string? SourceChangeVector { get; set; }

    public DateTime DeletedAtUtc { get; set; }
}

/// <summary>
/// Owner side: a document of a dirty-set collection changed since the last flush.
/// <c>SparkReplicaDirty/{collection}/{documentId}</c>, so further changes overwrite the one marker.
/// </summary>
public class ReplicaDirtyMarker
{
    public const string Prefix = "SparkReplicaDirty/";

    public static string For(string sourceCollection, string documentId) => $"{Prefix}{sourceCollection}/{documentId}";

    public string? Id { get; set; }

    public required string SourceCollection { get; set; }

    public required string DocumentId { get; set; }

    public DateTime MarkedAtUtc { get; set; }
}

/// <summary>
/// Owner side: the outcomes of the write-backs a module applied to one document, by op id (F15) — a replay after a
/// lost answer returns the stored outcome instead of a false conflict. Written in the same commit as the edit.
/// <c>SparkSyncInbox/{module}/{documentId}</c>.
/// </summary>
public class SyncInboxEntry
{
    /// <summary>How many recent op ids are remembered per document and module.</summary>
    public const int RememberedOps = 32;

    public static string For(string module, string documentId) => $"SparkSyncInbox/{module}/{documentId}";

    public string? Id { get; set; }

    public required string Module { get; set; }

    public required string DocumentId { get; set; }

    public List<SyncInboxOp> Recent { get; set; } = [];
}

/// <summary>One remembered op id and its outcome.</summary>
public class SyncInboxOp
{
    public required string OpId { get; set; }

    public SyncApplyOutcome Outcome { get; set; }

    public string? Reason { get; set; }

    public List<string> Attributes { get; set; } = [];

    public SyncRefusalKind Refusal { get; set; }
}

/// <summary>
/// Consumer side: what one owner last answered to this module's registration (WD15).
/// <c>SparkReplicaRegistrations/{owner}</c>. When the set hash is unchanged and every item was acknowledged as
/// approved, startup posts nothing.
/// </summary>
public class ReplicaRegistrationState
{
    public static string For(string owner) => $"SparkReplicaRegistrations/{owner}";

    public string? Id { get; set; }

    public required string Owner { get; set; }

    /// <summary>The set hash over the replica hashes the last answered post carried.</summary>
    public string? SetHash { get; set; }

    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>Per target collection: the owner's last answer.</summary>
    public Dictionary<string, ReplicaRegistrationAnswer> Items { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether nothing needs posting for <paramref name="setHash"/>: answered, and every item approved or unchanged.</summary>
    public bool IsSettled(string setHash)
        => SetHash == setHash
        && Items.Values.All(i => i.Status is ReplicaRegistrationStatus.Approved or ReplicaRegistrationStatus.Unchanged);
}

/// <summary>The owner's last answer for one replica collection.</summary>
public class ReplicaRegistrationAnswer
{
    public ReplicaRegistrationStatus Status { get; set; }

    /// <summary>The hash the owner delivers in (Q-W29: pushes in this shape are accepted while a newer one waits).</summary>
    public string? ApprovedHash { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Consumer side: the one write-back pending for a replica document (Q-W28), in the commit of the save that made it.
/// <c>SparkSyncPending/{documentId}</c>. It is pending while its queued SyncAction runs, and — once the owner applied
/// the change — until the owner's delivery rewrites the replica (its own change vector moves past
/// <see cref="ReplicaChangeVector"/>), so the badge stays up exactly until the new state is visible, on either
/// transport. A second edit while it is pending is refused: it would carry the same, soon stale, base.
/// </summary>
public class SyncPendingMarker
{
    /// <summary>How long an applied marker may wait for the delivery before it no longer counts (it then expires).</summary>
    public static readonly TimeSpan AppliedLifetime = TimeSpan.FromHours(1);

    public static string For(string documentId) => $"SparkSyncPending/{documentId}";

    public string? Id { get; set; }

    public required string DocumentId { get; set; }

    public required string OwnerModule { get; set; }

    /// <summary>The queued SyncAction, or null when the owner applied the edit within the save.</summary>
    public string? SyncActionId { get; set; }

    /// <summary>The replica document's own change vector when the edit was made.</summary>
    public string? ReplicaChangeVector { get; set; }

    public string? InitiatorId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
