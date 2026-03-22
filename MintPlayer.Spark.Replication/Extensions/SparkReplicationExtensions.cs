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
        services.AddSingleton<EtlScriptCollector>();
        services.AddSingleton<EtlTaskManager>();
        services.AddScoped<IRecipient<EtlScriptDeploymentMessage>, EtlScriptDeploymentRecipient>();
        services.AddHttpClient("spark-etl");

        // Sync action services
        services.AddScoped<ISyncActionInterceptor, SyncActionInterceptor>();
        services.AddHostedService<SyncActionSubscriptionWorker>();
        services.AddHttpClient("spark-sync");

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
        var collector = app.Services.GetRequiredService<EtlScriptCollector>();
        var appStore = app.Services.GetRequiredService<IDocumentStore>();

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
                using var modulesStore = registrationService.CreateModulesStore();
                await registrationService.RegisterAsync(modulesStore);

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

                // Step 3: For each source module, look up its URL and send ETL scripts via message bus
                foreach (var (sourceModule, scripts) in scriptsByModule)
                {
                    var sourceModuleId = $"moduleInformations/{sourceModule}";
                    using var session = modulesStore.OpenAsyncSession();
                    var sourceInfo = await session.LoadAsync<ModuleInformation>(sourceModuleId);

                    if (sourceInfo == null)
                    {
                        logger.LogWarning(
                            "Source module '{SourceModule}' not found in SparkModules database. " +
                            "ETL scripts will be retried via the message bus when it registers.",
                            sourceModule);

                        // Still send the message — the recipient will fail and the message bus will retry
                        // with exponential backoff until the source module registers and its endpoint is reachable
                        sourceInfo = new ModuleInformation
                        {
                            AppName = sourceModule,
                            AppUrl = $"http://{sourceModule.ToLowerInvariant()}:5000",
                            DatabaseName = "unknown",
                            RegisteredAtUtc = DateTime.UtcNow,
                        };
                    }

                    var request = new EtlScriptRequest
                    {
                        RequestingModule = options.ModuleName,
                        TargetDatabase = appStore.Database,
                        TargetUrls = appStore.Urls,
                        Scripts = scripts,
                    };

                    var deploymentMessage = new EtlScriptDeploymentMessage
                    {
                        SourceModuleName = sourceModule,
                        SourceModuleUrl = sourceInfo.AppUrl,
                        Request = request,
                    };

                    await messageBus.BroadcastAsync(deploymentMessage);
                    logger.LogInformation(
                        "Queued ETL deployment to '{SourceModule}' ({ScriptCount} scripts) via message bus",
                        sourceModule, scripts.Count);
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
    /// Maps the replication endpoints (ETL deploy, sync apply).
    /// </summary>
    internal static IEndpointRouteBuilder MapSparkReplication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSparkReplicationEndpoints();
        return endpoints;
    }
}
