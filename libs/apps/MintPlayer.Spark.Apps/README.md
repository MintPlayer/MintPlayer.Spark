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

// In the scope's session, the one the business write saves (#536 D50): the event leaves exactly when SaveChanges commits.
await webhooks.PublishAsync(new ReportCompleted(report.Id, report.LineRate), WebhookOwner.Organization(orgId));
await session.SaveChangesAsync();
```

Guide: [docs/guide-apps-webhooks.md](../../../docs/guide-apps-webhooks.md). Receiving them:
[docs/guide-webhook-receivers.md](../../../docs/guide-webhook-receivers.md).

- **Run twice, send once:** `PublishAsync(data, owner, idempotencyKey)` derives the `webhook-id` from the key,
  for message handlers and cron jobs.
- **Entity events:** `apps.AddEntityEvents<Car>(EntityEvents.All, "cars:read", car => WebhookOwner.Organization(car.OrganizationId))`
  raises `car.created/updated/deleted` after each Spark write commits, with the id only.
- **Owners:** users and organizations are built in; `apps.AddOwnerProvider<T>()` adds a kind (`IWebhookOwnerProvider`).
- **Apps:** `apps.EnableApps()` (needs `AddIdentityProvider()`) lets developers register Apps that users install on an
  owner. An App receives that owner's events within the granted permissions, and calls the API with an installation
  token (`client_credentials` + `installation_id`, `private_key_jwt`). `apps.MapPermission(permission, groups…)` maps a
  granted permission to Spark groups for that token.
- **Pages:** `withSparkApps()` from `@mintplayer/ng-spark/apps`, in `sparkAuthRoutes(...)`.
- Spark.Apps brings the Organizations security layer: without `AddOrganizations()`, set
  `"libraries": { "organizations": false }` in `security.json`.

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
| `GET apps?ownerKind=&ownerKey=`, `GET apps/app?slug=`, `POST apps`, `apps/update`, `apps/delete` | an owner's Apps (with `EnableApps()`) |
| `POST apps/keys/generate`, `apps/keys/delete` | an App's private keys; a new one is shown once |
| `GET installations/new?slug=`, `POST installations`, `installations/accept`, `/suspend`, `/unsuspend`, `/delete` | install an App and manage the installation |
| `GET installations?targetKind=&targetKey=`, `GET apps/installations?slug=` | installations by target, and by App |

## Delivery

- Each delivery is a Messaging message on `spark-apps-webhooks`: 8 attempts over about 32 hours, 16 at once, 15 s per
  receiver, no redirects. `410 Gone` disables the endpoint; `429`/`503` with `Retry-After` waits as asked (at most an hour).
- An endpoint failing for 5 days is disabled; implement `IWebhookEndpointDisabledHandler` to tell its owner.
- `Spark:Apps:Ordering = PerDestination` keeps one delivery in flight per endpoint; `OnExhausted = Halt` stops the
  chain on a delivery that ran out of attempts until the endpoint is re-enabled.
- Payloads over 256 KB go out thin: `data` is `{ "truncated": true, "size": n }`.
- The log keeps each payload 30 days. Secrets are stored with ASP.NET Core Data Protection.
- `apps.AddSigner(new Ed25519WebhookSigner(key), publicKey)` (MintPlayer.Webhooks.Ed25519) adds a `v2a` signature.

Receivers verify with [MintPlayer.Webhooks.Receiver](../../webhooks/MintPlayer.Webhooks.Receiver/README.md) or any
Standard Webhooks library.
