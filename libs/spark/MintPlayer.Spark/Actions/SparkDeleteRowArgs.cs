using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// What <see cref="DefaultPersistentObjectActions{T}.OnDeleteRowAsync"/> is handed when a row is
/// removed from a parent's <c>AsDetail</c> collection: the stored row, the parent that owns it, and
/// the chance to refuse.
/// <para>
/// The mirror of <see cref="SparkNewArgs{T}"/>, and subject to the same rule — nothing here is
/// written. The row is spliced out of the client's collection and reaches the database only when
/// the parent is saved, so a hook that wants to write something (an audit entry, a tombstone)
/// writes it from <see cref="IPersistentObjectActions{T}.OnBeforeSaveAsync"/> on the parent.
/// </para>
/// </summary>
/// <remarks>
/// ⚠️ <b>This hook is an affordance, not a security control</b>, and the distinction is the whole
/// reason the type is documented at this length.
/// <para>
/// A refusal here stops a <em>cooperating</em> client from removing the row. It does not stop a
/// caller that simply never posts to the endpoint and submits a parent with the row already gone —
/// and it cannot, because the endpoint writes nothing and the save is a separate request. What
/// stops that caller is the save path's own per-row <c>Delete/{RowType}</c> enforcement, which runs
/// unconditionally on every embedded collection and never consults
/// <see cref="EntityTypeDefinition.ServerSideRowLifecycle"/>.
/// </para>
/// <para>
/// So: use this hook to give the user a readable reason and to react. Do not use it as the only
/// thing standing between a caller and a row they must not remove — put that in the row type's
/// rights, where the save path will enforce it whether or not the round-trip is switched on.
/// </para>
/// </remarks>
/// <typeparam name="T">The row entity type the actions class serves.</typeparam>
public sealed class SparkDeleteRowArgs<T> where T : class
{
    internal SparkDeleteRowArgs(
        PersistentObject row,
        PersistentObject parent,
        string asDetailAttribute,
        string rowKey,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        Row = row;
        Parent = parent;
        AsDetailAttribute = asDetailAttribute;
        RowKey = rowKey;
        Parameters = parameters ?? EmptyParameters;
        CancellationToken = cancellationToken;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(0);

    /// <summary>
    /// The row being removed, as it is <b>stored</b> — read from the parent the server re-loaded,
    /// never from the request body.
    /// </summary>
    /// <remarks>
    /// That distinction is the difference between a hook that can decide anything and one that
    /// cannot. A hook refusing to delete a settled invoice line has to read <c>IsSettled</c> from
    /// the database; had it read the client's copy, a caller would only have to send
    /// <c>IsSettled = false</c> to be allowed through — the refusal would check the attacker's own
    /// claim about the row.
    /// </remarks>
    public PersistentObject Row { get; }

    /// <summary>
    /// The parent whose collection the row is being removed from. Never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Non-nullable, unlike <see cref="SparkNewArgs{T}.AsDetailParent"/>, because an unsaved parent
    /// cannot reach this path at all: a row on a parent that has never been saved does not exist in
    /// the database, so there is nothing to consult and nothing to refuse. The client splices it out
    /// locally and no request is made.
    /// </remarks>
    public PersistentObject Parent { get; }

    /// <summary>
    /// The name of the parent's <c>AsDetail</c> attribute the row is being removed from — the same
    /// discriminator <see cref="SparkNewArgs{T}.AsDetailAttribute"/> carries, and needed for the
    /// same reason: one row type can hang off a parent twice.
    /// </summary>
    public string AsDetailAttribute { get; }

    /// <summary>
    /// The row's key — its <c>[ValueKey]</c> value, which is also <see cref="Row"/>'s
    /// <see cref="PersistentObject.Id"/>.
    /// </summary>
    /// <remarks>
    /// Present as its own property rather than left for the hook to dig out, because it is the
    /// identity a hook is most likely to want and the one thing that makes a refusal message
    /// specific. It is the key that made this feature possible at all: before every embedded row
    /// carried one (#382), "which row is being removed" was not a question the server could answer.
    /// </remarks>
    public string RowKey { get; }

    /// <summary>Free-form arguments from the client, never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Cancelled when the caller gives up on the request.</summary>
    public CancellationToken CancellationToken { get; }
}
