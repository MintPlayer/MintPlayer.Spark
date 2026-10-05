using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.History;

/// <summary>
/// A row's RavenDB revisions, read and reverted through Spark's security (#460, item 3).
/// </summary>
/// <remarks>
/// <para>
/// Every call is gated on the <b>current</b> document first: the type-level <c>Read</c> right and the
/// row's read gate (the same load <c>POST /spark/po/load</c> does), then <c>History/T</c>. A row the
/// caller cannot see now has no history for them, whatever it looked like before — and a refusal is
/// indistinguishable from a missing row (<see cref="Abstractions.Authorization.SparkRowLevelAccessDeniedException"/>).
/// </para>
/// <para>
/// Revision content is redacted for the caller like a load (field-level
/// <c>GetProtectedAttributesAsync</c>), judged against the revision <b>and</b> the current document:
/// an attribute protected on either stays hidden.
/// </para>
/// </remarks>
public interface ISparkHistory
{
    /// <summary>The row's revisions, newest first.</summary>
    Task<IReadOnlyList<SparkRevision>> ListAsync(Guid objectTypeId, string id, int skip = 0, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>One revision, as a read-only persistent object (its <c>Etag</c> is the revision's change vector).</summary>
    Task<PersistentObject> GetAsync(Guid objectTypeId, string id, string changeVector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the row back to the revision <paramref name="changeVector"/>, through
    /// <c>IDatabaseAccess.SavePersistentObjectAsync(…, Revert)</c>: <c>Revert/T</c> and <c>Edit/T</c>, the
    /// row gate, the disabled-action hook (<c>Revert</c>/<c>Edit</c>/<c>Save</c>), protected
    /// attributes, WITH CHECK and every interceptor (stamping, soft deletion, locks) apply, and the
    /// current change vector guards against a concurrent edit. Only model attributes revert; audit
    /// fields are stamped anew and soft-delete fields keep their stored values. Returns the reverted row.
    /// </summary>
    Task<PersistentObject> RevertAsync(Guid objectTypeId, string id, string changeVector, CancellationToken cancellationToken = default);
}

/// <summary>One entry of a revision list.</summary>
public sealed class SparkRevision
{
    /// <summary>The revision's change vector — what <see cref="ISparkHistory.GetAsync"/> and revert take.</summary>
    public required string ChangeVector { get; init; }

    /// <summary>When the revision was written.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Who wrote it: the revision's <see cref="IAuditModified.ModifiedBy"/>, when the type is auditable.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// <see cref="UserId"/> resolved at read time by an <see cref="IHistoryUserNameResolver"/>;
    /// <see langword="null"/> without a resolver, or when the id resolves to nobody (a deleted user).
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>A revision RavenDB wrote for a (hard) delete; it has no content to read or revert to.</summary>
    public bool IsDeleteRevision { get; init; }

    /// <summary>Whether this revision is the current state of the row (its change vector is the document's).</summary>
    public bool IsCurrent { get; init; }
}

