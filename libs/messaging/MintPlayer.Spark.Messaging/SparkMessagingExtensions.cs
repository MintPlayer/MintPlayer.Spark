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
        Action<SparkMessagingOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        // Every scheduled delay in messaging goes through TimeProvider so tests can drive backoff
        // with a fake clock instead of sleeping through it. TryAdd: the host may already have one.
        services.TryAddSingleton(TimeProvider.System);
        // Singleton on purpose — the sequence is only monotonic if one instance issues every value.
        services.AddSingleton<MessageSequence>();

        // IAsyncDocumentSession is now registered by AddSpark() in the core library.
        services.AddScoped<IMessageBus, MessageBus>();
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());

        // Register IServiceCollectionAccessor so the manager can discover queues at runtime
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        // R2-H6: type allow-list derived from the same scan
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        services.AddHostedService<MessageSubscriptionManager>();
        // Issue #233: periodic wake-up for messages parked at Failed (retry backoff) or
        // Pending with a future NextAttemptAtUtc (delayed broadcast) — without it those
        // documents are never re-evaluated by the subscriptions and never redelivered.
        services.AddHostedService<MessageRetrySweeper>();

        return services;
    }

    /// <summary>
    /// Deploys the SparkMessages RavenDB index. Call this after the application is built.
    /// </summary>
    internal static IApplicationBuilder CreateSparkMessagingIndexes(this IApplicationBuilder app)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        new SparkMessages_ByQueue().Execute(documentStore);

        // Enable RavenDB document expiration so @expires metadata is honored
        documentStore.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
        {
            Disabled = false,
            DeleteFrequencyInSec = 36 * 60 * 60, // 36 hours (community license minimum)
        }));

        return app;
    }
}
