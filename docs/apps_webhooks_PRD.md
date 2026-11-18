# Spark Apps & outbound webhooks — PRD and plan (#691)

Status: **planned**, nothing built. Spikes S1–S8 run first, and their results are written back into this file.
Issue: [#691](https://github.com/MintPlayer/MintPlayer.Spark/issues/691), "Let website developers provide an Apps feature which lets
their website send webhooks to downstream users".
Written 2026-11-18 at master `fc95abe38`, after a three-agent investigation and three rounds of grilling with the owner.

## 1. Goal

A developer of a Spark site should be able to offer **its** users what GitHub offers its users:

- **Webhooks.** A user or organization registers an HTTPS endpoint and picks event types. The site delivers signed events, retries
  failures, keeps a delivery log and supports redelivery.
- **Apps.** A user registers an App: a webhook URL, requested permissions, subscribed events and a key pair. The App is installed on a
  user account or an organization. Each installation receives the events its permissions cover. With a short-lived installation token,
  the App calls the site's API back.
- **Receivers.** The downstream consumer gets typed SDKs, Octokit.Webhooks-style, for .NET, TypeScript/Node and Spark-to-Spark.

Design target: **as little code as possible for the site developer** (one builder call, one attribute per event, one publish call) and
**everything overridable** (seams for authorization, payload shaping, retry policy, transport and the signer).

### What the site developer writes (target shape, to be validated by S1/S3)

```csharp
spark.AddIdentityProvider(...)
     .AddApps(apps => apps
         .AddEventsFromAssemblyContaining<Program>()                    // [WebhookEvent] records
         .AddEntityEvents<Report>(EntityEvents.Created | EntityEvents.Deleted,
                                  permission: "reports:read"));          // opt-in lifecycle events

[WebhookEvent("report.completed", Permission = "reports:read")]
public sealed record ReportCompleted(string ReportId, string Repository, decimal LineRate);

// inside a handler/action, in the same Raven session as the business write (outbox: atomic)
await webhooks.PublishAsync(new ReportCompleted(...), WebhookOwner.Organization(orgId), session);
```

### What the downstream consumer writes (.NET)

```csharp
builder.Services.AddWebhookReceiver<CoverageWebhooks>(o => o.Secrets = [config["Coverage:WebhookSecret"]]);
app.MapWebhookReceiver<CoverageWebhooks>("/webhooks/coverage");

// CoverageWebhooksProcessor is generated from the site's catalog (`spark webhooks generate`)
public sealed class CoverageWebhooks : CoverageWebhooksProcessor
{
    protected override Task OnReportCompletedAsync(ReportCompleted e, WebhookContext ctx, CancellationToken ct) => ...;
}
```

## 2. Investigation: what Spark already has

| Need | Existing block | Verdict |
|---|---|---|
| Durable delivery, retry, backoff, dead-letter | `libs/messaging`: `IMessageBus.BroadcastAsync(msg, BroadcastOptions)` (dedup key, delay, MaxAttempts, queue), `IMessageOutbox` (atomic in the caller's session), `IRecipient<T>`, `RetryAfterException`, `NonRetryableException`, per-queue `SparkQueueOptions` (backoff, MaxAttempts, MaxConcurrency, rate limit). Defaults: 5 attempts, 5s/30s/2m/10m/1h | **The delivery engine.** Message retention (7 days) is too short for a delivery log, so the log is its own entity |
| SSRF-safe outbound POST | `libs/spark/MintPlayer.Spark.SafeHttp` (`AddSafeHttpClient`, `SparkBuilderSafeHttpExtensions.cs`). It allows global unicast addresses only, pins the checked IP (no DNS rebinding), re-checks each redirect, allows ports 80/443, caps size and time, throws `SafeHttpBlockedException`. Its README names "a webhook target" as a use case | **Use as-is.** Redirects must be refused rather than followed (S6) |
| App identity, app JWT, tokens | `libs/identity_provider`: `Models/OidcApplication.cs` (hashed secrets, JWKS, `private_key_jwt` in `Services/OidcClientAuthenticator.cs`), `Endpoints/Token.cs` (`client_credentials`, token exchange), `Endpoints/Register.cs` (RFC 7591). Scopes are `<resource>.<scope>`. Resource server: `AddSparkResourceServer`, `[RequireScope]` | **An App is an `OidcApplication`.** Gap: `Models/OidcGrant.cs` has `Subject` and `GrantedScopes` but no organization, so an installation needs an org-scoped grant (S2) |
| Installation target | `libs/organizations`: `Organization`, `OrganizationMembership`, `OrganizationRole` (Viewer/Member/Admin/Owner), `IOrganizationOwned.cs`, `OrganizationRowPolicy` | An `AppInstallation : IOrganizationOwned` gets row security for free when the package is present |
| Permissions | security.json rights `{verb}/{Type}` with library slots bound by the app; `[assembly: SparkReservedActions]`; `IGroupMembershipProvider.cs` | App permissions map to resource-server scopes. A group-membership provider can make existing security.json rights apply to installation tokens (S2) |
| Receiving + signature check | `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub`: `Services/SignatureService.cs` (constant-time HMAC, fails closed on an empty secret, but internal and GitHub's `sha256=` format), `Services/SparkWebhookEventProcessor.cs` (verify → `BroadcastOnceAsync` on the delivery id → `IRecipient<T>`) | Tied to Octokit, so not reusable as an assembly. **Its pattern is the template** for the Spark-to-Spark receiver |
| Event / subscriber model | `libs/notifications` (`ISparkNotifier`) is a direct "notify these users" call, not an event bus | **Built new.** Copy the outbox delivery pattern of `Messaging/WebPushDelivery.cs` |
| Feature package shape | `libs/notifications`: a single project, `<SparkLibraryAlias>`, `App_Data/{Model,security.json,programUnits.json,translations.json}`, an idempotent `AddNotifications(this ISparkBuilder)` with `[SparkRequiresStore]`, and the Angular entry point `libs/node_packages/ng-spark/notifications`. AllFeatures references core packages only | Same layout. Stays opt-in, outside AllFeatures |

External prior art (researched 2026-11-18):

- **Standard Webhooks** ([spec](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md)).
  - Headers: `webhook-id` (stable across retries), `webhook-timestamp` (per attempt) and `webhook-signature` (a space-separated list).
  - `v1` is HMAC-SHA256 over `id.timestamp.body` with a `whsec_` secret of 24–64 bytes.
  - The current asymmetric scheme is **`v2a`** (ed25519 over `endpoint_id.msg_id.timestamp.payload`, `whsk_`/`whpk_` keys). **`v1a` is deprecated.**
  - Secret rotation sends both signatures. Only 2xx counts as success; 3xx is a failure; 410 disables the endpoint; honour
    `retry-after` on 429/502/504; time out after 15–30 s.
  - NuGet `StandardWebhooks` exists (exact package id to be checked).
- **GitHub Apps**:
  - The manifest flow returns `id`, `pem` and `webhook_secret`, and must finish within 1 h.
  - The installation token lives 1 h, can be narrowed by permissions/repos, and never exceeds the app's own permissions.
  - Delivery headers: `X-GitHub-Event`, `X-GitHub-Delivery` (kept on redelivery) and `X-GitHub-Hook-ID`.
  - Response timeout 10 s, payload cap 25 MB. The delivery log and manual redelivery cover 3 days, with no automatic redelivery.
- **Svix**:
  - Retries: immediately, then 5s, 5m, 30m, 2h, 5h, 10h, 10h, with a 30 s timeout.
  - An endpoint is auto-disabled after 5 days of nothing but failures.
  - Emits `message.attempt.exhausted` and an endpoint-disabled event.
- **Stripe**: retries for 3 days; ordering is not guaranteed, so receivers dedupe on the event id. A rolled secret stays valid for up to 7 days. Stripe now recommends thin events.
- **Shopify**: 5 s timeout and 8 retries in 4 h. A subscription is deleted after 8 consecutive failures.
- **Octokit.Webhooks.AspNetCore**: a `WebhookEventProcessor` with virtual `ProcessXxxWebhookAsync` methods, plus
  `MapGitHubWebhooks(path, secret)`. That shape is the template for our receivers.

## 3. Decisions

Unless marked otherwise, each decision is the owner's answer in the grilling of 2026-11-18. The evidence column gives the reason.

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | **The first release is the full Apps platform:** apps, installations, permission-gated events and app credentials to call the API. It is built in layers, so direct webhooks work without Apps | Owner. Layering keeps the webhook-only use case cheap |
| D2 | **Wire format is Standard Webhooks.** `v1` HMAC is always on; `v2a` ed25519 is optional per site (S4). There is no GitHub-style format | Owner. It gives replay protection, and receivers in every language can use existing libraries. `v1a` is deprecated (spec) |
| D3 | **Three receivers:** plain .NET, TypeScript/Node, and Spark-to-Spark (durable through Messaging) | Owner |
| D4 | **CodeCoverage dogfoods it** (production, coverage.mintplayer.com), behind a setting that is off by default until M9 | Owner |
| D5 | **An App is an `OidcApplication`.** The App signs a `private_key_jwt` and exchanges it for a short-lived installation token, which needs a new org-scoped grant. Sites that offer Apps must run the IdentityProvider package; direct webhooks do not need it | Owner. `OidcClientAuthenticator.cs` already accepts `private_key_jwt`, and `Token.cs` already does `client_credentials` and token exchange. IdP PRD D1: "client_credentials is *the* machine credential" |
| D6 | **Events are declared two ways.** Explicit `[WebhookEvent]` records are raised through `IWebhookPublisher` (atomic outbox in the session). Opt-in entity lifecycle events cover PersistentObject create/update/delete. A build step produces the event catalog and JSON Schemas | Owner |
| D7 | **Payloads are full typed snapshots, filtered by permission.** An event goes only to installations whose granted permissions cover it | Owner. Receivers rarely need to call back. Thin events stay possible as a per-event shaping hook (§4.4) |
| D8 | **Receivers are typed by codegen from the catalog.** The site serves OpenAPI 3.1 `webhooks` plus JSON Schema, and `spark webhooks generate` (MintPlayer.Spark.Cli) emits typed processors. The generic core does verification and dispatch | Owner |
| D9 | **Owners are users and organizations, and direct hooks exist.** Apps install on a user account, or on an organization when Organizations is registered. Owners can also register plain webhook endpoints without an App | Owner. Organizations stays an optional dependency |
| D10 | **`@mintplayer/webhooks-receiver` uses independent semver**, starting at `1.0.0`. This is a **deliberate exception** to the CLAUDE.md rule "npm major = Angular major" | Owner, 2026-11-18. The package has no Angular dependency. M10 adds the exception to CLAUDE.md and to the versioning engine (which reads `@angular/core`, so S7) |
| D11 | **Apps have a public/private flag.** A private App installs only on its owner's account and orgs; a public App installs through its install URL. There is no app directory UI in v1 | Owner |
| D12 | **Package names:** server `MintPlayer.Spark.Apps`; receivers `MintPlayer.Webhooks.Receiver` (plain .NET, no Spark dependency), `MintPlayer.Spark.Webhooks.Receiver` (Spark durable adapter) and `@mintplayer/webhooks-receiver` (npm). The orphaned `libs/webhooks/MintPlayer.Spark.Webhooks` folder (bin/obj only, nothing tracked) is deleted | Owner. The empty folder was confirmed by the investigation |
| D13 | **Everything lands in one pull request**, including the CodeCoverage dogfood, the CLAUDE.md exception and the docs | Global rule 8 |
| D14 | **Delivery runs on Messaging, one message per (event, destination).** Each destination retries independently, and the event fan-out is itself a message, so `PublishAsync` stays one outbox write | Proposed. It reuses backoff and dead-letter (`SparkQueueOptions`). S3 confirms that one failing endpoint cannot starve the others |
| D15 | **Signing secrets are stored encrypted, not hashed.** HMAC signing needs the plaintext. App client secrets and keys stay with the IdP, hashed or public-key | Proposed. HMAC cannot be computed from a hash. Mechanism: S5 |

## 4. Design

### 4.1 Packages

```
libs/apps/MintPlayer.Spark.Apps                    server: events, catalog, endpoints, apps, installations, delivery
libs/webhooks/MintPlayer.Webhooks.Receiver         plain .NET: Standard Webhooks sign+verify, typed dispatch base, MapWebhookReceiver
libs/webhooks/MintPlayer.Spark.Webhooks.Receiver   Spark adapter: verify → BroadcastOnce(webhook-id) → IRecipient<T>
libs/node_packages/webhooks-receiver               @mintplayer/webhooks-receiver (Node/edge: verify + typed dispatch)
libs/node_packages/ng-spark/apps                   @mintplayer/ng-spark/apps entry point (developer settings, install consent, deliveries)
libs/cli/MintPlayer.Spark.Cli(.Commands)           `spark webhooks generate --catalog <url|file> --lang cs|ts`
```

The **signer and verifier live in `MintPlayer.Webhooks.Receiver`**, and the server references them. That way sending and checking cannot
drift apart, and interop tests against the official `StandardWebhooks` library prove both.

### 4.2 Domain (RavenDB, all ids natural or UUID; no string splicing into RQL)

- `WebhookEventType`: the catalog row, built from `[WebhookEvent]` and the entity-event registrations. Fields: name (dot-delimited
  `[a-zA-Z0-9_.]`, validated), required permission, JSON Schema, description (from the `///` summary), version.
- `App`: owner (user or org), slug, name, description, homepage, `OidcApplicationId`, webhook URL and active flag, encrypted
  signing secret(s), requested permissions (`resource:read|write`), subscribed event types, public flag, setup/callback URLs.
- `AppInstallation : IOrganizationOwned` (or user-owned): App, target, **granted** permissions (a subset of requested, never more), the
  events in effect, suspended flag, installed-by and when.
- `WebhookEndpoint`: a direct hook, owned by a user or org. URL, encrypted secret(s), event types, active/disabled with a reason,
  and a failure streak start.
- `WebhookDelivery`: one per (event, destination). `webhook-id`, event type, destination (App+installation or endpoint), payload
  (or a reference to it), attempts (time, status, duration, truncated response excerpt, error), outcome, and a redelivery-of link. Its
  own retention defaults to 30 days and is configurable. Owners can read their own; App owners can read their App's.

### 4.3 Flows

1. **Publish.** `IWebhookPublisher.PublishAsync(evt, owner, session)` writes one `WebhookEventRaised` message through `IMessageOutbox`,
   in the caller's session, so the event exists only if the business write commits.
2. **Fan-out** (an `IRecipient<WebhookEventRaised>`). It resolves:
   - active installations on the owner whose App subscribes to the type and whose **granted** permissions cover it;
   - active direct endpoints on the owner subscribed to the type.

   For each destination it applies `IWebhookPayloadShaper` (default: full snapshot), creates a `WebhookDelivery`, and broadcasts a
   `DeliverWebhook` onto queue `spark-apps-webhooks` with the dedup key `deliveryId`.
3. **Deliver.** The handler signs the body (`v1` with every active secret during rotation, and `v2a` when enabled) and POSTs through
   SafeHttp, with no redirects and a timeout of 15 s by default.
   - 2xx: success.
   - 410: disable the destination; `NonRetryableException`.
   - 429/502/504 with `retry-after`: `RetryAfterException`.
   - Anything else: throw, so the queue backs off.

   Every attempt is recorded on the `WebhookDelivery`.
4. **Exhaustion and auto-disable.** When all attempts fail, the delivery is marked failed. A destination with nothing but failures for 5
   days (Svix rule, configurable) is disabled, and its owner is told: by mail when the Mail package is present, by notification when
   Notifications is.
5. **Redelivery and ping.** Redeliver re-broadcasts the same payload and the same `webhook-id` as a new delivery linked to the original. A
   `ping` event is sent when an endpoint or App webhook is created or its URL changes.
6. **Install.** `GET /apps/{slug}/installations/new` shows a consent screen listing the requested permissions and events, with a choice
   of target (the user, or an org where the user is Admin or Owner). The user confirms; the installation is created, the App gets an
   `installation.created` event, and the user is redirected to the setup URL.
   - A permission upgrade on the App needs the installer to accept it again before it takes effect (GitHub behaviour).
7. **App token.** The App POSTs to the IdP token endpoint with a `private_key_jwt` client assertion and an `installation_id` (a token-exchange
   or `client_credentials` extension; S2). It receives a short access token (1 h) whose scopes are the installation's granted permissions
   and which carries the installation, target org and user claims. Spark resource-server `[RequireScope]` checks apply, and a group
   provider maps the token to security.json groups (S2).

### 4.4 Flexibility seams

`IWebhookPayloadShaper` (thin/full or redaction per event and destination), `IWebhookDestinationResolver` (who receives an event),
`IWebhookTransport` (default SafeHttp; tests use an in-memory one), `IWebhookSigner` (Standard Webhooks; others pluggable),
`WebhookDeliveryOptions` (queue backoff, timeout, retention, auto-disable window, payload cap), `IAppPermissionCatalog` (declared
permissions and labels). Every default is registered with `TryAdd`.

### 4.5 Security checklist (reviewed in every milestone)

- No string splicing into RQL. Event type names and permission names are validated by an allow-pattern; values are parameters.
- SSRF: SafeHttp for every send, re-validated on each attempt. Redirects are refused. HTTPS only, except a development opt-in.
- Secrets are encrypted at rest, never logged, shown once on creation, and rotatable with overlap (old secret valid for 24 h by default).
- Receivers compare in constant time, reject timestamps more than 5 min off, and dedupe on `webhook-id`.
- Payloads never exceed what the installation's granted permissions allow. The fan-out re-checks against the current grant, not the one
  at publish time.
- Private-repo and private-org existence must not leak through install URLs or deliveries (see the existing memory on existence oracles).
- Every new endpoint updates `CsrfSurfaceTests` and the route snapshot. security.json or model JSON edits run the synchronizers.

## 5. Spikes (run before M1; each ends with a written verdict in this section)

| # | Question | Exit criterion | Timebox |
|---|---|---|---|
| S1 | **Catalog generation.** A Roslyn source generator over `[WebhookEvent]` records, or `System.Text.Json.Schema.JsonSchemaExporter` at runtime? Memory notes generator limits around records | A catalog endpoint that returns OpenAPI 3.1 `webhooks` for 2 sample events, including nested types, enums, DateTimeOffset and nullable fields | 0.5 d |
| S2 | **Installation token through the IdP.** Extend `client_credentials` with an `installation_id`, or use token exchange (RFC 8693) with the installation as the resource? How does an org-scoped grant sit beside `OidcGrant`? Can `IGroupMembershipProvider` map token scopes to security.json groups? | A test that issues a token, calls a `[RequireScope]` endpoint, and is refused once the installation is suspended or a scope is missing | 1 d |
| S3 | **Messaging fit.** 8 attempts with backoff up to 10 h; per-queue concurrency; does one endpoint that always times out starve the queue (head-of-line)? Is a per-destination rate limit or lane needed? | A measured test with one dead and one healthy endpoint, where the healthy deliveries stay within their SLA | 0.5 d |
| S4 | **Standard Webhooks interop.** Check the exact NuGet id of the official library. Is `v2a` ed25519 available in .NET 11 BCL, or does it need a package? | Our signer's output is verified by the official .NET and JS libraries, and theirs by our verifier, for `v1` and `v2a` | 0.5 d |
| S5 | **Encrypting secrets at rest.** Is there an existing Spark pattern (IdP signing keys? ASP.NET Data Protection persisted in Raven?) | One mechanism chosen, with key rotation and the multi-instance deployment answered | 0.5 d |
| S6 | **SafeHttp for webhooks.** Can redirects be turned off per client, and is response-body capping configurable? Can the delivery log get an attempt's timing and status without the body? | Config confirmed, or the smallest SafeHttp change identified | 0.25 d |
| S7 | **Versioning engine and the non-Angular npm package.** What does the engine do with an npm package that has no `@angular/core`? What change gives D10's independent semver? Also: `MintPlayer.Webhooks.Receiver` for consumers on net8/net10 — multi-target, and what major then (see O2)? | An engine rule plus `coverage.yml` config, proven with a dry run | 0.5 d |
| S8 | **CodeCoverage owners.** CodeCoverage owners are forge owners (GitHub orgs/users), not Spark `Organization`s. How do installations target them? | A mapping chosen (adapter `IWebhookOwnerResolver` or Organizations adoption), with authorization checked against `reference_coverage_authorization_inventory` | 0.5 d |

## 6. Milestones (one PR, TDD red → green per milestone, full test run once at the end)

| M | Scope | Done when |
|---|---|---|
| M0 | Spikes S1–S8; verdicts written into §5; decisions updated | This file revised; owner reviews the open questions |
| M1 | `MintPlayer.Webhooks.Receiver` core: Standard Webhooks sign and verify (`v1`, `v2a`, rotation, tolerance), `WebhookProcessor` dispatch base, `MapWebhookReceiver` | Interop tests green against the official libraries |
| M2 | `MintPlayer.Spark.Apps` skeleton: `AddApps`, `[WebhookEvent]`, catalog build (S1), `GET …/webhooks/catalog` (OpenAPI 3.1) | The catalog snapshot test holds 2 sample events |
| M3 | Direct webhooks and the delivery engine: `WebhookEndpoint`, `IWebhookPublisher` (outbox), fan-out, deliver (SafeHttp), `WebhookDelivery` log, retry and 410/429 rules, auto-disable, redeliver, ping, secret rotation, retention job (Cron) | Tests: success, retry, 410, retry-after, auto-disable, redeliver keeps `webhook-id`, no delivery when the business write rolls back |
| M4 | Apps: registration on `OidcApplication`, keys (`private_key_jwt`), App webhook, requested permissions and events, public/private flag | An App is created and receives a ping |
| M5 | Installations: consent flow, user and org targets, permission upgrade re-consent, installation token (S2), resource-server scopes, permission-gated fan-out, `installation.*` events | Test: an event outside the granted permissions is never delivered; a token is refused after suspension |
| M6 | Entity lifecycle events: `AddEntityEvents<T>`, which hooks PersistentObject create/update/delete and uses the read-permission filter | Create/update/delete each raise exactly one event; an unreadable entity is never sent |
| M7 | Angular `@mintplayer/ng-spark/apps`: developer settings (Apps, keys, webhook, permissions), direct webhooks, deliveries plus redeliver, install consent, installations list. ng-bootstrap components; generic po/query pages where possible | Pages work in the demo app; zoneless; no full page reloads |
| M8 | Receivers: `spark webhooks generate --lang cs\|ts`, `MintPlayer.Spark.Webhooks.Receiver` (verify, then `BroadcastOnceAsync(webhook-id)`, then `IRecipient<T>`), `@mintplayer/webhooks-receiver` | E2E: a Spark demo receives a generated, typed event from another Spark demo (`SparkAppTestHost`) |
| M9 | CodeCoverage dogfood: events such as `coverage.report.completed` and `coverage.threshold.failed`, owner mapping (S8), behind a setting that is off until enabled in production | CodeCoverage tests green; the setting is off by default |
| M10 | Docs and release: a site-developer guide and a receiver guide in `docs/`, the D10 exception in CLAUDE.md plus the engine config (S7), translations goldens, CSRF/route snapshots, package READMEs | Owner review |

## 7. Open questions (for the owner after M0)

- **O1 Ordering.** Does any event need per-resource ordering, or is "unordered, dedupe on id, carry `occurred_at`" enough (Stripe/Svix)?
  Proposed: unordered.
- **O2 .NET receiver target framework.** Downstream consumers may run net8 or net10. The NuGet major is locked to the target framework
  (CLAUDE.md). Options: target net11 only (major 11), multi-target (which major?), or a second exception like D10. Not yet decided.
- **O3 Payload cap.** Proposed: 256 KB per delivery. A bigger payload is sent thin automatically (ids plus a fetch URL).
- **O4 Delivery log content.** Store the full payload for redelivery (30 days), or recompute it from the event? Proposed: store it,
  with a size cap. Response bodies are kept as a 2 KB excerpt.
- **O5 App manifest flow** (create an App from a JSON manifest, GitHub style). Proposed: out of v1.

## 8. Non-goals (v1)

An App directory or marketplace; billing; mTLS to receivers; CloudEvents envelopes; GitHub-style signature headers; webhooks for
anonymous users; event filtering beyond the type (for example "only repo X"), which is noted as a future seam on the destination
resolver.
