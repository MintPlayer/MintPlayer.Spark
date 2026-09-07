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

        // IAsyncDocumentSession is now registered by AddSpark() in the core library.
        services.AddScoped<IMessageBus, MessageBus>();
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());

        // Register IServiceCollectionAccessor so the manager can discover queues at runtime
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        // R2-H6: type allow-list derived from the same scan
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();
        // "Does anything consume this message type?" — asked by publishers that would otherwise
        // broadcast onto a queue with no worker, whose documents are never drained.
        services.AddSingleton<MessageRecipientRegistry>();
        services.AddSingleton<IMessageRecipientRegistry>(sp => sp.GetRequiredService<MessageRecipientRegistry>());

        // The per-message contract, shared by both subscription modes so they cannot drift.
        services.AddSingleton<MessageProcessor>();
        // In-process per-queue FIFO lanes: what replaces one subscription per queue.
        services.AddSingleton<MessageQueueRouter>();
        // Liveness only — RavenDB's WaitForFree, not this, is what makes feeding exclusive.
        services.AddSingleton<MessagingLeaseManager>();
        services.AddSingleton<LegacySubscriptionCleanup>();

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

        // Enable RavenDB document expiration so @expires metadata is honored.
        //
        // DeleteFrequencyInSec is deliberately not set, which takes the server default of 60 s.
        // It used to be pinned to 36 hours with the comment "community license minimum", which
        // inverted the limit it was citing: on a restricted licence 36 h is the *smallest
        // frequency value permitted* — i.e. a ceiling on how often the sweep may run — not a
        // floor the configuration must clear. Pinning it meant expired messages lingered up to
        // 36 h past their retention, and the same misreading was the load-bearing reason
        // @refresh was written off as unusable for redelivery elsewhere in the repo.
        documentStore.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
        {
            Disabled = false,
        }));

        return app;
    }
}
