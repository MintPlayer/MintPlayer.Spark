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

    /// <inheritdoc />
    public virtual async Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id)
        => await session.LoadAsync<T>(id);

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
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
        await OnAfterSaveAsync(obj, entity);
        return entity;
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
    [NoInterfaceMember]
    public virtual Task<bool> IsAllowedAsync(string action, T entity) => Task.FromResult(true);

    /// <summary>
    /// Row-level authorization as a composable filter. Where <see cref="IsAllowedAsync"/> judges
    /// one materialized row, this expresses the same policy as a predicate the framework can push
    /// into the RavenDB query itself — so a list over a row-scoped type reads only the caller's
    /// rows instead of the whole collection.
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
    /// </summary>
    /// <param name="action">Same vocabulary as <see cref="IsAllowedAsync"/>.</param>
    [NoInterfaceMember]
    public virtual System.Linq.Expressions.Expression<Func<T, bool>>? GetRowFilter(string action) => null;

    /// <inheritdoc />
    public virtual Task OnBeforeSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnAfterSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeDeleteAsync(T entity) => Task.CompletedTask;

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
