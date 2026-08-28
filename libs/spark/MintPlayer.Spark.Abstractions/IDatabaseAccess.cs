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

    Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(Guid objectTypeId);
    /// <summary>
    /// Authorizes a save without performing it, so a caller can be refused before the request is
    /// validated. <see cref="SavePersistentObjectAsync"/> calls this itself — asking early does not
    /// move the decision out of the chokepoint.
    /// </summary>
    Task EnsureSaveAuthorizedAsync(PersistentObject persistentObject);

    Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject);
    Task DeletePersistentObjectAsync(Guid objectTypeId, string id);
}
