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
        var entity = entityMapper.ToEntity<T>(obj);
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
