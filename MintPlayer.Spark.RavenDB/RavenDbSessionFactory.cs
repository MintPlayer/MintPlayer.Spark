using MintPlayer.Spark.Storage;
using Raven.Client.Documents;

namespace MintPlayer.Spark.RavenDB;

/// <summary>
/// RavenDB implementation of <see cref="ISparkSessionFactory"/>.
/// Wraps an <see cref="IDocumentStore"/>.
/// </summary>
public class RavenDbSessionFactory : ISparkSessionFactory
{
    private readonly IDocumentStore _documentStore;

    public RavenDbSessionFactory(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    /// <summary>
    /// The underlying RavenDB document store.
    /// Exposed for satellite packages (Authorization, Messaging, etc.) that need direct access.
    /// </summary>
    public IDocumentStore DocumentStore => _documentStore;

    public ISparkSession OpenSession()
    {
        var innerSession = _documentStore.OpenAsyncSession();
        return new RavenDbSparkSession(innerSession);
    }
}
