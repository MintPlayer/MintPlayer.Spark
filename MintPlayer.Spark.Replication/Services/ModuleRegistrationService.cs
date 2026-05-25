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
        var thumbprint = Options.ClientCertificate.Thumbprint;

        using var session = modulesStore.OpenAsyncSession();
        var existing = await session.LoadAsync<ModuleInformation>(documentId, cancellationToken);

        if (existing != null)
        {
            // R2-H7: refuse to overwrite a pinned thumbprint with a different one.
            // If a previous registration pinned this module to cert X and a new
            // process comes up with cert Y, that's either a key rotation (must be
            // performed via an operator-driven path, not silent overwrite) or an
            // attacker spinning up a malicious "HR" module to redirect ETL/sync
            // deliveries. Either way: refuse the overwrite and surface the error.
            if (!string.IsNullOrEmpty(existing.ClientCertificateThumbprint)
                && !string.IsNullOrEmpty(thumbprint)
                && !string.Equals(existing.ClientCertificateThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "Refusing to overwrite module registration '{Module}': pinned thumbprint differs from configured thumbprint. " +
                    "If this is an intentional cert rotation, delete moduleInformations/{Module} in SparkModules first.",
                    Options.ModuleName, Options.ModuleName);
                throw new InvalidOperationException(
                    $"Module '{Options.ModuleName}' is already registered with a different client-certificate thumbprint. " +
                    "Rotate via an operator-driven delete-then-register flow, not silent overwrite.");
            }

            existing.AppUrl = Options.ModuleUrl;
            existing.DatabaseName = appDocumentStore.Database;
            existing.DatabaseUrls = appDocumentStore.Urls;
            existing.RegisteredAtUtc = DateTime.UtcNow;
            // Pin the thumbprint if the existing entry didn't have one (legacy
            // upgrade path). After this first save, subsequent re-registrations
            // hit the mismatch check above.
            if (string.IsNullOrEmpty(existing.ClientCertificateThumbprint) && !string.IsNullOrEmpty(thumbprint))
                existing.ClientCertificateThumbprint = thumbprint;
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
                ClientCertificateThumbprint = thumbprint,
            };
            await session.StoreAsync(moduleInfo, documentId, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Registered module '{ModuleName}' in SparkModules database (URL: {ModuleUrl}, DB: {Database}, thumbprint pinned: {HasThumbprint})",
            Options.ModuleName, Options.ModuleUrl, appDocumentStore.Database, !string.IsNullOrEmpty(thumbprint));
    }
}
