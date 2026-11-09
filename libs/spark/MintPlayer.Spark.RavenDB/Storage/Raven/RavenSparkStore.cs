using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary><see cref="ISparkStore"/> over a RavenDB <see cref="IDocumentStore"/>.</summary>
[Register(typeof(ISparkStore), ServiceLifetime.Singleton)]
internal sealed partial class RavenSparkStore : ISparkStore
{
    [Inject] private readonly IDocumentStore store;

    public string ProviderName => "RavenDB";

    public SparkStoreCapabilities Capabilities =>
        SparkStoreCapabilities.Subscriptions | SparkStoreCapabilities.Revisions | SparkStoreCapabilities.FullTextSearch
        | SparkStoreCapabilities.Attachments | SparkStoreCapabilities.Expiration | SparkStoreCapabilities.ChangeNotifications
        | SparkStoreCapabilities.Indexes;

    public IDocumentStore DocumentStore => store;

    public ISparkSession OpenSession() => new RavenSparkSession(store.OpenAsyncSession(), ownsSession: true);

    public string GetCollectionName(Type type) => store.Conventions.GetCollectionName(type);

    /// <remarks>See <see cref="SparkIndexWait.WaitAsync"/>: sliced waits on the indexes over the written collections.</remarks>
    public Task<bool> WaitForConsistencyAsync(
        ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken)
        => SparkIndexWait.WaitAsync(
            store,
            ((session?.Native as InMemoryDocumentSessionOperations)?.DatabaseName) ?? store.Database,
            collections as IReadOnlySet<string> ?? new HashSet<string>(collections, StringComparer.OrdinalIgnoreCase),
            bound,
            onUnfinished,
            cancellationToken);
}
