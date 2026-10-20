# Commerce suite (#521): research

Date: 2026-10-17. Evidence for the commerce PRD, `docs/commerce_PRD.md`.

GitHub issue #521, "Ticket sale platform", grew into a modular commerce suite for the Spark framework: a webshop
and a ticketing platform built from the same libraries, with the behaviour of each chosen by configuration. This
file collects the evidence the PRD cites. It records what Spark already provides (with file:line), how a Spark
plugin is packaged and consumed, which RavenDB primitives give a no-oversell hold on the production
Community-licence node, and how payment providers' time-to-pay and capture rules interact with holds. It
summarises what comparable commerce, ticketing and waiting-room products model, how bots and scalpers attack
limited-availability sales and which measures hold up, and the legal constraints. Finally it records how a
persistent-object name acts as identity across library layers (and where library type names collide), and inventories
the approval-like flows that already exist and could move onto a generic approvals library.

**The PRD's decision log is authoritative for decisions; this file holds evidence only.** Where a passage below
recommends something, it records the researcher's reading of the evidence on this date; the PRD may have decided
otherwise.

**Conventions**
- Repository paths are relative to the repository root, read on 2026-10-17 at master `e39b16de`. Abbreviations:
  `S/` = `libs/spark/MintPlayer.Spark/` · `A/` = `libs/spark/MintPlayer.Spark.Abstractions/` ·
  `NS/` = `libs/node_packages/ng-spark/` · `Auth/` = `libs/authorization/MintPlayer.Spark.Authorization/` ·
  `IdP/` = `libs/identity_provider/MintPlayer.Spark.IdentityProvider/` ·
  `Org/` = `libs/organizations/MintPlayer.Spark.Organizations/` · `Mod/` = `libs/moderation/MintPlayer.Spark.Moderation/` ·
  `Msg/` = `libs/messaging/MintPlayer.Spark.Messaging/` · `MsgA/` = `libs/messaging/MintPlayer.Spark.Messaging.Abstractions/` ·
  `Notif/` = `libs/notifications/MintPlayer.Spark.Notifications/` · `Gen/` = `libs/source_generators/MintPlayer.Spark.SourceGenerators/`.
- **✔** marks a line re-read against the checkout, in the approvals inventory or while compiling this file. Other
  line numbers come from read-only research sweeps.
- **[unverified]** marks a claim the research flagged as not confirmed by a fetched page, or as needing a check
  before it is relied on.
- **(inference)** marks reasoning, not a documented or measured fact.
- Vendor pages are used for mechanisms more than for effectiveness numbers. Nothing here is legal or tax advice.

---

## 1. What Spark gives us today

### 1.1 Capability table

Verdicts: **Reusable** as is · **Extend**: usable after a framework addition · **Gap**: absent.

| Need | Spark feature | Evidence file:line | Verdict |
|---|---|---|---|
| Live stock counters and seat maps | `/spark/live` socket: subscribe to a PO or a query (`"live": true`), re-read under the viewer's rights, push diffs/patches | `S/Endpoints/Live/LiveSocket.cs:23-75`, `S/Live/LiveHub.cs`, `S/Live/LiveConnection.cs:175-181`, `docs/guide-live-updates.md:11-32` | Reusable |
| Live views for a crowd | Pushes are dirty bits: each viewer's read re-runs (at most 1 per 500 ms, revalidated at least once a minute), so every change costs one read per viewer. Writes made outside Spark notify through `ISparkLiveNotifier.Changed/CollectionChanged` | `S/Live/SparkLiveOptions.cs:42,60`, `docs/guide-live-updates.md:31-32`, `A/Live/ISparkLiveNotifier.cs:19-27` | Extend: a public sale page or queue needs a broadcast / public-counter mode |
| Per-visitor queue position | App streams: a `Custom.*` live query that yields rows from code | `docs/guide-live-updates.md:90-121`, `S/Queries/LiveQueryArgs.cs:21-48` | Extend: `LiveQueryArgs` has `Parent` but no visitor, user or `HttpContext`; the stream needs the type's Query right (`docs/guide-live-updates.md:119`); it is query-shaped, not a message channel |
| Client socket handling | `SparkLiveService`: one shared socket, backoff, resubscribe | `NS/services/src/spark-live.service.ts:51-74,92,156,180` | Reusable |
| Many visitors behind one address | `MaxSocketsPerUser` = 5; anonymous handshakes accepted and keyed `"ip:" + RemoteIpAddress`; the 6th socket per IP is closed with 4429; `MaxSocketLifetime` 30 min; same-origin guard | `S/Live/SparkLiveOptions.cs:48`, `S/Endpoints/Live/LiveSocket.cs:89-96`, `S/Live/LiveHub.cs:266`, `tests/MintPlayer.Spark.Tests/Live/LiveSessionTests.cs:81-86`, `docs/guide-live-updates.md:62`, `S/SparkMiddleware.cs:420-440` | Extend: a CGNAT or office NAT shares one cap of 5 |
| Server time for a countdown | None: the `ping` heartbeat carries no server time | `docs/guide-live-updates.md:157` ✔, `S/Live/LiveConnection.cs:354` ✔ | Gap |
| Release a hold at time T | Messaging: durable, retry/backoff, dead letter, `DelayBroadcastAsync`, `ExpiresAtUtc`, `BroadcastOnceAsync`, `BroadcastOptions.DeduplicationKey` | `MsgA/IMessageBus.cs:10-45`, `MsgA/BroadcastOptions.cs:20,23,37` | Reusable; granularity is the 30 s `FallbackPollInterval` (`Msg/SparkMessagingOptions.cs:61`) |
| A hold and its expiry message in one commit | Transactional outbox: `IMessageOutbox.EnqueueAsync(session, …)` commits in the caller's `SaveChanges`; durable after-commit interceptors (`IAfterSaveCommitted`); delivery is at least once | `Msg/IMessageOutbox.cs:19`, `A/Interceptors/SparkCommittedInterceptors.cs:21`, `Msg/README.md:231-245,322-324` | Reusable; handlers must be idempotent |
| Backstop sweeper | Cron: exactly once per cluster (compare-exchange claim), 6-field schedules with seconds; UTC only, no catch-up, runs with full trust and no principal | `libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:243,270`, `libs/cron/MintPlayer.Spark.Cron/README.md:31,165-185,193-212` (`:31` ✔) | Reusable |
| React to document changes | Subscription workers, `RetryNumerator` | `libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/RetryNumerator.cs:68` | Reusable; the Community licence allows 3 subscriptions |
| RavenDB-native timers | `@expires` and `@refresh` | `S/Expiration/SparkExpiration.cs:9-32` ✔ (36 h floor) | Not usable for minute holds on Community (§3.1) |
| Concurrent edits | Optimistic concurrency: the PO etag is the change vector; 409 on update and delete; add-on endpoints answer the same 409 | `docs/guide-concurrency.md:19-73,93-106`, `S/Services/DatabaseAccess.cs:825-826` | Reusable for edits |
| Create-if-absent by natural id | A probe, then `StoreAsync` without a change vector | `S/Services/DatabaseAccess.cs:250-276` (probe), `:827-828` (create) | Gap: probe-then-write is not atomic (last write wins); needs `StoreAsync(e, string.Empty, id)`. The probe also sees expired-but-unswept documents, so an expired hold would collide for up to 36 h |
| Atomic decrement | Optimistic-concurrency retry loop (`OptimisticConcurrencyMode.Writes` + `catch (ConcurrencyException)`): 20 concurrent uses against a limit of 5 admit exactly 5 | `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77` (`:51,73`), `docs/guide-quotas.md:3-8` | Copyable pattern; one hot document at scale (§3.5) |
| Reservations and uniqueness | Compare-exchange claims and one cluster-wide transaction | `Auth/Identity/UserStore.cs:94-123,152-186,200-222` ✔, `Msg/Services/MessagingLease.cs:75-131`, `libs/dashboards/MintPlayer.Spark.Dashboards/Services/DashboardSamplerLease.cs:47-77`, `IdP/Services/OidcClientIdReservation.cs:42-73`, `libs/migrations/MintPlayer.Spark.Migrations/SparkMigrationRunner.cs:201-238` | The pattern exists; there is no generic "reserve a key" service (§3.7) |
| Releasing holds | A hard delete ignores the change vector | `docs/guide-concurrency.md:246` | Release by a state change under concurrency control, never by hard delete |
| Buyer operations (hold, pay, release) | Custom actions: row-gated, `SetResult`; writes through `IDatabaseAccess` wait for indexes; `OnDisableActionsAsync` decides from stored state only | `docs/guide-custom-actions.md:13-141,65-85,625-657` | Reusable; an action does not enable itself at sale start |
| Throttling an action | `ISparkCustomActionGate` → `SparkThrottledException` (429 + `Retry-After`) | `A/Actions/ISparkCustomActionGate.cs:6-39`, `A/SparkThrottledException.cs:14-22` | Reusable |
| Payment redirect or popup | Retry/449 on 9 hooks; client methods (`Retry.Invoke`, `provideSparkClientMethods`). The action re-runs from the top on each pass; `RetryFromEveryHookTests` guards both halves | `A/Retry/IRetryableRequest.cs:8-24`, `docs/guide-manager-retry-actions.md:243-312,296-301` | Reusable; a hold must therefore be idempotent |
| PSP webhook endpoint | Own endpoints and controllers with `[SparkAuthorize]`, anonymous grants, CSRF | `docs/guide-controllers.md:51-80,105-132` | Reusable |
| Signed, deduplicated webhooks | HMAC + `FixedTimeEquals`, at most once per delivery id. `SignatureService` is internal and specific to the `sha256=` header, and the route is hand-mapped (Octokit). Dedupe building blocks: `BroadcastOnceAsync`, `IMessageContext.MessageId`, natural ids | `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SignatureService.cs:11-37`, `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SparkWebhookEventProcessor.cs:49,170`, `docs/endpoints_generator_webhooks_exception.md`, `Msg/README.md:166-191,570-571`, `Mod/Documents/ModerationIds.cs:25` | Pattern reusable; implementation GitHub-specific |
| Per-buyer limits | Quotas: `SparkQuota(name, Limit, Window)`, `[SparkQuota("x")]`, `ISparkQuotas.TryConsumeAsync/ConsumeAsync/GetUsedAsync`; sliding window, 429; consumes exactly 1, never releases, gives up after 8 attempts. Rate limiter | `docs/guide-quotas.md:35-73`, `docs/guide-rate-limiting.md` | Extend: no "max N per type per buyer", no release |
| Anonymous and signed-in access | security.json `@anonymous` / `@authenticated` and library slots; anonymous access needs two grants; `ISparkCurrentUser` exposes the caller | `docs/guide-authorization.md:20-66,319-370` (`:319-331`), `docs/guide-library-layers.md`, `A/Authentication/ISparkCurrentUser.cs:20` | Reusable |
| Published-sale filter | Row security and row policies (combined with AND only) | `docs/guide-row-security.md:20-106,195-216` (`:81-106`) | Reusable |
| Seller tenancy | Organizations: `IOrganizationOwned`, roles Viewer < Member < Admin < Owner, `[RequiresOrganizationRole]` | `Org/IOrganizationOwned.cs:25,46`, `docs/guide-organizations.md:60-91` | Reusable for sellers, with the caveat below |
| Guest buyer identity | None. Anonymous callers are keyed by IP (`SparkQuotas.cs:28-31`, `LiveSocket.cs:93`). Closest: the share-link capability token (256-bit, only its hash stored, fixed-time compare) | `docs/guide-share-links.md:3-58` (`:3-5,55-58`), `A/Sharing/ISparkShareable.cs:13` | Gap. Anonymous POSTs are not CSRF-checked (`docs/guide-controllers.md:74-79`), so a visitor cookie would become ambient authority |
| Payments | None | — | Gap |
| Money | `decimal` only; the form steps by 0.01 | `A/Model/SparkModelShape.cs:203`, `NS/po-form/src/spark-po-form.component.html:432` | Gap |
| Order lines and ticket types | AsDetail + `[ValueKey]`, inline grid, three-way merge. Rules on nested rows are not re-run on save, so a per-type maximum belongs in `IBeforeSave` | `docs/guide-asdetail-attributes.md:178-254,400`, `docs/guide-concurrency.md:136-148`, `docs/guide-triggers-refresh.md:325-330` | Reusable |
| Pickers | Transient and dynamic lookups, reference pickers with parent-scoped options, `triggersRefresh` | `S/Services/LookupReferenceService.cs`, `NS/po-form/src/spark-reference-picker.component.ts`, `docs/guide-triggers-refresh.md`, `libs/model/MintPlayer.Spark.Model/TransientLookupReference.cs:3,20`, `libs/model/MintPlayer.Spark.Model/DynamicLookupReference.cs:13-17`, `docs/guide-reference-attributes.md:126-198` | Reusable |
| Seat-map editor and picker | None. ng-bootstrap offers signature-pad, tile-manager and resizable; custom renderers plug in through `provideSparkAttributeRenderers` and an AsDetail `rowComponent` | `docs/guide-custom-attribute-renderers.md:191-226,405-420` | Gap: build as a custom attribute renderer |
| Event times | `DateTimeOffset` end to end (compare `.Offset` / `EqualsExact`); the first server render is UTC; `TimeProvider` already used in quotas | `docs/guide-dates-and-sorting.md:16,39,236-290,291-331`, `S/Services/RequestTimeZoneResolver.cs:29`, `SparkQuotas.cs:34,52` | Reusable |
| Logical expiry | `IExpiring` + `ExpirationRowPolicy` hide a document on every Spark read once `ExpiresAtUtc` passes; raw queries need the same predicate; `IExpiring` uses `DateTime?`; live viewers see the expiry at the next revalidate (≤ 1 min) unless notified | `A/IExpiring.cs:34`, `S/Expiration/ExpirationRowPolicy.cs:30`, `docs/guide-expiration.md:17,63` | Reusable for logical expiry; a reservation must not auto-hide (§3.4) |
| E-ticket QR code | `SparkQrCodeRenderer`: public, renders SVG | `Auth/Identity/SparkQrCodeRenderer.cs:17` | Reusable |
| Global defaults | `SparkSetting<T>`: stored value → `IConfiguration` → default, cached 30 s | `docs/guide-settings.md:11-29,58-85,106-117` | Reusable for global defaults; per-sale values belong on the sale |
| Rate limiting | Fixed window, 150 requests per 10 s per client IP, before authentication; named policies | `docs/guide-rate-limiting.md:20-27,76-90,104-110`, `S/Extensions/SparkRateLimiterOptions.cs:13-16,52`, `S/Extensions/SparkBuilderRateLimiterExtensions.cs:115` ✔ | Extend: the partition key is the exact `RemoteIpAddress` (§6.6) |
| Buyer data under GDPR | Open-generic account hooks `ISparkAccountDeletionHandler<>`, `ISparkPersonalDataContributor<>`, `ISparkSignedInHandler<>` | `Notif/SparkNotificationsExtensions.cs:89-90`, `Org/SparkOrganizationsExtensions.cs:72-74` | Reusable |

**Organizations caveat.** The organization row policy returns false for anonymous callers and non-members
(`Org/Services/OrganizationRowPolicy.cs:28-31,85-86`), so a public sale or a buyer-visible order cannot be
`IOrganizationOwned`. A type-specific `GetRowFilterAsync` can express `IsPublished || OrganizationId.In(myOrgs)` or
`BuyerId == me || OrganizationId.In(myOrgs)`; use `In()`, not `Contains` (`docs/guide-row-security.md:50-57` ✔).

**Queue position over live updates (inference, from the research).** Every viewer watches one public
`SaleQueueState { ServedUpTo }` and computes `myNumber − ServedUpTo` client-side.

### 1.2 Gap list

1. **Anonymous visitor identity**: a capability cookie or token and a non-IP user key.
2. **Per-visitor push**: a stream scoped by capability, or a topic subscription; plus a broadcast or public-counter
   mode, because every live change costs one read per viewer.
3. **An atomic reservation primitive**: create-if-absent, or a compare-exchange "reserve" service.
4. **Sub-minute hold expiry**: a delayed message (30 s granularity) or a seconds-resolution cron job.
5. **A payment abstraction** and generic signed webhooks on top of messaging deduplication.
6. **A Money type** and its renderer.
7. **A seat-map editor and renderer.**
8. **Per-buyer, per-type maximums** that are released on expiry, refund or cancellation.
9. **Server-time sync** for countdowns.
10. **CSRF on anonymous POSTs**, once a visitor cookie carries authority (`docs/guide-controllers.md:74-79`).
11. **Identity-keyed rate limiting** with IPv6 /64 grouping, and a non-IP anonymous quota key
    (`S/Extensions/SparkBuilderRateLimiterExtensions.cs:115` ✔, `SparkQuotas.cs:28-31`; §6.6).
12. **No SMS or phone verification flow, and no account-creation timestamp** on `SparkUser`
    (`libs/authorization/MintPlayer.Spark.Authorization.Abstractions/Identity/SparkUser.cs:23,27-28,95`; §6.6).

Already covered: seller tenancy (Organizations), buyer access (security.json and row policies), live stock and
seat updates, order lines (AsDetail), outbox messaging, a cron backstop, `DateTimeOffset`.

---

## 2. How a Spark plugin is packaged and consumed

### 2.1 Template

- **Copy `libs/notifications/MintPlayer.Spark.Notifications`.** It is the newest and most complete: options, a
  configuration section, model, actions, rights, menu, endpoints, mail templates, a startup check and an Angular
  entry point. `libs/organizations` has the same shape. For a library with per-type configuration, Moderation is the
  closer template (`Mod/SparkModerationExtensions.cs:38-97`).
- Plugins are `Microsoft.NET.Sdk.Web` class libraries.

### 2.2 Registration

- **Entry point:** `ISparkBuilder AddX(this ISparkBuilder, Action<XOptions>? configure = null)`. `ISparkBuilder`
  carries `Services`, `Configuration` and `Registry` (`A/Builder/ISparkBuilder.cs:3-8`).
- **No plugin-owned `UseX` or `MapX`.** Hooks go through the registry:

| Registry hook | Effect | Evidence |
|---|---|---|
| `Registry.AddEndpoints(...)` | Run by `MapSpark()` | `A/Builder/SparkModuleRegistry.cs:141`, `S/SparkMiddleware.cs:551-561` |
| `Registry.AddMiddleware(action, stage)` | Runs inside `UseSpark()`. Stages: `AfterSpark` = 0 (default), `BeforeAuthentication` = 1 (rate limiters) | `A/Builder/SparkModuleRegistry.cs:120-122`, `A/Builder/SparkMiddlewareStage.cs:24,43`, `S/SparkMiddleware.cs:519` |
| `Registry.RequireDocumentExpiration` | Enables RavenDB expiration (needed for a hand-written `@expires`) | `A/Builder/SparkModuleRegistry.cs:27` |
| `Registry.AddIndexAssembly` | Deploys a library's indexes | `A/Builder/SparkModuleRegistry.cs:53` |
| `Registry.AddCredentialScheme` | Registers a credential scheme | `A/Builder/SparkModuleRegistry.cs:165` |

- **`UseSpark` order:** `BeforeAuthentication` middleware (`S/SparkMiddleware.cs:322`) → verifiers for layers, model
  hash, security, actions and program units (`:488-505`) → `AfterSpark` middleware (`:521`). `MapSpark`: `:551-561`.
- **Always-on core sections:** Share, Live, Expiration, TimeZone, DataProtection and ForwardedHeaders
  (`S/SparkMiddleware.cs:110-159`). Xsrf/antiforgery lives in core `UseSpark` (`:385-411`).
- **Two `AddSpark` overloads.** `AddSpark(services, configuration, configure)` binds the `Spark` section
  (`S/SparkMiddleware.cs:39-44`); `AddSpark(services, configure)` has no configuration (`:46-50`), so
  `ISparkBuilder.Configuration` is nullable. `configure` runs at `:238`; the registry singleton is registered at `:241`.
- **Idempotency:** a marker singleton (`Notif/SparkNotificationsExtensions.cs:46-52`,
  `Org/SparkOrganizationsExtensions.cs:40-46`) or a find-or-create singleton (Dashboards, Cron). **Startup
  validation** is a `Registry.AddMiddleware` step (`Notif/SparkNotificationsExtensions.cs:95,102-121`).
- **The bundle.** `AddSparkFull` (`libs/all_features/MintPlayer.Spark.AllFeatures/SparkFullOptions.cs`) is
  generator-emitted and bundles only Spark, Authorization, Messaging, Replication and Cron (escape hatch at
  `SparkFullOptions.cs:61`; Fleet uses it, `apps/Fleet/Fleet/Program.cs:49-160`).

Existing plugins:

| Plugin | Registration | Configuration section |
|---|---|---|
| Notifications | `AddNotifications` (`Notif/SparkNotificationsExtensions.cs:43`) | `Spark:Notifications` (`Notif/SparkNotificationsOptions.cs:6`) |
| Organizations | `AddOrganizations` (`Org/SparkOrganizationsExtensions.cs:37`) | `Spark:Organizations` |
| Dashboards | `AddDashboards` (`libs/dashboards/MintPlayer.Spark.Dashboards/SparkDashboardsExtensions.cs:41,75`) | `Spark:Dashboards` |
| Mcp | `AddMcp` (`libs/mcp/MintPlayer.Spark.Mcp/SparkMcpExtensions.cs:45`) | `Spark:Mcp` |
| IdentityProvider | `AddIdentityProvider` (`IdP/Extensions/SparkIdentityProviderExtensions.cs:46-52`) | `Spark:IdentityProvider` |
| Moderation | `AddModeration<TUser>` (`Mod/SparkModerationExtensions.cs:38`) | `Spark:Moderation` + the library's `moderation.json` |
| MailManager | `AddMailManager` + `Use*Transport` (`libs/mail/MintPlayer.Spark.MailManager/SparkMailManagerExtensions.cs:40,106-147`) | `Spark:Mail` |
| Messaging | `AddMessaging` | `Spark:Messaging` |
| Cron | `AddCron` (`libs/cron/MintPlayer.Spark.Cron/SparkCronExtensions.cs:22`) | — |
| History, SoftDelete, Settings/Quotas, Contributions, Migrations, Replication, GitHub webhooks | `AddHistory`, `AddSoftDelete`, `AddSettings`/`AddQuotas`, `AddContributions`, `AddMigrations`, `AddReplication`, `AddGithubWebhooks` | — |
| Authorization | `AddAuthentication<TUser>` (`Auth/Extensions/SparkBuilderExtensions.cs:34-108`) + `AddExternalProviders(configuration)`; passkeys are a switch inside | — |

### 2.3 What a plugin contributes, and how

| Contribution | How | Evidence |
|---|---|---|
| Model, actions, translations, rights, program units | The library ships `App_Data/`: `Model/*.json`, `actions.json`, `translations.json`, `security.json`, `programUnits.json` (Moderation also ships `moderation.json`) | `docs/guide-library-layers.md`, `docs/spark_composition_PRD.md` |
| Actions class | Resolved by simple name, `{Entity}Actions`, across `AppDomain.CurrentDomain.GetAssemblies()`; a duplicate name throws, so generic entity names such as `Order` or `Product` collide | `S/Services/ActionsResolver.cs:44-46,120-163` (`:137`; throw at `:151`) |
| Custom actions | Found by reflection and declared in the library's `actions.json`; on a duplicate name the first one wins, so prefix the names | `S/Services/CustomActionResolver.cs:71-102`, `Org/CustomActions/OrganizationCustomActions.cs` |
| Assembly loading | The resolvers scan loaded assemblies, so the plugin's `AddX()` effectively ensures its assembly is loaded | `S/Services/ActionsResolver.cs:44-46` |
| Query sources | Methods on the Actions class, referenced as `"source": "Custom.MyNotifications"` | `Notif/App_Data/Model/Notification.json:117` |
| Indexes | `builder.AddIndexesFrom(typeof(X).Assembly)`, "so consuming applications need write nothing" | `A/Builder/SparkIndexAssemblyExtensions.cs:17-21`, `Mod/SparkModerationExtensions.cs:79` |
| Hosted services | `AddHostedService` | `libs/dashboards/MintPlayer.Spark.Dashboards/SparkDashboardsExtensions.cs:57-58` |
| Cron jobs | `builder.AddCron(c => c.AddJob<TJob>(schedule, name))` | `Mod/SparkModerationExtensions.cs:86-88`, `libs/cron/MintPlayer.Spark.Cron/SparkCronExtensions.cs:22-50` |
| Message queues | `services.Configure<SparkMessagingOptions>(m => m.Queues.TryAdd(...))` + `IRecipient<T>` through `TryAddEnumerable`; overridable by `Spark:Messaging:Queues:{name}` | `Notif/SparkNotificationsExtensions.cs:77-83` |
| Migrations | A public `ISparkMigration`, found by the generated `AddMigrations()` | `Gen/Generators/ReferencedMigrationsReader.cs:9-19` |
| Endpoints | `[assembly: EndpointsMethodName("MapSparkNotificationsEndpoints")]` + an `IEndpointGroup` with prefix `/spark/notifications` and `RequireAuthorization`; antiforgery on POSTs; `static bool IsEnabled(IServiceProvider)` per endpoint | `Notif/AssemblyInfo.cs:3`, `Notif/Endpoints/NotificationEndpoints.cs:11-16`, `IdP/Endpoints/Authorize.cs:18` |
| Save hooks and row policies | `builder.AddInterceptor<T>()`, `AddSparkRowPolicy<T>()` | `A/Interceptors/SparkBuilderInterceptorExtensions.cs:22`, `S/Extensions/SparkBuilderRowPolicyExtensions.cs:16` |
| Account hooks (GDPR) | Open-generic `ISparkAccountDeletionHandler<>`, `ISparkPersonalDataContributor<>`, `ISparkSignedInHandler<>` | `Notif/SparkNotificationsExtensions.cs:89-90`, `Org/SparkOrganizationsExtensions.cs:72-74` |
| Mail templates | MJML embedded with `LogicalName="SparkMail/…"` + `AddSparkMailTemplates(services, assembly, "SparkMail/")`; an app template with the same name wins | `Notif/MintPlayer.Spark.Notifications.csproj:33`, `Notif/SparkNotificationsExtensions.cs:92-93` |
| Reserved actions | `[assembly: SparkReservedActions(typeof(XRights))]` | `libs/history/MintPlayer.Spark.History/AssemblyInfo.cs:5` |
| Expiring documents | `IExpiring` + `"expiresAfter"` → `@expires`; a row policy hides expired documents at read time, so the sweep's lag (up to 36 h on Community) does not affect visibility; Notification uses it | `docs/guide-expiration.md` |

### 2.4 Layers, aliases and slots

- **Alias.** `<SparkLibraryAlias>` in the csproj (`Notif/MintPlayer.Spark.Notifications.csproj:10`) is permanent: it
  appears in every right key, slot and model id. It must be lower-kebab and unique (SPARK041–043;
  `docs/guide-library-layers.md:203-211`).
- **Layers.** The `LibraryLayersGenerator` turns App_Data files into `[assembly: SparkLayer(alias, kind, path, json)]`
  (`docs/guide-library-layers.md:198-233`; `Gen/build/MintPlayer.Spark.SourceGenerators.targets:33-55`), for a
  ProjectReference and a PackageReference alike. The set of layer kinds is closed
  (`libs/spark/Shared/Layering/SparkLibraries.cs:14-19,35-43`), so a new library's configuration belongs under
  `Spark:<Library>`, not in a new json layer.
- **Order and conflicts.** Core → libraries (in dependency order) → app, and the app wins silently. Library-vs-library
  conflicts raise SPARK036 / SPARK_TRANS_005. `--spark-describe <kind> [name] --layers` shows the composition. The gates
  are `modelHashes.json` v2 and `securityPosture.txt` (`docs/guide-library-layers.md:152-183`). Library diagnostics are
  SPARK041–047.
- **Model ids** are UUIDv5, stamped by `npm run stamp:library-model-ids` and checked by SPARK045.
- **App deltas.** App files compose per key on top of the library's. Model files are keyed by `persistentObject.name`,
  and an application file naming a library type is a delta (`docs/guide-library-layers.md:72` ✔). A library right is
  removed with `{"key":"alias:x","$remove":true}` (`docs/guide-library-layers.md:47-136,106-148`).
- **Library rights** may only grant, only on the library's own types (or `reservedTargets` pseudo-types), and only to
  `@authenticated`, `@anonymous` or the library's own slots (`libs/spark/Shared/Layering/SparkSecurityLayers.cs:436-446` ✔).
  The app must bind every slot (`"bindings": {"notifications:users": [...]}`) or startup refuses with SPARK048
  (`SparkSecurityLayers.cs:280-302` ✔). `"libraries": {"alias": false}` switches a library's rights off. Example:
  `Notif/App_Data/security.json`; SeoTester binds four library slots at
  `apps/SeoTester/SeoTester/App_Data/security.json:26-29` ✔.
- **A slot resolved from code** (`A/Authorization/SparkSecurityFiles.cs:101-129`) needs the library's own startup check
  (`Mod/ModerationStartupCheck.cs:38-60`).
- **Reserved targets.** The analyzer hardcodes `["LookupReferences","Moderation"]`
  (`Gen/Diagnostics/SecurityConfigurationAnalyzer.cs:120`); a new pseudo-target needs that list extended or
  generalised.
- **Transitive reach.** The analyzer enumerates transitive references (`Gen/Diagnostics/LibraryLayersReader.cs:40` ✔),
  so a slot-bound right in a library probably reaches every app that references the library indirectly
  [unverified at runtime].
- **Precedents:** per-type grants printed by an init command (`--spark-init-moderation`,
  `Mod/ModerationInitCommand.cs:12-57`); a per-type option map as History's case-insensitive `Types` dictionary
  (`libs/history/…/SparkHistoryOptions.cs:37`, applied in `PostConfigure`, `SparkHistoryExtensions.cs:73-82`).

### 2.5 Pitfalls

| Pitfall | Consequence | Evidence |
|---|---|---|
| Unused references are trimmed | The app generator records `[SparkLayerAssemblies]`; the runtime loads those, with a deps.json fallback | `A/SparkLayerCatalog.cs:98-118`, `docs/spark_composition_PRD.md:116-121` |
| SparkAware keys on assembly names | Detection keys on the Abstractions/Model/Attributes assembly names | `A/SparkAssemblies.cs:68-75` |
| Nx cache hits | App_Data writes are lost on a cache hit | `docs/spark_composition_PRD.md:31-37` |
| Adding a library with a model | `--spark-verify-model` is red until the app re-syncs and commits `modelHashes.json` and `securityPosture.txt` | `docs/guide-library-layers.md:152-183` |
| Type names collide silently | Model types match by `persistentObject.name`: an app type named like a library type silently becomes a delta of it. Actions classes match by simple name and throw on a duplicate. Full analysis in §8 | `docs/guide-library-layers.md:72` ✔, `S/Services/ActionsResolver.cs:151` |
| Library → library references | Declared through `SparkLayerDependencies` | — |
| Stale generator | A stale generator DLL can be packed | `Directory.Build.targets:44-52` |
| ModelSynchronizer | Development-only; writes only the app delta | `S/SparkMiddleware.cs:166-179` |
| buildTransitive and ProjectReference | Not imported through a ProjectReference; the repo imports them itself, and library targets must gate on `@(ReferencePath)` | `Directory.Build.targets:12-25,35-37` |
| Test hosts | `AddSpark(Action)` never binds library configuration defaults; `SparkTestSecurity.LibraryRightsOff()` exists | — |
| Entity-only libraries | `<SparkEntityLibrary>true` may not depend on ASP.NET (SPARKLIB001), so interfaces for app entities go in a plain `*.Abstractions` | `Directory.Build.targets:185-201` |
| NuGet consumers | Need the generator with `PrivateAssets="all"`, else SPARK044; the library has an analyzer ProjectReference to SourceGenerators | `Notif/MintPlayer.Spark.Notifications.csproj:55` |
| Authorization setup file | SPARK030 + `SparkAuthGenerateSetupFile` | `Auth/Targets/spark-authorization.targets:15-29,94-106` |
| Custom-action rights | A type offers an action only if `{Action}/T` is granted; no wildcards (SPARK021) | — |

### 2.6 Angular entry points

- **Shape.** Secondary entry points of `@mintplayer/ng-spark`: a folder + `ng-package.json` + `index.ts`. Existing:
  `notifications`, `organizations`, `organizations/accept`, `dashboards`, `moderation`, `auth` (+ 16 sub-entries),
  `identity-provider/{core,connected-applications,developers,management}`, `history`, `soft-delete`, `contributions`.
  ng-spark is at 22.41.1.
- **What an entry point exports:**
  - providers: `provideSparkNotifications(opts)` (a token; `NS/notifications/src/spark-notification.ts:58`),
    `provideSparkDashboards()` (wraps `provideSparkDetailLayouts`; plus `sparkDashboardRenderers`),
    `provideSparkModeration()` (moderation entry point, `:29`);
  - routes: standalone `sparkModerationRoutes(path)` (`:46`), placed before `...sparkRoutes()`
    (`apps/QnA/QnA/ClientApp/src/app/app.routes.ts:26`), or an auth feature such as `withOrganizationRoutes()` passed
    into `sparkAuthRoutes(...)` (`apps/SeoTester/SeoTester/ClientApp/src/app/app.routes.ts:30`);
  - renderers: arrays merged into one `provideSparkScoreRenderers([...])`
    (`apps/SeoTester/SeoTester/ClientApp/src/app/app.config.ts:33`);
  - shell components such as the notification bell (`shell.component.html:8-9`).
- Plugin data screens reuse the generic `po/:type` and `query/:alias` routes.
- Entry points lazy-import each other (`NS/organizations/src/organization-routes.ts`). Client paths must match the
  server configuration (`Spark:Organizations:AcceptPath`).
- Wiring examples: SeoTester `app.config.ts:30,35` and `app.routes.ts:68`; QnA `app.routes.ts:105`.
- **Add-on registry** in `NS/panels/src/spark-panels.ts`: `SPARK_DETAIL_ACTIONS` (`:82-87`), `SPARK_DETAIL_PANELS`
  (`:69-75`), `SPARK_DETAIL_LAYOUTS` (`:148-161`), `SPARK_QUERY_LIST_ACTIONS` (`:93-98`), `SPARK_QUERY_ROW_ACTIONS`
  (`:124-140`). History, SoftDelete, Moderation, Contributions and Dashboards use it. `PersistentObject.metadata`
  (`NS/models/src/persistent-object.ts:46-50`) carries add-on facts to the client.

### 2.7 Configuration binding and config-driven modes

- Each options class has a `SectionName`. A lazy `IConfigureOptions` reads the section, then applies the code lambda
  (`Notif/SparkNotificationsExtensions.cs:54-59`; `S/Extensions/SparkConfigurationSectionExtensions.cs:16-24`). It
  avoids `BindConfiguration`, so bare test `ServiceCollection`s work.
- Bind **eagerly** when the value decides registration or mapping: IdentityProvider
  (`IdP/Extensions/SparkIdentityProviderExtensions.cs:52` → `OidcSurface.For(options)`), Moderation's schedules
  (`Mod/SparkModerationExtensions.cs:81-88`), MailManager (`SparkMailManagerExtensions.cs:42-47`).
- **Precedence is inconsistent today.** Code wins in Notifications (`Notif/SparkNotificationsOptions.cs:3`),
  IdentityProvider (`SparkIdentityProviderExtensions.cs:51-53`) and MailManager; configuration wins in Moderation
  (its decision D14, `Mod/SparkModerationExtensions.cs:44-49`). Configuration-wins fits "configuration decides the
  scenario" (inference).
- **Library-shipped defaults:** `moderation.json` is the lowest-precedence configuration source
  (`AddSparkModerationFile`, `docs/guide-library-layers.md:76`). A new library cannot add a layer kind (§2.4).

| Plugin | Config-driven mode | Evidence |
|---|---|---|
| MailManager | SMTP when `Spark:Mail:Smtp:Host` is set, otherwise a pickup folder | `SparkMailManagerExtensions.cs:77-85,150-153` |
| Notifications | Web Push is off (`IsConfigured` false; its endpoints answer 404) until VAPID keys are set | `Notif/SparkNotificationsOptions.cs:58` |
| IdentityProvider | `[Flags]` `Flows` and `Endpoints`; invalid combinations refuse startup | `IdP/Configuration/SparkIdentityProviderOptions.cs:108,114`, `IdP/Configuration/SparkIdentityProviderSurface.cs:13-14,53` |
| Authorization | `SparkLocalCredentials` enum with a safe 0, set in code only; external providers are registered only when a ClientId is set | `Auth/Configuration/SparkLocalCredentials.cs:16-27`, `Auth/Extensions/SparkBuilderExtensions.cs:40-41` |

### 2.8 The smallest consumer

| Consumer | What it takes | Evidence |
|---|---|---|
| SeoTester + Notifications | 1 ProjectReference, `spark.AddNotifications();`, 1 slot binding in security.json; client: 1 provider line (+ an optional component) | `apps/SeoTester/SeoTester/SeoTester.csproj:38`, `apps/SeoTester/SeoTester/App_Data/security.json:27` ✔ |
| Organizations | `spark.AddOrganizations();` + `withOrganizationRoutes()` | — |
| DemoApp (core only) | `AddSpark` (8 lines) + `UseSpark` + `MapSpark` | `apps/DemoApp/DemoApp/Program.cs:12-26` |
| HR | Messaging and MailManager, one line each | `apps/HR/HR/Program.cs:78-81` |

---

## 3. RavenDB mechanics for no-oversell and holds

Client in the repository: `RavenDB.Client` 7.2.6 (TestDriver 7.2.1 / 7.2.6). Nothing below needs a new RavenDB
feature; the library composes existing blocks.

### 3.1 The constraint that decides the design: the Community 36 h floor

- Production runs a **Community licence**. Measured against production on 2026-09-07: `/license/status` reports
  `Type: Community` with `MinPeriodForExpirationInHours: 36`, and the database record carries
  `DeleteFrequencyInSec: 129600` (`S/Expiration/SparkExpiration.cs:9-32` ✔).
- A smaller value does not degrade, it **fails the server**: `ClusterStateMachine.AssertExpirationConfiguration`
  rejects the cluster command and the RavenDB process dies. CI and local runs hold a Developer licence with no minimum,
  so the mistake is invisible there; `ValidateDeleteFrequency` refuses it on every licence (same lines ✔).
- The same floor applies to `@refresh` (`MinPeriodForRefreshInHours`) (`SparkExpiration.cs:28-30` ✔).
- **Consequence: neither `@expires` nor `@refresh` can release a 10-minute hold in production.** Hold validity must
  be evaluated lazily at read and write time, with an application-side sweeper for side effects.

### 3.2 Primitives compared

| Primitive | What it guarantees | Limits | For allocation | Source |
|---|---|---|---|---|
| Optimistic concurrency (change vectors) | Default is none: last write wins. 7.2 deprecates `UseOptimisticConcurrency` for `OptimisticConcurrencyMode`: `None`, `Writes` (modified documents carry their change vector), `WritesAndReads` (every tracked document, even unmodified, is validated: a read-set check). `Store(entity, changeVector: string.Empty, id)` means "must not exist" and throws `ConcurrencyException` | `WritesAndReads` cannot be combined with cluster-wide transactions, NoTracking or sharding. A change vector is checked only on the node that receives the write; documents replicate by gossip, so two writes on two nodes (failover, or a client writing elsewhere) both succeed and become a replication conflict resolved later (default: latest wins) | Correct on a single node: load, check `Available >= n`, decrement, save with `Writes`, retry the loser. In a cluster, oversell is possible in a failover window, not in steady state (inference), so a "never oversell" promise must not rest on it | [OCC 7.2](https://docs.ravendb.net/7.2/client-api/session/configuration/how-to-enable-optimistic-concurrency) · [Ayende](https://ayende.net/blog/190978-B/complex-distributed-transactions-with-ravendb) |
| Cluster-wide transaction | `SaveChanges` becomes a Raft command that a majority must accept; compare-exchange values are validated first and the whole transaction rolls back on a mismatch; concurrent cluster transactions behave as if serial; a cluster transaction wins conflicts against a normal one. The page names "selling a product with limited inventory" as the use case | Only PUT and DELETE (an update is a PUT): no patch, counters or attachment operations; no document-level OCC (compare-exchange provides it); fails without quorum or a leader | Correct on a single node and in a cluster. Cost: one Raft round per commit (RTT × majority + fsync); contenders on one key serialize, and losers throw `ConcurrencyException` and retry (inference) | [cluster transactions](https://docs.ravendb.net/server/clustering/cluster-transactions) |
| Atomic guards (5.2+) | A compare-exchange item per document is created automatically in cluster-wide sessions; its Raft index bumps on every save, so a concurrent cluster session that loaded the older version is rejected | Opting out (`DisableAtomicDocumentWritesInClusterWideTransaction`) forfeits ACID unless compare-exchange is managed by hand. `UserStore` opts out because users "are written by ordinary sessions, so they carry no atomic guard to check" (`Auth/Identity/UserStore.cs:200-222` ✔) | Load-check-PUT in a cluster-wide session is a correct no-oversell recipe | [atomic guards 7.1](https://docs.ravendb.net/7.1/compare-exchange/atomic-guards) · [atomic guards](https://docs.ravendb.net/compare-exchange/atomic-guards) · [compare-exchange](https://docs.ravendb.net/compare-exchange/overview) · [compare-exchange 7.2](https://docs.ravendb.net/7.2/compare-exchange/overview) |
| Counters | Part of the session transaction; reverted if it fails | Increment-only and order-independent; each node changes them without coordinating; signed 64-bit (can go negative); no conditional increment (two concurrent −1 on 1 give −1); not allowed in a cluster-wide transaction | Statistics only (a sold count for display), never the guard | [counters](https://docs.ravendb.net/7.1/document-extensions/counters/overview) |
| Single-document patch | Runs server-side in Jint, one document at a time, in isolation. `if (this.Available < args.n) throw 'SOLD_OUT'; this.Available -= args.n;` is atomic on the executing node, with no read round trip and no client retry | Node-local (the OCC cluster caveat applies); not allowed in a cluster-wide transaction. Set-based (by-query) patches do no per-document concurrency check and do not roll back documents already patched | Highest single-node throughput (inference); not enough for the cluster guarantee; never set-based | [JavaScript engine](https://docs.ravendb.net/7.2/server/kb/javascript-engine) · [set-based patch](https://docs.ravendb.net/7.2/client-api/operations/patching/set-based) |
| Document expiration (`@expires`) | Deletes expired documents every `DeleteFrequencyInSec` (server default 60 s), further delayed by `MaxItemsToProcess` | Off by default; expired documents are **not** filtered out of loads, queries or indexing until deleted; 36 h floor in production | Garbage collection only; never decides whether a hold is valid | [expiration](https://docs.ravendb.net/7.1/server/extensions/expiration) · `SparkExpiration.cs` |
| `@refresh` + subscriptions | Re-touches a document at a set time so a subscription or index sees it again | Same periodic task, same 36 h Community floor | Long timers only, e.g. releasing unpaid bank-transfer orders after 12 days | `SparkExpiration.cs:28-30` ✔ |
| Revisions | An audit trail of stock, seat and reservation documents, for disputes | Not a concurrency primitive. The Community licence caps revisions (locally: `LicenseLimitException` "revisions 1000 > licensed 2") | Check the licence before relying on it | `CLAUDE.md` ("Running the test suites") |

### 3.3 Which combination is correct

| Setup | Correct no-oversell recipe |
|---|---|
| Single node | A conditional-throw patch, or load + `OptimisticConcurrencyMode.Writes` + retry |
| Cluster (any size) | A cluster-wide session (atomic guards) doing load-check-PUT, or an explicit compare-exchange CAS |
| Either | Counters, `@expires` and `@refresh` only as projections and housekeeping, never as the guard |

Research reading (inference): one code path, cluster-wide always. It is also correct on a single node (a one-node
"cluster" still runs Raft locally), and two engines would double the test matrix. A patch fast path is worth adding
only if a measured single-node throughput need appears.

### 3.4 Hold expiry: lazy validity, active sweeping

**Lazy expiry.** A hold is a claim `{ ownerId, expiresAtUtc }` that occupies capacity only while
`now < expiresAtUtc`. Every allocation computes `capacity − sold − Σ(holds where expiresAt > now)` inside the same
atomic write, and a new claimer may overwrite an expired claim (a CAS on its old version). Conversion to sold re-checks
`now < expiresAt` (plus grace) in the same atomic write. Why this makes correctness independent of the sweeper
(inference):
1. Validity is a pure function of stored data and the clock at commit time. A sweeper that runs late, twice or never
   cannot make a hold valid or invalid; it only tidies up.
2. There is no race between "the sweeper releases" and "the payment converts": both are CAS writes on the same
   version, and exactly one wins. With active-only expiry there is a window in which a hold past its time still
   occupies stock, or in which the sweeper and the payment both act on stale reads.
3. It survives the 36 h floor, process restarts and lost timers.
4. It is already Spark policy: `IExpiring` treats the read filter as the guarantee and the sweep as housekeeping
   (`A/IExpiring.cs`, `S/Expiration/ExpirationRowPolicy.cs:28-43`), and `MessagingLease` takes over when
   `ExpiresAtUtc <= now`, with a fence (`record MessagingLease(NodeId, AcquiredAtUtc, ExpiresAtUtc, Fence)`,
   `Msg/Services/MessagingLease.cs`).

**Active sweeping is still needed for** (a) prompt UX, such as a seat shown free again (cosmetic, since reads apply
the lazy rule too); (b) side effects: cancelling the PSP session or payment, a "your hold expired" mail, display
counters; (c) keeping hold lists small in pooled documents. The research suggests a `SparkCron` job about every
30 s over `ExpiresAtUtc <= now and Status == Held`, each release a CAS that no-ops if the reservation has moved on.
Sub-minute options in Spark: a delayed message (30 s poll granularity, `Msg/SparkMessagingOptions.cs:61`) or a
6-field cron with seconds.

**Clock** (inference). Use one clock, the application server's UTC, and give the payer the benefit of any skew:
conversion is allowed until `expiresAt + grace`, takeover by another buyer only after `expiresAt + grace`, so both can
never succeed. Show the countdown from the server-provided `expiresAt`, not from a client timer start (there is no
server-time sync today, §1.1).

**A reservation must not auto-hide.** Expired reservations must stay readable for late webhooks, so the reservation's
expiry is a separate field, not `IExpiring` with its read filter. The natural-id probe also sees expired-but-unswept
documents (§1.1).

### 3.5 Hot quotas and sharding

- **The hot key.** Every buyer of one SKU or quota contends on one document or compare-exchange key. Under OCC,
  throughput collapses to about one commit per (Raft round + retry latency), and the retry rate grows with concurrency
  (inference). The repository has a measurement of the effect: a compare-exchange counter taken by 8 parallel producers
  needed **6,668 CAS retries for 1,000 enqueues, p95 345 ms, 12 s wall against 1.3 s** (§3.6).
- **Bucketing ("stock tokens").** Split capacity C into K bucket documents `stock/{sku}/b{i}` of about C/K each. A
  buyer picks a random bucket, or `hash(reservationId) % K`, and falls through to the next when it is empty. Contention
  per key drops about K×. The costs: "sold out" needs all K empty (a read of K small documents), and a multi-unit order
  may span buckets (several PUTs in one cluster transaction). Rebalance by moving units between buckets in a cluster
  transaction (inference; the classic sharded-counter pattern).
- **One document per unit or seat.** For numbered seats this removes the hot spot: contention exists only between
  buyers of the same seat. Seat id `events/{e}/seats/{block-row-no}`; the claim is the seat document's `Hold` field or a
  compare-exchange key `seat/{e}/{s}` (alternatively `seat/{occurrence}/{guid}` per occurrence). A 50,000-unit GA pool
  as 50,000 documents is fine for RavenDB and makes held tokens auditable; buckets are the cheaper middle ground
  (inference).
- **Admission upstream.** A waiting room's admission rate caps contention before the database, so the guard only
  needs to be correct, not infinitely fast (inference).
- **Range reservation** (one compare-exchange put reserving a range of 100) costs **0.14 ms per item** (§3.6), the
  cheap path for chain fill. The kept spike test does not check the put's success
  (`tests/MintPlayer.Spark.Tests/Messaging/SequenceSourceSpikeTests.cs:145` ✔), so a production version must.

### 3.6 Measured: where a queue's order comes from (spike S-M7, SequenceSourceSpikeTests)

Method (`docs/issue_460_PRD.md:1653-1659` ✔; results table `:1662-1669` ✔): `tests/MintPlayer.Spark.Tests/Messaging/SequenceSourceSpikeTests.cs`
(kept; run with `SPARK_SPIKE_SM7_N=1000`, default 100). 1,000 `SparkMessage`s enqueued from 8 producers in parallel,
one session per message. Ground truth is each document's etag, i.e. commit order, which is the order a subscription
delivers in. An inversion is an adjacent pair, in commit order, whose key goes down. **Developer licence, embedded
RavenDB 7.2** (not a production-node measurement).

| Candidate | Per enqueue p50 / p95 / mean | Inversions / 999 | Notes |
|---|---|---|---|
| (a) `@last-modified` (via `MetadataFor` in the index) | 7.3 / 21.4 / 9.8 ms | **0** (ties 0) | Set by the server on commit; changes on every write, so captured once |
| (b1) compare-exchange counter, taken before the store | 34 / 345 / 99 ms | 0 | **6,668 CAS retries** for 1,000 enqueues; 12 s wall against 1.3 s |
| (b2) document counter (`CounterBatchOperation`) | 5.5 / 11.3 / 6.1 ms | **340** | Taken before the store, so commit order and key order disagree |
| (b3) batch enqueue with one reserved range of 100 | 0.14 ms per message | not measured | A range is reserved before its batch commits, so concurrent batches interleave as (b2) does |
| (c) first-save etag written back | 7.6 / 14.4 / 8.3 ms | 356 against delivery order | A second write per message, which itself moves the etag |
| (d) server identity id | 6.5 / 11.5 / 6.9 ms | **183** | Identities are not handed out in commit order |

Four runs had the same shape: (a) 0 inversions every run; (b1) 0–1; (b2) 307–340; (d) 109–199
(`docs/issue_460_PRD.md:1671` ✔). Messaging chose (a), captured once into `QueuedAtUtc`. For commerce this means
(inference): queue join order should come from the server's `@last-modified`, captured once (the `QueuedAtUtc`
pattern); a single compare-exchange stock key is a hotspot to shard.

### 3.7 Reservation patterns already in the repository

| Pattern | Where | What it shows |
|---|---|---|
| Unique email reservation | `Auth/Identity/UserStore.cs:94-123,152-186` | A create-only compare-exchange put (index 0) before the document save, with compensation on failure: the seat-claim pattern exactly |
| Stale takeover | `Auth/Identity/UserStore.cs:699-734` | Take over a reservation only when its owner document is missing |
| Owner-guarded release | `Auth/Identity/UserStore.cs:736-751` | Release only the caller's own reservation |
| Atomic delete and release | `Auth/Identity/UserStore.cs:200-222` ✔ | User delete and reservation release in one cluster-wide transaction, introduced after separate deletes left an address blocked for good |
| Passkeys | `Auth/Identity/UserStore.Passkeys.cs:110,195-217`, `docs/guide-passkeys.md:142-149` | A compare-exchange reservation keyed by a hash, unique across users, with no index read |
| Lease with fencing | `Msg/Services/MessagingLease.cs:75-131` | `NodeId`, `AcquiredAtUtc`, `ExpiresAtUtc`, `Fence`; takeover when `ExpiresAtUtc <= now`: structurally a seat hold |
| Other compare-exchange claims | `libs/dashboards/MintPlayer.Spark.Dashboards/Services/DashboardSamplerLease.cs:47-77`, `libs/migrations/MintPlayer.Spark.Migrations/SparkMigrationRunner.cs:201-238`, `IdP/Services/OidcClientIdReservation.cs:42-73` | The same claim shape |
| OCC retry loop | `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77` | A single-document atomic decrement |
| Server-side patches | `Auth/Agents/AgentGrantStore.cs:139`, `Msg/Services/MessageProgress.cs:50` | Patch precedents |
| Admission rate | `Msg/Services/QueueAdmission.cs:5-22,42-79,116-139`, `tests/MintPlayer.Spark.Tests/Messaging/QueueAdmissionTests.cs` | An internal, in-memory GCRA, leader-only, forgotten on restart and quantised by the 30 s poll: the algorithm transfers to a waiting-room admitter, the API does not |

Seat key proposed by the research (inference): `sale/{id}/seat/{seat}` holding `{holdId, ExpiresAtUtc}`. Per-buyer
limit key (inference): compare-exchange `limit/{event}/{type}/{buyer}` (or a `BuyerLimit/{event}/{type}/{buyer}`
document) in the same cluster transaction as the claim.

### 3.8 One engine for shop and tickets (research sketch, superseded in detail by the PRD)

Observation (inference): *sold* is a claim whose `expiresAt` is null. Allocating at payment is a claim made and
finalised in one atomic write; ticketing makes the claim at selection and finalises it later. One allocator covers
both:

```
TryClaim(inventory, units|quantity, owner, expiresAt?) -> Claimed(claimId, expiresAt) | Unavailable | LimitExceeded
Finalize(claimId, paymentRef)                          -> Confirmed | Expired(reallocatable?)
Release(claimId, reason)
```

All three are compare-and-set writes in a cluster-wide session, with validity evaluated lazily against `now`. The
research's state machine:

```
                 TryClaim ok
  [Selecting] ───────────────▶ [Held(expiresAt)] ──(release / sweeper / now≥expiresAt)──▶ [Expired]
       │  (OnPayment skips)          │ redirect to PSP: extend expiresAt to PSP.expiresAt+grace
       │                             ▼
       └──────────────────────▶ [PaymentPending(expiresAt)] ──(PSP expired/canceled webhook)──▶ [Released]
                                     │ paid/authorized webhook (idempotent, CAS)
                                     ▼
                         claim still valid? ── yes ──▶ [Confirmed] (claim finalized; capture if manual)
                                     │ no
                                     ▼
                         re-claim same units ── ok ──▶ [Confirmed]
                                     │ fail
                                     ▼
                       [Refunding|Voiding] ──▶ [Refunded|Voided]   (notify buyer)
  [Confirmed] ──(customer/admin cancel)──▶ [Cancelled] ──▶ claim released, refund per policy
```

Any read or transition treats `Held` or `PaymentPending` with `now ≥ expiresAt (+ grace)` as expired, whether or not
the sweeper has written it. Storage sketch (all writes cluster-wide): a `Reservation` document (state, owner, lines,
claim ids, its own expiry field, PSP references, idempotency key); numbered seats as a `Seat` document
`{ Status: Free|Held|Sold, ReservationId, HeldUntilUtc }` with an atomic guard per seat; pooled capacity as a
`StockBucket` document `{ Capacity, Sold, Holds: [{ReservationId, Qty, UntilUtc}] }` where each write prunes expired
holds and checks `Sold + Σ active holds + n ≤ Capacity`; display counters as RavenDB counters or an index,
informational only.

---

## 4. Payment-provider behaviour relevant to holds

### 4.1 Time to pay

| Provider / method | Time to pay | Notes |
|---|---|---|
| Stripe Checkout Session | `expires_at` 30 min to 24 h after creation; default 24 h | A 10-minute hold is shorter than the minimum, so expire the session (`POST /v1/checkout/sessions/{id}/expire`) when the hold dies; a payment already in flight can still complete, so a late-payment path is mandatory ([create](https://docs.stripe.com/api/checkout/sessions/create), [expire](https://docs.stripe.com/api/checkout/sessions/expire)) |
| Mollie iDEAL | 15 min | Per-method expiry, `open` → `expired` |
| Mollie credit card | 30 min | |
| Mollie Bancontact, KBC, Belfius | 1 h | |
| Mollie PayPal | 6 h | |
| Mollie Klarna | 48 h | |
| Mollie bank transfer | 12 days (+ weekend) | |

Mollie: `expired` and `canceled` are definitive and fire the webhook; `open` does not. Mollie says explicitly not to
predict expiry but to wait for the webhook ([handling payment status](https://docs.mollie.com/docs/handling-payment-status),
[status changes](https://docs.mollie.com/payments/status-changes)). [unverified: the two Mollie pages differ slightly;
check each payment's `expiresAt` in the live API.]

### 4.2 Authorize-then-capture

| Provider | Mechanism | Availability | Source |
|---|---|---|---|
| Stripe | `capture_method=manual` on a PaymentIntent (or `payment_intent_data` on Checkout); status `requires_capture`; cancel the PaymentIntent to void | Card authorisations stay valid about 7 days online (Visa CIT 7 d, MIT about 5 d). **Not supported by iDEAL or ACH**, among others. For a Belgian/Dutch audience, where Bancontact and iDEAL dominate, it is not universally available, so refund is the fallback | [place a hold](https://docs.stripe.com/payments/place-a-hold-on-a-payment-method) |
| Mollie | `captureMode: manual`: authorisation holds for cards | [unverified: one research pass did not fetch the page] | [place a hold](https://docs.mollie.com/docs/place-a-hold-for-a-payment) |

### 4.3 Webhooks are the source of truth

- Redirects are UX; webhooks decide. Events can arrive late, duplicated or out of order. Stripe recommends one
  idempotent fulfilment function, called from both the webhook and the success page
  ([fulfillment](https://docs.stripe.com/checkout/fulfillment), [webhooks](https://docs.stripe.com/webhooks)).
- Stripe idempotency keys make POST retries safe; keys are pruned after at least 24 h
  ([idempotent requests](https://docs.stripe.com/api/idempotent_requests)). The research suggests the reservation id
  (+ attempt) as the key for create-payment, refund, capture and cancel.
- The repository's messaging fits (inference): webhook → enqueue → an idempotent
  `ConfirmPayment(reservationId, pspPaymentId)`.

### 4.4 When payment and hold disagree

Patterns for "paid, but the hold expired or the stock is gone" (inference unless sourced above):
1. **Align the windows.** Make the hold TTL at least the PSP's time to pay, or move the reservation to
   `PaymentPending` on redirect and extend its expiry to the PSP payment's own `expiresAt` (+ grace). The PSP expiry
   is finite except for bank transfer, which is either excluded or treated as its own long hold. This keeps the seat
   held exactly while a payment can still succeed, which removes most late webhooks.
2. **Cancel the payment when the hold dies** (Stripe: expire the session or cancel the PaymentIntent; Mollie: cancel
   if `isCancelable`). This narrows the race but does not close it.
3. **Authorize-then-capture** where the method supports it (cards, PayPal, Klarna): allocate on
   `requires_capture`/`authorized`, capture only after the allocation commit succeeded, otherwise void (no money
   moved, no refund fee). The cleanest answer for a webshop.
4. **Late payment: re-allocate or refund.** On a paid webhook for an expired or released reservation, try to claim
   the same units again; success confirms, failure moves to `Refunding` with an automatic full refund (idempotent key)
   and a notification. Never "confirm anyway": that is oversell.
5. **Idempotent confirm.** Confirm is a CAS from `Held`, `PaymentPending` or `Expired`, recording the PSP payment id.
   A duplicate webhook finds `Confirmed` with the same id and no-ops; a different paid payment id on an already
   confirmed reservation (a double payment) is refunded.

Webshop specifics: with immediate-capture methods, payment completes before allocation, so a sell-out after payment
means a refund (pattern 4); with manual capture it means a void (pattern 3). A short hold when the buyer clicks Pay
removes nearly all refunds at the cost of briefly reserved stock; that is a policy, not a different engine.

Startup checks the research proposed: a hold TTL below the PSP's minimum window is valid only because the
`PaymentPending` extension exists; authorize-then-capture with a method set that lacks manual capture falls back to
refunds per method; a waiting room's admission TTL must cover the hold TTL plus the payment window.

### 4.5 Card fingerprints and payment identifiers

| Provider | What identifies the payer | When it is known | Source |
|---|---|---|---|
| Stripe | `card.fingerprint` "uniquely identifies this particular card number"; for Apple Pay and Google Pay "the tokenized number might be provided", so each wallet token can have a different fingerprint; `card.funding` flags prepaid cards. Radar rules and `request_three_d_secure` (`any` / `challenge`) add friction for risky orders | Before confirmation, so an over-limit fingerprint can be refused before the charge | [PaymentMethod object](https://docs.stripe.com/api/payment_methods/object.md?query=card) · [3DS flow](https://docs.stripe.com/payments/3d-secure/authentication-flow) · [Radar rules](https://docs.stripe.com/radar/rules) |
| Mollie | `details.cardFingerprint` "Can be used to identify returning customers"; also `cardFunding` (including prepaid), `cardHolder`, `wallet`, `consumerAccount` (the IBAN for bank methods), `paypalPayerId`. Risk Controls block lists by email, card fingerprint, card or IP country. A zero-amount (or €0.01) first payment for cards or PayPal yields a mandate, and a fingerprint, at registration | **After** payment, so an over-limit order is voided and refunded | [extra payment parameters](https://docs.mollie.com/reference/extra-payment-parameters) · [Risk Controls](https://help.mollie.com/hc/en-gb/articles/14199431980050) · [recurring](https://docs.mollie.com/payments/recurring) |

A family sharing one card hits a per-payer limit; that is what a published per-buyer limit means. Payment
fingerprints are the strongest cheap de-duplication key for limits (§6.3).

### 4.6 3-D Secure and marketplace payouts

- **PSD2 SCA / 3-D Secure.** A bank-app approval per card payment raises the cost of scripted checkout at scale
  (inference); exemptions exist. Asking the PSP for a 3DS challenge on elevated risk is the payment-native
  alternative to a CAPTCHA (Stripe `request_three_d_secure`; Mollie Risk Controls).
- **Marketplace payouts.** "Sellers create accounts and sales" needs the platform to take a fee while sellers are paid
  out, through Stripe Connect or Mollie Connect (no page fetched for either).

---

## 5. Domain research

### 5.1 What established platforms model

**pretix** (open source, Python/Django) is the closest reference for ticketing
([concepts](https://docs.pretix.eu/en/latest/development/concepts.html)):
- **Organizer → Event.** Buyers need no account; staff are "users".
- **Event series (sub-events, dates).** One event holds many dates; per date: title, date/time, location, prices,
  seating plan, product quotas and check-in rules ([event series](https://docs.pretix.eu/guides/event-series/)). pretix
  recommends separate events for multi-day festivals.
- **Item** (a ticket or merchandise), **Variation** (e.g. a T-shirt size), **Category**; add-on categories (buyable
  only as add-ons); bundles ("only sell as part of a bundle"); "only show after sellout of X"; an availability
  window; voucher-only; "require a valid membership" ([products](https://docs.pretix.eu/guides/products/)).
- **Quota**, the central capacity concept, **decoupled from items.** An item can be in several quotas and is buyable
  only when all of them have room; several items can share one ("If multiple items are assigned to the same quota,
  the quota will be counted as sold out" once their combined count reaches the limit). Availability = size − paid
  orders and pending orders still inside their payment window − unexpired cart positions − quota-blocking vouchers −
  people on the waiting list. Every product must be in at least one quota to be visible; a quota can close
  permanently once sold out.
- **Cart reservation:** "pretix' built-in checkout automatically reserves tickets in a user's cart for a configurable
  amount of time". Order creation can `consume_carts`, so "the quota reserved by the cart will be credited towards the
  order", atomically ([custom checkout](https://docs.pretix.eu/dev/api/guides/custom_checkout.html)). Default period
  30 minutes [unverified].
- **Two-stage reservation:** a cart hold, then a pending order that holds quota until its payment deadline.
- **Voucher:** a single-use grant that lowers the price, unlocks a hidden product, or reserves ("blocks") quota.
- **Waiting list:** an entry stores item, variation, sub-event, email, locale and `voucher`; when quota frees up, a
  voucher is sent and the entry stops waiting ([waiting list](https://docs.pretix.eu/dev/api/resources/waitinglist.html)).
  Automatic re-allocation of freed tickets is advertised ([railway.com](https://railway.com/deploy/pretix)).
- **Order:** statuses pending, paid, expired, canceled; one **position** per ticket, with attendee data from
  **Questions**. Approval is a separate flow (`require_approval`).
- **Seating:** each seat has `seat_guid`, `zone_name`, `row_name`, `row_label`, `seat_number`, `seat_label`, `blocked`,
  a mapped product (the seat category maps to an item), and a pointer to its holder: `cartposition`, `orderposition` or
  `voucher` ([seats](https://docs.pretix.eu/dev/api/resources/seats.html)). The plan is a `layout` JSON whose schema
  "still evolves" ([seating plans](https://docs.pretix.eu/dev/api/resources/seatingplans.html)).
- Check-in lists with rules, and ticket output (PDF, passbook) through plugins [unverified in fetched pages].
- **Takeaway:** pretix separates what is sold (item, variation) from how much may be sold (quota). Seats are a second,
  finer inventory layer under the quota.

| Platform | Capacity model | Hold / reservation | Notable | Sources |
|---|---|---|---|---|
| Medusa.js v2 (TypeScript, modular) | `InventoryItem` (with `requires_shipping`, false for tickets or licences); `InventoryLevel` per location holds `stocked_quantity`, `reserved_quantity`, `incoming_quantity` | A `ReservationItem` per item **at order placement**, not at cart. The docs give the ticket case as a custom use: "a ticket seller could reserve a ticket when a customer selects it, then delete the reservation if checkout isn't completed in time" | 22 independent commerce modules (API Key, Auth, Cart, Currency, Customer, Fulfillment, Inventory, Loyalty, Order, Payment, Pricing, Product, Promotion, Region, Sales Channel, Settings, Stock Location, Store, Store Credit, Tax, Translation, User) that do not reference each other; links and workflows compose them | [modules](https://docs.medusajs.com/resources/commerce-modules) · [inventory concepts](https://docs.medusajs.com/resources/commerce-modules/inventory/concepts) |
| Vendure (TypeScript/NestJS) | `stockOnHand`, `allocated`, `outOfStockThreshold`; saleable = stockOnHand − allocated − outOfStockThreshold; adding a variant with saleable 0 throws `InsufficientStockError`; a negative threshold enables backorders | **Pluggable** `StockAllocationStrategy`; the default allocates on `PaymentAuthorized` / `PaymentSettled` ("fix on payment"): the closest precedent for a configuration-selected policy | `StockMovement` types Allocation, Sale (at fulfilment), Cancellation, Release; `StockLocation` + `StockLocationStrategy` | [stock control](https://docs.vendure.io/guides/core-concepts/stock-control/) |
| Saleor (Python/GraphQL) | A per-channel allocation strategy picks the warehouse | Optional checkout reservations, enabled separately for authenticated and anonymous checkouts; without them the first checkout to complete wins and the others fail with `INSUFFICIENT_STOCK`; `Checkout.stockReservationExpires` | Order expiration (`expireOrdersAfter`, minutes) releases unpaid orders' allocations: a second timer, like pretix's payment window | [reservation](https://docs.saleor.io/developer/stock/stock-reservation) · [checkout stock](https://docs.saleor.io/developer/checkout/stock) · [order expiration](https://docs.saleor.io/developer/checkout/order-expiration) · [allocation](https://docs.saleor.io/developer/stock/stock-allocation) |
| Shopify | Per location: Available, Committed (placed, unfulfilled order), Unavailable (draft orders, pickups, damaged, safety stock, QC), Incoming; On hand is their sum | Draft orders can reserve units for a limited period; storefront checkout does not hold stock by default [unverified] | — | [inventory states](https://help.shopify.com/en/manual/products/inventory/managing-inventory-quantities/inventory-states) · [inventory apps](https://shopify.dev/docs/apps/fulfillment/inventory-management-apps) |
| commercetools | — | Cart `inventoryMode: ReserveOnCart`, per-SKU `reservationExpirationInMinutes`, a project-level default, a cart action that extends the reservation, warnings when a reservation cannot be made | Hold timing is configuration per entity, not code | [reserve stock on cart](https://docs.commercetools.com/tutorials/reserve-stock-on-cart) |
| nopCommerce (.NET) | Product-level min/max cart quantity; stock by product or by attribute combination; backorder modes; multi-warehouse | — | Plugin architecture for payment, shipping and tax: the most relevant .NET precedent for "plugins per provider type" [unverified; general knowledge] | — |
| Eventbrite | **Ticket classes** under **inventory tiers**, a shared-capacity layer between event and class (e.g. a GA tier of 200 shared by presale and main-sale classes); a class `capacity` of null/0 means limited only by tier and event; add-on tiers set `count_against_event_capacity=false`; `seatmap_number` 0 = GA, 1 = reserved | Parent tiers carry child **hold** tiers | The same idea as pretix's shared quota; field details came via third-party mirrors [unverified] | [ticket classes](https://www.eventbrite.com/platform/docs/ticket-classes) |
| Ticketmaster | Per-event ticket limits [unverified] | Smart Queue [unverified] | **Verified Fan**: pre-registration, scoring, then a unique code and a personal purchase window. **SafeTix**: an encrypted rotating barcode tied to the account, transfers only on-platform. Face Value Exchange [unverified] | [PYMNTS](https://www.pymnts.com/fraud-prevention/2019/how-ticketmaster-rewards-fans-bars-fraudsters-security/) · [SafeTix](https://business.ticketmaster.com/safetix/) · [404 Media](https://www.404media.co/ticketmaster-used-revolving-barcodes-to-control-ticket-resale-market-and-surveil-customers-doj-alleges/) |
| seats.io (seat-map SaaS) | **Chart** (the designed venue) ≠ **Event** (a chart instance with object statuses `free`, `reservedByToken`, `booked`) | Hold tokens default to 15 minutes, can be set per token (`expiresInMinutes`), and release all held objects on expiry; the hold timer starts when the chart renders, not at selection; booking passes the same token | Selection validator `noOrphanSeats` with a `lenient` mode. Categories, GA areas with capacity, tables, booths, sections, channels, best available, seasons, accessible seats [unverified; not re-fetched] | [hold tokens](https://docs.seats.io/docs/api/hold-tokens) · [session config](https://docs.seats.io/docs/renderer/config-session) · [temporarily hold](https://docs.seats.io/docs/api/temporarily-hold-objects) · [selection validators](https://docs.seats.io/docs/renderer/config-selectionvalidators) |

### 5.2 Waiting rooms

| Product | Behaviour | Sources |
|---|---|---|
| Cloudflare Waiting Room | Activates when traffic nears a threshold. Two knobs: total active users and new users per minute; session duration = minutes a user stays active after their last request. A cookie holds the position (with a first-arrival timestamp); admission is FIFO and the page auto-refreshes every 20 s; decisions are made at the edge from slots allocated from global state updated every few seconds. Queueing methods: FIFO, random, passthrough. Event scheduling adds a pre-queue with an optional shuffle at event start | [about](https://developers.cloudflare.com/waiting-room/about/) · [queueing methods](https://developers.cloudflare.com/waiting-room/reference/queueing-methods/) · [event scheduling](https://blog.cloudflare.com/waiting-room-event-scheduling/) |
| Queue-it, scheduled mode | A pre-queue with a countdown: early arrivals get **no position** and are randomised, like a raffle, at sale start; later arrivals are FIFO. This stops the "arrive earlier and earlier" race and removes the advantage of bots arriving seconds early | [pre-queue](https://queue-it.com/pre-queue/) · [FIFO vs randomization](https://www.queue-it.com/blog/first-in-first-out-randomization) · [scheduled](https://queue-it.com/product/scheduled/) · [ramint.gov.au FAQ](https://ramint.gov.au/about-us/frequently-asked-questions/faqs-purchases/queue-it) |
| Queue-it, safety-net mode | Activates only above a traffic threshold; FIFO | [how Queue-it works](https://queue-it.com/developers/how-queue-it-works) |
| Queue-it, position and exit | The position survives a refresh through a cookie bound to the queue ID ("if the end user closes their browser and later reopens…"). On exit the user carries a signed `queueittoken` (`q` queue id, `ts` validity, `h` HMAC with a shared secret); the server-side KnownUser connector validates it and sets a session cookie; no token and no cookie means a redirect to the queue | [cookie statement](https://queue-it.com/cookie-statement-user) · [KnownUser PHP mirror](https://github.com/sarooghi/KnownUser.V3.PHP) · [KnownUser ASP.NET mirror](https://github.com/sarooghi/KnownUser.V3.ASPNET) |
| NetFunnel | A "position retention" window, after which the visitor gets a new key at the back | [position retention](https://docs.stclab.com/products/netfunnel/user-guide/segment/section-control-segment/advanced-queue-position-retention) |
| Bot measures at the queue | Data-centre traffic rejection, CAPTCHA/Turnstile at queue entry, one queue id per verified account or phone, a token bound to the session (HMAC over queue id + expiry), an invisible pre-queue position | [Queue-Fair](https://Queue-Fair.com/beating-sneaker-bots-ticket-bots-scalpers-and-touts) + the Queue-it pages above |

Admission model sketched for a Spark built-in (inference): a `QueueTicket` document per visitor (`q` id, `joinedAt`,
`randomRank` for a pre-queue, `admittedAt`, `admissionExpiresAt`), a signed cookie carrying the id (the position is
server state, not cookie state, and survives a refresh), and an admitter (cron, every few seconds) that admits
`min(ratePerMinute·Δt, targetActive − currentActive)` lowest-ranked tickets; `currentActive` = admitted tickets with
recent activity (session-duration semantics).

Interaction with holds (inference):
- Admission is a **right to try**, not a reservation; allocation still goes through the atomic guard.
- The admission window must be at least the hold TTL plus the payment window, or admitted users are evicted
  mid-checkout.
- Tie the active-user target to remaining capacity: admit about remaining capacity ÷ average quantity ×
  (1 + expected abandonment). Close the queue (show "sold out") when available + held = 0, but keep it waiting while
  holds exist, because expiring holds release stock.
- Enforce the per-buyer maximum at allocation (compare-exchange `limit/{event}/{type}/{buyer}`); the queue's
  one-entry-per-account rule is the bot-resistant identity it hangs on.
- Require the queue id on the hold API, server-side, not only at the page edge.

### 5.3 The module split all platforms share

Catalog (product, variant, pricing) · Inventory (levels, reservations, locations) · Cart/Checkout · Order · Payment
(provider plug-ins) · Fulfilment/Delivery (provider plug-ins) · Promotion/Voucher · Tax · Customer/Sales channel.
Ticketing adds Event/Occurrence · Venue/Seat map · Quota (shared capacity) · Ticket issuance/Check-in · Waiting list ·
Waiting room.

### 5.4 Where webshop and ticketing overlap

| Concern | Shared core | Ticket-specific | Shop-specific |
|---|---|---|---|
| What is sold | Offer/Product + price + sale window + per-order min/max | Ticket type bound to an **occurrence**; seat category | Variants/attributes (size, colour), SKU, weight |
| Capacity | **Inventory pool** (a finite counter) + **reservations with expiry** | Shared quota across ticket types; per-seat inventory; GA areas | Multi-location stock, incoming stock, backorders |
| Hold timing | **Allocation policy**: hold on add / hold on checkout / fix on payment, TTL, extension | Usually hold on select (seats must be) | Usually fix on payment |
| Cart/checkout | Cart, line items, customer or guest, totals, promotions/vouchers | Attendee data per ticket ("questions"), seat selection | Shipping address and method |
| Order | Order, lines, status machine, payment deadline, expiry | One position/ticket per unit | Partial fulfilment, split shipments |
| Payment | Provider abstraction, intents, webhooks, refunds | — | — |
| After sale | Refund, cancellation, invoice | **Ticket issuance** (PDF/QR/wallet), **check-in**, **transfer/name change**, resale exchange, **event cancellation → mass refund** | **Shipping/fulfilment**, tracking, **returns/RMA**, restocking |
| Access control | Sale start/end, voucher-gated, member-gated | **Countdown + waiting room**, presale codes, verified-fan-style registration | Drop launches use the same waiting room (rarely) |
| Tax | VAT rate per offer, B2B reverse charge, invoices | Admission VAT: place of supply where the event takes place (§7.6) | Distance-selling OSS, destination VAT |

Conclusion (inference): the inventory → reservation → cart → order → payment chain is genuinely common. Webshop and
ticketing differ only in (a) when the hold happens and how long it lasts, (b) whether a unit is fungible (a counter) or
identified (a seat), and (c) what happens after payment (ship versus issue a ticket). Each is a strategy, so it can be
configuration rather than separate code.

### 5.5 Options the issue's list missed

Grouped, with the platform that proves the need:

**Capacity and limits**
1. **A shared quota across ticket types** (pretix quota, Eventbrite inventory tier): "Early bird" and "Regular"
   together ≤ 500 standing. Without it, a per-ticket-type maximum cannot express venue capacity.
2. **An item in several quotas** (pretix): block capacity *and* whole-event capacity.
3. **Per-order min/max** (pretix, nopCommerce) **and a per-customer maximum across orders** (anti-scalping), which
   needs an identity: email, account or payment fingerprint.
4. **Two-stage reservation:** a cart TTL, then a pending-order payment deadline (pretix payment window, Saleor
   `expireOrdersAfter`). "Complete the purchase within X minutes" covers only the first; bank transfer or invoice
   payment needs days.
5. **Hold extension** (commercetools) and a visible countdown (Saleor `stockReservationExpires`).
6. **Quota-blocking vouchers and organiser holds/kills**: seats held back for press or artists (Eventbrite hold tiers,
   pretix blocked seats).
7. **Release semantics:** a freed unit goes to the waiting list first, not to the open market.

**Access and fairness**
8. **Vouchers and presale codes** (discount, unlock a hidden product, reserve quota).
9. **A waiting list on sell-out** with automatic voucher hand-out (pretix).
10. **Waiting-room specifics:** randomise the pre-queue at the start time (Queue-it scheduled); a signed exit token
    validated server-side; a cap on concurrent shoppers inside; a position-retention window; one queue slot per
    identity.
11. **Bot and scalper protection:** CAPTCHA or proof-of-work at queue entry, rate limits, Verified-Fan-style
    registration, personalised tickets, transfer restrictions (§6).
12. **Sale phases:** presale → general sale, each with its own window, price and quota (Eventbrite classes in one
    tier).
13. **Approval-required orders** (pretix `require_approval`), useful for B2B.

**Seating**
14. **No orphan seats** (seats.io `noOrphanSeats`, lenient mode), adjacency, "best available N together".
15. **Accessible seats** (wheelchair + companion) as a category with its own rules.
16. **GA areas inside a seated plan:** a standing block is a capacity counter, not seats.
17. **Restricted-view flags**, blocked seats, sales channels per seat.

**Events**
18. **Multiple occurrences** of one event (pretix sub-events), each with its own quotas and seat-map instance;
    separate the **chart** (venue design) from the **event instance** (statuses), as seats.io does.
19. **Event cancellation or postponement:** bulk refund or voucher, notification, ticket re-validation.

**After sale**
20. **Ticket issuance:** a unique secret per ticket, QR, PDF/Apple/Google Wallet, re-issue on a name change
    (invalidating the old secret), optionally a rotating code (SafeTix).
21. **Check-in:** a scanner app or API, offline lists, check-in lists per gate or day, double-scan detection.
22. **Transfer or resale** at face value (optional).
23. **Refunds:** partial and full, refund-policy windows, fees.

**Money and legal**
24. **Currency** per seller or sale; prices as decimal + ISO currency; no FX in v1.
25. **EU VAT:** a rate per offer; place of supply for admission; B2B VAT-ID capture and reverse charge; **invoice
    numbering** (sequential, per seller in a B2B2C marketplace); credit notes on refunds.
26. **Marketplace payouts** (Stripe Connect / Mollie Connect), mandatory when sellers create their own accounts and
    sales.
27. **Guest checkout** (pretix buyers need no account), with order access through a signed link.
28. **GDPR:** attendee data minimisation, retention and erasure of attendee PII after the event, marketing consent,
    data export.
29. **Idempotency** of payment webhooks and order placement (double clicks, provider retries).

Probably irrelevant for v1: multi-warehouse stock locations, incoming stock, backorders, loyalty, store credit, dynamic
pricing, 3D seat views, season tickets, a resale marketplace.

### 5.6 Seat maps: designer versus rough sketch

- **Commercial designers:** seats.io and Seatmap.pro (visual editor, WebGL renderer, SDK; Seatmap.pro from €100/month
  in the cloud, about €20k/year on-premises: [Seatmap.pro vs seats.io](https://seatmap.pro/blog/seatmap-pro-vs-seats-io/);
  orphan-seat prevention in release 1.72: [release notes](https://seatmap.pro/knowledge-base/releases/1.72.0/)). Both
  are rich and expensive, and seat-level drawing is a large project.
- **Open source:** no mature, permissively licensed **editor** was found.
  - Viewers and pickers: seatmap-canvas ([npm](https://www.npmjs.org/package/@alisaitteke/seatmap-canvas-react), open
    source, selection only), react-seat-picker (a fork of react-seatmap), seatchart.js, Ticket Evolution
    seatmaps-client ([GitHub](https://github.com/ticketevolution/seatmaps-client), a viewer).
  - Editor-like: `seat-picker` on npm (a drag-and-drop canvas of seats, shapes and text; licence not confirmed:
    [npm](https://npmjs.com/package/seat-picker)); venue_seat_picker (Flutter, grid editor:
    [pub.dev](https://pub.dev/packages/venue_seat_picker)).
  - Building blocks: Konva, Fabric.js or plain SVG
    ([Seatmap.pro guide](https://seatmap.pro/blog/how-to-build-a-javascript-seating-chart/)).
  - The pretix seating-plan editor is not part of the open-source core [unverified]; only its `layout` JSON is exposed
    through the API.
- **A minimal representation** for a block-based sketch:

```
VenueChart (reusable per arena)
  canvas: width, height, optional background image (stage/field sketch)
  blocks[]:
    id, label, polygon: [[x,y],...]  (or rect + rotation), color
    kind: Standing | Seated
    Standing: capacity (int)                       -> one inventory pool
    Seated:   rows[]: { label, seats: [{ number, label?, accessible?, blocked?, restrictedView? }] }
              optional generator: rowCount, seatsPerRow, numbering (L→R, odd/even), curvature
              -> seats are NOT drawn individually in v1; rendered as a grid inside the polygon
    category (price tier key)
  stage/annotations: polygons + text
EventInstance (per occurrence)
  chartRef + snapshot/version (the chart may change later; sold seats must not move)
  per-seat status: free | held(holdId, expiresAt) | sold(orderLineId) | blocked
  per-standing-block counters: capacity, held, sold
```

- Design notes (inference): an SVG polygon editor (drag vertices, a block property panel, a "generate rows" helper)
  is a few hundred lines of plain SVG + Angular CDK drag; no canvas library is needed at that scale. Keep **chart ≠
  event instance** (seats.io) and version or snapshot the chart when a sale opens. **A block is an inventory pool:** a
  standing block is a counter, a seated block a set of identified units; both feed the same reservation engine, so
  "GA versus seated" is a pool type, not a subsystem. The orphan-seat rule is a server-side validator over a row's seat
  sequence, enforced on the server and not only in the UI (as Seatmap.pro does).

### 5.7 Decomposition alternatives considered

Constraint from the owner: webshop versus ticketing **behaviour** is chosen by .NET configuration, not by different
code; several small, single-purpose libraries are preferred.

| Option | Pros | Cons |
|---|---|---|
| A. One monolithic plugin, config-selected policy | One reference; the simplest story | A webshop drags in venue editors, waiting rooms, QR and check-in; indexes and security.json resources for unused features; one release cadence; against the composition model (unused library references are trimmed) and the owner's "don't couple unrelated features" preference |
| B. A shared core + Webshop and Ticketing "product" layers | Clear vertical stories | The layers re-implement the same policies and the behaviour split ends up in code, contradicting the constraint |
| C. Small capability packages; allocation behaviour is configuration (recommended by the research) | Each package owns one concept; hosts compose them; policy (hold timing, TTL, limits, queue on/off) is `IOptions<>` configuration consumed by core strategy interfaces, as Vendure's `StockAllocationStrategy` and commercetools' per-entity reservation expiry show | More packages to version and wire |

The research's package map for option C (names superseded by the PRD; dependencies as proposed):

| Package | Purpose | Depends on |
|---|---|---|
| Commerce abstractions | Money/Currency, Offer, customer reference, `IAllocationPolicy`, `IPaymentProvider`, `IDeliveryProvider`, events | Spark.Abstractions |
| Inventory | Pools (counter or identified units), shared quotas (a unit in N pools), reservations with TTL/extend/release, a sweeper, per-order and per-customer limits, clamp-to-availability | Commerce abstractions |
| Catalog | Offer/Product, variants, price, sale window and phases, seller ownership | Commerce abstractions |
| Checkout | Cart, line items, guest checkout, the allocation-policy pipeline, order + state machine, payment deadline/expiry, idempotency | Inventory, Catalog |
| Payments (+ Stripe, Mollie) | Payment intents, webhooks, refunds, marketplace payouts and fees | Commerce abstractions |
| Promotions | Vouchers and presale codes: discount, unlock, quota-blocking | Inventory, Catalog |
| Invoicing | VAT rates, place-of-supply rule, VAT-ID / reverse charge, sequential invoice numbers per seller, credit notes | Checkout |
| Waiting list | Sold-out sign-ups and an automatic voucher on release | Inventory, Promotions |
| Waiting room | Countdown, pre-queue randomisation, position kept on refresh (signed cookie), throughput admission, signed exit token, bot-gate hook; **independent of commerce**, it can guard any route | Spark.Abstractions only |
| Events | Event, Occurrence (series), sale binding per occurrence | Catalog, Inventory |
| Venues | Venue chart, blocks, categories, chart versioning; maps blocks/seats to inventory pools per occurrence; orphan-seat and adjacency validators | Events, Inventory |
| Tickets | Issuance (secret, QR, PDF/wallet), re-issue, transfer, check-in API and lists | Checkout, Events |
| Shipping | Delivery methods, address, shipment tracking, returns | Checkout |
| ng-spark entry points | Angular UI per package as secondary entry points of `@mintplayer/ng-spark` (the existing `/auth/*` pattern) | — |

A webshop host would reference Catalog, Inventory, Checkout, Payments (+ a provider), Invoicing and Shipping, with
Promotions, Waiting list and Waiting room optional; a ticketing host adds Events, Venues, Tickets, Waiting room,
Promotions and Waiting list. The same reservation engine runs in both; only `IOptions` values and the referenced
packages differ. Seats, QR and check-in are add-on packages rather than switches, because a webshop host should not
carry their indexes or security.json resources (inference).

---

## 6. Bot mitigation

### 6.1 Threat model

**Who attacks, and how much it pays.**

| Case | What the evidence shows | Source |
|---|---|---|
| NY Attorney General, *Obstructed View* (2016) | One broker bought **1,012 U2 tickets at Madison Square Garden within one minute** of the on-sale against a 4-per-customer limit (Prestige Entertainment, 2014; it paid $3.35M of $4.19M in 2017 settlements). Brokers used many IPs and bots that bypass CAPTCHA | [NY AG report](https://ag.ny.gov/sites/default/files/reports/Ticket_Sales_Report.pdf) · [ABA Journal](https://www.abajournal.com/news/article/new_york_ag_says_ticket_sales_for_concerts_sporting_events_is_a_fixed_game) · [settlements](https://indiantime.net/story/2017/05/18/news/nys-ag-announces-419-million-in-settlements-with-6-companies-illegally-purchasing-and-reselling-concert-tickets/24683.html) |
| FTC, first BOTS Act cases (22 Jan 2021) | 3 New York brokers, **> 150,000 tickets**; bots to search and reserve, IP concealment, **hundreds of fictitious accounts and credit cards**; > $31M in penalties, $3.7M paid (the rest suspended for inability to pay) | [FTC 2021](https://www.ftc.gov/news-events/news/press-releases/2021/01/ftc-brings-first-ever-cases-under-bots-act) |
| FTC v. Key Investment Group (18 Aug 2025) | **≥ 379,776 tickets in just over a year for about $57M**, part resold for about $64M; Eras Tour: **49 accounts → 273 tickets** against a 6-per-event limit; thousands of accounts (fake and bought), thousands of virtual and traditional cards, proxied or spoofed IPs, **SIM boxes** to receive verification texts. Ticketmaster's defences were account verification, unique cards and IPs, and sometimes SMS. KIG denies using bots | [FTC 2025](https://www.ftc.gov/news-events/news/press-releases/2025/08/ftc-takes-action-against-ticket-resellers-using-illegal-tactics-bypass-ticket-limit-protections) · [Relix](https://relix.com/news/detail/ftc-files-bots-act-suit-against-key-investment-group/) |
| FTC + 7 states v. Live Nation / Ticketmaster (Sept 2025) | Alleges Ticketmaster let brokers use multiple accounts around posted limits ("turn a blind eye as a matter of policy"), resold those tickets and misrepresented limits; BOTS Act and deception counts; active in 2026 | [FTC complaint release](https://www.ftc.gov/news-events/news/press-releases/2025/09/ftc-sues-live-nation-ticketmaster-engaging-illegal-ticket-resale-tactics-deceiving-artists-consumers) · [FTC testimony 2026](https://www.ftc.gov/system/files/ftc_gov/pdf/p994811-ftc-oversight-testimony-senate-commerce-committee-2026.pdf) |
| Eras Tour presale (Nov 2022) | Verified Fan: 3.5M registered, 1.5M invited, 2M wait-listed; still **3.5 billion system requests, 4× Ticketmaster's previous peak**, attributed to bots and fans without codes | [Ticketmaster](https://business.ticketmaster.com/?p=51362) |
| Oasis reunion (Aug 2024), vendor analysis | First underground post 3 minutes after the announcement; **site modules ready within 36 h**; "queue bypass modules"; fake accounts "flooded the queues"; **automated ballot entry**; one group up to 1,500 tickets (est. £568k profit at £221 face → £600 resale); a lone attacker 230 tickets | [Netacea](https://netacea.com/blog/how-scalper-bots-evaded-detection-to-snatch-oasis-tickets) |
| Sneaker drop (June 2023) | **2,600 accounts created over three weeks from 1,400 IPs / 466 ASNs**; **44M add-to-cart requests in hours** from 25,000 IPs; direct POSTs to known URLs with no referrer; about 41,000 shipping-address changes; carding (278,000 checkout attempts); $1.1–2M profit from one drop; bots 99.8% of traffic | [F5 Labs](https://www.f5.com/labs/articles/threat-intelligence/how-a-sneaker-bot-earned-2m-profit-from-one-shoe-drop) |
| Account market (Mar 2023) | **Phone-verified accounts: 100 for $499 (about $5 each); "aged" accounts $9–20**; a $149/month broker forum required code sales to include the whole account, to move "non-transferable" tickets | [Vice](https://www.vice.com/en/article/the-cure-tried-to-stop-scalpers-brokers-are-selling-entire-ticketmaster-accounts-instead/) |
| Code market (2017) | About 75 eBay sales of Verified Fan codes or accounts at $10–$1,299 | [TicketNews](https://www.ticketnews.com/2017/12/verified-fan-creates-new-marketplace-presale-password-resale/) |
| Tooling | Cook groups about $10–50/month; bot licences $300–5,000+/year [unverified: vendor and commercial blogs] | [Netacea](https://netacea.com/?p=2825) · [PageCrawl](https://pagecrawl.io/blog/what-is-a-bot-sneaker-bots-explained.md) |

**What a fake identity costs.**

| Ingredient | Approximate attacker cost | Source |
|---|---|---|
| A new visitor cookie or incognito window | About 0. A Queue-it deployment notes users "can join multiple queues by opening the registration link in multiple incognito windows" | [Active Network](https://support.activenetwork.com/camps/articles/en_US/Article/Queue-It-For-High-Traffic-Times) |
| An email address | About 0: Gmail delivers every dotted variant to one inbox; catch-all domains give unlimited addresses | [Gmail help](https://support.google.com/mail/answer/7436150?hl=en) |
| A proof-of-work solve | Negligible: Anubis' default (about 2^16 SHA-256) is about 30 ms for a free-tier VM doing about 2^21 hashes/s; a week of all 11,508 known Anubis sites ≈ 6 minutes of compute | [Ormandy on Anubis](https://lock.cmpxchg8b.com/anubis.html) |
| A CAPTCHA solve | About $0.6–3 per 1,000 for reCAPTCHA v2/hCaptcha, $5+ per 1,000 for v3/Enterprise (comparison site); Turnstile about $0.002 per solve (seller estimate) | [AIMultiple](https://research.aimultiple.com/captcha-solving-services) · [dev.to](https://dev.to/ren_joyce_cd41204d5cb261f/how-to-bypass-cloudflare-turnstile-captcha-best-solver-43jo) |
| A residential IP | $1.5–15/GB; Bright Data $8.40/GB at 10 GB down to $3.30/GB at 10 TB | [AIMultiple proxy pricing](https://research.aimultiple.com/proxy-pricing) |
| A phone-verified account | About $5 | Vice (above) |
| An aged account with history | $9–20 | Vice (above) |
| A distinct card number | Low: KIG used "thousands of virtual and traditional card numbers" | FTC (above) |
| A real person who shows ID at the gate | The whole resale margin | (inference) |

**Break-even sketch (inference, illustrative numbers).** A ballot for 50,000 seats, a limit of 4 per buyer, 500,000
registrations → about 12,500 winning entries, p(win) ≈ 2.5%. A broker with 2,000 phone-verified accounts ($10,000 at
about $5 each, plus proxies) wins about 50 entries = 200 tickets; at a €150 resale margin that is about €30,000, a 3×
return. Card-fingerprint de-duplication raises the cost (2,000 distinct cards), but virtual cards are cheap. **Named
tickets with an ID check drive the margin to about 0 and the return negative.** Identity-cost measures move the
break-even; only resale control removes it.

**Capabilities in 2025–2026.**
- **Headless browsers with stealth.** Since 2022 headless Chrome's fingerprint is close to regular Chrome's; Castle
  (June 2025) calls subtle-API-discrepancy detection "now largely ineffective". Detection moved to CDP side effects
  (`Runtime.enable`, serialization hooks); tooling answered by patching or avoiding CDP (Patchright, rebrowser,
  nodriver, selenium-driverless; Camoufox patches Firefox at engine level). The cycle is measured in **weeks**;
  puppeteer-extra-stealth has been unmaintained since 2023 (commercial sources)
  ([Castle](https://blog.castle.io/from-puppeteer-stealth-to-nodriver-how-anti-detect-frameworks-evolved-to-evade-bot-detection/),
  [Scrapfly](https://scrapfly.io/blog/posts/best-stealth-browsers), [Clawbrowser](https://clawbrowser.ai/blog/puppeteer-extra-stealth-is-dead/)).
- **HTTP clients with browser TLS.** `curl_cffi` / curl-impersonate reproduce Chrome, Firefox and Safari TLS
  fingerprints, and accept custom `ja3=` / `akamai=` strings ([curl_cffi](https://curl-cffi.readthedocs.io/en/v0.11.4/impersonate.html)).
- **Residential proxies.** Imperva 2025 (vendor report): automated traffic was 51% of the web in 2024, bad bots 37%,
  residential proxies among the most common evasions, API-directed attacks 44% of advanced bot traffic. Google
  disrupted IPIDEA in January 2026, a network advertising 6.1M daily IPs; others remain
  ([Imperva](https://www.imperva.com/blog/2025-imperva-bad-bot-report-how-ai-is-supercharging-the-bot-threat/),
  [The Hacker News](https://thehackernews.com/2026/01/google-disrupts-ipidea-one-of-worlds.html)).
- **Account farms.** Accounts are created slowly ahead of time from many ASNs (F5), bought aged or phone-verified
  (Vice), and SMS checks are beaten by SIM boxes (FTC, KIG).
- **CAPTCHA solving, human and ML.** ETH Zürich solved 100% of reCAPTCHA v2 with YOLO models, and bots needed no more
  challenges than humans ([arXiv 2409.08831](https://arxiv.org/abs/2409.08831)). ChatGPT Agent clicked Turnstile's "I
  am not a robot" without a harder challenge (reported July 2025,
  [Yahoo Tech](https://tech.yahoo.com/ai/articles/chatgpt-passes-m-not-robot-140447528.html)).
- **AI agents that identify themselves.** Web Bot Auth (HTTP Message Signatures, RFC 9421) and Visa's Trusted Agent
  Protocol / Mastercard Agent Pay (Oct 2025) let legitimate agents sign requests; unsigned agents still look human
  ([Cloudflare](https://blog.cloudflare.com/secure-agentic-commerce/)).
- **Timing at sale start.** Under FIFO the first spot is "a race over milliseconds" (Queue-it); bots poll up to 30
  minutes early and POST to add-to-cart URLs learned in advance (F5); "queue bypass modules" target sites whose queue is
  not enforced server-side (Netacea).
- **Queue farming** with multiple tabs, devices and identities: incognito windows give more entries (Active Network);
  fake accounts flood queues and ballots (Netacea).
- **Selling positions, codes and admission.** Codes and whole accounts are sold (TicketNews, Vice). Ticketmaster
  SafeTix rotating barcodes (refreshed every 15 s) were **reverse-engineered**: the per-ticket token was extracted and
  tickets regenerated on scalper infrastructure, scanning as genuine (AXS lawsuit, July 2024;
  [Digital Music News](https://www.digitalmusicnews.com/2024/07/11/ticketmaster-tickets-reverse-engineered/)).
- **Scripted checkout:** pre-filled profiles, pre-logged-in sessions, card testing, address changes (F5).
- **Hold-and-release griefing:** OWASP OAT-021 *Denial of Inventory* ("Selection and holding of items from a limited
  inventory … never actually bought"; also hoarding, seat spinning, inventory exhaustion), distinct from OAT-005
  *Scalping* ([OAT-021](https://owasp.github.io/www-project-automated-threats-to-web-applications/assets/oats/EN/OAT-021_Denial_of_Inventory.html),
  [OAT-005](https://owasp.org/www-project-automated-threats-to-web-applications/assets/oats/EN/OAT-005_Scalping)).

**What follows for the design (inference).** Identity is cheap at every layer except a physical person at the gate.
Detection is probabilistic and decays in weeks, and anything in open-source client code is read by the adversary. The
professional adversary is a human-operated farm that CAPTCHA and proof-of-work do not touch. So deterministic
server-side rules and economic design come first; probabilistic signals only choose friction and never punish on
their own.

### 6.2 Why detection loses

- **The arms race runs in weeks.** Client fingerprinting has been "largely ineffective" since headless and headful
  Chrome converged (Castle, June 2025); bot developers shipped site-specific Oasis modules within 36 hours (Netacea).
- **Challenges are solved.** An ML pipeline solves 100% of reCAPTCHA v2 (ETH Zürich, 2024); farms sell every widget for
  fractions of a cent; ChatGPT Agent clicked through Turnstile (July 2025).
- **Signals are spoofable.** TLS fingerprints are reproduced by curl_cffi; Cloudflare itself says fingerprints "can be
  easily spoofed, they change frequently" (§6.4). `navigator.webdriver` and CDP leaks catch stock Playwright/Puppeteer
  only.
- **An open-source framework is the worst-placed party.** Proprietary JS-integrity and sensor scripts (Akamai, Kasada,
  DataDome) are obfuscated and rotated; a framework's "secret" sits in its repository (inference).
- **The most damaging adversary is not a bot.** The KIG complaint describes thousands of accounts and cards, proxies
  and SIM boxes beating account, card, IP and SMS checks; KIG says it uses human buyers. CAPTCHA and proof-of-work do
  nothing against that.
- **The industry is moving from detecting bots to authenticated clients and costly identities:** signed agents (Web
  Bot Auth, Visa TAP), device-bound cookies (DBSC), Turnstile Ephemeral IDs and Private Access Tokens, and Cloudflare's
  February 2026 Turnstile redesign ([Friendly Captcha on Turnstile](https://friendlycaptcha.com/insights/cloudflare-turnstile-accessibility/)).

### 6.3 What works, ranked

The research's ranking, strongest lever first:
1. **Remove the speed race:** a randomised pre-queue, or a registration-then-draw ballot.
2. **One queue entry per identity, and posted per-buyer limits enforced across linked identifiers** (account, visitor,
   card fingerprint, phone, normalised email and address).
3. **A server-enforced, signed, short-lived, single-use admission → hold → checkout chain**, so knowing the API URLs is
   worthless.
4. **Attack the resale value:** named tickets with an ID check, face-value-only platform resale. The only lever that
   defeats the professional.
5. **Review and cancel after the fact, with a human appeal.**

**Queue design.**

| Design | Against bots | Fairness / false positives | Cost | Research verdict |
|---|---|---|---|---|
| **FIFO from sale start** | The worst case. "FIFO gives unfair advantages to those using bots or with fast internet"; a FIFO queue that opens at 9:00 for a 10:00 sale moves the sale to 9:00 (Queue-it). Thundering herd at T0 | No false positives, but rewards fast connections | None | Opt-in only, for low-demand sales |
| **Randomised pre-queue** (Queue-it) | Early visitors wait "with no position"; at T0 "all visitors in the pre-queue are randomized, just like a raffle"; latecomers FIFO. Removes the speed edge, but each entry becomes a lottery ticket, so attackers switch to volume. Must pair with one entry per identity (Queue-it: "Limit one queue spot per person using email, login, or other identifiers"; its queue token's optional `k` key "stops users from issuing multiple queue IDs") | Arriving early gains nothing. Guests without sign-in can multiply entries with fresh cookies | Low | Default for scheduled sales; require sign-in on high-demand channels |
| **Ballot: register, then draw** (Verified Fan, Glastonbury) | Registration takes days, not seconds: time to de-duplicate across identifiers, take a card on file, verify phones and review manually, and no T0 herd (inference). Attacked by automated ballot entries (Netacea, Oasis), bought aged accounts (Vice) and resold codes (TicketNews). Glastonbury: per-person registration with a photo, tickets carry the photo, "strictly non-transferable", resale only at face value via See Tickets | Two-phase UX; the draw must be explained and auditable | Medium | The mode for demand ≫ supply (a 50,000-seat flash sale) |

Sources: [Queue-it on FIFO](https://www.queue-it.com/blog/first-in-first-out-randomization) ·
[Queue-it bot protection](https://queue-it.com/bot-detection-protection/) ·
[queue-token](https://socket.dev/npm/package/@queue-it/queue-token/overview/^1.0.4) ·
[Ticketmaster](https://business.ticketmaster.com/?p=51362) · [Glastonbury](https://glastonburyfestivals.co.uk/) ·
[Ticket Fairy case study](https://www.ticketfairy.com/blog/2025/07/11/case-study-glastonbury-festivals-ticket-registration-system/).

**Admission tokens, enforced server-side.** Queue-it's server connector exists so "end users cannot bypass the queue by
adding a server-side integration": it validates the token's timestamp and hash, otherwise sends the user back
([KnownUser ASP.NET](https://github.com/queueit/KnownUser.V3.ASPNET)). Its token format carries an optional token id
`ti` "to limit replay attacks" ([QueueToken](https://github.com/queueit/QueueToken.V1.JAVA)). Its product page lists
"Transaction Validation — confirm visitor has passed through queue before checkout via API". Netacea's queue-bypass
modules show what happens otherwise. F5's bots POSTed directly to add-to-cart; a hold endpoint that demands a valid
admission token makes known URLs worthless (inference).

**Binding.** Queue-it offers IP binding ("validate that the same visitor enters and exits the queue"). Mobile users
change IPs, CGNAT pools rotate, and iCloud Private Relay egress addresses "may change between browsing sessions"
([dev.to](https://dev.to/abdullah_afzal/detecting-icloud-private-relay-and-not-treating-it-like-a-vpn-1163)), so IP
binding punishes genuine mobile users (inference); bind to the account or the signed visitor id instead. Device Bound
Session Credentials (TPM-bound cookies) shipped in Chrome 146 on Windows in April 2026 and would make an admission
cookie non-exportable on that platform only
([The Hacker News](https://thehackernews.com/2026/04/google-rolls-out-dbsc-in-chrome-146-to.html),
[Google](https://blog.google/security/protecting-cookies-with-device-bound-session-credentials/)).

**One account, several devices.** Queue-it lets a visitor move their position from desktop to mobile
([how Queue-it works](https://www.queue-it.com/how-does-queue-it-work/)). Ticketmaster tells fans to use one browser,
tab and device, warning that multiple tabs "will cause errors" and that a manual refresh may get them flagged
([Ticketmaster, Sept 2025](https://ticketmaster.substack.com/p/how-a-ticketmaster-queue-actually)). For Spark (inference):
one entry per account, visible on every signed-in device, admission redeemable once.

**Identity cost.**

| Measure | What it costs a bot | Fairness / false positives | Spark today | Research verdict |
|---|---|---|---|---|
| Verified email | About 0 (dots, plus-addressing, catch-all domains) | Low friction | `RequireConfirmedEmail` exists | Hygiene, not a bot defence; normalise emails for limit keys |
| Phone (SMS) verification | About $5 per phone-verified account (Vice); SIM boxes (FTC, KIG). A line-type lookup can refuse VoIP numbers (Twilio Lookup). SMS-pumping fraud costs the merchant money | Excludes people without a mobile; W3C notes phone requirements raise privacy concerns | `PhoneNumber` / `PhoneNumberConfirmed` fields only; no SMS flow | Opt-in for high-demand channels; valuable as a de-duplication key, weak as a cost |
| Account age / purchase history | Aged accounts $9–20; preferring them created that market (Vice) | Punishes new genuine fans | No creation timestamp on `SparkUser` | A weak positive signal only |
| Passkeys (WebAuthn) | About 0 for account creation: Chromium, WebDriver and Playwright ship virtual authenticators; Apple's synced passkeys return no attestation (iOS 16+), so a relying party cannot tell a software authenticator. In Spark a passkey cannot even create an account | Great UX | Implemented on ASP.NET Identity | Recommended for genuine users' fast, phishing-proof sign-in at T0 and against takeover, not as a bot defence |
| Payment fingerprint, Stripe | One card per identity; details in §4.5 | Families sharing a card hit the per-payer limit, which is the published limit | No PSP integration yet | The strongest cheap de-dup key; the default when a PSP is configured |
| Payment fingerprint, Mollie | Known after payment, so over-limit orders are voided and refunded; details in §4.5 | As above | No PSP integration yet | The same, with post-payment enforcement |
| Delivery address / name | "Address jigging" defeats naive per-address limits; F5 calls address controls a modest hurdle | Shared households | None | Normalise addresses; use as a de-dup key with tolerance |
| PSD2 SCA / 3-D Secure (EU) | A bank-app approval per card payment raises scripted-checkout cost at scale (inference); exemptions exist | Friction for genuine buyers | None | Ask the PSP for a 3DS challenge on elevated risk instead of a CAPTCHA |

Sources: [Twilio Lookup](https://www.twilio.com/docs/lookup/v2-api) ·
[Twilio on phone-number fraud](https://www.twilio.com/en-us/blog/developers/best-practices/protect-twilio-account-phone-number-fraud) ·
[W3C, inaccessibility of CAPTCHA](https://www.w3.org/TR/turingtest/) · [Apple forums](https://developer.apple.com/forums/thread/726208) ·
[Corbado on attestation](https://www.corbado.com/faq/why-some-platforms-do-not-support-attestation-for-passkeys.md) ·
[Corbado on virtual authenticators](https://www.corbado.com/blog/passkeys-e2e-playwright-testing-webauthn-virtual-authenticator) ·
[CrowdHandler](https://crowdhandler.com/blog/the-rise-of-sneaker-bots-how-we-stop-them-and-how-you-can-too), plus the
Stripe and Mollie pages in §4.5.

**Economic measures.**

| Measure | Evidence | Limits | Research verdict |
|---|---|---|---|
| **Named tickets + ID check** | Glastonbury: photo on the ticket, only the pictured person admitted, non-transferable. Italy: nominative tickets for concerts in venues > 5,000 since 1 July 2019, ID at entry, face-value resale through official channels | Gate throughput, a name-change process, people without ID; ID data is sensitive | The strongest measure; opt-in per sale |
| **Platform-controlled resale at face value** | Glastonbury resells cancellations at face value via See Tickets; Belgium and Ireland ban above-face resale; the UK has drafted it | Needs a resale/exchange flow | A framework hook (exchange back to the platform at face value); the marketplace stays in the app |
| **Rotating barcodes / delayed delivery** | SafeTix refreshes every 15 s, yet the seed token was extracted and tickets regenerated off-platform (2024) | A web client cannot hide a seed | Do not sell rotation as a control; delayed delivery plus named tickets is more robust (inference) |
| **Cancel bot-attributed orders per the terms of service** | A 2018 Ticketmaster document reserved cancellation of orders exceeding limits, including orders linked by name, email, billing address or card. Oasis, Feb 2025: thousands cancelled for bot use or T&C breach; fans say they were wrongly targeted; appeals offered | False positives hit real fans; GDPR Art. 22 | Review tooling, bulk void/refund, notices and an appeal state; never fully automatic on probabilistic signals |

Sources: [Glastonbury](https://glastonburyfestivals.co.uk/) · [Il Post](https://www.ilpost.it/2019/06/25/biglietti-nominali-concerti/) ·
[TicketOne resale](https://www.ticketone.it/campaign/rivendita/) ·
[Digital Music News](https://www.digitalmusicnews.com/2024/07/11/ticketmaster-tickets-reverse-engineered/) ·
[Ticketmaster 2018 response](https://data.pollstar.com/chart/2018/10/Ticketmaster%20Response%20(Final)_680.pdf) ·
[Far Out](https://faroutmagazine.co.uk/oasis-tickets-cancelled-ticketmaster/).

### 6.4 Proof-of-work, Private Access Tokens, Turnstile modes

**Proof-of-work.**
- **ALTCHA:** MIT, self-hosted. The server signs a challenge with an HMAC key; the client brute-forces a number up to
  `maxNumber` (altcha-lib v1 default 1,000,000), with an `expires` embedded in the salt. Widget v3 adds PBKDF2, Argon2
  and scrypt (memory-bound, "to neutralize hardware acceleration"). ALTCHA's benchmark at complexity 100,000: **iPhone
  12 mini ≈ 0.83 s, Samsung Galaxy A14 ≈ 2.5 s** ([altcha-lib](https://github.com/altcha-org/altcha-lib),
  [altcha 3.2.3 README](https://cdn.jsdelivr.net/npm/altcha@3.2.3/README.md), [complexity](https://altcha.org/docs/complexity)).
- **Effectiveness.** Laurie & Clayton (2004): a PoW hard enough to deter spammers also blocks legitimate senders, so PoW
  alone fails; Liu & Camp (2006): it can work combined with reputation
  ([Laurie & Clayton](https://www.cl.cam.ac.uk/~rnc1/proofwork.pdf), [Liu & Camp](https://sites.cs.ucsb.edu/~rich/class/cs293b-cloud/papers/lui-pow.pdf)).
  Ormandy (2025) on Anubis: the cost to a well-funded bot is "literally nothing", while ordinary users on weak devices
  pay ([Anubis analysis](https://lock.cmpxchg8b.com/anubis.html)). Codeberg reported scrapers solving its Anubis
  challenges in August 2025 [unverified: details unclear] ([Wikipedia](https://en.wikipedia.org/wiki/Anubis_(software))).
  Queue-it's PoW (vendor): 11% of visitors failed it in a 2019 concert on-sale and it blocked 39% in a 2020 airline sale;
  "cannot use any scripting language to solve this puzzle" ([Queue-it PoW](https://queue-it.com/blog/proof-of-work-block-bad-bots/)).
  Kasada makes difficulty grow "exponentially" with abusive requests (vendor;
  [data sheet](https://get.kasada.io/hubfs/Kasada-Data-Sheet.pdf)).
- **Reading (inference):** at about €150 profit per ticket, a PoW worth tens of milliseconds on a server core (and
  about 2.5 s on a cheap phone) is no deterrent. It filters clients that do not run JavaScript, makes floods cost CPU,
  verifies with one HMAC and one hash, and involves no third party. That is its value.
- Other PoW options: **mCaptcha** (AGPL-3.0, separate server; difficulty scales with load to about 2 s "when under
  attack"; "active development" but no tagged release beyond v0.1.0, March 2024;
  [GitHub](https://github.com/mCaptcha/mCaptcha)); **Cap** (Apache-2.0, PoW plus instrumentation challenges;
  [Cap](https://trycap.dev/guide/alternatives/friendlycaptcha)); **Friendly Captcha** (hosted, German, PoW-based; EU
  endpoint on Advanced/Enterprise plans; [EU endpoint](https://developer.friendlycaptcha.com/docs/v2/guides/eu-endpoint)).

**Private Access Tokens / Privacy Pass.** Apple's attester (iCloud) checks Secure Enclave device attestation and account
standing, and "can also perform rate-limiting, to recognize if the client device is … part of a farm of devices". The
server sends a `PrivateToken` challenge naming an issuer (Cloudflare, Fastly); the origin checks tokens "using your
issuer's public key"; tokens are one-time and **unlinkable**. Apple says to treat it "as an optional way to trust a
client", not as a page-load gate. Supported on iOS 16 / macOS 13 and later only. Consequence (inference): no
per-person limit can be built on it; it is a positive risk signal for Apple users
([WWDC22](https://developer.apple.com/videos/play/wwdc2022/10077/), [RFC 9578](https://www.rfc-editor.org/rfc/rfc9578.html),
[RFC 9577](https://www.rfc-editor.org/rfc/rfc9577.html),
[Cloudflare](https://blog.cloudflare.com/eliminating-captchas-on-iphones-and-macs-using-new-standard/),
[Cloudflare Privacy Pass](https://developers.cloudflare.com/waf/tools/privacy-pass)).

**Turnstile modes.** Managed (a checkbox only if needed), Non-interactive (a spinner, no interaction), Invisible (no
widget; the privacy policy must reference Cloudflare's Turnstile Privacy Addendum). Signals: proof-of-work,
proof-of-space, web API probing, browser quirks and behaviour. It embeds without routing traffic through Cloudflare.
Tokens are valid 300 s and validate once (`siteverify`). Enterprise adds Ephemeral IDs: short-lived, cookie-less client
ids that link accounts created across rotating IPs. Solvers and agents get through (§6.1)
([widget](https://developers.cloudflare.com/turnstile/concepts/widget/),
[server-side validation](https://developers.cloudflare.com/turnstile/get-started/server-side-validation/),
[FAQ](https://developers.cloudflare.com/turnstile/frequently-asked-questions/),
[Ephemeral IDs](https://blog.cloudflare.com/turnstile-ephemeral-ids-for-fraud-detection/)).

**TLS fingerprinting.** JA3 became unstable when browsers randomised extension order (Chrome 110+); JA4 sorts before
hashing. The JA4 TLS-client method is BSD-3; the other JA4+ methods are under the FoxIO licence. Cloudflare says
fingerprints "can be easily spoofed, they change frequently", and curl_cffi spoofs them. Kestrel exposes
`HttpsConnectionAdapterOptions.TlsClientHelloBytesCallback` (with `UseTlsClientHelloListener` in the .NET 11 preview API)
when Kestrel terminates TLS; behind nginx, Traefik or a CDN the proxy must compute it, and Cloudflare forwards
`cf-ja4` / `cf-bot-score` headers only on Enterprise with Bot Management. Catches naive HTTP clients only
([Stamus](https://www.stamus-networks.com/blog/ja3-fingerprints-fade-browsers-embrace-tls-extension-randomization),
[ja4plus](https://pypi.org/project/ja4plus/), [Cloudflare JA4](https://blog.cloudflare.com/ja4-signals/),
[Kestrel callback](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.https.httpsconnectionadapteroptions.tlsclienthellobytescallback?view=aspnetcore-10.0),
[aspnetcore#60805](https://github.com/dotnet/aspnetcore/issues/60805),
[managed transforms](https://developers.cloudflare.com/rules/transform/managed-transforms/reference/)).

**Other signals.** Datacenter-ASN reputation: F5 says rate controls on IP or ASN alone "are weak against residential
and hosting networks"; it misfires on VPN users and cloud desktops (inference). Rate limits belong per identity first,
per IP only as a coarse ceiling with IPv6 aggregated to /64: the Better Auth (CVE-2026-45364) and fastify-rate-limit
(CVE-2026-15144) advisories are exactly "keyed per IPv6 address, bypassable by rotating within a /64"
([CVE-2026-45364](https://corgea.com/advisories/vulnerabilities/CVE-2026-45364),
[CVE-2026-15144](https://www.sentinelone.com/vulnerability-database/cve-2026-15144/)).

### 6.5 CAPTCHA and challenge providers compared

| | ALTCHA | mCaptcha | Friendly Captcha | Cloudflare Turnstile | hCaptcha | reCAPTCHA v3 / Enterprise |
|---|---|---|---|---|---|---|
| Self-hostable | **Yes**: the verifier is our app, the widget is bundled (MIT). Paid self-hosted *Sentinel* adds adaptive risk | Yes (Rust server + database; AGPL-3.0) | No (hosted) | No (hosted; works without the CDN) | No (Enterprise on request) | No (Google Cloud) |
| Privacy / EU | No cookies, no third party; claims GDPR compliance | Self-hosted: no third party | German vendor; EU-only endpoint on Advanced/Enterprise | Privacy Addendum; invisible mode must be named in the privacy policy; collects environment signals | Claims GDPR; EU processing on Enterprise only (competitor claim) | CNIL fines partly for reCAPTCHA without consent: Cityscoot €125k (Mar 2023), NS Cards €105k (Dec 2023) |
| Accessibility | Claims WCAG 2.2 AA; W3C: PoW "poses no direct accessibility problems" but slows old hardware | PoW, no interaction | Claims WCAG 2.2 AA (competitor table) | FAQ: WCAG 2.2 AA (another page claims AAA after the 2026 redesign; no independent audit) | A text challenge if the site enables it; the accessibility cookie breaks with privacy browsers (competitor claim) | v2 falls back to inaccessible challenges (W3C note); v3 has no UI, but ambiguous scores push sites to fallbacks |
| Invisible | Yes (`display: invisible`, `auto: onload`) | Yes | Yes (background) | Yes (non-interactive / invisible) | Passive mode on Pro and up only; the free tier shows image challenges | v3 yes (score) |
| .NET server verification | No official C# library; community **Ixnas.AltchaNet** (MIT). SHA-256/PBKDF2 is doable with the BCL; Argon2/scrypt need a dependency | HTTP API | HTTP `siteverify` | HTTP `siteverify` (300 s, single use, `idempotency_key`); community NuGets | HTTP `siteverify` | Official `Google.Cloud.RecaptchaEnterprise.V1` (prerelease) or REST |
| Angular client | Web component; an Angular starter template | JS glue library | JS SDK / web component | `ngx-turnstile` (community, MIT) | Community wrappers | Community wrappers |
| Cost | Free; Sentinel €24–600/month per instance (docs; another page says €99) | Free | Free non-commercial (1 domain, 1,000 requests/month); Starter €9, Growth €39 (listings); Advanced €200 (official) | Free: 20 widgets, unlimited challenges; Enterprise via sales | Free Publisher tier; Pro from $99–139/month; Enterprise | Essentials free ≤ 10k assessments/month; Standard $8 up to 100k, then $1/1k; Enterprise with a 12-month commitment |
| Solver / farm resistance | None by design: a PoW is computed, not "solved" | Same | PoW plus risk signals | Moderate against scripts; solved by services (about $0.002/solve, estimate); ChatGPT Agent passed | Solved by farms (about $1–3/1k) | v2: 100% by ML (ETH); v3: farms and score gaming |

Sources: [ALTCHA open source vs paid](https://altcha.org/docs/open-source-vs-paid/) ·
[Sentinel pricing](https://altcha.org/docs/v2/sentinel/pricing) · [Ixnas.AltchaNet](https://www.nuget.org/packages/Ixnas.AltchaNet) ·
[ALTCHA widget](https://github.com/altcha-org/altcha) · [mCaptcha](https://github.com/mCaptcha/mCaptcha) ·
[Friendly Captcha](https://friendlycaptcha.com/?p=16210) · [european-alternatives.eu](https://european-alternatives.eu/product/friendly-captcha) ·
[Turnstile plans](https://developers.cloudflare.com/turnstile/plans/) · [ngx-turnstile](https://www.npmjs.com/package/ngx-turnstile) ·
[hCaptcha](https://hcaptcha.com/llms.txt) · [hCaptcha accessibility](https://www.hcaptcha.com/learning/accessibility/hcaptcha-accessibility/) ·
[Friendly Captcha on hCaptcha](https://friendlycaptcha.com/insights/hcaptcha-accessibility/) ·
[reCAPTCHA tiers](https://docs.cloud.google.com/recaptcha/docs/compare-tiers) ·
[reCAPTCHA libraries](https://docs.cloud.google.com/recaptcha/docs/libraries) ·
[CNIL Cityscoot fine](https://hunton.com/privacy-and-information-security-law/cnil-issues-e125000-fine-against-e-scooter-rental-company) ·
[reCAPTCHA and GDPR](https://phpcaptcha.org/recaptcha-gdpr-problem/) · [W3C note](https://www.w3.org/TR/turingtest/).

Research pick (inference): **ALTCHA** as the built-in default provider (self-hosted, EU-clean, accessible, no secret to
leak, cheap to verify); **Turnstile** as the best free hosted option when an app wants environment signals and accepts a
US processor; **Friendly Captcha** for an EU-hosted SaaS; **reCAPTCHA** only where the app already runs on Google Cloud
and handles consent; **hCaptcha** where a customer already pays for it. No provider stops a human farm.

### 6.6 What Spark already has for this

| Building block | Where | Relevance |
|---|---|---|
| Fixed-window rate limiter, opt-in: 150 requests / 10 s per **client IP** on `/spark` and `/connect`, 429 | `docs/guide-rate-limiting.md:20-27`, `S/Extensions/SparkRateLimiterOptions.cs:13-16,52` | The partition key is `RemoteIpAddress?.ToString()` (`S/Extensions/SparkBuilderRateLimiterExtensions.cs:115` ✔): **no IPv6 /64 aggregation** (the CVE-2026-45364 class), and a CGNAT IP puts thousands of fans in one bucket. Waiting-room endpoints need their own identity-keyed policy, and the core limiter needs prefix aggregation |
| One bucket per caller; named per-route policies via `RequireRateLimiting` | `docs/guide-rate-limiting.md:70-90` | A waiting-room policy can be a named policy |
| The limiter runs **before authentication** (`SparkMiddlewareStage.BeforeAuthentication`); such middleware must not read `User` | `docs/guide-rate-limiting.md:92-114`, `A/Builder/SparkMiddlewareStage.cs:16,43` | A cheap pre-auth gate (a signature check on admission or visitor tokens) can register there |
| Framework-owned forwarded-header trust (#436: a spoofed `X-Forwarded-For` picked fresh partitions) | `S/Configuration/SparkForwardedHeadersOptions.cs:3-31` | The same trust decides whether edge bot-score or JA4 headers are believed |
| Quotas: per-user sliding window, optimistic concurrency ("20 concurrent uses against a limit of 5 admit exactly 5"), 429 + `Retry-After`, limits as runtime settings | `docs/guide-quotas.md:3-8,31-33,42-47,66-73`, `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:15-31` | Reusable for "holds per identity" and velocity. The anonymous key is `ip:{address}` (`SparkQuotas.cs:28-31`), so guests must be keyed by visitor id instead |
| `SparkThrottledException(RetryAfter)` | `A/SparkThrottledException.cs:14-22` | The honest "Slow" action |
| Passkeys on ASP.NET Identity; user verification `required`; **enrolment always needs a session** (no passkey-first accounts) | `docs/guide-passkeys.md:8-10,50-54,121-130` | Good sign-in UX at T0; adds no cost to account creation |
| A compare-exchange reservation keyed by a hash, unique across users, no index read | `docs/guide-passkeys.md:142-149` | The pattern for one queue entry per identity, a single-use admission `jti` and identity-key de-duplication |
| Client-method retry (449): `Retry.Invoke("webauthn.create", …)`, re-sent with the browser's answer; the action re-runs from the top | `docs/guide-manager-retry-actions.md:17,243-258`, `docs/guide-passkeys.md:71-79` | A step-up challenge at hold or checkout, as `Retry.Invoke("spark.challenge", …)`, **before** any side effect |
| `RequireConfirmedEmail` (honoured for external sign-ups); registration refused without a mail transport | `Auth/Configuration/SparkAuthenticationOptions.cs:123-141` | Baseline identity hygiene |
| Local credentials default **Disabled**; passkeys default Disabled | `Auth/Configuration/SparkAuthenticationOptions.cs:43,98` | Sale channels sign in through external providers or explicitly enabled local accounts |
| External providers `AddGoogle`, `AddMicrosoftAccount`, `AddFacebook`, `AddTwitter`, `AddLinkedIn`, `AddOpenIdConnect`, `AddGitHub`, `AddExternalScheme`, config-driven `AddExternalProviders`; per-provider verified-email trust (`email_verified`) | `Auth/Extensions/SparkExternalProviderExtensions.cs:61,83,113,135,174,231,281`, `GitHubAuthenticationExtensions.cs:23`, `SparkExternalProvidersConfigurationExtensions.cs:49`, `Auth/Configuration/SparkExternalProviderPolicy.cs:54-78` | Pushes account-creation cost onto large providers. No Apple preset (Hide-My-Email relays would also complicate email de-duplication) |
| `SparkUser`: `EmailConfirmed`, `PhoneNumber`, `PhoneNumberConfirmed`, `Passkeys` | `libs/authorization/MintPlayer.Spark.Authorization.Abstractions/Identity/SparkUser.cs:23,27-28,95` | **No SMS or phone verification flow** in `libs/` (no `Sms` anywhere); **no account-creation timestamp**, so an account-age signal needs a field |
| Data Protection owned by Spark | `docs/guide-data-protection.md:3-5` | Sign the visitor cookie and admission tokens (`ITimeLimitedDataProtector`); no custom crypto |
| Share links: a 256-bit capability token, only its SHA-256 stored | `docs/guide-share-links.md:3-5` | The pattern for ballot-winner invitation links |
| `IExpiring` + read-time hiding; RavenDB sweep ≥ 36 h on Community | `docs/guide-expiration.md:52-67,69-82`, `A/IExpiring.cs:34` | 10-minute holds and 5-minute replay markers must be enforced **at read time**, never by `@expires` deletion; fine for risk-log retention |
| Live updates: one WebSocket per tab at `/spark/live` | `docs/guide-live-updates.md:3-6` | Queue-position push can ride it |
| Earlier owner preference: SeoTester "No CAPTCHA", "Not chosen: CAPTCHA/Turnstile" (D17) | `docs/seo_analyzer_PRD.md:311,373,627` | Consistent with an opt-in, PoW-first stance |

### 6.7 Risk-signal pipeline (research design)

```csharp
public interface ISparkRiskSignal
{
    string Name { get; }
    ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken ct);  // time-boxed (~20 ms)
}
public sealed record SparkRiskEvidence(string Signal, double Weight, bool Deterministic = false, string? Detail = null);
public enum SparkRiskAction { Allow, Flag, Challenge, Slow, ShadowQueue, Block }
public sealed record SparkRiskDecision(SparkRiskAction Action, double Score, IReadOnlyList<SparkRiskEvidence> Evidence);
```

Aggregation: noisy-OR over non-deterministic evidence, `score = 1 − Π(1 − wᵢ)`, with positive evidence lowering the
score (clamped at 0). Deterministic evidence decides on its own. The weights are starting points to calibrate (spike S9
below), not truths.

| Built-in signal (no external service) | Kind | Starting weight / action |
|---|---|---|
| Admission or challenge-pass token missing, forged, expired or **replayed** | deterministic | **Block** (403/410 with an explanation); shadow queue only if `DeterministicOnly` is enabled |
| Per-buyer limit hit on any linked key | deterministic | Refuse the hold or void the order, with the reason |
| Per-identity velocity beyond human plausibility (joins/min, hold attempts/min) | rule | **Slow** (429 + jittered `Retry-After`) |
| Challenge failures (count); reCAPTCHA score; edge score | probabilistic | 0.2–0.6 |
| Honeypot field filled | probabilistic | 0.3, never decisive alone |
| Client probe: `navigator.webdriver` / automation flags | probabilistic | 0.3 if present; absence proves nothing |
| Many identities from one IPv6 /64 or IPv4 /24 within minutes (excluding Private Relay and known CGNAT) | probabilistic | ≤ 0.3 |
| Account younger than N days; no verified phone | probabilistic | 0.1–0.2 |
| Positive: passkey sign-in, verified phone, a previous paid order, a valid Private Access Token | probabilistic | −0.1 to −0.2 |

Opt-in signals: edge headers from trusted proxies, Turnstile ephemeral-id fan-out, JA4 (Kestrel), an ASN database.

| Score | Default action at every point |
|---|---|
| < 0.3 | Allow |
| 0.3–0.7 | **Flag** (allow + log); Challenge instead if a provider is configured and the point is in `StepUpAt` |
| 0.7–0.9 | Challenge if a provider is configured, otherwise **Slow** + Flag |
| ≥ 0.9 | Challenge + Slow + Flag |

Two rules hold across the whole policy: **block only on deterministic evidence**, and **shadow-queue only with
`DeterministicOnly` enabled**. Cancellations from post-sale review are human decisions, with notice and an appeal
path (GDPR Art. 22(3)).

### 6.8 "Toying with bots" and its legal exposure

| Technique | Who uses it, and how | Effect | Harm on a false positive | Research verdict |
|---|---|---|---|---|
| **Shadow / infinite queue** | Cloudflare Waiting Room (Mar 2025): traffic that fails Turnstile gets a cookie that "looks like a normal waiting room cookie" and a fake queue whose ETA follows an asymptotic curve that "never quite reaches 0"; "about 1 in 20" clients that run the challenge fail it; no recovery path is described | Wastes bot time; most valuable under FIFO | A misclassified human waits forever | Opt-in, on deterministic evidence only. Of little value under a randomised queue, which has no race to win (inference) |
| **Tarpit / slow / serve alternate content** | Akamai Bot Manager maps bot-score ranges to monitor, challenge, slow or tarpit, serve alternate or cached content, or block, to "stop bots — without tipping off the bad guys" | Hides detection from operators | Slow is mild; alternate content deceives | Honest **Slow** (429 + `Retry-After`) yes. App-level tarpits hold our own connections open, against the repository's rule of no unbounded waits in library code |
| **Escalating PoW** | Kasada (vendor) | CPU cost grows per abusive client | Low-end phones | A possible later knob on ALTCHA complexity per risk; not a default |
| **Decoy content / labyrinth** | Cloudflare AI Labyrinth (Mar 2025): AI-generated link mazes for non-compliant crawlers, opt-in | Built for scrapers, not buyers | Low for crawlers | Not relevant to sales |
| **Honeypot fields / links** | W3C cites a hidden "This field is for robots only" field. Password managers and Firefox address autofill fill hidden fields; one CMS user reported about 20% false positives (anecdote) | Catches naive form bots | Real, from autofill | A score only, never a block on its own; `aria-hidden` + off-screen, checked server-side |
| **Poisoned tokens / delayed failure / fake sold-out** | No vendor documentation for ticketing found | — | A real buyer is deceived at the worst moment | The research advised against building them |

Sources: [Cloudflare shadow queue](https://blog.cloudflare.com/banish-bots-from-your-waiting-room-and-improve-wait-times-for-real-users/) ·
[Akamai Bot Manager](https://www.akamai.com/products/bot-manager) ·
[Infosecurity Magazine](https://www.infosecurity-magazine.com/news/akamai-bot-manager-takes-aim-at/) ·
[AI Labyrinth](https://blog.cloudflare.com/ai-labyrinth/) ·
[honeypot false positives](https://dev.to/alexisfranorge/why-your-honeypot-catches-humans-not-bots-3bcd) ·
[Mozilla support](https://support.mozilla.org/af/questions/1279049) ·
[Perch forum](https://community.perchcms.com/forum/thread/366-forms-honeypot-false-positives/). No documented deception
features were found from DataDome or Netacea; their public material is block/challenge, waiting rooms and threat
intelligence ([DataDome](https://datadome.co/learning-center/how-to-stop-online-bot-ticket-scalping-and-other-ticketing-industry-bot-threats/)).

**Legal exposure of deception (details in §7.3).** The rule that falls out: whatever we tell a client must be true if
that client is a consumer. A false queue position or a fake sold-out page shown to a misclassified human is misleading
information about availability (UCPD Art. 6); blocking or cancelling a purchase on a score is an automated decision
with significant effect (GDPR Art. 22, *SCHUFA*); client-side probing falls under ePrivacy Art. 5(3). In open source the
trick is also visible in the code, and under a randomised queue there is no speed race left for a tarpit to win
(inference).

### 6.9 Fairness for genuine users

- **Shared IPs.** One CGNAT IPv4 can carry 100+ devices; IP-keyed blocklists, rate limits and anomaly detection "may
  inadvertently block or throttle large groups of users", and the bias hits regions with scarce addresses (Cloudflare,
  Oct 2025). Universities and offices behave the same. iCloud Private Relay egress ranges are published
  (`mask-api.icloud.com/egress-ip-ranges.csv`) and shared by many users; vendors advise not to penalise them. Design:
  limits keyed by identity, IP only as a very high ceiling, IPv6 aggregated to /64, Private Relay treated as neutral
  ([Cloudflare CGN](https://blog.cloudflare.com/detecting-cgn-to-reduce-collateral-damage/),
  [Private Relay list](https://github.com/hroost/icloud-private-relay-iplist),
  [ShieldLabs](https://shieldlabs.ai/blog/icloud-private-relay)).
- **Mobile networks and slow connections (inference).** No IP binding; reconnect with the visitor or account identity;
  the position survives refresh and reconnect; admission windows long enough for a slow phone to load the seat map;
  the hold TTL starts only once the hold exists.
- **Accessibility.** WCAG 2.2 SC 3.3.8 (AA): a cognitive-function test in an authentication step needs an alternative
  (object recognition is excepted at AA, not at AAA 3.3.9). The W3C note: interactive human verification "inherently
  excludes many people with disabilities"; audio CAPTCHA is "a poor choice for users with cognitive disabilities"; PoW
  has no direct accessibility problem. The European Accessibility Act has applied to consumer e-commerce since 28 June
  2025. So: PoW by default, and never a puzzle without an alternative
  ([WCAG 3.3.8](https://www.w3.org/WAI/WCAG22/Understanding/accessible-authentication-minimum.html),
  [W3C note](https://www.w3.org/TR/turingtest/),
  [Bird & Bird on the EAA](https://www.twobirds.com/en/insights/2025/a-guide-to-navigating-the-european-accessibility-act-for-online-retailers-service-providers-and-plat)).
- **Slow devices.** Calibrate PoW complexity on low-end Android (ALTCHA: Galaxy A14 2.5 s at 100k); solve it during the
  pre-queue wait, where it costs no position; pause the hold timer during any step-up (inference).
- **Privacy.** First-party signals only by default, a purpose-limited risk log with a retention limit, and a DPIA for
  the scoring. Third-party CAPTCHAs bring the consent question (the CNIL decisions above).
- **Transparency and recourse.** Publish the per-buyer limit and the queue rules (randomised at T0); explain refusals;
  give cancelled buyers a reason and an appeal; offer named-ticket holders a name-change path.

### 6.10 Research recommendation: what goes where

**On by default** when an app uses the waiting-room and sale packages:
1. **A server-enforced admission chain.** Every hold, checkout and payment endpoint requires a valid admission token:
   Data-Protection-protected and time-limited (≤ 10 min); claims `sale`, `sub` (account or visitor id), `entry`, `jti`;
   single-use through a compare-exchange `jti` marker. No client-only gating anywhere.
2. **One queue entry per identity per sale.** Joining is idempotent: refresh, extra tabs and other signed-in devices get
   the same entry and position; a compare-exchange reservation keys it. Admission is redeemable once, by the first
   device.
3. **A randomised pre-queue + FIFO for latecomers** for scheduled sales, with ranks from a cryptographic RNG.
4. **Posted, enforced per-buyer limits:** shown on the sale page and returned by the API; enforced at hold time on the
   keys known then (account, visitor, normalised email, verified phone, and the Stripe card fingerprint before
   confirmation); re-checked at payment on the keys known then (Mollie card fingerprint, IBAN, PayPal payer id,
   normalised address); over-limit orders voided and refunded with a stated reason; identity keys stored as HMACs,
   never raw (inference: privacy).
5. **Denial-of-inventory rules:** one active hold per identity; hold size ≤ the limit; TTL enforced at read time;
   expired holds return stock at once; an escalating cooldown after repeated unpaid holds.
6. **Identity-keyed rate limiting** for waiting-room endpoints, with a high per-prefix ceiling (IPv4 about /24, IPv6
   /64) for gross abuse only; fixing the core limiter's IPv6 aggregation and the anonymous quota key belongs in the same
   unit of work.
7. **A decision log** per identity (signals, score, action, point), `IExpiring` (e.g. 90 days after the event), for
   review, appeals and GDPR access requests.
8. **Honest UX:** real positions, an explanation of the randomisation, explicit refusals.

**Opt-in** through configuration or extension methods: a human-challenge provider (`None` by default; ALTCHA built in;
Turnstile, hCaptcha, reCAPTCHA, Friendly Captcha and mCaptcha adapters; custom); the queue order per sale
(`RandomizedPreQueue`, `Ballot`, `Fifo`); identity requirements per channel (sign-in, `RequireConfirmedEmail`, phone
verification with an SMS sender abstraction and line-type check, a minimum account age, a card on file at ballot
registration via Stripe SetupIntent or a Mollie €0 first payment); payment risk (refuse prepaid cards, request a 3DS
challenge on elevated risk, authorisation holds for griefing-prone sales); named tickets and a transfer policy
(`None` or `PlatformFaceValueOnly`); external signals (trusted edge headers believed only from proxies trusted by
`Spark:ForwardedHeaders`, Turnstile Enterprise ephemeral id, Private Access Tokens, JA4 via Kestrel, an ASN database);
a shadow queue (`Off` by default or `DeterministicOnly`); DBSC registration for the admission cookie (Chrome 146+ on
Windows).

**Left to the app:** prices, dynamic pricing and seat maps; the resale marketplace (within the face-value hook); the
gate ID check; terms-of-service wording and the human appeal process; which channels need sign-in, and provider keys;
CDN/WAF and commercial bot-vendor contracts; legal review per jurisdiction.

**Where challenges apply** (when a provider is configured):

| Point | Default | When enabled | Why |
|---|---|---|---|
| Visitor cookie issue | none | — | Strictly necessary for the queue; no friction |
| Register (ballot) | off | Once per registration | Days before the draw; adds cost per entry |
| **Queue join** | off | Once per identity per sale, during the pre-queue; the pass is bound to the entry and not re-asked on refresh. Under FIFO it must run before T0 as pre-clearance, or it becomes a latency penalty | The main point of use |
| Admission | **never** | — | The queue position is the proof |
| Hold | off | **Step-up only** when risk ≥ `StepUpScore`, as a 449 `Retry.Invoke("spark.challenge", …)` before the hold is created (the action re-runs from the top), so no TTL is lost | Rare friction |
| Checkout / payment | off | PSP-native friction instead: a 3DS challenge (Stripe `request_three_d_secure`; Mollie Risk Controls) | Payment-native, accessible |

Implementation notes from the research: the challenge abstraction (`ISparkHumanChallenge` with `OfferAsync` /
`VerifyAsync`, points `Register`, `QueueJoin`, `Hold`, `Checkout`, `Form`) belongs in its own small package so it is
usable outside sales; no third-party NuGet is needed (each hosted provider is one POST via `IHttpClientFactory`; ALTCHA
is HMAC-SHA256 + SHA-256/PBKDF2 from the BCL); ALTCHA's HMAC key comes from Data Protection, never from configuration;
the challenge points can be a `SparkSetting` so an operator can switch challenges on mid-attack without a redeploy;
hosted widgets load lazily from the vendor origin, so the CSP `script-src` entries must be documented; replay protection
is a used-response marker in compare-exchange or a short in-memory cache, enforced by time at read.

### 6.11 Spikes with measurable pass criteria

| # | Spike | Pass when |
|---|---|---|
| S1 | ALTCHA in C# (SHA-256 v1 + PBKDF2) against the `altcha` v3 widget in Angular, with replay protection | The widget verifies against our verifier; **p95 solve ≤ 2 s on a low-end Android** (Galaxy A14 class) at the chosen default complexity, ≤ 1 s on an iPhone 12-class device; server verification ≤ 0.2 ms/op; a replayed solution is rejected 100/100; an expired one is rejected |
| S2 | Admission token chain (Data Protection time-limited payload + compare-exchange `jti`) | Under 50 concurrent redeemers × 20,000 tokens: **0 double-spends**, 0 accepted after expiry, 0 accepted for another subject or sale; ≥ 2,000 redemptions/s on one node |
| S3 | One entry per identity | 20 tabs + 3 signed-in devices joining concurrently → **exactly 1 entry, identical position everywhere**; refresh and reconnect after 60 s offline keep it; admission redeemed by exactly one device |
| S4 | Fair limits on shared networks | 2,000 simulated genuine clients behind one IPv4 and 500 behind one IPv6 /64 → **0 genuine joins refused**; one client rotating 10,000 addresses inside a /64 is counted as one prefix |
| S5 | Payment-instrument limits | Stripe test mode: a fingerprint over the limit is refused **before** confirmation. Mollie test mode: an over-limit order is auto-voided and refunded within 60 s of the webhook; a €0 first payment yields `cardFingerprint`; prepaid detection works on both |
| S6 | Hosted-provider adapters + accessibility | Turnstile (managed/invisible), Friendly Captcha and hCaptcha verify through one interface; ALTCHA and Turnstile completed keyboard-only with NVDA and VoiceOver without sighted help; the barriers of the others documented |
| S7 | Red team on staging with the default configuration | Stock Playwright, Patchright/nodriver and curl_cffi scripts: **no path to > 1 entry per identity, no hold without admission, no checkout without a hold, no hold larger than the limit**; every attempt appears in the decision log; report the cost per extra entry (accounts needed) |
| S8 | JA4 in Kestrel (only where Kestrel terminates TLS) | JA4 computed for Chrome, Firefox, Safari, curl and curl_cffi; plain curl distinguished, curl_cffi indistinguishable (documented honestly); < 50 µs per handshake |
| S9 | Risk-weight calibration | Replay a load-test decision log with a labelled human control group: **0 humans blocked or shadow-queued** (deterministic rules only), ≤ 1% of humans challenged at the default thresholds |
| S10 | Randomisation and load | 500,000 pre-queue entries: position independent of arrival time (Kolmogorov–Smirnov p > 0.05); every client sees its position ≤ 2 s after T0 (p95); latecomers strictly FIFO |

### 6.12 What does not work

- **CAPTCHA as the defence.** v2 is solved by ML; every widget is solved by farms for fractions of a cent; agents click
  through; puzzles exclude disabled users; reCAPTCHA brings consent duties.
- **Proof-of-work as a deterrent for high-value goods.** Negligible for servers, real for cheap phones; good only
  against non-JS scripts and floods.
- **Passkeys against account farms.** Virtual authenticators and no attestation on synced passkeys; Spark cannot create
  accounts with them anyway.
- **Email verification, account age, prior purchases.** Cheap to buy or fake ($0–20); preferring them creates a market.
- **SMS verification alone.** SIM boxes and about $5 phone-verified accounts.
- **IP and ASN blocking, IP binding.** Residential proxies on one side; CGNAT and Private Relay false positives on the
  other.
- **TLS fingerprints alone.** Spoofed by curl_cffi; every Chrome of one version shares one.
- **Client-side bot detection shipped in an open-source framework.** Public code; site modules within 36 h.
- **Rotating barcodes on the web.** SafeTix's seed was extracted.
- **Shadow queues and deception on probabilistic signals.** Real fans pay the price, with legal exposure.
- **Post-sale cancellation without appeal.** The Oasis 2025 backlash.

---

## 7. Legal notes

Not legal advice. The FTC cases are allegations (KIG denies them; Live Nation's motion to dismiss was pending). The
Belgian article-5 wording comes from legal commentary, not the Moniteur text.

### 7.1 Bots and ticket limits

- **US, BOTS Act (15 U.S.C. § 45c).** Unlawful to "circumvent a security measure, access control system, or other
  technological control or measure … used by the ticket issuer to enforce posted event ticket limits or to maintain the
  integrity of posted online ticket purchasing order rules". Enforcement: the FTC's first cases (2021), KIG (Aug 2025),
  Live Nation/Ticketmaster (Sept 2025, BOTS Act + deception). Executive Order 14254 (31 Mar 2025) tells the FTC to
  "rigorously enforce" it ([FTC 2025](https://www.ftc.gov/news-events/news/press-releases/2025/08/ftc-takes-action-against-ticket-resellers-using-illegal-tactics-bypass-ticket-limit-protections),
  [Wiley](https://wiley.law/alert-Executive-Order-on-Ticket-Resale-Market-Calls-for-Greater-FTC-Enforcement),
  [EO 14254](https://www.govinfo.gov/content/pkg/DCPD-202500418/pdf/DCPD-202500418.pdf)).
- **UK.** The Breaching of Limits on Ticket Sales Regulations 2018 (SI 2018/735, under DEA 2017 s.106) make using
  software to buy above a posted limit, for gain, an offence (unlimited fine in England & Wales)
  ([SI 2018/735](https://www.legislation.gov.uk/uksi/2018/735/made)).
- **EU, UCPD Annex I point 23a** (inserted by Directive 2019/2161) blacklists "Reselling events tickets to consumers if
  the trader acquired them by using automated means to circumvent any limit imposed on the number of tickets that a
  person can buy or any other rules applicable to the purchase of tickets"
  ([Directive 2019/2161](https://eur-lex.europa.eu/eli/dir/2019/2161/oj/eng)).

### 7.2 Ticket resale

- **Belgium, Act of 30 July 2013 on the sale of event tickets** (confirmed in the research). It prohibits habitual
  resale; occasional resale **above the final price** (the total including taxes and mandatory costs, per art. 6 of the
  Act of 6 April 2010); offering for resale before the initial sale; and, per commentary, both exposing tickets for
  such resale and **providing the means** for it. Fines €156–€60,000 (2017 commentary). FOD Economie: an organiser may
  require resale through its own channel. Football has a separate 1998 law. The Act is being challenged (Ticombo; a
  CJEU referral reported in January 2026)
  ([Lexgo](https://www.lexgo.be/en/news-and-articles/4487-reselling-your-tickets-for-summer-festivals-is-not-a-good-idea),
  [Actualités droit belge](https://www.actualitesdroitbelge.be/droit-des-affaires/dr-du-consommateur/la-loi-sur-la-revente-de-tickets-devant-la-cour-constitutionnelle/la-revente-de-tickets---article-5-de-la-loi-du-30-juillet-2013),
  [FOD Economie](https://news.economie.fgov.be/227111-let-op-waar-je-je-concert-of-festivalticket-koopt/),
  [MLex](https://www.mlex.com/mlex/articles/2425821),
  [The Ticketing Business](https://www.theticketingbusiness.com/2024/06/ticombo-takes-legal-action-against-uefa-and-belgian-state/)).
- **Ireland:** the Sale of Tickets Act 2021 bans above-face resale for designated venues and events of ≥ 1,000
  capacity; up to €100,000 / 2 years ([gov.ie](https://www.gov.ie/ga/preasraitis/71f1a-new-law-banning-ticket-touting-comes-into-force/)).
- **Italy:** nominative tickets above 5,000 capacity since 2019 ([Il Post](https://www.ilpost.it/2019/06/25/biglietti-nominali-concerti/)).
- **UK:** an above-face-value resale ban was announced in Nov 2025; a draft *Ticket Tout Ban Bill* was in the King's
  Speech of 13 May 2026 (face value + unavoidable fees, CMA enforcement). **Not yet law**
  ([DJ Mag](https://djmag.com/news/uk-government-ban-resale-of-live-event-tickets-above-face-value),
  [RPC](https://www.rpclegal.com/snapshots/consumer/spring-2026/uk-government-to-introduce-new-rules-banning-ticket-touting/)).
- **EU:** the Digital Fairness Act proposal is pending (planned Q3 2026); consumer groups push a face-value resale cap
  and a dynamic-pricing ban
  ([Freshfields](https://technologyquotient.freshfields.com/post/102lr1q/digital-fitness-check-and-digital-fairness-act-part-8-dropshipping-bots-ticket),
  [Euroconsumers](https://www.euroconsumers.org/can-the-digital-fairness-act-finally-end-ticket-resale-turmoil/)).

### 7.3 Automated decisions, deception and client probing

- **UCPD Art. 6:** a practice is misleading if it gives false information, including about a product's availability,
  that leads the average consumer to a decision they would not otherwise take. Telling a misclassified human "you are
  #4,512" in a queue that never admits fits that (inference) ([Directive 2005/29](https://eur-lex.europa.eu/eli/dir/2005/29/oj)).
- **GDPR Art. 22:** blocking or cancelling a purchase on a score is an automated decision with a significant effect.
  The CJEU (*SCHUFA*, C-634/21, 7 Dec 2023) treats a score as a decision when the outcome "depends decisively" on it,
  which requires a lawful basis and the right to human intervention and to contest
  ([A&O Shearman](https://www.aoshearman.com/en/insights/ao-shearman-on-data/cjeu-rules-that-a-credit-score-constitutes-automated-decision-making-under-the-gdpr)).
- **ePrivacy Art. 5(3)** covers client-side probing and fingerprinting (EDPB Guidelines 2/2023, v2 Oct 2024); the consent
  exemption for "user-centric security" cookies (WP29 Opinion 04/2012, WP194) is narrow
  ([EDPB](https://edpb.europa.eu/system/files/2024-10/edpb_guidelines_202302_technical_scope_art_53_eprivacydirective_v2_en_0.pdf),
  [WP194](https://Datenschutz.Hessen.de/sites/datenschutz.hessen.de/files/2022-11/wp_194.pdf)).
- **CNIL** fines partly for reCAPTCHA without consent: Cityscoot €125k (Mar 2023), NS Cards €105k (Dec 2023)
  ([Hunton](https://hunton.com/privacy-and-information-security-law/cnil-issues-e125000-fine-against-e-scooter-rental-company),
  [phpcaptcha](https://phpcaptcha.org/recaptcha-gdpr-problem/)).

### 7.4 Consumer withdrawal: 14 days, except dated leisure services

- **Consumer Rights Directive 2011/83/EU, Art. 9(1):** "Save where the exceptions provided for in Article 16 apply, the
  consumer shall have a period of 14 days to withdraw from a distance or off-premises contract, without giving any
  reason, and without incurring any costs other than those provided for in Article 13(2) and Article 14."
- **Art. 16(l):** Member States shall not provide the right of withdrawal for "the provision of accommodation other than
  for residential purpose, transport of goods, car rental services, catering or services related to leisure activities
  if the contract provides for a specific date or period of performance".
- So event tickets for a specific date are exempt from the 14-day withdrawal; webshop goods are not.
- Source: legislation.gov.uk's archived copy of the EU text ([Art. 9](https://www.legislation.gov.uk/eudr/2011/83/article/9),
  [Art. 16](https://www.legislation.gov.uk/eudr/2011/83/article/16)), fetched 2026-10-17 because EUR-Lex returned empty
  pages to the fetcher. The archive may not show amendments made after the UK left the EU; re-check both articles on
  EUR-Lex [unverified on EUR-Lex].

### 7.5 Accessibility

The European Accessibility Act has applied to consumer e-commerce since 28 June 2025; WCAG 2.2 SC 3.3.8 (AA) requires an
alternative to a cognitive-function test in authentication (§6.9).

### 7.6 VAT place of supply for admission

The place of supply for admission to an event is where the event takes place: Art. 53 of the VAT Directive for B2B,
Art. 54 for B2C; virtual attendance has been excluded since 2025
([Art. 53](https://legislation.gov.uk/eudr/2006/112/article/53),
[International Tax Review](https://internationaltaxreview.com/article/2ckmtgmklnroebnuekruo/)).

### 7.7 Belgian mandatory B2B e-invoicing

| Claim | Source | Status |
|---|---|---|
| Structured electronic invoices between Belgian VAT-liable companies have been compulsory since 1 January 2026: "Structured electronic invoices between companies are compulsory since 2026"; "Since 1 January 2026, all Belgian enterprises liable to VAT will have to use structured electronic invoices" in their transactions with each other | [FPS Finance portal](https://einvoice.belgium.be/en) (official) | Confirmed 2026-10-17 |
| Format and network: the "UBL" format sent over the Peppol network | [Forvis Mazars](https://www.forvismazars.com/be/en/insights/newsletters-client-alerts/newsletters/mandatory-b2b-e-invoicing-in-belgium) (vendor) | Vendor source; not named on the official page fetched |
| "Expected to use the Peppol interoperability model and the Peppol BIS format" | [Storecove](https://storecove.com/blog/en/transition-to-mandatory-b2b-e-invoicing-in-belgium) (vendor) | Vendor source |
| Consumer sales are excluded: the obligation "does not apply to supplies of goods or services provided to individuals for their private use" | Forvis Mazars (vendor) | The official page excerpt implies a B2B scope ("between companies") but does not state the exclusion |
| EN 16931 as the standard; a PDF no longer valid as a B2B invoice | — | [unverified]: not stated on any page fetched |
| The econnect page offered as a source | [econnect](https://econnect.eu/en/blog/belgium-announces-mandatory-b2b-e-invoicing-from-2026) (vendor) | As fetched on 2026-10-17 it covers only the EU ViDA package (cross-border B2B e-invoicing mandatory from July 2030) and does not address Belgium |

The vendor sources should be checked against FPS Finance before any rule is encoded.

### 7.8 What the law implies for the platform (inference)

1. **Post** the per-buyer limit on the sale page and in the API; both the BOTS Act and the UK offence hinge on a posted
   limit.
2. Enforce it across linked accounts; the FTC's case against Ticketmaster is that it advertised limits it did not
   enforce.
3. Never resell tickets the platform knows were bought by circumvention.
4. A Belgian resale feature must cap at the final price; a marketplace that allows more is "providing the means".
5. Keep an evidence log for enforcement and for GDPR access and appeal requests.
6. Never deceive a consumer in the queue.

---

## 8. Persistent-object name identity

A read-only investigation of what the persistent-object **name** keys, run against the owner's direction for this work:
an optional rename attribute (absent → today's name) plus an analyzer that detects colliding names at build time. The
researcher spot-checked the key citations (`ModerationTargets.cs:14`, `AgentGrant.cs:52,89-96`, `DashboardStorage.cs:44`,
`SparkShareOptions.cs:36`, `OrganizationActions.cs:47`, `McpPlanner.cs:355`, `QueryExecutor.cs:1240`); the other line
numbers come from two sweeps (plugin libraries and ng-spark). File names are given as cited; the most frequent live here:

| Directory | Files |
|---|---|
| `S/Services/` | `ModelSynchronizer.cs`, `ModelLoader.cs`, `ModelHashVerifier.cs`, `PermissionService.cs`, `QueryExecutor.cs`, `QueryLoader.cs`, `SecurityConfigurationValidator.cs`, `SparkTypeResolver.cs`, `CollectionGuard.cs` |
| `S/` | `SparkMiddleware.cs`, `SparkDocumentStoreConventions.cs`, `Live/LiveStartup.cs`, `Sharing/SparkShareOptions.cs` |
| `A/` | `EntityTypeDefinition.cs`, `SparkModelFiles.cs`, `SparkAppData.cs`, `ProgramUnit.cs`, `Authorization/IRowPolicy.cs`, `Authorization/ISecurityConfigurationLoader.cs`, `Authorization/SparkSecurityFiles.cs`, `Model/ModelFileShape.cs`, `Model/SparkModelShape.cs` |
| `libs/spark/Shared/Layering/` | `SparkKinds.cs`, `SparkLayers.cs`, `SparkModelLayers.cs`, `SparkSecurityLayers.cs` |
| `Gen/` | `Json/ComposedModel.cs`, `Generators/PersistentObjectNamesGenerator.cs`, `Diagnostics/LibraryLayersReader.cs`, `Diagnostics/LibraryActionsConflictAnalyzer.cs`, `Diagnostics/SecurityConfigurationAnalyzer.cs` |
| Libraries | `Auth/Agents/AgentGrant.cs`, `Mod/Services/ModerationTargets.cs`, `libs/dashboards/MintPlayer.Spark.Dashboards/Services/DashboardStorage.cs`, `libs/mcp/MintPlayer.Spark.Mcp/Tools/SparkGenericTools.cs`, `libs/mcp/MintPlayer.Spark.Mcp/Plans/McpPlanner.cs` |

### 8.1 What each key is used for

| Key | What it keys |
|---|---|
| **NAME** (`persistentObject.name`) | Layering (the composition key that decides delta versus own type); the target of every right; the prefix of every `model.*` translation; the join from a query to its type (`SparkQuery.entityType`); the binding of a **virtual** type to its actions (`{Name}Actions`); several stored records (moderation, agent grants, dashboards, share links) |
| **id** (Guid) | The wire and program units |
| **alias** | URLs |
| **CLR type** | References, AsDetail, interceptors and row policies. Its **simple name** (`typeof(T).Name`, ignoring the namespace) also keys typed actions and RavenDB collections |

### 8.2 Five traps, verified in code

1. **Silent hijack by the synchronizer** (the real "silent delta"). Take an app class `MyApp.Order` while a library ships
   `Order` (`Commerce.Order`). `--spark-synchronize-model` looks up the existing definition by `clrType` and finds none
   (`ModelSynchronizer.cs:156`); mints `Name = typeof(T).Name` with a random id (`ModelSynchronizer.cs:785-796`); finds
   the library type **by name** in `WriteModelFile` (`ModelSynchronizer.cs:765`) and writes `SparkModelLayers.Delta`,
   which drops the app's id because the library's id is immutable (`SparkLayers.cs:309`). The composed type then has
   the library's id, `clrType: MyApp.Order` and the union of both attribute sets. **Possible exposure:** the library's
   rights on `…/Order` match by name, so they now cover the app's documents, while the library's row policies match by
   CLR type (`IRowPolicy.cs:150`) and no longer apply. **Library code breaks:** code that resolves `Commerce.Order` by
   clrType gets null.
2. **A refusal that gives the wrong advice.** When an app type has its own id and a library later ships the same name,
   startup refuses with `'persistentObject.id' cannot be changed` (`SparkLayers.cs:422-426`), then *advises* "state only
   what differs, and never an id" (`SparkModelFiles.cs:84-85`). Following the advice turns the app's own type into a
   delta.
3. **Library versus library.** Startup refuses the same way; the build says nothing, because `ComposedModel.cs:37-48`
   discards the problems. The application has **no remedy** except dropping a library; only a rename mechanism fixes it.
4. **An orphaned delta degrades silently.** An id-less app file that no longer names a library type fails the
   `required Id` deserialization; `ModelLoader.cs:111-120` skips it with a console line, and the library type is then
   served **without the delta's restrictions** (e.g. `showedOn: None`). Production would probably stop on the model-hash
   drift; Development only warns (`ModelHashVerifier.cs:8-24`).
5. **Actions resolution crashes.** Typed actions are found as `typeof(T).Name + "Actions"` across every loaded
   assembly, and a duplicate throws (`ActionsResolver.cs:44-47,121-164`); virtual types as `{PO name}Actions`
   (`ActionsResolver.cs:93-99`). The startup validator swallows the exception (`SparkMiddleware.cs:827-849`), so the
   crash shows up on the first request, or at startup only through `LiveStartup.cs:45`.

For commerce (inference): a library with natural entity names (`Order`, `Product`) meets traps 1 and 5, and the
collection trap in §8.9, as soon as an app has a class with the same simple name (§2.5).

### 8.3 Where the NAME is the key

**Model composition, files and loader**

| Where | What | Key |
|---|---|---|
| `libs/spark/Shared/Layering/SparkModelLayers.cs:104-136` | Library model layers grouped by name, compared exactly (`byName`, `:108`) | NAME |
| `SparkModelLayers.cs:139-156` | An app file joins a library type by name and becomes its delta | NAME |
| `SparkModelLayers.cs:158-172` | An app file that matches no library type is the app's own type; two app files never merge | — |
| `SparkModelLayers.cs:203` | The composed `FileName` of a library type is the library's file name | file name |
| `SparkKinds.cs:27-31`, `SparkLayers.cs:422-426` | `id` is immutable across layers; a different id is a problem | ID |
| `A/SparkModelFiles.cs:76-88` | Throws on problems, with the delta advice quoted above | — |
| `EntityTypeDefinition.cs:11-16` | Documents the name as "its identity across layers" and the translation prefix | NAME |
| `S/Services/ModelLoader.cs:75` | The alias defaults to `Name.ToLowerInvariant()` | NAME → ALIAS |
| `ModelLoader.cs:82-89` | An alias collision throws | ALIAS |
| `ModelLoader.cs:95-98` | `query.EntityType ??= entityType.Name` | NAME |
| `ModelLoader.cs:249-250` | `GetEntityTypeByName`: case-insensitive, first match wins | NAME |
| `ModelLoader.cs:255-278` | `ResolveEntityType`: guid, then alias, then name (the name fallback is load-bearing for `parentType`) | ID / ALIAS / NAME |
| `ModelSynchronizer.cs:156,234,217,742-779,785-796` | Matches an existing type by clrType, writes `{typeof(T).Name}.json` with `EntityType = typeof(T).Name`, matches a library type **by name** and writes a delta, sets `Name = typeof(T).Name` | CLR + NAME |

The synchronizer legitimately writes a delta when an app exposes a **library's** CLR class: SeoTester's
`IRavenQueryable<Organization>` (`SeoContext.cs:28`) produced `apps/SeoTester/SeoTester/App_Data/Model/Organization.json`.
Only two deltas exist in the repository: that file and QnA's `SparkUser.json`.

**Authorization**
- Every check builds `{verb}/{definition.Name}` (`PermissionService.cs:58-60`). Callers include
  `DatabaseAccess.cs:100,133,205,282,288,470,583,611`; `QueryExecutor.cs:832,944,1174`; `New.cs:88-89`;
  `Refresh.cs:80-88`; `Create.cs:57`; `CustomActionExecution.cs:121-125` (custom actions are bound to a type only through
  the right `Action/Type`); `ListCustomActions.cs:58-69`; `GetPermissions.cs:45-69`; `GetAliases.cs:23,31`;
  `ProgramUnits/Get.cs:47`.
- Matching is exact and case-insensitive, with combined verbs expanded (`ISecurityConfigurationLoader.cs:99-108,126-128`).
  `SecurityConfigurationValidator.cs:38` says it outright: "resources name types by name, never by alias" (also `:71`).
- Library guard rails: the targets a library owns are read from its raw model layers, names and aliases both
  (`SparkSecurityFiles.cs:60-66` → `SparkSecurityLayers.cs:324-347`); a grant outside them is refused
  (`SparkSecurityLayers.cs:426-436`), and so is a reserved target equal to a model type (`:380-383`).
- Plugin verbs are keyed by name too: History (`SparkHistory.cs:99,165`); Contributions (`ContributionActions.cs:55`,
  `RevertContribution.cs:84`); Moderation (`SparkModeration.cs:57,290-291,305,364,383`, `ModerationEndpoints.cs:219`,
  `ModerationInterceptor.cs:164`); SoftDelete (`SoftDeleteRowPolicy.cs:73`, `SoftDeleteInterceptor.cs:130-131,165`); MCP
  (`McpPlanner.cs:348`).
- **Build/runtime mismatch:** SPARK012 accepts a type's **alias** as a target (`SecurityConfigurationAnalyzer.cs:641-655`),
  but the runtime matches names only, so a right on a custom alias passes the build and grants nothing (latent; it fails
  closed).
- `securityPosture.txt` rows are `group | effect | resource | key | layer` (`docs/guide-library-layers.md:170-173`). The
  resource carries the NAME and no type identity: if a name moves to another type (a library type renamed while the app
  adds its own type with the old name), the posture text does not change at all.

**Translations, program units, queries, references**
- Translations are keyed by NAME: `model.{Name}.label` plus `…attributes|tabs|groups.*` (`ModelLoader.cs:208-237`), and
  the synchronizer's description keys (`ModelSynchronizer.cs:457,1657`). Libraries ship them (`model.*` / `queries.*`
  keys counted: identity_provider 265, notifications 23, authorization 12, dashboards 9). `queries.{QueryName}.label`
  uses the *query* name, not the PO name.
- Program units are keyed by ID (Guid `persistentObjectId` and `queryId`, `ProgramUnit.cs:55,58`; the name is resolved
  on demand, `ProgramUnits/Get.cs:124-129`): **rename-safe**.
- Queries use the NAME in `SparkQuery.EntityType`, stated explicitly in 18 library model files (e.g.
  `PasskeyRow.json:74`, `Notification.json:126,141`, `Setting.json:88`, `OidcApplication.json:610`). It is consumed by
  `QueryExecutor.cs:498-508` (which resolves `Custom.X` through the type name to the actions class) and `:1561-1565`; by
  the live modules (`LiveStartup.cs:39`, `LiveQueryCollections.cs:32`, `LiveAppStreamSubscription.cs:57-59`); by
  `QueryLoader.cs:64-67`; and by `DistinctValues.cs:84`, `SubQueryPruner.cs:91`, `QueryReadPath.cs:98` and
  `AttributeRightsEnforcement.cs:291`.
- References and AsDetail use CLR full names (`EntityTypeDefinition.cs:291,295`), resolved by clrType
  (`EntityMapper.cs:538`, `Refresh.cs:217`, `SubQueryPruner.cs:145`, `AttributeRightsEnforcement.cs:190`,
  `SaveValidation.cs:154`): **rename-safe**. The reference's `query` field is a *query* name (`:287`);
  `lookupReferenceType` is a lookup class name, a separate namespace (`:379`). A type's sub-queries are keyed by alias or
  ID (`EntityTypeDefinition.cs:199-205`): rename-safe.
- `parentType` on the wire is the parent's NAME: the client sends `et.name` (`spark-po-detail.component.html:276`), the
  server resolves it alias first, then by name (`ModelLoader.cs:272-278`), and custom queries receive
  `ParentType = Name` (`QueryExecutor.cs:1240-1241`). `IManager.GetPersistentObjectAsync(string name)` looks types up by
  NAME (`IManager.cs:35-36`, `EntityMapper.cs:228-232`).

**Actions and custom actions**
- Typed actions resolve as `FindActionsType($"{typeof(T).Name}Actions")` (`ActionsResolver.cs:44-47`), matching any
  class with that simple name in any loaded assembly; a duplicate throws (`:121-164`), and so does a mismatched generic
  argument (`:55-69`).
- Virtual types resolve as `ResolveByEntityName(definition.Name)` (`ActionsResolver.cs:93-99`); call sites
  `DatabaseAccess.cs:1097`, `QueryExecutor.cs:508,789,1200`, `DisabledActionsEvaluator.cs:210`, `SparkMiddleware.cs:834`,
  `SparkDashboardsExtensions.cs:122`. Authorization already documents the hazard (`PasskeysActions.cs:22-25`: "No
  application may declare a class of this name").
- A CLR-typed registration already exists but is consulted second: ActionsRegistrationGenerator emits
  `AddSparkActions<TActions,TEntity>`, which registers `IPersistentObjectActions<TEntity>` (`SparkMiddleware.cs:286-293`);
  only five apps call the generated `AddActions()`, and no library does.
- Custom actions are found by class simple name minus `Action`, and the first loaded class silently wins
  (`CustomActionResolver.cs:87-92`).

**Interceptors and row policies** are keyed by CLR type: the interceptor pipeline (`SparkInterceptorPipeline.cs:73,105`),
the after-commit outbox payload (CLR full name: `SparkCommittedInterceptors.cs:49`, `SparkInterceptorPipeline.cs:191`),
and row policies (`AppliesTo(Type)`, `IRowPolicy.cs:38,150,171`). They are rename-safe, which is exactly why trap 1 is
dangerous: rights follow the NAME while policies follow the CLR type.

**Gates and tooling**
- `modelHashes.json`: `files` is keyed by the composed `FileName` (a library type uses the library's file name;
  `ModelFileShape.cs:67-73`); `entities` by the **CLR simple name** (`SparkModelShape.cs:122-127`); `contextRoots` holds
  simple names; `layers` maps `Model/{file}` to an alias. Two types with one file name, or one CLR simple name,
  **overwrite each other's hash silently**.
- The startup hash check refuses outside Development and warns in Development (`ModelHashVerifier.cs:8-24`,
  `SparkMiddleware.cs:741`). `--spark-describe model X` narrows by name (`SparkDescribeCommand.cs:81-89`); security uses
  the composed names (`:161`).

**Generators and analyzers (compile time)**
- `PersistentObjectNames` is built from each `DefaultPersistentObjectActions<T>`'s `entityType.Name`
  (`PersistentObjectNamesGenerator.cs:72-76`) plus the library type names (`:103-114`, `ComposedModel.cs:51-60`);
  duplicates collapse silently (`NamesProducer.cs:24-28`). `PersistentObjectIds` comes from the composed model, and on a
  duplicate (schema, name) the first wins (`IdsProducer.cs`, about `:60-67`).
- `ComposedModel.Compose` (`ComposedModel.cs:37-48`) uses the runtime's engine but **ignores problems**. The security
  analyzer reads the composed model (`SecurityConfigurationAnalyzer.cs:620-660`).
- `LibraryLayersReader.Read(compilation)` already reads every referenced `[assembly: SparkLayer(alias, kind, path, json)]`
  from `ReferencedAssemblySymbols` (`LibraryLayersReader.cs:36-67`); SPARK036 (`LibraryActionsConflictAnalyzer.cs`), the
  security analyzer and the names and ids generators all use it.
- SPARK045 is unaffected by app renames: library ids are UUIDv5 of `alias:DeclaredName` (`SparkModelLayers.cs:267-326`).

**Persisted data (RavenDB)**
- Collection names come from the CLR type: RavenDB's default pluralized CLR simple name, used for generated ids
  `{collection}/{guid}` (`SparkDocumentStoreConventions.cs:79-83`), and nothing overrides `FindCollectionName`. A PO
  name therefore never appears in a collection, a document id or `@collection`; but `Commerce.Order` and `MyApp.Order`
  share `Orders`, and `CollectionGuard` compares only `@collection` (`CollectionGuard.cs:35-45`). Index names are index
  class names; no index name contains a PO name.
- Records that **store** the name, each of which breaks on a rename:

| Record | What is stored | Evidence |
|---|---|---|
| Moderation | `TargetType` on 6 document classes, value `Definition.Name`; the review queue matches it by name; the audit sink writes CLR simple names instead (inconsistent) | `ModerationDocuments.cs:14,52,124,151,168,194`, `ModerationTargets.cs:14`, `spark-review-queue.component.ts:183-184`, `ModerationSatelliteAuditSink.cs:16,19` |
| Agent grants | `AgentGrant.Types[].Name`, written by the IdP consent form and checked by name; edits reject the old name, so a rename **needs a data migration** | `AgentGrant.cs:52,89-96`, `AgentConsent.cs:158,216-222`, `McpAgentRestriction.cs:56-79` |
| Dashboards | Document id `SparkDashboards/{name}`; the dashboard name equals the virtual PO name and `{name}Actions`; a mismatch refuses startup | `DashboardStorage.cs:44`, `SparkDashboardsExtensions.cs:110-123` |
| Share links | Registration key `typeName ?? typeof(T).Name`, re-resolved by name; the URL `/shared/{TypeName}/{token}` is held by external recipients | `SparkShareOptions.cs:36`, `SparkShareLinks.cs:175` |
| MCP plans | `Operations[].Type`, re-resolved on apply, only within the 10-minute window. Latent bug: `entityType.Name` is passed as `ObjectTypeId`, which is resolved alias first | `McpPlan.cs:73`, `McpPlanner.cs:310,340,355` |
| IdP audit | `Details["types"]`, informational only | `AgentConsent.cs:261` |

- Configuration keyed by name: `Spark:History:Types:{name}` (`SparkHistoryOptions.cs:33`; an unknown name only warns)
  and Moderation's `DownvoteCastTypes` (`SparkModerationOptions.cs:26`). Not keyed by name: History revisions (the
  collection, `RevisionsConfigurator.cs:69`), Contributions (CLR type or target id), the messaging outbox (CLR full
  name), Replication (the collection, `EtlDeploy.cs:99`), Migrations (class name).

**The client (ng-spark and app code)**
- Routes and links use `alias || id`, never the name (`spark-routes.ts:13-30`; `:type` is matched as
  `t.id === type || t.alias === type`, `spark-po-detail.component.ts:368` and its siblings).
- Keyed by NAME: the query lookup `q.entityType === et.name`, with `source` suffix heuristics
  (`spark-query-list.component.ts:297-302`); `parentType = et.name` (`spark-po-detail.component.html:276`), persisted in
  create URLs as `?parentType=` (`spark-po-create.component.ts:101-109`); the share route `shared/:type/:token`
  (`share-routes.ts:33`); `spark-query-actions.service.ts:89-90`, `spark-return-navigation.service.ts:40,61` and the
  contributions revert (`spark-revert-contribution.ts:92-97`); stored names in the review queue and connected agents
  (`spark-connected-agents.component.ts:177-179,308-336`); app templates that switch on `entityType.name` (CodeCoverage
  `po-detail-page.component.ts:54-79`, SeoTester `po-detail-page.component.ts:47-59`).
- Not keyed by name: the renderer registry (by the model's `renderer` name), panels and actions (by extension id),
  layouts (a predicate), client storage, translations (resolved on the server).
  `provideSparkAttributeRenderers(renderers)`, `provideSparkDetailPanels(...)` and
  `provideSparkDetailLayouts(...{ matches(item, entityType) })` take no type key.
- Net effect: live pages read names from the server's composed model, so a rename applied consistently on the server is
  invisible to them. What breaks are names held outside the process (bookmarked `?parentType=`, share URLs, stored
  grants and review cases) and literals in app code.

**The MCP server.** Tool names contain no type names (`discover`, `describe`, `get`). The tools expose `name` and join
queries with `q.EntityType == t.Name`, compared exactly (`SparkGenericTools.cs:59,63,91,113,178`); a tool's `type`
argument goes through `ResolveEntityType` (`:84,160`); `[McpReadsThrough]` and `[McpRunsAction]` take type names as
strings in app code (`McpToolReach.cs:20,39,73,77`).

### 8.4 Types without a CLR class

- Declared as a model JSON file with **no `clrType`**: "a page that exists in the model but not in the database, served
  exclusively through `OnLoadAsync(id, parent)` on a `{Name}Actions` class (resolved by name…)"
  (`EntityTypeDefinition.cs:24-30`). Everything document-shaped answers 404 (`ISparkTypeResolver` returns null,
  `SparkTypeResolver.cs:18-21,39`), and queries over them are "composed queries" (`SparkComposedQueries.IsComposed` = no
  clrType). Their actions classes are duck-typed, with the `OnLoadAsync` contract checked by reflection
  (`DatabaseAccess.cs:1085-1120`).
- Shipped examples: `Passkeys` (a page), `PasskeyRow` (rows) and `PasskeyRename` (a retry prompt) in Authorization;
  `DatabaseStatistics` in Dashboards (`live: true`; "a dashboard is a virtual type"); the identity provider's `*Row`
  types, whose actions implement `ISparkOwnsRowSecurity`; CodeCoverage's `ForgeAccounts`.
- Identity: the id is UUIDv5 of `alias:Name` for a library and a random Guid for an app (stamped, SPARK045); the NAME
  keys composition, the actions binding, rights, translations and query `entityType`; the alias is the URL, often
  explicit (`Passkeys.json` `"alias": "passkeys"`, route `/po/passkeys/me`).
- Library code addresses its own virtual types by name literal, and a rename breaks every one: `PasskeysActions.cs:43`
  `"Passkeys"`, `RenamePasskeyAction.cs:39` `"PasskeyRename"`, `TeamActions.cs:38` `"OidcInvitation"`,
  `OrganizationCustomActions.cs:22,42` `"OrganizationInvite"`, Dashboards' name triple, and `OrganizationActions.cs:47`,
  which compares `args.ParentType` to `nameof(Organization)`.
- A library-shipped virtual type has no CLR type to name, so the research keys its rename by library alias + declared
  name (the UUIDv5 seed): `[assembly: PersistentObjectName("authorization", "Passkeys", "MyPasskeys")]`. The actions
  binding must then keep using the declared name inside the declaring assembly, and library code must stop using name
  literals (generated id constants, or the CLR type). An app-owned virtual type needs no attribute: the app owns its JSON
  and changes `name`.

### 8.5 Feasibility of the optional rename attribute

Feasible: C# generic attributes work in .NET 11. The research's shape:

```csharp
// libs/attributes/MintPlayer.Spark.Attributes (next to SparkLayerAttribute)
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public class PersistentObjectNameAttribute : Attribute
{
    public PersistentObjectNameAttribute(string library, string type, string name) { … } // type without a CLR class: alias + declared name
    protected PersistentObjectNameAttribute(string name) { … }
    public string? Library { get; } public string? Type { get; } public string Name { get; }
    public virtual System.Type? EntityType => null;
}
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class PersistentObjectNameAttribute<T> : PersistentObjectNameAttribute where T : class
{
    public PersistentObjectNameAttribute(string name) : base(name) { }
    public override System.Type EntityType => typeof(T);
}
```

- **Reading it.** At run time through the non-generic base (`assembly.GetCustomAttributes<PersistentObjectNameAttribute>()`);
  at build time from `compilation.Assembly.GetAttributes()` (`AttributeClass.TypeArguments[0]`). When a library entity
  is `internal` the app cannot write `typeof(T)`; the non-generic form works for every library type, so the generic form
  is refactor-safe sugar.
- **Effective name.** An app CLR type with `PersistentObjectName<T>(n)` gets n, otherwise `typeof(T).Name` as today
  (`ModelSynchronizer.cs:790,795`). A library type gets the attribute's n (the generic form matched by
  `clrType == typeof(T).FullName`, the non-generic one by alias + declared name), otherwise its declared name. An app
  JSON type keeps its own `name`.
- **Checks** (analyzer and runtime): the target exists; it is renamed at most once; n is an identifier (no `/` as in
  `Verb/Name/Attr`, no `.` as in `model.Name.`, no `:` as in slots and keys; `ComposedModel.IsIdentifier` exists); n is
  unique **case-insensitively** (rights uppercase, `ISecurityConfigurationLoader.cs:128`; `GetEntityTypeByName` ignores
  case; `QueryLoader.cs:64`'s `ToDictionary(…, OrdinalIgnoreCase)` would throw on `Order` / `order`); n is not a reserved
  target; only the application may rename (the attribute is refused in a library compilation).
- **One place for library types.** A shared `SparkRenames` in `libs/spark/Shared/Layering`, compiled into both the
  runtime and the generators, rewrites the declaring library's layers once, as they load: the model
  (`persistentObject.name` and every `queries[].entityType` equal to the declared name); security (the target segment of
  each `rights[].resource`, `Verb/Old` → `Verb/New`, `Verb/Old/Attr` → `Verb/New/Attr`, so the guard-rail targets follow);
  translations (the `model.Old.` prefix, on the flattened form); nothing for program units (id-keyed), `actions.json` or
  `moderation.json`. It is called at run time inside `SparkLayerCatalog` (`SparkLayerCatalog.cs:41,58-93`; precedent for
  reading the entry assembly: `SparkLayerCatalog.cs:99-117`, `SparkAppData.cs:27`; test hosts pass renames explicitly,
  as they pass libraries via `ModelSource.For`; under `WebApplicationFactory`, fall back to
  `IHostEnvironment.ApplicationName`), and at build time inside `LibraryLayersReader.Read(compilation)`.
- **Rewriting the library's rights is mandatory, not cosmetic:** without it a library's `QueryRead/Order` grant would
  silently apply to the app's own new `Order`.
- **What sees the new name without further change:** the model composer and loader (alias, lookups,
  `query.EntityType`), every rights check, the library guard rails, labels, `--spark-describe`, the posture file,
  `PersistentObjectNames` / `PersistentObjectIds`, SPARK011/012/014/021/024, SPARK036 and the client.
- **What else has to change:** keep the raw library JSON for provenance hashes, recording renames as their own section
  (otherwise `modelHashes.json` `layers` and the posture's "libraries that ship rights" section report "library changed");
  carry provenance into the definition (`SparkComposedType.DeclaredName` beside its existing `Library`; a `[JsonIgnore]`
  origin on `EntityTypeDefinition`, which virtual-actions binding needs); apply the attribute to app CLR types in the
  synchronizer and generators rather than as a layer rewrite; fix code that assumes CLR simple name = PO name
  (`SparkShareOptions.cs:36`, ModerationInterceptor's CLR fallback `:188-189`, `ContributionDescriptor.cs:43,81`,
  `OrganizationActions.cs:47`, the synchronizer's `:217` / `:234`) by looking the name up through
  `GetEntityTypeByClrType(...).Name` or carrying an id.
- **The app's model file for a renamed library type** is a delta by the **new** name, anchored with `$extends`:

```json
{ "$extends": "commerce:Order",
  "persistentObject": { "name": "WebshopOrder", "attributes": [ … ] } }
```

  The old name `Order` becomes free for the app's own type. Generated constants follow the new name, so app code that
  uses the old constant gets a compile error (the desired loud failure); SPARK012 flags an app right still on the old
  name unless another type took it; SPARK045 is unaffected.
- **The attribute alone is not enough** (research reading): it lets a developer sidestep a collision they already know
  about, but it does not stop the silent hijack for a developer who does not know, does not touch the actions crash
  (keyed on the CLR simple name, which a PO rename leaves unchanged), and does not separate RavenDB collections.

### 8.6 Rename handling ("when the developer changes the string")

- **What stays stable.** A library type's id is UUIDv5 of its declared name, so an app-side rename never changes it. An
  app CLR type's id survives an attribute change because the synchronizer finds the definition by clrType
  (`ModelSynchronizer.cs:156`), but it would write `{new}.json` next to the old file (two files, one id, `byId` keeping
  the last), so the synchronizer must move the file.
- **Detection.** `modelHashes.json` version 3 records `names: { "<id>": "<name>" }`; `--spark-verify-model` and the
  startup check (which refuses in production) report `type <id> (commerce:Order) was 'Order', is now 'WebshopOrder'`.
  With `$extends`, a stale delta is exact without any history.
- **`--spark-synchronize-model` auto-migrates the files the app owns**, reviewed in the PR diff: move the model file and
  its `name`, keeping the id; rewrite `queries[].entityType`; re-key the app's `model.A.*` translations; rewrite the
  app's security.json resources `*/A` and `*/A/*` **only while A is free** (if another type is now named A, refuse and
  list the rights to re-target by hand: exactly the case where a right silently changes type); rewrite the names map.
  `--spark-synchronize-security` then shows the posture diff.
- **Runtime and verify refuse** an orphaned or stale delta (instead of `ModelLoader.cs:111-120`'s console line), a
  `$extends` target that no library ships, a delta `name` that differs from the effective name, and hash drift. The
  posture file should gain a type-identity column (`commerce:Order` or the app's id), so re-targeting shows as a diff
  even when the resource string does not change.
- **Outside the files.** The stored records in §8.3: store ids, not names (and a type's alias in URLs), with one
  `libs/migrations` migration for existing production data using parameterized patches only (the repository's "never
  splice strings into RQL" rule, `CLAUDE.md`). URLs that use the id or an explicit alias survive; a derived alias changes with the name. Bookmarked
  `?parentType=` and share URLs should carry the id or alias. App templates comparing `entityType.name` and
  `[McpReadsThrough("A")]` strings are app code that a lint can only warn about. Configuration
  (`Spark:History:Types:A`, `DownvoteCastTypes`) should refuse an unknown name at startup instead of warning. Never
  touch the database at startup.

### 8.7 Options compared (research recommendation)

| Option | Silent delta / hijack (developer unaware) | Library vs library | Duplicate-actions crash | Surface |
|---|---|---|---|---|
| Attribute alone | ✗ remedy only: the synchronizer still hijacks and the refusal still misleads | ✓ the only remedy | ✗ typed actions key on the CLR **simple** name, which a PO rename leaves unchanged | Attribute + layer rewrite + synchronizer + generators |
| Explicit delta marker alone | ✓ a bare name match becomes an error | Detected, **no remedy** | ✗ | `$extends` directive, schema, synchronizer |
| Actions by CLR type alone | ✗ | ✗ | ✓ for typed actions; virtual types stay bound by name | Resolver only |
| **Combination** | ✓ | ✓ | ✓ | Each piece small and independent |

The combination the research recommends:
- **(a) An identity rule in `SparkModelLayers.Compose`**, shared by the runtime, the synchronizer, the generators and the
  analyzer: an app file is a delta on a library type only with `$extends`; a bare name match, or a different `clrType` or
  id, is a collision problem whose message points at the attribute and at `$extends`, never at "remove the id"; the
  synchronizer matches library types by clrType, writes `$extends` and refuses a collision. The cost is one line each in
  QnA's `SparkUser.json` and SeoTester's `Organization.json`. A variant without new syntax ("different clrType or id ⇒
  collision; an id-less file that names no library type ⇒ refuse") catches every case except a hand-written, id-less
  virtual file that happens to share a library name; stating the library's UUID instead of `$extends` is the zero-syntax
  anchor the engine already accepts.
- **(b) Actions off simple names.** Typed actions resolve through `IPersistentObjectActions<T>`: a DI registration wins
  (the generated `AddActions()` already makes one); otherwise the non-abstract classes implementing it for exactly that T,
  where the most derived wins if it derives from all the others (a new, explicit way to override a library's actions),
  and anything else is an eager startup error rather than a first-request one. Virtual types resolve as
  `{DeclaredName}Actions` inside the declaring assembly for a library type (from `SparkComposedType.Library`), and inside
  non-library assemblies for an app type. An analyzer cannot be authoritative here: the runtime scans
  `AppDomain.CurrentDomain.GetAssemblies()` (`ActionsResolver.cs:137`), including assemblies the compiler never saw;
  library actions classes are mostly `internal` (`PasskeysActions.cs:26`, the identity provider's row actions); and two
  `OrderActions` classes for two different types are not a model error.
- **(c)** The optional attribute, applied once at layer load (§8.5). **(d)** The collision analyzer (§8.8). **(e)** The
  rename protocol (§8.6).

### 8.8 The collision analyzer: feasibility and tentative diagnostics

- **An analyzer can read the referenced libraries' layer JSON.** `LibraryLayersReader.Read(compilation)` reads the
  `SparkLayerAttribute` constructor arguments from `compilation.SourceModule.ReferencedAssemblySymbols`
  (`LibraryLayersReader.cs:36-67`). The other inputs are reachable too: the app's model files arrive as AdditionalFiles
  through the path convention the security analyzer uses (`SecurityConfigurationAnalyzer.cs:631-636`); the app's CLR
  entities are the generic arguments of the `IRavenQueryable<T>` properties on the app's `SparkContext`; the library
  entities' CLR names are in their layers' `clrType`. No new reading infrastructure is needed.
- **Build and runtime agree in production.** The build records exactly which layered assemblies it saw
  (`[assembly: SparkLayerAssemblies]`), and the runtime loads exactly those (`SparkLayerCatalog.cs:99-117`). Running the
  shared composer, renames included, and reporting its problems makes the build equal startup (the existing D14
  principle).

Proposed diagnostics, with **tentative ids** (SPARK056 is the highest id in use):

| Id | Severity | When | Anchor (needed for a live code fix) | Code fix |
|---|---|---|---|---|
| SPARK057 | Error | Two libraries ship one effective name | The app's `SparkContext` class (a symbol action); `Location.None` when there is none | Insert `[assembly: PersistentObjectName("ticketing","Order","TicketingOrder")]` |
| SPARK058 | Error | An app CLR entity's effective name equals a library type's and the CLR types differ: the synchronizer hijack, predicted before any file exists | The `SparkContext` property `IRavenQueryable<Order> Orders` | Insert `PersistentObjectName<MyApp.Order>("…")` (or rename the library type) |
| SPARK059 | Error | An app model file names a library type without `$extends`, or states a different id or clrType | The JSON file (an additional-file location; shows in the Error List) | Message only; a code fix in an additional document [unverified] |
| SPARK060 | Error | An orphaned or stale delta: `$extends` names no shipped type, or `name` differs from the effective name | The JSON file | Message: "run --spark-synchronize-model" |
| SPARK061 | Error | An invalid attribute: unknown target, renamed twice, not an identifier, colliding, used on an app virtual type, or used in a library | The attribute | — |
| SPARK062 | Warning | Two Spark entity CLR types share a simple name, and so the default RavenDB collection | The `SparkContext` property | Message (collection override) |

- **A library-side companion check** (in the generators project): string literals naming the library's own model types,
  passed to `GetPersistentObjectAsync(string)`, `GetEntityTypeByName` or `IsAllowedAsync(verb, "X")`, should use the
  generated id constant instead.
- **Constraints measured earlier in this repository:** a diagnostic from `RegisterCompilationAction` reaches the Error
  List but gets **no light bulb**, so use a symbol action; a location outside the analyzed compilation is **discarded**,
  so anchor on the app's own `SparkContext` symbols (that is how SPARK017 and its `MissingReferenceRootCodeFixProvider`
  work); code fixes live in the analyzer assembly (owner, 2026-10-05). An intentional delta (`$extends`) raises no
  diagnostic, and neither does an app exposing a library's own CLR type, as SeoTester does with `Organization`.

### 8.9 Adjacent traps a rename does not fix

| Trap | Evidence | Remedy direction |
|---|---|---|
| RavenDB collections are keyed by the CLR simple name: `Commerce.Order` and `MyApp.Order` both write to `Orders`, ids included; `session.Query<Commerce.Order>()` returns the app's documents, and a library index over `docs.Orders` indexes them | `SparkDocumentStoreConventions.cs:79-83`, `CollectionGuard.cs:35-45` | A per-type collection override, which a library with natural names should ship (SPARK062 can warn) |
| Custom actions are a global namespace keyed by class simple name, and the first loaded class wins silently: two `CancelAction` classes run the wrong one; SPARK036 treats same-named actions as one action | `CustomActionResolver.cs:87-92` | Prefix the names (§2.3) |
| Query names collide across libraries: an alias collision is refused (`SparkQueryAliasIndex`), a name collision is not (`GetQueryByName` takes the first match, and `queries.{name}.label` clashes) | — | — |
| Index names are index class names, so two libraries' `Orders_Overview` deploy to one RavenDB index | — | — |
| The model gate keys `entities` by CLR simple name and `files` by file name, and a duplicate is overwritten silently | `ModelFileShape.cs:67-73`, `SparkModelShape.cs:122-127` | — |

Documentation the research marks for update with this work: `docs/guide-library-layers.md:72` and its "Model deltas"
section (`:82-104`), and `diagnostics.md`.

---

## 9. Existing approval-like workflows

An inventory of what a generic approvals library could absorb (read-only, master at `e39b16de`). Keyword sweeps covered
`approv|reject|pending|review|propos|invit|decision` and the status enums and constants across the identity provider,
moderation, contributions, QnA, organizations, authorization, MCP, CodeCoverage, the server infrastructure, ng-spark and
the remaining apps.

### 9.1 Classification

1. **Class 1, a proposed document decided by a different person: nothing exists.** The document-proposal model has no
   code to absorb. The closest prior art is MCP plans (operations plus captured change vectors, applied in one commit),
   but there the same user approves, in the MCP host.
2. **Class 2, genuine third-party decisions: four, all in libraries.** The IdP developer request, the IdP cross-owner
   API scope approval, the IdP go-live review, and the Organizations join request, which production SeoTester uses. In
   each, approving **runs code** (flips a status, creates a membership); none applies a proposed document. **Three of
   the four need a per-item decider** (an API's owners, an organization's Owner/Admin), which a single approvers slot
   cannot express.
3. **Moderation's review queue** is a human decision but fits badly (§9.4).
4. **Everything else is class 3**, not a fit: MCP plans (self-approved); organization and IdP invitations (self-redeemed
   bearer tokens); OAuth consent, device flow and PAR; agent grants and PATs; external-login link confirmation; votes,
   reputation and flags; contributions (designed to avoid approval); QnA drafts and close; every CodeCoverage
   "approval", forge-native by design (`docs/code-coverage/product-overview.md:172`: "GitHub is the authority … it
   removes an entire approval-queue feature").

Consequence (research reading): "trim and point at the library" works only with two layers: (i) a generic
decision-request engine (typed approve/reject handlers, a decider resolver per kind, programmatic submit, also inside a
caller's session, plus reasons, notifications, audit, expiry and live updates), and (ii) document proposals as one
built-in kind on top. Migrating the four class-2 flows removes about 1,100 lines (about 960 in 20 IdP files that
disappear entirely) and adds back about 250–350. It closes these gaps: reject reasons are never stored, deciders are
never notified, organization Admins cannot decide in-app and there is no decline, and one suspected self-approval hole
(§9.7).

### 9.2 Inventory

| # | Workflow | Where | Who decides | Approve does | Class | Verdict |
|---|---|---|---|---|---|---|
| 1 | IdP developer request | `IdP/Models/OidcDeveloper.cs:10-33`, `IdP/CustomActions/ApproveDeveloperAction.cs:18-126` | Slot `identity-provider:administrators` (`OidcPortalAccess.cs:25`) | Sets `OidcDeveloper.Status`; derived group membership follows | 2 | Migrate |
| 2 | IdP cross-owner scope approval | `IdP/Models/OidcApplication.cs:244-264` ✔, `IdP/CustomActions/ReviewActions.cs:12-77` | **Per item**: `OidcResource.Owners` ∪ admins (`OidcPortalAccess.cs:31-33`) | Sets the scope row's `Status` | 2 (never 1) | Migrate as a decision request |
| 3 | IdP go-live review | `OidcApplication.cs:30-32`, `IdP/CustomActions/TeamActions.cs:160-196`, `ReviewActions.cs:79-137` | Admins (`ReviewActions.cs:110`) | `Mode = Live` | 2 | Migrate |
| 4 | Organizations join request | `Org/Models/OrganizationInvitation.cs:6-62`, `Org/Services/SparkOrganizations.cs:155-278` | **Per item**: Owner/Admin of that organization, read at decision time (`:236-240` ✔) | Creates the membership | 2 | Migrate (moderate risk: production SeoTester) |
| 5 | Moderation flag cases | `Mod/Services/SparkModeration.cs:296-356,485-553` ✔ | Right `Review/Moderation` via slots `moderation:reviewers` / `moderators` (some earned) | Reputation for flaggers; the target is untouched | 2, poor fit | Do not migrate |
| 6 | Moderation fraud cases | `Mod/Services/FraudDetector.cs`, `SparkModeration.cs:515-539` | Same as #5 | Dismiss, reverse, merge or suspend | 3 | Do not migrate |
| 7 | MCP plans `propose_changes` → `apply_changes` | `libs/mcp/MintPlayer.Spark.Mcp/Plans/McpPlan.cs:13-92`, `McpPlanner.cs` | The **same user**, in the MCP host's prompt | Commits the plan | 3 (self-approved) | Do not migrate; reuse its engine |
| 8 | Organization invitation | `SparkOrganizations.cs:110-153` (InviteAsync), `:243-278` (accept branch) | The invitee, through a mailed bearer token | Creates the membership | 3 | Keep |
| 9 | IdP team invitation | `IdP/Services/OidcInvitations.cs:42-106`, `IdP/Endpoints/Portal/InvitationEndpoints.cs:23-57` | The invitee | Invited → Active | 3 | Keep |
| 10 | OAuth consent, agent consent, device flow, PAR | `IdP/Endpoints/Consent.cs`, `AgentConsent.cs`, `Device.cs:102-156`, `Par.cs` | The user, about their own access (protocol state, 10 min) | Issues the grant | 3 | Keep |
| 11 | Agent grants and PATs | `Auth/Agents/AgentGrantsAccount.cs:83-244` | The user (self-service; no pending status) | — | 3 | Keep |
| 12 | External-login link, ConfirmByEmail | `Auth/Identity/SparkPendingExternalLogin.cs:25-80`, `SparkExternalLoginLinker.cs:137,230` | The account owner, through a mailed token | Links the login | 3 | Keep (security-critical, deliberately standalone) |
| 13 | Votes, reputation, earned privileges | `Mod/Documents/ModerationDocuments.cs:9-116` | Nobody (thresholds, 48 h crediting) | — | 3 | Keep |
| 14 | Contributions; hide/restore/revert | `libs/contributions/…/ContributionsInterceptor.cs:90-122`, `RevertContribution.cs:48-131` | Nobody, or a moderator acting immediately | — | 3 (exists to avoid approval: `docs/guide-contributions.md:20-21`) | Keep |
| 15 | QnA drafts, close/reopen, delete reason | `apps/QnA/QnA/Security/DraftQuestionPolicy.cs`, `CloseQuestionAction.cs`, `DeleteReasonInterceptor.cs` | The author or a moderator, immediately | — | 3 | Keep |
| 16 | CodeCoverage merge-queue approval floor | `apps/CodeCoverage/CodeCoverage/MergeQueue/MergeQueueEngine.cs:451-498` | A GitHub reviewer with Write (read live from the forge) | The queue proceeds | 3 (forge-native) | Keep |
| 17 | CodeCoverage dependency `approve <group>` / `hold` | `…/Dependencies/Policy/DependencyHolds.cs:88-138` | The webhook sender with forge Write | A `DependencyApproval` bound to a blob id and majors | 3 (forge-native) | Keep |
| 18 | CodeCoverage dependency onboarding; GitHub App install request | `DependencyOnboarding.cs`, `IForgeRepositoryAdministration.cs:103-116` | A forge merge, or the org owner on GitHub | — | 3 | Keep |

Every other "pending" in the code base is job or outbox state: `EMessageStatus`, `ESyncActionStatus`,
`BuildSession.ParseStatus`, `CrawlFrontier`, `DependencyUpdateRun.PendingTrigger` and similar.

### 9.3 The four class-2 flows

#### 9.3.1 IdP developer request: migrate

- **Purpose.** A signed-in user accepts the developer terms to get portal access. With `Developers:RequireApproval`
  (default `true`, `IdP/Configuration/SparkIdentityProviderPlatformOptions.cs:10`), an IdP administrator decides.
- **Request.** `POST /spark/identity-provider/developer` (`IdP/Endpoints/Portal/DeveloperEndpoints.cs:100-171`). The
  pending state is `Developer.Status = "Requested"` on the user's **own document** (`OidcDeveloper.cs:10-33`, statuses
  `:29-31` ✔), written by a deferred patch, `OidcDevelopers.Set` (`OidcDevelopers.cs:47-54`).
- **Decider, a fixed population.** Rights `ApproveDeveloper` / `RejectDeveloper` on `OidcDeveloperRequest`, granted to
  the administrators slot (`IdP/App_Data/security.json:81-95`); in code `IsAdministratorAsync()` =
  `IsAllowedAsync("ManageAll/IdentityProvider")` (`OidcPortalAccess.cs:25`; `ApproveDeveloperAction.cs:33,55`).
- **Approve / reject** (`ApproveDeveloperAction.cs:81-124`): a strongly consistent re-read in its own session; skip
  unless `Requested`; set `Status`, `DecidedAt`, `DecidedBy`; audit `developer.approved/rejected` in the same session
  (`Models/OidcAuditEvent.cs:45-47`); save; mail `DeveloperApproved` / `DeveloperRejected` to the requester; notify, then
  `RefreshQuery`.
- **The effect is derived.** `OidcDeveloperMembership.cs:31-42` puts the user in the group bound to
  `identity-provider:developers` while `IsActive` (Approved and the current `TermsVersion`, `OidcDevelopers.cs:43-44`).
  The same status gates registration tokens (`DeveloperEndpoints.cs:59`), RFC 7591 registration
  (`Endpoints/Register.cs:65`) and invitation eligibility (`OidcInvitations.cs:70,98`).
- **Gaps today:** no reason is ever written (`OidcDeveloper.Note` exists and the mjml renders `{{ if note }}`); deciders
  are never told about a new request; `Revoked` is read (`DeveloperEndpoints.cs:145-147`) but never written; a re-request
  after a rejection overwrites the rejection, so there is no history.
- **Queue and UI.** A virtual PO type: record `OidcDeveloperRequest` (`Actions/OidcDeveloperRequestActions.cs:12`) +
  `App_Data/Model/OidcDeveloperRequest.json`; raw RQL with a `$status` parameter (`:36-42`) and `ISparkOwnsRowSecurity`
  (`:19-24`). Requester UI: `NS/identity-provider/developers/src/spark-developers.component.ts` (183 lines; status and
  note `:48-75`, `request()` `:143-161`). Deciders use the generic query page plus server custom actions; there is no IdP
  review component (`management/src/spark-identity-provider-management.component.ts:20-28` only links to
  `/query/<alias>`).
- **Tests:** `tests/MintPlayer.Spark.Tests/IdentityProvider/OidcApprovalTests.cs:97-190` (6); gating in
  `OidcDynamicRegistrationTests.cs` (5) and `OidcInvitationTests.cs` (2); E2E
  `tests/MintPlayer.Spark.E2E.Tests/IdentityProvider/IdentityProviderJourneyTests.cs:79-101`.
- **Fit.** Class 2. Keep the effective status on the user document, because group membership point-loads it on every
  request. The library would take the queue, the decision, the reason, the mails and the history; the IdP keeps the
  request endpoint (which calls submit), the terms-version gate, "Revoked blocks a new request", and the
  `RequireApproval=false` auto-approve.

#### 9.3.2 IdP cross-owner scope approval: migrate as a decision request, never as a document proposal

- **Purpose.** An application asks for an API scope whose API no active team member owns; the scope is `Pending` until
  an owner of that API, or an IdP admin, decides.
- **Three creation paths:** the PO save, through `ApplyScopeApprovalsAsync` → `MarkCrossOwnerScopesAsync`
  (`IdP/Interceptors/OidcApplicationInterceptors.cs:162-208` ✔); RFC 7591 registration (`Register.cs:95`); RFC 7592
  update (`Register.cs:178`; kept scopes keep their status, `OidcClientRegistration.cs:119-124`). Skipped for the system
  context or a ManageAll caller ("an administrator adding a scope is the approval", `:182-183` ✔). **One save can raise
  several requests, each for a different decider.**
- **Pending state, embedded in the live document:** `Scopes[i].Status = "Pending"` plus
  `Review = OidcReviewDecision{Pending, RequestedBy, RequestedAt}` (`OidcApplication.cs:244-256` ✔).
- **Decider, per item, resolved at decision time.** The scope prefix gives the API name (`OidcScopeCatalog.cs:30-34`);
  the API's `OidcResource.Owners` (`OidcResource.cs:53-56`) ∪ admins decide, through `OwnsAsync`
  (`OidcPortalAccess.cs:31-33`). The rights `Query` / `ApproveScope` / `RejectScope` on `OidcScopeApproval` go to both
  slots (`security.json:126-140,171-185`); `OwnsAsync` is the real gate.
- **Approve / reject** (`ReviewActions.cs:40-76`): selection ids are `{appId}|{scope}`; a caller who may not decide is
  **silently skipped** (`:53-55`), so there is no existence oracle; on a decision: set `scope.Status` and the `Review`
  fields, audit `application.scope.decided`, save, mail `ScopeApprovalDecided` to the application's Admins, then
  `RefreshQuery`.
- **Runtime effect** (`OidcApplicationAccess.cs:23-29`, `AvailableScopes`): Approved → everyone; **Pending → the team
  only** (partial effect while pending); Rejected → nobody. Read at authorize (`OidcAuthorizeHandler.cs:237,258`), device
  (`Device.cs:142`), client_credentials (`Token.cs:762`) and token exchange (`TokenGrants.cs:201`).
- **Why it can never be a document proposal:** the request is one row of a sub-collection; it takes partial effect
  while pending; Rejected is a lasting state on the live row; the decider comes from another document; there are three
  creation paths outside the PO screen; and `OidcApplication`'s change vector moves **at least hourly** on its own
  (`secret.LastUsedAt`, `OidcClientAuthenticator.cs:225-240`; `MetadataDocumentFetchedAt`,
  `Mcp/OidcClientMetadataDocuments.cs:120`), so a whole-document optimistic apply would be stale almost every time.
- **Needs:** a subject keyed to a sub-row (`(OidcApplications/x, "fleet.read")`); submit inside the interceptor's
  session (atomic with the save); a decider resolver; a handler that patches the single row's `Status`.
  `AvailableScopes` and the `Status` field stay; `Review` / `OidcReviewDecision` go.
- **Tests:** `OidcApprovalTests.cs:225-350` (7); the predicate is tested by calling `MarkCrossOwnerScopesAsync`
  directly; no E2E; plus `OidcDynamicRegistrationTests` (1). Suspected team self-approval: §9.7.

#### 9.3.3 IdP go-live review: migrate

- **Request.** `SwitchToLiveAction` (`TeamActions.cs:160-196`), by an application Admin (`OidcPortalAccess.cs:27-29`),
  when `Apps:RequireReviewToGoLive` is on (default `false`, `SparkIdentityProviderPlatformOptions.cs:25-26`) and the
  caller is not an IdP admin.
- **Pending state:** `OidcApplication.GoLiveReview = {Pending,…}` (`:180`); `Mode` stays Development and is read-only in
  the model.
- **Decider:** admins (`ReviewActions.cs:110`, `security.json:186-200`).
- **Approve:** `Mode = Live`, the review is decided, audit, then a `GoLiveDecided` mail to the app's Admins (the template
  supports a note, but none is passed). **Reject:** the review becomes Rejected; `Mode` is unchanged.
- **Other behaviour:** a new request overwrites the review even while one is pending (no dedupe). The effect is read
  through `MayAuthorize` (`OidcApplicationAccess.cs:15-16`).
- **Tests:** `OidcApprovalTests.cs:383-475` (6); the E2E goes Live without review.
- **Fit.** Class 2, a one-field change from code. "Modify: true" on `OidcApplication` would put every edit under
  approval, and the change vector moves hourly. Risk: low.

**What the three IdP flows share is a data shape, not behaviour.** The shape is `OidcReviewDecision`
(`OidcApplication.cs:309-323` ✔: Status, RequestedBy/At, DecidedBy/At, Note). The behaviour is copy-pasted three times:
3 static `*Decision.DecideAsync` helpers, 6 `ICustomAction` shells, 3 virtual queue types each with its own row
security, 3 model JSONs, 14 rights ✔, 6 action definitions ✔ and 12 decision mails.

#### 9.3.4 Organizations join request: migrate (moderate risk)

- **Request.** `ISparkOrganizations.RequestToJoinAsync` (`Org/ISparkOrganizations.cs:57` →
  `SparkOrganizations.cs:155-209` ✔). Today only server code raises one: production SeoTester's `VerifySite`, when
  another organization already holds the host verified (`apps/SeoTester/SeoTester/Verification/SiteVerification.cs:71-88`).
  The requester needs a confirmed email; a new request replaces a pending one (`:164-175`).
- **Pending state.** An `OrganizationInvitation` with `Kind = JoinRequest` (`OrganizationInvitation.cs:6-13,25-62`).
  There is **no status field**: pending means the document exists and has not expired. It expires after 14 days, as
  `IExpiring` (`OrganizationInvitation.json:17`).
- **Decider, per item.** Any member of that organization with role ≥ Admin, read from storage at decision time
  (`SparkOrganizations.cs:236-240` ✔). But the mail goes to **Owners only** (`:195-207`), and **Admins can see the
  request** (`[RequiresOrganizationRole("Query", Admin)]`, `OrganizationInvitation.cs:21`) **but cannot approve
  in-app**: only the mailed bearer link approves.
- **Approve.** `POST /spark/organizations/accept` with the token (`Org/Endpoints/AcceptOrganizationInvitation.cs:35-54`):
  the token is compared in fixed time against its stored hash; it creates the membership (an Owner is demoted to
  Admin), deletes the request and writes an ILogger line. **The requester is not told, and there is no audit
  document.**
- **Reject.** None: an Admin can delete the row, or it expires; no reason is captured.
- **UI and tests:** `NS/organizations/accept/src/spark-accept-organization-invitation.component.ts`;
  `tests/MintPlayer.Spark.Tests/Organizations/OrganizationServiceTests.cs` (join tests at `:158,:189,:200,:411`) and
  `apps/SeoTester/SeoTester.Tests/Sites/VerificationTests.cs:98-106`.
- **Fit.** Class 2: approval runs custom code (`NewMembership`, `:417-425`). As a "Create proposal of
  `OrganizationMembership`" it would fail, because the requester has no `New` right. It needs a per-item resolver, a
  programmatic submit, GDPR cleanup on account deletion (`Identity/OrganizationAccountHooks.cs:72-78`) and an
  organization-deletion hook (`:342-352`). Gains: Admins decide in-app, decline with a reason, the requester is
  notified, audit.
- **Production risk (SeoTester's claim flow, its D18):** pending JoinRequest documents and mailed links in flight at
  deploy time need a data migration or a deliberate 14-day expiry; approve-from-mail becomes "deep link and sign in";
  the invitation half stays untouched.

### 9.4 What must not migrate, and why

| Workflow | Why it stays |
|---|---|
| **MCP plans** | No second party: the human approves client-side in the MCP host. The server checks only same user, client and grant (`McpPlanner.cs:177-180`), status `Proposed`, not expired (10 minutes, `:93`), and that the echoed changes equal the stored ones (`:190-191`, the prompt-injection defence); "reject" means never calling apply. `EAgentPermission` has only `None` / `Read` / `Write` ✔, and Write's "approval" is the host prompt. Migrating would lose the synchronous agent loop, the agent-write audit (`PlanRetention`) and the echo defence. Reuse or extract its engine instead (§9.5), and make MCP report proposals honestly |
| **Moderation** (votes, reputation, privileges, flags, the review queue, fraud cases, locks, suspensions) | Post-moderation only: nothing is held back and an upheld flag does not hide the target (`docs/contributions_PRD.md:42`). `DecideAsync` (`Mod/Services/SparkModeration.cs:485-553` ✔) requires `Review/Moderation` (`Mod/App_Data/security.json:11-17`; QnA binds the slots at `apps/QnA/QnA/App_Data/security.json:16-22`), and reviewer membership can be earned (`Mod/App_Data/moderation.json:42-48`; `ModerationUserState.cs:162-185`). One case has many flaggers and reopens itself (`SparkModeration.cs:327-336`); the outcome changes reputation, not the target (`:555-590`); fraud cases are system-raised with four outcomes (`:515-539`); the evidence views are domain-specific (`ModerationReview.cs:74-134`). The most a migration could delete is about 150–250 lines (`ModerationReview.cs` 217, `NS/moderation/…/spark-review-queue.component.ts` 210), at the price of coupling two unrelated packages |
| **Suggested edits** | Explicitly deferred ("Out of v1: suggested edits…", `docs/issue_460_PRD.md:259` ✔; `docs/contributions_PRD.md:41-43`, decision C5 `:100-102`); an approvals library is their natural home, but as a **new** feature, not a migration |
| **Contributions** and their moderation | The design premise is "no approval": per-user versions, "newest wins", moderators act afterwards (`docs/guide-contributions.md:20-21,182-218`); QnA silently drops non-author edits except `Translations` (`apps/QnA/QnA/Actions/QuestionActions.cs:53-65`) |
| **Invitations** (organization and IdP team) | The invitee redeems a bearer capability, possibly before having an account; the IdP side must not reveal whether an account exists; no third-party decider and nothing to diff |
| **OAuth consent, agent consent, device flow, PAR** | Protocol state inside a 10-minute authorization request (`IdP/Services/OidcAuthorizationFlow.cs:215-221`; RFC 8628 at `Device.cs:102-156`) |
| **Agent grants and PATs** | User self-service; statuses active/expired/revoked/disconnected (`AgentGrantsAccount.cs:231-244`); external agent servers are operator configuration; embedded IdP agent clients get scopes `Approved` outright (`IdP/Mcp/OidcAgentClients.cs:113`) |
| **External-login ConfirmByEmail** | The account owner confirms within 1 hour; deliberately standalone (`SparkPendingExternalLogin.cs:12-17`) |
| **All CodeCoverage approvals** | Forge-native and production. Merge-queue floor: GitHub reviews on the pinned SHA (tests `CodeCoverage.Tests/MergeQueue/MergeQueueEngineTests.cs:1050-1144`). `approve <group>`: the webhook sender's live forge Write role; an approval goes stale on a newer major or a `coverage.yml` change (`DependencyPolicyEngine.cs:433-459`). The state documents are not Spark entities (`Entities/DependencyUpdateState.cs:19-20`); account access deliberately has no join workflow (`CoverageAdministrators.cs:9-35`). GitHub branch protection must agree with what the queue counts, approvers need no CodeCoverage account, and the PR thread is the audit. If Account or Repository were ever gated, the onboarding fan-out hooks (`RepositoryActions.cs:122-139`) would fire on mere proposals |

### 9.5 Constraints an approvals library inherits

**Packaging** (template: Moderation). Alias `<SparkLibraryAlias>approvals</SparkLibraryAlias>`; configuration under
`Spark:Approvals` because layer kinds are closed (§2.4); UUIDv5 model ids. Registration convention
`Notif/SparkNotificationsExtensions.cs:43-52`, template `Mod/SparkModerationExtensions.cs:38-97`: options +
`BindConfiguration`, `AddInterceptor`, `AddIndexesFrom`, `AddCron`, `Registry.AddMiddleware(<StartupCheck>.Run)`,
`Registry.RequireDocumentExpiration`, `Registry.AddEndpoints`.

**Slots.** A right naming an unbound slot refuses startup (`SparkSecurityLayers.cs:290-298` ✔). If the IdP depended on
an approvals library, its layer would probably reach every app that references the IdP: CodeCoverage, SeoTester and
Fleet (all `AgentsOnly`, with `"identity-provider": false`) plus SparkId (`LibraryLayersReader.cs:40` ✔; not verified at
runtime). The research therefore advised against library rights bound to an approvals slot, proposing instead per-type
grants by the app (printed by an init command, as Moderation does) or a code-resolved slot enforced by the library's
startup check only when `Spark:Approvals:Types` is non-empty.

**Save pipeline and concurrency.**
- **No hook can replace a save.** "The framework owns persistence … an interceptor cannot skip any of that"
  (`A/Interceptors/SparkInterceptors.cs:22-24` ✔); only deletes have `IDeleteReplacement` (`:110-122` ✔). A cancel or
  refusal takes back everything interceptors wrote to the session (`S/Services/DatabaseAccess.cs:395-413` ✔). A
  cancelled update answers 200 with the stored row; a cancelled create answers **204**
  (`S/Endpoints/PersistentObject/Create.cs:89-92` ✔). Contributions diverts edits atomically through satellites
  (`ContributionsInterceptor.cs:90-122`); a create cannot be diverted.
- **Etag.** The PO's `Etag` is the change vector, checked twice: early in a side session, then as the expected change
  vector on the write (`DatabaseAccess.cs:343-348,785,825-826`). Reasons `changed` / `deleted` / `exists`
  (`S/Exceptions/SparkConcurrencyException.cs:14-63`); add-ons map them through
  `ISparkAddOnEndpoints.IsConcurrencyConflict` (`SparkAddOnEndpoints.cs:67-75`).
- **Applying later.** Precedent: History's revert sets `Etag` to a change vector and saves through the public path
  (`libs/history/MintPlayer.Spark.History/SparkHistory.cs:94-144`). The public `IDatabaseAccess.SavePersistentObjectAsync`
  **skips model validation** (`S/Services/SaveValidation.cs:69-80`). The endpoint-equivalent `IPersistentObjectSavePath`
  (with `CheckCreateAsync` / `CheckUpdateAsync`, MCP's propose step) and `ISparkWriteUnit` are `internal` ✔; only MCP has
  `InternalsVisibleTo` (`S/MintPlayer.Spark.csproj:43-50` ✔).
- **Whose rights apply: the approver's** (type right, row gate, WITH CHECK, disabled actions, the write shield that
  silently drops unwritable attributes, `ModifiedBy`). There is no impersonation or on-behalf-of API;
  `SparkSystemContext` is claim-based (`A/Authentication/SparkSystemContext.cs:26-41`); `PersistentObjectOperation` has
  no Approve kind.

**Row security and refusals.** Contracts `IRowFilterPolicy` (pushed into RQL) and `IRowCheckPolicy`
(`A/Authorization/IRowPolicy.cs:31-184`); the context carries `Action` (including custom action names) and `User`, but
no document id; per-action row rules are honoured for custom-action rows (`S/Services/CustomActionExecution.cs:349-353`).
Precedents: recipient-only in Notifications (`NotificationActions.cs:22-23,57-67`), privileged-or-member in the IdP
(`OidcApplicationActions.cs:37-52`), per-item owners (`OidcResourceActions.cs:43`). Pending creations in their own
collection make hiding moot; otherwise QnA's `DraftQuestionPolicy` pattern applies
(`apps/QnA/QnA/Security/DraftQuestionPolicy.cs:21-34`). `SparkDenial.Refuse` gives anonymous callers 401 and signed-in
callers 404 (`S/Endpoints/SparkDenial.cs:54-78`; add-ons use `ISparkAddOnEndpoints.Refusal`); `[SparkAuthorize]` answers
**403** (`SparkAuthorizeAttribute.cs:204-206`), so it must not guard per-item decisions; bulk decisions skip silently,
as IdP scopes do (`ReviewActions.cs:53-55`).

**Notifications, mail, audit, expiry, live.**
- `ISparkNotifier.NotifyAsync(SparkNotification)` (`Notif/ISparkNotifier.cs:18-49` ✔) takes **explicit `UserIds` only**
  (no group, slot or right target), plus `Title`, `Body`, `Url`, `OrganizationId`, `Category`, `Urgent`; it commits in
  its **own** session (`Notif/Services/SparkNotifier.cs:51,103`) and has **no dedup key**, so redelivery from a durable
  interceptor can duplicate.
- `ISparkMailer` (`libs/mail/MintPlayer.Spark.MailManager.Abstractions/ISparkMailer.cs:13-95`) has `DeduplicationKey`
  and overridable MJML templates.
- **Nothing lists the members of a slot, group or right.** The only chain is `ResolveGroup` →
  `SecurityConfiguration.Groups` → `UserStore.GetUsersInRoleAsync` (`Auth/Identity/UserStore.cs:349-390`), and it fails
  for `@authenticated` and for request-granted (earned or derived) groups.
- **Audit.** The core contract `ISatelliteAuditSink` (`A/Interceptors/ISatelliteAuditSink.cs:15`) is implemented by
  Moderation (`Mod/Services/ModerationSatelliteAuditSink.cs:10-20`). History stores who in revisions only
  (`libs/history/…/IAuditable.cs:19-58`), with no "approved by" or "on behalf of"; `Facts` reach durable interceptors
  (`SparkCommittedInterceptors.cs:46-83`) but are not persisted.
- **Expiry.** `IExpiring` + `ExpirationRowPolicy` (`A/IExpiring.cs:34-38`; `S/Expiration/ExpirationRowPolicy.cs:28-43`);
  a hand-written `@expires` needs `Registry.RequireDocumentExpiration`. Precedents: McpPlan (10 minutes proposed, then
  kept 365 days), organization invitations (14 days).
- **Live.** Commits publish automatically; a submit, reject or withdraw that touches only the request must call
  `ISparkLiveNotifier.Changed(targetId, collection)` (`A/Live/ISparkLiveNotifier.cs:19-28`; precedent
  `libs/dashboards/…/DashboardNotifier.cs:12-31`), or the target page waits for the ~60 s revalidate.
- **Write veto.** `ISatelliteWriteGuard` (`A/…/ISatelliteWriteGuard.cs:18`; Moderation refuses suspended callers and
  locked documents) should be asked at submit and at apply.

**Custom actions and reason prompts.** Discovery is cross-assembly and first-wins on a duplicate name
(`S/Services/CustomActionResolver.cs:71-102`), so prefix names; a type offers an action iff `{Action}/T` is granted, with
no wildcards (SPARK021); rights on the library's own request type can ship in the library. A reason prompt is
`manager.Retry.Action(...)` with a form PO (`A/Retry/IRetryAccessor.cs:30-37`); the answered form is **not** validated
server-side (`S/Services/RetryAccessor.cs:35-39`), so re-check that the reason is non-empty; the action reruns from the
top on each pass.

**Client (ng-spark).** The add-on registry in `NS/panels/src/spark-panels.ts` (§2.6); `PersistentObject.metadata`
carries add-on facts (Contributions writes them at `ContributionsInterceptor.cs:78`), so a `metadata.approval` entry is
the natural channel. Missing hooks: the title badge ("Deleted" is hard-coded,
`NS/po-detail/src/spark-po-detail.component.html:57-62`); po-edit injects no tokens (only `saved` / `cancelled`
outputs); po-create navigates to `/po/:type/:newId` after a save (`spark-po-create.component.ts:225-231`), a **404 for a
hidden pending creation**; grid rows never read `QueryResultItem.typeHints` (`NS/models/src/query-result.ts:100`); an
inbox link has only a `<spark-shell>` slot, like `spark-review-queue-link`. Diff: the only generic per-attribute diff is
History's `diffRevision(entityType, a, b)` (`NS/history/src/revision-diff.ts:26-39`), which should move to `models` so
approvals do not depend on History; the renderer registry is keyed by renderer name and has **no diff slot**
(`NS/renderers/src/spark-attribute-renderer-registry.ts:4-41`), and a `diffComponent` slot is non-breaking because
`withDeclaredInputs` passes only declared inputs; Contributions' `lineDiff` is generic text
(`NS/contributions/src/line-diff.ts:24`). po-edit's 409 handling (re-read, three-way merge, conflict dialog:
`NS/po-edit/src/spark-po-edit.component.ts:371-556`, `spark-po-conflict-dialog.component.ts:61-175`) is a ready "stale
proposal → resubmit" UX.

**Document proposals specifically.** Applying at decision time through the save path inside
`ISparkWriteUnit.RunInOneCommitAsync`, with `Etag` = the base change vector, makes a 409 a "stale" request. Refuse a
partial apply (the approver's write shield drops attributes silently) with MCP's "same result as proposed" check
(`McpPlanner.cs:232-233`). Types whose change vector moves on their own (e.g. `OidcApplication`, hourly) need an
attribute-level staleness option. Attribution: `ModifiedBy` becomes the approver, and Moderation stamps `AuthorId` as
the current user on `New` (`Mod/Services/ModerationInterceptor.cs:57-61`), so a proposer fact must reach History and
Moderation or the approver becomes the author of an approved create. Exclude satellite (contribution) properties from
proposals and diffs (`GetSparkSatellitePropertyNames`, `ReflectedTypeExtensions.cs:168-200`). If approvers are earned
groups, Moderation's startup check requires the verb in `Spark:Moderation:Earnable` or `NeverEarnable`
(`ModerationStartupCheck.cs:104-119`). A suggested demo host is HR (`Company` with Create + Modify, `Profession` with
Create only), already an E2E host (`tools/test-local.mjs:33`); proposals in their own collection stay out of HR → Fleet
replication.

**Reusable MCP machinery (about 300 lines).** The operation shape with a captured `Etag` (`McpPlan.cs:67-92`);
`PrepareAsync` in `McpPlanner.cs` (version capture `:358-368`, per-attribute before/after `:371-418`); re-validation at
apply (`:227-239`); the one-shot claim (`:168-202`); commit through `ISparkWriteUnit.RunInOneCommitAsync`
(`McpPlanner.cs:263-273` → `S/Services/DatabaseAccess.WriteUnit.cs:25-50,71-105`); conflict mapping
(`:208-214,512-515`). Caveats: the seams are `internal`; apply runs as the current caller, so the "re-render must be
identical" check would misfire when approver ≠ proposer; its diff is text for an LLM. If agents should need a Spark
approver, the research suggests composing it (an agent write on a type configured under `Spark:Approvals:Types` becomes a
proposal with the agent's user as proposer) rather than changing the enum.

### 9.6 Migration sizing

| Workflow | Kind / deciders / handler | Deleted | Added back | Risk |
|---|---|---|---|---|
| IdP developer request | `identity-provider.developer`; deciders: administrators (`IsAdministratorAsync`); handler sets `OidcDeveloper.Status` (the status stays on the user document) | `ApproveDeveloperAction.cs` (126), `OidcDeveloperRequestActions.cs` (45), `OidcDeveloperRequest.json` (85), `DeveloperApproved`/`DeveloperRejected` × 3 languages (135); 3 rights, 2 actions, 1 program unit | ~60–80 | Low–moderate |
| IdP scope approval | `identity-provider.scope`, subject `(app, scope)`; submitted inside the interceptor and `Register.cs`; deciders `Owners` ∪ admins; the handler patches only the row's `Status`; keep `Status` and `AvailableScopes` | `ReviewActions.cs:12-77` (~66), `OidcReviewActions.cs:10-51` (42), `OidcScopeApproval.json` (81), `ScopeApprovalDecided` × 3 (57), `OidcReviewDecision.json` (81) + its class (15) and `OidcApplicationScope.Review`; 6 rights, 2 actions, 1 unit | ~70–90 | Moderate: partial effect while pending, three creation paths, multi-request saves |
| IdP go-live | `identity-provider.go-live`; deciders: admins; handler: `Mode = Live` | `ReviewActions.cs:79-137` (59), `OidcReviewActions.cs:54-74` (21), `OidcGoLiveReview.json` (70), `GoLiveDecided` × 3 (66), the `GoLiveReview` property and its attribute; 3 rights, 2 actions, 1 unit | ~40–60 | Low |
| Organizations join request | `organizations.join`; deciders: Owner/Admin membership of the organization (point-load at decision); handler `NewMembership`; submitted from `RequestToJoinAsync` | About 120–150 lines: the `RequestToJoinAsync` body (`:155-209`), the accept branch (`:234-242`), `JoinRequest(.nl).mjml` (44 ✔), the kind enum value and requester fields, the account-hook branch | ~60–80, plus a data migration for pending production documents | **Moderate**: production SeoTester |

- **Totals:** about 1,100 lines removed: IdP ≈ 960 in 20 whole files (C# 382, model JSON 317, mjml 258), plus 14 rights ✔,
  6 actions ✔, 3 units and ~15 translation leaves, and organizations ≈ 150. About 250–350 lines added back; net roughly
  −750 to −850, before the new library itself.
- **Tests rewritten:** `OidcApprovalTests.cs` (475 lines, 19 tests); E2E `IdentityProviderJourneyTests.cs:79-101`; the 4
  `OrganizationServiceTests` join tests; `SeoTester.Tests/Sites/VerificationTests.cs:98-106`. The gating tests in
  `OidcDynamicRegistrationTests` and `OidcInvitationTests` should pass unchanged.
- **Regenerate** `App_Data/modelHashes.json` and `securityPosture.txt` in SparkId, CodeCoverage, SeoTester and Fleet: the
  IdP's approval types are listed there as inert (e.g. `apps/CodeCoverage/CodeCoverage/App_Data/securityPosture.txt:80-121`),
  and CI fails otherwise (`--spark-verify-model` / `--spark-verify-security`, `.github/workflows/pull-request.yml:110-149`).
- The organization join request is the one row to drop if zero production change is wanted; nothing else depends on it.
  The research still recommended it as the second per-item-resolver consumer (proof that the engine is generic, not
  IdP-shaped) and because it closes real production gaps.

### 9.7 Suspected defects found along the way

Read-only findings; each needs a red test before a fix.

1. **IdP: a team can approve its own pending or rejected scope (suspected; security).**
   `OidcApplicationScope.Status` is `isReadOnly: false` (`IdP/App_Data/Model/OidcApplicationScope.json:46-54` ✔);
   developers hold `ReadEditNewDelete/OidcApplicationScope` (`IdP/App_Data/security.json:51-55` ✔);
   `ApplyScopeApprovalsAsync` only resets the status of **added** scopes (`OidcApplicationInterceptors.cs:171-180`) and
   nothing restores a kept scope's `Status` / `Review` from `context.Before`. So a save that posts `"Approved"` on an
   existing row may serve the scope to everyone (`OidcApplicationAccess.cs:23-29`). The sweep traced the write gate
   through `EntityMapper.cs:672-680,810-844`. The existing test
   `Someone_who_does_not_own_the_api_cannot_approve_its_scope` (`OidcApprovalTests.cs:342`) covers only the action.
2. **Moderation: double side effects on a concurrent decide (suspected).** `DecideAsync` checks `Open` in one session
   (`SparkModeration.cs:493-498` ✔), runs the side effects (settling flags, reversing votes, merging, suspending), and
   only then writes the status in a second session (`:542-551` ✔). Two concurrent decisions can both pass the check and
   both run their side effects.
3. **IdP decision gaps:** `OidcReviewDecision.Note` and `OidcDeveloper.Note` are never written, so no reject reason
   exists; `OidcDeveloperStatuses.Revoked` is read but never produced; deciders are never notified of new requests; a
   go-live request overwrites a pending one.
4. **Organizations:** Admins can see join requests but cannot approve them in-app; there is no decline; the requester is
   never told.
5. **MCP / #482 misreport.** `PersistentObjectSavePath.UpdateAsync` swallows `SparkCancelException` and returns the stored
   row (`S/Services/PersistentObjectSavePath.cs:165-170` ✔); MCP's `WriteAsync` does not notice (`McpPlanner.cs:471-473`)
   and reports "all N operation(s) were saved" (`libs/mcp/MintPlayer.Spark.Mcp/Tools/SparkWriteTools.cs:56`). Any
   cancelling interceptor triggers it today; a proposal-producing seam would make it routine.

Stale documentation noticed: the rationale at `Mod/Documents/ModerationDocuments.cs:3-4`; "403 on create" at
`docs/guide-row-security.md:153`; the renderer guide still says "three slots" (`docs/guide-custom-attribute-renderers.md:20-26`).

---

## 10. Conventions for the PRD and a new demo app

**PRD naming and outline.** `docs/<topic>_PRD.md` + `docs/<topic>_plan.md` (or `issue_NNN_*`); this work's PRD is
`docs/commerce_PRD.md`. Copy the outline of `docs/seo_analyzer_PRD.md`:
- Header `# <Name> (#issue): PRD and plan`, a Status line, the issue link, and "the decision log is authoritative".
- §0 a status table (research, grilling rounds, spikes, milestones; ✅/⏳, date, SHA) · 1 Goal (+ non-goals) · 2 What
  Spark already gives us (Need | Feature | Evidence file:line) · 3 Product scope · 4 Architecture (D1..Dn) · 5 Framework
  additions X1..Xn (Addition | Why | Evidence of the gap; same PR) · a spikes table (# | Spike | Question | Done when),
  with results recorded as "Result (date, env) … Pass." or "### Spike S1 (date)" · Out of scope (with a reason) ·
  Milestones (one PR; M0 = spikes; tests batched at the end) · Open decisions Q-Mxa with options and a recommendation ·
  a decision log of "### Round N (owner, date)" entries, `**Dn (Gx): …** *Evidence: …*` · Risks · Verification /
  acceptance.

**Plan file.** Points to the PRD; states the rules (commit per milestone, no per-milestone test runs, raw logs to the
scratchpad, a minor version bump of touched libraries' csproj and ng-spark, no backward compatibility); one
`### Mx — title` per milestone with `- [ ]` checklists carrying file:line.

**Related material already in the repository.** Nothing commerce-, ticket- or queue-specific (and on GitHub only #521 in
this repo). Precedents: compare-exchange reservations for email and passkey uniqueness (`docs/guide-passkeys.md:145`);
messaging's `QueueAdmission` (reservation horizon, slot admission:
`tests/MintPlayer.Spark.Tests/Messaging/QueueAdmissionTests.cs`, `SequenceSourceSpikeTests.cs`);
`docs/guide-live-updates.md` (`/spark/live` + `ISparkLiveNotifier`); `docs/guide-expiration.md` (`IExpiring`, the 36 h
floor that rules out minute-level holds); `docs/guide-concurrency.md`, `docs/guide-quotas.md`,
`docs/guide-rate-limiting.md`, `docs/guide-settings.md`, `docs/guide-organizations.md`, `docs/guide-cross-module-sync.md`.

**A new app under `apps/`.** No template; the recipe is `docs/seo_analyzer_PRD.md` §4.1 plus its log entry "M7
(2026-10-10)". Layout `apps/X/X`, `X.Library`, `X.Tests`; its own database, cookie `.SparkAuth.X` and user-secrets id.
Hand-maintained lists to update: the root `package.json` workspaces; `MintPlayer.Spark.slnx`;
`.github/workflows/pull-request.yml` (the nx build list near line 101, the verify-model and verify-security loops near
lines 112 and 137); `tools/verify-ngsw-config.test.mjs`; `tools/test-local.mjs` (E2E; a new E2E host app joins
`E2E_APPS`, per `CLAUDE.md`); `regenerate.yml`; the Dockerfile and deploy files if deployed; the SparkId dev
relying-party migration if it signs in through SparkId.

| App | Ports (https / http) |
|---|---|
| Fleet | 5003 / 5004 |
| HR | 5005 / 5006 |
| DemoApp | 5007 / 5008 |
| QnA | 5009 / 5010 |
| SparkId | 5011 / 5012 |
| SeoTester | 5013 / 5014 |
| CodeCoverage | 5200 / 5201 |
| **Next free** | **5015 / 5016** (IIS Express 16757) |

**Gates a new library trips.** Regenerate `App_Data/modelHashes.json` and `securityPosture.txt` in every app the library
reaches (CI runs `--spark-verify-model` / `--spark-verify-security`, `.github/workflows/pull-request.yml:110-149`). New
endpoints change the route snapshots (`apps/QnA/QnA.Tests/RouteSnapshots/QnA.txt`,
`tests/MintPlayer.Spark.Tests/Endpoints/RouteTableSnapshotTests.cs:66-67`). Unit tests go in `tests/MintPlayer.Spark.Tests`
(template `SoftDelete/SoftDeleteTests.cs:34-74`), with fixture types given a unique prefix because Actions classes are
matched by simple name; E2E runs on a `SparkAppTestHost`.

---

## 11. Sources

**Repository documents cited most.** `docs/guide-live-updates.md` · `docs/guide-expiration.md` ·
`docs/guide-concurrency.md` · `docs/guide-quotas.md` · `docs/guide-rate-limiting.md` · `docs/guide-settings.md` ·
`docs/guide-organizations.md` · `docs/guide-row-security.md` · `docs/guide-authorization.md` ·
`docs/guide-library-layers.md` · `docs/spark_composition_PRD.md` · `docs/guide-custom-actions.md` ·
`docs/guide-manager-retry-actions.md` · `docs/guide-controllers.md` · `docs/guide-share-links.md` ·
`docs/guide-passkeys.md` · `docs/guide-data-protection.md` · `docs/guide-asdetail-attributes.md` ·
`docs/guide-triggers-refresh.md` · `docs/guide-reference-attributes.md` · `docs/guide-custom-attribute-renderers.md` ·
`docs/guide-dates-and-sorting.md` · `docs/guide-contributions.md` · `docs/guide-cross-module-sync.md` ·
`docs/issue_460_PRD.md` · `docs/contributions_PRD.md` · `docs/seo_analyzer_PRD.md` ·
`docs/code-coverage/product-overview.md` · `docs/endpoints_generator_webhooks_exception.md` · `Msg/README.md` ·
`libs/cron/MintPlayer.Spark.Cron/README.md` · `CLAUDE.md`.

**RavenDB.** https://docs.ravendb.net/7.2/client-api/session/configuration/how-to-enable-optimistic-concurrency ·
https://docs.ravendb.net/server/clustering/cluster-transactions · https://docs.ravendb.net/7.1/compare-exchange/atomic-guards ·
https://docs.ravendb.net/compare-exchange/atomic-guards · https://docs.ravendb.net/compare-exchange/overview ·
https://docs.ravendb.net/7.2/compare-exchange/overview · https://docs.ravendb.net/7.1/document-extensions/counters/overview ·
https://docs.ravendb.net/7.1/server/extensions/expiration · https://docs.ravendb.net/7.2/server/kb/javascript-engine ·
https://docs.ravendb.net/7.2/client-api/operations/patching/set-based ·
https://ayende.net/blog/190978-B/complex-distributed-transactions-with-ravendb

**Payments.** https://docs.stripe.com/api/checkout/sessions/create · https://docs.stripe.com/api/checkout/sessions/expire ·
https://docs.stripe.com/payments/place-a-hold-on-a-payment-method · https://docs.stripe.com/api/idempotent_requests ·
https://docs.stripe.com/checkout/fulfillment · https://docs.stripe.com/webhooks ·
https://docs.stripe.com/api/payment_methods/object.md?query=card · https://docs.stripe.com/payments/3d-secure/authentication-flow ·
https://docs.stripe.com/radar/rules · https://docs.mollie.com/docs/handling-payment-status ·
https://docs.mollie.com/payments/status-changes · https://docs.mollie.com/docs/place-a-hold-for-a-payment (not fetched in
one pass) · https://docs.mollie.com/reference/extra-payment-parameters · https://docs.mollie.com/payments/recurring ·
https://help.mollie.com/hc/en-gb/articles/14199431980050

**Commerce platforms.** https://docs.pretix.eu/en/latest/development/concepts.html · https://docs.pretix.eu/guides/event-series/ ·
https://docs.pretix.eu/guides/products/ · https://docs.pretix.eu/dev/api/guides/custom_checkout.html ·
https://docs.pretix.eu/dev/api/resources/waitinglist.html · https://railway.com/deploy/pretix ·
https://docs.pretix.eu/dev/api/resources/seats.html · https://docs.pretix.eu/dev/api/resources/seatingplans.html ·
https://docs.medusajs.com/resources/commerce-modules · https://docs.medusajs.com/resources/commerce-modules/inventory/concepts ·
https://docs.vendure.io/guides/core-concepts/stock-control/ · https://docs.saleor.io/developer/stock/stock-reservation ·
https://docs.saleor.io/developer/checkout/stock · https://docs.saleor.io/developer/checkout/order-expiration ·
https://docs.saleor.io/developer/stock/stock-allocation ·
https://help.shopify.com/en/manual/products/inventory/managing-inventory-quantities/inventory-states ·
https://shopify.dev/docs/apps/fulfillment/inventory-management-apps · https://docs.commercetools.com/tutorials/reserve-stock-on-cart ·
https://www.eventbrite.com/platform/docs/ticket-classes

**Ticketing and seat maps.** https://www.pymnts.com/fraud-prevention/2019/how-ticketmaster-rewards-fans-bars-fraudsters-security/ ·
https://business.ticketmaster.com/safetix/ ·
https://www.404media.co/ticketmaster-used-revolving-barcodes-to-control-ticket-resale-market-and-surveil-customers-doj-alleges/ ·
https://docs.seats.io/docs/api/hold-tokens · https://docs.seats.io/docs/renderer/config-session ·
https://docs.seats.io/docs/api/temporarily-hold-objects · https://docs.seats.io/docs/renderer/config-selectionvalidators ·
https://seatmap.pro/blog/seatmap-pro-vs-seats-io/ · https://seatmap.pro/knowledge-base/releases/1.72.0/ ·
https://www.npmjs.org/package/@alisaitteke/seatmap-canvas-react · https://github.com/ticketevolution/seatmaps-client ·
https://npmjs.com/package/seat-picker · https://pub.dev/packages/venue_seat_picker ·
https://seatmap.pro/blog/how-to-build-a-javascript-seating-chart/

**Waiting rooms and bot vendors.** https://developers.cloudflare.com/waiting-room/about/ ·
https://developers.cloudflare.com/waiting-room/reference/queueing-methods/ · https://blog.cloudflare.com/waiting-room-event-scheduling/ ·
https://queue-it.com/pre-queue/ · https://www.queue-it.com/blog/first-in-first-out-randomization · https://queue-it.com/product/scheduled/ ·
https://ramint.gov.au/about-us/frequently-asked-questions/faqs-purchases/queue-it · https://queue-it.com/developers/how-queue-it-works ·
https://queue-it.com/cookie-statement-user · https://github.com/sarooghi/KnownUser.V3.PHP · https://github.com/sarooghi/KnownUser.V3.ASPNET ·
https://github.com/queueit/KnownUser.V3.ASPNET · https://github.com/queueit/QueueToken.V1.JAVA ·
https://socket.dev/npm/package/@queue-it/queue-token/overview/^1.0.4 · https://queue-it.com/bot-detection-protection/ ·
https://queue-it.com/blog/proof-of-work-block-bad-bots/ · https://www.queue-it.com/how-does-queue-it-work/ ·
https://docs.stclab.com/products/netfunnel/user-guide/segment/section-control-segment/advanced-queue-position-retention ·
https://Queue-Fair.com/beating-sneaker-bots-ticket-bots-scalpers-and-touts ·
https://support.activenetwork.com/camps/articles/en_US/Article/Queue-It-For-High-Traffic-Times ·
https://ticketmaster.substack.com/p/how-a-ticketmaster-queue-actually ·
https://blog.cloudflare.com/banish-bots-from-your-waiting-room-and-improve-wait-times-for-real-users/ ·
https://blog.cloudflare.com/ai-labyrinth/ · https://www.akamai.com/products/bot-manager ·
https://www.infosecurity-magazine.com/news/akamai-bot-manager-takes-aim-at/ · https://get.kasada.io/hubfs/Kasada-Data-Sheet.pdf ·
https://datadome.co/learning-center/how-to-stop-online-bot-ticket-scalping-and-other-ticketing-industry-bot-threats/

**Threat and economics.** https://ag.ny.gov/sites/default/files/reports/Ticket_Sales_Report.pdf ·
https://www.abajournal.com/news/article/new_york_ag_says_ticket_sales_for_concerts_sporting_events_is_a_fixed_game ·
https://indiantime.net/story/2017/05/18/news/nys-ag-announces-419-million-in-settlements-with-6-companies-illegally-purchasing-and-reselling-concert-tickets/24683.html ·
https://www.ftc.gov/news-events/news/press-releases/2021/01/ftc-brings-first-ever-cases-under-bots-act ·
https://www.ftc.gov/news-events/news/press-releases/2025/08/ftc-takes-action-against-ticket-resellers-using-illegal-tactics-bypass-ticket-limit-protections ·
https://relix.com/news/detail/ftc-files-bots-act-suit-against-key-investment-group/ ·
https://www.ftc.gov/news-events/news/press-releases/2025/09/ftc-sues-live-nation-ticketmaster-engaging-illegal-ticket-resale-tactics-deceiving-artists-consumers ·
https://www.ftc.gov/system/files/ftc_gov/pdf/p994811-ftc-oversight-testimony-senate-commerce-committee-2026.pdf ·
https://business.ticketmaster.com/?p=51362 · https://netacea.com/blog/how-scalper-bots-evaded-detection-to-snatch-oasis-tickets ·
https://www.f5.com/labs/articles/threat-intelligence/how-a-sneaker-bot-earned-2m-profit-from-one-shoe-drop ·
https://www.vice.com/en/article/the-cure-tried-to-stop-scalpers-brokers-are-selling-entire-ticketmaster-accounts-instead/ ·
https://www.ticketnews.com/2017/12/verified-fan-creates-new-marketplace-presale-password-resale/ · https://netacea.com/?p=2825 ·
https://pagecrawl.io/blog/what-is-a-bot-sneaker-bots-explained.md · https://support.google.com/mail/answer/7436150?hl=en ·
https://research.aimultiple.com/captcha-solving-services ·
https://dev.to/ren_joyce_cd41204d5cb261f/how-to-bypass-cloudflare-turnstile-captcha-best-solver-43jo ·
https://research.aimultiple.com/proxy-pricing ·
https://www.imperva.com/blog/2025-imperva-bad-bot-report-how-ai-is-supercharging-the-bot-threat/ ·
https://thehackernews.com/2026/01/google-disrupts-ipidea-one-of-worlds.html ·
https://owasp.github.io/www-project-automated-threats-to-web-applications/assets/oats/EN/OAT-021_Denial_of_Inventory.html ·
https://owasp.org/www-project-automated-threats-to-web-applications/assets/oats/EN/OAT-005_Scalping ·
https://www.digitalmusicnews.com/2024/07/11/ticketmaster-tickets-reverse-engineered/ ·
https://data.pollstar.com/chart/2018/10/Ticketmaster%20Response%20(Final)_680.pdf ·
https://faroutmagazine.co.uk/oasis-tickets-cancelled-ticketmaster/ · https://glastonburyfestivals.co.uk/ ·
https://www.ticketfairy.com/blog/2025/07/11/case-study-glastonbury-festivals-ticket-registration-system/ ·
https://www.ticketone.it/campaign/rivendita/

**Bot tooling and detection.** https://blog.castle.io/from-puppeteer-stealth-to-nodriver-how-anti-detect-frameworks-evolved-to-evade-bot-detection/ ·
https://scrapfly.io/blog/posts/best-stealth-browsers · https://clawbrowser.ai/blog/puppeteer-extra-stealth-is-dead/ ·
https://curl-cffi.readthedocs.io/en/v0.11.4/impersonate.html · https://arxiv.org/abs/2409.08831 ·
https://tech.yahoo.com/ai/articles/chatgpt-passes-m-not-robot-140447528.html · https://blog.cloudflare.com/secure-agentic-commerce/ ·
https://blog.cloudflare.com/ja4-signals/ · https://www.stamus-networks.com/blog/ja3-fingerprints-fade-browsers-embrace-tls-extension-randomization ·
https://pypi.org/project/ja4plus/ ·
https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.https.httpsconnectionadapteroptions.tlsclienthellobytescallback?view=aspnetcore-10.0 ·
https://github.com/dotnet/aspnetcore/issues/60805 · https://developers.cloudflare.com/rules/transform/managed-transforms/reference/ ·
https://corgea.com/advisories/vulnerabilities/CVE-2026-45364 · https://www.sentinelone.com/vulnerability-database/cve-2026-15144/ ·
https://blog.google/security/protecting-cookies-with-device-bound-session-credentials/ ·
https://thehackernews.com/2026/04/google-rolls-out-dbsc-in-chrome-146-to.html ·
https://dev.to/abdullah_afzal/detecting-icloud-private-relay-and-not-treating-it-like-a-vpn-1163 ·
https://friendlycaptcha.com/insights/cloudflare-turnstile-accessibility/

**Proof-of-work, CAPTCHA, Private Access Tokens.** https://github.com/altcha-org/altcha-lib · https://github.com/altcha-org/altcha ·
https://cdn.jsdelivr.net/npm/altcha@3.2.3/README.md · https://altcha.org/docs/complexity · https://altcha.org/docs/open-source-vs-paid/ ·
https://altcha.org/docs/v2/sentinel/pricing · https://www.nuget.org/packages/Ixnas.AltchaNet · https://www.cl.cam.ac.uk/~rnc1/proofwork.pdf ·
https://sites.cs.ucsb.edu/~rich/class/cs293b-cloud/papers/lui-pow.pdf · https://lock.cmpxchg8b.com/anubis.html ·
https://en.wikipedia.org/wiki/Anubis_(software) · https://github.com/mCaptcha/mCaptcha · https://trycap.dev/guide/alternatives/friendlycaptcha ·
https://developer.friendlycaptcha.com/docs/v2/guides/eu-endpoint · https://friendlycaptcha.com/?p=16210 ·
https://european-alternatives.eu/product/friendly-captcha · https://developer.apple.com/videos/play/wwdc2022/10077/ ·
https://www.rfc-editor.org/rfc/rfc9578.html · https://www.rfc-editor.org/rfc/rfc9577.html ·
https://blog.cloudflare.com/eliminating-captchas-on-iphones-and-macs-using-new-standard/ ·
https://developers.cloudflare.com/waf/tools/privacy-pass · https://developers.cloudflare.com/turnstile/concepts/widget/ ·
https://developers.cloudflare.com/turnstile/get-started/server-side-validation/ ·
https://developers.cloudflare.com/turnstile/frequently-asked-questions/ · https://developers.cloudflare.com/turnstile/plans/ ·
https://blog.cloudflare.com/turnstile-ephemeral-ids-for-fraud-detection/ · https://www.npmjs.com/package/ngx-turnstile ·
https://hcaptcha.com/llms.txt · https://www.hcaptcha.com/learning/accessibility/hcaptcha-accessibility/ ·
https://friendlycaptcha.com/insights/hcaptcha-accessibility/ · https://docs.cloud.google.com/recaptcha/docs/compare-tiers ·
https://docs.cloud.google.com/recaptcha/docs/libraries · https://phpcaptcha.org/recaptcha-gdpr-problem/ ·
https://hunton.com/privacy-and-information-security-law/cnil-issues-e125000-fine-against-e-scooter-rental-company

**Identity.** https://www.twilio.com/docs/lookup/v2-api ·
https://www.twilio.com/en-us/blog/developers/best-practices/protect-twilio-account-phone-number-fraud ·
https://developer.apple.com/forums/thread/726208 · https://www.corbado.com/faq/why-some-platforms-do-not-support-attestation-for-passkeys.md ·
https://www.corbado.com/blog/passkeys-e2e-playwright-testing-webauthn-virtual-authenticator ·
https://crowdhandler.com/blog/the-rise-of-sneaker-bots-how-we-stop-them-and-how-you-can-too

**Fairness, accessibility, privacy.** https://blog.cloudflare.com/detecting-cgn-to-reduce-collateral-damage/ ·
https://github.com/hroost/icloud-private-relay-iplist · https://shieldlabs.ai/blog/icloud-private-relay · https://www.w3.org/TR/turingtest/ ·
https://www.w3.org/WAI/WCAG22/Understanding/accessible-authentication-minimum.html ·
https://www.twobirds.com/en/insights/2025/a-guide-to-navigating-the-european-accessibility-act-for-online-retailers-service-providers-and-plat ·
https://edpb.europa.eu/system/files/2024-10/edpb_guidelines_202302_technical_scope_art_53_eprivacydirective_v2_en_0.pdf ·
https://Datenschutz.Hessen.de/sites/datenschutz.hessen.de/files/2022-11/wp_194.pdf ·
https://www.aoshearman.com/en/insights/ao-shearman-on-data/cjeu-rules-that-a-credit-score-constitutes-automated-decision-making-under-the-gdpr ·
https://dev.to/alexisfranorge/why-your-honeypot-catches-humans-not-bots-3bcd · https://support.mozilla.org/af/questions/1279049 ·
https://community.perchcms.com/forum/thread/366-forms-honeypot-false-positives/

**Law.** US: https://wiley.law/alert-Executive-Order-on-Ticket-Resale-Market-Calls-for-Greater-FTC-Enforcement ·
https://www.govinfo.gov/content/pkg/DCPD-202500418/pdf/DCPD-202500418.pdf (plus the FTC pages above). EU:
https://eur-lex.europa.eu/eli/dir/2019/2161/oj/eng · https://eur-lex.europa.eu/eli/dir/2005/29/oj ·
https://technologyquotient.freshfields.com/post/102lr1q/digital-fitness-check-and-digital-fairness-act-part-8-dropshipping-bots-ticket ·
https://www.euroconsumers.org/can-the-digital-fairness-act-finally-end-ticket-resale-turmoil/ ·
https://www.legislation.gov.uk/eudr/2011/83/article/9 · https://www.legislation.gov.uk/eudr/2011/83/article/16. Belgium:
https://www.lexgo.be/en/news-and-articles/4487-reselling-your-tickets-for-summer-festivals-is-not-a-good-idea ·
https://www.actualitesdroitbelge.be/droit-des-affaires/dr-du-consommateur/la-loi-sur-la-revente-de-tickets-devant-la-cour-constitutionnelle/la-revente-de-tickets---article-5-de-la-loi-du-30-juillet-2013 ·
https://news.economie.fgov.be/227111-let-op-waar-je-je-concert-of-festivalticket-koopt/ · https://www.mlex.com/mlex/articles/2425821 ·
https://www.theticketingbusiness.com/2024/06/ticombo-takes-legal-action-against-uefa-and-belgian-state/. UK:
https://www.legislation.gov.uk/uksi/2018/735/made · https://djmag.com/news/uk-government-ban-resale-of-live-event-tickets-above-face-value ·
https://www.rpclegal.com/snapshots/consumer/spring-2026/uk-government-to-introduce-new-rules-banning-ticket-touting/. Ireland:
https://www.gov.ie/ga/preasraitis/71f1a-new-law-banning-ticket-touting-comes-into-force/. Italy:
https://www.ilpost.it/2019/06/25/biglietti-nominali-concerti/.

**Tax and e-invoicing.** https://legislation.gov.uk/eudr/2006/112/article/53 ·
https://internationaltaxreview.com/article/2ckmtgmklnroebnuekruo/ · https://einvoice.belgium.be/en (official) ·
https://www.forvismazars.com/be/en/insights/newsletters-client-alerts/newsletters/mandatory-b2b-e-invoicing-in-belgium (vendor) ·
https://storecove.com/blog/en/transition-to-mandatory-b2b-e-invoicing-in-belgium (vendor) ·
https://econnect.eu/en/blog/belgium-announces-mandatory-b2b-e-invoicing-from-2026 (vendor; no longer covers Belgium as
fetched).

**Caveats on sources.** Vendor pages (Queue-it, Cloudflare, Akamai, Kasada, Netacea, Imperva, ALTCHA, the CAPTCHA
competitors, the e-invoicing service providers) were used for mechanisms more than for effectiveness numbers. Prices for
solvers, proxies and CAPTCHA plans come partly from comparison sites and move often. The Belgian article-5 wording comes
from legal commentary, and the Act is under EU-law challenge. The UK face-value ban is a draft. The FTC cases are
allegations. The CRD text was read from legislation.gov.uk's archived copy, not EUR-Lex.
