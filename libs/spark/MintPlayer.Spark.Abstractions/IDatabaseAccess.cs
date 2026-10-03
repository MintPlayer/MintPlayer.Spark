namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Reading and writing documents. <b>Two families, and the difference is authorization.</b>
/// <para>
/// The <c>PersistentObject</c> methods are the chokepoint: they resolve the entity type, call
/// <c>IPermissionService</c> for the type-level right, and apply the row-level gate. Every ordinary
/// data path in the framework goes through them — including cross-module sync since M11.
/// </para>
/// <para>
/// The <c>…UncheckedAsync</c> methods do <b>none of that</b>. They are a thin typed wrapper over the
/// RavenDB session and check nothing at all. They carry the word in their names because the previous
/// names (<c>SaveDocumentAsync</c> alongside <c>SavePersistentObjectAsync</c>) invited exactly the
/// wrong inference — that anything reached through <c>IDatabaseAccess</c> is authorized. Their only
/// callers today are custom actions, which are gated separately at <c>Action/{Name}</c>, so the
/// asymmetry is currently sound; the name is what keeps it sound as new callers appear.
/// </para>
/// <para>
/// If you are writing app data on behalf of a caller, use the PersistentObject family. Reach for the
/// unchecked family only when the authorization decision has demonstrably already been made
/// somewhere the reader can see.
/// </para>
/// </summary>
public interface IDatabaseAccess
{
    Task<T?> GetDocumentUncheckedAsync<T>(string id) where T : class;
    Task<IEnumerable<T>> GetDocumentsUncheckedAsync<T>() where T : class;
    Task<IEnumerable<T>> GetDocumentsByObjectTypeIdUncheckedAsync<T>(Guid objectTypeId) where T : class;
    Task<T> SaveDocumentUncheckedAsync<T>(T document) where T : class;
    Task DeleteDocumentUncheckedAsync<T>(string id) where T : class;

    // PersistentObject-specific methods that handle entity mapping
    Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id);

    /// <summary>
    /// Resolves several objects of one type by id, in the order given, in one batched pass — the
    /// shape a bulk action needs.
    /// </summary>
    /// <remarks>
    /// Plural rather than a loop over <see cref="GetPersistentObjectAsync(Guid, string)"/> because
    /// that loop is an N+1 of full row-gated loads: a document load plus its declared includes plus
    /// breadcrumb resolution, per row. Same principle as <c>IRowSecurity.AreAllowedAsync</c>, and
    /// for the same reason — so it cannot regress into one.
    /// <para>
    /// An id is omitted when it names no document, names a foreign collection, or is refused by the
    /// row rule, the three deliberately indistinguishable. The result may therefore be shorter than
    /// the request: <b>a caller that needs every row must compare the counts and refuse</b>, never
    /// act on the survivors.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<PersistentObject>> GetPersistentObjectsByIdAsync(Guid objectTypeId, IReadOnlyList<string> ids);
    /// <summary>
    /// Authorizes a save without performing it, so a caller can be refused before the request is
    /// validated. <see cref="SavePersistentObjectAsync"/> calls this itself — asking early does not
    /// move the decision out of the chokepoint.
    /// </summary>
    Task EnsureSaveAuthorizedAsync(PersistentObject persistentObject);

    /// <summary>
    /// Creates (no <see cref="PersistentObject.Id"/>) or updates an object.
    /// <para>
    /// An update with an <see cref="PersistentObject.Etag"/> edits that version: a row changed since is
    /// a <c>409</c>, and a row deleted since is a <c>409</c> too — never recreated (#467, D15). An
    /// update without one overwrites what is stored, or creates the object under its id when none is:
    /// the internal overwrite, for callers that never showed the row to anyone. Every HTTP update
    /// requires an etag (D16), so no request reaches it.
    /// </para>
    /// </summary>
    Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject);

    /// <summary>
    /// A save whose kind the caller states — <see cref="Interceptors.PersistentObjectOperation.Revert"/>,
    /// <see cref="Interceptors.PersistentObjectOperation.Restore"/> or
    /// <see cref="Interceptors.PersistentObjectOperation.Sync"/> — so interceptors can tell it from an
    /// ordinary edit. <c>Save</c> and <c>New</c> are derived from the id either way. Same gates as the
    /// one-argument overload, except that a <b>Restore</b> is gated under its own name: the
    /// type-level right is <c>Restore/T</c> (not <c>Edit/T</c>), the row gate is asked about
    /// <c>"Restore"</c>, the disabled-action hook refuses it when <c>Restore</c>, <c>Edit</c> or
    /// <c>Save</c> is withheld, and the document must exist (a restore never creates). A
    /// <b>Revert</b> needs <c>Revert/T</c> <i>and</i> <c>Edit/T</c>, the row gate is asked about
    /// <c>"Revert"</c>, the disabled-action hook refuses it when <c>Revert</c>, <c>Edit</c> or
    /// <c>Save</c> is withheld, and the document must exist.
    /// <para>
    /// An Actions class's own row hooks (<c>GetRowFilterAsync</c> / <c>IsAllowedAsync</c>) are asked
    /// about the base verb — <c>Edit</c> for a restore or revert, <c>Delete</c> for a purge — so a rule
    /// written for the built-in verbs also governs them; row policies see the real name.
    /// </para>
    /// </summary>
    Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject, Interceptors.PersistentObjectOperation operation);

    Task DeletePersistentObjectAsync(Guid objectTypeId, string id);

    /// <summary>
    /// A delete whose kind the caller states: <see cref="Interceptors.PersistentObjectOperation.Purge"/>
    /// (must not be replaced — a soft-delete interceptor lets it through) or
    /// <see cref="Interceptors.PersistentObjectOperation.Sync"/>. Same gates as the two-argument overload,
    /// except that a <b>Purge</b> is gated under its own name: the type-level right is
    /// <c>Purge/T</c> (not <c>Delete/T</c>), the row gate is asked about <c>"Purge"</c>, and the
    /// disabled-action hook refuses it when <c>Purge</c> or <c>Delete</c> is withheld. If an interceptor
    /// refuses after an earlier hook changed the entity, the entity is evicted from the request
    /// session, so no later save in the request writes the half-made change.
    /// </summary>
    /// <param name="etag">
    /// The version the caller saw (#467, D14). When given, a row changed since is refused with a 409
    /// before any hook, and the delete is written with it, so a change landing after that check is a
    /// 409 too. Null deletes whatever is stored: the internal overwrite, for callers that never showed
    /// the row to anyone (a job, a sync). Every HTTP delete requires one.
    /// </param>
    Task DeletePersistentObjectAsync(Guid objectTypeId, string id, Interceptors.PersistentObjectOperation operation, string? etag = null);

    /// <summary>
    /// Deletes several objects of one type as <b>one unit of work</b> (#460, D18): all of them or none.
    /// <para>
    /// Every row goes through the single-row delete pipeline — <c>Delete/T</c>, the collection guard,
    /// the row gate, the disabled-action hook (asked about the query target, with its parent, and every
    /// row; one refused row refuses the lot), the delete replacement (so a soft-deletable type is
    /// soft-deleted) and the before-delete hooks — but every gate runs before the first write, and the
    /// framework commits every row with one <c>SaveChanges</c> (#482: no hook or Actions class can
    /// commit early). A missing, foreign-collection or row-denied id refuses the whole request with
    /// <see cref="Authorization.SparkRowLevelAccessDeniedException"/>, never a silently shorter delete.
    /// </para>
    /// <para>
    /// A hook that refuses a row names it in the refusal; a <c>Retry.Action</c> prompt from a hook is
    /// refused the same way (a per-row prompt in a bulk delete is unworkable), and a
    /// <see cref="SparkCancelException"/> cancels the whole batch.
    /// </para>
    /// </summary>
    /// <param name="objectTypeId">The type of every row.</param>
    /// <param name="ids">The rows; duplicates collapse.</param>
    /// <param name="context">The query the rows were selected in and its parent, for the disabled-action hook.</param>
    Task DeletePersistentObjectsAsync(Guid objectTypeId, IReadOnlyList<string> ids, SparkBulkDeleteContext? context = null)
        => throw new NotSupportedException($"{GetType().Name} does not implement bulk deletes.");
}

/// <summary>Where a bulk delete was started from, for <c>OnDisableActionsAsync</c> (#460, D18).</summary>
public sealed class SparkBulkDeleteContext
{
    /// <summary>The query the rows were selected in; must produce the deleted type to count.</summary>
    public SparkQuery? Query { get; init; }

    /// <summary>The sub-query's container, already loaded through the gated read.</summary>
    public PersistentObject? Parent { get; init; }

    /// <summary>The container's entity type name.</summary>
    public string? ParentType { get; init; }

    /// <summary>
    /// The user's reason, applied to every row (#467, D20). A soft delete records it as the row's
    /// <c>DeleteReason</c>; a hard delete ignores it.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The version of each row the caller saw, by id (#467, D14). When given, it must name every row;
    /// a row changed since refuses the batch with a 409 that names it (D18), and every delete is
    /// written with its etag. Null deletes whatever is stored — the internal overwrite; the
    /// <c>delete-many</c> endpoint always passes them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Etags { get; init; }
}
