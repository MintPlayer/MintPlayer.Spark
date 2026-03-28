using MintPlayer.Spark.Storage;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.RavenDB;

/// <summary>
/// RavenDB implementation of <see cref="ISparkSession"/>.
/// Wraps an <see cref="IAsyncDocumentSession"/>.
/// </summary>
public class RavenDbSparkSession : ISparkSession
{
    /// <summary>
    /// The underlying RavenDB async document session.
    /// Exposed for advanced RavenDB-specific operations via extension methods.
    /// </summary>
    public IAsyncDocumentSession InnerSession { get; }

    public RavenDbSparkSession(IAsyncDocumentSession innerSession)
    {
        InnerSession = innerSession;
    }

    public IQueryable<T> Query<T>() where T : class
        => InnerSession.Query<T>();

    public IQueryable<T> Query<T>(string? indexName) where T : class
    {
        if (string.IsNullOrEmpty(indexName))
            return InnerSession.Query<T>();

        return InnerSession.Query<T>(indexName);
    }

    public async Task<T?> LoadAsync<T>(string id) where T : class
        => await InnerSession.LoadAsync<T>(id);

    public Task StoreAsync<T>(T entity) where T : class
        => InnerSession.StoreAsync(entity);

    public Task SaveChangesAsync()
        => InnerSession.SaveChangesAsync();

    public void Delete(string id)
        => InnerSession.Delete(id);

    public void Delete<T>(T entity) where T : class
        => InnerSession.Delete(entity);

    public void Dispose()
        => InnerSession.Dispose();
}
