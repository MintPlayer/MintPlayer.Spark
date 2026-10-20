using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Webhooks;

/// <summary>
/// The one handler of every payments webhook source (<c>payments-{provider}</c>, X5). It runs from the
/// <c>spark-webhooks</c> queue: the provider has its 200 once the delivery is durable, and this retries with backoff
/// when it throws. The provider adapter turns the delivery into events (Mollie: fetching the payment with the
/// credentials it was created with; Stripe: reading the signed body), and <see cref="ISparkPayments"/> records them.
/// </summary>
internal sealed class PaymentWebhookHandler(
    PaymentProviderRegistry registry,
    IPaymentMerchantResolver merchants,
    ISparkPayments payments,
    ILogger<PaymentWebhookHandler> logger) : IWebhookHandler
{
    public async Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (PaymentWebhookSources.ProviderOf(delivery.Source) is not { } name)
            throw new NonRetryableException($"Webhook source '{delivery.Source}' is not a payments source.");

        IPaymentProvider provider;
        try { provider = registry.Get(name); }
        catch (InvalidOperationException ex) { throw new NonRetryableException(ex.Message); }

        var events = await provider.ParseWebhookAsync(delivery, merchants, cancellationToken);
        if (events.Count == 0)
        {
            logger.LogDebug("A {Provider} webhook delivery carried nothing for us.", provider.Name);
            return;
        }
        await payments.ProcessEventsAsync(events, cancellationToken);
    }
}
