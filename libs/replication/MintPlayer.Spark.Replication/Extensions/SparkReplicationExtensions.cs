using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication;

internal static class SparkReplicationExtensions
{
    /// <summary>
    /// Registers replication's services (warehouse §4.2): the module registry, the replica catalog (also the framework's
    /// <see cref="ISparkReplicaTypes"/>), the owner's approval gate and delivery, the consumer's apply endpoint and
    /// write-back, and their message recipients. No hosted service and no subscription: everything durable rides the
    /// message queue (<c>spark-replication</c>) and its transactional outbox.
    /// </summary>
    internal static IServiceCollection AddSparkReplication(
        this IServiceCollection services,
        Action<SparkReplicationOptions> configure)
    {
        services.Configure(configure);
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMintPlayerSparkReplication();

        services.AddSingleton<ModuleRegistrationService>();
        services.AddSingleton<IModuleDirectory, ModuleDirectory>();
        // R2-C1/C2: outbound calls attach this module's client certificate (per-target overrides honoured, F5).
        services.AddSingleton<IReplicationHttpClientProvider, ReplicationHttpClientProvider>();

        services.AddSingleton<ReplicaCatalog>();
        services.AddSingleton<ISparkReplicaTypes>(sp => sp.GetRequiredService<ReplicaCatalog>());
        services.AddSingleton<IReplicaSourceTypes, ReplicaSourceTypes>();
        services.AddSingleton<IModuleGrants, ModuleGrants>();
        services.AddSingleton<IReplicationTransportResolver, ReplicationTransportResolver>();
        services.AddSingleton<IEtlTaskManager, EtlTaskManager>();

        services.AddScoped<IReplicaSubscriptions, ReplicaSubscriptions>();
        services.AddScoped<IReplicaFanOut, ReplicaFanOut>();
        services.AddScoped<IReplicaPublisher, ReplicaPublisher>();
        services.AddScoped<IReplicaApplier, ReplicaApplier>();
        services.AddScoped<IReplicaReconciler, ReplicaReconciler>();
        services.AddScoped<ISyncApplyService, SyncApplyService>();
        services.AddScoped<ISyncActionSender, SyncActionSender>();
        services.AddScoped<ISyncOutcomeNotifier, SyncOutcomeNotifier>();

        services.AddScoped<IRecipient<ReplicaRegistrationMessage>, ReplicaRegistrationRecipient>();
        services.AddScoped<IRecipient<ReplicaDeployMessage>, ReplicaDeployRecipient>();
        services.AddScoped<IRecipient<ReplicaPushMessage>, ReplicaPushRecipient>();
        services.AddScoped<IRecipient<ReplicaReconcileMessage>, ReplicaReconcileRecipient>();
        services.AddScoped<IRecipient<SyncActionDispatchMessage>, SyncActionDispatchRecipient>();
        services.AddScoped<IRecipient<ReplicaChangedMessage>, ReplicaChangedRecipient>();

        // Owner: a committed write of a source document is pushed (HTTP transport). Consumer: a replica edit becomes a
        // write-back and never a local write; the row shows a pending badge.
        services.AddSparkInterceptor<ReplicaPublishInterceptor>();
        services.AddSparkInterceptor<ReplicaWriteBackInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, ReplicationLiveTopicPolicy>());

        // The queue's library defaults; Spark:Messaging:Queues:spark-replication overrides them. Ordering is not needed
        // (change-vector guard, op ids), so several pushes may run at once and one dead consumer does not hold up the rest.
        services.Configure<SparkMessagingOptions>(options =>
            options.Queues.TryAdd(ReplicationQueue.Name, new SparkQueueOptions { MaxConcurrency = 4 }));

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkReplicationExtensions).Assembly, "SparkMail/");

        return services;
    }

    /// <summary>
    /// On startup: refuses what cannot work (a replica without a source, a store collection name the generated one
    /// disagrees with, <c>Transport=Etl</c> on a licence without ETL, no messaging), then — in the background —
    /// registers this module in <c>SparkModules</c> and queues one registration per owner whose set hash is not settled
    /// (WD15), plus an empty one for an owner it no longer reads from (F18).
    /// </summary>
    internal static WebApplication UseSparkReplication(this WebApplication app)
    {
        var services = app.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SparkReplication");
        var appStore = services.GetRequiredService<IDocumentStore>();
        var catalog = services.GetRequiredService<ReplicaCatalog>();

        var problems = catalog.Problems(appStore).ToList();
        if (services.GetService<IMessageRecipientRegistry>() is null)
            problems.Add("Replication needs messaging: call spark.AddMessaging() before spark.AddReplication().");
        if (problems.Count > 0)
            throw new InvalidOperationException("Replication cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));

        // F18: Etl pinned on a licence without it refuses startup with the fix in the message.
        services.GetRequiredService<IReplicationTransportResolver>().ResolveAsync().GetAwaiter().GetResult();

        _ = Task.Run(async () =>
        {
            try
            {
                var registrationService = services.GetRequiredService<ModuleRegistrationService>();
                await registrationService.RegisterAsync(services.GetRequiredService<IModuleDirectory>().Store);
                await QueueRegistrationsAsync(services, catalog, appStore, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Spark replication startup");
            }
        });

        return app;
    }

    /// <summary>One registration message per owner whose set hash this module has not had acknowledged as approved.</summary>
    internal static async Task QueueRegistrationsAsync(IServiceProvider services, ReplicaCatalog catalog, IDocumentStore store, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var now = (scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        using var session = store.OpenAsyncSession();
        var known = await session.Advanced.LoadStartingWithAsync<ReplicaRegistrationState>("SparkReplicaRegistrations/", pageSize: 1024);
        var owners = catalog.Owners.Concat(known.Select(k => k.Owner)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var owner in owners)
        {
            var state = known.FirstOrDefault(k => string.Equals(k.Owner, owner, StringComparison.OrdinalIgnoreCase));
            if (state?.IsSettled(catalog.SetHash(owner)) == true)
            {
                logger.LogDebug("Replicas from '{Owner}' are registered and approved as they are; nothing posted.", owner);
                continue;
            }
            await bus.BroadcastAsync(new ReplicaRegistrationMessage { Owner = owner, QueuedAtUtc = now });
            logger.LogInformation("Queued the replica registration with '{Owner}'.", owner);
        }
    }

    /// <summary>Maps <c>/spark/replicas/*</c> and <c>/spark/sync/apply</c>.</summary>
    internal static IEndpointRouteBuilder MapSparkReplication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSparkReplicationEndpoints();
        return endpoints;
    }
}
