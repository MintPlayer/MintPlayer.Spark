namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Patches a single attribute on a currently-open PersistentObject on the frontend.
/// If the target PO is not displayed, the operation is silently dropped by the
/// frontend dispatcher (no error).
/// </summary>
/// <remarks>
/// <see cref="Value"/> is the new attribute value as produced by the server.
/// Sending <see cref="Value"/> inline avoids a round-trip fetch when the server
/// already computed the new value. Future extension: a null <see cref="Value"/>
/// with a protocol signal could mean "refetch" rather than "set to null" — not
/// specified yet.
/// <para>
/// <see cref="Object"/> and <see cref="Objects"/> carry an <c>AsDetail</c> attribute's nested
/// rows, and exist because <see cref="Value"/> alone made this operation <b>structurally unable
/// to refresh an AsDetail attribute</b>. A loaded AsDetail attribute puts its rows in
/// <c>object</c> / <c>objects</c> and leaves <c>value</c> null — the client's own pipes read
/// them from there — so a patch that could only carry <c>value</c> was guaranteed to send null
/// for exactly the attributes whose refresh a user is most likely to notice: a detail grid that
/// a server-side action just rewrote.
/// </para>
/// <para>
/// It failed <em>quietly</em>, which is why this went unnoticed: the client skips a patch equal
/// to the attribute's current value, so patching a null over a null repainted nothing and the
/// action looked inert while having succeeded. Found in Coverage's <c>SyncColumns</c>, which
/// replaced a board's cached columns and left the grid empty until a manual reload.
/// </para>
/// </remarks>
public sealed class RefreshAttributeOperation : ClientOperation
{
    public required Guid ObjectTypeId { get; init; }
    public required string Id { get; init; }
    public required string AttributeName { get; init; }
    public object? Value { get; init; }

    /// <summary>The nested row of a non-array <c>AsDetail</c> attribute, or null.</summary>
    public PersistentObject? Object { get; init; }

    /// <summary>
    /// The nested rows of an array <c>AsDetail</c> attribute, or null when the attribute is not
    /// one. An <b>empty list is meaningful</b> and must survive serialization: it is how "the
    /// action emptied this grid" is expressed, and treating it as absent would leave the removed
    /// rows on screen.
    /// </summary>
    public IReadOnlyList<PersistentObject>? Objects { get; init; }
}
