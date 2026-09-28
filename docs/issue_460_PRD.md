# PRD — Issue #460: framework work for the MintPlayer migration

Composable row policies & interceptors, SoftDelete + History, auth fixes, MailManager + queue
throttling, and Moderation — **all in one pull request** (the issue proposed two; the owner decided on
one, 2026-09-28).

- Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/460 (closes #432, #299, #283, #285)
- Consumer plan: `C:\Repos\MintPlayer\docs\PRD-Spark-Completion.md` (D1–D34, S1–S10)
- Implementation plan: [issue_460_plan.md](issue_460_plan.md)
- Baseline: master `ebc566dd`. Packages `11.0.0-preview.90` (NuGet), `ng-spark 22.23.0`, `ng-spark-auth 22.13.0`.

**The framework is in preview: breaking changes are allowed** and ship as minor bumps with release
notes (NuGet major stays 11, npm major stays 22 — see CLAUDE.md "Versioning").

---

## 1. What already exists on master (investigated 2026-09-28)

| Area | State |
|---|---|
| Single-subscription messaging (#369): `BroadcastOnceAsync`, `DelayBroadcastAsync`, `NonRetryableException`, `ICheckpointRecipient`, leader lease, claims + renewal, stranded reclaim, `HandlerTimeout`, per-queue FIFO lanes, dead-lettering, both `SubscriptionMode`s | **EXISTS** — the "MessageQueue stuff already on master". `docs/decisions_messaging_and_project_automation.md` line 14 ("neither is implemented") is stale — fix it in this PR. |
| Per-queue throttling, per-queue options, `IMessageProgress` | **MISSING** — `[MessageQueue]` carries only `QueueName`; all options global. |
| `BroadcastOnceAsync` dedup key | **BUG** — `Sanitize` maps every non-`[A-Za-z0-9-_.]` char to `_`, so `c1:a_b@x.com` and `c1:a@b_x.com` collide (a campaign silently skips a recipient). Not namespaced by type, unbounded length. |
| Handler access to its own message id | **MISSING** (needed for VERP / stable Message-ID). |
| `SubscriptionPerQueue` mode | **BUG (side finding)** — `HandlerTimeout` and claim renewal not applied (`MessageSubscriptionWorker.cs:110`). Fix in this PR. |
| Mail | No package. `ISparkLinkConfirmationSender<TUser>` contract exists; CodeCoverage has its own inline (awaited inside the login callback, not queued) `System.Net.Mail` `SmtpLinkConfirmationSender`. `IEmailSender<TUser>` is always Identity's no-op. |
| Row security | `IRowSecurity` (scoped) is the single point every row decision passes through, with a per-request (type, action) filter cache. No multi-registration mechanism anywhere in `libs/spark`. |
| #283 (breadcrumb bypass) | **Fixed in code** (`BreadcrumbResolver.cs:131-161`, commit `c1c12b9b`), issue still open → close with a regression test that includes a policy. |
| #285 (projection push-down) | **Not fixed** (`RowSecurity.cs:306-320`) → fixed here (D2). |
| `DisableActions` | Affordance only; enforced nowhere (`Get.MergeClientWithholds`: "an affordance, not a permission"). |
| Custom action results | Envelope already has a `Result` field; `ICustomAction.ExecuteAsync` returns `Task`; ng-spark discards `envelope.result`. |
| Auth | Login = Microsoft `MapIdentityApi` verbatim (email looked up as user name). Passkeys (#442) exist. Link/unlink endpoints exist. No GDPR code, no Data Protection config in `libs/`. Authenticator key + tokens plaintext. |
| Timezone | Header-only resolver (`RequestTimeZoneResolver.cs:51-65`), no validation; no SSR anywhere in the repo. |
| SocketExtensions 11.x | `11.0.1-preview.86` is already on nuget.org → **no work**; reply on the issue. Stable must be `11.0.1` (11.0.0 hard-deleted). |
| Wildcard rights | Supported by the matcher (`ResourcePattern`), used by **no** security.json in any app or MintPlayer → removed (D3). |
| Forwarded headers | MintPlayer, DemoApp, HR, Fleet **trust X-Forwarded-For from anyone** (KnownNetworks/Proxies cleared) — the #436 bug again → framework fix (D15). |

---

## 2. Decisions (grilling session 2026-09-28)

Locked with the owner. Do not re-litigate without new evidence.

| # | Decision |
|---|---|
| D1 | **Interceptors run in `DatabaseAccess`**, around the existing `actions.OnSaveAsync`/`OnDeleteAsync` calls — additive, not a pipeline rewrite. Delete *replacement* is decided there (so an `OnDeleteAsync` override cannot defeat SoftDelete). The base class's after-save row check (WITH CHECK) is rerouted through `IRowSecurity` so policies apply on save. **No other core pipeline moves**; the known override gaps (an `OnLoadAsync`/`OnSaveAsync` override that skips base skips the row gate / WITH CHECK; references not row-checked on save) are **documented, not fixed**. |
| D2 | **General #285 fix**: entity row filters are rebound onto the index projection by member name and pushed down; if a projection lacks a member the filter uses, fall back to today's post-filter. Closes #285. |
| D3 | **Wildcard rights removed entirely** (grants *and* denies). Validator + analyzer reject `*` with an error pointing at the composite rights; matcher branches and the posture "floor" warning deleted. Rationale: GDPR / ISO 27001 access reviews need an enumerable "who can do what". Deny-by-default for new types is accepted busywork. |
| D4 | **Sign in with email or user name.** `SparkSignInManager` override: identifier containing `@` → `FindByEmailAsync`, and only if *no account* is found → `FindByNameAsync`; no `@` → `FindByNameAsync`. A wrong password never falls through to a second candidate. **User-validator rule: a user name containing `@` must equal that user's own email** (collision impossible by construction). 2FA + recovery-code steps use the resolved user. OIDC `/connect/login` uses the same resolver. Login label: "Email or user name". Pre-cutover: count MintPlayer accounts violating the rule. |
| D5 | **Data Protection always on, in Spark core.** Persisted keys required outside Development: `Spark:DataProtection:KeysPath` (file system) or `Spark:DataProtection:Storage=RavenDb`; unset → startup error explaining the redeploy sign-out. Development defaults to a local folder. `Spark:DataProtection:ApplicationName` configurable (default entry assembly name; **CodeCoverage keeps `CodeCoverage`**). Raven store keeps the `DataProtectionKeys/` prefix; CodeCoverage's hand-written setup + repository deleted. Docs state loudly: an unmounted key folder loses its keys on redeploy — the operator's responsibility. Authenticator key + external-login tokens stored as `sdp1:` + Protect(value); legacy plaintext still read; idempotent backfill. **Owner decision (2026-09-28): production (CodeCoverage, and MintPlayer once absorbed) uses `KeysPath` on a mounted volume via `Spark__DataProtection__KeysPath`; test hosts set `Storage=RavenDb` in their own config; no app's base `appsettings.json` sets either (both set refuses startup). CodeCoverage users are signed out once on the first deploy; its old `DataProtectionKeys/` documents are orphaned.** |
| D6 | **`RequireConfirmedEmail`** option, default `false`. Startup guard: registration enabled + no-op email sender → error, unless `Spark:Auth:AllowUnconfirmedRegistration=true`. **Completing a password reset (valid token) sets `EmailConfirmed=true`**, and `forgotPassword` sends to unconfirmed addresses (rescues MintPlayer's ~650 unconfirmed users). Demo apps use MailManager pickup-folder mode. Closes #299. |
| D7 | **Per-provider verified-email trust** for social sign-up. Each provider preset declares its verified-email signal; Microsoft trusted only for personal accounts (consumers tenant); a provider without a reliable signal → account created unconfirmed + confirmation mail. Add Twitter/X and LinkedIn presets. **User name = slug of display name** (`john-doe`, `john-doe-2`), editable on the profile page (never the email local part). Spike SP-C required. |
| D8 | **GDPR.** `DELETE /spark/auth/manage/account` (re-authentication required) → all `ISparkAccountDeletionHandler<TUser>` (multi-registered) → `UserStore.DeleteAsync` last (releases email/passkey reservations; a handler failure leaves the account intact and retryable). `GET /spark/auth/manage/personal-data` → account JSON + all `ISparkPersonalDataContributor<TUser>`. Audit fields hold the **user id only**, resolved to a name at read time ("deleted user" afterwards). **Revisions are not rewritten** — stated in the guide; content-level personal data is the app's handler's job. Moderation ships a deletion handler. |
| D9 | **MailManager is building blocks, configured through `IConfiguration`.** Pluggable `ISparkMailTransport`: SMTP (MailKit) when `Spark:Mail:Smtp:Host` is set (Security `None`/`StartTls`/`SslOnConnect`/`Auto` — no forced STARTTLS); `.eml` pickup folder when `Spark:Mail:PickupFolder` is set; custom transport replaces both; none → startup error. Bounce handling opt-in: `Spark:Mail:Bounces:VerpDomain` enables VERP; `Spark:Mail:Bounces:Endpoint:Enabled` + secret maps `POST /spark/mail/bounces`; DSN parser pluggable, **the secret check stays in the framework, before the parser**. Suppression list always present (checked at publish and send; fed by bounces, unsubscribes, public `ISparkMailSuppressions`). One-click `List-Unsubscribe` (RFC 8058) per message type (default on for bulk). Dev mode: `Spark:Mail:Development:RedirectTo` or pickup folder. Tier-1 (local Postfix pipe) and tier-2 (MX + inbound 25) are documented deployment recipes; the framework doesn't care which. |
| D10 | **CodeCoverage moves onto MailManager in this PR** (production dogfooding). **Pre-merge ops step:** set `Spark:Mail:*` on the VPS before merging (Data Protection needs no VPS step: `docker-compose.yml` carries the key-ring volume and `KeysPath`, see D5) (merge auto-redeploys). Update CodeCoverage's three hand-written lists (Dockerfile COPY, deploy path filter, `implicitDependencies`). |
| D11 | **New `apps/QnA` demo** proves Moderation. A generic E2E base host is extracted from `FleetTestHost` **as a pure refactor, Fleet's suite green before any QnA test**. QnA joins the model-sync and posture CI loops; name checked against `.gitignore`. |
| D12 | **Earned privileges are bounded.** `moderation.json` references security.json groups **by id**; validated at startup (exists, not well-known, holds only earnable rights). `Lock`, `Suspend`, `Purge`, `Restore`, `Revert`, `ViewDeleted` are **never earnable**; explicit `earnable` escape hatch in moderation.json, also validated against the destructive list. Core: `AddGroupMembershipProvider<T>()` **composes** (merges results; `Use…` keeps replace semantics), providers may return group ids, resolution cached per request in the evaluator. |
| D13 | **`IDisablable` + `OnDisableActionsAsync`** — the single source of truth for disabled actions (deliberately deviates from Vidyano). `IDisablable.DisableActions(params string[])` is the *only* member that knows the method; implemented by `PersistentObject` and the query result. Empty virtual `OnDisableActionsAsync(IDisablable target, DisableActionsContext ctx)` on the actions class. The framework calls it **at page load** (result → returned `DisabledActions`) **and at submit** (parent, query, each selected row — union). Covers **every action, built-in (Edit/Save/Delete/New) and custom**. Refused with **403 + action name, only after the row gate passed** (a hidden row stays 404 — #453 lesson). Old entry points **deleted**: `PersistentObject.DisableActions`, `ClientAccessor.DisableActionsOn`/`DisableQueryActions`/`DisableActions`, `IClientAccessor.DisableActionsForSession` (`IClientAccessor.cs:74`), `CustomQueryArgs.DisableActions`, `SparkQueryContext.DisableActions` (`Queries/SparkQueryContext.cs:58`). CodeCoverage `RepositoryActions` migrates. Batched form for multi-row submits. Documented rule: the hook depends only on entity, user and stored state. |
| D14 | **All 10 vote-fraud measures in v1** (§3.12). `moderation.json` is an `IConfiguration` source bound to `Spark:Moderation` — every threshold overridable via appsettings/env vars/user secrets; group-id and earnable validation runs **after** layering. |
| D15 | **Forwarded headers configured by Spark.** Default trust: loopback + private ranges (10/8, 172.16/12, 192.168/16, fc00::/7); override `Spark:ForwardedHeaders:KnownNetworks`/`KnownProxies`; `ForwardLimit` = `Spark:ForwardedHeaders:ProxyHops` (default 1, **never null**); `X-Forwarded-Proto` same trust; **`X-Forwarded-Host` not forwarded by default** — opt-in `Spark:ForwardedHeaders:ForwardHost=true`, which is refused at startup while `AllowedHosts` is `*` (every app's appsettings has `"*"` today, so checking against it would do nothing). Outside Development, a trust-everyone configuration is a startup error. Effective trust list logged at startup. Hand-written blocks in CodeCoverage (`Program.cs:35-70`, plus the comment at `:371-374`), DemoApp, HR and Fleet deleted (MintPlayer, a separate repo, removes its own). Deployment guide: entry proxy should *overwrite* XFF (nginx `$remote_addr`, Traefik `trustedIPs`, never `insecure`). **Pre-merge: verify what fronts coverage.mintplayer.com and MintPlayer** (a CDN needs its ranges added, or the whole site shares one rate-limit bucket). |
| D16 | **ng-spark-auth `withAccount()`**: confirm-email, change/set-password, profile, 2FA enrollment + recovery regeneration, connected logins, passkeys, personal data/delete — each also a standalone component. Profile accepts app-contributed fields (`SPARK_ACCOUNT_PROFILE_FIELDS`), validated server-side via `ISparkProfileContributor<TUser>`. Email change goes through confirmation. QR code server-rendered SVG. Test enumerates every `/manage/*` route for `SparkLocalCredentials` classification + antiforgery stamp. |

### Technical decisions (made by Claude, reasons recorded; owner may override)

| # | Decision | Why |
|---|---|---|
| T1 | History/SoftDelete endpoints follow the **literal route table, ids in the POST body**: `POST /spark/po/revisions`, `/spark/po/revision`, `/spark/po/revert`, `/spark/po/restore`, `/spark/po/purge`. | Raven ids contain slashes; the route table is fully literal by convention (`PersistentObjectRequest.cs:30-47`). The issue's `GET /spark/po/{type}/{id}/revisions` breaks it. |
| T2 | The "Deleted" filter is a **field on the query request** (`deleted: exclude|include|only`), not a header. | Breaking changes allowed; a header is invisible in the request contract. Honoured only for holders of `ViewDeleted/T`. |
| T3 | History, SoftDelete and Moderation UIs ship as **ng-spark secondary entry points** (`@mintplayer/ng-spark/history`, `/soft-delete`, `/moderation`), not new npm packages. | Entry points need no workflow/gate/tsconfig edits; a new npm package needs 5 hand-written lists. |
| T4 | Mail payloads carrying tokens are **encrypted with Data Protection** (now always available, D5) and **scrubbed on terminal status**. | Reset/confirm tokens otherwise sit in `PayloadJson` for the whole retention window, dead-letters included (conflicts with item 5). |
| T5 | **Custom actions return data**: `CustomActionArgs.SetResult<T>(value)` → envelope `Result`; ng-spark `executeCustomAction<T>(): Promise<T \| undefined>`; `SparkActionResult.Result`. Result bypasses redaction — the action author owns it (documented). | In the issue as nice-to-have; one PR. |
| T6 | Moderation data (Vote, ReputationEvent, Flag, ReviewCase, Lock, Suspension, AuditEntry) are **plain Raven documents behind the package's own `/spark/moderation/*` endpoints**, not persistent objects. | security.json, customActions.json and model JSON are app-only for add-on assemblies; POs would force every app to own and sync Moderation's model. |
| T7 | New .NET packages live at `libs/<group>/<PackageId>/<PackageId>.csproj` (exactly two levels — the version gate's `sed` requires it), registered in `MintPlayer.Spark.slnx`, versions inline in the csproj (template: `MintPlayer.Spark.Cron`). Unit tests join `tests/MintPlayer.Spark.Tests`. | Verified packaging conventions. |
| T8 | Messaging gains **`BroadcastOptions { DeduplicationKey (hashed, type-namespaced), Delay, MaxAttempts, ExpiresAtUtc, Queue, ScrubPayloadOnTerminal }`** and **`SparkMessagingOptions.Queues`** (`Spark:Messaging:Queues:{name}`). Handlers get the current message id (`IMessageContext`). | MailManager needs delay+dedup, per-message expiry (a throttled reset mail must not send after its token expired), lane override, and the id for VERP/Message-ID. |
| T9 | Item 9: **delete the `SparkAuthEnsureNpmPackage` target** and the `DependsOnTargets="SparkAuthEnsureNpmPackage"` edge on `SparkAuthGenerateSetupFile` (targets:114); keep `SparkAuthGenerateSetupFile`; warn when `@mintplayer/ng-spark-auth` is missing from `$(SpaRoot)package.json`. | The apps using Authorization (CodeCoverage, HR, Fleet) already declare the dependency; the target runs npm in the wrong directory and writes `^latest`. |
| T10 | Revisions config lives on the model: `EntityTypeDefinition.Revisions { Enabled, MinimumRevisionsToKeep, MinimumRevisionAgeToKeep, PurgeOnDelete }`; startup reads the database record and **merges** (`ConfigureRevisionsOperation` replaces the whole config). | Replaces MintPlayer's `RevisionsConfigurator`; model sync preserves hand-written blocks. |

---

## 3. Requirements per item

### 3.1 Core seam (item 1) — D1, D2

**Row policies** (registered scoped, `AddSparkRowPolicy<T>()`):
- `IRowPolicy { bool AppliesTo(Type) /* fixed per type, cached process-wide */; bool BypassInSystemContext => true; bool IsVisibilityDecision => false; }`
- `IRowFilterPolicy : IRowPolicy { ValueTask<LambdaExpression?> GetFilterAsync(RowPolicyContext) }` — keeps database paging.
- `IRowCheckPolicy : IRowPolicy { ValueTask<bool> IsAllowedAsync(RowPolicyContext, object entity) }` — declares it switches off database paging.
- Typed helpers `RowFilterPolicy<T>` / `RowCheckPolicy<T>` with `AppliesTo` = `IsAssignableFrom` (so `ISoftDeletable` covers every implementer).
- `RowPolicyContext`: entity type, action (incl. custom action names), request flags (e.g. `Deleted` mode), `IsSystemContext`.
- **Combination in `RowSecurity`:** effective filter = actions filter AND every applicable filter policy, joined by **parameter rebinding + `AndAlso`** (never `Expression.Invoke`), constant-folded (`false` short-circuits, `true` dropped), cached under the existing (type, action) key, applied **before** column filters and search. Policies return predicates only — never receive the `IQueryable` (the `SearchOptions` leak turns a security `Where` into an OR).
- Per-row rule = compiled filter AND actions `IsAllowedAsync` AND every check policy. Per-row refinement flag = "actions override OR any check policy".
- `HasRowRule` split: filter-only policies do not trigger the `SparkQueryPage<T>` refusal (`QueryExecutor.cs:333-340`); only `IsVisibilityDecision` policies count for `RowPolicyDeclarationValidator` (SoftDelete must not silently satisfy the anonymous-readable warning).
- Policy calls count toward the 30-request session budget and the N+1 warning.
- **#285 (D2):** rebind entity filter onto the projection type by member name; push down when every referenced member exists on the projection; otherwise fall back to the post-filter unchanged.

**Interceptors** (`IPersistentObjectInterceptor`, `AppliesTo`, multi-registered, run in `DatabaseAccess`):
- `OnBeforeSaveAsync(SaveContext)` / `OnAfterSaveAsync(SaveContext)`; `OnBeforeDeleteAsync(DeleteContext)` with `Replace()` / `WasReplaced`; `OnAfterDeleteAsync(DeleteContext)`; `OnAfterLoadAsync(LoadContext)`.
- Context: operation kind (`Save`/`New`/`Delete`/`Revert`/`Restore`/`Purge`/`Sync`), before/after entity, caller, elevation flags, `IsPurge` on delete.
- Order: before-hooks run after the actions class's `OnBefore*Async`, interceptors in registration order; after-hooks run the actions hook, then interceptors in reverse order.
- A replaced delete: `actions.OnDeleteAsync` is not called; the replication/`ISyncActionInterceptor` must not send a hard delete for a soft one.
- Refusals distinguishable: 404 (not visible), 400 (`SparkValidationException`, e.g. locked), 429 (throttled).

**Group membership (for D12):** `AddGroupMembershipProvider<T>()` composes; providers may return ids; per-request cache inside `SecurityFileAccessControl` (~45 call sites + `[SparkAuthorize]` handler) with identical answers to today.

**Current user:** `ISparkCurrentUser` abstraction in core (id, is-authenticated) — needed for stamping, Moderation, audit.

### 3.2 SoftDelete (item 2) — `libs/soft_delete/MintPlayer.Spark.SoftDelete` (+ `.Abstractions`)

- `ISoftDeletable { bool IsDeleted; DateTimeOffset? DeletedAt; string? DeletedBy; string? DeleteReason }` in a small abstractions assembly (Domain projects reference it).
- `SoftDeleteRowPolicy` (filter policy): `x => x.IsDeleted != true` — **never `!x.IsDeleted` / `== false`** (absent field ≠ false; `HasValue` on a static index is a 500). Holders of `ViewDeleted/T` with query `deleted=include|only` see deleted rows. For `Restore`/`Purge` the row must be deleted. Applies everywhere `IRowSecurity` does: list, detail, custom queries, sub-queries, distincts, stream, breadcrumbs, edit/delete gates, custom-action selections. Option: applies in system context (default: yes — background jobs should not see deleted rows unless they opt out).
- `SoftDeleteInterceptor`: replaces delete with setting the fields; skipped when `IsPurge`.
- Service API `ISparkSoftDelete { RestoreAsync, PurgeAsync }` + endpoints (T1). Purge also runs `DeleteRevisionsOperation` (GDPR). Events Deleted/Restored/Purged for Moderation.
- Rights: `Restore/T`, `Purge/T`, `ViewDeleted/T` (granted by name — no wildcards, D3).
- Startup/analyzer warning: a soft-deletable type queried through an index whose Map doesn't emit `IsDeleted`; analyzer for `!x.IsDeleted` on `ISoftDeletable`.
- Startup warning (in the SoftDelete package — core cannot reference `ISoftDeletable`): a type that is `ISoftDeletable` **and** overrides `OnDeleteAsync`.
- **References and lookups:** reference-picker queries go through `IRowSecurity`, so deleted rows are hidden there by the policy; displaying an existing reference to a now-deleted row goes through the breadcrumb row check (redaction placeholder). Because core does not row-check references on save (D1 gap), the SoftDelete interceptor itself refuses a save that points a reference at a soft-deleted `ISoftDeletable` target (400), unless the caller holds `ViewDeleted` on that target type.
- Natural-id trap: creating a document whose id is held by a soft-deleted row → documented (restore instead) with a clear error.
- ng-spark `/soft-delete`: Deleted toggle on query lists (for `ViewDeleted` holders), Restore/Purge actions.

### 3.3 History (item 3) — `libs/history/MintPlayer.Spark.History`

- Revisions per entity from the model (T10), merged into the database record idempotently.
- `IAuditable { CreatedBy, CreatedAt, ModifiedBy, ModifiedAt }` stamped by an interceptor from `ISparkCurrentUser` — **ids only** (D8). `CreatedBy` immutable after create (reputation theft otherwise).
- Service `ISparkHistory { ListAsync, GetAsync(cv), RevertAsync(cv) }` + endpoints (T1). Reads gated on the **current** document's row check + `History/T`, and run through `RedactAsync` (field-level redaction must hold on old revisions). Revert: load revision, verify id + collection, `IEntityMapper.ToPersistentObject`, save through `IDatabaseAccess.SavePersistentObjectAsync` with the current etag (Edit right, row gate, WITH CHECK, protected attributes, stamping, lock interceptor with operation = `Revert`); requires `Revert/T`. Only model attributes revert; soft-delete and audit fields excluded; AsDetail row-identity caveat documented.
- `ISparkRevisionObserver.OnRevisionCreatedAsync({EntityType, Id, ChangeVector, PreviousChangeVector, UserId, Kind, ChangedAttributes})` — in-process, multi-registered; fires only for writes through the Spark pipeline. History does not depend on Messaging.
- ng-spark `/history`: `SPARK_DETAIL_PANELS` multi-provider token (new in ng-spark core, since `sparkRoutes()` passes no templates) + `<spark-po-history>`: revision list, read-only view, field diff vs current, Revert.

### 3.4 Sign in by email (item 4) — D4
### 3.5 Secrets at rest (item 5) — D5
### 3.6 Account flows (item 6) — D6, D7, D8, D16

Server endpoints under `/spark/auth/manage/`: `password` (set, for social-only accounts), `profile` (GET/POST, via `ISparkProfileContributor<TUser>`), `2fa/authenticator-uri` (+ SVG QR), `personal-data` (GET), `account` (DELETE, re-auth), confirm-new-email. `ISparkAuthLinkBuilder` (SPA base URL + route paths) so confirmation/reset mails link to SPA pages, not the server's plain-text `confirmEmail`. `SparkUser` gains `CreatedAtUtc` + `RegistrationMethod` (stamped in `UserStore.CreateAsync`; backfill from Raven `@created`).

### 3.7 Timezone for SSR (item 7)

- `Spark:TimeZone:CookieName` (default `spark-timezone`; null disables). Precedence: valid header → valid cookie → UTC; an invalid header falls through to the cookie. Both validated before `FindSystemTimeZoneById`: ≤64 chars, `^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$` (`[GeneratedRegex]`). Never echoed, never written by the server. Log names the source.
- ng-spark `withSparkTimezone({ cookieName?: string | false })`: sets the cookie in the browser only, only when changed (`Path=/; Max-Age=31536000; SameSite=Lax; Secure` on https); **the interceptor must not send the header on the server platform** (it would send Node's UTC and override the cookie). First visit renders in UTC (documented). Update `docs/guide-dates-and-sorting.md`.

### 3.8 DisableActions enforced (item 8) — D13

Implemented in M3 as specified in D13; the author-facing contract is in `guide-custom-actions.md`
("Disabling actions"), the measurements in §4.1 (S6, S-MOD-F) and the deviations there.

### 3.9 Pinned npm (item 9) — T9

### 3.10 MailManager (item 10) — `libs/mail/MintPlayer.Spark.MailManager` — D9, D10, T4, T8

- Implements `IEmailSender<TUser>` (**`Replace`**, since `AddIdentityApiEndpoints` TryAdds the no-op) and `ISparkLinkConfirmationSender<TUser>`; they only **publish** `SparkMail { Template, Language, To, Data, DeliveryId }` — never send inline.
- Lanes: `SparkMail` → `mail-transactional`, `SparkBulkMail` → `mail-bulk` (or `BroadcastOptions.Queue`). Mail lanes get a longer backoff (a relay outage > 13 min must not dead-letter all mail) and per-publish `ExpiresAtUtc` (a reset mail dead-letters as Expired before its token expires).
- Templates: embedded resources of the consuming app, **`{lang}/{name}.mjml` layout** (avoids MSBuild `AssignCulture` moving `name.en.mjml` into satellite assemblies — verify in S-M1). Scriban strict variables; **HTML-escape string values** before MJML; convert `JObject` data to `ScriptObject`. Mjml.Net renders. All templates parsed at startup; a test helper (in the MailManager package, not Spark.Testing) renders every template × language with sample data.
- Failure classification: synchronous SMTP 5xx / invalid address → `NonRetryableException` **logged loudly** (a 553 from `ALLOWED_SENDER_DOMAINS` would otherwise dead-letter everything silently); 4xx / connection errors → retry.
- VERP `bounces+{deliveryId}@{VerpDomain}` with compact `DeliveryId` (UUIDv5 of campaign + email for fan-out); `SparkMailDeliveries/{id}` record, 30-day retention; `Message-ID: <{deliveryId}@domain>` (at-least-once duplicates identifiable — documented, not "fixed").
- Bounce endpoint: raw DSN body, shared bearer secret compared in constant time, **stated** `DisableAntiforgery()` exemption, rate limited; MimeKit DSN parse; failed + 5.x.x → suppress.
- Suppression: `SparkMailSuppressions/{sha256(normalizedEmail)}` {Reason, Scope}; suppressed → delivery record "Suppressed", message Completed (no new `EMessageStatus`).
- Bulk: one campaign message fans out via `BroadcastOnceAsync(…, "{campaignId}:{recipient}")` (with the fixed, hashed dedup key); never one mail with many BCCs.
- Dev mode per D9. Rewrite `guide-outgoing-mail.md` §8 (bulk no longer out of scope) instead of contradicting it; add Postfix per-domain pacing notes (`smtp_destination_rate_delay`, `smtp_destination_concurrency_limit`).
- CodeCoverage migration (D10): its link-confirmation template becomes MJML (default wording from `SparkLinkConfirmationMessage`).
- **Owner decision (2026-09-28, during M8): multi-language, file-based templates.** Templates are files
  in the app's repository, managed by developers. A pluggable resolver (`ISparkMailTemplateResolver`,
  file-system default) picks the file per recipient culture with a fallback chain —
  `PasswordReset.nl-BE.mjml` → `PasswordReset.nl.mjml` → `PasswordReset.mjml` — applied to the subject
  (`<mj-title>`), the optional `.txt` part and every `mj-include` alike. The culture is chosen **per
  send, never from the ambient request culture**: explicit culture on the send → the recipient's
  stored preference (`ISparkMailRecipientCulture`; Authorization adds `SparkUser.PreferredCulture`) →
  `Spark:Mail:DefaultCulture`. The resolved culture is **stored on the queued message**, so a retry
  renders the same language. Embedded defaults (the account mails, shipped by Authorization) are
  overridden by an app file with the same name. Dates and numbers format in the resolved culture
  (Scriban culture pushed). Startup: a template name without a neutral file fails (warns in
  Development); a missing localized variant falls back silently (Debug log). Tested: fallback order,
  explicit culture over preference, culture surviving a retry, app override over embedded default.
  This supersedes the `{lang}/{name}.mjml` layout above (S-M1 showed `WithCulture="false"` solves the
  satellite problem for `{name}.{culture}.mjml`).
- **Owner decision (2026-09-28, during M10): explicit, pluggable transports; development mail fails
  closed.** MailManager ships `UseSmtpTransport(...)` (MailKit, `Spark:Mail:Smtp`),
  `UseMailpitTransport(...)` (SMTP into Mailpit, default `localhost:1025`, no TLS/auth,
  `Spark:Mail:Mailpit:{Host,Port,Tags}`, an `X-Tags` header with template name and stream; SMTP, not
  Mailpit's JSON API, so the exact MIME is kept), `UsePickupFolderTransport(...)` and
  `AddMailTransport<T>()`. Explicit registration wins; with none, the config fallback
  (`Smtp:Host` / `PickupFolder`) keeps existing configs (CodeCoverage) unchanged; two transports
  (registered or configured) are a startup error. `ISparkMailTransport.DeliversToRealRecipients`
  (default `true`): Mailpit and pickup `false`, SMTP `false` only for a loopback host. In Development
  a transport that delivers to real recipients without `Spark:Mail:Development:RedirectTo` refuses
  startup (set RedirectTo in user secrets, or use Mailpit). In Production `RedirectTo` itself refuses
  startup; other environments (staging, E2E) keep redirecting with a warning.

### 3.11 Queue throttling (item 11) — T8

- `SparkQueueOptions { MaxPerInterval + Interval, BatchSize + MinDelayBetweenBatches, MaxConcurrency, MaxAttempts, Backoff }` per queue name, in code or `Spark:Messaging:Queues:{name}`.
- Admission at the top of `MessageProcessor.RunHandlersAsync` (shared by both modes). **Never wait inside a lane** (bounded channel → a sleeping lane blocks the one shared feeder and stalls every queue; queued claims aren't renewed). Over budget → one optimistic write: Pending, owner/claim null, `WakeUp=false`, `NextAttemptAtUtc = reserved slot`, `AttemptCount--` (no retries burned). `MessageClaims.ReleaseUnstartedAsync` (`:124`) is **not reusable as-is** (it sets `WakeUp=true`, takes no `NextAttemptAtUtc`, opens its own session without optimistic concurrency) → add a variant that runs in the caller's session with a target time and `WakeUp=false`.
- **GCRA-style reserved slots** per queue (in-memory `ConcurrentDictionary`; on restart a message is deferred once more) — each throttled message deferred once, not O(n²). Honour the later of backoff and slot; slot after `ExpiresAtUtc` → dead-letter as Expired. **Persistence:** `SparkMessage` gains `ExpiresAtUtc` and `DeadLetterReason` (`MaxAttempts` / `NonRetryable` / `Expired`); status stays `DeadLettered` (`EMessageStatus` is Pending/Processing/Completed/Failed/DeadLettered — no new value).
- A batch is a pacing window only; each message keeps its own status and retry.
- `MaxConcurrency > 1` only in `SingleSubscription` mode (N pumps per lane); warning in per-queue mode. In-process bucket suffices — no compare-exchange bucket — because the leader lease gates the feeder **and** the per-queue workers in both modes (`MessageSubscriptionManager.cs:60-112`) (one lease owner owns all queues).
- Accuracy documented: long-run rate exact, bursts quantised by `FallbackPollInterval` (30 s).
- Optional `IMessageProgress`: sidecar `SparkMessages/{id}/progress/{handlerIndex}` in its own collection, appended by patch, **same `@expires` as its message**; `IsDoneAsync`/`MarkDoneAsync`. Not an array on `SparkMessage`.
- Pattern documented: `mail-transactional` generous, `mail-bulk` strict.

### 3.12 Moderation (item 12) — `libs/moderation/MintPlayer.Spark.Moderation` (+ `.Abstractions`) — D11, D12, D14, T6

- **Configurable reputation events** in `moderation.json` (`Spark:Moderation:Reputation`): points per event type — upvote received (default +10), downvote received (−2), downvote cast on an answer (−1), flag upheld (+2), flag declined (0), content deleted by a moderator (reverses its votes). Unknown event names are a startup error.
- **Votes & reputation:** up/down on opt-in entities (`IModeratable`); deterministic vote ids + optimistic concurrency (no double votes); ledger = one `ReputationEvent` per event, summed by a hand-written map-reduce index (`AbstractIndexCreationTask<T,R>` — `[GenerateIndex]` can't reduce); a test asserts the indexes deployed (deploy failures only log to console).
- **Privileges** (D12): `moderation.json` as `IConfiguration` source (D14); thresholds per privilege: `rep`, `minAccountAgeDays`, `minActiveDays`; applied as groups via a composed `IGroupMembershipProvider`, request-cached; **suspension read from a document** (instantly consistent), never an index or TTL cache.
- **Vote-fraud defence (D14, all 10):**
  1. Age + activity gates on every privilege.
  2. Diversity rule: rep counts toward a privilege only from ≥ `max(3, votes/5)` distinct voters over ≥ `votes/4` distinct days.
  3. Voter eligibility: voter < 7 days old or < 50 rep → changes score, adds 0 rep.
  4. Caps: 200 rep/target/day; 30 votes cast/voter/day; voter→target pair 3 credited/day, 10/30 days.
  5. Delayed crediting: `CreditableAfterUtc = CastAt + 48h`; privilege sum counts only effective entries; credited by a Cron job (atomic with the vote save — not delayed messages).
  6. Nightly detector (Cron, last 30 days): serial (≥5 A→B votes in 24h) and concentration (≥10 votes, ≥50% of A's) **auto-reverse**; reciprocal, fast-voting (<60 s after post), registration cluster (≥2 accounts within 1h sharing IP-/24 hash, or same non-webmail domain, voting for each other) **open a review case only** (NAT false positives).
  7. Reversal = compensating `Reversal` ledger entry {RuleId, CaseId}, idempotent per original vote, never a deletion; target sees "Voting corrected (−N)" without the voter; merging/deleting an account reverses all its votes.
  8. Review surface: voter→target matrix, timeline vs post creation, account ages side by side, "shares IP-/24 hash with N accounts" (never the address), rep before/after, rule fired; actions dismiss / reverse / merge / escalate-to-suspend; every decision audited.
  9. GDPR: store only HMAC(truncated IP: /24 v4, /48 v6) with a server-side rotated key, `@expires` 90 days; legitimate-interest assessment + privacy-notice text documented.
  10. `SparkUser.CreatedAtUtc` + registration method (§3.6), backfilled.
  Out of v1: device fingerprinting, ML/graph Sybil detection, vote fuzzing, shadow bans, automatic suspension.
- **Flags & review queue:** flag any opt-in entity with a reason; one review-queue page; outcomes feed reputation. Vote/flag endpoints must not reveal that a hidden post exists (missing ≡ no-access, #453).
- **Moderator tools:** lock/unlock (interceptor refusing save/delete with 400 "locked", moderators exempt; `locks/{targetId}` doc; revert passes with operation = Revert), revert (History), restore/purge (SoftDelete), suspend (Identity lockout + security-stamp refresh + immediate server-side write block + no reputation groups). Every action written to an audit log.
- **New-account throttling** as a row check on `New` (429, not 404).
- **Deletion handler** (D8): removes votes/flags, anonymises the ledger.
- **ng-spark `/moderation`:** vote widget (attribute renderer via `SPARK_ATTRIBUTE_RENDERERS`), flag button, review-queue page, reputation badge, moderator panel (via `SPARK_DETAIL_PANELS`).
- `--spark-init-moderation` prints the security.json grants the app must add; startup check that every privilege has at least one grant.
- **Proof:** `apps/QnA` (D11) with E2E tests as the spec. Out of v1: suggested edits, accepted answers/bounties, close votes, badges, notifications.

### 3.13 Forwarded headers — D15 (new, found during investigation)

### 3.14 Housekeeping

- Close #283 (regression test with a policy), #285, #299, #432.
- Reply on #460: SocketExtensions 11.x is already `11.0.1-preview.86`.
- Fix the stale line in `docs/decisions_messaging_and_project_automation.md`.
- Bump every touched lib's csproj version and both npm packages (the version gate requires a `+<Version>` line per changed project).
- Update `tools/verify-coverage-paths.mjs` only if a test project is added (none planned).

---

## 4. Spikes

Each spike states what it proves. Run them in the milestone that needs them, before building on the answer. "Real" = a database with an index, documents and a query (an empty-DB spike proves nothing).

| ID | Proves | Blocks |
|---|---|---|
| S1 | Two rebound lambdas joined with `AndAlso` + a Search group + column filters translate on RavenDB 7.2 (Corax **and** Lucene) as `(A and B) and (search…)` — no `Invoke`, no `SearchOptions` leak. | Seam |
| S2 | Rebinding an entity filter onto an index projection by member name pushes down; paging + totals correct with 30% deleted rows; fallback when the member is missing. | #285 |
| S3 | `IsDeleted != true` vs `!x.IsDeleted` on documents without the field, on the collection and on a static index (list, detail, distincts, sub-query). Which expression shape (interface cast vs `Expression.Property`) the provider translates. | SoftDelete |
| S4 | Delete replacement in `DatabaseAccess` with an app overriding `OnDeleteAsync`; replication doesn't send a hard delete. | Seam |
| S5 | `HasRowRule` split: validator, `SparkQueryPage` refusal and per-row `Can` flags behave under a non-visibility filter policy. | Seam |
| S6 | `OnDisableActionsAsync` evaluated at load and submit (parent, query, rows) — no operation leakage into the execute response; 403 only after the row gate. | D13 |
| S7 | Custom-action result through the envelope and a 449 retry, in ng-spark and `SparkClient`. | T5 |
| S8 | Breadcrumbs + 3 policies stay under 30 session requests, ≤1 policy call per (type, action). | Seam |
| H1 | Revisions config, reads and `DeleteRevisionsOperation` on the **Community** licence (`raven-community-license.log`). | History |
| H2 | After a save, `po.Etag` equals the newest revision's change vector. | History |
| H3 | Revert fidelity through the save pipeline for TranslatedString, DateTimeOffset, AsDetail rows, undeclared fields (JSON diff). | History |
| H4 | Reading + merging the revisions config keeps existing settings and is idempotent. | History |
| SP-A | 2FA and recovery-code steps work under the `SparkSignInManager` override, cookie and bearer modes. | D4 |
| SP-B | Data Protection round-trip; restart with persisted Raven key ring; legacy plaintext read; backfill idempotence; **CodeCoverage reads its existing key documents** and its GitHub token refresh still works. | D5 |
| SP-C | Each provider (Google, Microsoft, Facebook, X, LinkedIn): verified-email signal, `ClaimTypes.Name` shape, Microsoft consumers-tenant detection. | D7 |
| SP-D | Server-side SVG QR (library vs in-house encoder) scans in common authenticator apps. | D16 |
| SP-E | Run #439 SP2 (passkey clone detection), never run. | item 6 |
| S-M1 | Where embedded `.mjml` resources land (main vs satellite assembly) for `{lang}/{name}.mjml` and `{name}.{lang}.mjml`. | MailManager |
| S-M2 | Mjml.Net output vs npm mjml on 3 real templates (incl. `mj-include`, Outlook). | MailManager |
| S-M3 | Throttle accuracy: 1000 messages at 20/min alongside transactional traffic — rate, burst, transactional p95, writes per message. | Throttling |
| S-M4 | Scriban strict mode, `JToken` conversion, escaping. | MailManager |
| S-M5 | boky/postfix: VERP → pipe → HTTP; `master.cf` customisation, `curl` presence, exit 75 on HTTP failure. | D9 docs |
| S-M6 | MailKit `SecureSocketOptions.None` against a relay advertising STARTTLS; VERP envelope; no `Sender:` header. | MailManager |
| S-TZ1 | `FindSystemTimeZoneById` on path-shaped ids under .NET 11 (Linux + Windows) — defence-in-depth evidence. | item 7 |
| S-TZ2 | How MintPlayer's prerender fetches data (in-process vs Node over HTTP) — whether ng-spark needs an SSR cookie-forwarding helper. | item 7 |
| S-TZ3 | Vitest proves the interceptor would send Node's zone during SSR (and the guard stops it). | item 7 |
| S-TZ4 | A strict id regex doesn't break `SparkClient.TimeZoneId` callers passing Windows ids. | item 7 |
| S-MOD-A | Map-reduce index (voter→target counts, reciprocal pairs, rep sums) from an add-on assembly under SPARK018 tooling; catch-up time. | Moderation |
| S-MOD-B | Composed group providers + per-request cache — backward compatible across the existing auth tests. | D12 |
| S-MOD-C | How long a locked-out user's cookie/bearer keeps working after security-stamp refresh. | suspend |
| S-MOD-D | One falsifiable test per write path that the lock interceptor fires (PO save, AsDetail parent, DeleteRow, custom actions, revert, restore, purge, sync). | lock |
| S-MOD-E | Vote + ReputationEvent commit atomically on the shared actions session. | Moderation |
| S-MOD-F | 404/400/429/403 through the response envelope, distinguishable by the client. | Seam, D13 |
| S-FH1 | `ForwardedHeadersMiddleware` with Spark defaults: spoofed leftmost XFF ignored, untrusted source keeps socket IP, `ProxyHops=2`, startup refusal. | D15 |
| S-PKG1 | nuget.org `MintPlayer.*` prefix reservation + CI API key scope cover the 6 new package ids (**owner checks in the nuget.org account**). | publish |
| S-PKG2 | `dotnet pack` on the branch includes the new libs without `IsPackable` tweaks. | publish |

### 4.1 Spike results (measured)

Measured facts only. Each spike is kept as a test so the answer stays pinned.

**S1 — composed filter + column filter + search (M2, 2026-09-28).**
*Question:* do two rebound lambdas joined with `AndAlso`, then a column filter, then a two-field
search, translate on RavenDB 7.2 as one AND group ahead of the search group, on Corax and Lucene?
*Method:* `RowPolicyCompositionSpikeTests_{Corax,Lucene}.S1_…`: 25 documents (16 alice incl. 6
deleted, 5 bob, 4 legacy documents **without** the `IsDeleted` field), an actions-style filter
`d => d.Owner == "alice"` and a policy `(ISpikeSoftDeletable x) => x.IsDeleted != true` combined by
`RowFilterExpressions.Combine`, then `.Where(Category == "A").Search(Title).Search(Body)`, against the
collection (auto index) and a static index, with the database's auto and static engines set per
fixture and verified through `IndexStats.SearchEngineType`.
*Answer:* identical RQL on all four combinations —
`where ((Owner = $p0 and IsDeleted != $p1)) and (Category = $p2) and (search(Title, $p3) or search(Body, $p4))`.
No `Invoke`, no search-options leak; the result set was exactly the 4 expected rows on every
combination.

**S3 — absent field and expression shape (M2, 2026-09-28).**
*Question:* `IsDeleted != true` vs `!x.IsDeleted` on documents without the field, and which shape the
provider translates: the interface cast `((ISoftDeletable)row).IsDeleted` or the property
`row.IsDeleted`.
*Method:* same fixture, same four engine/source combinations; plus the compiled predicate over a loaded
legacy document (the detail and distinct-value paths evaluate in memory).
*Answer:* `!= true` returns 14 rows (10 explicit `false` + all 4 without the field); `!x.IsDeleted`
returns 10 — it drops **every** document lacking the field, on both engines, collection and static
index. In memory both shapes return `true` for the legacy document (CLR default `false`), so the
divergence exists only in the database. Both expression shapes translate: the cast shape produced RQL
byte-identical to the property shape and the same 14 rows. **Decision:** rebinding uses the property
shape (member re-resolved by name on the target type), because it is the only shape that also works on
an index projection (S2), which implements no interface; the cast is used only when a predicate uses
the parameter as a whole value.

**S2 — projection rebinding (#285, M2, 2026-09-28).**
*Question:* does an entity filter rebound onto an index projection by member name push down, with
correct paging and totals under 30% deleted rows, and is a missing member detected?
*Method:* `S2_…`: the composed filter rebound onto `SpikeRow` (stored index fields `Owner`, `Title`,
`IsDeleted`), applied after `ProjectInto` (the order the query executor uses), `Skip(10).Take(5)` with
statistics; and a projection without `Owner`.
*Answer:* RQL `from index 'SpikeDocs/Overview' where (Owner = $p0 and IsDeleted != $p1) order by Title, id() select id() as Id, Owner, Title, IsDeleted`;
`TotalResults` = 14 (25 rows − 5 bob − 6 deleted), the page held rows 11–14 (4 rows); every row
`Owner == alice`, not deleted. The projection without `Owner` makes `Rebind` return null → fallback.
Also through `RowSecurity.ComposeRowFilterAsync` (`Issue285_…`): mode `PushedDownOntoProjection`, the
right rows; a projection lacking `LicensePlate` → `ProjectionFallback`. Note: the query executor still
pages a static-index query in memory (fan-out rule, #431 M14); the pushdown narrows what is read.

**S4 — delete replacement vs an `OnDeleteAsync` override (M2, 2026-09-28).**
*Question:* with an Actions class whose `OnDeleteAsync` hard-deletes without calling the base, does a
replacement decided in `DatabaseAccess` hold, and does replication avoid a hard delete?
*Method:* `PersistentObjectInterceptorTests.S4_…` through `IDatabaseAccess` with two interceptors and a
recording `ISyncActionInterceptor`.
*Answer:* the document survives with `IsDeleted = true`; the override's `OnDeleteAsync` is never
called; call order `actions.OnBeforeDeleteAsync → replace.before → stamp.before → stamp.after →
replace.after`; replication received one `HandleSaveAsync(entity, id)` and no `HandleDeleteAsync`. A
`Purge` is not replaced: the override deletes, replication receives the delete.

**S5 — `HasRowRule` split (M2, 2026-09-28).**
*Question:* under a non-visibility filter policy, do the anonymous-readable validator, the
`SparkQueryPage<T>` refusal and the per-row `Can` flags behave?
*Method:* `RowPolicyCompositionTests.S5_…` (kinds + `RowPolicyDeclarationValidator.Validate` with an
anonymous `Query/RpTag` grant) and `RowPolicyPipelineTests.S5_…` (detail load through `IDatabaseAccess`
with a soft-delete filter policy and a lock check policy, no Actions override).
*Answer:* a type governed only by a soft-delete filter reports `RowRuleKinds.FilterPolicy`; the
validator **still reports** the anonymous type (soft deletion does not satisfy it); the
`SparkQueryPage` refusal does not fire for it. A visibility policy or check policy does trigger both.
`Can` is computed: live row `Edit/Delete = true/true`, locked row `false/true`; a soft-deleted row is
404 by id; a create that would produce a deleted row is refused by the rerouted WITH CHECK.

**S8 — request budget with breadcrumbs and three policies (M2, 2026-09-28).**
*Question:* breadcrumbs + 3 policies stay under 30 session requests, ≤ 1 policy call per (type,
action)?
*Method:* `RowPolicyBreadcrumbTests.S8_…`: 50 spots → cars → people, a soft-delete filter policy, an
every-type filter policy and a check policy, real `RowSecurity` + `BreadcrumbResolver`.
*Answer:* the resolution added **2** session requests (one batched load per level), independent of
row count; every filter policy was called exactly **once** per (type, `Read`). Check policies run per
row by design (in memory, no request).

**Deviation (S5):** §3.1 said filter-only policies do not trigger the `SparkQueryPage<T>` refusal. A
*visibility* filter policy (tenancy) now does: it removes rows the caller may not see from the
author's page after the author counted them, which is exactly the total-count oracle the refusal
exists to stop. Only non-visibility filter policies (soft deletion) are exempt, as intended.

**S6 — `OnDisableActionsAsync` at load and submit (M3, 2026-09-28).**
*Question:* is the hook evaluated at load (detail, query execute) and at submit (update, delete,
create, custom action over parent + query + rows, union), with no operation leakage into the execute
response and 403 only after the row gate?
*Method:* `DisableActionsTests` against the real route table (`SparkEndpointFactory`): a `DisProbe`
type whose hook withholds `Edit`, `Delete` and `DisProbeRun` on a stored row with `State = Locked`,
withholds `DisProbeRun` on the query `DisProbesFrozen`, and optionally `New` everywhere; a row rule
hiding rows referenced `hidden`; a recorder capturing every context and every batched call.
*Answer:* load — `/po/load` of a locked row returns `disabledActions = [Edit, Delete, DisProbeRun]`
(context: Load, PersistentObject, `Entity` the stored `DisProbe`); an open row omits the field;
`/queries/execute` of the frozen query returns `disabledActions = [DisProbeRun]` in a bare
`QueryResult` with no `operations` member (context: Load, Query, `Entity` null). Submit — update of a
locked row: `403 { action: "Edit" }`, document unchanged, and the context's entity carried the
**stored** `Reference`, not the posted one; delete: `403 Delete`, document kept; create with `New`
withheld: `403 New`, 0 documents, entity null; a custom action whose parent, query, or one of two
selected rows withholds it: `403`, the action never ran. A hidden row that would also withhold: update,
delete and custom action all `404`, and the hook was **never asked** about it at submit. An enabled
custom action with parent + query + 2 rows: one Load batch (the re-executed query, 1 item) then
exactly **one** Submit batch of **4** items (1 query, 3 objects each carrying its entity); the success
envelope had `operations: []` and only the action's own result. A save made by an action through
`IDatabaseAccess` on a locked row: `403 Edit`, not the catch-all 500.

**S-MOD-F — refusals distinguishable by the client (M3, 2026-09-28).**
*Question:* do 404, 400, 429 and 403 arrive through the response envelope distinguishably?
*Method:* same fixture, raw HTTP and `SparkClient`; a second host with `AddRateLimiter(PermitLimit = 3)`.
*Answer:* unknown action → `404 { result: { error: "Custom action '…' not found" }, operations: [] }`;
a hidden selected row → `404 { result: { error: "Not found" } }`; 201 ids → `400 { result: { error:
"At most 200 items…" } }`; disabled → `403 { result: { error, action } }`. All four are envelopes;
`SparkClient` throws `SparkClientException` with `StatusCode` 404 / 404 / 400 / 403 and the 403
`ResponseBody` names the action. **429 is not an envelope:** the rate limiter answers before any
endpoint runs, with an **empty** body; `SparkClient` surfaces `StatusCode = 429`. So the four are
told apart by status code; only 403 carries a machine-readable action name.

**S7 — custom-action result through the envelope and a 449 retry (M3, 2026-09-28).**
*Question:* does `CustomActionArgs.SetResult` reach ng-spark and `SparkClient`, including across a
449 conversation?
*Method:* `DisableActionsTests.S7_…` with an action that sets a result, then prompts, then sets
another from the answer; ng-spark `spark.service.spec.ts` (HttpTestingController).
*Answer:* the 449 envelope's `result` is `null` even though the action had called `SetResult` before
raising the prompt; the answered attempt returns `200 { result: { jobId: "job-1", answer: "Yes" } }`.
`SparkClient`: with `onRetry` the finished call's `GetResult<T>()` is the value; without a handler
`IsRetry` is true and `Result` null, and `ContinueAsync(…, "No")` returns `{ job-1, No }`. ng-spark:
`executeCustomAction<T>()` resolves to the result, to `undefined` for `result: null`, and to the second
attempt's result after a 449. Found on the way: `SparkClient`'s handler-driven path returned a bare
status code and dropped the success envelope's operations; it now surfaces both.

**Deviations (M3).**
- *Where `IDisablable` lives.* D13 names `PersistentObject` and "the query result". `PersistentObject`
  implements it (explicitly, so `DisableActions` is no longer a public member). The query side does
  **not** implement it on `SparkQuery` — `ModelLoader` hands out the shared singleton, so withholding
  on it would leak across requests (the #310 lesson) — nor on `QueryResult`, which is built after the
  hook runs and whose rows must stay out of the hook's reach (they cannot be re-derived at submit). The
  query target is a per-request framework collector; at submit every target is. Authors must not
  downcast the target; the context's `TargetKind` says what it is.
- *`Save` alias.* Update is refused when `Edit` **or** `Save` is disabled, create when `New` or
  `Save` is. Revert / Restore / Purge (later milestones) check their own operation name; a module sync
  and the system context are never gated.
- *`disableAction` client operation deleted* (server type, `SparkDisableActionOperation`, the ng-spark
  type and its warn-only handler), since every entry point that emitted it is gone. An older server's
  operation still parses in `SparkClient` as `SparkUnknownOperation`.
- *ng-spark UI* consumes the unchanged `disabledActions` for built-ins too: detail hides Edit when
  `Edit`/`Save` is withheld and Delete when `Delete` is; the grid gained `offersCreate` (New/Save),
  used by the query list; `canCreate` stays the bare right.
- *CodeCoverage.* `RepositoryActions.OnLoadAsync` became `OnDisableActionsAsync` with the same rule
  (withhold `DeleteData` unless the stored repository is `Disconnected`), so the page is identical.
  At submit a connected repository's `DeleteData` is now refused by the framework with 403 before
  `DeleteDataAction` runs (it used to answer 200 with a warning toast); the UI never offers that
  button, so only a hand-made request or a reconnect between load and click sees the difference.
  `DeleteDataAction` keeps its own checks.

**S-M3 — throttle accuracy (M4, 2026-09-28).**
*Question:* 1000 messages at 20/min alongside transactional traffic — rate, burst, transactional p95,
writes per message.
*Method:* `ThrottleAccuracySpikeTests.S_M3_…` (run with `SPARK_SPIKE_SM3_BULK=1000`; the kept test
defaults to 100): the real single-subscription pipeline (manager, lease, feeder, lanes, admission,
sweeper) on the embedded RavenDB 7.2 server. Time compressed ×20: `MaxPerInterval = 20` per
`Interval = 3 s`, `FallbackPollInterval = 1.5 s` (every ratio preserved). 1000 `spike-bulk` messages
published at once; a `spike-transactional` message (unthrottled queue) every 250 ms while the backlog
drains; revisions enabled on `SparkMessages`, so writes per message = revisions per document. A
no-backlog baseline of 20 transactional messages first.
*Answer:* publishing 1000 took 3.3 s; the backlog drained in 148.0 s (expected (1000−20)×0.15 s =
147 s). **Steady rate 19.90 per interval** (target 20). **Burst: up to 61 starts in one sliding 3 s
window** — the initial burst of 20 followed by the steady rate, clumped by the 1.5 s sweeper tick
(`MaxPerInterval` is a rate with a burst allowance, not a hard window cap). **Transactional latency:**
baseline p50 16 ms / p95 29 ms / max 43 ms; during the backlog (n = 556) p50 9 ms / **p95 153 ms** /
max 5788 ms. **Writes:** every unthrottled message 5 writes (577 transactional + 21 bulk admitted at
once); every deferred bulk message **8** (979 of them) — exactly 3 more: the deferral, the sweeper's
wake-up patch, the re-claim. No message was deferred twice.

**Deviations (M4).**
- *Burst semantics.* §3.11 says "MaxPerInterval + Interval"; measured, GCRA with burst tolerance
  `Interval − T` lets a sliding window hold ~3× the figure right after an idle period (61 vs 20 above).
  Kept as GCRA (the PRD's own choice) and documented on `SparkQueueOptions.MaxPerInterval` and in the
  README: for no burst, state the rate alone (1 per 3 s).
- *Transactional max 5.8 s during the bulk publish.* The p95 (153 ms) is fine, but the single feeder
  claims in etag order, so transactional messages written while 1000 bulk documents are in front of
  them wait for those claims. A lane cannot help here; it is the one-subscription trade-off (A6), not
  the throttle. Recorded, not fixed.
- *Configuration beats code for `Queues` only.* D14 asks for operator-overridable thresholds; code
  winning (the rest of `Spark:Messaging`) would make a library-declared queue default unoverridable.
  `Spark:Messaging:Queues` is re-applied in a `PostConfigure`, property by property; a configured
  `Backoff` replaces the code schedule.
- *`BroadcastOptions.Queue` must be declared* in `Queues` (else the publish throws) — this is what
  keeps the reason the old queue-name overload was deleted from coming back; declared queues also get
  a worker in per-queue mode.
- *Dedup id* `SparkMessages/{readable ≤64}.{first 16 bytes of SHA-256(versionless type + "\n" + key)}`.
  Pre-upgrade documents keep their old ids, so a duplicate of a pre-upgrade message is enqueued once
  more within their retention (README upgrade note).
- *`IMessageProgress`* writes through the processor's session (like `IMessageCheckpoint`) and marks
  `HandlerExecution.HasProgress`, which is how the terminal save knows to copy `@expires` onto the
  sidecar (a deferred patch per sidecar).
- *`WasReclaimedAsync`* no longer reports a throttle deferral (Pending, `WakeUp = false`, future
  `NextAttemptAtUtc`) as a lost claim; a sweeper reclaim always sets `WakeUp`.
- *Found, not fixed:* the processor's session saves the whole message after each handler, which
  writes back the `ClaimExpiresAtUtc` it loaded and so undoes a renewal made in between; the next
  renewal restores it. A handler longer than `ClaimTtl` right after such a save can be reclaimed for
  up to one `ClaimRenewInterval`. Pre-existing; both modes now share it.
  **Now fixed:** an `OnBeforeStore` hook on the processor's session stamps `ClaimExpiresAtUtc = now +
  ClaimTtl` on every save of a message it still owns (Processing), so no step save moves the expiry
  back; `TryRenewAsync` also reloads on a conflict with such a save instead of ending renewal.

**SP-A — second factor and recovery codes under `SparkSignInManager` (M5, 2026-09-28).**
*Question:* do the 2FA and recovery-code steps of `MapIdentityApi`'s `/login` still work when the
identifier is resolved by the override, in cookie and bearer mode?
*Method:* `SignInIdentifierTests` against a real Identity host (Spark stores, sign-in manager,
endpoints) on the embedded RavenDB; TOTP computed from the stored key per RFC 6238.
*Answer:* by email and by user name → 200 (bearer body carries `accessToken`), unknown identifier → 401.
With 2FA on, identifier = user name: password only → `401 "RequiresTwoFactor"`, wrong code → 401,
correct TOTP → 200 in both modes (cookie: `.AspNetCore.Identity.Application` set; bearer:
`accessToken`). Identifier = email: recovery code → 200, the same code again → 401, both modes. A
legacy account B whose user name is account A's email: B's password with that identifier → 401 and B's
`AccessFailedCount` stays 0 (no fall-through); A's password → 200. The session's sign-in instant
(`.spark.authenticated_at` in the ticket properties) reads back within a minute of the login.

**SP-B (rest) — secrets at rest (M5, 2026-09-28).** The key-document compatibility half ran in M1
(`SparkDataProtectionTests`).
*Question:* protected round trip through the store; restart over the persisted Raven key ring; legacy
plaintext; backfill idempotence; the GitHub token path; what an unreadable key does.
*Method:* `UserSecretsAtRestTests`: containers built with Spark's own `AddSparkDataProtection`
(`Storage=RavenDb`) + `AddSparkAuthentication`, raw documents read back through a plain session.
*Answer:* after `ResetAuthenticatorKeyAsync` and `SetAuthenticationTokenAsync("GitHub", "access_token", …)`
— the calls CodeCoverage's `GitHubUserTokenService` makes — both stored values start `sdp1:` and do
not contain the plaintext; `UserManager` reads the base32 key and the token back. A second container
over the same database reads both. A legacy plaintext key reads as-is; the backfill's first pass
protects 1 user, the second is skipped by the marker, and with the marker deleted a third pass
protects 0 and leaves the ciphertext byte-identical. Under another `ApplicationName` (a lost ring)
`GetValidTwoFactorProvidersAsync` still lists `Authenticator` and the original TOTP is rejected. A
stored document's metadata holds `@last-modified` and **no `@created`**; with revisions on, the
backfill filled `CreatedAtUtc` for the document with history and left the one stored before revisions
existed `null`. Not measured: `GitHubUserTokenService` itself against GitHub (its own tests use fakes).

**SP-C — provider verified-email signals (M5, 2026-09-28).**
*Question:* per provider (Google, Microsoft, Facebook, X, LinkedIn): the verified-email signal, the
`ClaimTypes.Name` shape, Microsoft consumers-tenant detection.
*Method:* `ExternalProviderPolicyTests.SP_C_…` resolves each preset's handler options (package
`11.0.0-rc.1.26425.128`) and lists endpoints, scopes and claim actions; the callback tests drive each
policy outcome through the real `/external-login-callback`. **No live provider was contacted** — what
each provider's response contains is from its documentation, not measured.
*Answer (measured, handler side):* Google — userinfo `https://www.googleapis.com/oauth2/v3/userinfo`,
scopes `openid profile email`; the handler maps `sub`/`id`, `name`, `email`… and **no verification
claim** — `email_verified→email_verified` exists only because the preset adds it. Microsoft — authorize
`…/common/oauth2/v2.0/authorize`, userinfo Graph `/v1.0/me`, scope `user.read` (+ `openid` from the
preset); `displayName→Name`, email via a custom action (mail / userPrincipalName); no tenant claim, so
the preset reads `tid` from the token response's `id_token`. Facebook — Graph `v22.0/me`, fields
`name,email,first_name,last_name`, no verification claim. X — `RetrieveUserDetails=True` (preset),
claim actions map only `email`. LinkedIn (preset over OAuth) — OIDC userinfo, `sub`, `name`, `email`,
`email_verified`. Registrations: GitHub `ProviderHandle`, the other five `DisplayNameSlug`.
*Answer (callback):* a no-signal policy → account created `EmailConfirmed=false`, user name
`Jöhn Doe` → `john-doe`, `RegistrationMethod external:StubProvider`, one confirmation mail to
`{PublicBaseUrl}/confirm-email?…`, signed in; with `RequireConfirmedEmail` → `confirm_email_sent`, no
application cookie, account exists; an undescribed scheme without `email_verified` → `email_not_verified`,
no account, no mail; `Jane Doe` then `Jane  DOE!` → `jane-doe`, `jane-doe-2`.
*Decisions from it:* Google and LinkedIn `email_verified=true` → verified, else no account; Microsoft
verified only for tid `9188040d-6c67-4c5b-b112-36a304b66dad` (consumers), otherwise no signal; Facebook
and X no signal. The `tid` value and each provider's response shape are documented facts, unverified here.

**SP-D — server-side SVG QR (M5, 2026-09-28).**
*Question:* library vs in-house encoder; does the SVG scan?
*Method:* `AuthenticatorQrCodeTests`: the otpauth URI rendered by `SparkQrCodeRenderer` (QRCoder 1.8.0,
ECC level M), the SVG's own geometry rasterised at 2 px/module and decoded by ZXing.Net's
`QRCodeReader`; separately, every module compared with the encoder's matrix.
*Answer:* a 117-character otpauth URI → `viewBox="0 0 53 53"` (45 modules + 4-module quiet zone), one
white `rect` and one `path` of horizontal runs; ZXing decodes the exact URI and every module matches.
**Library chosen** (MIT, no dependencies): an in-house encoder is mode selection + Reed–Solomon +
masking with nothing application-specific. Not measured: physical authenticator apps.

**SP-E — passkey clone detection (#439 SP2, M5, 2026-09-28).**
*Question:* does sign-in refuse a sign counter that did not advance from a non-zero baseline, and still
accept the permanent zero of synced passkeys?
*Method:* `PasskeyCloneDetectionTests`: a software ES256 authenticator (COSE key stored through
`AddOrUpdatePasskeyAsync`; authenticator data, client data and a DER signature built per assertion)
through the real `/passkeys/request-options` and `/passkeys/sign-in`.
*Answer:* stored 5 → presented 6: 200, stored count becomes 6. Stored 5 → 3 and stored 5 → 5: `401
passkey_failed`, stored count stays 5. Stored 0 → 0: 200, twice in a row. Identity's handler enforces
it; Spark adds no check (as `guide-passkeys.md` claimed, now measured).

**Deviations (M5).**
- *Spark owns the mail-sending half of the local-credential surface.* D6 (`forgotPassword` to
  unconfirmed addresses, reset confirms) and D16 (links to SPA pages) cannot be had from
  `MapIdentityApi`, whose `register`, `resendConfirmationEmail`, `confirmEmail`, `forgotPassword`,
  `resetPassword` and `POST manage/info` build their own links and check confirmation. They are
  filtered out in **every** mode (Full no longer maps Microsoft's surface verbatim) and mapped by
  `SparkAccountEndpoints` with the same contracts. `forgotPassword` now sends a **link**
  (`SendPasswordResetLinkAsync`) instead of Microsoft's code; `resendConfirmationEmail` sends only to
  unconfirmed accounts. Kept from Microsoft: `login`, `refresh`, `manage/2fa`, `GET manage/info`.
- *Confirm-new-email is not its own route*: `POST /spark/auth/confirm-email { userId, code, changedEmail? }`
  handles both (the SPA page gets the same query either way). Microsoft's `GET confirmEmail` was
  replaced too — it set the user name to the new email unconditionally, overwriting a chosen handle.
- *`confirmEmail` / `confirm-email` are mapped under `Disabled`* (they were not): a D7 sign-up from a
  provider without a signal must be confirmable in any mode.
- *`Spark:Auth:PublicBaseUrl` is new and required outside Development* for link-bearing mail. A link
  built from the request `Host` on anonymous `forgotPassword` is password-reset poisoning. Not a
  startup guard (HR, Fleet and CodeCoverage would stop between milestone commits, as the plan notes for
  the mail guard); the mail is refused and logged. M8 should configure it with the transport.
- *Unknown schemes fail closed.* A scheme without a preset keeps "`email_verified=true` or no account";
  only presets that **declare** no signal (Facebook, X, Microsoft work/school) get an unconfirmed
  account + mail.
- *GitHub keeps the login verbatim* (`SparkUserNameSource.ProviderHandle`) instead of a display-name
  slug: CodeCoverage compares `ClaimTypes.Name` (= user name) with repository owners, so a slug would
  change production behaviour. Every other preset slugs.
- *`CreatedAtUtc` backfill*: RavenDB has no `@created` (SP-B), so the source is the oldest revision's
  `@last-modified`, else `null`. `RegistrationMethod` is not inferred for existing accounts (`null`).
- *Purpose strings without the user id*; *unreadable key → a random key nobody holds* (2FA stays
  required, recovery codes work), unreadable token → absent. Reasons in `UserStore.Secrets.cs`.
- *Re-authentication for deletion* = the current password (lockout-counted) **or** a sign-in younger
  than `ReauthenticationMaxAge` (5 min), read from a ticket property `SparkSignInManager` stamps.
- *`GET manage/2fa/authenticator-uri` is read-only* (409 `no_authenticator_key` until `POST manage/2fa`
  created one), `Cache-Control: no-store`.
- *LinkedIn* uses the generic OAuth handler against LinkedIn's OIDC userinfo (no third-party package);
  its provider key is `sub`, and whether that equals the member id the retired v2 API (legacy
  `AspNet.Security.OAuth.LinkedIn`) stored is undocumented — MintPlayer's one LinkedIn login may land on
  the email-already-registered path instead of signing in.
- *Found:* the `[Inject]` generator emits one constructor per `partial` declaration; fields injected
  from a second file produced a second, ambiguous constructor. All of `UserStore`'s live in `UserStore.cs`.
- *T9*: warning `SPARK030` (text match on `"@mintplayer/ng-spark-auth"` in `$(SpaRoot)package.json`).

**H1 (M6 part) — `DeleteRevisionsOperation` for a purge (M6, 2026-09-28).** The config / read parts
of H1 stay in M7.
*Question:* does `DeleteRevisionsOperation` work on the Community licence, and in which order must a
purge delete the document and its revisions so that nothing is left?
*Method:* `PurgeRevisionsSpikeTests.H1_…` on the embedded RavenDB 7.2 server: revisions enabled on one
collection; a document created and edited twice; (A) document deleted, then
`DeleteRevisionsOperation(id, removeForceCreatedRevisions: true)`; (B) the reverse order; (C) a
force-created revision in a collection without a revisions configuration, deleted with the flag off,
then on. The server's `/license/status` is printed. Run three times: `RAVENDB_LICENSE` =
`raven-community-license.log`, = `raven-license.log`, and the shell's ambient value.
*Answer:* **the Community licence could not be applied** — with `raven-community-license.log` the
server reported `Type=None, Status=AGPL - Open Source, MaxCores=3` (the spike read this as the
file's licence having expired on 2026-09-25. That is unverified, because a licence RavenDB fails to apply
also falls back to AGPL without any error. Renew the Community licence before any Community-tier
measurement); with `raven-license.log`, `Type=Developer, Status=Commercial,
MaxCores=9`; the ambient value also gave AGPL. The results were identical under AGPL and Developer:
(A) 3 revisions → 4 after the delete (a delete revision) → the operation reported 4 deletes → **0
left**; (B) 3 → 3 deleted → 0 → **2** again after the document delete; (C) 1 → 2 after the delete →
without the flag 1 deleted, **1 left** (the force-created one) → with the flag 1 deleted, 0 left. So a
purge deletes the document **first**, then its revisions **with** `removeForceCreatedRevisions: true`.
Not measured: an actual Community licence (needs a current licence file), and a secured server where
the client certificate lacks database-admin (the operation is documented as admin).

**Deviations (M6).**
- *Restore and purge are gated under their own names in core.* §3.2 lists the rights; it does not say
  how core asks. `IDatabaseAccess` now uses `Restore/T` (not `Edit/T`) and row-gate action `"Restore"`
  for a `Restore` save, and `Purge/T` (not `Delete/T`) and `"Purge"` for a `Purge` delete — the only
  way a row policy can say "only a deleted row" while `Edit`/`Delete` keep hiding deleted rows. A
  restore of an id that names nothing is a 404 (the base save would otherwise create it).
- *Disabled-action aliases (M3 left it open).* The PRD is silent, so the conservative reading: a hook
  withholding `Restore`, `Edit` or `Save` refuses a restore; `Purge` or `Delete` refuses a purge. The
  403 names the withheld action that matched (`Edit` / `Delete`), not the operation.
- *Reference refusal checks only changed references.* §3.2 says a save that "points a reference at" a
  deleted target is refused; a reference that was already stored before its target was deleted does
  not block unrelated edits. Checked for `[Reference]` `string` and string-collection properties on
  the entity itself (not inside AsDetail rows).
- *Natural-id trap.* Core gained `IPersistentObjectInterceptor.OnNaturalIdCollisionAsync`; SoftDelete
  answers 400 "restore it instead" only to holders of `ViewDeleted/T` or `Restore/T` — everyone else
  keeps core's 404, so the deleted state of a row the caller cannot see is not disclosed.
- *Purge completion.* A purge through `IDatabaseAccess` returns quietly for a missing or
  foreign-collection id; revisions are deleted in the interceptor's after-delete hook (which only runs
  when the delete happened), after re-checking that the document is gone. An `OnDeleteAsync` override
  that did not delete makes the purge fail with a 500 rather than wipe the history of a live row.
- *Add-on endpoint helpers.* Core's envelope / refusal / request-type helpers are internal; the
  package needs the same answers, so core exposes them as `MintPlayer.Spark.Endpoints.SparkAddOnEndpoints`
  (History reuses it in M7). The package references `MintPlayer.Spark` (core), not only Abstractions.
- *Detail view of a deleted row.* `/spark/po/load` carries no `deleted` field, so a deleted row is a
  404 by id for everyone, `ViewDeleted` holders included. The recycle-bin list works (query
  `deleted: only`); opening a row from it needs a load-side flag (M10).
- *Analyzer.* SPARK022 (`SoftDeleteFilterAnalyzer`) flags `!x.IsDeleted` / `== false` on an
  `ISoftDeletable` inside a lambda converted to `Expression<…>`. The "index Map doesn't emit IsDeleted"
  check is a **startup warning**, not an analyzer: the query-to-index binding is model JSON, read at
  runtime (the wall `SortCompanionAnalyzer` documents).
- *Not in AllFeatures.* `MintPlayer.Spark.AllFeatures` does not reference SoftDelete: it would change
  every delete of an `ISoftDeletable` type for apps that only wanted the meta-package.

**Licence observed in the M7 runs.** The test fixtures used the ambient `RAVENDB_LICENSE` (which file
it holds was not inspected). `/license/status` read at the start of each test showed `Type=None,
Status=AGPL - Open Source` for the first tests of a process and `Type=Community, Status=Commercial,
Expired=False, MaxCores=3` for later tests **in the same process** — the licence is applied some time
after the embedded server starts, and a test that runs first sees AGPL. That timing alone could
explain the AGPL reading in H1 (M6 part) above. Results below say which licence they ran under; the
Community-tier limits were measured under `Type=Community`.

**H1 (M7 part) — revisions configuration and reads (M7, 2026-09-28).**
*Question:* do the revisions configuration and revision reads History needs work on Community, and
what does the licence refuse?
*Method:* `HistorySpikeTests.H1_…`: (1) seven `ConfigureRevisionsOperation` probes, each result
recorded; (2) a collection configured with `MinimumRevisionsToKeep = 2`, `MinimumRevisionAgeToKeep =
30 d` read back through `GetDatabaseRecordOperation`; two documents written three times each, then
`GetMetadataForAsync`, `GetForAsync<T>`, `GetAsync<T>(changeVector)` for the document's own, another
document's and an unknown change vector.
*Answer:* under **Community**: accepted — a collection without limits, `PurgeOnDelete = true`, a
**disabled** default; refused with `LicenseLimitException` — `MinimumRevisionsToKeep = 100` ("exceeds
the licensed one '2'"), `MinimumRevisionAgeToKeep = 90 d` ("exceeds the licensed one '45'"), an
**enabled** default ("doesn't allow the creation of a default configuration for revisions"), and
therefore any configuration carrying one. Under AGPL (an earlier run) an enabled default with 100
revisions was accepted. The configuration reads back as written (min 2, 30 d). Revisions list
**newest first** (change vectors in reverse write order, contents `v3, v2, v1`); metadata keys
`@change-vector, @collection, @flags, @id, @last-modified, Raven-Clr-Type`, flags `HasRevisions,
Revision`. `GetAsync<T>(cv)` of **another** document's change vector returns that document's revision
(`@id = HSpikeDocs/b`) — a change vector is not scoped to the id asked about; an unknown one returns
null. 5 requests for the reads.

**H2 — a save's change vector is the newest revision's (M7, 2026-09-28).**
*Method:* `HistorySpikeTests.H2_…` (raw session) and `HistoryTests.H2_…` (`POST /spark/po/update`
through the pipeline, revisions from the model).
*Answer:* equal in both: the raw save's `GetChangeVectorFor` = the newest revision's `@change-vector`
= the reloaded document's; the update response's `etag` = the newest revision's change vector
(`A:3-…` in both runs).

**H4 — merging the revisions configuration (M7, 2026-09-28).**
*Question:* does reading + merging keep existing settings and is it idempotent; can it be read without
a server-wide operation?
*Method:* `HistorySpikeTests.H4_…`: an operator configuration (a disabled default with 1, collection
`HSpikeOthers` with 2); then a naive `ConfigureRevisionsOperation` naming only `HSpikeDocs`; restored;
then read-modify-write; then the same again. Plus `GET /databases/{db}/revisions/config` before and
after a configuration. Kept end to end in `HistoryTests.Startup_merges_…` through
`RevisionsConfigurator` at host startup.
*Answer:* the naive send **wiped** the default (null) and every other collection (only `HSpikeDocs`
left) — `ConfigureRevisionsOperation` replaces the whole configuration. Read-modify-write kept the
default (disabled, 1) and `HSpikeOthers` (2) and added `HSpikeDocs`. Sending an **unchanged**
configuration again still bumped the database-record etag (35 → 36), so "idempotent" must mean "send
nothing when equal" — the configurator compares first and a second `ApplyAsync` returned no changes.
`GET /databases/{db}/revisions/config` answers 404 (empty body) before any configuration and `200
{"Default":null,"Collections":{…}}` after, so the configurator reads through that database-level
endpoint (a small custom operation; the 7.2.6 client has none) rather than the server-wide
`GetDatabaseRecordOperation`. Not measured: which clearance that endpoint needs on a secured server.

**H3 — revert fidelity through the save pipeline (M7, 2026-09-28).**
*Question:* does a revert through `SavePersistentObjectAsync(…, Revert)` restore a `TranslatedString`,
a `DateTimeOffset`, `AsDetail` rows and leave undeclared fields?
*Method:* `HistoryTests.H3_…`: v1 = `Title v1`, `Label {en: one}`, `DueAt 2026-01-01T10:00+02:00`,
`Lines [a×1, b×2]`, `CreatedBy alice`; v2 written raw (`Label {en: two, nl: twee}`, `DueAt
2026-06-01T08:30−05:00`, `Lines [b×5, c×3]`); an undeclared field `Legacy` patched in. First v1's
values saved as a **plain edit** (`Save`, base pipeline only), then the real `POST /spark/po/revert`
as bob.
*Answer:* the plain edit left `Label {"en":"one","nl":"twee"}` — the base save **merges**
`TranslatedString` per language, so a language added after the revision survives. The revert (with
History's interceptor) gave `Title v1`, `Label {"en":"one"}`, `DueAt 2026-01-01T10:00:00+02:00`
(offset kept), `Lines [a×1, b×2]`, `Legacy` still `added after v1`, `CreatedBy alice`, `ModifiedBy bob`.

**Deviations (M7).**
- *Revert's rights and aliases.* `IDatabaseAccess` gates a `Revert` save under `Revert/T` **and**
  `Edit/T` (the PRD lists both), row action `"Revert"`, and the disabled-action hook refuses it when
  `Revert`, `Edit` or `Save` is withheld — the same reading as Restore in M6.
- *Base-verb mapping for Actions-class row rules (M6 finding).* The PRD is silent; the safe reading
  was taken: an Actions class's `GetRowFilterAsync` / `IsAllowedAsync` is asked about `"Edit"` for a
  restore or revert and `"Delete"` for a purge; row policies keep the real name (SoftDelete needs it).
  Before, a rule handling only the built-in verbs let those operations through unfiltered. Measured
  with the mapping disabled: a purge of a row the Delete rule forbids answered 204, and a revert that
  hands a row now owned by someone else back to its old state answered 200 (WITH CHECK judges the
  result, which passes); a restore was already refused by WITH CHECK under `"Edit"`. With the mapping:
  404 for all three (`Restore_and_purge_are_judged_…`, `Revert_is_judged_…`). This changes what M6's
  row-gate action names mean for Actions classes only.
- *Exact `TranslatedString` on revert.* H3 showed the base merge would keep later languages; the
  History interceptor replaces each reverted (non-shielded) `TranslatedString` with the revision's.
  Validation rules are not re-run on a revert (the revision was valid when written); documented.
- *Revisions read at the database level* (H4), not via `GetDatabaseRecordOperation`; a model limit
  beyond the licence (Community: 2 revisions / 45 days / no enabled default) refuses startup with
  RavenDB's reason; `Spark:History:ConfigureRevisions=false` opts out.
- *No `.Abstractions` for History.* §3.3 names one package; `IAuditable` lives in
  `MintPlayer.Spark.History` (a Domain project that implements it references the package).
- *Names at read time.* D8's "resolved to a name at read time" needs an id → name source core does
  not have; History adds an optional `IHistoryUserNameResolver` (none → ids only).
- *Revision reads use a core presenter.* `IRowSecurity` is internal; core gained the public
  `IPersistentObjectPresenter` (breadcrumbs + redaction, no row filter) so an add-on renders content
  core did not load. Redaction is the union of "protected on the revision" and "protected on the
  current row". `SparkAddOnEndpoints` gained the 409 (`IsConcurrencyConflict` / `ConcurrencyConflict`).
- *Observers fire for model-enabled types only* (`revisions.enabled`), after the write; a purge is not
  reported (its revisions are gone). `canViewHistory` was added to the permissions endpoint alongside
  the requested `canRevert`.
- *M6 carry-overs.* (1) A delete refused by a later interceptor now evicts the entity from the request
  session in `IDatabaseAccess` (any interceptor, and the Actions class's `OnBeforeDeleteAsync`), so
  no later save in the request writes the mark (test fails without the eviction). (2)
  `/spark/po/load` takes `deleted`, honoured through the same row policy as queries (ViewDeleted);
  ng-spark `get(type, id, { deleted })`, `SparkClient.GetPersistentObjectAsync(…, deleted:)`. (3) The
  permissions endpoint reports `canRestore`, `canPurge`, `canViewDeleted`, `canViewHistory`,
  `canRevert` (ng-spark fields optional, for older servers). (4) A purge first runs
  `DeleteRevisionsOperation` on an id that names nothing (same endpoint and authorization, no effect);
  if refused, the purge is refused before the document is deleted; success is cached per process.
  README documents the database-admin requirement. Not measured: the probe on a secured server with a
  non-admin certificate (the embedded server is unsecured); the test replaces the probe with a
  refusing fake.


**S-M1 — where embedded `.mjml` resources land (M8, 2026-09-28).**
*Question:* for `{lang}/{name}.mjml` and `{name}.{lang}.mjml`, main or satellite assembly?
*Method:* `MailSpikeM1Tests.S_M1_…`: fixtures under `tests/…/Mail/SpikeM1Templates` embedded four ways
in the test csproj (folder layout; suffix layout; suffix with `WithCulture="false"`; `LogicalName` +
`WithCulture="false"`), plus a `Content` item; manifest names of the main assembly and of the `nl` /
`nl-BE` satellites listed.
*Answer:* folder layout → main assembly, `…folder.nl.Welcome.mjml` / `…folder.nl_BE.Welcome.mjml`
(`-` becomes `_`). Suffix layout → the neutral file stays in main as `…suffix.Welcome.mjml`; **`nl` and
`nl-BE` are moved into `nl/…resources.dll` and `nl-BE/…resources.dll` under the neutral file's name**
(`…suffix.Welcome.mjml`). With `WithCulture="false"` they stay in main under their full names
(`…Welcome.nl-BE.mjml`). `LogicalName="SparkMailSpike/%(RecursiveDir)%(Filename)%(Extension)"` gives
`SparkMailSpike/Auth\Welcome.nl-BE.mjml` — the OS separator from `%(RecursiveDir)` (Windows build).
`Content` files are copied under their own names.

**S-M2 — Mjml.Net vs npm mjml (M8, 2026-09-28).**
*Question:* does Mjml.Net render the shipped templates (with `mj-include`) like npm mjml, Outlook
conditionals included?
*Method:* `MailSpikeM2Tests.S_M2_…` (npm half with `SPARK_SPIKE_SM2_MJML` = the mjml CLI): the three
`SparkAuth/*` templates rendered by Mjml.Net 4.15.0 through `SparkMailRenderer`; the same Scriban
output (main file + include) written to disk and rendered by npm `mjml` 5.4.1
(`--config.beautify false --config.allowIncludes true`); visible text compared after the same
HTML→text conversion.
*Answer:* Mjml.Net: 0 validation errors on all three; 9 `<!--[if mso` conditionals and 10 `<table>`s
each; 7591 / 7650 / 8360 characters. npm mjml: 9 conditionals and 10 tables each; 9138 / 9197 / 10079
characters. **Visible text identical for all three** (headings, paragraphs, button text with its URL,
the fallback link, the included footer). Neither emits VML buttons for `mj-button`. npm mjml 5 refuses
`mj-include` unless `allowIncludes` is set (first run: the footer was silently missing). Not measured:
rendering in a real Outlook client.

**S-M4 — Scriban strict mode, JSON data, escaping, culture (M8, 2026-09-28).**
*Method:* `MailSpikeM4Tests` against Scriban 7.5.0 (`Scriban.Signed`).
*Answer:* `StrictVariables` refuses an unknown top-level name (`ScriptRuntimeException: The variable
or function 'missing' was not found`) but a **missing member renders empty** (`{{ user.missing }}` →
`''`, no error); with `EnableRelaxedMemberAccess = false` it fails too ("Cannot get member with name
missing"). A Newtonsoft `JObject` is readable (`Ann|2`); an STJ `JsonElement` is **opaque** (members
null: "Cannot get the member data.items.size for a null object"); converted to `ScriptObject`s it
renders `Ann|2|12` under strict mode. Scriban writes values **verbatim** (`<b>Ann & "Co"</b><script>`);
pre-escaped with `WebUtility.HtmlEncode` the value survives Mjml.Net as `&lt;b&gt;Ann &amp; &quot;Co&quot;…`
and an `href` with `&amp;` stays intact. Culture: pushed `nl-BE` → `1234,5|1.234,50|05 januari 2026|5/01/2026`,
`en-US` → `1234.5|1,234.50|05 January 2026|1/5/2026`; **nothing pushed on an `nl-BE` thread →
invariant** (`1,234.50|05 January 2026`). A `DateTimeOffset` value makes `date.to_string` fail
("Unable to convert type DateTimeOffset to DateTime") — found in `MailManagerTests`.

**S-M5 — boky/postfix VERP → pipe → HTTP (M8, 2026-09-28).**
*Question:* can the relay image route VERP bounces to an HTTP endpoint; `master.cf` customisation,
`curl`, exit 75.
*Method:* scratch script against `boky/postfix:v4.3.0` (Postfix 3.7.11, Debian 12) and a Python HTTP
receiver on one Docker network: an init script in `/docker-init.db/` (`postconf -e relay_domains,
transport_maps = inline:{…=sparkbounce:}, recipient_delimiter`, `postconf -M sparkbounce/unix=… pipe
flags=Rq user=nobody argv=… ${original_recipient}`), the pipe script (curl, `|| exit 75`), a DSN injected
with `sendmail -f "" -t`; then the receiver stopped, another DSN, receiver started, `postqueue -f`.
*Answer:* `curl` present (`/usr/bin/curl`), `wget` absent; `/docker-init.db/*.sh` are sourced after the
image's own postconf steps (`execute_post_init_scripts`), and the image then unsets secret variables,
so the pipe reads URL and secret from files. The master.cf entry read back as written. Up: one POST
`/spark/mail/bounces?recipient=bounces%2B{id}%40verp.test`, bearer correct, `Content-Type:
message/rfc822`, 720 bytes containing `message/delivery-status`, queue empty, `status=sent (delivered
via sparkbounce service)`. Down: `dsn=4.3.0, status=deferred (temporary failure. Command output: curl:
(6) Could not resolve host: sm5-receiver)`, one message in the queue. Back + flush: second POST (204),
queue empty. Recipe in `guide-outgoing-mail.md` §8.3.

**S-M6 — MailKit against a relay advertising STARTTLS (M8, 2026-09-28).**
*Method:* `MailSpikeM6Tests` against `FakeSmtpServer` (loopback; advertises STARTTLS, answers it 454),
MailKit 4.18.1.
*Answer:* `SecureSocketOptions.None`: `Capabilities` lists StartTLS, the client **never sends
STARTTLS**; commands `EHLO`, `MAIL FROM:<bounces+d-1234@verp.app.example> SIZE=221`, `RCPT TO`, `DATA`,
`QUIT`; the headers carry `From: App <noreply@app.example>` and `Message-Id`, **no `Sender:`** — the VERP
address is envelope-only. `StartTlsWhenAvailable` sends STARTTLS and the connect fails
(`SmtpCommandException: 4.7.0 TLS not available`). Refusals surface as `SmtpCommandException`:
`553` at MAIL FROM → `SenderNotAccepted`, `StatusCode 553`; `550` / `451` at RCPT →
`RecipientNotAccepted` with the mailbox — so 5xx vs 4xx is `(int)StatusCode`.

**Deviations (M8).**
- *Template layout* (owner decision, §3.10): `{name}.{culture}.mjml` in the app's folder over embedded
  defaults, not `{lang}/{name}.mjml`; S-M1 made it safe (`WithCulture="false"`). Precedence is per
  file: an app's neutral file does not hide a shipped `nl` file — documented.
- *Strict members:* §3.10 says "strict variables"; S-M4 showed that covers top-level names only, so
  `EnableRelaxedMemberAccess = false` is set too. Dates in template data are ISO strings converted to
  `DateTime` (the clock time as written; S-M4 found `DateTimeOffset` unsupported by `date.to_string`).
- *Package split:* `MintPlayer.Spark.MailManager.Abstractions` (`ISparkMailer`, requests,
  `ISparkMailSuppressions`, `ISparkMailRecipientCulture`, template-assembly registration) so
  Authorization wires account mail without referencing MailKit/Mjml.Net/Scriban. The `IEmailSender<TUser>`
  and link-confirmation implementations live in Authorization (auth code stays out of the mail
  package), replacing Identity's no-op only when it is the registered one; transient (MapIdentityApi
  resolves it from the root provider — a scoped registration fails there, found by the OIDC tests).
  `IEmailSender`'s link methods receive HTML-encoded links (Identity's contract); the sender decodes
  them because the renderer escapes.
- *Scriban.Signed* instead of `Scriban`: WireMock.Net in the test project depends on `Scriban.Signed`;
  both side by side are CS0433 on every type. Same library, strong-named.
- *Bounce endpoint always mapped* through the generated `MapSparkMailManagerEndpoints()` and answers 404
  unless enabled (the per-library generated method maps every endpoint; History and SoftDelete were
  moved onto theirs too). Rate limit is an endpoint-local fixed window (120/min), not a second
  `UseRateLimiter()`.
- *Delivery records* are written when the worker handles the mail (sent / suppressed / failed), not at
  queue time: one write per mail, and the record holds the address a bounce is matched against. The
  bounce suppresses **the delivery record's address**, not the DSN's `Final-Recipient` (a forwarded
  bounce could name another address).
- *D6 guard* fires for `LocalCredentials = Full` only (the one mode that maps `register`); a sender is
  "discarding" when it is Identity's no-op or Spark's with no MailManager. 14 auth test hosts that map
  registration without being about mail register a test sink or set `AllowUnconfirmedRegistration`.
- *CodeCoverage* (D10): no app template copy — the shipped `SparkAuth/LinkConfirmation` carries
  `SparkLinkConfirmationMessage`'s wording, in HTML + text instead of text only; `ApplicationName=Coverage`
  and `From:Name` moved to `appsettings.json`; MailManager is added only when `Smtp:Host` and
  `From:Address` are set (the old `IsConfigured` rule), so production with no `MAIL_FROM_ADDRESS` still
  starts and sends nothing. `implicitDependencies` needed no entry (Nx infers project references).
- *Found (M5 left it):* `XsrfSurfaceTests.Auth_surface_…` still pinned MapIdentityApi's surface; M5's account endpoints (incl. `register`, `resendConfirmationEmail`, `manage/account`, `manage/password`, `manage/profile`, `confirm-email`) all require antiforgery — the pinned lists were updated to the measured surface.
- *Not built:* VERP tier 2 (MX + inbound 25) is a recipe only (§6 out of scope); complaint (ARF)
  parsing; a UI for `PreferredCulture` (M10 profile page can expose it).

**S-TZ1 — `FindSystemTimeZoneById` on path-shaped ids (M9, 2026-09-28).**
*Question:* what does .NET 11 do with path-shaped zone ids on Windows and Linux — is the regex the
only thing between a header and the file system?
*Method:* scratch console app (`net11.0`, .NET `11.0.0-rc.1.26425.128`), run on Windows 11 and
self-contained on `mcr.microsoft.com/dotnet/runtime:11.0-preview` (Ubuntu 26.04, `TZDIR` unset), 35
probes through `FindSystemTimeZoneById`, each also checked against the PRD regex. Pinned in
`RequestTimeZoneResolverTests.The_id_shape_check_is_the_PRD_regex`.
*Answer:* both OSes throw `TimeZoneNotFoundException` for every `..`, absolute (`/etc/passwd`,
`/usr/share/zoneinfo/Europe/Brussels`), `./`, trailing-slash, backslash, NUL and `%2F` probe, so .NET
itself refuses traversal. **Linux resolves `Europe//Brussels`** (found, id kept as given), and also
non-zone names in the zoneinfo folder: `posixrules` (−05:00), `localtime` (the container's zone, UTC),
`right/Europe/Brussels`, `Factory`; `leapseconds` throws `InvalidTimeZoneException` ("the file … was
corrupt"). `zone.tab`/`tzdata.zi`/`iso3166.tab` are not found. Windows finds none of those. The regex
refuses `Europe//Brussels` and every dotted or path-shaped probe; it accepts `posixrules`, `localtime`,
`Factory` and `right/…` (well-formed names of real tz files — harmless, and the resolver catches both
exception types). A 65-character id matches the regex (the length cap is what refuses it).

**S-TZ4 — the strict regex against `SparkClient.TimeZoneId` callers passing Windows ids (M9, 2026-09-28).**
*Method:* the same app enumerated `GetSystemTimeZones()` and the Windows→IANA conversions on both OSes;
`Intl.supportedValuesOf('timeZone')` from Node 24.15 (ICU 78.2, tz 2026a) as the browser's list; a grep
for `TimeZoneId =` callers.
*Answer:* the server resolved Windows ids before this change on **both** OSes (`Romance Standard
Time`, `W. Europe Standard Time`, `Pacific Standard Time (Mexico)` all found on Linux too). The regex
refuses **134 of the 141** Windows ids (spaces, dots, parentheses), so such a caller would silently
fall back to UTC. In-repo callers pass IANA ids only (`Europe/Brussels`, `Asia/Kolkata` in
`EndpointCoverageTests`). `TryConvertWindowsIdToIanaId` converts 139 of 141 on Windows, and the same
answers on Linux (`Romance Standard Time`→`Europe/Paris`, `Pacific Standard Time (Mexico)`→
`America/Tijuana`, `UTC`→`Etc/UTC`); every converted id passes the regex. All 419 Linux system ids and
all 418 browser ids pass it; the longest browser id is 30 characters
(`America/Argentina/Rio_Gallegos`). 5 browser ids are unknown to .NET on Windows by direct lookup
(`America/Ciudad_Juarez`, `America/Coyhaique`, `Antarctica/Troll`, `Antarctica/Vostok`,
`Asia/Urumqi`). Pinned in `A_Windows_zone_id_is_sent_as_its_IANA_id` and
`Every_zone_this_runtime_reports_as_IANA_passes_the_shape_check`.

**S-TZ2 — how MintPlayer's prerender gets its data (M9, 2026-09-28).**
*Method:* read `C:\Repos\MintPlayer` (branch `feature/spark-migration`): `Program.cs`,
`PublicSite/MintPlayerSpaPrerenderingService.cs`, `ClientApp/src/main.server.ts`,
`app/ssr/server-renderer.ts`, `app.config.ts`; a scratch Node script calling Angular 22.2's
`formatDate` under `TZ=UTC`.
*Answer:* **in-process.** `UseSpaPrerendering` (MintPlayer.AspNetCore.SpaServices) renders in Node, but
the data comes from `ISpaPrerenderingService.OnSupplyData(HttpContext, data)`, which loads from RavenDB
inside the ASP.NET Core request and hands it to the boot function as `params.data`; `main.server.ts`
renders with a path-only URL and makes no HttpClient call ("no relative HttpClient call is made during
the render"). So the browser's cookie is on the request `IRequestTimeZoneResolver` reads, and
**ng-spark needs no SSR cookie-forwarding helper**. MintPlayer's `app.config.ts` registers
`withSparkAuth()` only, not `withSparkTimezone()`. Side finding: Angular's `formatDate` (what
`DatePipe` calls) **ignores an IANA id silently** — for 12:00Z, `'Europe/Brussels'` and
`'Asia/Tokyo'` both render `2026-07-15 12:00 Z` (the process zone), `'+0200'` renders
`14:00 +02:00`. A date in a Node render follows the Node process's zone whatever the cookie says.

**S-TZ3 — the interceptor during a server-side render (M9, 2026-09-28).**
*Method:* vitest, `PLATFORM_ID = 'server'`, the pre-M9 interceptor, a relative `/spark/po/load`
request; run on this machine and under `TZ=UTC`.
*Answer:* the pre-M9 interceptor **sent the process's zone**: `expected 'Europe/Brussels' to be null`
locally, `expected 'UTC' to be null` under `TZ=UTC` — on the server that header wins over the viewer's
cookie. With the `isPlatformBrowser` guard: no header and no cookie write on the server platform
(21/21 in `spark-timezone.interceptor.spec.ts`, both runs).

**Deviations (M9).**
- *Windows ids:* the PRD regex stands (owner decision), but S-TZ4 showed it breaks a `SparkClient`
  caller passing a Windows id, which worked before. `SparkClient` now converts a Windows id to IANA
  before sending; one that does not convert is sent as given and the server falls back.
- *Cookie write point:* the cookie is written by the interceptor on the first browser request, not
  by an app initializer — `withSparkTimezone()` returns `HttpFeature`s, and every Spark app makes a
  request on start. It is written only when the zone passes the server's shape check.
- *Logging:* a malformed value is logged at Debug with its length only (never the value); a
  well-formed but unknown id keeps the existing Warning, now naming the source (`header`/`cookie`).
  `Spark:TimeZone:CookieName=""` disables the cookie (measured: the binder binds the empty string).
- *Mail rendering is not wired to the resolver:* MailManager renders in a queue worker with no
  request (the resolver answers UTC there). The guide says to convert at enqueue time, while the
  request is still available.
- *No SSR helper* in ng-spark (S-TZ2). The guide documents the `DatePipe` limitation instead.

**S-M5b — the bounce pipe's exit code per endpoint answer (M9, carry-over from M8, 2026-09-28).**
*Question:* M8's pipe exited 75 on every non-2xx, so a 400 report sat in the queue for 5 days. Which
exit code drops a report the endpoint can never accept without Postfix retrying it or bouncing the
bounce, and does the drop get logged?
*Method:* the S-M5 setup again (`boky/postfix:v4.3.0`, the recipe's init script and pipe, a DSN from
`sendmail -f "" -t`), with a Python receiver answering the status in its path; one DSN per case, then
`postqueue -j | wc -l`, `postsuper -d ALL`. Two runs: 204/400/404/413/422/401/403/429/500/503/
unreachable, then 204/400/422/401 and 400 with exit 69, followed by 15 s of log.
*Answer:* 2xx → `status=sent`, queue 0. 400/404/413/422 with exit 0 → `status=sent`, queue 0, and
**the command output is appended on success too** (`delivered via sparkbounce service (spark-bounce:
dropped, the endpoint answered 400 …)`). 401/403/429/500/503 with exit 75 → `dsn=4.3.0,
status=deferred (temporary failure. Command output: spark-bounce: the endpoint answered 401; …)`,
queue 1; unreachable → `curl: (6) Could not resolve host … answered 000`, deferred. Exit 69 →
`dsn=5.3.0, status=bounced (service unavailable. …)`, then `removed`, and no `postfix/bounce` line in
the next 15 s (the message has a null sender, so no notification). `postlog` exists at
`/usr/sbin/postlog` but the pipe's empty environment cannot find it (`postlog: not found`). The
recipe's `chown nobody` of the URL/secret files makes `postfix-script` warn `not owned by root` at
start (harmless, pre-existing).
*Resolution:* 2xx → 0; 400/404/413/422 → echo a `dropped` line, exit 0; everything else (401/403,
429, 5xx, no answer, unlisted codes) → 75. Documented in the MailManager README and
`guide-outgoing-mail.md` §8.3. The guide's exact script, extracted from the markdown and run as
`nobody` under `env -i /bin/sh` in the same image, exited 0 for 204/400/404/413/422 (the four with the
`dropped` line) and 75 for 401/403/429/500/unresolvable host. No automated test covers the script (none existed; it is a recipe run
inside the relay container).

**S-TZ5 — the five browser zones .NET on Windows does not know (M10 carry-over from S-TZ4, 2026-09-28).**
*Question:* do `America/Ciudad_Juarez`, `America/Coyhaique`, `Antarctica/Troll`, `Antarctica/Vostok` and
`Asia/Urumqi` resolve through the M9 resolver on Windows, whose fallback is `TryConvertIanaIdToWindowsId`?
*Method:* scratch probe and `RequestTimeZoneResolverTests` on Windows 11 10.0.26200, .NET
`11.0.0-rc.1.26425.128`, ICU 72.1.0.4 (NLS off): for each id, `FindSystemTimeZoneById`,
`TryConvertIanaIdToWindowsId`, and what the resolver returned; offsets probed on 2026-01, -04, -07, -11
and 2027-07.
*Answer:* **none resolved.** For all five, `FindSystemTimeZoneById` threw `TimeZoneNotFoundException` and
`TryConvertIanaIdToWindowsId` returned `False` (empty id), so the resolver fell back to UTC for each.
*Resolution:* when an id does not resolve, the resolver uses an explicit alias to a zone with the same
current rules. A server whose tz data knows the id still uses it directly. Each alias target resolves
on this machine, and its probed offsets match the zone's current rules:

| Zone | Alias | Offsets |
|---|---|---|
| `America/Ciudad_Juarez` | `America/Denver` | −7 / −6, US DST |
| `America/Coyhaique` | `America/Punta_Arenas` | −3 all year |
| `Antarctica/Vostok` | `Asia/Tashkent` | +5 |
| `Asia/Urumqi` | `Asia/Dhaka` | +6 |
| `Antarctica/Troll` | built-in rule (no alias) | +0, and +2 from the last Sunday of March to the last Sunday of October, switching at 01:00Z |

An alias hit logs at Information, naming the id, its source and the alias. Unknown ids keep the UTC
fallback and the Warning. Pinned in
`A_zone_unknown_to_the_server_data_resolves_to_its_current_rules`,
`Every_alias_target_resolves_on_this_platform` and
`The_built_in_Troll_rule_switches_at_the_EU_instants_by_two_hours`.

Side finding: this machine's Windows zone data is stale for two other zones. `Asia/Almaty` and
`Central Asia Standard Time` still report +06, though Almaty moved to +05 in 2024, so no alias points at
them. `America/Ojinaga` still reports −7/−6, though it has followed Central time since 2022.

**Deviations (M10).**
- *Bounce endpoint when disabled:* answers 503 instead of 404, so Postfix keeps the report (exit 75)
  until the endpoint is enabled. The endpoint never distinguished an unknown delivery: that case is 204,
  logged, and suppresses nothing. It therefore never answers 404, and a 404 can only mean the relay's URL
  does not reach it. The pipe recipe moved 404 from the drop branch to the retry branch, like a wrong
  secret. The exit-75 branch itself was measured in S-M5b; 404 was not re-measured under the new branch.
- *`RedirectTo`:* refused at startup in Production. It still redirects in other environments, with a
  warning, and the Development fail-closed rule is the owner decision in §3.10.
- *Transport context:* `ISparkMailTransport` gained two default members, `DeliversToRealRecipients` and
  `SendAsync(…, SparkMailSendContext, …)`, which forwards to the old overload. `SparkMailMessage` gained
  a nullable `Queue`, so Mailpit's `X-Tags` can name the lane.
- *Preferred mail language:* `GET/POST /spark/auth/manage/profile` carries `preferredCulture`. Absent
  keeps it, null or empty clears it, and otherwise it must be a predefined culture, stored in canonical
  casing; an invalid value is a 400 under `PreferredCulture`. The personal-data export includes it. The
  profile page exposes it.
---

## 5. Risks

1. **Size.** One PR carrying a core seam, 4 packages (+2 abstractions), auth, mail, messaging, a new demo app and an E2E host refactor. Mitigation: milestone commits, each type-checked; one test sweep at the end (per the user's global rules); CI must be green across **all 5** test projects.
2. **Production redeploy on merge** (CodeCoverage): new startup guards (Data Protection storage, mail transport, forwarded headers) stop it from starting unless the VPS config is set first → pre-merge ops checklist (plan §"Pre-merge").
3. **Key ring**: wrong `ApplicationName`/prefix during CodeCoverage's migration signs every production user out once → SP-B + a test reading a current-format key document.
4. **Behind a CDN**: D15 defaults make every visitor share the proxy IP → verify both production front-ends.
5. **Absent-field semantics** (`!= true`) — this repo has shipped the `!x` bug twice.
6. **History bypassing redaction/row security** on old revisions.
7. **Wildcard removal and DisableActions removal** break unknown third-party consumers — accepted (preview), release notes.
8. **Vote fraud**: a patient attacker still farms slowly; thresholds are visible config. Bounded by "only reversible privileges are earnable".
9. **Revisions keep personal data**; no storage quota exists.
10. **E2E host refactor** in known-flaky infrastructure can block CI for the whole PR.

## 6. Out of scope (genuinely not being done)

- Rewriting the overridable core pipeline (D1 — owner decision, documented gaps instead).
- Moderation: suggested edits, accepted answers/bounties, close votes, badges, notifications, device fingerprinting, ML Sybil detection, vote fuzzing, shadow bans, automatic suspension.
- Opening inbound SMTP on the VPS (tier-2 bounces are a documented recipe, D9).
- Absorbing the MintPlayer repository into `apps/MintPlayer`: **decided, as the next step after this PR merges**, then the old repository is archived and this one pinned (see the plan).
- MintPlayer's own upgrade (tracked in the MintPlayer repo) — but its ForwardedHeaders and soft-delete overrides are called out for it.
- Deprecating SocketExtensions 10.0.0 on nuget.org (manual account step).
