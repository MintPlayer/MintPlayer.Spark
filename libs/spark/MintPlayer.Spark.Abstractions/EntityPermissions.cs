namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// What <c>GET /spark/permissions/{entityTypeId}</c> answers: the caller's type-level rights on one entity
/// type. Every right is always sent; a row may still refuse what the type allows (<see cref="PersistentObject.Can"/>).
/// </summary>
/// <param name="CanQuery">
/// List the type. Independently grantable from <paramref name="CanRead"/>: <c>Query/Person</c> alone lists
/// rows while refusing a by-id load, and the combined <c>QueryRead</c> right bundles the two invisibly (#298).
/// </param>
/// <param name="CanRead">Load one object by id.</param>
/// <param name="CanCreate">Create (<c>New/T</c>).</param>
/// <param name="CanEdit">Save changes (<c>Edit/T</c>).</param>
/// <param name="CanDelete">Delete (<c>Delete/T</c>).</param>
/// <param name="CanRestore">SoftDelete (#460): restore a deleted object (<c>Restore/T</c>).</param>
/// <param name="CanPurge">SoftDelete: delete a deleted object for good (<c>Purge/T</c>).</param>
/// <param name="CanViewDeleted">SoftDelete: the recycle bin, querying and loading with <c>deleted: 'include' | 'only'</c> (<c>ViewDeleted/T</c>).</param>
/// <param name="CanViewHistory">History (#460): list and read revisions (<c>History/T</c>).</param>
/// <param name="CanRevert">History: revert to a revision, which needs <c>Revert/T</c> together with <c>Edit/T</c>.</param>
/// <param name="CanRevertContribution">Contributions: make a contribution current again (<c>RevertContribution/T</c>; a generated contribution type only).</param>
[SparkContract]
public sealed record EntityPermissions(
    bool CanQuery,
    bool CanRead,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    bool CanRestore,
    bool CanPurge,
    bool CanViewDeleted,
    bool CanViewHistory,
    bool CanRevert,
    bool CanRevertContribution)
{
    /// <summary>Every right refused: the answer for a caller with none, and for an unknown type (no existence oracle).</summary>
    public static EntityPermissions None { get; } = new(false, false, false, false, false, false, false, false, false, false, false);
}
