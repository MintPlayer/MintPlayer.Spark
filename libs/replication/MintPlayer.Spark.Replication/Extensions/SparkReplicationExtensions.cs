using MintPlayer.Spark.Replication.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Services;
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
        services.AddSingleton<EtlScriptCollector>();
        services.AddSingleton<EtlTaskManager>();
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

        // Sync action services. A sync action is a message: it travels on the shared messaging
        // subscription, and retry, backoff, dead-lettering and retention all come from there. The
        // dedicated subscription worker and its sweeper are gone, along with the second RavenDB
        // subscription they cost — which matters because the licence allows three per database.
        services.AddScoped<ISyncActionInterceptor, SyncActionInterceptor>();
        services.DeclareSyncActionLane();

        return services;
    }

    /// <summary>
    /// On startup: (1) registers this module in the shared SparkModules database,
    /// (2) scans assemblies for [Replicated] attributes, (3) sends ETL scripts to
    /// source modules via the durable message bus.
    /// </summary>
    internal static WebApplication UseSparkReplication(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SparkReplication");
        var options = app.Services.GetRequiredService<IOptions<SparkReplicationOptions>>().Value;
        var registrationService = app.Services.GetRequiredService<ModuleRegistrationService>();
        var moduleDirectory = app.Services.GetRequiredService<IModuleDirectory>();
        var collector = app.Services.GetRequiredService<EtlScriptCollector>();
        var appStore = app.Services.GetRequiredService<IDocumentStore>();


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

                var scriptsByModule = collector.CollectScripts(assemblies);

                if (scriptsByModule.Count == 0)
                {
                    logger.LogInformation("No [Replicated] attributes found — no ETL scripts to deploy");
                    return;
                }

                // Step 3: For each source module, broadcast an ETL deployment message. The
                // recipient resolves the source module's URL from SparkModules on each delivery,
                // so we don't need to look it up here — and a not-yet-registered source no
                // longer needs a fabricated fallback URL (which previously baked
                // `http://{name}:5000` into the message and made retries hit a stale endpoint
                // forever even after the source module finally registered).
                foreach (var deploymentMessage in BuildDeploymentMessages(scriptsByModule, options, appStore))
                {
                    await messageBus.BroadcastAsync(deploymentMessage);
                    logger.LogInformation(
                        "Queued ETL deployment to '{SourceModule}' ({ScriptCount} scripts) via message bus",
                        deploymentMessage.SourceModuleName, deploymentMessage.Request.Scripts.Count);
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
    /// Pure function that projects the collected per-source-module scripts into the
    /// <see cref="EtlScriptDeploymentMessage"/> envelopes the message bus broadcasts.
    /// Extracted so the message-shape contract can be unit-tested without spinning up
    /// a host (which would otherwise require IMessageBus + IDocumentStore + Task.Run timing).
    /// </summary>
    internal static IEnumerable<EtlScriptDeploymentMessage> BuildDeploymentMessages(
        IReadOnlyDictionary<string, List<EtlScriptItem>> scriptsByModule,
        SparkReplicationOptions options,
        IDocumentStore appStore)
    {
        foreach (var (sourceModule, scripts) in scriptsByModule)
        {
            // Note: TargetDatabase and TargetUrls are captured here at send-time and travel
            // with the message — unlike the source module's URL, which the recipient
            // resolves freshly from SparkModules on each delivery. If the consumer module's
            // RavenDB cluster ever moves to new URLs, pending deployment messages will
            // carry stale values until the next app startup re-broadcasts.
            yield return new EtlScriptDeploymentMessage
            {
                SourceModuleName = sourceModule,
                Request = new EtlScriptRequest
                {
                    RequestingModule = options.ModuleName,
                    TargetDatabase = appStore.Database,
                    TargetUrls = appStore.Urls,
                    Scripts = scripts,
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
