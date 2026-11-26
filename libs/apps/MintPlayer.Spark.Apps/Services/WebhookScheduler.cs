using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Messaging;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Messaging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>
/// Puts deliveries on the wire: straight onto the queue (unordered), or through the destination's
/// <see cref="WebhookChain"/> (D22, S9), which keeps one delivery in flight and starts the next when it completes.
/// Every chain change runs under optimistic concurrency, so two events racing for one chain cannot both start.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookScheduler
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkAppsOptions> options;

    public bool Ordered => options.Value.Ordering == WebhookOrdering.PerDestination;

    /// <summary>
    /// Stores and schedules <paramref name="delivery"/> in <paramref name="session"/> (which the caller saves, with
    /// optimistic concurrency on). <paramref name="bypassChain"/> is for pings and redeliveries, which are not events
    /// in the stream.
    /// </summary>
    public async Task ScheduleAsync(IAsyncDocumentSession session, WebhookDelivery delivery, bool bypassChain, CancellationToken cancellationToken)
    {
        var id = delivery.Id.Value;
        if (!Ordered || bypassChain)
        {
            await session.StoreAsync(delivery, id, cancellationToken);
            await outbox.EnqueueAsync(session, new WebhookDeliveryMessage { DeliveryId = id }, cancellationToken: cancellationToken);
            return;
        }

        delivery.Ordered = true;
        await session.StoreAsync(delivery, id, cancellationToken);
        var chainId = WebhookChain.IdFor(delivery.EndpointId);
        var chain = await session.LoadAsync<WebhookChain>(chainId.Value, cancellationToken);
        if (chain is null)
        {
            chain = new WebhookChain { Id = chainId };
            await session.StoreAsync(chain, chainId.Value, cancellationToken);
        }
        if (chain.InFlight is null && !chain.Halted)
        {
            chain.InFlight = id;
            await outbox.EnqueueAsync(session, new WebhookDeliveryMessage { DeliveryId = id }, cancellationToken: cancellationToken);
        }
        else
        {
            // Kept in event order (the id's time key sorts newest first): a fan-out retried after a later event's
            // still lands ahead of it. Ignoring case: one queued before #536 waits as "WebhookDeliveries/…".
            var index = chain.Waiting.FindIndex(w => StringComparer.OrdinalIgnoreCase.Compare(w, id) < 0);
            chain.Waiting.Insert(index < 0 ? chain.Waiting.Count : index, id);
        }
    }

    /// <summary>
    /// An ordered delivery is done (succeeded, or given up). <paramref name="halt"/>: stop the chain on it instead of
    /// moving on. A delivery that is not the chain's head (an old message, a redelivery) changes nothing.
    /// </summary>
    public Task CompleteAsync(WebhookDelivery delivery, bool halt, CancellationToken cancellationToken)
        => !delivery.Ordered ? Task.CompletedTask : WithChainAsync(delivery.EndpointId, (session, chain) =>
        {
            if (!string.Equals(chain.InFlight, delivery.Id.Value, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
            if (halt)
            {
                chain.Halted = true;
                return Task.FromResult(true);
            }
            return StartNextAsync(session, chain, cancellationToken);
        }, cancellationToken);

    /// <summary>A halted chain resumes (the endpoint was re-enabled): its failed head is tried again, then the rest.</summary>
    public Task ResumeAsync(string endpointPublicId, CancellationToken cancellationToken)
        => WithChainAsync(endpointPublicId, async (session, chain) =>
        {
            if (!chain.Halted)
                return false;
            chain.Halted = false;
            if (chain.InFlight is { } head && await session.LoadAsync<WebhookDelivery>(head, cancellationToken) is { } delivery)
            {
                delivery.Status = WebhookDeliveryStatus.Pending;
                delivery.CompletedUtc = null;
                await outbox.EnqueueAsync(session, new WebhookDeliveryMessage { DeliveryId = head }, cancellationToken: cancellationToken);
                return true;
            }
            return await StartNextAsync(session, chain, cancellationToken);
        }, cancellationToken);

    private async Task<bool> StartNextAsync(IAsyncDocumentSession session, WebhookChain chain, CancellationToken cancellationToken)
    {
        chain.InFlight = null;
        while (chain.Waiting.Count > 0)
        {
            var next = chain.Waiting[0];
            chain.Waiting.RemoveAt(0);
            // A waiting delivery whose log entry expired or was removed is skipped.
            if (await session.LoadAsync<WebhookDelivery>(next, cancellationToken) is null)
                continue;
            chain.InFlight = next;
            await outbox.EnqueueAsync(session, new WebhookDeliveryMessage { DeliveryId = next }, cancellationToken: cancellationToken);
            break;
        }
        return true;
    }

    private async Task WithChainAsync(string endpointPublicId, Func<IAsyncDocumentSession, WebhookChain, Task<bool>> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var chain = await session.LoadAsync<WebhookChain>(WebhookChain.IdFor(endpointPublicId).Value, cancellationToken);
            if (chain is null || !await change(session, chain))
                return;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < 10)
            {
                // Another delivery or event moved the chain first: read it again.
            }
        }
    }
}
