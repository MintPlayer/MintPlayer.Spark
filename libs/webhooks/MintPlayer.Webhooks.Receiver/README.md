# MintPlayer.Webhooks.Receiver

[Standard Webhooks](https://www.standardwebhooks.com) for any .NET (`netstandard2.0`). Receive webhooks sent by a
MintPlayer.Spark.Apps site, or by any Standard Webhooks producer.

- `WebhookVerifier`: checks the `webhook-id`, `webhook-timestamp` and `webhook-signature` headers. It rejects
  timestamps outside the tolerance (5 minutes by default), accepts any configured secret (useful while a secret
  rotates), and fails closed when nothing is configured.
- `WebhookProcessor`: routes events to handlers. Override `OnEventAsync` to handle events untyped, or generate a typed
  processor from the site's event catalog with `spark webhooks generate`.
- `WebhookReceiver.HandleAsync(getHeader, body, processor)`: the whole path, for any host.
- `HmacWebhookSigner` / `WebhookSignatureHeader.Create`: the producer side, used by MintPlayer.Spark.Apps.

```csharp
var receiver = new WebhookReceiver(new WebhookVerifierOptions { Secrets = { "whsec_..." } });
var result = await receiver.HandleAsync(name => request.Headers[name], bodyBytes, new MyProcessor());
// answer result.StatusCode
```

On ASP.NET Core, use `MintPlayer.Webhooks.Receiver.AspNetCore` (`MapWebhookReceiver<TProcessor>`). For `v2a`
(ed25519) signatures, use `MintPlayer.Webhooks.Ed25519`.

Deduplicate on `WebhookContext.MessageId`: deliveries are at-least-once, and a redelivery keeps the same id.
