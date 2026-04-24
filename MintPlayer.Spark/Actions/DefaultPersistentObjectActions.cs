using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Runtime.CompilerServices;

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
    public virtual async Task<IEnumerable<T>> OnQueryAsync(IAsyncDocumentSession session)
        => await session.Query<T>().ToListAsync();

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
    /// Intentionally distinct from <see cref="OnQueryAsync"/>: the latter describes
    /// *where* to find entities of this type; this hook answers *whether* the caller
    /// may act on each one. Keeping the concerns apart keeps row-level security explicit.
    ///
    /// Inject <c>IHttpContextAccessor</c> into your Actions class to reach the current
    /// <c>ClaimsPrincipal</c>.
    /// </summary>
    /// <param name="action">One of "Read" / "Query" / "Edit" / "Delete" / "New" — the
    /// same vocabulary used by <c>IPermissionService.IsAllowedAsync</c>.</param>
    /// <param name="entity">The specific row being evaluated.</param>
    public virtual Task<bool> IsAllowedAsync(string action, T entity) => Task.FromResult(true);

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
    public virtual IAsyncEnumerable<IReadOnlyList<T>> StreamItems(
        StreamingQueryArgs args, [EnumeratorCancellation] CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"Streaming method 'StreamItems' is not implemented on {GetType().Name}. Override it to enable streaming.");

    /// <summary>
    /// Override to stream a single entity via WebSocket.
    /// Each yielded value is diffed against the previous one; only changed attribute values are sent as patches.
    /// </summary>
    public virtual IAsyncEnumerable<T> StreamItem(
        StreamingQueryArgs args, [EnumeratorCancellation] CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"Streaming method 'StreamItem' is not implemented on {GetType().Name}. Override it to enable streaming.");
}
