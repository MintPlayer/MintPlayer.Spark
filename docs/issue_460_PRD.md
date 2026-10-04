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
| D8 | **GDPR.** `DELETE /spark/auth/manage/account` (re-authentication required) → all `ISparkAccountDeletionHandler<TUser>` (multi-registered) → `UserStore.DeleteAsync` last (releases email/passkey reservations; a handler failure leaves the account intact and retryable). `GET /spark/auth/manage/personal-data` → account JSON + all `ISparkPersonalDataContributor<TUser>`. Audit fields hold the **user id only**, resolved to a name at read time ("deleted user" afterwards). **Revisions are not rewritten** — stated in the guide; content-level personal data is the app's handler's job. Moderation ships a deletion handler. **Extended in M14 (§4.1 "Deviations and findings (M13)", *Account-deletion mail*):** `ISparkAccountDeletedHandler<TUser>` (`OnAccountDeletedAsync`) runs only after `UserManager.DeleteAsync` succeeded; work that must not happen for a refused or failed deletion (a goodbye mail) belongs there, not in `ISparkAccountDeletionHandler<TUser>`. |
| D9 | **MailManager is building blocks, configured through `IConfiguration`.** Pluggable `ISparkMailTransport`: SMTP (MailKit) when `Spark:Mail:Smtp:Host` is set (Security `None`/`StartTls`/`SslOnConnect`/`Auto` — no forced STARTTLS); `.eml` pickup folder when `Spark:Mail:PickupFolder` is set; custom transport replaces both; none → startup error. Bounce handling opt-in: `Spark:Mail:Bounces:VerpDomain` enables VERP; `Spark:Mail:Bounces:Endpoint:Enabled` + secret maps `POST /spark/mail/bounces`; DSN parser pluggable, **the secret check stays in the framework, before the parser**. Suppression list always present (checked at publish and send; fed by bounces, unsubscribes, public `ISparkMailSuppressions`). One-click `List-Unsubscribe` (RFC 8058) per message type (default on for bulk). Dev mode: `Spark:Mail:Development:RedirectTo` or pickup folder. Tier-1 (local Postfix pipe) and tier-2 (MX + inbound 25) are documented deployment recipes; the framework doesn't care which. **Superseded in part by the M10 owner decisions (§3.10):** transports are registered explicitly (`UseSmtpTransport`, `UseMailpitTransport`, `UsePickupFolderTransport`, `AddMailTransport<T>`), the config fallback remains; two transports are a startup error; `ISparkMailTransport.DeliversToRealRecipients` decides the dev-mode rule — Development fails closed without `RedirectTo` for a real-recipient transport, Production refuses `RedirectTo`; Mailpit AutoStart. |
| D10 | **CodeCoverage moves onto MailManager in this PR** (production dogfooding). **Pre-merge ops step:** set `Spark:Mail:*` on the VPS before merging (Data Protection needs no VPS step: `docker-compose.yml` carries the key-ring volume and `KeysPath`, see D5) (merge auto-redeploys). Update CodeCoverage's three hand-written lists (Dockerfile COPY, deploy path filter, `implicitDependencies`). **Superseded (M8/M14, plan §"Pre-merge checklist"):** the VPS needs nothing — `docker-compose.yml` maps the existing `.env` variables (`MAIL_HOST`, `MAIL_PORT`, `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME`) onto `Spark__Mail__*`, sets `Spark__Mail__Smtp__Security=None` and `Spark__Auth__PublicBaseUrl`, and the deploy pulls that file from master. |
| D11 | **New `apps/QnA` demo** proves Moderation. A generic E2E base host is extracted from `FleetTestHost` **as a pure refactor, Fleet's suite green before any QnA test**. QnA joins the model-sync and posture CI loops; name checked against `.gitignore`. |
| D12 | **Earned privileges are bounded.** `moderation.json` references security.json groups **by id**; validated at startup (exists, not well-known, holds only earnable rights). `Lock`, `Suspend`, `Purge`, `Restore`, `Revert`, `ViewDeleted` are **never earnable**; explicit `earnable` escape hatch in moderation.json, also validated against the destructive list. Core: `AddGroupMembershipProvider<T>()` **composes** (merges results; `Use…` keeps replace semantics), providers may return group ids, resolution cached per request in the evaluator. |
| D13 | **`IDisablable` + `OnDisableActionsAsync`** — the single source of truth for disabled actions (deliberately deviates from Vidyano). `IDisablable.DisableActions(params string[])` is the *only* member that knows the method; implemented by `PersistentObject` and the query result. Empty virtual `OnDisableActionsAsync(IDisablable target, DisableActionsContext ctx)` on the actions class. The framework calls it **at page load** (result → returned `DisabledActions`) **and at submit** (parent, query, each selected row — union). Covers **every action, built-in (Edit/Save/Delete/New) and custom**. Refused with **403 + action name, only after the row gate passed** (a hidden row stays 404 — #453 lesson). Old entry points **deleted**: `PersistentObject.DisableActions`, `ClientAccessor.DisableActionsOn`/`DisableQueryActions`/`DisableActions`, `IClientAccessor.DisableActionsForSession` (`IClientAccessor.cs:74`), `CustomQueryArgs.DisableActions`, `SparkQueryContext.DisableActions` (`Queries/SparkQueryContext.cs:58`). CodeCoverage `RepositoryActions` migrates. Batched form for multi-row submits. Documented rule: the hook depends only on entity, user and stored state. **Superseded in part (§4.1 "Deviations (M3)", *Where `IDisablable` lives*):** `IDisablable` is implemented by `PersistentObject` and by per-request framework collectors, not by `SparkQuery` (a shared singleton) or `QueryResult`. |
| D14 | **All 10 vote-fraud measures in v1** (§3.12). `moderation.json` is an `IConfiguration` source bound to `Spark:Moderation` — every threshold overridable via appsettings/env vars/user secrets; group-id and earnable validation runs **after** layering. |
| D15 | **Forwarded headers configured by Spark.** Default trust: loopback + private ranges (10/8, 172.16/12, 192.168/16, fc00::/7); override `Spark:ForwardedHeaders:KnownNetworks`/`KnownProxies`; `ForwardLimit` = `Spark:ForwardedHeaders:ProxyHops` (default 1, **never null**); `X-Forwarded-Proto` same trust; **`X-Forwarded-Host` not forwarded by default** — opt-in `Spark:ForwardedHeaders:ForwardHost=true`, which is refused at startup while `AllowedHosts` is `*` (every app's appsettings has `"*"` today, so checking against it would do nothing). Outside Development, a trust-everyone configuration is a startup error. Effective trust list logged at startup. Hand-written blocks in CodeCoverage (`Program.cs:35-70`, plus the comment at `:371-374`), DemoApp, HR and Fleet deleted (MintPlayer, a separate repo, removes its own). Deployment guide: entry proxy should *overwrite* XFF (nginx `$remote_addr`, Traefik `trustedIPs`, never `insecure`). **Pre-merge: verify what fronts coverage.mintplayer.com and MintPlayer** (a CDN needs its ranges added, or the whole site shares one rate-limit bucket). |
| D16 | **ng-spark-auth `withAccount()`**: confirm-email, change/set-password, profile, 2FA enrollment + recovery regeneration, connected logins, passkeys, personal data/delete — each also a standalone component. Profile accepts app-contributed fields (`SPARK_ACCOUNT_PROFILE_FIELDS`), validated server-side via `ISparkProfileContributor<TUser>`. Email change goes through confirmation. QR code server-rendered SVG. Test enumerates every `/manage/*` route for `SparkLocalCredentials` classification + antiforgery stamp. |
| D17 | ⚠️ **Superseded in part by #467** (`issue_467_query_selection_PRD.md`): `single` is gone (D10, modes are `auto \| none \| multiple`), `auto` counts the built-in Edit and Delete like any action whose rule accepts a row (R1), and a row click opens the row while only the checkbox cell selects (D9). The rest stands. **Selection is opt-in per query (M15, owner 2026-09-29).** `selectionMode: auto \| none \| single \| multiple` on the query definition (`SparkQuery.SelectionMode`, omitted = `auto`), overridable per sub-query entry in the parent type's `Queries`. `auto` is today's behaviour exactly — derived from the **custom** actions offered; the default Delete does not widen it (selection is opt-in, so no existing grid changes its click behaviour), and without selection Delete is reachable per row through the `⋮` menu. A `Queries` entry is either the bare alias (unchanged, and what model sync writes back when the entry has no override) or `{ "query", "selectionMode", "parentReference" }`; `EntityTypeDefinition.Queries` is `SparkSubQuery[]` (implicit from `string`). Presentation only: every row-taking action still enforces its own rule server-side. |
| D18 | **New and Delete are catalogue entries with rules (M15, owner 2026-09-29).** Built-in defaults: `New` (no rule) and `Delete` (`>0`, danger), both `showedOn: both`; an app overrides `showedOn`, `selectionRule`, label, icon, offset, variant and confirmation by adding an entry of the same name to `customActions.json` (no C# class; an `ICustomAction` class with either name is never executed — `/actions/execute` answers 404 for both names; an override entry may omit `displayName`, a custom action may not). `/spark/actions/list` returns them with `isDefault: true` when the caller holds `New/T` or `Delete/T`. **Bulk Delete:** `POST /spark/po/delete-many { objectTypeId, ids, queryId?, parentId?, parentType? }` runs every row through the normal delete pipeline — Delete right, collection guard, row gate, `OnDisableActionsAsync` (query target with the parent, plus every row; union; 403 after the row gate), `OnBeforeDeleteAsync`, interceptors (SoftDelete turns it into a soft delete, History keeps revisions) — with the writes deferred to **one `SaveChanges`**: all rows or none. The rule and the 200-row cap are enforced exactly as for custom actions (400). Offered on top-level lists and sub-queries alike. Bulk **Purge** is not offered: a purge deletes revisions with an admin operation that cannot join the transaction. |
| D19 | **New from a sub-query goes through the server hook (M15, owner 2026-09-29).** The hook already existed — `OnNewAsync(SparkNewArgs<T>)` behind `POST /spark/po/new` — so it is extended rather than duplicated: `SparkNewArgs` gains `ParentType`, `Query` and `ParentReference`, set when New is invoked from a sub-query (`/po/new` with `parentId`, `parentType`, `queryId` and no `asDetailAttribute`; the parent is loaded through the gated read, the query must be declared as a sub-query of the parent's type and produce the constructed type, else refused like a missing row). The ng-spark create page now always asks `/po/new` for its blank object, and the sub-query card carries `parentId`/`parentType`/`queryId` to the create page as query parameters, so the parent survives navigation. **Owner refinement (2026-09-29): the base `DefaultPersistentObjectActions<T>.OnNewAsync` auto-fills the parent reference** through the reusable `args.FillParentReference()`: the reference attribute of the new object whose target is the parent's type; exactly one → set to the parent's id, zero or several → nothing, logged at Debug. An explicit `parentReference` on the query or the sub-query entry names the attribute when there are several, and a name that does not exist or does not reference the parent's type fails at startup. D1 caveat, intended: an override that does not call base loses the auto-fill (the hook then owns initialisation) and may call `args.FillParentReference()` itself. The auto-fill applies to a sub-query New only, never to an `AsDetail` row (whose parent owns the save). |
| D20 | **Messaging priority lanes (M16, owner 2026-09-29).** Fixes the M4-measured 5.8 s transactional max behind a bulk backlog. `SparkQueueOptions.Priority` (`Low`/`Normal`/`High`; `Spark:Messaging:Queues:{name}:Priority`, config beats code, D14). The single feeder reads **look-ahead windows** (`SparkMessagingOptions.FeederBatchSize`, default 256) and serves each window highest priority first, one load and one write per priority, deferring a throttled message **before** claiming it; FIFO between windows and within a queue. **No starvation by construction**: a window is served completely before the next is fetched (a message is overtaken by at most `FeederBatchSize − 1`). The existing single subscription is kept (same query, no `now()`, never deleted); a subscription per priority is rejected (Community's 3-slot cap, decision register A15). MailManager declares `mail-transactional` High and `mail-bulk` Low, so apps get it with no code. |
| D21 | **ARF complaint parsing (M16, owner 2026-09-29).** RFC 5965 feedback reports go to the existing bounce endpoint (same secret, rate limit, 503-when-disabled) and the default parser: delivery from `Original-Mail-From` (VERP) → original `Return-Path` → original `Message-ID` → envelope recipient; `abuse`/`fraud` suppress the **delivery record's** address for every stream with `SparkMailSuppressionReason.Complaint` and mark the delivery `Complained`; `not-spam` and other types are recorded only. Unmatched → logged, 204 (dropped, not retried); malformed → 400 (dropped). Feedback-loop address on the VERP domain, same pipe (guide §8.6). |
| D22 | **Configurable revision limits (M16, owner 2026-09-29).** `SparkHistoryOptions.Revisions` (default) and `Types[{type}]` (with `Enabled`), bound from `Spark:History:Revisions` / `Spark:History:Types:{type}`, config beats code. Per setting: type options → model block → default; `0` = no limit. **Default: 30 days, no count limit, no purge on delete** (fits Community; bounds personal data in revisions, risk 9). `RevisionsConfigurator` keeps H4 (read, merge, send only on change); when `/license/status` says `Community`, the merged limits are checked against 2 revisions / 45 days **before** sending, and startup refuses naming type, setting and source, with `Spark:History:ConfigureRevisions=false` as the escape hatch. |
| D23 | **Hybrid sorted-query feeder (M16b, owner 2026-09-29; supersedes D20's window).** Strict ordering: **sort by priority descending, then enqueue order ascending, and only then take a page.** The one leader-elected `SparkMessaging` subscription stays (A6: the wake-up signal and the exclusivity/claim safety; query unchanged, no `now()`, never deleted, no new subscription). On each batch the leader's feeder reads the top `FeederBatchSize` (page size, default 256) of the static index `SparkMessages_ByPriority` — claimable-now messages only, `order by Priority desc, Sequence asc` — merges the batch's own items (a message not indexed yet is served from the batch, never skipped), and claims highest priority first with the existing durable per-message claim under optimistic concurrency; throttled messages are deferred before the claim as in M4/M16 and leave "pending now" until their slot. **`Sequence` is server-assigned** (spike S-M7): `@last-modified` of the write that made the message claimable, captured once into `SparkMessage.QueuedAtUtc` on the feeder's first write; `SparkMessage.Priority` is stamped at publish. **Starvation:** strict priority cannot starve `Low` — every delivered message is served in its batch, so a `Low` message waits at most for its FIFO position plus one page per batch — so no aging is added (decision register A16). `SubscriptionPerQueue` mode is unaffected. |

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
- **Revision limits from configuration (D22, M16):** `Spark:History:Revisions` (default 30 days) and `Spark:History:Types:{type}` over the model block, config beats code; Community pre-check before sending.
- `IAuditable { CreatedBy, CreatedAt, ModifiedBy, ModifiedAt }` stamped by an interceptor from `ISparkCurrentUser` — **ids only** (D8). `CreatedBy` immutable after create (reputation theft otherwise).
- Service `ISparkHistory { ListAsync, GetAsync(cv), RevertAsync(cv) }` + endpoints (T1). Reads gated on the **current** document's row check + `History/T`, and run through `RedactAsync` (field-level redaction must hold on old revisions). Revert: load revision, verify id + collection, `IEntityMapper.ToPersistentObject`, save through `IDatabaseAccess.SavePersistentObjectAsync` with the current etag (Edit right, row gate, WITH CHECK, protected attributes, stamping, lock interceptor with operation = `Revert`); requires `Revert/T`. Only model attributes revert; soft-delete and audit fields excluded; AsDetail row-identity caveat documented.
- `ISparkRevisionObserver.OnRevisionCreatedAsync({EntityType, Id, ChangeVector, PreviousChangeVector, UserId, Kind, ChangedAttributes})` — in-process, multi-registered; fires only for writes through the Spark pipeline. History does not depend on Messaging.
- ng-spark `/history`: `SPARK_DETAIL_PANELS` multi-provider token (new in ng-spark core, since `sparkRoutes()` passes no templates) + `<spark-po-history>`: revision list, read-only view, field diff vs current, Revert.

### 3.4 Sign in by email (item 4) — D4

Specified in full by D4 (resolver, `@` user-name rule, shared `/connect/login`). Implemented in M5:
the measurement is §4.1 SP-A, the author-facing text the Authorization README ("Sign-in identifier (#460 D4)").

### 3.5 Secrets at rest (item 5) — D5

Specified by D5 (Data Protection in core, `sdp1:` values, backfill; production uses `KeysPath`).
Measured in §4.1 (M1 "SP-B (key documents)", M5 "SP-B (rest)"); operator guide `guide-data-protection.md`.

### 3.6 Account flows (item 6) — D6, D7, D8, D16

Server endpoints under `/spark/auth/manage/`: `password` (set, for social-only accounts), `profile` (GET/POST, via `ISparkProfileContributor<TUser>`), `2fa/authenticator-uri` (+ SVG QR), `personal-data` (GET), `account` (DELETE, re-auth), confirm-new-email. `ISparkAuthLinkBuilder` (SPA base URL + route paths) so confirmation/reset mails link to SPA pages, not the server's plain-text `confirmEmail`. `SparkUser` gains `CreatedAtUtc` + `RegistrationMethod` (stamped in `UserStore.CreateAsync`; backfill from Raven `@created`).

### 3.7 Timezone for SSR (item 7)

- `Spark:TimeZone:CookieName` (default `spark-timezone`; null disables). Precedence: valid header → valid cookie → UTC; an invalid header falls through to the cookie. Both validated before `FindSystemTimeZoneById`: ≤64 chars, `^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$` (`[GeneratedRegex]`). Never echoed, never written by the server. Log names the source.
- ng-spark `withSparkTimezone({ cookieName?: string | false })`: sets the cookie in the browser only, only when changed (`Path=/; Max-Age=31536000; SameSite=Lax; Secure` on https); **the interceptor must not send the header on the server platform** (it would send Node's UTC and override the cookie). First visit renders in UTC (documented). Update `docs/guide-dates-and-sorting.md`.

### 3.8 DisableActions enforced (item 8) — D13

Implemented in M3 as specified in D13; the author-facing contract is in `guide-custom-actions.md`
("Disabling actions"), the measurements in §4.1 (S6, S-MOD-F) and the deviations there.

### 3.9 Pinned npm (item 9) — T9

Specified by T9: `SparkAuthEnsureNpmPackage` is deleted, and a missing `@mintplayer/ng-spark-auth`
in `$(SpaRoot)package.json` is warning `SPARK030` (`docs/diagnostics.md`; M5 deviation *T9* in §4.1).

### 3.10 MailManager (item 10) — `libs/mail/MintPlayer.Spark.MailManager` — D9, D10, T4, T8

- Implements `IEmailSender<TUser>` (**`Replace`**, since `AddIdentityApiEndpoints` TryAdds the no-op) and `ISparkLinkConfirmationSender<TUser>`; they only **publish** `SparkMail { Template, Language, To, Data, DeliveryId }` — never send inline.
- Lanes: `SparkMail` → `mail-transactional`, `SparkBulkMail` → `mail-bulk` (or `BroadcastOptions.Queue`). Mail lanes get a longer backoff (a relay outage > 13 min must not dead-letter all mail) and per-publish `ExpiresAtUtc` (a reset mail dead-letters as Expired before its token expires).
- Templates: embedded resources of the consuming app, **`{lang}/{name}.mjml` layout** (avoids MSBuild `AssignCulture` moving `name.en.mjml` into satellite assemblies — verify in S-M1). Scriban strict variables; **HTML-escape string values** before MJML; convert `JObject` data to `ScriptObject`. Mjml.Net renders. All templates parsed at startup; a test helper (in the MailManager package, not Spark.Testing) renders every template × language with sample data.
- Failure classification: synchronous SMTP 5xx / invalid address → `NonRetryableException` **logged loudly** (a 553 from `ALLOWED_SENDER_DOMAINS` would otherwise dead-letter everything silently); 4xx / connection errors → retry.
- VERP `bounces+{deliveryId}@{VerpDomain}` with compact `DeliveryId` (UUIDv5 of campaign + email for fan-out); `SparkMailDeliveries/{id}` record, 30-day retention; `Message-ID: <{deliveryId}@domain>` (at-least-once duplicates identifiable — documented, not "fixed").
- Bounce endpoint: raw DSN body, shared bearer secret compared in constant time, **stated** `DisableAntiforgery()` exemption, rate limited; MimeKit DSN parse; failed + 5.x.x → suppress.
- **ARF complaints (D21, M16):** the same endpoint parses RFC 5965 feedback reports; `abuse`/`fraud` → `Complaint` suppression of the delivery's address; `not-spam` recorded only; unmatched 204, malformed 400.
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
- **Owner decision (2026-09-28, during M10): Mailpit AutoStart.** `Spark:Mail:Mailpit:AutoStart`
  (default `false`) with `Mode` = `Binary` (default: `mailpit` from `PATH` or `ExecutablePath`, `--smtp`
  and `--listen` from the configured SMTP port and `UiPort`, default 8025; never downloaded) or `Docker`
  (`docker run --rm -d --name spark-mailpit -p <smtp>:1025 -p <ui>:8025 axllent/mailpit:v1.31.3` by default, `ImageTag` configurable;
  an existing container of that name is reused and started). Only in Development (elsewhere: ignored,
  one Information line). A listener already on the SMTP port is reused and nothing starts. A hosted
  service that never blocks startup; logs the UI URL; on shutdown stops only what this process started
  (the process tree, or `docker stop` of a container it started). A missing binary or Docker gives one
  warning (install hints: `winget install axllent.mailpit`, `scoop install mailpit`, the GitHub release)
  and the app continues. `ProcessStartInfo` with an argument list, never a shell string.
  Process lifetime: on Windows the started binary is assigned to a Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, its handle held for the host's lifetime, so Mailpit dies with a
  crashed or debugger-stopped host (assignment failure → one warning, graceful stop only). Linux/macOS
  have no reliable parent-death hook from `Process.Start` and no watchdog is added: graceful stop on
  shutdown, and a leftover from a crash is adopted by the next run through the port-in-use reuse. Docker
  containers are labelled `spark.mailpit.owner=<ApplicationName>`; only a labelled container this
  process created or started is stopped.

### 3.11 Queue throttling (item 11) — T8

- `SparkQueueOptions { MaxPerInterval + Interval, BatchSize + MinDelayBetweenBatches, MaxConcurrency, MaxAttempts, Backoff }` per queue name, in code or `Spark:Messaging:Queues:{name}`.
- Admission at the top of `MessageProcessor.RunHandlersAsync` (shared by both modes). **Never wait inside a lane** (bounded channel → a sleeping lane blocks the one shared feeder and stalls every queue; queued claims aren't renewed). Over budget → one optimistic write: Pending, owner/claim null, `WakeUp=false`, `NextAttemptAtUtc = reserved slot`, `AttemptCount--` (no retries burned). `MessageClaims.ReleaseUnstartedAsync` (`:124`) is **not reusable as-is** (it sets `WakeUp=true`, takes no `NextAttemptAtUtc`, opens its own session without optimistic concurrency) → add a variant that runs in the caller's session with a target time and `WakeUp=false`.
- **GCRA-style reserved slots** per queue (in-memory `ConcurrentDictionary`; on restart a message is deferred once more) — each throttled message deferred once, not O(n²). Honour the later of backoff and slot; slot after `ExpiresAtUtc` → dead-letter as Expired. **Persistence:** `SparkMessage` gains `ExpiresAtUtc` and `DeadLetterReason` (`MaxAttempts` / `NonRetryable` / `Expired`); status stays `DeadLettered` (`EMessageStatus` is Pending/Processing/Completed/Failed/DeadLettered — no new value).
- A batch is a pacing window only; each message keeps its own status and retry.
- `MaxConcurrency > 1` only in `SingleSubscription` mode (N pumps per lane); warning in per-queue mode. In-process bucket suffices — no compare-exchange bucket — because the leader lease gates the feeder **and** the per-queue workers in both modes (`MessageSubscriptionManager.cs:60-112`) (one lease owner owns all queues).
- Accuracy documented: long-run rate exact, bursts quantised by `FallbackPollInterval` (30 s).
- Optional `IMessageProgress`: sidecar `SparkMessages/{id}/progress/{handlerIndex}` in its own collection, appended by patch, **same `@expires` as its message**; `IsDoneAsync`/`MarkDoneAsync`. Not an array on `SparkMessage`.
- Pattern documented: `mail-transactional` generous, `mail-bulk` strict.
- **Priority lanes (D20, M16):** `SparkQueueOptions.Priority`, `FeederBatchSize`; the feeder serves each look-ahead window highest priority first and defers throttled messages before the claim; no starvation (window bound). Measured in §4.1 (M16). **Superseded in M16b (D23):**
- **Sorted-query feeder (D23, M16b):** static index `SparkMessages_ByPriority` (claimable-now only; `Priority`, `Sequence` stored); per wake-up one page `order by Priority desc, Sequence asc` of `FeederBatchSize`, merged with the subscription batch, claimed a priority at a time (one load + one write each), stale entries re-checked on load against the subscription's predicate; `SparkMessage.Priority` stamped at publish, `SparkMessage.QueuedAtUtc` captured from `@last-modified` on the feeder's first write (no extra write). A missing index degrades to the batch alone with one warning. No starvation by the subscription backstop; no aging. Measured in §4.1 (M16b: S-M7, S-M3, S-M8, S-M9).

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

Specified in full by D15 (`AddSparkForwardedHeaders`, private-range default, `ProxyHops`, opt-in
`ForwardHost`, startup refusals). Measured in §4.1 M1 "S-FH1"; operator guide and proxy recipes in
`guide-docker-deployment.md` ("Forwarded headers").

### 3.14 Housekeeping

- Close #283 (regression test with a policy), #285, #299, #432.
- Reply on #460: SocketExtensions 11.x is already `11.0.1-preview.86`.
- Fix the stale line in `docs/decisions_messaging_and_project_automation.md`.
- Bump every touched lib's csproj version and both npm packages (the version gate requires a `+<Version>` line per changed project).
- Update `tools/verify-coverage-paths.mjs` only if a test project is added (none planned).

---

### 3.15 Sub-query selection & actions, Vidyano parity (M15) — D17, D18, D19

The reference is Vidyano, measured in a real app. An action is defined once per type, and every
query of that type offers it, whether top-level or sub-query. Each sub-query tab has its own toolbar,
with pinned actions and an overflow. Rows have checkboxes and a per-row `⋮` menu, and a
"N selected ⊗" chip clears the selection (the demo measured has `selectAll.isAvailable = false`: no
select-all). Actions enable and disable live from the
selection count.

**Server**
- `SparkQuery.SelectionMode` / `ParentReference`; `EntityTypeDefinition.Queries: SparkSubQuery[]`,
  where a bare string or an object is accepted and a bare string is written back when there is no
  override. Model sync is a fixed point for both shapes.
- Startup validation: an explicit `parentReference` must name a `Reference` attribute of the query's
  row type whose `referenceType` is the parent's CLR type. `--spark-verify-model` reports the same
  problem.
- Default `New`/`Delete` catalogue entries, overridable in `customActions.json`, listed with
  `isDefault: true` when the caller holds the right.
- `POST /spark/po/delete-many`. It enforces the rule, the cap, the right, the gated parent, the
  collection guard, the row gate, `OnDisableActionsAsync` (query plus rows, union) and the
  interceptors, then commits with one `SaveChanges`, all or nothing.
  - The base `OnDeleteAsync` defers its `SaveChanges` while a batch is open.
  - An override that saves on its own breaks atomicity. That is documented as a D1 gap.
- `/spark/po/new` sub-query context (`parentId`, `parentType`, `queryId`) and `SparkNewArgs`
  `ParentType`/`Query`/`ParentReference`/`FillParentReference()`. The base `OnNewAsync` auto-fills.
  `NewInvoker` invokes the base hook when the context is a sub-query.
- `SparkClient.DeletePersistentObjectsAsync`, and `NewPersistentObjectFromSubQueryAsync`.
- **`parentDeleted` (17f35fd3).** `/spark/queries/execute` and `/spark/queries/distinct-values`
  take an optional `parentDeleted` (`exclude | include | only`), the mode the *parent* is resolved
  under, scoped to the parent's type. Core only carries it to the row policies (`SubQueryParent`);
  the SoftDelete policy honours a widening only for holders of `ViewDeleted/{ParentType}`, as on
  `/spark/po/load`. Everyone else, and every request without the field, gets the same 404 a missing
  parent gives, byte-identical (#453). The rows' own `deleted` is independent. `/spark/actions/execute`
  and `/spark/po/delete-many` keep resolving their parent as a live row, so a smuggled
  `parentDeleted` widens nothing. `SparkClient.ExecuteQueryAsync` and `GetDistinctValuesAsync` take
  it as their last optional parameter.
- **Actions on deleted rows (1632dfe4).** `/spark/po/delete-many` and `/spark/actions/execute` judge
  live rows only, so a hand-made request aimed at a deleted row is the same 404 as a hidden row, even
  with a `deleted` field in the body. A SoftDelete test pins that, with a live control.

**ng-spark**
- `SparkSelectionModeSetting = 'auto' | SparkSelectionMode` (the rendered `SparkSelectionMode` stays `none|single|multiple`, which is what `<bs-datatable>` binds). `selectionModeFor(actions, declared)` returns the declared
  mode unless it is `'auto'`. `filterQueryActions` compares `showedOn` case-insensitively.
- The grid owns the shared toolbar model, `toolbarActions()`: New, Delete and custom actions, with
  enablement read live from the selection. It also owns:
  - the selection bar: the "N selected" chip. No select-all, and the datatable's header checkbox
    is the deselect-all (see the owner decision below);
  - the per-row `⋮` menu, which lists the actions whose rule accepts exactly one row. A menu item
    runs on that row only and leaves the checkbox selection alone.
  - bulk Delete, which asks for confirmation.
  - New, which navigates to the create page and carries the parent.
- The card renders the caption on the left and the actions on the right, through the priority nav,
  whose overflow label is the translated `common.more`, as on the list and detail pages (40e07b86).
  The header buttons are square (`rounded-0`), since they sit edge to edge (cbf1045e). The
  query-list page renders the same `toolbarActions()`. Neither forks the grid.
- **Recycle-bin toolbar rule (1632dfe4).** While the grid lists `deleted: 'only'`, or sits under a
  deleted parent (the card's `parentDeleted` input, set by the detail page for a row opened with
  `?deleted=only`), `toolbarActions()` and `rowActions()` are empty and `offersCreate()` is false:
  New, Delete and custom actions are hidden and the recycle bin offers only Restore and Purge. The
  card forwards a `deleted` input to its grid. Under a deleted parent the grid sends
  `parentDeleted: 'include'` on execute and distinct-values (6a2a6a46); the rows' `deleted` is left
  alone.
- **Found in the browser check:** opening a second row's `⋮` menu while one was open closed it at
  once, because both overlays called an unscoped `closeRowMenu()`; it is now scoped to its row
  (726ba3f9). A selected row's links and menu toggle rendered primary on primary; a scoped override
  in `spark-query-grid.component.scss` hands the datatable's `--mp-datatable-row-selected-color`
  down to them (75ab0395). The chip's ⊗ in single mode (8b545062) is recorded under D17 below.
- The detail page reads normalised `Queries` entries and passes the entry's `selectionMode` to the
  card.
- The create page asks `/po/new`, and forwards the query parameters `parentId`, `parentType` and
  `queryId`.

**Why the owner saw no actions on sub-queries (root cause, M15 investigation).** No code path hid
them. The configuration had none to show, and New and Delete were never rendered on a sub-query:
- **QnA:** the only sub-query is `question-answers`, which lists Answers. Answer had no custom
  actions; `CloseQuestion` and `ReopenQuestion` are `showedOn: "detail"` on Question.
- **Fleet:** no sub-queries at all. Its query actions, `CarCopy` and `ScatterRegistrationOffsets`,
  sit on the top-level Car lists.
- **DemoApp:** `company-cars` is the only sub-query whose row type has a query action
  (`CopyCarsToCompany`, `both`, `>=1`, granted to everyone). The chain there is intact.
- **HR and CodeCoverage:** no query-level custom actions.
- Every card lacked New and Delete, which the card had never rendered (`offersCreate` was computed
  and unused), so a card looked action-less even for a caller who could create or delete.

Three latent defects in that chain are fixed:
- `showedOn` was compared case-sensitively (`"Both"` rendered nowhere).
- The query-list page ignored the result's `disabledActions`, which the card honoured.
- A query whose entity type did not resolve skipped the action lookup without a word.

**QnA demo**
- `Question.queries` = `{ "query": "question-answers", "selectionMode": "multiple" }`.
- A new `DuplicateAnswer` custom action (`showedOn: "query"`, `=1`).
- Bulk Delete of answers is soft (SoftDelete).
- A New from the card relies on the base auto-fill for `Answer.QuestionId`, with no custom code
  (QnA README).

**Tests**
- .NET:
  - the bulk delete: its rule, all-or-nothing, SoftDelete, the 200 cap, and one refused row refusing
    the lot;
  - `OnNewAsync`: it receives the parent; the auto-fill cases (one candidate, zero, several,
    explicit name, an override without base, an override calling the helper); startup validation of
    `parentReference`;
  - the `selectionMode` overrides and JSON shapes, and the model-sync fixed point;
  - default catalogue entries in the list endpoint.
- vitest: selection modes, the chip, action enablement, the row menu, header order, and the create
  page's parent round-trip.
- E2E (QnA): sub-query checkboxes, bulk soft Delete, and New with the parent.

**Owner decision on D17 (2026-09-29): no select-all, only deselect-all**
- The grid offers **no select-all**, on the sub-query card and the query-list page alike. With
  paged, lazy-loaded or virtual-scrolled rows it is not a meaningful action: it could only select the
  rows loaded so far, and a bulk action would then silently skip the rest. Vidyano's demo has
  `selectAll.isAvailable = false` for the same reason.
- **Deselect-all is `<bs-datatable>`'s own header checkbox.** The datatable already renders it in
  the checkbox column's header, visible only while at least one row is selected, and clicking it
  clears the selection (its selection model flows back through `[(selection)]`). Spark adds no
  control of its own, so the "N selected" chip shows the count only: a ⊗ on it would duplicate the
  header checkbox. **Single selection is the exception:** it has no checkbox column, so no header
  checkbox, and a row click there only ever selects. The chip keeps its ⊗ in `single` mode only,
  as the one way to clear (found in the browser check).
- Removed: the grid's select-all box and its `toggleSelectAll()` / `allPageRowsSelected()` /
  `somePageRowsSelected()`, and the chip's clear button outside `single` mode.

**Owner's addendum to D17 (2026-09-29): the sub-query search box**
- A search box on the right of the `<spark-query-card>` header, after the actions, as in Vidyano.
- It is `<spark-search-box>` (`@mintplayer/ng-spark/grid`), extracted from the query-list page, which
  renders the same component. Both surfaces therefore share the 300 ms debounce, the clear button and
  Escape-to-clear. The query page used to send one query per keystroke.
- It feeds the card's grid `search`, so `/spark/queries/execute` gets `search` with
  `parentId`/`parentType` and searches server-side (#210). The card's `search` input is the starting
  term (`linkedSignal`). `[searchable]="false"` hides the box, and a card over bound `data` never
  shows it.
- **`/spark/queries/distinct-values` did not take the grid's search.** Its `search` narrows the
  listed values (the panel's own box), so a searched grid's filter panel listed values from rows the
  grid did not show. It now takes **`querySearch`**, applied exactly as `/execute` applies `search`:
  pushed down where possible, otherwise narrowed in memory by the same helper
  (`QueryExecutor.NarrowBySearch`). It was added to `SparkService.getDistinctValues`,
  `SparkClient.GetDistinctValuesAsync` and `IQueryExecutor.GetDistinctValuesAsync`.
- **Selection on search: cleared.** A new term empties the selection on both surfaces, in both
  transports. Reconciling it with the new rows was rejected, because a ticked row on another page of
  the searched result cannot be told apart from one the search excluded, and a bulk Delete would
  then act on rows the user can no longer see. The "N selected" chip therefore always counts rows
  that are ticked on screen, which is also what the soft-deletion mode switch does.
- Tests: vitest for the box (debounce, clear, Escape, external term), for the card (header order,
  term plus parent sent to the grid, opt-out, bound data), and for the grid (search with the parent,
  the selection cleared, `querySearch` sent to the value list). .NET tests cover `querySearch` on the
  endpoint. The QnA E2E step checks that searching the Answers card narrows its rows and clears the
  selection.

**Row `⋮` menu: CDK overlay, not `@mintplayer/ng-bootstrap/dropdown`.** ng-bootstrap 22.19.0's
dropdown fesm declares `BsDropdownToggleDirective`, whose factory lists `BsDropdownDirective` as an
eager dependency, before `BsDropdownDirective`. An app build links it and never notices. Every
unlinked JIT test run that imported the grid (7 ng-spark suites and CodeCoverage's detail page spec
in CI run `a1b47012`) failed at import with "Cannot access 'BsDropdownDirective' before
initialization". The menu now uses `cdkConnectedOverlay` directly, with ng-bootstrap's
`<bs-dropdown-menu>`/`bsDropdownItem` inside. The upstream fix is for the toggle to
`inject(forwardRef(() => BsDropdownDirective))`, as `BsDropdownMenuDirective` already does; it is
filed as [ng-bootstrap issue #419](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/419),
and the menu can move back to the dropdown once that ships.

**Spec type-check (59070c6f, b911f804).** vitest strips TypeScript without checking it and
ng-packagr excludes the specs, so spec type errors were reported nowhere (ten in ng-spark, one a
fixture silently `undefined`). They are fixed, and `pull-request.yml` now runs `tsc --noEmit` on
both packages' `tsconfig.spec.json`.

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
| S-M7 | Where the feeder's FIFO key `Sequence` comes from: `@last-modified`, a compare-exchange / document counter, the first-save etag written back, a server identity — cost per enqueue and inversions against commit order under concurrent producers; sort-index lag under a 1,000-message bulk load. | D23 |
| S-M8 | Leader handover under load (crash and graceful), two hosts on one database, 1,000 mixed-priority backlog: no loss, duplicates, takeover gap, sorted order under the new leader. | D23 |
| S-M9 | The feeder and the S-M3 load under the **Community** licence: one subscription, the sort index deploys and serves. | D23 |
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
| S-PKG1 | nuget.org `MintPlayer.*` prefix reservation + CI API key scope cover the 7 new package ids (**owner checks in the nuget.org account**). | publish |
| S-PKG2 | `dotnet pack` on the branch includes the new libs without `IsPackable` tweaks. | publish |

### 4.1 Spike results (measured)

Measured facts only. Each spike is kept as a test so the answer stays pinned.

#### M1 — forwarded headers, group-provider composition, key documents

Written after the fact from the M1 tests and a re-run on 2026-09-28
(`dotnet test tests/MintPlayer.Spark.Tests --filter "FullyQualifiedName~SparkForwardedHeadersTests|FullyQualifiedName~SparkDataProtectionTests|FullyQualifiedName~MintPlayer.Spark.Tests.Authorization"`:
**470/470 passed**, 52 s; 13 forwarded-headers cases, 7 Data Protection cases, 450 in the
`Tests.Authorization` namespace).

**S-FH1 — `ForwardedHeadersMiddleware` with Spark defaults (M1, 2026-09-28).**
*Question:* with Spark's defaults, is a spoofed leftmost `X-Forwarded-For` ignored, does an untrusted
sender keep its socket address, does `ProxyHops=2` read exactly two entries, and which configurations
refuse to start?
*Method:* `SparkForwardedHeadersTests` (13 cases): the pipeline assembled from the registered
`IStartupFilter`s exactly as the host does, after `AddSparkForwardedHeaders()`, invoked with a
hand-built `HttpContext` whose `RemoteIpAddress` is chosen per case (a test server cannot choose the
transport peer). Trusted peer `172.18.0.2` (Docker bridge, inside the private-range default), public
peer `198.51.100.9`.
*Answer:* from the trusted peer, `XFF: 6.6.6.6, 203.0.113.7` → `203.0.113.7` (one hop reads only the
entry the proxy appended); from the public peer, `XFF: 6.6.6.6` + `X-Forwarded-Proto: https` → the
socket address and scheme `http` are kept; with `ProxyHops=2`, `6.6.6.6, 203.0.113.7, 10.0.0.5` →
`203.0.113.7`. `X-Forwarded-Proto` is honoured from a trusted peer; `X-Forwarded-Host: evil.example`
is not applied by default. `KnownProxies:0=203.0.113.1` **replaces** the private-range default (the
Docker peer is then untrusted). Startup refusals, all `InvalidOperationException`: clearing
`KnownNetworks` + `KnownProxies` outside Development ("trusts every sender"; in Development it runs
and forwards `6.6.6.6`), `ProxyHops=0`, `ForwardLimit = null`, and `ForwardHost=true` while
`AllowedHosts` is unset or `*`. With `ForwardHost=true` and `AllowedHosts=coverage.example;…`, an
allowed forwarded host is applied and `evil.example` is not. Not measured: a real proxy in front
(nginx/Traefik recipes are documentation) and what fronts coverage.mintplayer.com (D15 pre-merge).

**S-MOD-B — composed group providers + per-request cache (M1, 2026-09-28).**
*Question:* does `AddGroupMembershipProvider<T>()` compose with the primary provider while
`Use…` keeps replacing, may providers return ids, is resolution cached per request, and do the
existing authorization tests still pass?
*Method:* `GroupMembershipCompositionTests` (the real registrations `AddSpark` makes);
`SecurityFileAccessControlTests` (composed provider, id provider, reserved ids, call counts);
`SparkAuthorizeAttributeTests` (the `[SparkAuthorize]` group form through a composed provider and a
provider-returned id); the rest of the `Tests.Authorization` namespace unchanged as the regression
net.
*Answer:* `Use…` replaces the primary and `Add…` merges, in either call order; adding the same
provider twice is a no-op; with neither call only the claims provider is asked. A composed provider's
group is added to the primary's (both `Delete/Car` via the primary and `Edit/Car` via the composed
one are allowed). An `IGroupIdMembershipProvider` naming `EditorsId` grants that group's rights and no
other; an id equal to the well-known authenticated group (or an id security.json does not declare)
grants nothing to an anonymous caller. Ten `IsAllowedAsync` calls in one request ask **each provider
exactly once**. All 450 tests in the namespace pass, so answers for existing configurations are
unchanged.

**SP-B (key documents) — CodeCoverage's existing key ring under Spark (M1, 2026-09-28).**
*Question:* does Spark's Data Protection, with `ApplicationName=CodeCoverage` and `Storage=RavenDb`,
decrypt what CodeCoverage's hand-written setup protected, and where do new keys land?
*Method:* `SparkDataProtectionTests` (7 cases) on the embedded RavenDB: a payload protected through
CodeCoverage's old wiring (`SetApplicationName("CodeCoverage")` over its repository, writing
`DataProtectionKeys/{name}` documents through a raw put with production's metadata verbatim —
`@collection: KeyDocuments`, `@Raven-Clr-Type: CodeCoverage.Services.RavenDataProtectionKeyRepository+KeyDocument, CodeCoverage`),
then unprotected by a provider built with `AddSparkDataProtection()`.
*Answer:* it decrypts (`"signed-in"` round-trips). The same ring under `ApplicationName=SomethingElse`
throws `CryptographicException`, so the application name is part of the contract. New keys land under
`DataProtectionKeys/` in the `KeyDocuments` collection. `KeysPath` writes one `key-*.xml` and a second
provider over the same folder (a restart) decrypts. `Validate` refuses an unpersisted ring outside
Development ("signs every user out on every redeploy"), accepts it in Development, and refuses both
`KeysPath` and `Storage=RavenDb`. Production later moved to `KeysPath` (D5 owner decision), so the old
documents are orphaned and users are signed out once; this measurement is what `Storage=RavenDb`
still guarantees. The rest of SP-B is in the M5 entry below.

#### M2 onward

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
  the throttle. **Fixed in M16 (owner decision D20, 2026-09-29):** priority lanes in look-ahead windows; the same S-M3 run now measures a 52 ms max (§4.1, M16).
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
  RavenDB's reason; `Spark:History:ConfigureRevisions=false` opts out. **M16 (D22):** on Community the merged limits are now checked before sending, naming type, setting and source.
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
- *Not built:* VERP tier 2 (MX + inbound 25) is a recipe only (§6 out of scope). Complaint (ARF)
  parsing: **built in M16 (owner decision D21, 2026-09-29)**. (A `PreferredCulture` UI exists:
  `spark-account-profile.component.ts` in `@mintplayer/ng-spark-auth/account`.)

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
- *Where the tokens live:* `SPARK_DETAIL_PANELS`, and the two action slots `SPARK_DETAIL_ACTIONS` and
  `SPARK_QUERY_LIST_ACTIONS`, are in a new entry point, `@mintplayer/ng-spark/panels`, not in the root
  entry point, which exports only the bootstrap API. Every component receives one `context` input
  (`SparkDetailContext` / `SparkQueryListContext`).
- *The recycle bin is a route parameter:* the query page and the detail page read `?deleted=`, and core
  understands it without the soft-delete entry point. The grid sends the value with every page and
  distinct-values request, and row links carry it. A row loaded with `only` hides Edit, Delete and
  custom actions, because those judge live rows and would 404. `SparkService.postEnvelope` was made
  public so the add-on entry points get the envelope and 449-retry handling.
- *Account pages:* `withAccount()` mounts the 7 D16 pages, plus an `account` overview. The six
  signed-in pages share one `@mintplayer/ng-spark-auth/account` entry point, so they load as one lazy
  chunk; `confirm-email` and `passkeys` have their own. The signed-in pages use a new
  `sparkAuthenticatedGuard`, which waits for `/me`. The existing synchronous `sparkAuthGuard` sends a
  signed-in user to sign-in on a hard reload; it is unchanged.
- *The mail-language list* is the app's UI languages (`GET /spark/culture`). A stored culture outside
  that list is kept as an extra option.
- *Deletion re-authentication:* a wrong password and a stale sign-in both answer 403
  `reauthentication_required`, and the page shows one message for both.
- No spikes are named for M10. Nothing in M10 was verified in a browser; that is left to the parent
  session.

**Deviations (M11).**
- *`sparkAuthGuard` fixed (supersedes the M10 note above):* it now waits for the session check when
  the user is not known to be signed in, so a hard reload no longer bounces a signed-in user.
  `sparkAuthenticatedGuard` is kept as an alias of it, and `withAccount()` defaults to
  `sparkAuthGuard`. The vitest hard-reload case failed before the fix and passes after.
- *Base host shape:* `SparkAppTestHost` (abstract) + `SparkAppDescriptor` (project path, SPA root,
  bundle output, extra bundle source roots, database prefix, coverage slug, mail pickup) in
  `tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/`. Hooks: `ConfigureAppSettings` (JSON override),
  `ExtraDatabases`, `SeedAsync`, `RegisterTemporaryFile`, `AdminGroup`. The build gate is global
  across apps, because apps share the library outputs. `FleetTestHost` keeps every public member the
  tests use.
- *Two harness changes that are not pure refactor:* the stale-bundle check also watches
  `libs/node_packages/ng-spark-auth` (consumed from source, previously unwatched), and in pickup mode
  each host writes mail to its own temp folder (`MailPickupFolder`, `PickedUpMails()`), deleted on
  dispose, instead of the shared `apps/Fleet/Fleet/mail-pickup`.
- *M5 regression found by the run:* M5 put `/spark/auth/register` behind antiforgery, but
  `SparkClient.RegisterAsync` and the hosts' seeding still posted without a token, so every Fleet
  E2E test failed in fixture startup (`Register failed (400)`). `RegisterAsync` now warms up and sends
  the token; the hosts seed through it.
- *Fleet E2E result (2026-09-28):* 104/105 passed. The one failure,
  `CrossModuleSyncTests.Etl_deployment_is_accepted_for_a_granted_collection`, is environmental: the
  machine's `RAVENDB_LICENSE` has no ETL feature (`LicenseLimitException`). It needs the Developer
  licence; nothing in the code changed it.
- *Coverage slug:* `tools/verify-coverage-paths.mjs` accepts only `fleet-host-*` reports. A QnA host
  run with `SPARK_E2E_HOST_COVERAGE` needs its slug added there (M13).

**S-MOD-A — map-reduce indexes from the add-on assembly (M12, 2026-09-28).**
*Question:* do hand-written map-reduce indexes (voter→target counts, reciprocal pairs, rep sums)
deploy from an add-on assembly through core's index creation, pass the SPARK018 tooling, and how
fast do they catch up?
*Method:* `ModerationSpikeTests.S_MOD_A_…` (Developer licence): a `SparkEndpointFactory` host that
only declares `AddIndexesFrom(<Moderation assembly>)`; then 8,002 documents bulk-inserted (4,001
`ModerationVote`, 4,001 `ReputationEvent`, 40 voters × 50 authors × 2), `WaitForIndexingAsync`, and
queries against each index. The Moderation project references Spark's analyzer project
(`OutputItemType="Analyzer"`) and was rebuilt with `--no-incremental`.
*Answer:* all four indexes deployed by core's `UseSpark` from the add-on assembly with no index
errors: `Moderation/ReputationCells`, `Moderation/PendingReputation`, `Moderation/VotePairs`
(`MapReduce`) and `Moderation/EntriesToCredit` (`Map`). Spark's catalog accepted them (no
projection, so no default-index conflict with three indexes on one collection). **0 SPARK018 (0
SPARK diagnostics of any id) on the Moderation sources.** The bulk insert took 889 ms; all four
indexes were non-stale **365 ms** after it returned. Results were exact: a voter→author pair summed
to its 2 votes, a withdrawn vote (`Direction 0`) was not mapped, a recipient's credited cells summed
to 40 × 10 from 40 distinct voters and the pending index held the other 40 × 10.
Found on the way (measured, not in the spike's question): RavenDB **refuses
`WaitForNonStaleResults` on a stream** (`NotSupportedException: Since Stream() does not wait for
indexing …`) — Moderation waits with a zero-row query first, then streams; and the LINQ provider
**cannot translate `string.Compare`** on a static index field (`ArgumentException: Could not
understand expression`) — the vote-pair index carries an integer `DayNumber` for the window filter.

**S-MOD-E — vote + ledger entry atomic on the shared session (M12, 2026-09-28).**
*Question:* do a vote, its `ReputationEvent` and the tally commit atomically on the request's
(shared actions) session?
*Method:* `ModerationSpikeTests.S_MOD_E_…`: the scoped `IAsyncDocumentSession` of a Spark host;
(1) store vote (deterministic id, `""` change vector = must not exist) + event + tally, one
`SaveChangesAsync`; (2) load "no vote", let a second session store the same vote id, then save the
first session's vote + event + tally.
*Answer:* (1) **one request** for all three documents. (2) `ConcurrencyException`; the event was
**not** stored and the tally kept its previous value (`Up = 1`) — the batch is one transaction.
Moderation therefore writes each vote as one optimistic transaction (Writes mode) on a **fresh**
session per attempt (a retry cannot reuse the request session after a failed save), retried up to 4
times on a conflict.

**S-MOD-C — how long a suspended user's cookie and bearer token keep working (M12, 2026-09-28).**
*Question:* after the Identity lockout + security-stamp refresh Moderation does on a suspension, how
long does an existing cookie / bearer token still authenticate?
*Method:* `ModerationSuspensionSpikeTests.S_MOD_C_…`: `AccountTestHost` (Identity + Spark's store,
`MapSparkIdentityApi`), a controllable `TimeProvider` on `SecurityStampValidatorOptions`, the cookie
and bearer options; `ValidationInterval = 30 min`, `BearerTokenExpiration = 1 h`. Sign in once with a
cookie and once for a bearer + refresh token, then `IdentityModerationAccounts.LockOutAsync` (lockout
end = max, `UpdateSecurityStampAsync`), then `/whoami` authenticating each scheme at +0, +29, +31, +59
and +61 minutes.
*Answer:* cookie `True, True, False, False, False`; bearer `True, True, True, True, False`. So a
**cookie survives until its next stamp validation (≤ `ValidationInterval`)** and a **bearer access
token until it expires (the stamp is not re-checked on access tokens)**; `/refresh` answered **401**
and a new password sign-in **401** immediately. The window is covered server-side: the suspension
document is read by id on every request, so writes and reputation groups stop on the **next request**
(`A_suspension_blocks_writes_votes_and_privileges_on_the_next_request`).

**S-MOD-D — the lock on every write path (M12, 2026-09-28).**
*Method:* `ModerationToolsTests.S_MOD_D_…`: a post by a non-moderator (who holds Edit, Delete,
Revert, Restore, Purge), locked by a moderator; every path attempted by the author.
*Answer:* PO update → 400 "This post is locked."; AsDetail parent (update adding a `Lines` row) →
400; custom action saving through `IDatabaseAccess` → 400 (was the catch-all **500** before this
milestone; `/spark/actions/execute` now maps `SparkValidationException` to 400); revert → 400; delete
→ 400; after the moderator (exempt) soft-deleted it: restore → 400, purge → 400; vote → 400. The
moderator's restore → 200. The document kept `Title = second`, no lines, not deleted. A module
**Sync** save by the same author **passed** (by design, below). `/spark/po/delete-row` writes nothing
(a consultation), so there is no write to refuse — the removal is the parent save, refused above. A
locked post loads for the author with `disabledActions` containing `Edit` and `Delete`, for the
moderator without.

**Deviations (M12).**
- *Refusal codes.* A suspended account's write is **400** "Your account is suspended." (a
  `SparkValidationException`, like the lock), not 403: 403 is the disabled-action answer and names an
  action. The new-account throttle is **429** through a new core `SparkThrottledException`
  (`Retry-After`, envelope `retryAfterSeconds`), mapped on `/spark/po/create` and
  `/spark/actions/execute`; §3.12 said "a row check on `New`", but a row check can only answer 404, so
  it is a before-save interceptor.
- *The score is not on the entity.* A vote writes `ModerationTallies/{targetId}`; putting the score on
  the post would move its etag and 409 an author editing at the same time. The vote widget (attribute
  renderer `spark-vote`) ignores the attribute value and reads `/spark/moderation/votes` (batched per
  macrotask); a grid column needs `rendererOptions.type` because a query row carries no type.
- *Document ids.* `ModerationLocks/{targetId}` instead of `locks/{targetId}` (a bare `locks/` prefix
  could collide with an application's documents); cases are `ModerationCases/{rule}/{a}/{b}` and
  `ModerationCases/flag/{targetId}`; one compensation per entry is `{entryId}/compensation` (a
  retraction and a reversal share it, so an entry is never compensated twice).
- *Sync passes a lock.* A module sync replicates the owner's decision; refusing it would split the
  replicas. Measured in S-MOD-D, documented.
- *Privileges read a summary document.* The provider reads `ModerationReputation/{userId}` (with the
  profile and the suspension, one lazy batch) instead of querying the map-reduce indexes per request.
  The summary is recomputed from the indexes by the crediting job, by reversals, flag decisions and
  deletions. Consequence: a newly credited entry counts from the job run that credits it (every 5 min
  by default), which the 48 h delay dwarfs.
- *Diversity reading.* "votes" = credited, uncapped, eligible **up-votes received**; when the rule
  fails, the vote-derived part of the reputation (net of compensations) is excluded from the
  privilege reputation and non-vote reputation (flags) still counts. Zero votes pass trivially.
- *Caps.* The votes-cast cap refuses (429 until UTC midnight); the other caps zero the entry's points
  (`ZeroedBy = pair-cap / daily-cap / ineligible-voter`) and the score still moves. The daily
  recipient cap clamps the last entry to the headroom (10 → 5). A retraction does not refund a cap.
- *Delayed crediting* applies to every vote-derived entry, the voter's down-vote cost included; flag
  outcomes are credited at once (a moderator decided them). A compensation is never creditable before
  the entry it cancels, so a vote withdrawn inside the delay nets to zero instead of dipping first.
- *A reversal also neutralises the vote* (direction 0, tally decremented), so the score is corrected
  with the reputation; the voter may vote again (caps and the next detector run apply).
- *Invented defaults* where §3.12 gave none: reciprocal ≥ 5 votes each way, fast voting ≥ 3 votes
  under 60 s, the webmail-domain list, `NewAccounts` 5 posts/day for 7 days. A decided case is never
  reopened by the detector.
- *Registration cluster* uses network observations recorded on the user's activity (once per user,
  day and network): Authorization has no registration hook exposing the address. Hashes only match
  within one key period (30 days by default); the key is Data-Protection-protected in
  `ModerationIpKeys/{period}` and expires with the last observation made under it.
- *Unknown account age fails closed.* `SparkUser.CreatedAtUtc = null` (no revision found by the M5
  backfill) counts as 0 days, so such accounts pass no age gate — see risks for M13 / the MintPlayer
  cutover.
- *Configuration names.* "Down-vote cast on an answer" is `DownvoteCastTypes` (empty = every
  moderatable type); "content deleted by a moderator reverses its votes" is
  `ReverseVotesOnModeratorDelete` (a behaviour, not points), so `Reputation` only takes the five point
  events. `Audit/Moderation` is a right of its own and never earnable; the default earnable set is
  `Query, Read, New, Edit, Vote, Downvote, Flag, Review`. Each privilege lists `Grants` for
  `--spark-init-moderation`, which prints the rights and writes nothing.
- *Merge* reverses every vote the duplicate cast (rule `merge`); it does not move content or
  accounts.
- *Package references.* Moderation references `MintPlayer.Spark.Authorization` (`SparkUser`, lockout,
  the D8 deletion/personal-data hooks) and `MintPlayer.Spark.Cron`; `AddModeration<TUser>()` is
  generic. It does not reference SoftDelete or History: moderator restore/purge/revert/delete are seen
  (and audited, and a moderator delete reverses votes) through the core interceptor's operations.
- *Map-reduce base class.* The indexes derive from `AbstractIndexCreationTask<T, R>` directly;
  `SparkIndexCreationTask<T>` has no two-type form and none of them has generated field options.
- *Core translations* gained a `moderation` block (en/fr/nl) for the ng-spark entry point.
- *Test harness notes (measured):* a test host without a sign-in scheme refuses anonymous callers
  with 404, not 401 (`SparkDenial`, by design); the antiforgery token is bound to the user name, so a
  test that switches principals mints a token per identity.

**Deviations and findings (M13).** No spikes are named for M13. Nothing in M13 was run end to end:
the QnA E2E spec compiles and is run by M14, and nothing was checked in a browser.
- *Test seam.* Moderation gained a public `ISparkModerationJobs` (`RunCreditingAsync`,
  `RunFraudDetectorAsync`, `RecomputeReputationAsync`), the Cron jobs' own code, with no right check
  and no endpoint of its own. QnA maps `POST /qna-test/moderation/{credit,detect,recompute}` only when
  `QnA:TestSeams:Enabled` is set (Development and the E2E host), refuses startup with it in
  Production, and answers 404 to anyone without `Audit/Moderation`. The E2E host sets
  `CreditDelayHours = 0` and both job schedules to 31 February instead of shifting a clock: a global
  `TimeProvider` moved 49 h would also move Identity's cookie and bearer expiry. So "delayed" in the
  E2E spec means "counted only once the job credits it"; the 48 h figure stays pinned by the unit
  tests on a controllable clock.
- *Found (M12): the pending figure is stale.* A vote does not recompute its recipient's
  `ModerationReputation/{userId}` summary, and the crediting job recomputes only the users it
  credited, so `Pending` (the badge's "+N pending") changes only when something else recomputes the
  summary: that user being credited, a reversal, a flag decision, a deletion. During the 48 h delay
  the badge never shows the pending part. Not fixed here (a recompute on the vote path costs two
  index waits); the E2E spec calls the recompute seam. Decide in M14. **Fixed in M14:** pending is
  read live from `Moderation/PendingReputation` by `GetReputationAsync` (bounded 2 s index wait,
  the stale figure on timeout). Chosen over a recompute on vote because the ledger entries are
  written in the vote's own transaction, so a live read can never miss a vote through a skipped or
  crashed recompute, and the vote path stays free of index waits; pending affects no privilege, so
  only the badge needs it.
- *Found: a reversal lowers the total at the next crediting run.* A compensation is written with
  `Credited = false` (never creditable before the entry it cancels), so the detector's reversal
  reaches `Total` only when crediting runs next (every 5 min). Consistent with the design; the spec
  credits after detecting. **Fixed in M14:** a `Reversal` of an already-credited entry is written
  credited, so the recompute every reversal path already runs (`ReverseVotesAsync`: detector,
  moderator reverse, content delete, account deletion, merge) lowers `Total` at once; a reversal of a
  pending entry still waits for the entry it cancels. Tests: the vote test reads the badge with no
  recompute; the serial-voting test reads the stored summary with no crediting run.
- *SPARK011 / SPARK012 did not know Moderation.* The first app to grant `Vote`, `Downvote`, `Flag`,
  `Lock`, `Review`, `Suspend`, `Audit` got a SPARK011 per right: Moderation asks for them through
  `IPermissionService`, not `[SparkAuthorize]`, so the analyzer cannot harvest them. They joined the
  analyzer's built-in actions (as M6/M7's did) and `Moderation` its reserved targets; tests added.
- *QnA's own rules.* A moderator is whoever holds `Lock/Question` (never earnable, D12). `IsClosed` is
  read-only, and the entity mapper never writes a read-only attribute — not even from server code
  through `IDatabaseAccess` — so Close / Reopen record the change in a scoped `QuestionStateChanges`
  and save through `IDatabaseAccess`, where `QuestionActions.OnBeforeSaveAsync` applies it; the lock,
  the stamping and the revision all apply. The Delete gate asks a query (live answers), waited on for
  at most 3 s and failing closed, because the auto index is created by that first query.
- *The vote widget's slot* is a real `int Votes` property (a model cannot declare a display-only
  attribute); it is always 0 — the score is in `ModerationTallies/{id}`. The `AuthorId` cell renders
  the author's reputation badge: the SPA has no user names (D8 stores ids; `AuthUser` carries no id).
- *Account-deletion mail.* The framework sends none; QnA's `ISparkAccountDeletionHandler` mails one
  from an app template (`Templates/Mail/AccountDeleted{,.nl}.mjml`), registered after Moderation's.
  Handlers run before the store delete, so a store failure after it leaves a sent goodbye on a live
  account — documented on the handler. **Fixed in M14:** Authorization gained
  `ISparkAccountDeletedHandler<TUser>` (`OnAccountDeletedAsync`), run only after
  `UserManager.DeleteAsync` succeeded; a failure there is logged and the answer stays 204. QnA's
  handler implements it instead, so a stopped or refused deletion mails nothing (tests in
  `AccountFlowTests`).
- *QnA sets `RequireConfirmedEmail = true`* (D6's option, default false), so the E2E spec proves the
  confirmation link. `appsettings.Development.json` lowers the fraud gates and turns the seams on, as
  a layering override of `moderation.json` (D14).
- *Coverage.* QnA's host report is its own expected entry in `tools/verify-coverage-paths.mjs`
  (required when `SPARK_E2E_HOST_COVERAGE` is on), so a CI run that filters the QnA tests out while host
  coverage is on fails that check. **Fixed in M14:** `SparkAppTestHost` writes
  `coverage/{slug}-host-…/host-started.txt` before it starts a measured host, and a host report is
  required only for the slugs with a marker (still fail-closed when marker information is absent);
  a filtered run passes, a host that started and lost its report still fails. Node tests added.

**S-PKG2 — `dotnet pack` output of the new packages (M14, 2026-09-28).** `dotnet pack -c Release` of
the seven new projects into a scratch folder, nuspecs read back. Every package is
`11.0.0-preview.91`, `net11.0`, MIT, with its own id:
`MintPlayer.Spark.Moderation.Abstractions` (no dependencies, README),
`MintPlayer.Spark.Moderation` (→ Spark.Authorization, Spark.Cron, Moderation.Abstractions, Spark,
Endpoints 11.2.0-rc.0, SourceGenerators.Attributes 12.1.0, RavenDB.Client 7.2.6; README),
`MintPlayer.Spark.MailManager.Abstractions` (no dependencies), `MintPlayer.Spark.MailManager`
(→ MailManager.Abstractions, Messaging, Spark, MailKit 4.18.1, Mjml.Net 4.15.0, Scriban.Signed 7.5.0,
Endpoints, Attributes, RavenDB.Client; README), `MintPlayer.Spark.SoftDelete.Abstractions` (none,
README), `MintPlayer.Spark.SoftDelete` (→ SoftDelete.Abstractions, Spark, …; README),
`MintPlayer.Spark.History` (→ Spark, …; README). Only `lib/net11.0/<id>.dll` + README in each; no
app or test project packs (`IsPackable=false` on both QnA projects). **Found and fixed:**
`MailManager.Abstractions` shipped without a README; it now packs the MailManager README, as the
other Abstractions packages do. The master workflow packs the whole `.slnx` (`dotnet pack --no-build`
after `nx run-many --target=build:release`, which lists all seven projects), so the new ids are
pushed with no workflow change. S-PKG1 (nuget.org prefix reservation and API-key scope for the new
ids) is the owner's.

**Full sweep (M14).** First pass: Spark.Tests 3197/3198, E2E 116/120, the rest green. Triage:
- *Real bug:* a suspended account's vote or flag answered 404 (the suspension withdraws the earned
  right, and the right was checked first) instead of the documented 400; the suspension is now
  checked first — it depends on the caller only, so it reveals nothing about the target.
- *Tests behind intended changes:* `FailOpenRegressionTests` mocked `HasRowRule` while the refusal asks
  `GetRowRuleKinds` since M2 (an NSubstitute substitute replaces the default interface method); two QnA
  specs expected `null` for an anonymous load, but anonymous callers get 401 by design (now compared
  with a missing id); the QnA confirm-link regex captured the `)` the mail's text part puts after a URL.
- *CI-only (run 36472913903):* both E2E collections ran `playwright install --with-deps` at once and
  apt-get's lock failed every QnA test; now one install per process.
- *Harness hang:* the host's `dotnet build` left MSBuild nodes holding its redirected stdout, so a
  local run waited ~15 min for EOF; builds now use `--disable-build-servers`, output drain bounded.
Reruns: the failed tests, then Spark.Tests (3198/3198) and E2E (120/120) in full.
#### M15 — deviations and findings

- **`OnNewAsync` already existed.** It is `OnNewAsync(SparkNewArgs<T>)`, behind `POST /spark/po/new`,
  added for AsDetail rows. The pre-M15 audit said there was no such hook, and that was wrong. The
  existing hook was extended rather than joined by a second `OnNewAsync(obj, NewContext)`:
  `SparkNewArgs` gained `ParentType`, `Query`, `ParentReference` and `FillParentReference()`, and
  `INewInvoker` gained an optional `SparkNewSubQueryContext`.
  - The auto-fill runs for a New started from a sub-query only. An AsDetail row's parent owns the
    save.
  - `NewInvoker` still skips a base hook that is not overridden, except for a sub-query New, where the
    base does work.
- **`auto` does not count the default Delete.** Counting it would have turned every deletable grid
  into click-to-select. Selection stays opt-in, and without it Delete sits in the row `⋮` menu.
- **The type is `SparkSelectionModeSetting`.** The `'auto'` value lives on
  `SparkSelectionModeSetting = 'auto' | SparkSelectionMode`, because the rendered mode is what
  `<bs-datatable>` binds.
- **An `ICustomAction` named `New` or `Delete` is never executed** (404), instead of failing at
  startup: the loader is lazy.
- **No bulk Purge.** A purge deletes revisions with an admin operation that cannot join the
  transaction.
- **Found by the new tests:** `[Register(typeof(Self))]` registers nothing (the generator maps a
  service type only when it differs from the implementation), so the write batch got an internal
  interface.
- **Not run locally.** At the owner's request (2026-09-29) the full sweep was stopped after the new
  .NET tests passed (45/45), and CI runs the suites. The other test projects, vitest, the QnA E2E specs
  and `--spark-verify-*` for the other apps were not run locally. QnA's verify-model (after the
  resync) and verify-security pass.
- **The recycle bin offers only Restore and Purge (1632dfe4).** With `deleted=only` (or under a
  deleted parent) the grid hides New, Delete and custom actions, in the toolbar and the row menu. The
  server needed no change: delete-many and actions/execute already judge live rows only, so another
  action aimed at a deleted row is a 404. Verified in the browser with a fresh moderator.
- **A deleted parent's sub-queries were all 404 (17f35fd3, 6a2a6a46).** Execute and distinct-values
  resolved the parent as a live row, so a deleted row opened from the recycle bin listed no
  sub-query. They now take `parentDeleted` (`exclude|include|only`), gated by
  `ViewDeleted/{ParentType}`, with the same 404 as a missing parent otherwise. Actions and
  delete-many stay live-only.
- **Browser check (10/10 on QnA and DemoApp) found three bugs, all fixed:** the second row menu
  closing at once (726ba3f9), no way to clear a single selection without the chip's ⊗ (8b545062),
  and selected-row links rendered primary on primary (75ab0395). Also from the owner's review: the
  card's overflow label is the translated `common.more` (40e07b86), and the header buttons are
  square (cbf1045e).
- **Row menu on the CDK overlay** because of the ng-bootstrap dropdown's initialisation order,
  [ng-bootstrap issue #419](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/419); see §3.15.
- **Spec type errors were reported nowhere.** Ten in ng-spark were fixed (59070c6f) and CI now runs
  `tsc --noEmit` on both packages' spec configs (b911f804).

#### M16 — priority lanes, ARF complaints, revision limits (owner decisions D20–D22, 2026-09-29)

**S-M3 re-run — transactional latency behind the bulk backlog (M16, 2026-09-29).**
*Method:* unchanged from M4: `ThrottleAccuracySpikeTests.S_M3_…` with `SPARK_SPIKE_SM3_BULK=1000`,
×20 time compression (20 per 3 s, 1.5 s sweeper tick), a `spike-transactional` message every 250 ms
while 1,000 `spike-bulk` messages drain, revisions on `SparkMessages`, Developer licence. The spike's
queues declare no priority (both `Normal`), so the gain measured is the window mechanism itself; mail
lanes add High/Low on top. "Before" ran from a clean worktree at `c62fa88e` immediately before "after"
at the M16 code, same machine.
*Answer:*

| | Before (c62fa88e) | After (M16) |
|---|---|---|
| Transactional during backlog, p50 / p95 / **max** | 9 ms / 50 ms / **5,235 ms** (n = 566) | 9 ms / 21 ms / **52 ms** (n = 564) |
| Baseline, no backlog, p50 / p95 / max | 19 / 34 / 34 ms | 13 / 33 / 37 ms |
| Bulk steady rate / drain | 19.94 per interval / 147.6 s | 19.96 per interval / 147.5 s |
| Most starts in one sliding 3 s window | 61 | 32 |
| Writes per deferred bulk message | 8 (×979) | **7** (×980) — the lane-side claim is gone |

A first "before" run on a loaded machine (another agent building) measured p95 3,491 ms / max 9,410 ms
and failed the kept assertion (some bulk messages at 9–11 writes, i.e. deferred more than once); the
clean runs above passed it. The M4 figure was max 5,788 ms. The throttle is unchanged (rate, drain);
the lower burst figure follows from deferrals now being written in window batches.

**Deviations and findings (M16).**
- *Priority is a bounded look-ahead, not a cross-window scheduler.* The subscription delivers in etag
  order and A11 requires a claim before the ack, so the feeder cannot hold claims in memory to reorder
  across windows; strict priority inside a window plus a window served whole is the aging scheme, and
  it makes starvation impossible rather than unlikely. The latency fix comes as much from the window
  being cheap (one load and one write per priority, deferral before the claim) as from the order.
  Remaining limit, documented in the Messaging README: an **unthrottled** slow low-priority queue that
  fills its lane (512) still back-pressures the feeder.
- *Admission moved ahead of the claim in `SingleSubscription` mode.* The processor still decides
  (per-queue mode, and as a check); a feeder-admitted message keeps a due reservation so it is not
  charged twice (unit test). The feeder uses `OptimisticConcurrencyMode.Writes`, the non-obsolete form.
- *ARF matching by `Message-ID`* is what makes redacted feedback-loop reports usable; the address
  suppressed is always the delivery record's. Complaints suppress every stream (not only the mailed
  one): continuing to mail a complainer harms the sender's reputation for all its mail; the app can lift
  it. Fixtures are hand-built on RFC 5965 Appendix B with reserved example domains.
- *Community pre-check.* `/license/status` `Type` read through a small server operation (measured:
  `Community` under `raven-community-license.log`, `Developer` under the Developer licence); only the
  Community caps are known, so other tiers fall back to RavenDB's own refusal. The model's
  `purgeOnDelete: false` cannot be told from unset (a non-nullable `bool` in Abstractions, left
  unchanged), so it defers to the default.
- *Libraries only.* No app code changed: MailManager declares the lane priorities, ARF lives in its
  bounce pipeline, the limits in History's options and configurator. QnA (the only History user) gets
  the 30-day default with no change.

#### M16b — hybrid sorted-query feeder (owner decision D23, 2026-09-29)

**S-M7 — where `Sequence` comes from (M16b, 2026-09-29).**
*Method:* `SequenceSourceSpikeTests` (kept; run with `SPARK_SPIKE_SM7_N=1000`, the kept test defaults
to 100). For each candidate, 1,000 `SparkMessage`s are
enqueued from **8 producers in parallel**, one session per message as `MessageBus` does; the ground
truth is each document's etag (its change vector on a fresh single-node database), i.e. commit order
and the order the subscription delivers in. An *inversion* is an adjacent pair, in commit order, whose
key goes down. Developer licence, embedded RavenDB 7.2.
*Answer:*

| Candidate | Per enqueue p50 / p95 / mean | Inversions / 999 | Notes |
|---|---|---|---|
| **(a) `@last-modified`** (via `MetadataFor` in the index) | 7.3 / 21.4 / 9.8 ms — the plain store, no extra work | **0** (ties 0) | Set by the server on commit. Changes on every write, hence captured once (below) |
| (b1) compare-exchange counter, taken before the store | 34 / 345 / 99 ms | 0 | **6,668 CAS retries** for 1,000 enqueues; 12 s wall against 1.3 s |
| (b2) document counter (`CounterBatchOperation`, returns the total) | 5.5 / 11.3 / 6.1 ms | **340** | Taken before the store, so commit order and key order disagree |
| (b3) batch enqueue with one reserved range of 100 | 0.14 ms / message | not measured | Cheap per message, but a range is reserved before its batch commits, so concurrent batches interleave exactly as (b2) does |
| (c) first-save etag written back | 7.6 / 14.4 / 8.3 ms | 356 against delivery order | A **second write** per message, and that write moves the etag, so delivery order no longer matches the key |
| (d) server identity id (`SparkMessages\|`) | 6.5 / 11.5 / 6.9 ms | **183** | Identities are not handed out in commit order; and a deduplicated message has a fixed id, so it cannot have one |

Four runs, the same shape each time ((a) 0 inversions every run; (b1) 0–1; (b2) 307–340; (d) 109–199).
**Chosen: (a), captured once.** The index uses `QueuedAtUtc` when set, else `@last-modified`; the
feeder writes `QueuedAtUtc` from the loaded `@last-modified` in the claim or deferral it writes
anyway, so a retry, a reclaim or a throttle wake-up (each of which moves `@last-modified`) keeps the
message's place, at no extra write. Until the feeder's first write nothing else writes a claimable
message, so the two values are the same; a delayed broadcast is the one exception, and its key is then
the sweeper's wake-up — "joined the queue when it became due", which is the intended reading.
Caveat, accepted: on a cluster fail-over to another node the keys come from a different server clock,
so ordering across that moment is off by the clock skew; nothing is lost, and the subscription still
delivers everything.
*Index semantics, asserted:* the index holds exactly the claimable set (deferred, parked, claimed and
finished messages have no entry; `Status` compares as a string); an absent `NextAttemptAtUtc`
**matches** `== null` in the map (a pre-M16b-shaped document is indexed); an absent `Priority` sorted
**below `Low`** under `desc` until the map coalesced it (`(int?)m.Priority ?? 0`) — now `Normal`.
*Index lag:* 1,000 messages from 8 producers in 212 ms; time from a message's save returning until it
is visible in `SparkMessages_ByPriority`: p50 61 ms, p95 98 ms, max 107 ms (poll granularity ~15 ms);
the last write was visible 27 ms after it returned. The subscription's own delivery lag was not measured, but
the S-M3 merge counters below show batches carrying messages the page did not have yet, and
those are served from the batch, never skipped.

**S-M3 re-run — transactional latency behind the bulk backlog (M16b, 2026-09-29).**
*Method:* unchanged from M4 and M16 (`SPARK_SPIKE_SM3_BULK=1000`, ×20 compression, a transactional
message every 250 ms while 1,000 bulk messages drain at 20 per 3 s, revisions on, Developer licence;
both spike queues `Normal`, so what is measured is the feeder mechanism). "Before" ran from a clean
worktree at `0510a293` (M16) immediately before "after" at the M16b code, same machine, nothing else
running.
*Answer:*

| | M16 as recorded | Before (0510a293, today) | After (M16b) |
|---|---|---|---|
| Transactional during backlog, p50 / p95 / **max** | 9 / 21 / **52** ms (n = 564) | 10 / 19 / **89** ms (n = 559) | 9 / 17 / **30** ms (n = 558); an earlier run the same hour 13 / 25 / 80 ms |
| Baseline, no backlog, p50 / p95 / max | 13 / 33 / 37 ms | 19 / 29 / 34 ms | 18 / 35 / 39 ms (earlier run 23 / 43 / 59) |
| Bulk steady rate / drain | 19.96 per interval / 147.5 s | 19.93 per interval / 147.7 s | 20.01 per interval / 147.2 s |
| Most starts in one sliding 3 s window | 32 | 33 | 32 |
| Writes per deferred bulk message | 7 (×980) | 7 (×980) | 7 (×980) — capturing `QueuedAtUtc` adds no write |
| Feeder merge (M16b only) | — | — | 227 messages pulled ahead from the page; 106 served from the batch alone (not indexed yet, or beyond the page); page never unavailable |

*Reading:* with both spike queues at `Normal`, M16b is within run noise of M16 despite the extra
query per wake-up (p95 17–25 ms against 19–21, max 30–80 against 52–89; the back-to-back pair is
9/17/30 against 10/19/89, and the M16 max itself ranged 52–89 across two runs). The throttle is unchanged (rate,
drain, burst, writes). Strict ordering is not what this spike exercises — the pipeline test in
`QueuePriorityTests` does (a High message behind 300 Low is served first, and fails with the page
disabled). The merge counters are the confirmation S-M7 asked for: during the bulk load the
subscription batch repeatedly carried messages the page did not yet have (106), and each was served.

**S-M8 — leader handover under load (M16b, 2026-09-29).**
*Method:* `LeaderHandoverSpikeTests` (`SPARK_SPIKE_SM8=1`). Three messaging hosts in one process on
one database, each with its own `IDocumentStore` and its own identity (`MessageClaims.HostScope`, an
`AsyncLocal` test seam, because `NodeId` is per process); queues `High`/`Normal`/`Low`, a 3 ms
handler that records its side effect last, `ClaimTtl` 10 s, sweeper tick 1 s, the real lease (TTL
30 s, standby poll 5 s). **Crash:** 1,000 mixed messages; once 250 are handled, host A's document
store is disposed (no release, no drain, claims left at `Processing`) and a High message is published
through B. **Graceful:** B drains a second 1,000, is stopped with `StopAsync` mid-drain; the test then
holds the released lease, commits 300 mixed + 1 High in one transaction, waits for indexing and
releases, so C's first page is the whole sorted backlog.
*Answer:*

| | Crash (store disposed at 250–258 handled) | Graceful (`StopAsync` mid-drain) |
|---|---|---|
| Lost | **0** of 1,001 (two runs) | **0** of 1,301 |
| Processed twice | **3** then **1** — each the handler in flight on one of the dead host's three lanes (finished, completion not recorded) | **0** |
| Takeover | standby leads after **27.4–27.5 s** (the lease's 30 s TTL, less its age at the crash), serves its first message the same instant; all 1,001 done after 32.3–32.6 s (737–744 orphaned claims come back in two sweeps, 512 + the rest, once `ClaimTtl` has passed) | `StopAsync` (feeder stop + lane drain + release) **3.1–4.0 s**; standby leads **4.7 s** after the release (≤ its 5 s poll) |
| High message published during the handover | the new leader's **#1** | the new leader's **#102** — after the 101 older High messages, before every Normal and Low |

*Found and fixed by this spike:* (1) RavenDB's `SubscriptionWorker.Run()` can return on cancellation
while the batch callback still runs, so a stopping feeder routed into lanes the drain had closed
(`ChannelClosedException`) and the messages it had just claimed waited for `ClaimTtl` — 5 minutes by
default on every graceful deploy that landed mid-batch. The feeder now releases the claims it could not
route (`MessageClaims.ReleaseUnstartedAsync`, which existed and nothing called). (2) The first run's
graceful takeover took 29 s because the spike stopped B under the *test's* identity, and the release is
(correctly) guarded on the holder's `NodeId`; with the host's own identity the release works, and
`MessageSubscriptionManagerLifecycleTests.A_graceful_stop_releases_the_messaging_lease` pins it.
(3) *Traced and fixed:* the one `OperationCanceledException` logged per run as "Error releasing the messaging lease" was the crashed host A's teardown — RavenDB's context pool throws it on a disposed store (the release already used `CancellationToken.None`, not the stopping token); the release now skips a disposed store at Debug and runs on its own 5 s failure bound (`ReleaseTimeout`), and the trace also exposed a real race — the lease loop ran on the stopping token alone, so a renewal falling due during the drain re-acquired the lease and started a second feeder on the stopping host — now ended first by `StopAsync`; rerun: no lease error, graceful takeover 4.7 s, 0 lost (`A_stop_after_the_store_is_disposed_…`, `A_renewal_falling_due_during_the_drain_…` fail before, pass after).

**S-M9 — the feeder under the Community licence (M16b, 2026-09-29).**
*Method:* `ThrottleAccuracySpikeTests.S_M9_…` (`SPARK_SPIKE_SM9=1`, `SPARK_SPIKE_SM3_BULK=1000`,
`RAVENDB_LICENSE` = the renewed Community licence): polls `/license/status` until the licence is applied
(reading only `Type` and `Status`), then runs the S-M3 load without revisions (Community keeps 2) and
checks the database's subscriptions and the sort index.
*Answer:* `/license/status` reported **`Type: Community`**, `Status: Commercial` (not an AGPL
fallback). After the run the database held **one** subscription (`SparkMessaging`), inside the 3-slot
cap; `SparkMessages/ByPriority` deployed and served in state `Normal` with **0** indexing errors (0
entries once everything was done, as it should be), and the page was never unavailable. The load: 1,000
bulk drained in 147.2 s at 20.03 per interval (most starts in one window 31); transactional during the
backlog p50 / p95 / max **11 / 29 / 193 ms** (n = 559), baseline 29 / 40 / 47 ms. Community's 3-core cap
shows in the merge counters: 1,337 messages pulled ahead from the page and 1,103 served from the batch
alone (Developer: 227 / 106), consistent with the index lagging further behind on 3 cores; the batch merge is what keeps that
from delaying anything. Revisions (writes per message) are not measured under Community.

---

## 5. Risks

1. **Size.** One PR carrying a core seam, 4 packages (+3 abstractions), auth, mail, messaging, a new demo app and an E2E host refactor. Mitigation: milestone commits, each type-checked; one test sweep at the end (per the user's global rules); CI must be green across **all 5** test projects.
2. **Production redeploy on merge** (CodeCoverage): new startup guards (Data Protection storage, mail transport, forwarded headers) stop it from starting unless the VPS config is set first → pre-merge ops checklist (plan §"Pre-merge"). **Updated (M14):** none of them needs a VPS step — `docker-compose.yml` (pulled from master on deploy) carries the key-ring volume + `Spark__DataProtection__KeysPath`, maps `MAIL_*` onto `Spark__Mail__*` with `Security=None`, and sets `Spark__Auth__PublicBaseUrl`; forwarded headers use the private-range default and need a setting only if a CDN fronts the site. What remains is verifying what fronts coverage.mintplayer.com (risk 4).
3. **Key ring**: wrong `ApplicationName`/prefix during CodeCoverage's migration signs every production user out once → SP-B + a test reading a current-format key document.
4. **Behind a CDN**: D15 defaults make every visitor share the proxy IP → verify both production front-ends.
5. **Absent-field semantics** (`!= true`) — this repo has shipped the `!x` bug twice.
6. **History bypassing redaction/row security** on old revisions.
7. **Wildcard removal and DisableActions removal** break unknown third-party consumers — accepted (preview), release notes.
8. **Vote fraud**: a patient attacker still farms slowly; thresholds are visible config. Bounded by "only reversible privileges are earnable".
9. **Revisions keep personal data** — **mitigated in M16 (D22, 2026-09-29):** revisions are kept 30 days by default (`Spark:History:Revisions`), per type overridable; RavenDB still has no storage quota, and a revision is only purged when its document is written again.
10. **E2E host refactor** in known-flaky infrastructure can block CI for the whole PR.
11. **Moderation age gates fail closed** (M12): accounts whose `CreatedAtUtc` the M5 backfill could not
    find pass no `MinAccountAgeDays` gate. Count them before the MintPlayer cutover; backfill from
    another source or accept it. The count is a task in the plan's MintPlayer cutover list
    (`docs/issue_460_plan.md`).

## 6. Out of scope (genuinely not being done)

- Rewriting the overridable core pipeline (D1 — owner decision, documented gaps instead).
- Moderation: suggested edits, accepted answers/bounties, close votes, badges, notifications, device fingerprinting, ML Sybil detection, vote fuzzing, shadow bans, automatic suspension.
- Opening inbound SMTP on the VPS (tier-2 bounces are a documented recipe, D9).
- System dark mode (`prefers-color-scheme`), tracked in #462, was left out by the owner on 2026-09-29. It needs an ng-bootstrap release first, for theme switching and theme-aware datatable and component variables. The planned order is ng-bootstrap, then its release, then Spark. The #460 selected-row contrast fix already uses the datatable's theme variable, so it carries over unchanged.
- Absorbing the MintPlayer repository into `apps/MintPlayer`: **decided, as the next step after this PR merges**, then the old repository is archived and this one pinned (see the plan).
- MintPlayer's own upgrade (tracked in the MintPlayer repo) — but its ForwardedHeaders and soft-delete overrides are called out for it.
- Deprecating SocketExtensions 10.0.0 on nuget.org (manual account step).
