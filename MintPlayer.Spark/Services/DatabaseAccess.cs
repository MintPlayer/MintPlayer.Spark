using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly IDocumentStore documentStore;

    public async Task<T?> GetDocumentAsync<T>(string id) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Query<T>().ToListAsync();
    }

    public async Task<IEnumerable<T>> GetDocumentsByTypeAsync<T>(string clrType) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ClrType == clrType)
            .ToListAsync();
    }

    public async Task<T> SaveDocumentAsync<T>(T document) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(document);
        await session.SaveChangesAsync();
        return document;
    }

    public async Task DeleteDocumentAsync<T>(string id) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync();
    }
}
