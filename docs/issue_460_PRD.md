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
| D5 | **Data Protection always on, in Spark core.** Persisted keys required outside Development: `Spark:DataProtection:KeysPath` (file system) or `Spark:DataProtection:Storage=RavenDb`; unset → startup error explaining the redeploy sign-out. Development defaults to a local folder. `Spark:DataProtection:ApplicationName` configurable (default entry assembly name; **CodeCoverage keeps `CodeCoverage`**). Raven store keeps the `DataProtectionKeys/` prefix; CodeCoverage's hand-written setup + repository deleted. Docs state loudly: an unmounted key folder loses its keys on redeploy — the operator's responsibility. Authenticator key + external-login tokens stored as `sdp1:` + Protect(value); legacy plaintext still read; idempotent backfill. |
| D6 | **`RequireConfirmedEmail`** option, default `false`. Startup guard: registration enabled + no-op email sender → error, unless `Spark:Auth:AllowUnconfirmedRegistration=true`. **Completing a password reset (valid token) sets `EmailConfirmed=true`**, and `forgotPassword` sends to unconfirmed addresses (rescues MintPlayer's ~650 unconfirmed users). Demo apps use MailManager pickup-folder mode. Closes #299. |
| D7 | **Per-provider verified-email trust** for social sign-up. Each provider preset declares its verified-email signal; Microsoft trusted only for personal accounts (consumers tenant); a provider without a reliable signal → account created unconfirmed + confirmation mail. Add Twitter/X and LinkedIn presets. **User name = slug of display name** (`john-doe`, `john-doe-2`), editable on the profile page (never the email local part). Spike SP-C required. |
| D8 | **GDPR.** `DELETE /spark/auth/manage/account` (re-authentication required) → all `ISparkAccountDeletionHandler<TUser>` (multi-registered) → `UserStore.DeleteAsync` last (releases email/passkey reservations; a handler failure leaves the account intact and retryable). `GET /spark/auth/manage/personal-data` → account JSON + all `ISparkPersonalDataContributor<TUser>`. Audit fields hold the **user id only**, resolved to a name at read time ("deleted user" afterwards). **Revisions are not rewritten** — stated in the guide; content-level personal data is the app's handler's job. Moderation ships a deletion handler. |
| D9 | **MailManager is building blocks, configured through `IConfiguration`.** Pluggable `ISparkMailTransport`: SMTP (MailKit) when `Spark:Mail:Smtp:Host` is set (Security `None`/`StartTls`/`SslOnConnect`/`Auto` — no forced STARTTLS); `.eml` pickup folder when `Spark:Mail:PickupFolder` is set; custom transport replaces both; none → startup error. Bounce handling opt-in: `Spark:Mail:Bounces:VerpDomain` enables VERP; `Spark:Mail:Bounces:Endpoint:Enabled` + secret maps `POST /spark/mail/bounces`; DSN parser pluggable, **the secret check stays in the framework, before the parser**. Suppression list always present (checked at publish and send; fed by bounces, unsubscribes, public `ISparkMailSuppressions`). One-click `List-Unsubscribe` (RFC 8058) per message type (default on for bulk). Dev mode: `Spark:Mail:Development:RedirectTo` or pickup folder. Tier-1 (local Postfix pipe) and tier-2 (MX + inbound 25) are documented deployment recipes; the framework doesn't care which. |
| D10 | **CodeCoverage moves onto MailManager in this PR** (production dogfooding). **Pre-merge ops step:** set `Spark:Mail:*` and `Spark:DataProtection:Storage=RavenDb` on the VPS before merging (merge auto-redeploys). Update CodeCoverage's three hand-written lists (Dockerfile COPY, deploy path filter, `implicitDependencies`). |
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
