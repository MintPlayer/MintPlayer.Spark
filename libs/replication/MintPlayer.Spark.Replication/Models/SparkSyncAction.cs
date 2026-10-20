using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>The states of a <see cref="SparkSyncAction"/>.</summary>
public static class SyncActionStatuses
{
    /// <summary>Queued: the owner was unreachable (or slow) when the edit was saved; retried on the ladder.</summary>
    public const string Pending = "Pending";

    /// <summary>Applied at the owner. The owner's delivery brings the new state to the replica.</summary>
    public const string Completed = "Completed";

    /// <summary>A listed property changed at the owner since the edit started, or the row is gone. Nothing written.</summary>
    public const string Conflict = "Conflict";

    /// <summary>The owner refused it for good (rights, validation). Nothing written.</summary>
    public const string Refused = "Refused";

    /// <summary>The owner stayed unreachable for the whole retry budget. Re-drivable with <c>RetrySyncAction</c>.</summary>
    public const string GaveUp = "GaveUp";

    /// <summary>Whether <paramref name="status"/> ended without the edit being applied.</summary>
    public static bool IsFailure(string status) => status is Conflict or Refused or GaveUp;
}

/// <summary>
/// A queued write-back on the consumer (warehouse §4.2.5): stored in the edit's own commit with its outbox message
/// (F13), when the owner did not answer within the save. Also the durable per-user record of how it ended: the
/// initiator's "My pending changes" lists it until they dismiss it, and a terminal failure is pushed to their open
/// pages (<c>replication:outcomes</c>). A library persistent object; every attribute is read-only.
/// <c>SparkSyncActions/{opId}</c>.
/// </summary>
public class SparkSyncAction
{
    public static string For(string opId) => $"SparkSyncActions/{opId}";

    public string? Id { get; set; }

    /// <summary>The operation id the owner deduplicates on (F15).</summary>
    public required string OpId { get; set; }

    /// <summary>The module that owns the document: the replica's source.</summary>
    public required string OwnerModule { get; set; }

    public required SyncActionType ActionType { get; set; }

    /// <summary>The owner's collection.</summary>
    public required string SourceCollection { get; set; }

    /// <summary>This module's replica collection.</summary>
    public required string TargetCollection { get; set; }

    public required string DocumentId { get; set; }

    /// <summary>The changed properties with their bases (the owner's property names), as JSON text: the store serializes with Newtonsoft, which cannot hold a JsonElement.</summary>
    public List<SparkSyncActionChange> Changes { get; set; } = [];

    /// <summary>A readable list of the changed properties, for the grid.</summary>
    public string? Summary { get; set; }

    /// <summary>The user who made the edit (#271): who is told how it ended.</summary>
    public string? InitiatorId { get; set; }

    /// <summary>One of <see cref="SyncActionStatuses"/>.</summary>
    public string Status { get; set; } = SyncActionStatuses.Pending;

    /// <summary>The owner's reason, the last transient error, or why it gave up.</summary>
    public string? Reason { get; set; }

    /// <summary>The conflicting, unwritable or invalid properties.</summary>
    public List<string> Attributes { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>After this the queued edit stops retrying and is <see cref="SyncActionStatuses.GaveUp"/>.</summary>
    public DateTime GiveUpAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Attempts made, for the ladder.</summary>
    public int Attempts { get; set; }

    /// <summary>How often it was re-driven after giving up (F14).</summary>
    public int Redrives { get; set; }

    /// <summary>The initiator dismissed it from "My pending changes".</summary>
    public bool Dismissed { get; set; }
}

/// <summary>One stored property change of a <see cref="SparkSyncAction"/>: JSON text in the stores' own serialization.</summary>
public class SparkSyncActionChange
{
    public required string Property { get; set; }

    /// <summary>The replica's value when the edit started.</summary>
    public string BaseJson { get; set; } = "null";

    /// <summary>The new value.</summary>
    public string ValueJson { get; set; } = "null";
}
