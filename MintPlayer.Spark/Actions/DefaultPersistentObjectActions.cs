using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Actions;

public class DefaultPersistentObjectActions<T> where T : class
{
    protected IAsyncDocumentSession Session { get; }

    public DefaultPersistentObjectActions(IAsyncDocumentSession session)
    {
        Session = session;
    }

    public virtual async Task<IEnumerable<T>> OnQuery()
    {
        return await Session.Query<T>().ToListAsync();
    }

    public virtual async Task<T?> OnLoad(string id)
    {
        return await Session.LoadAsync<T>(id);
    }

    public virtual async Task<T> OnSave(T entity)
    {
        await OnBeforeSave(entity);
        await Session.StoreAsync(entity);
        await Session.SaveChangesAsync();
        await OnAfterSave(entity);
        return entity;
    }

    public virtual async Task OnDelete(string id)
    {
        var entity = await Session.LoadAsync<T>(id);
        if (entity != null)
        {
            await OnBeforeDelete(entity);
            Session.Delete(id);
            await Session.SaveChangesAsync();
        }
    }

    public virtual Task OnBeforeSave(T entity) => Task.CompletedTask;
    public virtual Task OnAfterSave(T entity) => Task.CompletedTask;
    public virtual Task OnBeforeDelete(T entity) => Task.CompletedTask;
}
