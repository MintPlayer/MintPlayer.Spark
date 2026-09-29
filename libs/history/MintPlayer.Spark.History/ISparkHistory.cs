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

    /// <summary>Who wrote it: the revision's <see cref="IAuditable.ModifiedBy"/>, when the type is auditable.</summary>
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

/// <summary>
/// Told about every write through the Spark pipeline to a type whose model enables revisions
/// (<c>revisions.enabled</c>, T10). In-process, multi-registered, after the write committed; an
/// exception reaches the caller but does not undo the write.
/// </summary>
/// <remarks>
/// Only writes through <c>IDatabaseAccess</c> are seen — a raw session write, a patch or an ETL
/// produces a revision nobody is told about. History does not depend on Messaging: to fan out
/// durably, publish a message from the observer.
/// </remarks>
public interface ISparkRevisionObserver
{
    /// <summary>A write produced a revision.</summary>
    ValueTask OnRevisionCreatedAsync(SparkRevisionEvent revision);
}

/// <summary>What <see cref="ISparkRevisionObserver"/> is told.</summary>
public sealed class SparkRevisionEvent
{
    /// <summary>The CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The document id.</summary>
    public required string Id { get; init; }

    /// <summary>The new revision's change vector; <see langword="null"/> for a hard delete.</summary>
    public string? ChangeVector { get; init; }

    /// <summary>The change vector the document had before; <see langword="null"/> for a create.</summary>
    public string? PreviousChangeVector { get; init; }

    /// <summary>The acting user's id (<see langword="null"/> for the system).</summary>
    public string? UserId { get; init; }

    /// <summary>What kind of write: <c>New</c>, <c>Save</c>, <c>Revert</c>, <c>Restore</c>, <c>Delete</c> (soft or hard), <c>Sync</c>.</summary>
    public required PersistentObjectOperation Kind { get; init; }

    /// <summary>The model attributes whose stored value changed (top-level names). Empty for a delete.</summary>
    public required IReadOnlyList<string> ChangedAttributes { get; init; }
}
