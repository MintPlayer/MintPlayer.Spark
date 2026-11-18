# @mintplayer/webhooks-receiver

Receive [Standard Webhooks](https://www.standardwebhooks.com) in Node 20+, Deno, Bun, Cloudflare Workers and other
fetch-style runtimes. It verifies the signature with WebCrypto, with no dependencies. Events are dispatched typed,
over the types `spark webhooks generate --lang ts` writes from a Spark site's event catalog.

## Generate the event types

```sh
spark webhooks generate --catalog https://shop.example.com/spark/apps/webhooks/catalog --lang ts --name Shop -o src/shop-webhooks.ts
```

This writes one interface per event's `data`, `ShopWebhookEvents` (event type → data type) and `shopWebhookEventTypes`.

## Receive

```ts
import { createWebhookReceiver } from '@mintplayer/webhooks-receiver';
import type { ShopWebhookEvents } from './shop-webhooks';

const receiver = createWebhookReceiver<ShopWebhookEvents>({
  secrets: [process.env.SHOP_WEBHOOK_SECRET!], // whsec_…; list the old one too while a secret rotates
  on: {
    'order.shipped': async (order, event) => {
      // event.messageId is the same on every retry and redelivery: deduplicate on it.
      await markShipped(order.orderId, event.messageId);
    },
  },
});

// Fetch-style hosts (Workers, Deno, Bun, Next.js route handlers, Hono):
export const POST = (request: Request) => receiver.fetch(request);
```

With Express or another Node framework, pass the raw body bytes (not parsed JSON) to `receive`:

```ts
app.post('/webhooks/shop', express.raw({ type: 'application/json' }), async (req, res) => {
  const result = await receiver.receive(req.headers, req.body);
  res.sendStatus(result.status);
});
```

The answers are:

- **204:** the event was handled. This includes `ping` and events without a handler (`onEvent` sees those), so the
  producer does not retry them.
- **401:** verification failed. `result.failure` says why: `missing-headers`, `invalid-timestamp`,
  `timestamp-out-of-tolerance` or `invalid-signature`.
- **400:** the signed body is not a webhook envelope.

An error thrown by a handler propagates to your host, which answers 5xx, and the producer retries later.

## Verify only

```ts
import { verifyWebhook, WebhookVerificationError } from '@mintplayer/webhooks-receiver';

const { messageId, timestamp } = await verifyWebhook(headers, rawBody, { secrets: [secret] });
```

It throws `WebhookVerificationError` (with its `failure`) when verification fails.

The timestamp tolerance is 300 seconds either way. Change it with `toleranceSeconds`. In tests, `now` replaces the
clock and `signWebhook(secret, id, timestamp, body)` signs a request.

The .NET counterparts are `MintPlayer.Webhooks.Receiver` (any .NET) and `MintPlayer.Spark.Webhooks.Receiver`, a
durable receiver for Spark apps.
