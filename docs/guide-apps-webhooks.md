# Apps and outbound webhooks

`MintPlayer.Spark.Apps` lets a Spark site send events to its users, the way GitHub does. Users and organizations
register HTTPS endpoints and pick the events they want. Each event goes out signed per
[Standard Webhooks](https://www.standardwebhooks.com/), with retries, a delivery log and redelivery. On top of that, a
site can offer **Apps**: a third party registers an App once, users install it on their account or organization, and
the App receives their events and calls the site's API with a short-lived installation token.

This guide is for the site developer. To receive these events, see the
[receiver guide](guide-webhook-receivers.md). The design and its decisions are in `docs/apps_webhooks_PRD.md`.

## 1. Turn it on

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMessaging();                 // required: events and deliveries are Messaging messages
    spark.AddOrganizations();             // optional: organizations own webhooks too
    spark.AddApps(apps => apps
        .AddEventsFromAssemblyContaining<Program>()
        .ConfigureOptions(o => o.CatalogTitle = "Shop webhooks"));
});
```

Spark.Apps references the Organizations package, so its security layer comes along. If the site does not call
`AddOrganizations()`, switch that layer off in `App_Data/security.json` (otherwise startup fails with SPARK048):

```json
"libraries": { "organizations": false }
```

Then run `--spark-synchronize-security` and `--spark-synchronize-model`, as for any library layer
([library layers](guide-library-layers.md)).

## 2. Declare events

An event is a record with `[WebhookEvent]`. Its name is what receivers subscribe to, and its record becomes the
envelope's `data`:

```csharp
[WebhookEvent("order.shipped", Permission = "orders:read", Description = "An order left the warehouse.")]
public sealed record OrderShipped(string OrderId, int Items, [property: Description("The carrier's name.")] string? Carrier);
```

- `AddEventsFromAssemblyContaining<T>()` registers every `[WebhookEvent]` in an assembly. `AddEvent<T>()` registers one.
- `Permission` matters for Apps only: an installation receives the event only when its granted permissions cover it
  (`orders:write` covers `orders:read`).
- `GET /spark/apps/webhooks/catalog` publishes every event as an OpenAPI 3.1 `webhooks` document, anonymously.
  `[Description]` texts and nested types travel with it, and receivers generate their types from it.

## 3. Publish

Publish in the session of the business write. The event is an outbox message stored by that `SaveChanges`, so it
goes out exactly when the write commits and never when it rolls back:

```csharp
[Inject] private readonly IWebhookPublisher webhooks;

session.Store(order);
await webhooks.PublishAsync(new OrderShipped(order.Id, order.Lines.Count, order.Carrier), WebhookOwner.Organization(order.OrganizationId), session);
await session.SaveChangesAsync();
```

An owner without any webhook or installation costs a single document load, and no message is stored.

**Code that may run twice for one event** (a message handler, a cron job) passes an idempotency key. The
`webhook-id` is then derived from the event type, the owner and the key, and a second run queues nothing:

```csharp
await webhooks.PublishAsync(evt, owner, idempotencyKey: build.Id, session);
```

### Entity events

For entities written through Spark (persistent objects), the package can raise lifecycle events itself:

```csharp
apps.AddEntityEvents<Car>(EntityEvents.All, permission: "cars:read", owner: car => WebhookOwner.Organization(car.OrganizationId));
```

This registers `car.created`, `car.updated` and `car.deleted`. **The payload is the id only** (plus the changed
attribute names on an update, and `soft` on a delete). The receiver reads the row through the API with its token, so it
sees exactly what its rights allow. The events are published after the commit, at least once, and deduplicated by their
`webhook-id`. A row whose owner function returns null raises nothing. Writes through a raw RavenDB session raise
nothing either; publish those with `IWebhookPublisher`.

## 4. Owners

A webhook belongs to an owner, a `WebhookOwner(kind, key)`. Two kinds are built in:

| Kind | Key | Managed by |
|---|---|---|
| `user` (`WebhookOwner.User(id)`) | the user id | that user |
| `organization` (`WebhookOwner.Organization(id)`) | the organization id | its admins and owners (needs `AddOrganizations()`) |

A site adds its own kinds with an `IWebhookOwnerProvider`:

```csharp
apps.AddOwnerProvider<AccountWebhookOwnerProvider>();

internal sealed partial class AccountWebhookOwnerProvider : IWebhookOwnerProvider
{
    public string Kind => "account";
    public Task<bool> CanManageAsync(string key, string userId, CancellationToken ct) => ...;
    public Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken ct) => ...;
}
```

`CanManageAsync` gates every management call, so fail closed: answer false for anything you cannot prove. CodeCoverage
is the worked example (`apps/CodeCoverage/CodeCoverage/Webhooks/CoverageWebhooks.cs`). Its owners are forge accounts,
and the forge decides who manages them.

## 5. Management: endpoints and pages

Everything is under `/spark/apps`, for signed-in users, with CSRF protection like the rest of Spark. Secrets are shown
once, in the answer that creates or rotates them, and never listed.

| | |
|---|---|
| `GET webhooks/owners` | the owners the caller manages |
| `GET webhooks/endpoints?ownerKind=&ownerKey=` | an owner's webhooks |
| `POST webhooks/endpoints` | register one (URL, description, events or `*`); the answer holds its `whsec_` secret |
| `POST webhooks/endpoints/update`, `/delete`, `/enable`, `/ping`, `/rotate-secret` | manage one; after a rotation the old secret keeps signing for 24 hours |
| `GET webhooks/deliveries?endpointId=&after=` | the delivery log, newest first, 50 at a time |
| `POST webhooks/deliveries/redeliver` | send a delivery again, with the same `webhook-id` |

The Angular pages come from `@mintplayer/ng-spark/apps`. Add `withSparkApps()` to `sparkAuthRoutes(...)`. It adds
`account/webhooks` (with each webhook's deliveries), `account/apps`, `account/installations` and the install page
`apps/:slug/install`. `withSparkApps({ apps: false })` mounts only the webhook pages. SparkId is the reference host
(`apps/SparkId/SparkId/ClientApp/src/app/app.routes.ts`).

## 6. Delivery

- Each delivery is a Messaging message on `spark-apps-webhooks`: 8 attempts over about 32 hours, 16 at once, 15 seconds
  per receiver. Every attempt carries the same `webhook-id`.
- `410 Gone` disables the endpoint. `429` or `503` with `Retry-After` waits as asked, at most an hour, without spending
  an attempt.
- An endpoint that has failed for 5 days (`AutoDisableAfter`) is disabled. Implement `IWebhookEndpointDisabledHandler`
  to tell its owner. Three failures within a minute (`BreakerThreshold`, `BreakerWindow`) open a breaker for that
  endpoint.
- Payloads over 256 KB (`MaxPayloadBytes`) go out thin: `data` becomes `{ "truncated": true, "size": n }`.
- The log keeps each delivery for 30 days (`DeliveryRetention`). Secrets are stored with ASP.NET Core Data Protection.
- Deliveries leave through Spark's safe HTTP client (`AddSafeHttpClient`, configurable under `Spark:Apps:Http`), which
  refuses internal addresses. It follows no redirects, because a redirect would carry the signed body elsewhere, and
  reads at most 64 KB of an answer. `AllowInsecureUrls` allows `http://` endpoint URLs, for development only.
- **Ordering:** `Ordering = PerDestination` keeps one delivery in flight per endpoint. `OnExhausted = Halt` then stops
  the chain at a delivery that ran out of attempts, until the owner re-enables the endpoint, which retries that one
  first. Pings and redeliveries skip the chain.
- `apps.AddSigner(new Ed25519WebhookSigner(key), publicKey)` (MintPlayer.Webhooks.Ed25519) adds an asymmetric `v2a`
  signature next to the `v1` HMAC.

## 7. Apps and installations

```csharp
spark.AddIdentityProvider(...);           // Apps are identity-provider clients
spark.AddApps(apps => apps
    .AddEventsFromAssemblyContaining<Program>()
    .EnableApps()                        // startup fails without AddIdentityProvider()
    .MapPermission("orders:write", "Order managers"));
```

1. **A developer registers an App** (`account/apps`). They give it a slug, the permissions it needs
   (`resource:read|write`), the events it wants (each covered by a permission), a webhook URL and an optional setup URL.
   Behind it is a confidential `client_credentials` client of the identity provider, authenticated with
   `private_key_jwt`. **Generate a key** returns an RS256 private key (PEM) once; an App holds at most 5.
2. **A user installs it** (`apps/:slug/install`) on an owner they manage, granting what the App asks for. A private App
   installs only on its own owner. The user is then sent to the setup URL with `installation_id` and
   `setup_action=install`.
3. **The App receives events** of that owner on its webhook when the installation is not suspended, it subscribes to
   the event, and the **granted** permissions cover it. The payload gains `installation.id`. Installation lifecycle
   events (`installation.created`, `.deleted`, `.suspend`, `.unsuspend`, `.new_permissions_accepted`) always reach it.
4. **The App calls the API** with an installation token (60 minutes) from the identity provider's
   `/connect/token`: `grant_type=client_credentials`, `installation_id=in_…`, and a `client_assertion` signed with
   its key. The token carries `installation_id`, and `MapPermission(permission, groups…)` turns a granted permission
   into Spark group membership for that token, so `security.json` rights apply to it as to a user.

Suspending an installation stops its events and kills its tokens, and resuming does not revive old tokens. Uninstalling
revokes its grant. An App that asks for fewer permissions narrows every installation at once; asking for more takes
effect per installation when its owner accepts.

## 8. Testing

`tests/MintPlayer.Spark.Tests/Apps/AppsTestHost.cs` is a host with a fake receiver behind the package's delivery
client. `WebhookDeliveryTests`, `AppInstallationTests` and `EntityWebhookEventTests` show the patterns: publish, then
wait until the deliveries settle, with a failure bound rather than a fixed delay.
