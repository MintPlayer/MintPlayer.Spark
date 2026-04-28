using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Registers this module in the shared SparkModules database on startup,
/// making it discoverable by other modules.
/// </summary>
internal partial class ModuleRegistrationService
{
    [Inject] private readonly IOptions<SparkReplicationOptions> optionsAccessor;
    [Inject] private readonly IDocumentStore appDocumentStore;
    [Inject] private readonly ILogger<ModuleRegistrationService> logger;

    private SparkReplicationOptions Options => optionsAccessor.Value;

    /// <summary>
    /// Creates a dedicated DocumentStore for the shared SparkModules database.
    /// </summary>
    internal IDocumentStore CreateModulesStore()
    {
        var store = new DocumentStore
        {
            Urls = Options.SparkModulesUrls,
            Database = Options.SparkModulesDatabase,
        };
        store.Initialize();
        return store;
    }

    /// <summary>
    /// Ensures the SparkModules database exists (development), then stores this module's information.
    /// </summary>
    public async Task RegisterAsync(IDocumentStore modulesStore, CancellationToken cancellationToken = default)
    {
        // Auto-create SparkModules database if it doesn't exist
        try
        {
            var databaseNames = modulesStore.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
            if (!databaseNames.Contains(Options.SparkModulesDatabase))
            {
                modulesStore.Maintenance.Server.Send(new CreateDatabaseOperation(o =>
                    o.Regular(Options.SparkModulesDatabase).WithReplicationFactor(1)
                ));
                logger.LogInformation("Created shared SparkModules database '{Database}'", Options.SparkModulesDatabase);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ensure SparkModules database exists (may already exist or lack permissions)");
        }

        var documentId = $"moduleInformations/{Options.ModuleName}";

        using var session = modulesStore.OpenAsyncSession();
        var existing = await session.LoadAsync<ModuleInformation>(documentId, cancellationToken);

        if (existing != null)
        {
            existing.AppUrl = Options.ModuleUrl;
            existing.DatabaseName = appDocumentStore.Database;
            existing.DatabaseUrls = appDocumentStore.Urls;
            existing.RegisteredAtUtc = DateTime.UtcNow;
        }
        else
        {
            var moduleInfo = new ModuleInformation
            {
                Id = documentId,
                AppName = Options.ModuleName,
                AppUrl = Options.ModuleUrl,
                DatabaseName = appDocumentStore.Database,
                DatabaseUrls = appDocumentStore.Urls,
                RegisteredAtUtc = DateTime.UtcNow,
            };
            await session.StoreAsync(moduleInfo, documentId, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Registered module '{ModuleName}' in SparkModules database (URL: {ModuleUrl}, DB: {Database})",
            Options.ModuleName, Options.ModuleUrl, appDocumentStore.Database);
    }
}
