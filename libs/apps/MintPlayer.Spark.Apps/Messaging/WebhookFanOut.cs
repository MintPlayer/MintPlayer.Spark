using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Apps.Messaging;

/// <summary>
/// Turns a <see cref="WebhookEventRaised"/> into one <see cref="WebhookDelivery"/> per subscribed, enabled endpoint of
/// its owner. Delivery ids are derived from the event, so a retried fan-out finds the deliveries it already made and
/// makes none twice.
/// </summary>
internal sealed partial class WebhookFanOut : IRecipient<WebhookEventRaised>
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly WebhookScheduler scheduler;
    [Inject] private readonly IOptions<SparkAppsOptions> options;

    public async Task HandleAsync(WebhookEventRaised message, CancellationToken cancellationToken = default)
    {
        var owner = new WebhookOwner(message.OwnerKind, message.OwnerKey);
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            // A chain changed by another event in between is read again; so is a delivery made by a concurrent run.
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

            var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner), cancellationToken);
            if (registry is null || registry.EndpointIds.Count == 0)
                return;
            var endpoints = await session.LoadAsync<WebhookEndpoint>(registry.EndpointIds, cancellationToken);

            var targets = endpoints.Values
                .Where(e => e is not null && e.Enabled && e.Subscribes(message.EventType))
                .Select(e => (Endpoint: e!, DeliveryId: WebhookDelivery.IdFor(e!.PublicId, message.CreatedUtc, message.MessageId)))
                .ToList();
            if (targets.Count == 0)
                return;

            var existing = await session.LoadAsync<WebhookDelivery>(targets.Select(t => t.DeliveryId), cancellationToken);
            foreach (var (endpoint, deliveryId) in targets)
            {
                if (existing.TryGetValue(deliveryId, out var made) && made is not null)
                    continue;
                await scheduler.ScheduleAsync(session, New(deliveryId, message, endpoint), bypassChain: false, cancellationToken);
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < 10)
            {
            }
        }
    }

    private WebhookDelivery New(string id, WebhookEventRaised message, WebhookEndpoint endpoint) => new()
    {
        Id = id,
        MessageId = message.MessageId,
        EventType = message.EventType,
        EndpointId = endpoint.PublicId,
        Payload = message.Payload,
        Status = WebhookDeliveryStatus.Pending,
        CreatedUtc = message.CreatedUtc,
        ExpiresAtUtc = message.CreatedUtc + options.Value.DeliveryRetention,
    };
}
