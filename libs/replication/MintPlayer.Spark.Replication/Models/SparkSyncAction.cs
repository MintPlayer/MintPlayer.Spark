using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>
/// RavenDB document that stores a pending sync action to be processed by the subscription worker.
/// Replaces the message bus approach for sync action delivery.
/// </summary>
public class SparkSyncAction
{
    public string? Id { get; set; }

    /// <summary>The name of the module that owns the entity and should receive the sync action.</summary>
    public required string OwnerModuleName { get; set; }

    /// <summary>The name of the module that initiated the sync action.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The RavenDB collection name (e.g., "Cars").</summary>
    public required string Collection { get; set; }

    /// <summary>The sync actions to apply on the owner module.</summary>
    public required List<SyncAction> Actions { get; set; }

    /// <summary>Processing status of this sync action document.</summary>
    public ESyncActionStatus Status { get; set; } = ESyncActionStatus.Pending;

    /// <summary>When this document was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Last error message if processing failed.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// When the next processing attempt should occur. Null means deliver immediately.
    /// Populated when a retry is scheduled.
    /// </summary>
    /// <remarks>
    /// This field is <b>not</b> readable by the subscription query — see <see cref="WakeUp"/>.
    /// It is the sweeper's input, and the audit trail for when a retry became due.
    /// </remarks>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Set by <c>SyncActionRetrySweeper</c> once <see cref="NextAttemptAtUtc"/> has passed;
    /// cleared by the worker on pickup and whenever the action is parked for another attempt.
    /// This is the subscription-visible redelivery gate.
    /// </summary>
    /// <remarks>
    /// A subscription query cannot ask whether a backoff has elapsed. Subscriptions are
    /// change-vector-driven — a document is tested against the query only when it is written — so a
    /// time comparison is evaluated at the one moment it cannot be true and never again. RavenDB
    /// 7.2.1 answered <c>NextAttemptAtUtc &lt;= now()</c> with a silent false; 7.2.5 rejects the
    /// query outright (<c>'now()' function is not supported in filter or subscription
    /// expressions</c>). Both are the same underlying fact.
    /// <para>
    /// So "the backoff has elapsed" is computed by something that can evaluate time and written down
    /// as plain field state. The write is doing double duty: it makes the gate true, and it bumps the
    /// change vector that causes the subscription to look at the document again at all.
    /// </para>
    /// <para>Mirrors <c>SparkMessage.WakeUp</c>, which solved this for messaging in #233.</para>
    /// </remarks>
    public bool WakeUp { get; set; }

    /// <summary>
    /// When the sweeper last woke this action for redelivery. Informational only.
    /// </summary>
    public DateTime? LastWakeUpUtc { get; set; }
}
