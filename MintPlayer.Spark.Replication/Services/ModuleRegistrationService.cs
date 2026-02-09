using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Registers this module in the shared SparkModules database on startup,
/// making it discoverable by other modules.
/// </summary>
internal class ModuleRegistrationService
{
    private readonly SparkReplicationOptions _options;
    private readonly IDocumentStore _appDocumentStore;
    private readonly ILogger<ModuleRegistrationService> _logger;

    public ModuleRegistrationService(
        IOptions<SparkReplicationOptions> options,
        IDocumentStore appDocumentStore,
        ILogger<ModuleRegistrationService> logger)
    {
        _options = options.Value;
        _appDocumentStore = appDocumentStore;
        _logger = logger;
    }

    /// <summary>
    /// Creates a dedicated DocumentStore for the shared SparkModules database.
    /// </summary>
    internal IDocumentStore CreateModulesStore()
    {
        var store = new DocumentStore
        {
            Urls = _options.SparkModulesUrls,
            Database = _options.SparkModulesDatabase,
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
            if (!databaseNames.Contains(_options.SparkModulesDatabase))
            {
                modulesStore.Maintenance.Server.Send(new CreateDatabaseOperation(o =>
                    o.Regular(_options.SparkModulesDatabase).WithReplicationFactor(1)
                ));
                _logger.LogInformation("Created shared SparkModules database '{Database}'", _options.SparkModulesDatabase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure SparkModules database exists (may already exist or lack permissions)");
        }

        var documentId = $"moduleInformations/{_options.ModuleName}";

        using var session = modulesStore.OpenAsyncSession();
        var existing = await session.LoadAsync<ModuleInformation>(documentId, cancellationToken);

        if (existing != null)
        {
            existing.AppUrl = _options.ModuleUrl;
            existing.DatabaseName = _appDocumentStore.Database;
            existing.DatabaseUrls = _appDocumentStore.Urls;
            existing.RegisteredAtUtc = DateTime.UtcNow;
        }
        else
        {
            var moduleInfo = new ModuleInformation
            {
                Id = documentId,
                AppName = _options.ModuleName,
                AppUrl = _options.ModuleUrl,
                DatabaseName = _appDocumentStore.Database,
                DatabaseUrls = _appDocumentStore.Urls,
                RegisteredAtUtc = DateTime.UtcNow,
            };
            await session.StoreAsync(moduleInfo, documentId, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Registered module '{ModuleName}' in SparkModules database (URL: {ModuleUrl}, DB: {Database})",
            _options.ModuleName, _options.ModuleUrl, _appDocumentStore.Database);
    }
}
