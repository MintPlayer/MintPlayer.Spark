# MintPlayer.Spark.Webhooks

Inbound webhooks for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark), from any
provider: payment providers, carriers, e-invoicing access points. A source declares how its
deliveries are verified and which handler receives them; the library does the rest the same way for
every provider.

- **Signature strategies:** an HMAC of the body in a header (algorithm, encoding and prefix
  configurable), Stripe's timestamped `t=…,v1=…` scheme with a replay tolerance, and fetch-by-id for
  providers that sign nothing (Mollie). Every check is fail-closed on an empty secret and
  constant-time.
- **Raw-body preservation:** the exact bytes the provider sent are verified, enqueued and handed to
  the handler. Nothing is parsed and re-serialized on the way.
- **Deduplication:** a delivery with a provider delivery id is enqueued at most once per source and
  id, through Spark Messaging's `BroadcastOnceAsync`, before the provider gets its `200`.
- **One route:** `POST /spark/webhooks/{source}`, a generator endpoint. A package can map its own
  route through the same receiver (GitHub keeps `/api/github/webhooks`).

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();
    spark.AddWebhookSource("stripe", source =>
    {
        source.Verification = services => new StripeSignatureStrategy(
            services.GetRequiredService<IConfiguration>()["Stripe:WebhookSecret"]);
        source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
        source.ContentTypes.Add("application/json");
        source.UseHandler<StripeWebhookHandler>();
    });
});

internal sealed partial class StripeWebhookHandler : IWebhookHandler
{
    public async Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        using var json = delivery.ReadJson();
        // … idempotent fulfilment
    }
}
```

The full guide, with the response codes, the three strategies, fetch-by-id and the GitHub source:
[docs/guide-webhooks.md](../../../docs/guide-webhooks.md).
