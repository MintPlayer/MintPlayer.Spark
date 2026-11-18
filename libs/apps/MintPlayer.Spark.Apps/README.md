# MintPlayer.Spark.Apps

Outbound webhooks for a Spark site, the way GitHub offers them: the site's users and organizations register HTTPS
endpoints, pick events, and receive them signed ([Standard Webhooks](https://www.standardwebhooks.com/)), with
retries, a delivery log, redelivery and a typed event catalog.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMessaging();
    spark.AddOrganizations();                       // optional: organizations own webhooks too
    spark.AddApps(apps => apps.AddEventsFromAssemblyContaining<Program>());
});

[WebhookEvent("report.completed", Permission = "reports:read", Description = "A report finished processing.")]
public sealed record ReportCompleted(string ReportId, decimal LineRate);

// In the session of the business write: the event leaves exactly when SaveChanges commits.
await webhooks.PublishAsync(new ReportCompleted(report.Id, report.LineRate), WebhookOwner.Organization(orgId), session);
await session.SaveChangesAsync();
```

## Endpoints (`/spark/apps`)

| | |
|---|---|
| `GET webhooks/catalog` | OpenAPI 3.1 `webhooks` document of every event (anonymous; codegen reads it) |
| `GET webhooks/owners` | the accounts and organizations the caller manages |
| `GET webhooks/endpoints?ownerKind=&ownerKey=` | an owner's webhooks |
| `POST webhooks/endpoints` | register one; the answer holds its `whsec_` secret, shown once |
| `POST webhooks/endpoints/update`, `/delete`, `/rotate-secret`, `/enable`, `/ping` | manage one |
| `GET webhooks/deliveries?endpointId=&after=` | the delivery log, newest first |
| `POST webhooks/deliveries/redeliver` | send a delivery again, with the same `webhook-id` |

## Delivery

- Each delivery is a Messaging message on `spark-apps-webhooks`: 8 attempts over about 32 hours, 16 at once, 20 s per
  receiver. `410 Gone` disables the endpoint; `429`/`503` with `Retry-After` waits as asked (at most an hour).
- An endpoint failing for 5 days is disabled; implement `IWebhookEndpointDisabledHandler` to tell its owner.
- `Spark:Apps:Ordering = PerDestination` keeps one delivery in flight per endpoint; `OnExhausted = Halt` stops the
  chain on a delivery that ran out of attempts until the endpoint is re-enabled.
- Payloads over 256 KB go out thin: `data` is `{ "truncated": true, "size": n }`.
- The log keeps each payload 30 days. Secrets are stored with ASP.NET Core Data Protection.
- `apps.AddSigner(new Ed25519WebhookSigner(key), publicKey)` (MintPlayer.Webhooks.Ed25519) adds a `v2a` signature.

Receivers verify with [MintPlayer.Webhooks.Receiver](../../webhooks/MintPlayer.Webhooks.Receiver/README.md) or any
Standard Webhooks library.
