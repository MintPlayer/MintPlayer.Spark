# Plan — Absorb WebhooksDemo's project automation into `apps/CodeCoverage`

Companion to [coverage_project_automation_PRD.md](coverage_project_automation_PRD.md). Constraint
labels (C1…C10), requirements (FR1…FR10), decisions (D1…D3) and risks (R1…R8) refer to that file.

**One pull request.** Everything here — the migration, the deletion, the docs repointing, the
defects in §6 of the PRD — lands together. No follow-up PR, no phase 2.

**Test discipline.** Verify intermediate milestones by reading code and building/type-checking. Run
the test suites **once**, in the M12 sweep. Committing per milestone is expected; test *runs* are
batched.

---

## Status (2026-09-07)

**The messaging prerequisite has LANDED** on this branch — one shared subscription, in-process
per-queue lanes, durable claims. Two consequences for this plan, which is otherwise as written:

- **C1 is lifted.** A queue name no longer costs a RavenDB data subscription, so **M4 may take its
  own queue name** instead of sharing `spark-github-all`, and should: project automation then gets
  its own FIFO lane, its own retry and dead-letter state, and a slow board reconciliation cannot
  delay coverage feedback. The `IRecipient<GitHubWebhookMessage>`-sibling shape still stands (a
  sibling, not an extension of `GitHubEventsRecipient`'s switch) — what changes is only that its
  `[MessageQueue]` may be new.
- **M4's verify step is obsolete as written.** `grep -rn "MessageQueue" apps/CodeCoverage` showing
  "only the two existing constants" was the guard for a constraint that no longer exists. The
  property worth checking now is ordering, not count: does this workload need to be serialised with
  check-run publishing, or isolated from it? Isolated, hence its own queue.
- **R1 is downgraded.** "A new queue name slips in and kills a queue" is no longer a failure mode.
  `CoverageQueuesTests`' count and exact-name facts are already deleted; the equivalent guard is now
  `MessageSubscriptionManagerLifecycleTests`, which asserts one subscription however many queues
  exist.

### Milestone status

| | Status |
|---|---|
| **S1** typed Projects-V2 listing | **Done, green** — see "S1 — RESULTS" below. Typed builder works for both owner shapes; cost 1 point for `first: 100` |
| **S2** inline rules editor | **Retired, not run** — `HR/Person.json` already ships `editMode: "inline"` on an `AsDetail` array, so the mechanism was proven in-tree and needed no spike |
| **M0** branch + pinned facts | **Done.** `Octokit.GraphQL` pinned explicitly on `CodeCoverage.csproj` (R6); `CoverageQueues` doc rewritten |
| **M1** entities | **Done.** `GitHubProject`, `ProjectColumn`, `EventColumnMapping`, `WebhookEventType` (18 keys — the PRD's "19" was a miscount) |
| **M2** registration + row security | **Done.** `CoverageSparkContext.GitHubProjects`, `GitHubProjectActions`, `GitHubProjectVisibility` (no public tier, one rule, filter applies to writes) |
| **M3** discovery by reconciliation | **Done.** `IInstallationProjects` seam; folded into `GitHubStateReconciler` so the fail-closed rule is shared code. Also refreshes **columns** for automation-enabled boards (C11) |
| **M4** webhook routing | **Done.** `ProjectAutomationRouter` re-publishes onto its **own** queue (`ProjectAutomationQueue`), C1 being lifted; self-authored guard on `ProductionAppId` |
| **M5** card movement | **Done.** `GitHubProjectCards`, null-stripping workaround kept and explained |
| **M6** all 18 mappings | **Done**, including the three previously-inert handlers and the merged/review payload disambiguation |
| **M7** model + rights + menu + action | **Done — but the earlier "both gates pass" claim was wrong.** Running the app refused to boot: `QueryRead/ProjectColumn` granted with no row rule, which the startup gate rightly treats as indistinguishable from an unscoped collection. Fixed with `ISparkOwnsRowSecurity`. Three more only-visible-at-runtime gaps: `GetGitHubProjects` declared no `alias`, so it derived `githubprojects` while the program unit routed to `github-projects`; `EventColumnMapping` needed a type-level grant to render its nested form at all; and `Read/LookupReferences` was granted nowhere, so the event dropdown 404'd |
| **OD3** target-column picker | **Done** — `[Reference]` + parent-scoped `Custom.*` query. See the PRD's OD3 row and `docs/guide-reference-attributes.md` |
| **M8** account sub-query | **Done.** `GitHubProjectActions.Account_Projects` + a `Custom.Account_Projects` query declared on `Account` (`account-projects`). A `Custom.*` source is mandatory here, not stylistic — a `Database.*` source is refused for a *declared* sub-query. Filtered on the account **document id** rather than `OwnerLogin`, so a GitHub rename cannot silently empty the list |
| **M9** delete `apps/WebhooksDemo` | **Done.** 70 files removed (69 app + `webhooks-demo-deploy.yml`). Config updated: `MintPlayer.Spark.slnx`, `package.json` workspaces, `.github/dependabot.yml` (its whole `docker` ecosystem entry existed only for this image), `pull-request.yml` (build matrix + both project loops), `CLAUDE.md`, `README.md`, two lib READMEs. Solution **builds clean** without it. ⚠️ Two corrections to the inventory: the `ng-spark-auth` spec reference is a **comment**, not a dependency, so it never blocked anything — and `npm install` does **not** prune removed workspaces from `package-lock.json`, which is why four stale `Demo/*` entries from the earlier folder rename are still there; the two for this app were removed by hand |
| **M10** docs | **Done for the guides that describe current state.** Rewrote the webhooks README's board-automation section against the CodeCoverage implementation (new file table, real document shape, and the two design points worth copying); repointed `guide-docker-deployment.md`, `guide-authentication-schemes.md` and `guide-nx-remote-cache.md`. Historical PRDs, plans, release notes and build logs **keep** their references deliberately — rewriting those would make them lie about what happened. Earlier: `guide-row-security.md`, `guide-reference-attributes.md` |
| **M11** tests | **Partial.** Framework-level tests done: `FailOpenRegressionTests` updated for the narrowed sub-query guard plus a companion for the option-source case, and two `ng-spark` specs pinning that AsDetail rows reach the client and that `objects: []` survives as emptiness. Also **fixed six pre-existing failures** in `GitHubStateReconcilerTests` — `IInstallationProjects` was added to the reconciler in M3 and never registered in that harness, so every test in the class failed on construction. **Now also covers the automation feature's two silent decisions** — `ProjectAutomationTests`, 13 facts over the loop guard and event-key disambiguation, both widened to `internal` with `InternalsVisibleTo` rather than driven through a webhook round trip (a full-path test would pass with either defect present, because both look exactly like "no board matched"). **One of them found a live bug**: `IsPerformedByUs` read `performed_via_github_app` from the payload **root**, but GitHub hangs it off the resource (`comment`, `issue`, `pull_request`), so the loop guard was **inert for every event except `check_run`** — it could not recognise a comment this app had just posted. Fixed to check the resource first and the root last. Still not covered: card movement against a GraphQL fake |
| **M12** single sweep | **Done.** `MintPlayer.Spark.Tests` **1984 passed / 0 failed**; `ng-spark` **409 passed / 0 failed**. `CodeCoverage.Tests` was **359 / 6** until the harness fix above — the plan's previously recorded "365 / 0" predates M3 and was stale when quoted. ⚠️ And "365 / 0" was itself misreported: that run **exited 1** on a fixture *Test Class Cleanup Failure*, which xUnit keeps out of the summary line entirely. Read the exit code, never the summary alone — the fixture's teardown is now guarded so a disposal race cannot fail the build. ⚠️ The first sweep ran `dotnet test` and vitest **concurrently** with the app host up and produced three unrelated failures, all the documented CPU-starvation teardown flake; suites must be run sequentially, host killed, before a red result means anything |
| **M14** correct the two flags' semantics (C15) | **Done, green (2026-09-08).** `AutoAddToBoard` (default `true`) replaces `AddLinkedIfMissing` and is read at all four `addIfMissing` sites — the issue path (was hard-coded `true`), the PR and `check_run` paths (both `false`), and the linked-issue loop. Model JSON renamed with all three descriptions rewritten (nl/fr by hand — synchronize preserves them, so stale text would have survived); `--spark-synchronize-model` re-run and **verified a fixed point** (second run diffs clean), `modelHashes.json` re-stamped. Migration `M_202609081200_RenameAddLinkedIfMissing` patches stored rules, writing `true` only where the member is absent so a retry cannot overwrite a later user choice — and backfills `MoveLinkedIssues` too, because the development database showed that trap had **already fired**: the one configured rule predates #377, carries neither member, and has therefore been loading as `MoveLinkedIssues = false` since. `CodeCoverage.Tests` **387 passed / 0 failed, exit 0**. **Verified live** (`dotnet run` + browser, 2026-09-08): migration applied at startup, the stored rule now reads `AutoAddToBoard: true` / `MoveLinkedIssues: true`, the board's rules row opens with both boxes ticked and the column picker resolving `98236657` → "Done", and every renamed label renders in en/nl/fr with 0 console errors. Test note: `A_pull_request_event_never_adds_the_card` was **deleted** — it asserted the removed policy — and replaced by both directions of the flag, plus the issue-off case and a `check_run` case (that third hard-coded site had no coverage at all) |
| **M15** labels + tooltips (owner request) | **Done, green (2026-09-08).** 38 labels and 35 descriptions shortened in all three languages; tooltip English cut from 20,296 chars to 7,473 by moving rationale into `<remarks>`. Presentational, so the model hash is unchanged. Both passes hit the same trap — synchronize preserves `fr`/`nl`, so 46 translations were left stale and had to be rewritten. Framework guidance added to two guides. See the M15 section below |
| **M16** drop the top-level boards unit, head and reorder the sub-query (owner request) | **Done (2026-09-08).** Closes OD2 the other way — `Account`'s `account-projects` sub-query only. The `GitHub` program-unit group is removed entirely, the sub-query gained the `description` that renders as its heading ("Project boards", translated), and `Name` moved to the front of the column order. `GetGitHubProjects` stays declared and granted for direct links. Two mechanism traps, both documented in guides: `programUnits.json` **is** part of the model hash unlike labels/descriptions/order (needed a re-stamp; hash now `31725c97…`), and `order: 0` would have been silently reverted, so `Name` takes `1`. Both verifies exit 0 |
| **M13** manual verification | **Done.** Live webhook verification recorded in PRD §3b; the form has now been opened end to end. A rule saved as `PullRequestMerged` → `TargetColumnOptionId` `"98236657"` — the GitHub **option id**, not the name — from a picker offering only that board's own columns, so `[Reference]` + a parent-scoped `Custom.*` query is confirmed working from an embedded `AsDetail` row, and renaming a column on GitHub cannot break a saved rule. `SyncColumns` verified populating the grid in place. Four defects surfaced only by doing this — see M7 and the framework fixes |

### M9 inventory (measured, 2026-09-07)

The plan's original estimate of "11 references" was low by roughly six times.

- **69** tracked files under `apps/WebhooksDemo`.
- **73** files reference it from outside: **11** code/config, **62** docs.
- Load-bearing: `MintPlayer.Spark.slnx:25-27`, `package.json:17` (+ `package-lock.json`),
  `.github/dependabot.yml:27`, `.github/workflows/pull-request.yml:115,126,148`,
  `.github/workflows/webhooks-demo-deploy.yml` (delete), `CLAUDE.md`, `README.md`,
  `libs/webhooks/.../README.md`, `libs/authorization/.../README.md`.
- ⚠️ **`libs/node_packages/ng-spark-auth/sign-in/src/spark-sign-in.projection.spec.ts` references it** —
  so deleting the app breaks a **client package test** in the vitest run, not the .NET suites.
- The 62 doc hits need triage, not a sweep: the four *guides* using it as a worked example must be
  repointed, but historical PRDs and build logs describing past work should keep their references —
  rewriting those makes them lie about what happened.

⚠️ **M13 still needs credentials** for anything beyond what PRD §3b already covers.

---

## Spikes first

Two unknowns are load-bearing enough that guessing wrong would rework several milestones. Both are
throwaway — nothing from a spike is committed.

### S1 — Typed `Octokit.GraphQL` listing of Projects V2 for an installation

**Question:** can boards for an account be listed through the typed `Octokit.GraphQL` query builder
over `IGitHubInstallationService.CreateGraphQLConnectionAsync(installationId, clientType)`, and what
does the node cost look like for an org with many boards?

WebhooksDemo never did this — its board listing was a **raw interpolated string** in a controller
using the *user's* token (B1), while the typed builder was used only for the columns fetch
(`GitHubProjectService.cs:27-43`). FR3 needs the typed builder against an *installation* token, which
is code that does not exist yet.

**Method:** a scratch console/xUnit fact against CoverageDevelopment's installation on the
MintPlayer org. Build `organization(login:).projectsV2(first:100)` with the typed builder, run it,
print ids/titles/numbers and the `rateLimit` cost. Try a user-owned account too — the old raw query
branched `organization|user`, so the typed path needs both.

**Decides:** the shape of FR3's reconciler extension, whether `.AllPages()` is safe here (it is used
for columns and its node cost grows with project size), and whether the 1024/30-request bounding
that repositories use transfers as-is.

**Kill criterion:** if the typed builder cannot express it, fall back to a parameterized (never
interpolated) raw GraphQL document with variables — and record that in the PRD rather than shipping
B1 again.

#### S1 — RESULTS (run 2026-09-07 against the CoverageDevelopment app, id 4567511)

**GREEN. Kill criterion not triggered — the typed builder expresses it, so B1's interpolated GraphQL
can be avoided outright rather than merely parameterized.**

- **Both owner shapes work.** `new Query().Organization(Var("login")).ProjectsV2(first: 100).Nodes`
  and the `.User(...)` equivalent both compile and run, with the login passed as a **GraphQL
  variable**. The raw query being replaced branched `organization|user`; the typed path needs the
  same branch, because the two are different root fields and no common interface covers them.
- **Cost is 1 point** for `first: 100`, on both shapes.
- The app sees **two installations**: `159465567` (user `PieterjanDeClippel`) and `153539364`
  (organization `MintPlayer`). The user installation has **0 boards**; the org has **1**
  (`PVT_kwDOAug2bM4AthJv`, #1). So M3 must handle both owner types and a zero-board account, which
  is the common case rather than an edge one.
- **Rate-limit budgets differ per installation** — 11,800 for the user, 5,050 for the org. Any
  bounding must be per-installation, not a single global number.

**Methodological correction, recorded because the first measurement was wrong.** `rateLimit.cost`
reports the cost of *the query it appears in*. Asking for it in a separate query measures the
rate-limit query itself and says nothing about the listing — the first run reported "cost=1" that
way, which was accidentally the right number for the wrong reason. The figure above comes from
selecting `projectsV2` and `rateLimit` in **one** document.

**Two questions S1 was meant to answer that remain open, and cannot be closed here:**

1. **Node cost for an org with many boards, and whether `.AllPages()` is safe.** There is exactly
   one board in the whole account set, so there is nothing to page. Cost 1 for `first: 100` suggests
   a Projects-V2 connection is flat-cost per page — i.e. `.AllPages()` ≈ 1 point per 100 boards —
   but that is **inference from a single-board sample, not a measurement.** Treat `first: 100`
   without paging as sufficient until an account actually exceeds it; a board count above 100 per
   owner is not a realistic shape for this app.
2. **Composition with the framework's token-refreshing connection.** The spike mints the JWT and
   installation token itself, because `GitHubInstallationService` is `internal` and registered only
   through the full `ISparkBuilder` graph. So `CreateGraphQLConnectionAsync`'s refresh handler is
   **unexercised** by this spike. Its REST twin is proven in production, and M3 uses the same
   connection every other GraphQL call in the app already uses, so the risk is low — but it is not
   zero and it is not tested here.

### S2 — Generic UI renders the rules editor with zero client code

**Question:** does an `AsDetail` + `isArray` + `editMode: "inline"` collection with a
`lookupReferenceType` column actually render editable **in CodeCoverage's SPA**, given that app
registers 11 attribute renderers and has no `editMode: "inline"` anywhere today?

The evidence says yes (`po-form/src/spark-po-form.component.html:53-262`, lookup options loaded for
AsDetail child columns at `spark-po-form.component.ts:311-313`, lookup discovery by assembly scan),
and WebhooksDemo registered *zero* renderers — but that was a different SPA with different
providers, and "renders in one app" is not "renders in this one".

**Method:** on a throwaway branch, add a minimal `AsDetail` array + a two-key
`TransientLookupReference` to an existing CodeCoverage type, synchronize the model, `dotnet run`, and
open the detail page. Confirm add/delete rows, the lookup dropdown, and a boolean checkbox.

**Decides:** whether M7 is a model-only milestone or needs a renderer, and settles OD4.

**Kill criterion:** if inline editing does not render, drop `editMode: "inline"` (OD4 → no) and use
the modal per-row editor, which is the default path in the same template.

---

## Milestones

### M0 — Branch, and pin the facts

- Branch `feat/coverage-project-automation` off `master`.
- Fix B7 now, while it is cheap: `apps/CodeCoverage/CodeCoverage/Feedback/CoverageQueues.cs:8-9`
  says "AGPL/open-source licence" where the server holds a registered **Community** licence, and
  `:18` says "previously declared five" where it was seven with five dead. Doc-only.
- Add `Octokit.GraphQL` as an explicit `PackageReference` on `CodeCoverage.csproj` (R6) — it arrives
  transitively from the webhooks lib today, and a **beta** pin should not be implicit.

**Verify:** solution builds.

### M1 — Entities (FR1)

- `CodeCoverage.Library/Entities/GitHubProject.cs`: `[GenerateIndex]` (**never** with a type
  argument), `static DocumentId(...)` deriving the id from the GitHub node id so upserts are
  idempotent, `OwnerLogin`, `InstallationId`, `NodeId`, `Number`, `Name`, `StatusFieldId`,
  `Columns`, `EventMappings`, `AutomationEnabled`, `DeleteBranchOnPrClose` (D3, default false),
  `Connection` + `DisconnectedReason` mirroring `Repository`.
- `Entities/ProjectColumn.cs`, `Entities/EventColumnMapping.cs` — embedded value objects, `Id` from
  the option id / a stable key.
- `LookupReferences/WebhookEventType.cs` — all 19 keys (FR9), namespaced to CodeCoverage.
- Every property gets a `///` summary — the description generator runs in `CodeCoverage.Library`
  because that is where the summaries live. Apply `[IgnoreForIndex]` to anything that must not
  become a queryable grid column (opt-out, not opt-in).

**Verify:** builds; read the generated index/projection names.

### M2 — Registration, row security (FR2)

- `IRavenQueryable<GitHubProject> GitHubProjects` on `CoverageSparkContext`.
- `Actions/GitHubProjectActions.cs : DefaultPersistentObjectActions<GitHubProject>` — the class name
  must keep the generic argument or `ActionsResolver` throws.
- `Services/GitHubProjectVisibility.cs` beside `RepositoryVisibility`: an expression
  `p => p.OwnerLogin.In(owners)` plus an imperative twin so the two forms cannot drift. **`In()` not
  `Contains`**; **`!= Disconnected` not `== Connected`**. No public tier.
- `GetRowFilterAsync` pulls owners from `ISparkVisibility` (per-request memoized — hooks run up to
  3× per read plus per row for redaction).

**Verify:** builds; re-read the filter against `RepositoryVisibility`'s two documented constraints.

### M3 — Board discovery by reconciliation (FR3, uses S1)

- `Services/GitHubProjectDiscovery.cs` (or extend `GitHubStateReconciler`): list Projects V2 per
  account through the typed builder over `CreateGraphQLConnectionAsync`. Upsert reachable boards,
  `Disconnect` vanished ones, **never delete**, and **fail closed on anything but a definitive
  404/401** — copy the reconciler's most dangerous line deliberately, not by accident.
- Bound it as repositories are bounded, respecting the 30-request session budget; mutate the
  caller's session and **do not save** (the cron job and `ResyncAction` own the save).
- Wire into `ReconcileGitHubStateCronJob` (`20 3 * * *`) and `ResyncAction`, so both the nightly
  sweep and the existing manual button pick up boards for free (D1 objection 2).

**Verify:** builds; trace that no `SaveChangesAsync` was added inside the per-account path.

### M4 — Webhook recipient on the existing queue (FR5, FR7, C1)

- `Recipients/ProjectAutomationRecipient.cs : IRecipient<GitHubWebhookMessage>` — a **sibling** of
  `GitHubEventsRecipient`, not an extension of its switch (independent retry/dead-letter state), and
  **not** `IRecipient<GitHubWebhookMessage<T>>` (that mints a queue name per closed generic, R1).
- Switch on `message.EventType`, deserialize `EventJson` locally: `issues`, `pull_request`,
  `pull_request_review`, `issue_comment`, `check_run`.
- FR7: query boards by owner/repository through the generated index filtered on
  `AutomationEnabled && Connection != Disconnected`, and **return before any GitHub call** when no
  board maps the event. This replaces B2's unpaged `ToListAsync()` per delivery.

**Verify:** `grep -rn "MessageQueue" apps/CodeCoverage` shows only the two existing constants.

### M5 — Card movement service

- `Services/GitHubProjectService.cs` — the Projects-V2 GraphQL work, migrated not copied: typed
  builder throughout, the `RunCleanedUp` null-stripping workaround kept (ugly but load-bearing),
  `catch (Exception ex) { throw; }` noise removed (B5).
- Port `HandleIssuesEvent` / `HandlePullRequestEvent` *logic*: event → mapping key, add-or-move the
  card, and `MoveLinkedIssues` resolving `closingIssuesReferences`.
- **Two flags, two orthogonal questions** (C15, and the PRD's decisions 5–7): `MoveLinkedIssues`
  says *which* items the rule moves — the event's subject, or its linked issues **as well**;
  `AutoAddToBoard` says what happens when **any** of those items is not on the board yet. The
  add-or-skip decision is one flag read at *every* call site, the event's own subject included; it
  is **not** "add linked items". Never re-derive it from the item's type.

**Verify:** builds.

### M6 — The three previously-inert handlers, and the loop guard (FR9, FR6, C10, R2)

- Implement `pull_request_review` (approved / changes_requested / dismissed), `issue_comment`
  (created) and `check_run` (completed), so all 19 mappings work.
- **FR6 is a correctness requirement, not polish.** This server creates check runs
  (`PublishFeedbackRecipient`) and posts PR comments (`PullRequestCommentGateway`), so it receives
  deliveries caused by its own writes. Drop those before evaluating any mapping — by app id on the
  check run, and by the existing bot-author test on comments. Unguarded, publishing coverage
  feedback moves a card, which publishes more feedback.
- D3: `DeleteBranchOnPullRequestClose` logic folds into the `pull_request` closed path, gated on
  `DeleteBranchOnPrClose`, still skipping forks (head repo id ≠ base repo id), tolerating
  `NotFoundException` and warning on 422 (protected branch).

**Verify:** builds; re-read each handler for an unguarded self-authored path.

### M7 — Model + menu + rights (FR4, FR8, uses S2)

- Run the `Synchronize` launch profile (`--spark-synchronize-model`), committing
  `App_Data/Model/GitHubProject.json`, the two value-object files and `App_Data/modelHashes.json`
  **in the same commit** (C8, R4).
- Hand-edit the generated model for presentation: `AutomationEnabled` / `DeleteBranchOnPrClose` as
  booleans, `EventMappings` as `AsDetail` + `isArray` + `editMode: "inline"` (OD4) with
  `lookupReferenceType` on the event column, `TargetColumnOptionId` promoted to a lookup over the
  board's cached `Columns` (OD3). Re-synchronize afterwards to re-stamp hashes.
- `App_Data/programUnits.json`: a new `GitHub` group with the boards unit (OD2 — plus the Account
  sub-query in M8). `_comment` **is** safe in this file (it is an object), unlike
  `customActions.json`.
- `App_Data/security.json`: new rights from the next free block
  `c0e5a9e1-0000-4000-8000-000000000071`, **authenticated only** — `anonymous` is not "Everyone".
  Remember `QueryRead` expands to `Query` + `Read`, and `Read` implies `Query` for grants.
- `App_Data/customActions.json`: the `SyncColumns` entry. Actions attach **by right, not by
  declaration**, so `SyncColumns/GitHubProject` is the only thing scoping it. Do not add a
  `_comment` key here — it is a flat map and an unknown key deserializes as an action and fails.
- `CustomActions/SyncColumnsAction.cs : SparkCustomAction`, ending in `Notify` + `RefreshQuery(alias)`.
  Every exit says something — a silent `return` is indistinguishable from success in the browser.

**Verify:** `--spark-verify-model` and `--spark-verify-security` exit 0.

### M8 — Account page integration (OD2)

- Declare the per-account board sub-query beside `account-repositories`: `Custom.GetBoards(args)`
  scoped by `args.Parent!.Id`, its alias listed in `Account.json`'s `persistentObject.queries`.
- Return a bare `IQueryable`/`IEnumerable`, **never `SparkQueryPage<T>`** — C5: a query that owns its
  own paging is not re-executable, so custom actions on its rows silently find nothing.

**Verify:** `--spark-verify-model` exits 0.

### M9 — Delete `apps/WebhooksDemo` (FR10)

- `git rm -r apps/WebhooksDemo` and `.github/workflows/webhooks-demo-deploy.yml`.
- `MintPlayer.Spark.slnx:25-27`; `package.json:17`; `.github/workflows/pull-request.yml:91,115,126,148`
  (build list, both verify loops, the "all five apps" wording); `.github/dependabot.yml:26-30`.
- C9: confirm nothing added in M1-M8 needs a new entry in `Dockerfile:21-35` or
  `code-coverage-deploy.yml:26-46` — and add it to **both** if it does (R5).

**Verify:** `git ls-files | grep -ci webhooksdemo` returns only `docs/` prose; solution builds; a
clean `npm install` from the repo root succeeds.

### M10 — Docs (R8)

Per PRD §7: `README.md:140`, `CLAUDE.md:20`, `libs/webhooks/.../README.md` (drop the "Project board
automation" section, repoint paths/ports), `libs/authorization/.../README.md:387`,
`docs/guide-docker-deployment.md` (rebuilt on `apps/CodeCoverage`'s compose), 
`docs/guide-nx-remote-cache.md:51-63`, `docs/guide-row-security.md:38` (the worked example is now a
*real* row filter, which is a better example), `docs/guide-authentication-schemes.md:440,473`,
`libs/node_packages/ng-spark-auth/sign-in/src/spark-sign-in.projection.spec.ts:104`.

Leave `libs/webhooks/**` itself — CodeCoverage and `tests/MintPlayer.Spark.Tests` depend on it.

### M11 — Tests

Extend, don't invent infrastructure. `CoverageWebAppFactory` boots the real `Program` so
`[SparkAuthorize]` filters actually execute; `CoverageRavenTest` is the single place
`RavenTestDriver.ConfigureServer` may be called. Seams already exist:
`TestGitHubAccessService`, `TestRepositoryResolver`, `ScriptedDiffService`.

- `Recipients/ProjectAutomationRecipientTests.cs` — one case per event type (FR9: all 19 mappings),
  beside the existing `GitHubEventsRecipientTests` (7) and `GitHubRepositoryLifecycleTests` (21).
- **FR6/R2:** a self-authored `check_run` and a self-authored `issue_comment` delivery move no card.
- **FR7:** no GitHub call when no board maps the event.
- **Row security:** a caller who does not manage an owner sees none of its boards and cannot mutate
  them, through `CoverageWebAppFactory`.
- **FR3:** a transient GitHub failure disconnects nothing (fails closed).
- Queue guards stay green — `CoverageQueuesTests` (3) and `DeleteDataActionTests:81-89` (R1).

### M12 — The single test sweep

Now, and only now: `dotnet test` for `CodeCoverage.Tests` and `tests/MintPlayer.Spark.Tests`, the
client vitest run, and `nx run-many --target=build` over the four remaining apps.

Read the numbers, not just pass/fail: per the flake write-ups, zero failures on an idle machine
proves nothing, and a jsdom `_namespaceURI` rejection means a fixture lifetime problem, never a
reason to re-run until green.

### M13 — Manual verification against CoverageDevelopment

- `dotnet run` the app (**never** `ng serve` alongside — the host spawns `npm start` itself; wait
  for the dev server's own `➜ Local:` line, not `Now listening on:`).
- Sign in, open the boards unit, confirm a board appears after a Resync, enable it, add a rule, save,
  disable it, and confirm **the rules survive** (D1 / B4).
- Trigger a real `issues` and a real `pull_request` event through the dev tunnel and watch a card
  move.
- Confirm publishing coverage feedback moves no card (FR6).

---

## M14 — Correct the two flags' semantics (C15)

**Why this milestone exists:** the flags shipped in #377 were built against a misreading — see C15 in
the decision register and decisions 5–7 in `docs/prd/PRD-ProjectBoardAutomation.md`, which are now
the spec. `MoveLinkedIssues` is behaviourally right and only needs its documentation tightened
("as well as", not "instead of"). The recruitment flag is wrong in **name, scope and default**.

- **`EventColumnMapping.cs`** — replace `AddLinkedIfMissing` with
  `public bool AutoAddToBoard { get; set; } = true;`. Rewrite the doc comment: it governs the
  event's own subject *and* linked issues, and the old comment's whole "asymmetry with a direct
  issue event is deliberate" paragraph is now obsolete, not just reworded. Tighten
  `MoveLinkedIssues`'s comment to say the linked issues move *in addition to* the PR's card, and
  keep the "ignored for issue events" sentence — GitHub models no issue→issue closing link, so that
  part is correct, not a limitation to fix.
- **`ProjectAutomationRecipient.cs`** — every `addIfMissing:` argument becomes
  `rule.AutoAddToBoard`. Four call sites: `MoveIssueAsync` (was hard-coded `true`),
  `MovePullRequestAsync` (was hard-coded `false`), `MoveCheckRunPullRequestsAsync` (was hard-coded
  `false`), and the linked-issue loop in `MoveLinkedIssuesAsync` (was `rule.AddLinkedIfMissing`).
  Delete the two long comments justifying the type-aware policy — the policy is gone, and a comment
  arguing for it would outlive it.
- **`App_Data/Model/EventColumnMapping.json`** — rename the attribute and rewrite all three
  descriptions (`en`/`nl`/`fr`). **Read the nl and fr text back as a spec review before
  committing**; the Dutch text is what surfaced this defect, and it is the cheapest place to catch
  the next one. Re-stamp `modelHashes.json` with the model file (C8, R4).
- **Migration.** A stored `AddLinkedIfMissing: false` must not silently become
  `AutoAddToBoard: false` — the two mean different things and the new default is `true`. Since the
  old field only ever governed linked-issue recruitment and shipped days ago, the intended
  behaviour for existing rules is the new default. Drop the old field and let `AutoAddToBoard`
  default: decide explicitly (a `MintPlayer.Spark.Migrations` patch, or accepting the absent-field
  default) and record which, because "absent JSON field ≠ `false`" cuts both ways here — an absent
  bool deserialises to `false`, **not** to the property initialiser's `true`, for documents written
  before the rename. **This is the one part of M14 that can be wrong silently.**

  **Decided: a migration, and it backfills BOTH booleans.** `M_202609081200_RenameAddLinkedIfMissing`
  patches every `GitHubProjects.EventMappings` element, writing `true` only where the member is
  absent (so a retry cannot overwrite a later user edit) and dropping `AddLinkedIfMissing`.
  `MoveLinkedIssues` is in scope because reading the development database found the trap had
  **already fired once**: the single configured rule predates #377 and carries neither member, so
  it has been loading as `MoveLinkedIssues = false` and the linked-issue movement #377 shipped has
  never run for that board. #377 added the field with a `true` default and no migration — the
  default only ever applies to newly constructed objects.
- **`ProjectAutomationMovementTests.cs`** — the `addLinkedIfMissing` parameter becomes
  `autoAddToBoard` (default `true`). Add the facts the old shape could not express: a
  `PullRequestOpened` rule with `AutoAddToBoard = true` **adds the PR** (previously impossible), and
  the same rule with it off adds nothing at all — neither the PR nor its linked issues. Keep
  `A_ready_for_review_pull_request_moves_its_linked_issues` and the "must not even ask GitHub"
  assertion for `MoveLinkedIssues = false`.
- **UI check.** The rules editor is generated from the model JSON, so the new label and description
  arrive for free — but confirm the inline `AsDetail` row renders the renamed attribute (the type
  needs its type-level grant, per M7), and that an existing board's saved rules still open.

**Verify:** builds; `grep -rn "AddLinkedIfMissing" apps/ docs/` returns no *live* use — only the
migration that drops the field and the comments that say why it is gone.

## M15 — Labels and tooltips (owner request, 2026-09-08)

Presentational only: no hash change, no test impact, and `--spark-verify-model` stays green
throughout. Both halves have the same trap, which is the reason they are one milestone.

- **Labels.** 38 shortened across nine model files, in all three languages. Generator artifacts
  fixed (`Git Hub Id`, `Ci Run Id`, `Is Private`), the `At Utc` suffix dropped throughout —
  following the precedent already in the model, where `CreatedAtUtc` was hand-labelled "Created" —
  and redundant type nouns removed (`Event Name` → `Event`). Two needed a decision rather than a
  trim: `TargetColumnOptionId` → "Target Column" (the user picks a column; the id is persistence),
  and `Repository.LatestCoverageAtUtc` → "Last Measured", because `LatestCoverage` beside it is
  already labelled "Coverage" and the trimmed name would have read as the value.
- **Descriptions.** 35 summaries condensed by moving the rationale into `<remarks>`, which the
  description generator ignores — nothing was deleted from the source. English went from 20,296
  characters (mean 156, worst case 2,097) to 7,473 (mean 57, nothing over 130).
- **The trap, both times:** synchronize preserves `fr`/`nl`. Shortening `en` alone leaves the other
  languages carrying the old text, invisibly — 46 descriptions needed rewriting after the summary
  pass. Guidance added to `guide-translated-strings.md` (labels) and
  `guide-attribute-descriptions.md` (the `<remarks>` split, and the length warning).

**Verify:** no `fr`/`nl` much longer than its `en`; snapshot the model directory, re-synchronize,
`diff -r` clean; `--spark-verify-model` exits 0 with an unchanged hash.

## M16 — Drop the top-level boards unit (owner request, 2026-09-08)

Closes OD2 the other way: the sub-query on `Account` only. See the OD2 rows in the PRD and the
decision register.

- **`programUnits.json`** — the whole `GitHub` group goes, since `Project boards` was its only unit.
  The reasoning is recorded in the file's own `_comment`, beside the existing note about why there
  is no `url` unit for the GitHub App link: boards are reached from the account that owns them,
  which is where a user already is and which scopes the list for free.
- **`Account_Projects` gains a `description`** — "Project boards" / "Tableaux de projet" /
  "Projectborden", the translations the removed unit carried. A query's `description` is what
  renders as its heading (`spark-query-list.component.html:28`, falling back to `name`);
  `SparkQuery` has **no `label`** field, unlike an attribute or a column. Without this the
  sub-query on the Account page showed the raw name.
- **`GetGitHubProjects` stays** declared, aliased and granted, so `/query/github-projects` still
  answers a direct link. Removing it would have meant a security.json change for no gain.
- **`Name` moved to the front of the column order** (owner request): it was 6th, behind Account,
  Owner, Installation Id, Node Id and Number, so the grid led with everything except the board's
  title. `Name` takes `order: 1` and the five above it shift down one.

  ⚠️ **Not `order: 0`.** Synchronize preserves a hand-set order only while it is positive —
  `existingAttr.Order = existingAttr.Order > 0 ? existingAttr.Order : order`
  (`ModelSynchronizer.cs:768`) — so a `0` reads as "unset" and the next synchronize would put the
  column back in sixth place. Verified by snapshot + re-sync + `diff -r`.

  This moves `Name` on **every** surface, not just this sub-query: `QueryResultProjector.cs:54`
  and `EntityMapper.cs:406` read the same per-attribute `order`, so both queries and the detail
  page follow. The model has no per-query column order. Mechanism and the `0` trap documented in
  `guide-attribute-grouping.md`.

⚠️ **`programUnits.json` is hashed, unlike labels and descriptions.** Removing the unit drifted the
model (`--spark-verify-model` exit 3, naming `config programUnits.json`) and needed a
`--spark-synchronize-model` re-stamp. The hash moved to `31725c97…`.

**Verify:** `--spark-verify-model` and `--spark-verify-security` both exit 0; the sidebar shows only
the `Coverage` group; the Account page's boards sub-query is headed "Project boards" and leads with
the board name. `CodeCoverage.Tests` **387 / 0, exit 0**.

**Verified live (2026-09-08).** Sidebar shows the `Coverage` group alone; the Account page carries
"Repositories" and "Project boards" side by side; the boards grid leads with `Name`; the 24 tooltips
average 59 characters with a maximum of 111 (Dutch 131); Dutch labels render (`Naam`, `Eigenaar`,
`Installatie-id`); 0 console errors.

⚠️ **A description is also an `aria-label`.** The `[i]` control carries it, so a screen reader
announces the whole text — before this pass one attribute announced 2,097 characters. The
condensation fixed an accessibility defect, not just a wide tooltip. Noted in
`guide-attribute-descriptions.md`.

---

## PR checklist

- [ ] No new `[MessageQueue]` name (C1, R1); 4 queue guards green
- [ ] No `SparkQueryPage<T>` on a query that has row actions (C5)
- [ ] No `[AllowAnonymous]` controller; no bespoke `/api/github/*` endpoints (PRD §2.2)
- [ ] No interpolated GraphQL anywhere (B1)
- [ ] `In()` not `Contains`; `!= Disconnected` not `== Connected` (FR2)
- [ ] `[GenerateIndex]` carries no type argument (FR1)
- [ ] `modelHashes.json` committed with the model files it stamps (C8, R4)
- [ ] No `addIfMissing` argument derived from the item's type — every call site reads
      `rule.AutoAddToBoard` (M14, C15); `grep -rn "AddLinkedIfMissing" apps/` clean
- [ ] The renamed option's **nl and fr** descriptions read back as a spec review, not a
      translation pass — that is where C15 was caught
- [ ] `Dockerfile` COPY list and `code-coverage-deploy.yml` `paths:` both updated if a reference was
      added (C9, R5)
- [ ] `Octokit.GraphQL` declared explicitly, beta pin visible (R6)
- [ ] **Version diff reviewed** — CI publishes on push to `master`. The major digit moves only when
      the targeted platform moves: npm major = Angular major, NuGet major = .NET major. A wrongly
      published major is burned forever.
- [ ] `git ls-files | grep -i webhooksdemo` clean outside `docs/` prose
- [ ] PRD §8 manual GitHub steps handed to the owner, especially the shared webhook secret (B6/C2)
