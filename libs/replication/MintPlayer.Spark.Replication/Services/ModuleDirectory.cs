using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// The shared <c>SparkModules</c> registry: who else exists, where they are, and which
/// certificate they are pinned to.
/// <para>
/// It exists to own the connection. Module records live in a *different* database from
/// the application's, so every caller that wanted one used to construct and initialize a
/// fresh <see cref="DocumentStore"/> for a single point-load — including the mTLS
/// validator, which meant unauthenticated inbound requests drove store creation and
/// teardown. One store for the process, opened on first use, is the whole point.
/// </para>
/// </summary>
internal interface IModuleDirectory
{
    /// <summary>
    /// Looks up a module by name, or returns <c>null</c> if it never registered. A
    /// point-load, deliberately: authentication decisions are made on the answer.
    /// </summary>
    Task<ModuleInformation?> FindAsync(string moduleName, CancellationToken cancellationToken);

    /// <summary>
    /// The shared store, for the registration path — which writes, and needs to create the
    /// database when it is missing, so a single record is not the right shape for it.
    /// </summary>
    IDocumentStore Store { get; }
}

internal sealed class ModuleDirectory : IModuleDirectory, IDisposable
{
    private readonly IOptions<SparkReplicationOptions> optionsAccessor;
    private readonly Lazy<IDocumentStore> store;

    public ModuleDirectory(IOptions<SparkReplicationOptions> optionsAccessor)
    {
        this.optionsAccessor = optionsAccessor;

        // Lazy rather than eager: an app that configures replication but never reaches a
        // cross-module call should not fail to start because SparkModules is unreachable.
        store = new Lazy<IDocumentStore>(CreateStore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IDocumentStore Store => store.Value;

    public async Task<ModuleInformation?> FindAsync(string moduleName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(moduleName))
            return null;

        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<ModuleInformation>(
            ModuleInformation.DocumentId(moduleName), cancellationToken);
    }

    private IDocumentStore CreateStore()
    {
        var options = optionsAccessor.Value;
        var created = new DocumentStore
        {
            Urls = options.ResolvedSparkModulesUrls,
            Database = options.SparkModulesDatabase,
        };
        created.Initialize();
        return created;
    }

    [NoInterfaceMember]
    public void Dispose()
    {
        if (store.IsValueCreated)
            store.Value.Dispose();
    }
}
