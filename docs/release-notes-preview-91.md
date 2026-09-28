# Spark 11.0.0-preview.91 — the framework work for the MintPlayer migration (#460)

**Packages:** every changed `MintPlayer.Spark*` NuGet package → `11.0.0-preview.91`: `MintPlayer.Spark`,
`.Abstractions`, `.Attributes`, `.AllFeatures`, `.AllFeatures.SourceGenerators`, `.Authorization`,
`.Client`, `.Client.Authorization`, `.Controllers`, `.Cron`, `.IdentityProvider`, `.Messaging`,
`.Messaging.Abstractions`, `.Migrations`, `.Replication`, `.Replication.Abstractions`,
`.SourceGenerators`, `.LibraryGenerators`, `.SubscriptionWorker`, `.SubscriptionWorker.Abstractions`,
`.Testing` and `.Webhooks.GitHub.DevTunnel`. **New packages**, also `11.0.0-preview.91`:
`MintPlayer.Spark.SoftDelete` (+ `.Abstractions`), `MintPlayer.Spark.History`,
`MintPlayer.Spark.MailManager` (+ `.Abstractions`) and `MintPlayer.Spark.Moderation`
(+ `.Abstractions`). Unchanged: `MintPlayer.Spark.Webhooks.GitHub` and
`MintPlayer.Dotnet.SocketExtensions`. npm: `@mintplayer/ng-spark` → `22.24.0`,
`@mintplayer/ng-spark-auth` → `22.14.0`. The majors do not move: the packages still target .NET 11
and Angular 22, and breaking changes ship as a minor.

One theme: **everything MintPlayer needs before it can move onto Spark** — soft delete, revision
history, mail, community moderation and self-service account pages — built as packages over one new
core seam (row policies and persistent-object interceptors), plus the startup guards that stop a
deployment from silently losing keys, trusting a spoofed client address or mailing real users from
a laptop.

The authoritative record, with every measurement and deviation, is `docs/issue_460_PRD.md` (§4.1).

---

## ⚠️ Breaking changes

### 1. Wildcard rights are gone

A `security.json` resource containing `*` — `Read/*`, `*/Person`, `*/*`, grant **or** denial — is
refused at startup, and the build reports it first as analyzer error **SPARK021**. The matcher
branches and the posture report's "floor rather than a ceiling" warning were removed with them.

**Why.** A GDPR or ISO 27001 access review has to be able to enumerate who can do what, and a
wildcard also covers types and actions that do not exist yet.

**Who is affected:** every application with a `*` in `security.json`.

**Migration.** Name every target and cover several actions with a combined action:
`QueryReadEditNewDelete/Person` instead of `*/Person`. A new entity type is denied to everyone until a
right names it. See [guide-authorization.md](guide-authorization.md).

### 2. Disabling actions: one hook, `OnDisableActionsAsync`; the old entry points are deleted

**Deleted:** `PersistentObject.DisableActions(...)` (now an explicit `IDisablable` member),
`ClientAccessor.DisableActionsOn(...)` (both overloads), `ClientAccessor.DisableQueryActions(...)`,
`ClientAccessor.DisableActions(...)`, `IClientAccessor.DisableActionsForSession(...)`,
`CustomQueryArgs.DisableActions(...)` and `SparkQueryContext.DisableActions(...)`.

**Replaced by** `IDisablable.DisableActions(params string[])` and a virtual
`OnDisableActionsAsync(IDisablable target, DisableActionsContext context)` on the Actions class. The
framework asks it **at load** (detail, query execute → the unchanged `disabledActions` on the wire) and
**at submit** (update, delete, create, custom action: the parent, the query and every selected row,
unioned). It covers built-in actions (`Edit`, `Save`, `Delete`, `New`) and custom ones. A disabled
action is refused with **`403 { error, action }`**, and only **after** the row gate: a hidden row
stays a 404, and the hook is never asked about it. Update is refused when `Edit` **or** `Save` is
withheld, create when `New` or `Save` is. The hook must depend only on the entity (the **stored**
one at submit, not the posted one), the user and stored state.

**Also deleted: the `disableAction` client operation** — the server types `DisableActionOperation`
and `DisableTarget`, `SparkDisableActionOperation` in `MintPlayer.Spark.Client`, and the ng-spark
operation type with its warn-only handler. An older server's operation still parses in `SparkClient`
as `SparkUnknownOperation`.

**Who is affected:** any Actions class, custom query or action that disabled actions through the old
calls; clients that handled `disableAction`.

**Migration.** Move the rule from `OnLoadAsync` (or wherever it lived) into `OnDisableActionsAsync`;
`context.TargetKind` says what the target is (never downcast it). CodeCoverage's `RepositoryActions`
is the worked example: the page is identical, but a connected repository's `DeleteData` is now
refused by the framework with 403 before the action runs. See
[guide-custom-actions.md § Disabling actions](guide-custom-actions.md).

### 3. New startup guards — a misconfigured deployment now refuses to start

⚠️ **Read the Operator checklist at the end before deploying.**

- **Data Protection key ring.** Spark now calls `AddDataProtection()` itself. Outside Development,
  startup is refused unless the key ring is persisted: `Spark:DataProtection:KeysPath` (a folder —
  on a **mounted volume**, or every redeploy signs everyone out) **or**
  `Spark:DataProtection:Storage=RavenDb`. `Storage=RavenDb` is supported but meant for **test hosts**
  (set in their own configuration); production uses `KeysPath` on a mounted volume (owner decision,
  #460 D5), and no app's base `appsettings.json` sets either. Setting **both** also refuses. `ApplicationName` defaults to
  the entry assembly's name. Development defaults to a per-user local folder. An application's own
  `AddDataProtection()` calls still win, but the check still wants one of the two keys; delete them.
  See [guide-data-protection.md](guide-data-protection.md).
- **Forwarded headers.** Spark now configures `ForwardedHeadersOptions` and places
  `UseForwardedHeaders()` itself — remove your own. Defaults: `X-Forwarded-For` and
  `X-Forwarded-Proto` trusted from loopback and the private ranges (`10/8`, `172.16/12`,
  `192.168/16`, `fc00::/7`); `ProxyHops` (the `ForwardLimit`) `1`, never unlimited;
  `X-Forwarded-Host` **not** honoured. Override with `Spark:ForwardedHeaders:KnownNetworks` /
  `KnownProxies` (setting either replaces the default list). Refused at startup: a configuration that
  trusts every sender outside Development (both lists empty — what the old
  `KnownNetworks.Clear(); KnownProxies.Clear();` blocks did), `ProxyHops < 1`, a `ForwardLimit` set to
  null afterwards, and `ForwardHost=true` while `AllowedHosts` is `*`. The effective trust list is
  logged. ⚠️ Behind a CDN, add its ranges, or every visitor shares one rate-limit bucket. See
  [guide-docker-deployment.md § Forwarded headers](guide-docker-deployment.md).
- **Mail** (when MailManager is added). Refused: no transport (none registered, neither
  `Spark:Mail:Smtp:Host` nor `Spark:Mail:PickupFolder`), no `Spark:Mail:From:Address`, two
  transports; in **Development**, a transport that delivers to real recipients without
  `Spark:Mail:Development:RedirectTo` (fail closed — use Mailpit, the pickup folder or a redirect in
  user secrets); in **Production**, `RedirectTo` itself (other environments keep redirecting with a
  warning); a template name without a neutral (culture-less) file, outside Development (a warning
  in Development). See
  [guide-outgoing-mail.md § 1](guide-outgoing-mail.md).
- **Registration needs mail (D6).** `SparkLocalCredentials.Full` whose account mail would be
  discarded (Identity's no-op sender, or Spark's without MailManager) refuses startup. Opt out with
  `Spark:Auth:AllowUnconfirmedRegistration=true`.
- **History and the RavenDB licence.** History merges the model's revisions configuration into the
  database record at startup. A model limit beyond the licence refuses startup with RavenDB's own
  reason — measured on Community: at most 2 `minimumRevisionsToKeep`, at most 45 days
  `minimumRevisionAgeToKeep`, no enabled default configuration. Configuring revisions (and a purge's
  `DeleteRevisionsOperation`) needs **database-admin**; opt out of the configuration step with
  `Spark:History:ConfigureRevisions=false`. A purge without that clearance is refused *before* the
  document is deleted.
- **Moderation** validates `moderation.json` after configuration layering: group ids must exist, not be
  well-known, and hold only earnable rights; unknown reputation event names are an error.

### 4. Messaging: the deduplication-key format changed, and `IMessageBus` has a new core member

`BroadcastOnceAsync` / `BroadcastOptions.DeduplicationKey` now produce
`SparkMessages/{readable part ≤ 64}.{first 16 bytes of SHA-256(versionless type + "\n" + key)}` — hashed
and namespaced by message type, so two types may use the same key and keys differing only in a
character an id cannot hold no longer collide. **Pre-upgrade documents keep their old ids**, so a
duplicate of a message enqueued before the upgrade is enqueued once more within the old documents'
retention (7 days by default).

`IMessageBus` gained `BroadcastAsync(message, BroadcastOptions, ct)`; `BroadcastAsync(message)`,
`DelayBroadcastAsync` and `BroadcastOnceAsync` are now default members forwarding to it. **A
hand-written `IMessageBus` (a test fake, a recorder) must implement the new overload.**
`BroadcastOptions.Queue` must name a queue declared in `Spark:Messaging:Queues`, otherwise the publish
throws.

### 5. Spark owns the account endpoints that send mail

`MapIdentityApi`'s `register`, `resendConfirmationEmail`, `confirmEmail`, `forgotPassword`,
`resetPassword` and `POST manage/info` are filtered out in **every** `SparkLocalCredentials` mode
(`Full` no longer maps Microsoft's surface verbatim) and mapped by Spark under the same paths and
contracts, because Microsoft's versions build their own links and check confirmation themselves. Kept
from Microsoft: `login`, `refresh`, `manage/2fa`, `GET manage/info`. New:
`POST /spark/auth/confirm-email` (also confirms a changed email; mapped in every mode, `Disabled`
included), `manage/password`, `manage/profile`, `GET manage/2fa/authenticator-uri`,
`GET manage/personal-data`, `DELETE manage/account`. The table is in the
[Authorization README](../libs/authorization/MintPlayer.Spark.Authorization/README.md#identity-endpoints).

What changed in behaviour:

- Mail links point at the **SPA**: `{PublicBaseUrl}/confirm-email?…` and
  `{PublicBaseUrl}/reset-password?email=…&code=…`. ⚠️ Set `Spark:Auth:PublicBaseUrl` outside
  Development: without it no link-bearing mail is sent (logged as an error) rather than building a link
  from the request's `Host` (password-reset poisoning). Development falls back to the request origin.
- `resendConfirmationEmail` mails unconfirmed accounts only.
- `GET confirmEmail` no longer sets the user name to a changed email (it overwrote chosen handles).
- ⚠️ `register`, `resendConfirmationEmail` and the new `manage/*` routes require an **antiforgery
  token** (preview.87 had left `register` and `resendConfirmationEmail` ungated). `SparkClient.RegisterAsync`
  sends one; a hand-rolled client must GET first and echo `XSRF-TOKEN` in `X-XSRF-TOKEN`.
- **User names containing `@`** must now equal the account's own email (validator rule, enforced on
  every save). An existing account that violates it fails its next update until renamed.
- The MSBuild target `SparkAuthEnsureNpmPackage` is deleted (it ran npm in the wrong folder and wrote
  `^latest`); a missing `@mintplayer/ng-spark-auth` in `$(SpaRoot)package.json` is warning **SPARK030**.

**Who is affected:** applications that mapped their own handler on one of those paths, sent their own
confirmation mail (CodeCoverage deleted `SmtpLinkConfirmationSender` and moved onto MailManager), or
whose SPA has no `confirm-email` / `reset-password` page.

**Migration.** Remove the app's own versions; add MailManager; set `Spark:Auth:PublicBaseUrl`; mount
the pages with `sparkAuthRoutes(withLocalLogin(), withAccount())` (`withLocalLogin()` brings
`reset-password`, `withAccount()` brings `confirm-email`).

### 6. `forgotPassword` mails a link, not a code

It used to mail Microsoft's reset **code**; it now mails a link (`SendPasswordResetLinkAsync`) to the
SPA's `reset-password` page, which posts the code back. It also mails **unconfirmed** addresses, and
completing a reset with a valid token sets `EmailConfirmed = true` — so an old unconfirmed account can
always get in. Templates that rendered the code must render the link.

### 7. `SparkTestSecurity.Permissive` is a baseline, not a `*/*` grant

`MintPlayer.Spark.Testing`'s permissive default used to write a wildcard grant into the fixture's
`security.json`. Wildcards are refused now, so it became a thin layer over the real evaluator: a
resource is allowed when `security.json` allows it **or** when nothing the builder denied (`Denying`,
`Without`) covers it. Denials are still written to the file and evaluated by the production code path.
`Without(target)` now denies `QueryReadEditNewDelete/{target}` in the file and withholds custom actions
too.

⚠️ **A host assembled by hand through `SparkTestSecurityFile.Write` gets only the file, which grants
nothing** — there `Permissive` means "boots", not "allows". Build such hosts through
`SparkEndpointFactory`, or write the grants they need.

### 8. `sparkAuthGuard` is asynchronous

`@mintplayer/ng-spark-auth`'s `sparkAuthGuard` now awaits the session check (`checkAuth()`) when the
user is not yet known to be signed in, so a hard reload no longer bounces a signed-in user to the
sign-in page. It returns a `Promise`; code that called the guard function directly and used its
result synchronously must await it. `sparkAuthenticatedGuard` is an alias.

### 9. Refusal codes

- A write refused with a `SparkValidationException` inside a custom action (a locked post, a
  suspended account, a reference to a soft-deleted row) is now **400** from `/spark/actions/execute`;
  it was the catch-all **500**.
- New core `SparkThrottledException` → **429** with `Retry-After` and envelope `retryAfterSeconds`,
  mapped on `/spark/po/create` and `/spark/actions/execute` (Moderation's new-account throttle and
  votes-cast cap use it). The rate limiter's own 429 still has an empty body.

### 10. Smaller changes that can break

- **Timezone ids are validated** (≤ 64 characters, IANA shape) before `FindSystemTimeZoneById`; a
  Windows id in the header (`Romance Standard Time`) now falls back to UTC. `SparkClient` converts a
  Windows id to IANA before sending. The server also reads a `spark-timezone` cookie
  (`Spark:TimeZone:CookieName`, empty disables): header → cookie → UTC.
- **Secrets at rest.** The authenticator key and external-login tokens are stored as
  `sdp1:` + Data Protection ciphertext, and a backfill rewrites legacy plaintext once after start. An
  older Spark has no code for the prefix and would read the ciphertext as the value itself, so do not
  roll an upgraded database back to an older Spark; a lost key
  ring makes them unreadable (two-factor stays required; recovery codes still work).
- **Actions-class row rules and the new operations.** `GetRowFilterAsync` / `IsAllowedAsync` are asked
  about `"Edit"` for a restore or revert and `"Delete"` for a purge (row policies see the real names),
  so a rule written for the built-in verbs also governs them.
- **A custom `ISparkMailTransport` is treated as delivering to real recipients.**
  `ISparkMailTransport.DeliversToRealRecipients` defaults to `true` (Mailpit and the pickup folder say
  `false`, SMTP only for a loopback host), so a custom transport added with `AddMailTransport<T>()`
  **refuses to start in Development** unless `Spark:Mail:Development:RedirectTo` is set. For a catcher
  (a local test inbox, an in-memory sink) override the property to `false`.
- **The bounce endpoint answers 503 while disabled**, never 404. `POST /spark/mail/bounces` is mapped
  with the rest of MailManager; with `Spark:Mail:Bounces:Endpoint:Enabled` false it answers 503. The
  documented pipe recipe (`spark-bounce`, [guide-outgoing-mail.md § 8.3](guide-outgoing-mail.md))
  maps the answer to an exit code: **2xx → 0** (delivered); **400, 413, 422 → 0** with a `dropped` line
  (the report can never be accepted); **anything else → 75** (`EX_TEMPFAIL`: 401/403 wrong secret,
  404 wrong URL, 429, 503 disabled, other 5xx, no answer), so Postfix keeps the report deferred and
  delivers it once the cause is fixed or the endpoint is enabled. Update a copied script whose
  `case` treated 404 or 503 as a drop.
- **Goodbye mail moves to `ISparkAccountDeletedHandler<TUser>`.** `ISparkAccountDeletionHandler<TUser>`
  runs *before* the account is deleted, so a mail sent there goes out even when a later handler or the
  store refuses the deletion. Anything that must happen only for a completed deletion (a goodbye or
  confirmation mail) belongs in the new `ISparkAccountDeletedHandler<TUser>.OnAccountDeletedAsync`,
  which runs after `UserManager.DeleteAsync` succeeded; keep clean-up that must succeed first (and may
  stop the deletion) in `ISparkAccountDeletionHandler<TUser>`.

---

## New

### Core (`MintPlayer.Spark`, `.Abstractions`)

- **Row policies** — `IRowFilterPolicy` / `IRowCheckPolicy` (typed helpers `RowFilterPolicy<T>`,
  `RowCheckPolicy<T>`), `spark.AddSparkRowPolicy<T>()`: one rule for many types, composed with the
  Actions class's filter by parameter rebinding + `AndAlso` (no `Invoke`, no search-option leak) and
  applied on every path the row filter reaches. See [guide-row-security.md](guide-row-security.md),
  including the documented D1 gaps (an `OnLoadAsync`/`OnSaveAsync` override that skips the base skips
  the row gate and `WITH CHECK`; references are not row-checked on save).
- **Persistent-object interceptors** — `IPersistentObjectInterceptor`
  (`spark.AddPersistentObjectInterceptor<T>()`) around save, delete (with `Replace()`, which an
  `OnDeleteAsync` override cannot defeat), load and natural-id collisions, run in `DatabaseAccess`.
- **#285 closed** — an entity row filter is rebound onto an index projection by member name and pushed
  down; a projection lacking a member falls back to the post-filter.
- `deleted: exclude | include | only` on query execute and `/spark/po/load` (honoured for
  `ViewDeleted/T` holders); the permissions endpoint reports `canRestore`, `canPurge`,
  `canViewDeleted`, `canViewHistory`, `canRevert`.
- **Custom actions return data** — `CustomActionArgs.SetResult<T>()`, envelope `result`,
  ng-spark `executeCustomAction<T>()`, `SparkActionResult.Result`.
- `ISparkCurrentUser`; `AddGroupMembershipProvider<T>()` **composes** providers (ids allowed via
  `IGroupIdMembershipProvider`, resolved once per request); `UseGroupMembershipProvider<T>()` keeps
  replace semantics.
- `IPersistentObjectPresenter` and `SparkAddOnEndpoints` for add-on packages.

### `MintPlayer.Spark.SoftDelete` (+ `.Abstractions`)

`ISoftDeletable` (`IsDeleted`, `DeletedAt`, `DeletedBy`, `DeleteReason`), `spark.AddSoftDelete()`: a
delete becomes a mark, deleted rows are hidden everywhere the row filter reaches (filter
`IsDeleted != true` — analyzer **SPARK022** flags `!x.IsDeleted` / `== false`), restore and purge
(`POST /spark/po/restore`, `/spark/po/purge`; rights `Restore/T`, `Purge/T`, `ViewDeleted/T`), a purge
also deletes the revisions, a save pointing a reference at a deleted row is refused (400). Not in
`AllFeatures`. [README](../libs/soft_delete/MintPlayer.Spark.SoftDelete/README.md)

### `MintPlayer.Spark.History`

Revisions configured on the model (`revisions` on the entity type, merged into the database record),
`IAuditable` stamping (user ids only, `CreatedBy` immutable), `POST /spark/po/revisions`, `/revision`,
`/revert` (rights `History/T`, `Revert/T`; reads redacted; revert through the full save pipeline),
`ISparkRevisionObserver`. [README](../libs/history/MintPlayer.Spark.History/README.md)

### `MintPlayer.Spark.MailManager` (+ `.Abstractions`)

`spark.AddMailManager()` with explicit transports (`UseSmtpTransport`, `UseMailpitTransport` with an
optional Development `AutoStart`, `UsePickupFolderTransport`, `AddMailTransport<T>`); mail is
**queued** (lanes `mail-transactional`, `mail-bulk` throttled to 20/min), MJML + Scriban templates as
files with a culture fallback chain (`Name.nl-BE.mjml` → `Name.nl.mjml` → `Name.mjml`), the culture
stored on the message, token-bearing payloads encrypted and scrubbed, VERP bounces
(`POST /spark/mail/bounces`, opt-in, bearer secret checked before parsing), a suppression list,
one-click `List-Unsubscribe`, campaigns fanned out one message per recipient. Authorization's account
mail uses it with shipped English + Dutch templates.
[README](../libs/mail/MintPlayer.Spark.MailManager/README.md) ·
[guide-outgoing-mail.md](guide-outgoing-mail.md)

### `MintPlayer.Spark.Messaging`

`BroadcastOptions { DeduplicationKey, Delay, MaxAttempts, ExpiresAtUtc, Queue, ScrubPayloadOnTerminal }`,
per-queue `SparkQueueOptions` (`Spark:Messaging:Queues:{name}`: `MaxPerInterval` + `Interval`,
`BatchSize` + `MinDelayBetweenBatches`, `MaxConcurrency`, `MaxAttempts`, `Backoff`) with GCRA throttling
that never waits inside a lane, `DeadLetterReason` (`MaxAttempts` / `NonRetryable` / `Expired`),
`IMessageContext`, `IMessageProgress`. `SubscriptionPerQueue` gained the handler timeout and claim
renewal. [README § Per-queue options and throttling](../libs/messaging/MintPlayer.Spark.Messaging/README.md#per-queue-options-and-throttling)

### `MintPlayer.Spark.Moderation` (+ `.Abstractions`)

`spark.AddModeration<TUser>()` on opt-in `IModeratable` entities: votes and a reputation ledger,
privileges earned as `security.json` groups (never `Lock`, `Suspend`, `Purge`, `Restore`, `Revert`,
`ViewDeleted`, `Audit`), all ten vote-fraud defences (age/activity gates, diversity rule, voter
eligibility, caps, 48 h delayed crediting, nightly detector with compensating reversals, IP /24 HMAC
with a rotated key and 90-day expiry), flags and a review queue, lock / suspend / merge, an audit log, a
new-account throttle (429) and an account-deletion handler. `moderation.json` binds to
`Spark:Moderation`, so every threshold is overridable; `--spark-init-moderation` prints the grants.
Two fixes landed at the end of this PR:

- The reputation badge's **pending** figure is read live from the pending index (bounded 2 s wait,
  the stored figure on timeout) instead of a summary that only changed on the next recompute.
- A **reversal of an already-credited entry counts at once**: it is written credited, so the
  recompute every reversal path already runs lowers the total immediately (a reversal of a pending
  entry still waits for the entry it cancels).

[README](../libs/moderation/MintPlayer.Spark.Moderation/README.md) ·
[guide-moderation.md](guide-moderation.md) · demo: `apps/QnA`

### `MintPlayer.Spark.Authorization`

- **Sign in with email or user name** (`SparkSignInManager`; `/connect/login` shares it); an
  identifier with `@` is an email first, a user name only when no account has that email.
- `RequireConfirmedEmail` (default `false`); per-provider verified-email trust; X and LinkedIn presets;
  new user names are a slug of the display name (GitHub keeps the login).
- **GDPR**: `DELETE manage/account` (re-authentication: password or a sign-in younger than 5 minutes)
  runs every `ISparkAccountDeletionHandler<TUser>` first, deletes the account last, and then every
  **new `ISparkAccountDeletedHandler<TUser>`** — which runs only after the store deleted the account
  (the place for a goodbye mail); a failure there is logged and the answer stays 204.
  `GET manage/personal-data` exports the account plus `ISparkPersonalDataContributor<TUser>` sections.
  Revisions are not rewritten.
- `SparkUser.CreatedAtUtc`, `RegistrationMethod`, `PreferredCulture`; server-rendered SVG QR code for
  the authenticator.

### npm

- `@mintplayer/ng-spark` 22.24.0: entry points `/soft-delete` (Deleted toggle, recycle bin via
  `?deleted=`, Restore/Purge), `/history` (`<spark-po-history>`: list, read-only view, diff, Revert),
  `/moderation` (vote widget, flag button, review queue, reputation badge, moderator panel) and
  `/panels` (`SPARK_DETAIL_PANELS`, `SPARK_DETAIL_ACTIONS`, `SPARK_QUERY_LIST_ACTIONS`);
  `withSparkTimezone({ cookieName })` writes the zone cookie and never sends the header during SSR.
- `@mintplayer/ng-spark-auth` 22.14.0: `withAccount()` — account overview, confirm-email, profile
  (app fields via `SPARK_ACCOUNT_PROFILE_FIELDS`, mail language), password, two-factor, connected
  logins, passkeys, personal data + deletion — each also a standalone component; `twitterProvider()`,
  `linkedInProvider()`; login label "Email or user name".

---

## Operator checklist

From the plan's pre-merge checklist (`docs/issue_460_plan.md`), the items a deployer must act on:

- [ ] **Data Protection volume.** Mount a volume for the key ring and set
  `Spark__DataProtection__KeysPath` into it (create the folder in the Dockerfile before `USER app`).
  Do not also set `Spark__DataProtection__Storage`: both together refuse startup. Expect every user to
  be signed out once when an existing app first switches key ring. CodeCoverage's
  `docker-compose.yml` already carries what it needs: the `dataprotection-keys` volume mounted at
  `/var/lib/codecoverage/dataprotection-keys` and `Spark__DataProtection__KeysPath` pointing at it
  (`ApplicationName=CodeCoverage` is in `appsettings.json`). It sets **no** `Storage`, and the VPS
  `.env` must not add one. Its orphaned `DataProtectionKeys/…` documents can be deleted once the
  deploy is healthy and a key file exists in the volume.
- [ ] **Mail environment.** Set `Spark__Mail__Smtp__*` (or a pickup folder) and
  `Spark__Mail__From__Address`, plus `Spark__Auth__PublicBaseUrl`. Never set
  `Spark__Mail__Development__RedirectTo` in Production. If bounces are enabled, the relay's secret must
  equal `Spark__Mail__Bounces__Endpoint__Secret`, and Postfix `ALLOWED_SENDER_DOMAINS` / SPF must cover
  the VERP domain.
- [ ] **Forwarded headers.** Remove your own `UseForwardedHeaders()` / `ForwardedHeadersOptions` code.
  If a **CDN** fronts the site, add its published ranges to `Spark:ForwardedHeaders:KnownNetworks`
  (together with the proxy's own network — setting the list replaces the default) and raise
  `ProxyHops`; see the proxy recipes in [guide-docker-deployment.md](guide-docker-deployment.md).
- [ ] **security.json** has no `*` left (SPARK021 fails the build first).
- [ ] **History on Community**: the model's revision limits fit the licence, and the database
  certificate has database-admin, or `Spark:History:ConfigureRevisions=false`.
