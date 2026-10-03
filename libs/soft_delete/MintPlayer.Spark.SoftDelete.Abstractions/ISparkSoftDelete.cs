namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// Soft deletion as a service — what <c>POST /spark/po/restore</c> and <c>POST /spark/po/purge</c>
/// call, and what Moderation (or any server code acting for a caller) uses.
/// </summary>
/// <remarks>
/// <para>
/// Every method goes through <c>IDatabaseAccess</c>, so every gate an HTTP caller meets applies:
/// the type-level right, the row gate, the disabled-action hook, interceptors, replication. None
/// of them is a back door.
/// </para>
/// <para>
/// A row the caller may not act on, a missing row, a foreign-collection id and a type that is not
/// <see cref="ISoftDeletable"/> all end the same way — <c>SparkAccessDeniedException</c> (the
/// endpoints answer 404) — so the service is not an existence oracle either.
/// </para>
/// </remarks>
public interface ISparkSoftDelete
{
    /// <summary>
    /// Soft-deletes the row, recording <paramref name="reason"/> in
    /// <see cref="ISoftDeletable.DeleteReason"/>. Same gates as <c>POST /spark/po/delete</c>
    /// (<c>Delete/T</c>, row gate, disabled-action hook), which soft-deletes too, just without a reason.
    /// </summary>
    Task DeleteAsync(Guid objectTypeId, string id, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings a soft-deleted row back. Requires <c>Restore/T</c>; the row must be deleted; refused when
    /// the type's <c>OnDisableActionsAsync</c> withholds <c>Restore</c>, <c>Edit</c> or <c>Save</c> on it.
    /// Returns the restored row's new change vector.
    /// </summary>
    Task<string?> RestoreAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a soft-deleted row and every revision of it (GDPR). Requires
    /// <c>Purge/T</c>; the row must already be deleted; refused when the hook withholds <c>Purge</c>
    /// or <c>Delete</c>. Cannot be undone.
    /// </summary>
    /// <param name="etag">
    /// The version the caller saw (#467, D14): a row changed since — restored and edited, say — is a
    /// 409 instead of purged. <c>POST /spark/po/purge</c> requires it; null purges whatever is stored.
    /// </param>
    Task PurgeAsync(Guid objectTypeId, string id, string? etag = null, CancellationToken cancellationToken = default);
}
