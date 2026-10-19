using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Indexes;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Replication.Workers;
using Raven.Client.Documents;
using System.Reflection;

namespace MintPlayer.Spark.Replication;

internal static class SparkReplicationExtensions
{
    /// <summary>
    /// Registers replication services (module registration, ETL script collection, ETL task management,
    /// message bus recipient for deployment, and HTTP client for outbound requests).
    /// </summary>
    internal static IServiceCollection AddSparkReplication(
        this IServiceCollection services,
        Action<SparkReplicationOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<ModuleRegistrationService>();
        services.AddSingleton<IModuleDirectory, ModuleDirectory>();
        services.AddMintPlayerSparkReplication();
        services.AddSingleton<ReplicaContractCollector>();
        services.AddSingleton<ReplicationContractCatalog>();
        services.AddSingleton<IRavenEtlLicence, RavenEtlLicence>();
        services.AddSingleton<EtlTaskManager>();
        services.AddSingleton<IEtlTaskManager>(sp => sp.GetRequiredService<EtlTaskManager>());
        services.AddScoped<IRecipient<EtlScriptDeploymentMessage>, EtlScriptDeploymentRecipient>();

        // R2-C1/C2: outbound replication HttpClients attach this module's
        // client certificate so the peer module can verify mTLS. The cert is
        // *this* module's identity — same cert presented to every target by
        // default. Per-target overrides exist for advanced multi-CA cases.
        // Operators set the cert via SparkReplicationOptions.ClientCertificate.
        //
        // Both outbound paths know their target module, so both resolve this provider.
        // They previously used named clients whose handler only ever attached the default
        // certificate, which is why PerTargetOverrides was documented but did nothing (F5).
        services.AddSingleton<IReplicationHttpClientProvider, ReplicationHttpClientProvider>();

        // Sync action services
        services.AddScoped<ISyncActionInterceptor, SyncActionInterceptor>();
        // Forwards every committed write of a [Replicated] type to its owner (#482).
        Spark.Abstractions.Interceptors.SparkBuilderInterceptorExtensions.AddSparkInterceptor<ReplicationInterceptor>(services);
        services.AddHostedService<SyncActionSubscriptionWorker>();

        // Without this, a retry is scheduled and then never delivered: the subscription only
        // re-evaluates a document that gets written, and a backoff elapsing writes nothing. See #258.
        services.AddHostedService<SyncActionRetrySweeper>();

        return services;
    }

    /// <summary>
    /// On startup: (0) translates the contracts this module publishes, refusing startup on a
    /// projection the ETL transform cannot express, (1) registers this module in the shared
    /// SparkModules database, (2) scans assemblies for [Replicated] replica types, (3) asks each
    /// source module for the contracts they read, via the durable message bus.
    /// </summary>
    internal static WebApplication UseSparkReplication(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SparkReplication");
        var options = app.Services.GetRequiredService<IOptions<SparkReplicationOptions>>().Value;
        var registrationService = app.Services.GetRequiredService<ModuleRegistrationService>();
        var moduleDirectory = app.Services.GetRequiredService<IModuleDirectory>();
        var collector = app.Services.GetRequiredService<ReplicaContractCollector>();
        var appStore = app.Services.GetRequiredService<IDocumentStore>();

        // Step 0, synchronously: resolving the catalog translates every published projection, so a
        // complex one refuses startup here rather than failing a consumer's deployment later.
        app.Services.GetRequiredService<ReplicationContractCatalog>();

        // Deploy the SparkSyncActions index
        new SparkSyncActions_ByStatus().Execute(appStore);

        // Run registration and ETL deployment asynchronously to not block startup
        _ = Task.Run(async () =>
        {
            try
            {
                // IMessageBus is scoped, so create a scope to resolve it
                using var scope = app.Services.CreateScope();
                var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                // Step 1: Register this module in the shared SparkModules database
                await registrationService.RegisterAsync(moduleDirectory.Store);

                // Step 2: Scan assemblies for [Replicated] attributes
                var assemblies = options.AssembliesToScan.Length > 0
                    ? options.AssembliesToScan
                    : [Assembly.GetEntryAssembly()!];

                var contractsByModule = collector.Collect(type => appStore.Conventions.FindCollectionName(type), assemblies);

                if (contractsByModule.Count == 0)
                {
                    logger.LogInformation("No [Replicated] attributes found — no contracts to request");
                    return;
                }

                // Step 3: For each source module, broadcast an ETL deployment message. The
                // recipient resolves the source module's URL from SparkModules on each delivery,
                // so we don't need to look it up here — and a not-yet-registered source no
                // longer needs a fabricated fallback URL (which previously baked
                // `http://{name}:5000` into the message and made retries hit a stale endpoint
                // forever even after the source module finally registered).
                foreach (var deploymentMessage in BuildDeploymentMessages(contractsByModule, options))
                {
                    await messageBus.BroadcastAsync(deploymentMessage);
                    logger.LogInformation(
                        "Queued ETL deployment to '{SourceModule}' ({ContractCount} contracts) via message bus",
                        deploymentMessage.SourceModuleName, deploymentMessage.Request.Contracts.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Spark replication startup");
            }
        });

        return app;
    }

    /// <summary>
    /// Pure function that projects the collected per-source-module contracts into the
    /// <see cref="EtlScriptDeploymentMessage"/> envelopes the message bus broadcasts.
    /// Extracted so the message-shape contract can be unit-tested without spinning up
    /// a host (which would otherwise require IMessageBus + IDocumentStore + Task.Run timing).
    /// <para>
    /// The message names no database or URLs (F19): the owner reads this module's destination
    /// from its SparkModules registry entry, which registration keeps current.
    /// </para>
    /// </summary>
    internal static IEnumerable<EtlScriptDeploymentMessage> BuildDeploymentMessages(
        IReadOnlyDictionary<string, List<EtlContractRequest>> contractsByModule,
        SparkReplicationOptions options)
    {
        foreach (var (sourceModule, contracts) in contractsByModule)
        {
            yield return new EtlScriptDeploymentMessage
            {
                SourceModuleName = sourceModule,
                Request = new EtlDeploymentRequest
                {
                    RequestingModule = options.ModuleName,
                    Contracts = contracts,
                },
            };
        }
    }

    /// <summary>
    /// Maps the replication endpoints (ETL deploy, sync apply).
    /// </summary>
    internal static IEndpointRouteBuilder MapSparkReplication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSparkReplicationEndpoints();
        return endpoints;
    }
}
