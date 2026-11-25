using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using Raven.Client.Documents;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// RavenDB as a test database (#536 D35): a database on the embedded server, as <see cref="RavenSparkTestDriver"/>
/// makes them. The package's build targets name it in the test assembly's <see cref="SparkTestDatabaseAttribute"/>.
/// </summary>
public sealed class RavenSparkTestDatabaseProvider : ISparkTestDatabaseProvider
{
    static RavenSparkTestDatabaseProvider() => SparkEmbeddedServer.EnsureConfigured();

    private static readonly Lazy<StoreSource> Source = new(() => new StoreSource());

    public string Name => "RavenDB";

    public Task<ISparkTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        LicenseHelper.EnsureAvailable();
        // Before the first store: without a server directory RavenDB throws a bare ArgumentNullException.
        RavenServerLocator.RequireServerDirectory();
        return Task.FromResult<ISparkTestDatabase>(new RavenSparkTestDatabase(Source.Value.Create(), owned: true));
    }

    /// <summary>The process's one <see cref="RavenTestDriver"/>, for its embedded server and database names.</summary>
    private sealed class StoreSource : RavenTestDriver
    {
        public IDocumentStore Create() => GetDocumentStore();

        protected override void PreInitialize(IDocumentStore documentStore)
        {
            documentStore.ApplySparkConventions();
            RavenDatabaseDeletion.DeleteOnDispose(documentStore);
            RavenDatabaseDeletion.DisableTopologyCache(documentStore);
            base.PreInitialize(documentStore);
        }
    }
}

/// <summary>A RavenDB database for <see cref="SparkEndpointFactory{TContext}"/>: the host uses this store.</summary>
public sealed class RavenSparkTestDatabase : ISparkTestDatabase
{
    private readonly bool owned;

    /// <param name="store">The store the host uses, with Spark's conventions applied.</param>
    /// <param name="owned">Whether disposing this disposes the store (and so deletes its database).</param>
    public RavenSparkTestDatabase(IDocumentStore store, bool owned = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        this.owned = owned;
    }

    /// <summary>The raw store, for a test that pins RavenDB's behaviour.</summary>
    public IDocumentStore Store { get; }

    public string ProviderName => "RavenDB";

    public void UseProvider(ISparkBuilder spark) => spark.UseRavenDb();

    public void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IDocumentStore>();
        services.AddSingleton(Store);
    }

    public Task WaitForConsistencyAsync(IServiceProvider services, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Store.WaitForIndexingAsync(timeout: timeout, cancellationToken: cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (owned)
            Store.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Wraps a RavenDB store for <see cref="SparkEndpointFactory{TContext}"/>.</summary>
public static class RavenSparkTestDatabaseExtensions
{
    /// <summary>The store as a test database the host runs on; the caller keeps owning the store.</summary>
    public static ISparkTestDatabase AsSparkTestDatabase(this IDocumentStore store) => new RavenSparkTestDatabase(store);
}
