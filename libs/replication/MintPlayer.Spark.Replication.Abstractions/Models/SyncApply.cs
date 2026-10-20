using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

// String or number over the wire: the endpoint binds the body before the mTLS check, and a body that fails to bind
// would 400 ahead of the intended 401/403.
[JsonConverter(typeof(JsonStringEnumConverter<SyncActionType>))]
public enum SyncActionType
{
    Update,
    Delete,
}

/// <summary>
/// One write-back: <c>POST /spark/sync/apply</c> from a consumer to the owner, over the consumer's module certificate
/// (warehouse §4.2.5). One action per request. The owner applies it with a per-property compare-and-set: every listed
/// property's stored value must still be <see cref="SyncPropertyChange.Base"/>.
/// </summary>
public class SyncApplyRequest
{
    /// <summary>The consumer. Must be the module whose pinned certificate the caller presents.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>
    /// The operation id, unique per edit: a replay (a resend after a lost answer) returns the first outcome instead
    /// of a false conflict (F15).
    /// </summary>
    public required string OpId { get; set; }

    public required SyncActionType ActionType { get; set; }

    /// <summary>The owner's collection (the replica's <c>ReplicationSourceCollection</c>).</summary>
    public required string SourceCollection { get; set; }

    public required string DocumentId { get; set; }

    /// <summary>
    /// For an update: the changed properties, by the owner's property name. For a delete: every replicated property
    /// with its base (the values are ignored).
    /// </summary>
    public List<SyncPropertyChange> Changes { get; set; } = [];

    /// <summary>The user who made the edit: stamping only (#271), never authorization.</summary>
    public string? InitiatorId { get; set; }
}

/// <summary>One property of a write-back: the value the person started from and the one they saved.</summary>
public class SyncPropertyChange
{
    /// <summary>The owner's top-level property name.</summary>
    public required string Property { get; set; }

    /// <summary>The replica's value when the edit started (JSON null when it had none).</summary>
    public JsonElement Base { get; set; }

    /// <summary>The new value (JSON null to clear it).</summary>
    public JsonElement Value { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncApplyOutcome>))]
public enum SyncApplyOutcome
{
    /// <summary>Written at the owner (200). The owner's push brings the new state to every replica, the requester's too.</summary>
    Applied,

    /// <summary>A listed property changed at the owner since the edit started, or the row is gone (409). Nothing written.</summary>
    Conflict,

    /// <summary>Refused for good (403 rights, 422 validation, 400 otherwise). Nothing written.</summary>
    Refused,
}

[JsonConverter(typeof(JsonStringEnumConverter<SyncRefusalKind>))]
public enum SyncRefusalKind
{
    Other,
    Forbidden,
    Invalid,
}

/// <summary>The owner's answer to a <see cref="SyncApplyRequest"/>; never 207 (F12).</summary>
public class SyncApplyResult
{
    public SyncApplyOutcome Outcome { get; set; }

    /// <summary>For a conflict <c>changed</c> or <c>deleted</c>; for a refusal the owner's reason.</summary>
    public string? Reason { get; set; }

    /// <summary>The conflicting, unwritable or invalid properties.</summary>
    public List<string> Attributes { get; set; } = [];

    /// <summary>For <see cref="SyncApplyOutcome.Refused"/>: what kind.</summary>
    public SyncRefusalKind Refusal { get; set; }
}
