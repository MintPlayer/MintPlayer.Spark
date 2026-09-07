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
        // DeleteFrequencyInSec is pinned to the Community licence's minimum, and this is a licence
        // CONSTRAINT rather than a tuning choice. Measured against production on 2026-09-07:
        // /license/status reports Type "Community" with MinPeriodForExpirationInHours: 36, and the
        // live database record carries DeleteFrequencyInSec: 129600 — exactly that minimum.
        //
        // Configuring anything smaller does not degrade, it FAILS THE SERVER: the cluster command
        // is rejected by ClusterStateMachine.AssertExpirationConfiguration and the RavenDB process
        // dies. It presents as "the server exited before becoming healthy", which looks nothing
        // like a licence problem — reproduced locally with no RAVENDB_LICENSE, and it is why
        // UploadActionDogfoodTests cannot pass without one.
        //
        // This comment previously said the opposite. It claimed 36 h was "a ceiling on how often
        // the sweep may run" and therefore removed the pin to take the 60 s server default, which
        // would have taken production's database down on the next deploy. CI did not catch it
        // because CI holds a *Developer* licence, which imposes no such minimum; only production is
        // Community. A limit that exists on one licence tier and not another cannot be verified on
        // the tier CI runs.
        //
        // The cost is real and accepted: on Community an expired message can linger up to 36 h past
        // its retention. The alternative is not a faster sweep, it is no server.
        //
        // The same 36 h floor applies to MinPeriodForRefreshInHours, which is the operative reason
        // @refresh is unusable for message redelivery here. The spike that measured @refresh waking
        // a subscription in 4.65 s ran against a Developer licence; the mechanism is real, but on
        // Community it cannot be scheduled more often than every 36 h, so it cannot carry retries.
        const int communityMinimumExpirationFrequencySeconds = 36 * 60 * 60;

        documentStore.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
        {
            Disabled = false,
            DeleteFrequencyInSec = communityMinimumExpirationFrequencySeconds,
        }));

        return app;
    }
}
