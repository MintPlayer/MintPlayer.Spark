# Plan — Silent custom actions, and honest coverage

Companion to [actions_and_coverage_PRD.md](actions_and_coverage_PRD.md). One pull request.

Ordering principle: **spikes first** (they can invalidate milestones), then the action fixes
(small, user-visible, and they unblock the owner today), then instrumentation, then a
re-baseline, then coverage work, then the gate.

## Status — 2026-09-06

On branch `fix/coverage-queue-licence-cap`.

| | Milestone | State |
| --- | --- | --- |
| S1 | Which `DeleteData` guard fires | **Resolved** — none does; it was the subscription cap |
| M0 | Queue consolidation + fold the two #366 queues | **Done** |
| M1 | `DeleteDataAction` reports every outcome | **Done** |
| M2 | `refreshAttribute` handler — **and `navigate`** | **Done** |
| M3 | Non-stale read after the resync write | **Done** |
| M4 | Busy state on custom actions | **Done** |
| M5 | Latent defects on the same path | **Done** — bar the virtual-PO id, deliberately left |
| M6 | Tests for the delete path | **Done** |
| S2 | Does an SPA report reach the badge | **Resolved** — it does now; verified by M13's checker |
| S3 | Does `--settings` stabilise the `<source>` root | **Resolved — NO.** M13 is mandatory, not defensive |
| S4 | Why `libs/testing` is in zero reports | **Resolved — false premise.** It IS measured; the E2E suite covers 15 of its files |
| S5 | Can a `WebApplicationFactory` boot the app | **Resolved — yes**, after fixing a framework defect it exposed |
| S6 | Nx cache and coverage outputs | **Done** — `test.outputs` now covers the real dir |
| M7 | Wire `coverlet.runsettings` | **Done** — moved to root, all five targets |
| M8 | SPA vitest coverage | **Done** — *without* replacing the executor |
| M9 | Action into the nx graph | **Done** — 41.28% now measured |
| M10 | Demo ClientApps | **Reverted** — owner decision: demos are not tested, so not measured |
| M11 | `libs/testing` visible | **Not needed** — S4 dissolved it |
| M12 | Demo .NET apps | **Resolved** — excluded from the denominator entirely |
| M13 | Port `verify-coverage-paths.mjs` | **Done** — wired into both workflows |
| M14 | Re-baseline | **Done** — 81.51% (22276/27328) after the demo decision |
| M16 | `[SparkAuthorize]` end to end | **Started** — host built, first 3 tests green |
| M15, M17–M22 | Raise real coverage, then gate | Not started |

Verified green: framework 1924 tests, `CodeCoverage` 314, `ng-spark` 402, `ng-spark-auth` 98.

Everything above still belongs to **one pull request**; the table records progress within it,
not a split.

---

## Spikes

Each spike is cheap and decisive, and each one can change the milestones below. None of them
writes production code.

### S1 — ~~Which `DeleteData` guard fires in production?~~ **RESOLVED — no guard fires**

**Answered. Do not re-run this spike.** The production log shows
`Queued deletion of MintPlayer/CodeCoverage`: both guards passed and the message was written.
The failure is the RavenDB subscription cap (PRD Defect 3b) — production runs a **Community**
licence with `MaxNumberOfSubscriptionsPerDatabase: 3` against **eight** declared subscriptions,
and `coverage-delete-repository-data` is one of the five with no consumer.

`CanManageOwnerAsync` is **refuted**; the degradation theory was wrong. What remains for M1 is
therefore only the reporting fix, not a redefinition of "manage".

The superseding work is **M0** below.

<details>
<summary>Original spike definition (kept for provenance)</summary>

The two refusals and the success are logged verbatim and distinguishably:

```
"Refused DeleteData on {FullName}: caller does not manage {Owner}"   DeleteDataAction.cs:50
"Refused DeleteData on {FullName}: it is still connected"            DeleteDataAction.cs:57
"Queued deletion of {FullName}"                                      DeleteDataAction.cs:72
```

One grep of the production server log around the click distinguishes all three. If none of the
three appears, the action returned at line 38/42/46 and the parent never resolved.

**Already executed locally — result: the path works.** Run the app with
`dotnet run --launch-profile https` (the **https** profile on `localhost:5200` is the one the
GitHub OAuth app's redirect URI is registered against; the default `http` profile on 5201
fails with *"The redirect_uri is not associated with this application"*). Signed in as the
owner, against `Repositories/1305831351` patched to `Connection = 1`, *Delete data* logged
`Queued deletion of MintPlayer/CodeCoverage`, the message reached `Status: Completed`, and
`Commits` went 27→19, `Repositories` 160→159, repository document gone.

**So the remaining question is production-only**, and one grep answers it. The prediction is
`"caller does not manage {Owner}"` — `CanManageOwnerAsync` is the only candidate the local run
did not rule out. If confirmed, M1 is not only a message: it needs a decision about what
"manage" should mean for a repository we have *already lost access to*, since a degraded token
makes exactly those repositories undeletable.

A production A/B is available if the log is not: clicking *Delete data* on a **user-owned**
disconnected repository should succeed where the org-owned one fails, because the caller's own
login is always in the allowed-owner set. It is destructive, so it needs the owner's explicit
say-so first.

**Blocks:** M1's refusal wording and scope.

</details>

### S2 — Does an SPA cobertura report actually reach the badge?

Publish one SPA report from a branch and confirm the server's `git ls-files` suffix match
resolves `apps/CodeCoverage/CodeCoverage/ClientApp/src/...`, and that it lands in the badge
denominator rather than the unmatched bucket. This is the exact failure mode of defect 3 and
it fails **silently**. **Blocks M2.**

### S3 — Does `--settings` actually stabilise the `<source>` root?

Run two suites with `coverlet.runsettings` wired and diff the `<source>` values. If they still
disagree, M1's one-line fix does not close defect 4 and **M7 becomes mandatory rather than
defensive**. **Blocks M6/M7 sequencing.**

### S4 — Why is `libs/testing` in zero reports?

Confirm *why* before choosing a fix. The xunit-test-assembly-detection hypothesis is
**unverified**. 1,784 lines of a published package are at stake. **Blocks M5.**

### S5 — ~~Can a `WebApplicationFactory` boot `apps/CodeCoverage`?~~ **RESOLVED — yes**

`CoverageWebAppFactory` boots the real composition root in-process against an embedded RavenDB.
Getting there surfaced four obstacles; three were configuration, and the fourth was a genuine
framework defect that this spike is the reason anyone found.

1. **Configuration must go through `UseSetting`**, not `ConfigureAppConfiguration`. `Program`
   reads `builder.Configuration` while it is still registering services, so a source added
   later is invisible and the app threw *"GitHub sign-in is not configured"* before any test ran.
2. **The content root must be the app's project directory**, since Spark reads `App_Data` from
   it. Resolved by walking up to the repository root rather than counting `..` segments.
3. **The environment must not be `Development`**, or `UseAngularCliServer` spawns an Angular dev
   server per test run.
4. **`Assembly.GetEntryAssembly()` seeded index discovery** — see below.

### The framework defect S5 exposed

`SparkModuleRegistry.ResolveIndexAssemblies()` seeded discovery from
`Assembly.GetEntryAssembly()`. Under `dotnet run` that is the application; under an in-process
test host it is **the test runner**, so the index catalog came up **empty**. An empty catalog
makes `ModelShapeDiscovery` yield no projection, and `SparkModelShape.Describe` then silently
drops two lines — `querytype` and `index` — from every projection-backed entity's shape. Those
hashes move, and the startup gate rejects a model that `--spark-verify-model` had just accepted
on the same build.

Measured, not inferred: the canonical shape texts differ by exactly those two lines, and the
catalog is `<empty>` under the entry assembly and fully populated under the app assembly.

Only `Account`, `Build` and `Repository` drifted because the predicate is *"context root with a
projection-bearing index"*. `Commit` has an index (`Commits_ByRepository`) but no `[FromIndex]`
projection, so it is immune.

**Fix:** anchor discovery on the context's assembly in `SparkExtensions.UseContext<TContext>`
(`libs/spark/MintPlayer.Spark/SparkMiddleware.cs`) — `AddIndexAssembly` appends rather than
replaces, runs before all three consumers read the list, and re-registering a type is idempotent.

**Two theories that measured false, recorded so nobody re-runs them:**

- *The `{Entity}Actions` correlation.* The three drifting entities each have an Actions class, but
  so does `Commit`, which does not drift. There is no path from a discovered Actions type to the
  hash at all.
- *Attribute descriptions.* Descriptions are **not part of any hash**, by design —
  `SparkModelShape.Describe` never emits them and `ModelSynchronizer.cs:374-378` says so outright.
  A stale English description therefore cannot break startup; it is reported only by
  `--spark-verify-model`.

**Blast radius**, since this is published framework code: all five apps in this repo declare their
context in the entry assembly, so it is a no-op for `dotnet run`. A third-party app whose context
assembly is not the entry assembly *and* which holds projection-bearing indexes will now catalogue
them, moving its hash and needing one `--spark-synchronize-model`. That is a correctness fix, but
it is a behaviour change and belongs in the release notes.

`SparkReplicationExtensions.cs:87` has the identical `GetEntryAssembly()!` trap in another
subsystem — **not fixed here**, and worth doing before it costs someone the same day.

### S6 — Nx cache and coverage outputs

M2–M4 change where coverage lands for five projects while `nx.json` declares only
`{projectRoot}/coverage`. Verify a cache hit still yields a report. **Blocks M2, M3, M4.**

---

## Part 1 — Make custom actions speak

### M0 — Land the queue consolidation *(highest priority — this is the live outage)*

Branch `fix/coverage-queue-licence-cap` (2 ahead / 2 behind master) already contains the right
fix and reached the same diagnosis independently: consolidate to `CoverageQueues.Ingestion` +
`CoverageQueues.Publishing`, and make a `LicenseLimitException` on subscription create **fatal**
instead of a swallowed warning that leaves a dead worker behind a healthy-looking app.

**It must not be rebased unchanged.** It predates #366. Current master declares seven coverage
queues; the branch maps only five. On rebase, map the two additions onto `Publishing`:

- `coverage-delete-repository-data` → `Publishing` (retention — it keeps company with
  `coverage-delete-pr-builds`, which the branch already put there)
- `coverage-reconcile-account` → `Publishing` (it calls GitHub)

That restores the total to `Ingestion` + `Publishing` + `spark-github-all` = **3**, exactly at
the cap. Add both names to `CoverageQueuesTests`, which asserts the queue count and names at
build time — that test is what stops the next feature walking off the same cliff.

Both additions are ordinary messaging queues — `[MessageQueue(...)]` record plus
`IRecipient<T>`, the identical mechanism as the five the branch already folds — so folding them
is a one-constant change per message type. **It costs zero new subscriptions**: verified against
production, the three live subscriptions are exactly
`SparkMessaging-coverage-parse-session`, `SparkMessaging-coverage-publish-feedback` and
`SparkMessaging-spark-github-all`, and every subscription in the database is a
`SparkMessaging-*` one — nothing else competes for the budget.

**Pair with the recipient's chunking.** Consolidation means an unbounded repository sweep now
shares a queue with PR comments, so a large delete blocks user-visible feedback.

`ICheckpointRecipient<T>` was considered and **rejected**: it is resume-on-retry, not a yield —
the framework calls the checkpoint overload only when a checkpoint survives from a *previous
attempt*, so it bounds a transaction but never hands the queue back. It is also redundant here,
because deleting is idempotent.

What actually relieves the blocking is a per-message budget plus a re-broadcast: each message
deletes at most `MaxDeletesPerMessage` documents and, if more remain, re-queues itself and
returns, so the continuation goes to the back of the queue. No cursor is needed — a deleted
document is gone from the prefix stream, so the next chunk resumes by streaming the same prefix.
The `Repository` document must be deleted **last**, since it is the marker the handler gates on;
deleting it early would make the continuation find it missing and strand the remainder.

**After deploy, verify** all three subscriptions start and none logs
`does not exist (non-recoverable)`. Note `coverage-delete-pr-builds` has **never** run in
production, so merged-PR build retention has been unenforced since it was added — expect a
backlog on the first successful run.

*Separate decision, not a prerequisite:* `feat/single-subscription-partitioned-ordering`
(1 ahead / **31 behind** master) is the more ambitious single-subscription design and is badly
stale. M0 is the shippable fix.

### M1 — `DeleteDataAction` reports every outcome

`apps/CodeCoverage/CodeCoverage/CustomActions/DeleteDataAction.cs`

Replace all five silent `return`s with a `Notify` stating the actual reason (inject `IManager`,
as `ResyncAction` already does). None of these was the production cause (S1), but their silence
is what made a dead queue look like a permission problem for the entire life of the button —
which is the case for fixing them regardless:

- not a manager → "You do not manage {owner}."
- still connected → "This repository is still connected. Press Resync first."
- success → "Deletion queued — it may take a minute."

On success, add a `NavigateOperation` to the account page, so the user is not left looking at
an object that is about to disappear. Set `"refreshOnCompleted": false` for `DeleteData` in
`App_Data/customActions.json` — the refresh is a race it always loses, and navigation replaces
it.

### M2 — Make `refreshAttribute` real — **and `navigate`, which is dead too**

`libs/node_packages/ng-spark/client-operations/src/provide.ts`

Register a handler for `refreshAttribute`, mirroring the `refreshQuery` /
`SparkQueryRefreshService` shape: a root `SparkAttributeRefreshService` keyed by
`objectTypeId + id`, read in an effect by `SparkPoDetailComponent`.

**Scope grew once implementation started.** `provide.ts` registers only `notify`,
`refreshQuery` and `disableAction` — so `navigate` was a declared wire type with no handler as
well. Every `IClientAccessor.Navigate` call in every Spark application has been silently
discarded, exactly like `RefreshAttribute`. Both are registered. That makes three operations
lost to the same gap (`refreshQuery` was the first), which is why the spec asserts the
registered **set**, not just per-handler behaviour: the next omission should be a failing test,
not a production mystery.

The patch carries the server-computed value, so the detail page applies it in place rather than
re-fetching — which also sidesteps the stale-index problem M3 addresses, for this path.

*Alternative considered and rejected:* deleting the `refreshAttribute` wire type. The server
side is already written and correct, and the operation is the right primitive for "one
attribute changed" — the bug is that only half of it was built.

### M3 — Wait for non-stale reads after a write

`ResyncAction` and `Services/MyAccountsService.cs` — add
`.Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))` on the post-write read
path, or plumb a flag so only the resync path pays for it. Without this, even a correct
refresh renders pre-action data.

### M4 — Busy state on custom actions — **done**

`runningAction` signal in `SparkPoDetailComponent`, `[disabled]` bound on every custom-action
button, and a re-entry guard in `onCustomAction` itself — the template is not the only caller, and
a host driving the method directly would otherwise bypass it.

The reset lives in a `finally`, not after the `try`: an action that throws must not leave every
button on the page permanently dead. The spec covers exactly that, plus the second-click case,
because a guard nobody tests is how the last three regressions in this area happened.

### M5 — The latent defects on the same path

**Done:**

- ✅ `ResyncAction`: `SaveChangesAsync` moved inside the error boundary.
- ✅ `DeleteRepositoryDataRecipient`: deletes in bounded chunks and re-queues a continuation.
  This supersedes the original "flush in batches (~512)" item — batching alone bounds the
  transaction but does nothing about head-of-line blocking on the now-shared queue. It does both:
  `BatchSize` per transaction, `MaxDeletesPerMessage` per message.

**Also done:**

- ✅ `Program.cs` now calls `spark.AddCustomActions()`. Worth recording how misleading the evidence
  was: `obj/generated/.../SparkCustomActionsRegistrations.g.cs` was dated three days stale and
  listed only `ResyncAction`, and after a clean `--no-incremental` rebuild the generator's output
  directory **disappeared entirely** — yet the call compiles. `EmitCompilerGeneratedFiles` output
  is a debug artifact and is not evidence of what the compiler actually used. Do not diagnose a
  generator from it.
- ✅ `CustomActionResolver.Resolve` now rethrows instead of returning null. Null means "no such
  action" to every caller, so a dependency the container could not satisfy became a 404 claiming
  the action does not exist — pointing the reader at the action name and `customActions.json`,
  neither of which is wrong, while the real cause was log-only. It is now a 500 naming the type
  and carrying the container's message.

**Still open, deliberately:**
- `EntityMapper` / virtual PO load: stamp `obj.Id ??= id` so `args.Parent` is non-null for
  every virtual PO action. **Check the blast radius** on `spark-query-card [parentId]` —
  Home passes `null` today and would begin passing `"main"`.
  ⚠️ Lower priority than it first appeared: production evidence shows `args.Parent` is **not**
  null on Home (Resync emitted both `refreshAttribute` operations, which only happen inside that
  `if`). This is a latent inconsistency, not a live bug.

### M6 — Tests for the delete path

Correction to the original claim that there were none: #366 shipped
`DeleteRepositoryDataRecipientTests` (186 lines), covering the happy path, the neighbouring
repository, the reconnect refusal and the already-gone case. What was missing is the action and
the wiring.

**Done:**

- ✅ A repository that fits in one message is **not** re-queued — the infinite-loop guard on the
  new continuation. A continuation queued after everything is deleted would find the repository
  gone, re-queue, and spin forever on the publishing queue.
- ✅ `provide.spec.ts` asserts the exact registered set of client operations, which is what makes
  M2 permanent and would have caught all three dead operations.
- ✅ `CoverageQueuesTests` covers the two folded queues automatically (it reflects over every
  message type).

**Also done:**

- ✅ `The_delete_message_is_queued_on_the_shared_publishing_lane` — asserts the message declares
  `CoverageQueues.Publishing`. This is the assertion that would have caught the outage:
  `CoverageQueuesTests` guards the queue *count*, this guards that THIS message is on one of the
  two that exist rather than quietly declaring a fourth.
- ✅ `A_document_with_no_Connection_field_reads_as_Connected_and_is_not_deletable` — pins the
  legacy-document case. The delete gate needs positive, persisted evidence of disconnection;
  spelling it `== Connected` instead of `!= Disconnected` would let a pre-#366 document through
  and destroy live data.
- ✅ `A_repository_that_reconnected_after_queueing_is_not_deleted` — the recipient re-checks
  rather than trusting the message, because a repository can reconnect between click and sweep.

**Still open:** driving `POST /spark/actions/repository/DeleteData` over HTTP through
`CoverageWebAppFactory` and asserting the notify operations per refusal path. The factory now
exists (S5), so this is no longer blocked — it is just not written.

---

## Part 2 — Make the number honest

### M7 — Wire `coverlet.runsettings` into all five .NET test targets

`--settings` on each of `tests/MintPlayer.Spark{,.Client,.SourceGenerators,.E2E}.Tests` and
`apps/CodeCoverage/CodeCoverage.Tests`. Closes defect 2, and — pending S3 — defect 4, since
`UseSourceLink=false` + `DeterministicReport=false` are what stabilise the `<source>` root.
Fix the file's header comment, which describes a location and a workflow that no longer exist.

### M8 — Give the SPA a real vitest target

Replace `@nx/angular:unit-test` in `apps/CodeCoverage/CodeCoverage/ClientApp/project.json`
with `nx:run-commands` + a `vitest.config.ts` carrying `all: true`,
`reporter: ['cobertura']`, `reportsDirectory: './coverage'`, matching the two libs.

The executor **has no `all` option** — its schema exposes only `coverage`, `coverageInclude`,
`coverageExclude`, `coverageReporters`, `coverageThresholds`, `coverageWatermarks` — so this
cannot be fixed by adding an option. Doing it this way also makes the existing CI glob and
`nx.json`'s `test.outputs` correct without touching either.

Expect the SPA denominator to go 6 → 36 files and its headline to fall from 67 % to ≈7 %.

### M9 — The action into the nx graph

`apps/CodeCoverage/action` has no `project.json` at all. Add one with a `test` target running
`vitest run --coverage`, emit cobertura alongside lcov, and add
`apps/*/action/coverage/cobertura-coverage.xml` to both workflows. Baseline lands at 41.3 %.

### M10 — The four demo ClientApps

They emit no coverage at all. Either give them the M8 treatment or record an explicit written
exclusion. Decide before the gate exists.

### M11 — Make `libs/testing` visible

Driven by S4. It is a published package in no report. Either force it in via runsettings
`Include`, or record a deliberate, argued exclusion.

### M12 — The demo .NET apps

Referenced by no test project, so today they are in neither numerator nor denominator. Put
them in the denominator or exclude them by rule — again, before the gate.

### M13 — Port `tools/verify-coverage-paths.mjs`

From `feat/coverage-95` (155 lines + 102 lines of its own tests). Run it as a CI step: every
uploaded report path must resolve against `git ls-files`, and an unmatched non-generated path
**fails**. This is the only thing that stops defect 4 recurring silently, and it is what turns
the app's "N paths couldn't be matched" banner back into a signal.

### M14 — Re-baseline

One full `nx run-many --target=test` on master, aggregate, publish the honest number.
**This is the only place in the plan where a full suite run belongs** — everything before it
is verified by reading code and type-checking.

---

## Part 3 — Raise real coverage

Ordered by uncovered lines × risk. Production before framework, framework before demos.

### M15 — `TimeProvider` seam *(enabler — lands before M17/M18)*

53 `DateTime.UtcNow` in `libs/`, 41 in `apps/CodeCoverage`. One adoption exists
(`GitHubUserTokenService`) to copy. Several workers in M17/M18 are untestable without it, so
this is not a cleanup — it is a prerequisite.

### M16 — `[SparkAuthorize]` end to end — **started**

Driven by S5, which is now resolved. `CoverageWebAppFactory` exists and the first three tests
are green:

- the application starts at all (which also executes `Program.cs`, 291 lines that no test had
  ever run, and catches startup-only failures no controller test can);
- an anonymous badge request is **not** challenged, because `[AllowAnonymous]` beats the
  type-level `[SparkAuthorize]` and a public badge is the entire point;
- an anonymous caller **cannot** reach an authorized endpoint.

Both halves matter and only one is usually remembered. A change that made the whole app require
a signed-in user would break every README badge in the wild while every existing test stayed
green, because none of them runs a filter.

**Still to do:** drive all six attributed endpoints with real principals — an owner, a
non-owner and an anonymous caller each — and cover `ApiTokenAuthenticationHandler` (0%) through
the token scheme rather than by constructing it.

### M17 — Controllers

`UploadsController` (122), `BrowseController` (135), `TokensController` (60),
`RepoSettingsController` (36), `BadgeController` + `BadgeRenderer` (72). ≈430 uncovered lines,
all highest-risk. Note `[AllowAnonymous]` beats `[SparkAuthorize]` — the badge surface is
anonymous and must be tested as such.

### M18 — Framework core

`EntityMapper` (87), `PersistentObject/Refresh.cs` (80), `QueryExecutor` (57),
`DatabaseAccess` (46), `SparkMiddleware` (33), `RowSecurity` (33), `SyncActionHandler` (33).
≈370 lines in the security-relevant core.

### M19 — Subscription, messaging, webhooks

`SparkSubscriptionWorker` (89, 41 %), `MessageSubscriptionWorker` (59),
`SparkWebhookEventProcessor` (35), `SparkBuilderExtensions` (52). ≈240 lines — and the exact
surface that produced the dropped-`installation_repositories` regression #366 had to fix.

### M20 — Replication mTLS + IdentityProvider token path

`ModuleCertificateAuthentication` (58, 33 %), `EtlTaskManager` (53, 22 %),
`SparkReplicationExtensions` (52), `Token.cs` (57), `OidcTokenGenerator` (48).
≈300 lines, high risk, low coverage.

### M21 — Written exclusions — **scope decided, and it is nearly empty**

Owner decision, 2026-09-06:

- **The four demo apps are not tested at all**, so they are out of the **denominator**
  entirely — not measured at ~0%. `DemoApp`, `Fleet`, `HR` and `WebhooksDemo`, both their
  .NET projects and their ClientApps. This settles M12 and reverses M10.
- **`apps/CodeCoverage` must be tested**, because that code runs a live website. It is
  production, and it is where the coverage effort goes.
- **The dev tooling must be tested too** — the DevTunnel/Smee services and the
  `SparkDevelopmentExtensions` CLI verbs are *not* excluded. They were the largest proposed
  exclusion (≈255 lines) and that proposal is withdrawn.

What remains of this milestone is therefore almost nothing. `Program.cs` (291 lines, 0%) is
**`apps/CodeCoverage` code and so must be covered, not excluded** — but unit tests are the
wrong tool for a composition root. It is covered by the boot smoke test in S5/M16 instead,
which exercises it end to end for free while testing something that actually matters.

Add an `[ExcludeFromCodeCoverage]` only with a written reason, and expect to add none.

### M22 — Turn on the gate

Not before M14's re-baseline is stable across **two** master runs.

---

## Verification

Per the repository convention, intermediate milestones are verified by reading code and
type-checking. **Test suites run once, at the end** — with the single exception of M14, whose
entire purpose is a full measured run.

## Sequencing note

M1–M6 are independently shippable and fix what the owner is looking at today. They do not
depend on any coverage milestone. If the work is ever split by time (not by PR), that is the
seam — but it all lands in one pull request.
