using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Expiration;

namespace MintPlayer.Spark.Messaging;

internal static class SparkMessagingExtensions
{
    internal static IServiceCollection AddSparkMessaging(
        this IServiceCollection services,
        Action<SparkMessagingOptions>? configure = null,
        Action<IMessagingLaneBuilder>? lanes = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        var registry = new LaneRegistry();
        var laneBuilder = new MessagingLaneBuilder(registry);
        lanes?.Invoke(laneBuilder);
        services.AddSingleton(registry);
        services.AddSingleton(laneBuilder);

        // Every scheduled delay in messaging goes through TimeProvider so tests can drive backoff
        // with a fake clock instead of sleeping through it. TryAdd: the host may already have one.
        services.TryAddSingleton(TimeProvider.System);
        // Singleton on purpose — the sequence is only monotonic if one instance issues every value.
        services.AddSingleton<MessageSequence>();

        // IAsyncDocumentSession is now registered by AddSpark() in the core library.
        services.AddScoped<IMessageBus, MessageBus>();
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());

        // The built provider cannot be enumerated, so the descriptors are captured here for the
        // type allow-list and for lane discovery.
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        // R2-H6: type allow-list derived from the same scan
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        services.AddSingleton<MessageLaneDiscovery>();
        services.AddSingleton<IMessageLaneDiscovery>(sp => sp.GetRequiredService<MessageLaneDiscovery>());
        services.AddSingleton<MessageProcessor>();

        // ONE subscription for every lane. RavenDB allows three per database on the unlicensed and
        // Community tiers alike, so one per queue cannot scale — and exceeding the limit fails
        // silently, which is how three of a production app's six queues stayed dead for months.
        services.AddHostedService<MessageFeeder>();

        // No retry sweeper. It existed only to materialize "the backoff has elapsed" as a boolean,
        // because a subscription where-clause cannot evaluate now(). A lane's drain is an ordinary
        // index query and can, so the sweeper, SparkMessage.WakeUp and LastWakeUpUtc are all gone.

        return services;
    }

    /// <summary>
    /// Deploys the SparkMessages RavenDB index. Call this after the application is built.
    /// </summary>
    internal static IApplicationBuilder CreateSparkMessagingIndexes(this IApplicationBuilder app)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        new SparkMessages_ByQueue().Execute(documentStore);

        ValidateAndReportLanes(app.ApplicationServices);

        // Enable RavenDB document expiration so @expires metadata is honored
        documentStore.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
        {
            Disabled = false,
            DeleteFrequencyInSec = 36 * 60 * 60, // 36 hours (community license minimum)
        }));

        return app;
    }

    /// <summary>
    /// Fails startup on a lane that cannot work, and prints what the ones that can will actually do.
    /// </summary>
    /// <remarks>
    /// The table is deliberately printed rather than validated against a threshold. A line reading
    /// "7 rungs declared, 2 reachable, dead-letters after 6m" tells an operator more than any warning,
    /// and it is the one artifact that makes a misconfigured schedule visible before it matters.
    /// </remarks>
    private static void ValidateAndReportLanes(IServiceProvider services)
    {
        var registry = services.GetRequiredService<LaneRegistry>();
        var laneBuilder = services.GetRequiredService<MessagingLaneBuilder>();
        var discovery = services.GetRequiredService<MessageLaneDiscovery>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MintPlayer.Spark.Messaging");

        registry.Validate(discovery.MessageTypes(), laneBuilder.PartitionBlockBudget);

        foreach (var laneName in discovery.DiscoverLaneNames().Concat(registry.DeclaredLanes)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order())
        {
            var plan = registry.PlanFor(laneName);
            logger.LogInformation(
                "Lane {Lane}: {Mode}, {InFlight} in flight, retry {Retry}",
                laneName,
                plan.Ordered ? "ordered per partition" : "concurrent",
                plan.MaxInFlight,
                plan.Retry);
        }
    }
}
