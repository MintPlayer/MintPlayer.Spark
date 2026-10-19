using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>
/// A write-back request on the consumer: stored in the edit's own commit (F13, through <c>ISaveReplacement</c>),
/// then sent to the owner by the SyncAction subscription worker. One document per edit.
/// </summary>
public class SparkSyncAction
{
    public string? Id { get; set; }

    /// <summary>The module that owns the document: the replica's source.</summary>
    public required string OwnerModuleName { get; set; }

    /// <summary>This module.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The edit.</summary>
    public required SyncAction Action { get; set; }

    /// <summary>The replica's document id (copied from <see cref="Action"/> for the index: per-document ordering, F15).</summary>
    public required string DocumentId { get; set; }

    /// <summary>The per-document order (copied from <see cref="Action"/>).</summary>
    public long Sequence { get; set; }

    public ESyncActionStatus Status { get; set; } = ESyncActionStatus.Pending;

    /// <summary>The owner's last verdict (F12).</summary>
    public SyncActionOutcome? Outcome { get; set; }

    /// <summary>The reason of a refusal, or the last transient error.</summary>
    public string? LastError { get; set; }

    /// <summary>For an attribute-rights refusal (F17): the fields the module may not write.</summary>
    public List<string> RefusedAttributes { get; set; } = [];

    /// <summary>When the request was stored.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the owner's verdict arrived.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// When the next attempt is due; null means deliver immediately. Not readable by the subscription query —
    /// see <see cref="WakeUp"/>.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Set by <c>SyncActionRetrySweeper</c> once <see cref="NextAttemptAtUtc"/> has passed; cleared by the worker on
    /// pickup. A subscription cannot evaluate time (<c>now()</c> is refused in subscription queries, RavenDB 7.2.5),
    /// so the sweeper writes the verdict, and that write is also what makes the subscription look again (#258).
    /// </summary>
    public bool WakeUp { get; set; }

    /// <summary>When the sweeper last woke this request. Informational.</summary>
    public DateTime? LastWakeUpUtc { get; set; }

    /// <summary>How often the request was re-driven after Failed (F14). Informational.</summary>
    public int Redrives { get; set; }
}
