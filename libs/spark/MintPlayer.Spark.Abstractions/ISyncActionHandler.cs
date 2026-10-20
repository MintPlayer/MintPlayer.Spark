using System.Text.Json;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Applies a write-back request on the owner module (warehouse §4.2.5): an edit a consumer module made on its replica
/// of a document this module owns. Implemented by MintPlayer.Spark; called by the sync endpoint in
/// MintPlayer.Spark.Replication, with the requesting module's principal on the request.
/// <para>
/// <b>Per-property compare-and-set</b> (owner decision 2026-10-20): every changed property carries the value the
/// person started from. The owner compares each one with the value stored now; equal everywhere → applied, any
/// difference → <see cref="SyncEditOutcomeKind.Conflict"/> naming the properties, and nothing is written. A property
/// the edit does not list never conflicts, so two people editing different fields of one row both succeed.
/// </para>
/// </summary>
public interface ISyncActionHandler
{
    /// <summary>
    /// Applies <paramref name="edit"/> through the path an approved change takes
    /// (<see cref="Interceptors.ISparkReplacedSaves"/>): the module's type right, its attribute rights (an attribute it
    /// may not write refuses the whole edit, F17), validation and interceptors, in one commit with
    /// <see cref="SyncEdit.Alongside"/>, against the version whose values it compared. A write landing between the
    /// compare and the commit is compared again (bounded), never overwritten.
    /// </summary>
    Task<SyncEditOutcome> ApplyEditAsync(SyncEdit edit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the document through the path a person's delete takes (rights, interceptors), after the same
    /// compare-and-set over <see cref="SyncDelete.BaseValues"/>: a row changed since the replica was edited is a
    /// <see cref="SyncEditOutcomeKind.Conflict"/>.
    /// </summary>
    Task<SyncEditOutcome> ApplyDeleteAsync(SyncDelete delete, CancellationToken cancellationToken = default);
}

/// <summary>A write-back edit to apply (see <see cref="ISyncActionHandler.ApplyEditAsync"/>).</summary>
public sealed record SyncEdit
{
    /// <summary>The owner's CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The document id.</summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// The new values, keyed by the owner's top-level property names, as JSON in the stores' own serialization. Only
    /// these attributes are written; every other keeps its stored value. Collections and AsDetail values are whole
    /// values.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> Values { get; init; }

    /// <summary>
    /// The value of every property in <see cref="Values"/> as the person saw it when they started the edit (the
    /// replica's value then). A key of <see cref="Values"/> missing here is refused: no blind writes.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> BaseValues { get; init; }

    /// <summary>The user the consumer made the edit for: stamping only (#271), never authorization.</summary>
    public string? InitiatorId { get; init; }

    /// <summary>
    /// Writes that must commit with the edit or not at all (the owner's record of the op id, F15). Gets the
    /// RavenDB <c>IAsyncDocumentSession</c> the edit commits through; store, do not save.
    /// </summary>
    public Func<object, Task>? Alongside { get; init; }
}

/// <summary>A write-back delete to apply (see <see cref="ISyncActionHandler.ApplyDeleteAsync"/>).</summary>
public sealed record SyncDelete
{
    /// <summary>The owner's CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The document id.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The replicated values the person saw when they deleted it; compared like an edit's.</summary>
    public required IReadOnlyDictionary<string, JsonElement> BaseValues { get; init; }

    /// <summary>The user the consumer made the delete for: stamping only.</summary>
    public string? InitiatorId { get; init; }
}

/// <summary>How a write-back edit ended.</summary>
public enum SyncEditOutcomeKind
{
    /// <summary>Saved, with <see cref="SyncEdit.Alongside"/>, in one commit.</summary>
    Applied,

    /// <summary>Refused for good (<see cref="SyncEditOutcome.Reason"/>): rights or validation. Nothing was written.</summary>
    Refused,

    /// <summary>
    /// A listed property's stored value is no longer the base the edit started from, or the row is gone
    /// (<see cref="SyncEditOutcome.Reason"/> <c>changed</c> / <c>deleted</c>, as <see cref="SparkConflictException"/>).
    /// <see cref="SyncEditOutcome.Attributes"/> names the properties. Nothing was written (no partial apply).
    /// </summary>
    Conflict,
}

/// <summary>Why a <see cref="SyncEditOutcomeKind.Refused"/> edit was refused, for the consumer to answer like a local save.</summary>
public enum SyncEditRefusal
{
    /// <summary>Not one of the others.</summary>
    Other,

    /// <summary>The module may not edit the row or write an attribute (403 at the owner).</summary>
    Forbidden,

    /// <summary>The owner's validation refused the values (422 at the owner).</summary>
    Invalid,
}

/// <summary>The result of <see cref="ISyncActionHandler.ApplyEditAsync"/>.</summary>
public sealed record SyncEditOutcome(SyncEditOutcomeKind Kind, string? Reason = null)
{
    /// <summary>The properties concerned: the conflicting ones, the unwritable ones, or the invalid ones.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>For <see cref="SyncEditOutcomeKind.Refused"/>: what kind of refusal.</summary>
    public SyncEditRefusal Refusal { get; init; }
}
