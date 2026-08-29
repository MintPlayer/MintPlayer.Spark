using MintPlayer.Spark.Abstractions;
using System.Linq.Expressions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Interface defining lifecycle hooks for entity-specific business logic.
/// Implement this interface to customize CRUD behavior for specific entity types.
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public interface IPersistentObjectActions<T> where T : class
{
    /// <summary>
    /// Called when a single object's page is loaded: id in, page out — what this method returns
    /// is what renders. <see cref="DefaultPersistentObjectActions{T}.OnLoadAsync"/> runs the
    /// entity pipeline (document load, collection guard, row security, breadcrumbs, mapping,
    /// redaction, etag); overrides typically call it and decorate the result. Returning null is
    /// "not found" — a 404 indistinguishable from a missing document.
    /// </summary>
    /// <param name="id">The requested object id, straight from the URL — untrusted.</param>
    /// <param name="parent">The object this load is nested under, when the client provided one;
    /// null for a top-level page load (the usual case).</param>
    /// <remarks>
    /// This is the only load hook. When the framework resolves several ids at once — a custom
    /// action's selection — it batches the work internally rather than calling this N times, but
    /// that is an optimization, not a second seam: an actions class that overrides this method
    /// still has it called per id, so an override can never be bypassed by a bulk path.
    /// </remarks>
    Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent);

    /// <summary>
    /// Called when saving (creating or updating) an entity.
    /// Receives the full PersistentObject with attribute metadata (including IsValueChanged).
    /// Entity mapping happens inside this method.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <param name="obj">The PersistentObject with attribute values and metadata</param>
    /// <returns>The saved entity</returns>
    Task<T> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj);

    /// <summary>
    /// Called when deleting an entity.
    /// This method should call OnBeforeDeleteAsync.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <param name="id">The document ID to delete</param>
    Task OnDeleteAsync(IAsyncDocumentSession session, string id);

    /// <summary>
    /// Lifecycle hook called before saving an entity.
    /// Use this to validate, transform, or enrich the entity before persistence.
    /// Has access to both the PersistentObject (with IsValueChanged metadata) and the mapped entity.
    /// </summary>
    /// <param name="obj">The PersistentObject with attribute metadata</param>
    /// <param name="entity">The mapped entity about to be saved</param>
    Task OnBeforeSaveAsync(PersistentObject obj, T entity);

    /// <summary>
    /// Lifecycle hook called after saving an entity.
    /// Use this for post-save operations like notifications, auditing, or cache invalidation.
    /// Has access to both the PersistentObject (with IsValueChanged metadata) and the saved entity.
    /// </summary>
    /// <param name="obj">The PersistentObject with attribute metadata</param>
    /// <param name="entity">The entity that was saved</param>
    Task OnAfterSaveAsync(PersistentObject obj, T entity);

    /// <summary>
    /// Lifecycle hook called before deleting an entity.
    /// Use this for validation, cleanup, or cascade operations.
    /// </summary>
    /// <param name="entity">The entity about to be deleted</param>
    Task OnBeforeDeleteAsync(T entity);

    /// <summary>
    /// Called when the value of an attribute declaring <c>"triggersRefresh": true</c> changes, so the
    /// form can be reshaped in response. Mutate <c>args.PersistentObject</c>: toggle
    /// <see cref="PersistentObjectAttribute.IsRequired"/>,
    /// <see cref="PersistentObjectAttribute.IsReadOnly"/> and
    /// <see cref="PersistentObjectAttribute.IsVisible"/>, rewrite
    /// <see cref="PersistentObjectAttribute.Rules"/>, replace an attribute's selectable options, or
    /// set a dependent value.
    /// <para>
    /// ⚠️ <b>Establish the complete presentation state on every call.</b> Each invocation is handed a
    /// freshly scaffolded object, never the result of the last one, so a handler that applies only
    /// the delta it cares about silently loses every rule and flag it set previously.
    /// </para>
    /// <para>
    /// ⚠️ <b>No side effects.</b> Spark also runs this while validating a save, so the rules it
    /// establishes are enforced whether or not the client ever asked for a refresh.
    /// </para>
    /// </summary>
    Task OnRefreshAsync(SparkRefreshArgs<T> args);

    // ---- Row-level security ------------------------------------------------------------------
    //
    // These three were deliberately absent from this interface and reached by reflection instead,
    // on the reasoning that a hook nobody has overridden is not part of the contract. The cost of
    // that was hidden: the public IActionsResolver returns an IPersistentObjectActions<T> that
    // cannot be asked for a row rule, so an application with a mixed /spark + /api surface had no
    // way to reuse the rule it had already written, and rewrote the predicate per endpoint (#301).
    //
    // Declaring them here is what makes ISparkRowRule<T> possible without reflection. It breaks
    // hand-written implementers of this interface — classes deriving from
    // DefaultPersistentObjectActions<T> are unaffected, which is every actions class in every demo
    // and the shape the source generator emits.

    /// <summary>
    /// Whether the current caller may perform <paramref name="action"/> on this specific row.
    /// Consulted by every read path — list, query, stream and detail — and on write, where a
    /// rejected row is a rejected write.
    /// <para>
    /// Return <see langword="true"/> to impose no restriction; that is what
    /// <see cref="DefaultPersistentObjectActions{T}"/> does, so a type that does not care about row
    /// security costs nothing. Genuinely per-row and <b>not</b> memoized — express I/O-backed rules
    /// in <see cref="GetRowFilterAsync"/> instead.
    /// </para>
    /// </summary>
    /// <param name="action">One of "Read" / "Query" / "Edit" / "Delete" / "New".</param>
    Task<bool> IsAllowedAsync(string action, T entity);

    /// <summary>
    /// The same policy as a composable predicate, so the framework can push it into the RavenDB
    /// query instead of reading a whole collection and discarding most of it.
    /// <para>
    /// <b><see langword="null"/> means unrestricted</b>, not "deny everything". A caller consuming
    /// this directly must never coalesce it to <c>x =&gt; false</c>. Note also that the filter is
    /// only half the rule: the effective rule is this predicate AND
    /// <see cref="IsAllowedAsync"/>, so a type overriding only the latter returns
    /// <see langword="null"/> here and is <em>not</em> unrestricted. Prefer
    /// <c>ISparkRowRule&lt;T&gt;.ApplyAsync</c>, which applies both halves in one call.
    /// </para>
    /// <para>
    /// Invoked at most once per (entity type, action) per request and cached, so awaiting I/O here
    /// is safe. See <see cref="DefaultPersistentObjectActions{T}.GetRowFilterAsync"/> for the full
    /// cost contract and the reasons this is an expression rather than a pre-filtered query.
    /// </para>
    /// </summary>
    Task<Expression<Func<T, bool>>?> GetRowFilterAsync(string action);

    /// <summary>
    /// The attributes of this specific row the current caller must not see. The framework nulls
    /// their values, marks them invisible on every read path, and shields them from write-back.
    /// <see langword="null"/> or empty means nothing is redacted — the default, costing nothing.
    /// <para>
    /// A dotted name ("Jobs.Salary") reaches a column inside an AsDetail attribute's embedded rows.
    /// This is <b>attribute</b> visibility, distinct from the <b>row</b> visibility the two hooks
    /// above express.
    /// </para>
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, T entity);
}
