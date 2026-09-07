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

Everything else here is **not started**: both spikes, and M0-M13. `apps/WebhooksDemo` is still
present, and no `GitHubProject` entity exists.

⚠️ **S1 cannot be completed without credentials.** It queries Projects V2 for a real installation on
the MintPlayer org, so it needs the CoverageDevelopment app's private key. The code for M3/M5 can be
written and type-checked without it, but the node-cost and `.AllPages()` questions S1 exists to
answer stay open until someone runs it. The same applies to M13 in full.

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

## PR checklist

- [ ] No new `[MessageQueue]` name (C1, R1); 4 queue guards green
- [ ] No `SparkQueryPage<T>` on a query that has row actions (C5)
- [ ] No `[AllowAnonymous]` controller; no bespoke `/api/github/*` endpoints (PRD §2.2)
- [ ] No interpolated GraphQL anywhere (B1)
- [ ] `In()` not `Contains`; `!= Disconnected` not `== Connected` (FR2)
- [ ] `[GenerateIndex]` carries no type argument (FR1)
- [ ] `modelHashes.json` committed with the model files it stamps (C8, R4)
- [ ] `Dockerfile` COPY list and `code-coverage-deploy.yml` `paths:` both updated if a reference was
      added (C9, R5)
- [ ] `Octokit.GraphQL` declared explicitly, beta pin visible (R6)
- [ ] **Version diff reviewed** — CI publishes on push to `master`. The major digit moves only when
      the targeted platform moves: npm major = Angular major, NuGet major = .NET major. A wrongly
      published major is burned forever.
- [ ] `git ls-files | grep -i webhooksdemo` clean outside `docs/` prose
- [ ] PRD §8 manual GitHub steps handed to the owner, especially the shared webhook secret (B6/C2)
