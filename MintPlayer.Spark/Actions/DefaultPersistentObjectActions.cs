using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Default implementation of <see cref="IPersistentObjectActions{T}"/> providing standard CRUD behavior.
/// Inherit from this class to customize specific operations while keeping default behavior for others.
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public class DefaultPersistentObjectActions<T> : IPersistentObjectActions<T> where T : class
{
    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> OnQueryAsync(IAsyncDocumentSession session)
        => await session.Query<T>().ToListAsync();

    /// <inheritdoc />
    public virtual async Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id)
        => await session.LoadAsync<T>(id);

    /// <inheritdoc />
    public virtual async Task<T> OnSaveAsync(IAsyncDocumentSession session, T entity)
    {
        await OnBeforeSaveAsync(entity);
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
        await OnAfterSaveAsync(entity);
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
    public virtual Task OnBeforeSaveAsync(T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnAfterSaveAsync(T entity) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeDeleteAsync(T entity) => Task.CompletedTask;
}
