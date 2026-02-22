using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
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
            session.Delete(id);
            await session.SaveChangesAsync();
        }
    }

    /// <inheritdoc />
    public virtual Task OnBeforeSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnAfterSaveAsync(PersistentObject obj, T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeDeleteAsync(T entity) => Task.CompletedTask;
}
