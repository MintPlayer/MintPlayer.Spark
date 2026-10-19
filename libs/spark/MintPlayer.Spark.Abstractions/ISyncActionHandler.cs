namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Applies a write-back request on the owner module (warehouse WX3): an edit a consumer module asked for on a
/// document this module owns. Implemented by MintPlayer.Spark; called by the sync endpoint in
/// MintPlayer.Spark.Replication, with the requesting module's principal on the request.
/// </summary>
public interface ISyncActionHandler
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the stored version of the document, through the same path an approved
    /// change takes (<see cref="Interceptors.ISparkReplacedSaves"/>): the module's type right, its attribute rights
    /// (an attribute it may not write refuses the whole edit, F17), validation and interceptors, in one commit with
    /// <see cref="SyncEdit.Alongside"/>. A write landing between the read and the commit is a
    /// <see cref="SyncEditOutcomeKind.Conflict"/>, never overwritten.
    /// </summary>
    Task<SyncEditOutcome> ApplyEditAsync(SyncEdit edit, CancellationToken cancellationToken = default);
}

/// <summary>A write-back edit to apply (see <see cref="ISyncActionHandler.ApplyEditAsync"/>).</summary>
public sealed record SyncEdit
{
    /// <summary>The owner's CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The document id.</summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// The changed values, keyed by the owner's property names. Only these attributes are written; every other
    /// keeps its stored value. Nested values are objects and arrays (a <c>JsonElement</c>, dictionaries, lists),
    /// never JSON text.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>The user the consumer made the edit for: stamping only (#271), never authorization.</summary>
    public string? InitiatorId { get; init; }

    /// <summary>
    /// Writes that must commit with the edit or not at all (the owner's record of the op id, F15). Gets the
    /// RavenDB <c>IAsyncDocumentSession</c> the edit commits through; store, do not save.
    /// </summary>
    public Func<object, Task>? Alongside { get; init; }
}

/// <summary>How a write-back edit ended.</summary>
public enum SyncEditOutcomeKind
{
    /// <summary>Saved, with <see cref="SyncEdit.Alongside"/>, in one commit.</summary>
    Applied,

    /// <summary>Refused for good (<see cref="SyncEditOutcome.Reason"/>). Nothing was written.</summary>
    Refused,

    /// <summary>The document changed meanwhile. Nothing was written; the caller retries.</summary>
    Conflict,
}

/// <summary>The result of <see cref="ISyncActionHandler.ApplyEditAsync"/>.</summary>
public sealed record SyncEditOutcome(SyncEditOutcomeKind Kind, string? Reason = null)
{
    /// <summary>For an attribute-rights refusal: the attributes the module may not write.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}
