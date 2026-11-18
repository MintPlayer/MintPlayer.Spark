# Receiving webhooks

How to receive the events of a Spark site that sends webhooks with `MintPlayer.Spark.Apps`
([site-developer guide](guide-apps-webhooks.md)). The events follow [Standard Webhooks](https://www.standardwebhooks.com/),
so any Standard Webhooks library can verify them. The receivers below also give you typed events and the right answers.

## Pick a receiver

| You run | Use | What you get |
|---|---|---|
| A Spark app | [`MintPlayer.Spark.Webhooks.Receiver`](../libs/webhooks/MintPlayer.Spark.Webhooks.Receiver/README.md) | Verify, enqueue once per `webhook-id`, answer 204, then your `IRecipient<WebhookMessage<T>>` runs durably with Messaging's retries |
| Any ASP.NET Core app | [`MintPlayer.Webhooks.Receiver.AspNetCore`](../libs/webhooks/MintPlayer.Webhooks.Receiver.AspNetCore/README.md) | An endpoint that verifies and calls the generated `…WebhooksProcessor` in the request |
| Any .NET (netstandard2.0) | [`MintPlayer.Webhooks.Receiver`](../libs/webhooks/MintPlayer.Webhooks.Receiver/README.md) | `WebhookVerifier`, for a host of your own |
| Node 20+, Deno, Bun, Workers | [`@mintplayer/webhooks-receiver`](../libs/node_packages/webhooks-receiver/README.md) | `createWebhookReceiver<Events>()`, with `fetch(Request)` and `receive(headers, body)` |

## 1. Generate the event types

Every Spark site publishes its event catalog at `/spark/apps/webhooks/catalog`. The Spark CLI turns it into types:

```sh
spark webhooks generate --catalog https://shop.example.com/spark/apps/webhooks/catalog --lang cs --name Shop --namespace MyApp.Webhooks -o Webhooks/Shop.g.cs
spark webhooks generate --catalog https://shop.example.com/spark/apps/webhooks/catalog --lang ts --name Shop -o src/shop-webhooks.ts
```

- **C#** gives one class per object with `[JsonPropertyName]` (it compiles on netstandard2.0), and a
  `ShopWebhooksProcessor` with `EventTypes`, a constant per event and a virtual `On…Async` per event.
- **TypeScript** gives one interface per object, enums as string unions, `ShopWebhookEvents` (event type → data type)
  and `shopWebhookEventTypes`.

`--catalog` also takes a file. Regenerate when the site adds events. An event you did not generate still arrives, and
goes to the unknown-event path of each receiver.

## 2. Get a secret

Register the endpoint on the site (its account or organization settings, `account/webhooks`). The answer shows the
`whsec_…` secret once; store it as a secret of your app. **Rotate secret** issues a new one, and the old one keeps signing
next to it for 24 hours: list both in `Secrets` until you have deployed the new one.

An App gets its webhook secret once, when it is registered. Its events carry `installation.id`.

## 3. Answer correctly

- **2xx quickly.** The site waits 15 seconds and counts anything slower as a failure. Do the work after answering (the
  Spark receiver does this for you) or keep it short.
- **Deduplicate on `webhook-id`.** A retry and a redelivery carry the same id. The Spark receiver handles each id once;
  elsewhere, keep the id (`event.messageId` in TypeScript, `WebhookContext.MessageId` in .NET) with the work it caused.
- **401 for a bad signature or a timestamp more than 5 minutes off.** All the receivers do this. Keep the clock in sync.
- **5xx or a timeout makes the site retry**: 8 attempts over about 32 hours. `429` or `503` with `Retry-After` waits as
  asked (at most an hour) without spending an attempt. **`410 Gone` disables the endpoint**: answer it only when you want
  no more events.
- **Ping:** the site sends a `ping` event when you press Ping, and to an App's webhook when the App is created or its
  URL changes. The receivers answer it with 204.
- Payloads over the site's limit (256 KB by default) arrive thin: `data` is `{ "truncated": true, "size": n }`. Read the
  object through the site's API instead.

## 4. Apps: call back with an installation token

An App receives an owner's events after the owner installs it. Entity events (`car.updated`, …) carry only the id, so the
App reads the row through the site's API. For that it needs an installation token from the site's identity provider:

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&installation_id=in_…
&client_id=<the App's client id>
&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
&client_assertion=<a JWT signed RS256 with the App's private key: iss = sub = client id, aud = the token endpoint, short exp, unique jti>
```

The token lives 60 minutes and allows what the installation was granted, no more. It stops working at once when the
installation is suspended or removed. Ask for a new one per installation when it expires; do not keep one per App.

## 5. Test

- .NET: `HmacWebhookSigner` over a `WebhookSigningInput` (in `MintPlayer.Webhooks.Receiver`) signs a request, so a test
  can post a valid delivery to your endpoint.
- TypeScript: `signWebhook(secret, id, timestamp, body)` does the same, and the `now` option fixes the clock.
- The Standard Webhooks specification's test vector passes in both.
