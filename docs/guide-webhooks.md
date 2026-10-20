# Inbound webhooks

How a Spark app receives webhooks from a provider (a payment provider, a carrier, an e-invoicing
access point, GitHub) with `MintPlayer.Spark.Webhooks`: one source per provider, verified at the
request, deduplicated, and handled from a queue.

> **The webhook is the source of truth, the redirect is not.** A provider calls back whether or not
> the user's browser ever comes back, and it calls back late, twice, or out of order. Handlers are
> therefore idempotent and act on the state they are given (or fetch), never on "this is the first
> time I hear of it".

---

## 0. The shape, in one table

| | |
|---|---|
| **The app declares** | a source: a name, a verification strategy, where the delivery id is, and one handler |
| **Spark ships** | the route `POST /spark/webhooks/{source}`, raw-body reading under a size cap, the three strategies, deduplication through Spark Messaging, and the queue `spark-webhooks` |
| **The provider is told** | `200` once the delivery is durable (enqueued), `401` when it fails verification |
| **The handler runs** | from the queue, with Messaging's retries and backoff |

---

## 1. Registering a source

```csharp
spark.AddMessaging();
spark.AddWebhookSource("stripe", source =>
{
    // Built per delivery, from the request's services: a rotated secret applies without a restart.
    source.Verification = services => new StripeSignatureStrategy(
        services.GetRequiredService<IConfiguration>()["Stripe:WebhookSecret"]);
    // Deduplicate on Stripe's event id (evt_…).
    source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
    source.ContentTypes.Add("application/json"); // anything else: 415
    source.MaxBodyBytes = 512 * 1024;            // default 1 MiB; larger: 413
    source.UseHandler<StripeWebhookHandler>();   // exactly one per source
});
```

| Setting | Default | Meaning |
|---|---|---|
| name | — | 1–64 lower-case letters, digits or `-`; the route's last segment and part of the dedupe key |
| `Verification` | required | `Func<IServiceProvider, IWebhookSignatureStrategy>`; there is no unverified source |
| `DeliveryId` | none | `WebhookDeliveryIds.FromHeader(…)` or `.FromJsonProperty(…)`; no id means no deduplication |
| `ContentTypes` | any | media types accepted; parameters such as `charset` are ignored |
| `MaxBodyBytes` | 1 MiB | refused with `413` from the declared `Content-Length`, or as the body streams in |
| `SharedRoute` | `true` | served at `/spark/webhooks/{name}`; `false` for a package that maps its own route (§6) |
| `ScrubPayloadOnTerminal` | `true` | the queued payload is cleared once handled or dead-lettered; provider payloads carry personal data |
| `UseHandler<T>(handling)` | `Queued` | `Inline` runs the handler inside the request instead (§5) |

A source missing its verification or its handler, a second handler, a bad name, or a name used
twice fails at registration, not on the first delivery.

---

## 2. What the route answers

`POST /spark/webhooks/{source}`, a generator endpoint with antiforgery explicitly off (a provider has
no browser session). It is mapped only when at least one source uses the shared route.

| Status | When |
|---|---|
| `200` | verified, and enqueued (or, for an inline source, handled) |
| `401` | verification failed: no secret configured, no signature, wrong signature, outside the replay tolerance, or no usable resource id |
| `404` | no such source on this route, before a byte of the body is read |
| `413` | body over `MaxBodyBytes` |
| `415` | content type not in `ContentTypes` |
| `500` | the enqueue (or the inline handler) threw; the provider retries |

Every refusal is a bare status: a provider reads nothing else, and a body would only describe the
source to whoever is probing. The reason is logged (`Webhook source {Source}: delivery refused
({Reason})`); a source without a secret logs an **error**, because it refuses everything.

---

## 3. Raw bytes, never re-serialized

The body is read as bytes in the endpoint's binding hook and kept as those bytes all the way to the
handler: verified as bytes, stored in the queued message as base64 (which round-trips every byte),
and handed over as `WebhookDelivery.Body`.

This is the one rule that cannot be bent. A provider signs the bytes it sent, and any trip through a
JSON object model changes them: `1.50` becomes `1.5`, `a\/b` becomes `a/b`, and Newtonsoft's default
date handling even rebases `+02:00` into the host's local offset, so the damage depends on the
machine. A minifier over the parsed object does not undo it. Capture the bytes; never reconstruct them.

In a handler, read the body with `delivery.ReadJson()` (a `JsonDocument`, disposed by you) or
`delivery.ReadBodyText()`. `Cookie` and `Authorization` request headers are never stored with a
delivery.

---

## 4. The three strategies

### 4.1 HMAC of the body in a header

```csharp
source.Verification = services => new HmacSignatureStrategy(
    new HmacSignatureScheme(
        Header: "X-Shopify-Hmac-Sha256",
        Algorithm: WebhookHmacAlgorithm.Sha256,      // Sha1, Sha256, Sha384, Sha512
        Encoding: WebhookSignatureEncoding.Base64,   // or Hex (lower-case)
        Prefix: ""),                                 // e.g. "sha256=" for GitHub
    services.GetRequiredService<IConfiguration>()["Shop:WebhookSecret"]);
```

The header must equal `{Prefix}{encoded HMAC of the exact body}`, compared in constant time. This
scheme carries no timestamp, so a captured delivery verifies for as long as the secret is unchanged;
set `DeliveryId` so a replay is not handled twice.

### 4.2 Stripe: `t=…,v1=…` with a tolerance

`StripeSignatureStrategy(secret, tolerance: null, timeProvider: null)` checks
`Stripe-Signature: t={unix seconds},v1={hex}[,v1=…]`, where each `v1` is HMAC-SHA256 over
`{t}.{body}`. Any `v1` may match (Stripe sends several while a secret is rolled). The signature is
checked first, then `t` must be within the tolerance of now in either direction (default five
minutes, Stripe's own): a genuine delivery replayed later is refused as `OutsideTolerance`.
`StripeSignatureStrategy.ComputeHeader(secret, timestamp, body)` produces a header, for tests and an
in-process test provider.

### 4.3 Fetch by id (Mollie)

Mollie signs nothing. It posts `id=tr_…` (form-encoded) and expects you to fetch the payment.

```csharp
spark.AddWebhookSource("mollie", source =>
{
    source.Verification = _ => new FetchByIdStrategy(); // field "id", 1–128 of [A-Za-z0-9_-]
    source.UseHandler<MollieWebhookHandler>();
    // No DeliveryId: see below.
});
```

The delivery is accepted as a **hint**: `WebhookDelivery.PayloadVerified` is `false` and
`ResourceId` holds the id. The handler fetches the resource from the provider, with the right
merchant's credentials, and acts only on what the provider returns. Anyone can POST an id, but all a
forged one achieves is a lookup of a real resource's current state, which a genuine delivery causes
too. An id that does not match the pattern is refused, so arbitrary text never reaches the
provider's API as a path segment.

**Do not deduplicate a fetch-by-id source on the id.** Mollie sends the same payment id for every
status change; a key on it would drop every update after the first. Leave `DeliveryId` unset: each
delivery is enqueued, and the fetch makes a repeat harmless.

---

## 5. Deduplication and handling

A queued source (the default) enqueues the delivery on `spark-webhooks` **before** answering `200`.
The order is load-bearing: a provider that has its `2xx` does not send again, so acknowledging first
would lose the delivery if the enqueue then failed. The opposite failure, enqueued but the `200`
lost, earns a redelivery, and that is what deduplication absorbs: with a delivery id, the message is
published with `BroadcastOnceAsync(delivery, "{source}:{deliveryId}")`, whose id is derived from the
key, so the second publish is a no-op whichever host receives it.

This deduplicates *enqueueing*, within Messaging's retention window. A provider can also send the
same event under a new delivery id. Handlers stay idempotent.

The handler runs from the queue with Messaging's retries and backoff. Throw `NonRetryableException`
for a delivery that can never succeed. A delivery for a source that is no longer registered is
dead-lettered at once.

`UseHandler<T>(WebhookHandling.Inline)` runs the handler inside the request instead, after
verification: `200` when it returns, `500` when it throws. Use it only for a handler that does its
own durable enqueue and deduplication. An app whose sources are all inline gets no
`spark-webhooks` queue at all.

---

## 6. A package with its own route: GitHub

`MintPlayer.Spark.Webhooks.GitHub` registers the source `github` with `SharedRoute = false` and maps
its own generator endpoint at `GitHubWebhooksOptions.WebhookPath` (default `/api/github/webhooks`,
the URL every GitHub App is configured with). Its endpoint calls the same `IWebhookReceiver`:
`ReadAsync(source, WebhookRoute.Own, …)` in its binding hook and `ReceiveAsync` in its handler, so
the checks are identical.

- Verification: `GitHubWebhookSignature`, an `HmacSignatureStrategy` for `X-Hub-Signature-256`
  (`sha256=` + lower-case hex), keyed with `WebhookSecret` read per delivery. Empty: `401`.
- JSON only, 25 MB cap (GitHub's own).
- Inline handling: `SparkWebhookEventProcessor` forwards development-app deliveries to the dev tunnel,
  and otherwise broadcasts its catch-all envelope once per `X-GitHub-Delivery` and the typed
  envelopes. See the [package README](../libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md).

Another package does the same: register the source with `SharedRoute = false`, and map an
`IPostEndpoint<WebhookRequest>` whose `BindRequestAsync` calls `receiver.ReadAsync(name,
WebhookRoute.Own, context.Request.Headers, context.Request.Body, context.Request.ContentLength,
context.RequestAborted)`. The shared route answers `404` for such a source, and its own route
answers `404` for every other.

---

## 7. Testing a handler

- Strategies are plain classes: build a `WebhookRequest(headers, bytes)` and call `Verify`.
- `StripeSignatureStrategy.ComputeHeader` signs a test body; an `HmacSignatureStrategy` header is
  `Prefix + hex/base64(HMAC(secret, body))`.
- Use **real recorded provider payloads** as fixtures, never hand-written ones: a hand-written body
  is shaped by what you think the provider sends.
- Tests in this repository: `tests/MintPlayer.Spark.Tests/Webhooks/` (`WebhookSignatureStrategyTests`,
  `WebhookReceiverTests` against a real message bus, `ReceiveWebhookEndpointTests`,
  `GitHub/GitHubWebhookEndpointTests`).
