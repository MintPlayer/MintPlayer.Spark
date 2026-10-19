using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// A write-back request: a consumer asks the owner of a replicated document to change it (warehouse WX3).
/// It is an edit only — replicas are read-only, so no create or delete is ever requested. The owner
/// applies it through its own pipeline, as the requesting module's principal, with that module's
/// attribute rights (F17), and the new state returns to every consumer, the requester included,
/// through the read path.
/// </summary>
public class SyncAction
{
    /// <summary>
    /// The request's id, chosen by the requester. The owner applies an op id once (F15): a retry of an
    /// applied request answers <see cref="SyncActionOutcome.Duplicate"/>.
    /// </summary>
    public required string OpId { get; set; }

    /// <summary>The contract the replica reads.</summary>
    public required string Contract { get; set; }

    /// <summary>The contract version the replica reads; the field names in <see cref="Data"/> are this version's.</summary>
    public required int Version { get; set; }

    /// <summary>The document id (the owner's).</summary>
    public required string DocumentId { get; set; }

    /// <summary>
    /// The requester's per-document order (F15): later edits carry higher numbers. The owner refuses a
    /// request older than the last one it applied for the same document and requester, as
    /// <see cref="SyncActionOutcome.Superseded"/>, so a retried old edit never overwrites a newer one.
    /// </summary>
    public required long Sequence { get; set; }

    /// <summary>
    /// The changed contract fields and their values. Nested values keep their JSON structure (F18): the
    /// requester sends objects and arrays as such, never as JSON text.
    /// </summary>
    public required Dictionary<string, object?> Data { get; set; }

    /// <summary>The user the edit was made for, for the owner's stamping only (#271), never for authorization.</summary>
    public string? InitiatorId { get; set; }
}

/// <summary>The body of <c>POST /spark/sync/apply</c>.</summary>
public class SyncActionRequest
{
    /// <summary>The requesting module. Must match the presented module certificate.</summary>
    public required string RequestingModule { get; set; }

    public required List<SyncAction> Actions { get; set; }
}

/// <summary>
/// The owner's verdict on one write-back request (F12). Only <see cref="Conflict"/> and <see cref="Error"/>
/// are retried; a refusal is final, recorded as refused with its reason, and reported to the initiator.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncActionOutcome>))]
public enum SyncActionOutcome
{
    /// <summary>Applied in one commit with the owner's record of the op id.</summary>
    Applied,

    /// <summary>This op id was applied before; nothing was done again.</summary>
    Duplicate,

    /// <summary>A newer request for the same document from the same module was applied already; this one is dropped.</summary>
    Superseded,

    /// <summary>
    /// The owner refused it, finally: no right on the contract or its type, an attribute the module may not
    /// write (F17), a field the contract cannot write back, a validation error, an action an interceptor
    /// cancelled, or a document the owner no longer has.
    /// </summary>
    Refused,

    /// <summary>The document changed while the request was applied. Transient: retried.</summary>
    Conflict,

    /// <summary>An unexpected failure at the owner. Transient: retried.</summary>
    Error,
}

/// <summary>The owner's answer for one request.</summary>
public class SyncActionResult
{
    public required string OpId { get; set; }
    public SyncActionOutcome Outcome { get; set; }

    /// <summary>Why, for every outcome but <see cref="SyncActionOutcome.Applied"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>For an attribute-rights refusal: the fields the module may not write.</summary>
    public List<string> Attributes { get; set; } = [];
}

/// <summary>The answer of <c>POST /spark/sync/apply</c>: always 200 once the module is authenticated, with one result per action.</summary>
public class SyncActionResponse
{
    public List<SyncActionResult> Results { get; set; } = [];
}
