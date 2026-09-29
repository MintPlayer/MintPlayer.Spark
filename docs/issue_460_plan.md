# Plan — Issue #460 (one pull request)

Requirements, decisions (D1–D19, T1–T10) and spikes live in [issue_460_PRD.md](issue_460_PRD.md). This
file is the order of work.

**Rules for executing this plan**
- One branch, one PR: `feat/460-mintplayer-migration-framework`.
- Commit per milestone. **Do not run test suites per milestone.** Verify with a build + reading the code.
  Run spikes when a milestone depends on their answer. One full test sweep at the end (M14).
- Write logs raw to the scratchpad (`cmd > x.log 2>&1; echo "EXIT: $?"`), then grep them.
- Never start `ng serve` next to a running host; `dotnet run` is the whole command.
- Every touched `libs/` csproj gets a version bump; both npm packages bump minor (22.x).

---

## Milestones

Dependencies flow downward. Items in the same milestone are independent.

### M1 — Core foundations (no new packages yet)
- [x] **D3** Remove wildcard rights: validator + `SecurityConfigurationAnalyzer` reject `*` (error points at composite rights); delete matcher branches (`ISecurityConfigurationLoader.cs:130-131,199`), posture "floor" warning, `RowPolicyDeclarationValidator` wildcard handling; rewrite `SecurityFileAccessControlTests` / `SecurityPostureReporterTests` / analyzer tests to assert rejection.
- [x] **D15** Forwarded headers in core: `Spark:ForwardedHeaders:{KnownNetworks,KnownProxies,ProxyHops}`, private-range default, `ForwardLimit` never null, Proto/Host handling, startup refusal outside Development, trust list logged. Delete hand-written blocks in CodeCoverage (`Program.cs:35-70`; update the comment at `:371-374`), DemoApp (`:15-16`), HR (`:20-21`), Fleet (`:19-20`). `ForwardHost` opt-in refused while `AllowedHosts` is `*`. Spike **S-FH1**.
- [x] **D5** Data Protection in core: always `AddDataProtection()`, `ApplicationName`, `KeysPath` / `Storage=RavenDb` (lift `RavenDataProtectionKeyRepository`, same `DataProtectionKeys/` prefix), startup error outside Development when unset. Delete CodeCoverage's setup (`Program.cs:288-295`: comment + `:292-295`) + repository class; CodeCoverage config sets `ApplicationName=CodeCoverage`; production uses `KeysPath` on a volume (compose), test hosts set `Storage=RavenDb` (owner decision, see PRD D5). Spike **SP-B** (key-document compatibility part).
- [x] `ISparkCurrentUser` abstraction.
- [x] **D12 core part**: `AddGroupMembershipProvider<T>()` composition, id-returning providers, per-request cache in `SecurityFileAccessControl`. Spike **S-MOD-B**.
- [x] Update memory note: "a claim CAN assert a reserved group" is stale (reserved ids are dropped now).

### M2 — Seam (item 1) + #285 + #283
- [x] Spikes **S1, S3** first (expression shape decides the policy API).
- [x] `IRowPolicy` / `IRowFilterPolicy` / `IRowCheckPolicy`, typed helpers, `RowPolicyContext`, `AddSparkRowPolicy<T>()`; `AppliesTo` cached in `ReflectionCache`.
- [x] Compose in `RowSecurity` (`ResolveEffectiveRuleAsync`, `InvokeGetRowFilterAsync`, `ComposeRowFilterAsync`): rebinding + `AndAlso`, constant folding, same cache key, same position; budget + N+1 accounting.
- [x] `HasRowRule` split (spike **S5**); reroute base after-save WITH CHECK (`DefaultPersistentObjectActions.cs:284-309`) through `IRowSecurity`.
- [x] `IPersistentObjectInterceptor` in `DatabaseAccess` (save: `SaveEntityViaActionsAsync` `:255`, after the Edit row gate `:249`; delete `:277-321`, call at `:313`; load); contexts; ordering; delete `Replace()`; `ISyncActionInterceptor` awareness of replaced deletes. Spike **S4**.
- [x] **T2** query-request `deleted: exclude|include|only` field (core request contract; surfaces in `RowPolicyContext` request flags). ng-spark request model updated.
- [x] **D2 / #285**: projection rebinding + push-down with fallback. Spike **S2**.
- [x] #283 regression test design (breadcrumb + policy) — written now, run in M14.
- [x] Spike **S8** (request budget).

### M3 — DisableActions redesign (item 8) + custom action results
- [x] **D13**: `IDisablable`, `OnDisableActionsAsync(IDisablable, DisableActionsContext)` + batched form; call at load (PO get, query execute) and at submit (update, delete, new, custom action: parent + query + each row, union); 403 after row gate. Delete the old entry points; migrate CodeCoverage `RepositoryActions.OnLoadAsync`. ng-spark: consume `DisabledActions` unchanged on the wire. Spike **S6**, **S-MOD-F**.
- [x] **T5**: `CustomActionArgs.SetResult<T>`, envelope `Result`, ng-spark `executeCustomAction<T>`, `SparkActionResult.Result`. Spike **S7**.

### M4 — Messaging (item 11 + primitives for mail)
- [x] Fix `BroadcastOnceAsync` dedup key (hashed, type-namespaced) — keep existing ids readable (migration note).
- [x] **T8** `BroadcastOptions`, `SparkMessagingOptions.Queues` / `SparkQueueOptions`, `IMessageContext` (current message id).
- [x] Throttle admission in `MessageProcessor.RunHandlersAsync`: GCRA reserved slots, reschedule via `ReleaseUnstartedAsync`, `ExpiresAtUtc` → Expired dead-letter, `MaxConcurrency` pumps (SingleSubscription only).
- [x] `IMessageProgress` sidecar with matching `@expires`.
- [x] Fix `SubscriptionPerQueue` missing `HandlerTimeout` + claim renewal.
- [x] Fix stale line 14 in `docs/decisions_messaging_and_project_automation.md`.
- [x] Spike **S-M3** (throttle accuracy) at the end of the milestone.

### M5 — Auth (items 4, 5, 6-server, 9)
- [x] **D4** `SparkSignInManager` + `@` rule in the user validator; `/connect/login` shares the resolver. Spike **SP-A**.
- [x] **D5** secrets at rest in `UserStore` (`sdp1:` prefix, legacy read, backfill job); decide + document purpose string (with/without user id) and unprotect-failure behaviour (must not silently disable 2FA). Spike **SP-B** (rest).
- [x] **D6** `RequireConfirmedEmail`, reset-confirms-email, forgotPassword to unconfirmed. **The no-op-sender startup guard lands in M8**, together with MailManager and the demo apps' pickup mode — otherwise CodeCoverage, HR and Fleet cannot start between milestone commits.
- [x] **D7** provider verified-email trust, X + LinkedIn presets, display-name slug user names. Spike **SP-C**.
- [x] **D8** personal-data + account-deletion endpoints and handler interfaces.
- [x] **D16 server**: `/manage/password`, `/manage/profile` + `ISparkProfileContributor<TUser>`, `/manage/2fa/authenticator-uri` + SVG QR (spike **SP-D**), confirm-new-email, `ISparkAuthLinkBuilder`; `SparkLocalCredentials` classification + antiforgery for every new route.
- [x] `SparkUser.CreatedAtUtc` + `RegistrationMethod` stamped in `UserStore.CreateAsync`; backfill from `@created`.
- [x] **T9** delete `SparkAuthEnsureNpmPackage`, add the missing-dependency warning.
- [x] Spike **SP-E** (#439 SP2 passkey clone detection).

### M6 — SoftDelete package (item 2)
- [x] `libs/soft_delete/MintPlayer.Spark.SoftDelete(.Abstractions)`, slnx registration.
- [x] Spike **H1**'s `DeleteRevisionsOperation`-on-Community part first (Purge depends on it; the rest of H1 stays in M7).
- [x] `ISoftDeletable`, `SoftDeleteRowPolicy` (honours T2's `deleted` flag from M2), `SoftDeleteInterceptor` (incl. refusing references to soft-deleted targets), `ISparkSoftDelete` (Restore/Purge incl. `DeleteRevisionsOperation`), events, endpoints (T1), rights, analyzer/startup warnings (incl. `ISoftDeletable` + `OnDeleteAsync` override), natural-id error.

### M7 — History package (item 3)
- [x] Spikes **H1, H2, H4** first.
- [x] `libs/history/MintPlayer.Spark.History`; **T10** `EntityTypeDefinition.Revisions` + model-sync preservation + merge at startup.
- [x] `IAuditable` stamping interceptor (ids only, `CreatedBy` immutable).
- [x] `ISparkHistory` + endpoints (redaction on reads, current-document gate, revert through the save pipeline). Spike **H3**.
- [x] `ISparkRevisionObserver`.
- [x] M6 carry-overs: refused delete evicts the entity; `/spark/po/load` `deleted` flag; permissions `canRestore`/`canPurge`/`canViewDeleted`/`canViewHistory`/`canRevert`; purge refuses before deleting without database-admin; base-verb mapping for Actions-class row rules.

### M8 — MailManager (item 10) + CodeCoverage migration
- [x] Spikes **S-M1, S-M2, S-M4, S-M6** first.
- [x] `libs/mail/MintPlayer.Spark.MailManager` (MailKit, Mjml.Net, Scriban — pin latest stable in the csproj).
- [x] Senders (`Replace` `IEmailSender<TUser>`), `SparkMail`/`SparkBulkMail` lanes, template loader + startup parse, render test helper, transports (SMTP, pickup, custom), failure classification, dev mode, **T4** payload protection + scrub.
- [x] VERP + `SparkMailDeliveries`, bounce endpoint + pluggable DSN parser (secret check first), suppression list, `List-Unsubscribe`, campaign fan-out.
- [x] **D10** CodeCoverage: delete `SmtpLinkConfirmationSender`, add MJML template, config keys, three hand-written lists.
- [x] **D6 no-op-sender startup guard** (moved here from M5), and in the same commit: demo apps with registration → pickup-folder mode.
- [x] Rewrite `guide-outgoing-mail.md` §8; Postfix recipes (tier 1 + tier 2); spike **S-M5** informs the recipe.
- [x] Owner decision (added during M8): multi-language, file-based templates with a culture fallback chain, culture stored on the message (PRD §3.10). M7 carry-overs: a refused save evicts the entity; History reads take `deleted`. Spikes and deviations in PRD §4.1 (M8).

### M9 — Timezone cookie (item 7)
- [x] Spikes **S-TZ1, S-TZ4**, then server resolver (options, validation, precedence, logging); **S-TZ2, S-TZ3**, then `withSparkTimezone(options)` cookie write + server-platform guard. Update `guide-dates-and-sorting.md`.
- [x] M8 carry-over: the bounce pipe maps the endpoint's answer to its exit code (spike S-M5b). Spikes and deviations in PRD §4.1 (M9).

### M10 — ng-spark / ng-spark-auth UI
- [x] ng-spark core: `SPARK_DETAIL_PANELS` token (+ list/detail action slots) wired into `sparkRoutes()` pages.
- [x] `@mintplayer/ng-spark/soft-delete`: Deleted toggle, Restore/Purge. Consumes the server flags added in M7 (carried over from M6): `canRestore`/`canPurge`/`canViewDeleted` on the permissions endpoint, and the `deleted` flag on `/spark/po/load` so a row can be opened from the recycle bin.
- [x] `@mintplayer/ng-spark/history`: `<spark-po-history>` list, read-only view, diff, Revert.
- [x] ng-spark-auth `withAccount()` + 7 standalone pages, `SPARK_ACCOUNT_PROFILE_FIELDS`, `twitterProvider()`/`linkedInProvider()`, login label "Email or user name".
- [x] Carry-overs: bounce endpoint 503 when disabled; `RedirectTo` refused in Production; explicit transports + Development fail-closed + Mailpit AutoStart (owner decisions, PRD §3.10); spike S-TZ5 (five browser zones); profile `preferredCulture`. Spikes and deviations in PRD §4.1 (M10).
- [x] **Browser verification** (playwright_node MCP, on QnA, 2026-09-29): recycle bin → open → Restore/Purge; History view, diff and Revert; every account page, including 2FA with the QR code, recovery codes and deletion; Moderation (votes, flags, lock, suspension, review queue); DisableActions; drafts. It found and fixed 17 bugs; all passed on re-check. `/connect/login` wording is covered by a unit test only, because QnA hosts no identity provider.

### M11 — E2E base host extraction (D11, pure refactor)
- [x] Extract a generic host from `FleetTestHost` (embedded Raven, `dotnet run` under dotnet-coverage, seeded users, stale-bundle check, shared rate-limit awareness). **Run Fleet's E2E suite here and require green** — the one sanctioned mid-plan test run, because every later E2E test depends on it.
- [x] `sparkAuthGuard` waits for the session check (M10 bug); `SparkClient.RegisterAsync` sends the antiforgery token (M5 regression). Fleet E2E 104/105, the one failure is the machine's ETL-less Raven licence. Deviations in PRD §4.1 (M11).

### M12 — Moderation package (item 12)
- [x] Spikes **S-MOD-A, S-MOD-E** first.
- [x] `libs/moderation/MintPlayer.Spark.Moderation(.Abstractions)`; `moderation.json` as `IConfiguration` source + post-layering validation (D12, D14); `--spark-init-moderation`.
- [x] Votes (deterministic ids), ledger, map-reduce indexes + deployment assertion; privilege provider (composed, request-cached; suspension from a document).
- [x] Fraud defence 1–10 (caps and eligibility in the write path; `CreditableAfterUtc` + crediting Cron job; nightly detector; compensating reversals; IP-hash with rotated HMAC key + 90-day `@expires`).
- [x] Flags, review cases + queue, lock interceptor (spike **S-MOD-D**), suspend (spike **S-MOD-C**), revert/restore/purge wiring, audit log, new-account throttle (429), deletion handler.
- [x] `@mintplayer/ng-spark/moderation`: vote widget renderer, flag button, review-queue page, reputation badge, moderator panel.
- [x] Core: `SparkThrottledException` → 429; custom actions map a refused write to 400. Spikes and deviations in PRD §4.1 (M12). 42 moderation tests + 7 vitest pass.

### M13 — `apps/QnA` demo + E2E spec
- [x] Check the name against `.gitignore` (case-insensitive component match — the `Coverage` lesson).
- [x] App + Library projects (Questions, Answers, Moderation, SoftDelete, History, MailManager pickup mode, Identity), security.json grants from `--spark-init-moderation`, model sync, `securityPosture.txt`.
- [x] Add QnA to `pull-request.yml` in all three hand-written places: the build list (`:123`, `nx run-many --projects=DemoApp,HR,Fleet,CodeCoverage`), the model-sync loop (`:134`), and the security-posture loop (`:156`).
- [x] E2E tests on the extracted host: vote → delayed credit → privilege; serial-voting reversal; flag → review; lock blocks every write path; suspend; soft delete hidden everywhere + restore/purge; history diff + revert; account deletion. Written and compiled; **run in M14**. Deviations and two M12 findings in PRD §4.1 (M13).

### M14 — Housekeeping, full verification, PR
- [x] M13 findings fixed first: pending read live, reversals credited at once, `ISparkAccountDeletedHandler<TUser>` (QnA's goodbye mail after the delete), host coverage reports required only for hosts that started. See PRD §4.1 (M13, "Fixed in M14").
- [x] Guides: row policies + interceptors (incl. the documented D1 override gaps), SoftDelete, History, MailManager, throttling lanes, Data Protection (volume warning), forwarded headers + proxy recipes, DisableActions hook, wildcard removal, account pages, GDPR (revisions not rewritten), Moderation + fraud limits + legitimate-interest text.
- [x] Release notes: every breaking change (wildcards, DisableActions entry points, new startup guards, dedup-key format) — `docs/release-notes-preview-91.md`.
- [x] Version bumps on every touched lib + both npm packages; spike **S-PKG2** (`dotnet pack` output; PRD §4.1, MailManager.Abstractions README fixed); owner does **S-PKG1**.
- [x] **Full test sweep, all 5 test projects** (`MintPlayer.Spark.Tests`, `E2E.Tests`, `SourceGenerators.Tests`, `Client.Tests`, `CodeCoverage.Tests`) + vitest for ng-spark/ng-spark-auth + `--spark-verify-model` / `--spark-verify-security` for every app. The solution is `.slnx`. Final: 3198 / 120 / 417 / 105 / 1041 passed; vitest 760 + 216 + QnA 7; verify model + security exit 0 for all five apps. Failures and fixes in PRD §4.1 (M14).
- [ ] Open the PR (closes #283, #285, #299, #432, #460); reply on #460 re SocketExtensions; check CI, not only local.

---

### M15 — Sub-query selection & actions, Vidyano parity (D17–D19, PRD §3.15; owner decision 2026-09-29: same PR)
- [x] PRD D17–D19 + §3.15 recorded before coding; root cause of the "no actions on sub-queries" report investigated.
- [x] **D17** `SparkSelectionMode`, `SparkQuery.SelectionMode`/`ParentReference`, `SparkSubQuery` entries (bare string or object), consumers updated, startup + verify-model validation of `parentReference`.
- [x] **D18** default `New`/`Delete` catalogue entries (overridable in `customActions.json`, `isDefault` on the wire); `POST /spark/po/delete-many` (rule, 200 cap, row gate, `OnDisableActionsAsync`, interceptors, one `SaveChanges`); `SparkClient.DeletePersistentObjectsAsync`.
- [x] **D19** `/spark/po/new` sub-query context, `SparkNewArgs.ParentType`/`Query`/`ParentReference`/`FillParentReference()`, base auto-fill (owner refinement), `SparkClient` `queryId`.
- [x] ng-spark: grid toolbar model, selection bar + chip (select-all later removed by owner decision; deselect-all is the datatable header checkbox), row `⋮` menu, bulk Delete, New carrying the parent; card header (caption left, actions right); query-list page on the same model; detail page reads entries; create page via `/po/new`; case-insensitive `showedOn`.
- [x] QnA: Answers sub-query with checkboxes, soft bulk Delete, `DuplicateAnswer` (`=1`), New relying on the auto-fill; model sync + security posture.
- [x] Tests: .NET, vitest, QnA E2E (PRD §3.15). Docs: `guide-custom-actions.md`, `query-grid-card-PRD.md`, ng-spark README, release notes, AGENTS.md.
- [x] **Sub-query search box (owner, 2026-09-29, D17 addendum):** a search box on the right of the `<spark-query-card>` header, next to the actions, as in Vidyano. Reuse the query-list page's box (debounce, clear button) via a shared component so both surfaces behave identically. It feeds the card's existing `search` input, so the grid sends `Search` together with `parentId`/`parentType` to `/spark/queries/execute`, which already does server-side search (#210). Check that `/spark/queries/distinct-values` also takes the search, so a column filter's value list matches, and add it if it doesn't. Add vitest specs and a QnA E2E step. Done: `<spark-search-box>` shared with the query page; distinct-values did NOT take the grid search, so it gained `querySearch`; a new term clears the selection. See PRD §3.15 addendum.
- [x] Fix whatever CI run `a1b47012` breaks (expected: exact `/actions/list` counts, the old card-header order in specs, `SparkService` mocks without `newObject`, E2E selectors). Done: the .NET cases were outdated tests (defaults in the list, `SparkSubQuery` entries, the delete-many antiforgery position and README row); the 7 vitest suites + CodeCoverage spec were a real import-time TDZ in ng-bootstrap 22.19.0's dropdown fesm, fixed by moving the row menu to the CDK overlay; the po-create spec lacked `provideNoopAnimations()`; the E2E selectors matched the priority nav's three copies of each action.
- [ ] Full sweep: all 5 test projects, vitest (ng-spark, ng-spark-auth, QnA), verify model + security for every app. CI serves as the sweep, at the owner's instruction.
- [ ] Browser check on QnA: checkboxes, no select-all + the datatable deselect-all, the chip, rule-driven enabling, the `⋮` menu, New pre-filling the Question, the header layout, the search box (debounce, clear, Escape, chip cleared on search), the CDK row menu (position, outside click, Escape). On DemoApp, check that `company-cars` shows `CopyCarsToCompany` (no automated layer covers DemoApp; the vitest card spec covers a both-shown custom action on a sub-query).

## Pre-merge checklist (merge auto-publishes packages and redeploys coverage.mintplayer.com)

- [ ] CodeCoverage Data Protection: nothing to set on the VPS. The deploy pulls `apps/CodeCoverage/docker-compose.yml` from master, which now mounts the `dataprotection-keys` volume at `/var/lib/codecoverage/dataprotection-keys` and sets `Spark__DataProtection__KeysPath` to it (`ApplicationName=CodeCoverage` is in `appsettings.json`). Do **not** set `Spark__DataProtection__Storage` in the VPS `.env` — KeysPath + Storage together refuse startup. Expect every user to be signed out once on the first deploy. Once that deploy is healthy and a key file exists in the volume, the orphaned `DataProtectionKeys/…` documents (`KeyDocuments` collection) in the `Coverage` database can be deleted.
- [ ] CodeCoverage mail: nothing new is required on the VPS. The compose file now maps the SAME `.env` variables (`MAIL_HOST`, `MAIL_PORT`, `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME`) onto `Spark__Mail__Smtp__*` / `Spark__Mail__From__*`, sets `Spark__Mail__Smtp__Security=None` and `Spark__Auth__PublicBaseUrl=https://coverage.mintplayer.com`. With `MAIL_FROM_ADDRESS` unset the app still starts and sends nothing (as today). Secret names only if bounces are ever enabled: `SPARK_BOUNCE_SECRET` (relay) = `Spark__Mail__Bounces__Endpoint__Secret` (app); `MAIL_RELAY_PASSWORD` is unchanged.
- [ ] Verify what fronts coverage.mintplayer.com (and MintPlayer): if a CDN, add its ranges to `Spark:ForwardedHeaders:KnownNetworks`.
- [ ] Postfix `ALLOWED_SENDER_DOMAINS` / SPF cover any VERP domain in use.
- [ ] nuget.org prefix reservation + API key scope cover the new package ids (S-PKG1).
- [ ] Version diff reviewed: NuGet major 11, npm major 22 — nothing else moves the major.

## After this PR: absorb MintPlayer into this repository (decided 2026-09-28)

**Decision:** the MintPlayer website moves into `apps/MintPlayer`, the same way CodeCoverage was
absorbed (`docs/coverage_monorepo_*`). This happens as the **next step after this PR merges**, not
inside it. Until then MintPlayer consumes the published packages. Afterwards:

- The owner archives `MintPlayer/MintPlayer`, with a README pointing to `apps/MintPlayer`.
- The owner pins `MintPlayer.Spark` first on the organisation profile.
- Both are manual GitHub UI steps; there is no API for pinning.

Archive only once the code builds here. The trade-off is kept below so it doesn't have to be worked
out again.

- **For:** MintPlayer would use `ProjectReference` instead of NuGet, so a framework change would be
  checked against its biggest consumer in the same PR, with no publish-then-consume round trip and no
  cross-repo work. It also means one fewer repository in the organisation.
- **Against / costs:**
  - The organisation would no longer have a `MintPlayer` repository. Mitigation: archive it with a
    README pointing to `apps/MintPlayer`, and import its history with `git filter-repo`.
  - The fetcher might eventually have to come along too. That isn't forced: it can stay a NuGet
    dependency, like `MintPlayer.AspNetCore.SpaServices`.
  - Every `libs/` merge would redeploy **two** production sites, since `code-coverage-deploy.yml`'s
    path filter would be copied.
  - CI cost grows: `pull-request.yml` builds and verifies every app through hand-written lists and
    doesn't use Nx `affected` for apps.
- **How to do the import:**
  - Move only `MintPlayer.Web`, `MintPlayer.Domain` (plus `MintPlayer.Migration` while the cutover is
    pending) and their tests, with history via `git filter-repo`. Never move `legacy/`.
  - Check `apps/MintPlayer` and every imported path against `.gitignore` first. A folder named
    `coverage` would be silently untracked (the `Coverage` lesson).
  - MintPlayer's root `appsettings.json` / `docker-compose.yml` / `deploy/` belong under
    `apps/MintPlayer`, never at this repository's root.
  - Switch its `PackageReference`s to `MintPlayer.Spark*` into `ProjectReference`s.
  - Add it to the three hand-written lists in `pull-request.yml`, and consider building only affected
    apps there.
  - Add a deploy workflow only at the cutover. mintplayer.com still runs the legacy app, so importing
    redeploys nothing.
- **Timing:** after this PR merges, before the MintPlayer cutover. The cutover tasks listed below then
  land in this repository.

## For the MintPlayer cutover (done in `apps/MintPlayer` once it is imported; listed so nothing is lost)

- Count accounts whose user name contains a foreign `@` (D4) before cutover.
- Remove its ForwardedHeaders block (D15) and `RevisionsConfigurator` (T10); replace `EntityActions` soft-delete overrides with `ISoftDeletable`.
- Its Data Protection volume maps to `Spark:DataProtection:KeysPath`.
- MintPlayer production sets `Spark__DataProtection__KeysPath` to a directory on a mounted volume (its legacy site already persisted keys to a folder, per the line above), and sets an explicit, stable `Spark:DataProtection:ApplicationName` so existing cookies keep decrypting. No `Storage` in its base `appsettings.json`; its test hosts set `Storage=RavenDb` (PRD D5 owner decision).
- `people_overview` / `subjects_search` indexes must emit `IsDeleted` for push-down.
- Call `withSparkTimezone()` in `app.config.ts` (it registers only `withSparkAuth()` today, S-TZ2), so the
  browser writes the `spark-timezone` cookie the server resolver reads.
- Format dates in the SSR render in .NET (`OnSupplyData`), not in the Node render: Angular's `formatDate`
  / `DatePipe` ignores IANA ids, so a Node render uses the Node process's zone whatever the cookie says
  (S-TZ2).
