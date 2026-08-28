using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Default implementation of <see cref="IPersistentObjectActions{T}"/> providing standard CRUD behavior.
/// Inherit from this class to customize specific operations while keeping default behavior for others.
/// Entity mapping from PersistentObject to T happens inside OnSaveAsync.
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public partial class DefaultPersistentObjectActions<T> : IPersistentObjectActions<T> where T : class
{
    [Inject] private readonly IEntityMapper entityMapper;
    // Optional (nullable [Inject] fields get a `= null` ctor default) so existing manual
    // constructions keep compiling. Used only to recognize the system context — module sync and
    // background work — which row rules don't apply to.
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;

    /// <inheritdoc />
    public virtual async Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id)
    {
        // #239: prime the consumer's declared includes so referenced documents arrive in the same
        // round-trip instead of a breadcrumb load each. RavenDB requires the includes on the same
        // fluent load call (there's no pre-register-on-session), so this is the seam. A consumer
        // overriding OnLoadAsync takes over include responsibility (same caveat as the WITH CHECK).
        var includes = GetDefaultIncludes();
        if (includes is not { Count: > 0 })
            return await session.LoadAsync<T>(id);

        return await session.LoadAsync<T>(id, builder =>
        {
            foreach (var path in includes)
                builder.IncludeDocuments(path);
        });
    }

    /// <summary>
    /// Reference paths the framework should always RavenDB-<c>.Include()</c> when loading or querying
    /// this type — so referenced documents arrive in the same round-trip rather than a follow-up load
    /// (each of which counts against the session's request budget). Null/empty = only the
    /// <c>[Reference]</c>-decorated properties are auto-included.
    /// <para>
    /// Paths are dotted JSON paths <b>into the document</b>: <c>"Company"</c> for a top-level
    /// reference id, <c>"Address.City"</c> for an id nested inside an <b>embedded</b> object. They do
    /// <b>not</b> cross a document boundary — RavenDB has no recursive include, so a chain through a
    /// referenced <em>document</em> (Car → Owner → Owner.Company) is not expressible; use an index or
    /// let the breadcrumb resolver's batched load handle deeper levels.
    /// </para>
    /// </summary>
    [NoInterfaceMember]
    public virtual IReadOnlyCollection<string>? GetDefaultIncludes() => null;

    /// <summary>
    /// The read path's composition seam: return a non-null <see cref="PersistentObject"/> and the
    /// framework serves it as this type's page for the requested id — <b>instead of</b> loading an
    /// entity. The default returns null, which means "not composed": the normal entity pipeline
    /// (<see cref="OnLoadAsync"/> → collection guard → row security → mapping) runs unchanged.
    /// <para>
    /// This is how a menu entry opens a page that exists in the model but not in the database —
    /// the start-page pattern: a model-declared type with a CLR marker class and no context root,
    /// whose Actions class fills <c>args.PersistentObject</c>'s attribute values and
    /// <see cref="PersistentObject.Breadcrumb"/> (the page title) here, ignoring
    /// <see cref="SparkComposeArgs.RequestedId"/>. Composition runs under the type-level
    /// <c>Read</c> right, which <c>security.json</c> must grant explicitly.
    /// </para>
    /// <para>
    /// A composed object is read-only through the generic pipeline: unless this hook sets
    /// <see cref="PersistentObject.Can"/> itself, the framework forces Edit/Delete to false.
    /// Anything interactive on the page belongs in a custom action, which carries its own
    /// authorization.
    /// </para>
    /// </summary>
    [NoInterfaceMember]
    public virtual Task<PersistentObject?> OnComposeAsync(SparkComposeArgs args)
        => Task.FromResult<PersistentObject?>(null);

    /// <inheritdoc />
    public virtual async Task<T> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
    {
        // Update path: load the existing entity and merge the PO's values onto it. Fields
        // absent from the PO (server-managed metadata, untouched TranslatedString languages,
        // etc.) survive — ToEntity's always-new-instance flow wiped them. Create path
        // (Id is null/empty, or Raven returned null for an unknown Id) falls through to
        // ToEntity which builds a fresh instance from the PO.
        T entity;
        if (!string.IsNullOrEmpty(obj.Id))
        {
            var existing = await session.LoadAsync<T>(obj.Id);
            if (existing is not null)
            {
                await ShieldProtectedAttributesAsync(obj, existing);
                await entityMapper.PopulateObjectValuesAsync(obj, existing, session);
                entity = existing;
            }
            else
            {
                entity = entityMapper.ToEntity<T>(obj);
            }
        }
        else
        {
            entity = entityMapper.ToEntity<T>(obj);
        }

        await OnBeforeSaveAsync(obj, entity);
        await EnsureRowSaveAllowedAsync(obj, entity);
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
        await OnAfterSaveAsync(obj, entity);
        return entity;
    }

    /// <summary>
    /// The write half of row-level security — SQL RLS's <c>WITH CHECK</c> to the read paths'
    /// <c>USING</c>. Judged against the entity's <b>resulting</b> state, after mapping and
    /// <see cref="OnBeforeSaveAsync"/> (so ownership stamping has happened): a create must produce
    /// a row its caller could see, and an edit must not move a row <em>into</em> someone else's
    /// scope. Without this, nothing stops an authenticated caller creating a document stamped with
    /// another tenant's owner. Skipped for the system context (module sync, background work) —
    /// row rules scope viewers, and infrastructure has none. Overriding <see cref="OnSaveAsync"/>
    /// without calling the base implementation takes over this responsibility.
    /// </summary>
    private async Task EnsureRowSaveAllowedAsync(PersistentObject obj, T entity)
    {
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return;

        var action = string.IsNullOrEmpty(obj.Id) ? "New" : "Edit";

        var filter = await GetRowFilterAsync(action);
        if (filter is not null && !filter.Compile()(entity))
            throw new Abstractions.Authorization.SparkRowLevelAccessDeniedException($"{action}/{typeof(T).Name}");

        if (!await IsAllowedAsync(action, entity))
            throw new Abstractions.Authorization.SparkRowLevelAccessDeniedException($"{action}/{typeof(T).Name}");
    }

    /// <inheritdoc />
    public virtual async Task OnDeleteAsync(IAsyncDocumentSession session, string id)
    {
        var entity = await session.LoadAsync<T>(id);
        if (entity != null)
        {
            await OnBeforeDeleteAsync(entity);
            session.Delete(entity);
            await session.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Row-level authorization hook. Answers whether the current principal may perform the
    /// given <paramref name="action"/> on this specific <paramref name="entity"/> instance.
    /// The entity-type-level check (<see cref="Abstractions.Authorization.IPermissionService"/>)
    /// has already succeeded by the time this is called — this is the second layer that
    /// enforces ownership, tenant isolation, or any other per-row policy.
    ///
    /// Returning false translates to 404 for single-entity reads (so existence isn't
    /// leaked; see security audit M-3), a filtered-out row in list responses, or a
    /// rejected write. The default is permissive — override in an application-specific
    /// Actions class to enforce row-level policy. Overriding is a clear signal to code
    /// reviewers that the class takes responsibility for row-level security.
    ///
    /// This is the hook every read path consults — list, query, stream and detail alike.
    ///
    /// Inject <c>IHttpContextAccessor</c> into your Actions class to reach the current
    /// <c>ClaimsPrincipal</c>.
    /// </summary>
    /// <param name="action">One of "Read" / "Query" / "Edit" / "Delete" / "New" — the
    /// same vocabulary used by <c>IPermissionService.IsAllowedAsync</c>.</param>
    /// <param name="entity">The specific row being evaluated.</param>
    public virtual Task<bool> IsAllowedAsync(string action, T entity) => Task.FromResult(true);

    /// <summary>
    /// Row-level authorization as a composable filter. Where <see cref="IsAllowedAsync"/> judges
    /// one materialized row, this expresses the same policy as a predicate the framework can push
    /// into the RavenDB query itself — so a list over a row-scoped type reads only the caller's
    /// rows instead of the whole collection.
    ///
    /// <para><b>Why this hook rather than filtering the query yourself.</b> You might expect to
    /// scope rows by customizing the list query (an "OnQuery"-style hook that returns a filtered
    /// <c>IQueryable</c>). Two reasons that isn't what row security uses — and why there is
    /// deliberately no such hook (the old <c>OnQueryAsync</c> was removed):
    /// <list type="number">
    ///   <item><b>One rule, every path.</b> A query filter guards only the <i>list</i>. Row security
    ///   must also gate a <i>detail</i> read (a load by id — no query runs, so a query filter never
    ///   sees it), an <i>edit</i>/<i>delete</i> of a specific row, and a <i>create</i>/<i>edit</i>
    ///   that would stamp a row into someone else's scope (SQL's <c>WITH CHECK</c>), plus streaming
    ///   and breadcrumb reference loads. The framework derives <b>all</b> of those from this single
    ///   expression, so they cannot drift out of sync. A per-query filter would leave detail reads
    ///   and writes wide open — filter the list to 8 cars and a caller could still open, edit, or
    ///   delete car #9 by id. Spreading the rule across per-path hooks is exactly how a list screen
    ///   ends up leaking rows the detail screen protects.</item>
    ///   <item><b>An expression, not a pre-filtered query.</b> Because you return an
    ///   <see cref="System.Linq.Expressions.Expression{TDelegate}"/> the framework can both push it
    ///   into the RavenDB query <i>and</i> <c>Compile()</c> it to answer "may this one already-loaded
    ///   row be edited?" — a filtered <c>IQueryable</c> is opaque and can't be reused for a
    ///   single-row decision. It also lets the framework keep owning query construction (projection
    ///   and index selection, <c>.Include()</c>s, sorting, paging, the collection guard): you
    ///   contribute only the predicate and the rest still works.</item>
    /// </list></para>
    ///
    /// Evaluated per request: capture request-scoped data (the current user, an allow-list) as
    /// locals so it lands in the expression as constants. Return <c>null</c> to mean "no
    /// restriction for this caller" (e.g. an administrator).
    ///
    /// If only this member is overridden, single-row checks (detail, edit, delete) are derived by
    /// compiling the expression — one source of truth, list and detail cannot diverge. If
    /// <see cref="IsAllowedAsync"/> is also overridden, both must allow the row (the filter
    /// narrows, the predicate refines). When the query runs against an index projection, the
    /// filter cannot compose (it is typed on the entity, not the projection) and the framework
    /// falls back to post-materialization filtering with a batched base-document reload — never
    /// silently unfiltered.
    ///
    /// The predicate's properties must be queryable in RavenDB for the pushdown to apply: on a
    /// plain collection query anything on the document works; on a static index the fields it
    /// names must be indexed.
    ///
    /// <para><b>Async construction, synchronous expression.</b> The hook may <c>await</c> while
    /// building the filter (fetch an allow-list, query the store) — the returned
    /// <see cref="System.Linq.Expressions.Expression{TDelegate}"/> stays synchronous and RavenDB-
    /// translatable.</para>
    ///
    /// <para><b>Cost contract.</b> The framework invokes this hook <b>at most once per
    /// (entity type, action) per request</b> and caches the result, so awaiting I/O here is safe —
    /// the cost is bounded by the model, never by row count, page size, or streaming batch count.
    /// On a stream the cache refreshes on the periodic re-authorization tick (~every 10 batches), so
    /// a filter is at most that stale. Because the result is cached per request, the filter must be
    /// a <b>pure function of request-scoped state</b> — do not depend on something you mutate later
    /// in the same request. (By contrast <see cref="IsAllowedAsync"/> is genuinely per-row and is
    /// NOT memoized; express I/O-backed rules here, not there.)</para>
    ///
    /// <para><b>Pair with <see cref="GetDefaultIncludes"/> on reference-heavy types.</b> The filter
    /// narrows <i>which</i> rows come back, but each surviving row's referenced documents (rendered
    /// as breadcrumbs) are still fetched — and an async filter already spends request budget, so on
    /// a reference-heavy row-scoped type a page can march toward RavenDB's per-session request cap.
    /// Level-1 <c>[Reference]</c> properties are <c>.Include()</c>d automatically; override
    /// <see cref="GetDefaultIncludes"/> to prime <i>additional</i> references (embedded dotted paths,
    /// or reference ids not decorated <c>[Reference]</c>) so they arrive in the same round-trip
    /// rather than one load apiece.</para>
    /// </summary>
    /// <param name="action">Same vocabulary as <see cref="IsAllowedAsync"/>.</param>
    public virtual Task<System.Linq.Expressions.Expression<Func<T, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<System.Linq.Expressions.Expression<Func<T, bool>>?>(null);

    /// <summary>
    /// Per-viewer attribute redaction. Names the attributes of this specific row that the current
    /// caller must not see; the framework nulls their values and marks them invisible at mapping
    /// time on every read path (detail, list, query, stream), and shields them from write-back on
    /// updates. Null or empty means nothing is redacted — the default, costing nothing.
    ///
    /// A dotted name ("Jobs.Salary") redacts a column inside an AsDetail attribute's embedded
    /// rows — the one place a row filter can't reach, since embedded rows aren't rows. Write-back
    /// shielding applies to top-level names; dotted names are read-side redaction only.
    ///
    /// The canonical case: a secret only managers of this row may view —
    /// <c>CanManage(entity) ? null : ["BadgeToken"]</c>. Redaction is per row and per caller;
    /// evaluated against the base entity even when the query returned index projections.
    /// </summary>
    /// <param name="action">Same vocabulary as <see cref="IsAllowedAsync"/>.</param>
    public virtual Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, T entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(null);

    /// <summary>
    /// Write-back safety for redaction: a client that received a redacted (nulled) attribute and
    /// submits the form back would silently clobber the stored secret — and a malicious client
    /// could overwrite it deliberately. Before the merge, protected attributes get the existing
    /// entity's current value restored, so the merge writes the secret back to itself. Skipped
    /// for the system context (sync replicates full values).
    /// </summary>
    private async Task ShieldProtectedAttributesAsync(PersistentObject obj, T existing)
    {
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return;

        var protectedNames = await GetProtectedAttributesAsync("Edit", existing);
        if (protectedNames is not { Count: > 0 })
            return;

        foreach (var name in protectedNames)
        {
            if (name.Contains('.'))
                continue; // AsDetail child columns: read-side redaction only (documented).

            var attribute = obj.Attributes.FirstOrDefault(a => a.Name == name);
            var property = typeof(T).GetProperty(name);
            if (attribute is null || property is null || !property.CanRead)
                continue;

            attribute.Value = property.GetValue(existing);
            attribute.IsValueChanged = false;
        }
    }

    /// <inheritdoc />
    public virtual Task OnBeforeSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnAfterSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeDeleteAsync(T entity) => Task.CompletedTask;

    /// <summary>
    /// Called when the value of an attribute declaring <c>"triggersRefresh": true</c> changes, so the
    /// form can be reshaped in response. Mutate <c>args.PersistentObject</c>: toggle
    /// <see cref="PersistentObjectAttribute.IsRequired"/>, <see cref="PersistentObjectAttribute.IsReadOnly"/>
    /// and <see cref="PersistentObjectAttribute.IsVisible"/>, rewrite
    /// <see cref="PersistentObjectAttribute.Rules"/>, replace an attribute's selectable options, or set a
    /// dependent value. Does nothing by default.
    ///
    /// <para>
    /// ⚠️ <b>Establish the complete presentation state on every call, and make no assumptions about the
    /// previous one.</b> Each invocation is handed a freshly scaffolded object, never the result of the
    /// last refresh, so a handler that only applies the delta it cares about will silently lose every
    /// rule and flag it set previously. Share one helper between this hook and any load-time shaping
    /// rather than patching incrementally.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>No side effects.</b> Spark also runs this hook while validating a save, so that the rules
    /// it establishes are enforced whether or not the client ever asked for a refresh. A hook that
    /// writes, notifies or calls out will do so on save as well.
    /// </para>
    ///
    /// <para>
    /// It is called far more often than load or save — potentially on every field blur — so treat
    /// database access here as a cost, not a convenience.
    /// </para>
    /// </summary>
    public virtual Task OnRefreshAsync(SparkRefreshArgs<T> args) => Task.CompletedTask;

    /// <summary>
    /// Override to stream a collection of entities via WebSocket.
    /// Each yielded batch is diffed against the previous one; only changed attribute values are sent as patches.
    /// </summary>
    [NoInterfaceMember]
    public virtual IAsyncEnumerable<IReadOnlyList<T>> StreamItems(
        StreamingQueryArgs args, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"Streaming method 'StreamItems' is not implemented on {GetType().Name}. Override it to enable streaming.");

    /// <summary>
    /// Override to stream a single entity via WebSocket.
    /// Each yielded value is diffed against the previous one; only changed attribute values are sent as patches.
    /// </summary>
    [NoInterfaceMember]
    public virtual IAsyncEnumerable<T> StreamItem(
        StreamingQueryArgs args, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"Streaming method 'StreamItem' is not implemented on {GetType().Name}. Override it to enable streaming.");
}
