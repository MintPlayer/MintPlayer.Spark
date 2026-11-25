using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

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

        // The documents messaging stores, so a relational provider maps a table for each (#536 D40).
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoredTypeServiceCollectionExtensions.AddSparkStoredType<Models.SparkMessage>(services);
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoredTypeServiceCollectionExtensions.AddSparkStoredType<Models.SparkMessageProgress>(services);
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoreReadinessServiceCollectionExtensions.AddSparkStoreReadiness(services);

        services.AddScoped<MessageBus>();
        services.AddScoped<IMessageBus>(sp => sp.GetRequiredService<MessageBus>());
        services.AddScoped<IMessageOutbox>(sp => sp.GetRequiredService<MessageBus>());

        // Durable after-commit interceptors (#482, D17): the framework writes through this seam, and the
        // recipient runs them. In the default single-subscription mode the extra message type costs nothing.
        services.AddScoped<MintPlayer.Spark.Abstractions.Interceptors.ISparkAfterCommitOutbox, SparkAfterCommitOutbox>();
        services.AddScoped<IRecipient<MintPlayer.Spark.Abstractions.Interceptors.SparkAfterCommitWork>, SparkAfterCommitRecipient>();
        services.AddScoped<MessageCheckpoint>();
        services.AddScoped<IMessageCheckpoint>(sp => sp.GetRequiredService<MessageCheckpoint>());
        services.AddScoped<MessageContext>();
        services.AddScoped<IMessageContext>(sp => sp.GetRequiredService<MessageContext>());
        services.AddScoped<MessageProgress>();
        services.AddScoped<IMessageProgress>(sp => sp.GetRequiredService<MessageProgress>());

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

}
