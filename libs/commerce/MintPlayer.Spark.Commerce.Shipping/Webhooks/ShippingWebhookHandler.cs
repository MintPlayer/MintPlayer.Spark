using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Commerce.Shipping.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.Webhooks;

/// <summary>
/// The one handler of every carrier webhook source (<c>shipping-{carrier}</c>, X5). It runs from the
/// <c>spark-webhooks</c> queue: the carrier has its 200 once the delivery is durable, and this retries with backoff
/// when it throws. Tracking webhooks are at-least-once and unordered; <see cref="ISparkShipping.RecordTrackingAsync"/>
/// deduplicates on the event id and orders by event time (warehouse §4.12).
/// </summary>
internal sealed class ShippingWebhookHandler(
    ShippingCarrierRegistry registry,
    ISparkShipping shipping,
    ILogger<ShippingWebhookHandler> logger) : IWebhookHandler
{
    public async Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (ShippingWebhookSources.CarrierOf(delivery.Source) is not { } name || registry.Find(name) is not { } carrier)
            throw new NonRetryableException($"Webhook source '{delivery.Source}' is not a registered carrier's.");

        var events = await carrier.ParseWebhookAsync(delivery, cancellationToken);
        if (events.Count == 0)
        {
            logger.LogDebug("A {Carrier} webhook delivery carried no tracking.", carrier.Name);
            return;
        }
        await shipping.RecordTrackingAsync(carrier.Name, events, cancellationToken);
    }
}
