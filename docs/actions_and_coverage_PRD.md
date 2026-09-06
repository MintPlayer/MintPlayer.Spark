# PRD — Silent custom actions, and honest coverage

Two aims in one unit of work:

1. **Custom actions must tell the user what happened.** Two production buttons on
   coverage.mintplayer.com — *Resync* on the home page and *Delete data* on a disconnected
   repository — appear to do nothing. Neither is "broken" in the sense of throwing; both
   return HTTP 200 and then discard, or never produce, the only evidence the user had.
2. **The repository's coverage number must be real before it is raised.** The instrument is
   still wrong in several measurable ways. Every fix moves the number *down* first, so the
   fixes must land before any target is set.

They ship together because aim 1 is a large part of what aim 2 would have caught: the
`apps/CodeCoverage` custom actions had no tests at all, and the framework's client-operation
channel had three dead branches that no test exercised in any app.

## Status — 2026-09-06

Aim 1 is **implemented** on `fix/coverage-queue-licence-cap` and awaiting deploy: defects 1, 2, 3
and 3b are fixed; defect 4 (busy state) is open. Aim 2 is **not started** — its milestones and
spikes are in the [plan](actions_and_coverage_plan.md), which carries the per-milestone status
table.

The investigation narrative below is kept as written rather than rewritten in hindsight, because
the sequence is the useful part: two hypotheses were confirmed against production and one — the
`CanManageOwnerAsync` degradation, which survived a full local reproduction — was **wrong**. Only
production could distinguish them, because the local RavenDB is not subscription-capped.

---

## Part 1 — Custom actions that report nothing

### Evidence gathered

Captured live against coverage.mintplayer.com while signed in as the owner, and against a
local run of `apps/CodeCoverage` on commit `bf6b39c7`.

| Observation | Source |
| --- | --- |
| `POST /spark/actions/home/Resync` → **200**, no console errors | live network capture |
| Its body: `{"result":null,"operations":[{"type":"refreshAttribute",…AccountCount:2},{"type":"refreshAttribute",…RepoCount:177},{"type":"refreshQuery","queryId":"my-accounts"}]}` | live network capture |
| Resync **does** perform real work | `Repositories/1305831351` carries `DisconnectedAtUtc = 2026-09-06T10:28:33Z`, `DisconnectedReason = RemovedFromInstallation`, written by that reconcile |
| `POST /spark/actions/repository/DeleteData` → **200**, body `{"result":null,"operations":[]}` | live network capture |
| Nothing was deleted | full page reload one minute later: all 10 commits present, repository document intact |
| The confirmation dialog works and is translated | live: *"This permanently deletes every commit, build and coverage report for this repository. It cannot be undone."* |
| The delete lane **is** correctly wired | local run: `Subscription worker 'SparkMessaging-coverage-delete-repository-data' started` (and `…-coverage-reconcile-account`) |
| **The whole delete path works locally** | local run, signed in as the owner, against a repository patched to `Connection = 1`: log `Queued deletion of MintPlayer/CodeCoverage`, message `Status: Completed`, `Commits` 27→19, `Repositories` 160→159, repository document gone |
| **The same click on production deletes nothing** | re-checked 15 minutes after the click: repository, commits and page all still present |

### Defect 1 — `refreshAttribute` is a client operation with no client handler *(fixed)*

**Confirmed, not inferred.** `refreshAttribute` is declared as a wire type at
`libs/node_packages/ng-spark/client-operations/src/operations.ts:30` and has **zero handlers
anywhere in the repository**. `provideSparkClientOperations()` registers only `notify`,
`refreshQuery` and `disableAction`, and the dispatcher silently drops unknown operation types
by design.

So the server computes the two counts, serialises them, sends them — and the client throws
them away without a warning. This is framework-wide: `IClientAccessor.RefreshAttribute` is
inert in **every** Spark application, not just this one. The `provide.ts` header comment
documents this exact failure mode for `refreshQuery` and then reproduces it for
`refreshAttribute`.

This also **refutes** the competing theory that `args.Parent` is null on the virtual Home
object: the two `refreshAttribute` operations are emitted only from inside
`if (args.Parent is { } home)`, and they were emitted.

**And it was worse than one operation.** Registering the handler revealed that **`navigate` was
dead in exactly the same way** — a declared wire type with no handler anywhere, so every
`IClientAccessor.Navigate` call in every Spark application was computed, serialised, sent and
discarded. That makes **three** operations lost to one gap; `refreshQuery` was the first.

The pattern is the defect, not any one operation: *a wire type in `operations.ts` is not a
feature*. Both are now registered, and `provide.spec.ts` asserts the registered **set** exactly,
so the next omission is a failing test rather than a production mystery. `disableAction` remains
deliberately unimplemented, registered only to warn.

### Defect 2 — the post-write read races a stale index *(fixed)*

`ResyncAction` saves, then immediately re-queries through `MyAccountsService`, which hits
`Indexes.Repositories_Overview` / `Accounts_Overview` with **no `WaitForNonStaleResults`**.
The `refreshQuery` refetch lands a round trip later, also with no staleness wait. RavenDB
indexes are eventually consistent, so on precisely the click that changes something, both
reads return pre-reconcile data. The change shows up on a later reload — i.e. "the button did
nothing".

### Defect 3 — `DeleteDataAction` has five silent `return` paths *(fixed)*

`apps/CodeCoverage/CodeCoverage/CustomActions/DeleteDataAction.cs` returns without emitting
anything at lines 38, 42, 46, 52 and 58: no parent, no id, repository not found, caller does
not manage the owner, repository still connected. Two of those log a warning server-side; none
reaches the user.

And on **success** it broadcasts a message and returns — also `{"result":null,"operations":[]}`.
**Success and all five failure modes are byte-identical from the client.** That is the whole
of the reported bug: the user cannot tell refusal from success from a queued sweep.

**None of the five guards is what fails in production.** They all pass — see Defect 3b, which
is the actual cause. What Defect 3 costs is *diagnosis*: because success and every refusal look
identical from the client, a dead message lane was indistinguishable from a permission refusal
for as long as the button has existed.

### Defect 3b — production RavenDB is over its subscription cap, so the lane is dead *(the actual cause — fixed, awaiting deploy)*

Confirmed by grepping the production container log and querying the licence:

```
LicenseLimitException: The maximum number of subscriptions per database cannot exceed
                       the limit of: 3
Subscription 'SparkMessaging-coverage-delete-repository-data' does not exist (non-recoverable)
Subscription worker 'SparkMessaging-coverage-delete-repository-data' stopped
```

```
"LicensedTo":"2sky"  "Status":"Commercial"  "Type":"Community"
"MaxNumberOfSubscriptionsPerDatabase": 3
```

`apps/CodeCoverage` declares **seven** distinct queue names; with the framework's
`spark-github-all` that is eight subscriptions against a cap of **three**. Three win the race
and five die permanently. The production log shows `Queued deletion of MintPlayer/CodeCoverage`
— **the action worked, both guards passed, the message was written** — and then nothing, because
`coverage-delete-repository-data` is one of the five with no subscription to consume it.

Also dead in production, from the same cap: `coverage-reconcile-account`,
`coverage-publish-pr-comment`, `coverage-open-pr-comment` and `coverage-delete-pr-builds`. So
webhook-driven account reconciliation and the sticky PR comment are both inert, and
merged-PR build retention **has never run in production**.

The framework made this invisible: `SparkSubscriptionWorker.EnsureSubscriptionExistsAsync`
swallowed the create failure in a bare `catch (Exception)` whose comment asserted the only
possible cause was "already exists", logged a benign *"will try to use existing"*, and then
started a worker against a subscription that does not exist. The app stays up and reports
healthy with silently dead queues.

**Fixed** on `fix/coverage-queue-licence-cap`, which consolidates to two queue constants and makes
a `LicenseLimitException` on create fatal with an actionable message. That branch reached the same
diagnosis independently, before this investigation.

**It was not sufficient against current master, and that gap is now closed.** The branch predates
#366: it consolidated the five queues that existed then, and #366 added
`coverage-delete-repository-data` and `coverage-reconcile-account`. Rebasing it unchanged would
have declared **five** subscriptions against the cap of three and left the delete lane a candidate
to stay dead — the exact bug it was written to fix, surviving its own fix. Both are now folded onto
`Publishing`: `ReconcileAccountMessage` because it calls GitHub, `DeleteRepositoryDataMessage`
because it is retention, alongside `DeletePullRequestBuildsMessage`.

Verified against production: every subscription in the database is a `SparkMessaging-*` one, and
the three that exist are exactly `coverage-parse-session`, `coverage-publish-feedback` and
`spark-github-all`. Nothing else competes for the budget, and reusing those two names costs **no
new subscription**. `CoverageQueuesTests` pins the count and the names at build time.

The cost of consolidation is head-of-line blocking, and it is materially worse now that an
unbounded repository sweep shares a queue with PR comments. `DeleteRepositoryDataRecipient`
therefore deletes in bounded chunks and re-queues a continuation, so blocking is one batch rather
than one repository.

⚠️ **`coverage-delete-pr-builds` has never run in production.** Merged-PR build retention has been
unenforced since it was added, so expect a backlog on the first successful run after deploy.

### Defect 4 — no busy state on a multi-second action *(open)*

`spark-po-detail`'s `onCustomAction` sets no pending state and does not disable the button,
while *Resync* now performs N paged GitHub App round trips inside the request. The user gets
several seconds of a live-looking button followed by an identical grid.

### Related latent defects found on the same path

- `CustomActionResolver.Resolve` swallows every construction exception and returns `null`,
  turning a DI misconfiguration into a 404 "Custom action not found". The real cause is
  log-only.
- Every virtual persistent object goes over the wire with `Id == null`
  (`EntityMapper.ScaffoldFrom` hard-codes it), so `[parentId]="currentItem.id!"` passes `null`
  through a non-null assertion.
- `spark.AddCustomActions()` is generated but **never called** in
  `apps/CodeCoverage/Program.cs`. It survives only because the resolver falls back to
  `ActivatorUtilities.CreateInstance`. This is the recorded "generated `AddX()` nobody wires"
  trap, one dependency away from biting.
- ✅ *(fixed)* `DeleteRepositoryDataRecipient` accumulated every deferred delete into a **single**
  `SaveChangesAsync`. For a long-lived repository that is tens of thousands of commands in one
  transaction; if it throws, the message retries and eventually dead-letters invisibly.
- ✅ *(fixed)* `ResyncAction`'s `SaveChangesAsync` sat **outside** the per-account try/catch, so one
  account's write conflict discarded the reconcile of all the others and 500'd the button — the
  opposite of the stated "a GitHub hiccup must not turn this into an error page" intent.

### Goals

- A custom action's outcome is always visible: success, refusal-with-reason, or queued-work.
- `refreshAttribute` either works or does not exist.
- A refusal states *which* condition failed, in the user's language.
- Asynchronous actions say that they are asynchronous.
- The delete path has tests for the action and its wiring, not only the recipient.

### Non-goals

- Redesigning the custom-action framework. The `notify` operation already exists and is
  already wired; this is about using it.
- Making deletion synchronous. Queuing is the right call and the doc comment defends it well.
- Replacing native `confirm()` with a styled modal. Worth doing, not in scope here — it works.

---

## Part 2 — Honest coverage, then more of it

### The re-baseline — measured 2026-09-06, after the instrumentation fixes

Run `node tools/coverage-summary.mjs` to reproduce. It joins each filename to **its own**
report's `<source>` root and de-duplicates per (file, line) with hits taken as the max, which
is what the naive aggregations kept getting wrong.

```
reports  18        (every path resolves; verify-coverage-paths.mjs reports 0 errors)
files    661
lines    22282/27423
coverage 81.25%
```

Suites behind it, all green: **2,587 .NET tests** (1924 + 314 + 229 + 82 + 38) and
**599 JS tests** (ng-spark 402, ng-spark-auth 98, SPA + demos + action).

**Read the scope before quoting the number.** 81.25% is the truth about *what is measured*,
and it is higher than the ≈76.5% this document previously estimated for two traceable reasons:
`libs/testing` turned out to be measured after all (the "zero reports" claim came from stale
artifacts — the E2E suite covers 15 of its files), and the demo **.NET** apps are still in
neither numerator nor denominator, because no test project references them. Folding those in at
their true ~0% would put the all-in figure near **78%**. That decision is M12 and is still open;
until it is made, quote 81.25% *with* the scope, never alone.

The largest single gap is `apps/CodeCoverage/Program.cs` at **291 uncovered lines, 0%** — a
composition root, and the strongest candidate for an argued exclusion rather than tests.
`DeleteDataAction` and `ResyncAction` also sit at 0%, which is a fair verdict on the code this
same PR just changed.

### What the number was before the fixes

Recomputed this session from the five local .NET cobertura reports plus the two JS reports,
each report's filenames joined to **its own** `<source>` root:

| | valid lines | covered | % |
| --- | ---: | ---: | ---: |
| .NET union | 20,511 | 17,108 | 83.4 % |
| ng-spark | 1,752 | 1,476 | 84.2 % |
| ng-spark-auth | 336 | 317 | 94.3 % |
| **What the badge currently sees** | **22,599** | **18,901** | **83.6 %** |
| **Honest, all-in, excluding WebhooksDemo** | **≈24,916** | **≈19,063** | **≈76.5 %** |

**The ≈68 % on record is superseded.** Three traceable reasons: `all: true` landed on master
via #356 and closed the ng-spark / ng-spark-auth denominators honestly; #356 also added the
`libs/node_packages/*` globs so those ~2,000 measured lines now reach the server; and the
earlier figure double-counted the `libs/`-rooted vs repo-rooted path split. That split is a
live trap — reproducing it naively during this audit cost **12 percentage points**.

Even ≈76.5 % is a **floor**: every local `apps/CodeCoverage` artifact predates #361 and #366,
whose ~1,700 lines of new tests appear in no report yet.

### Instrumentation defects, re-verified against master

| # | Defect | Status |
| --- | --- | --- |
| 1 | `coverage.all` unset → v8 measures only what a spec imports | **Diagnosis was wrong; symptom was real.** Vitest 4 **removed** `coverage.all`; the `all: true` lines were dead config that also failed `tsc --noEmit`, and they are deleted. The actual lever is an explicit `coverage.include` — "by default only files covered by tests are included". Fixed for every JS project. |
| 2 | `coverlet.runsettings` is an orphan | **Fixed.** Moved to the repo root (where its own comment always claimed it lived) and passed by all five .NET test targets. Its `IncludeTestAssembly=false` now demonstrably applies. |
| 3 | The SPA report is never uploaded | **Fixed**, and more cheaply than planned — see below. |
| 4 | Report path roots differ per suite | **Confirmed permanent.** See the S3 result below. |

**Three corrections to the audit, each of which changed the work:**

1. **The SPA did not need its executor replaced.** The audit concluded `@nx/angular:unit-test`
   "has no `all` option, so this cannot be fixed by adding an option". True but irrelevant: the
   executor *does* expose `coverageInclude`, which is forwarded to vitest's `coverage.include`,
   and that is the real lever in Vitest 4. The SPA already set it — as `src/**/*.ts`, which
   **silently matches nothing**, because these globs resolve against vitest's root (the
   workspace root), not the project. Correcting it to the full path took the report from
   **6 files to 36**, and the honest headline from 66.66% to **4.84% (29/599 lines)**. The
   same latent bug was in `coverageExclude`, so the exclusions were not applying either.

2. **`--settings` does NOT stabilise the `<source>` root (spike S3).** Measured with the file
   correctly wired: `tests/*` still emit `<source>.../libs/</source>` while
   `apps/CodeCoverage/CodeCoverage.Tests` emits the repo root. The root is the compilation's
   common path prefix and nothing in the runsettings changes it. So the server's longest-suffix
   match is load-bearing **permanently**, and M13's checker is mandatory rather than defensive.

3. **`ExcludeByFile` does not work** for the source generator's `Inject.g.cs`. Forward-slash,
   backslash and separator-free patterns were all measured against a valid settings file and
   none excluded it. 7 files / 68 lines still arrive, all 100% covered. They never reach the
   badge (`obj/` is gitignored, so they never match `git ls-files`), but they *would* keep the
   new tripwire permanently red, so `verify-coverage-paths.mjs` carries a narrow, documented
   allowance for `obj/` only.

**A fourth defect nobody had found — a file silently dropped from the denominator:**

`ng-spark-auth/src/lib/provide-spark-auth.ts` was **excluded from coverage entirely**, and this
is why the package reported 20 of 21 source files. Its vitest config had no alias for
`@mintplayer/ng-spark`, which the file imports. Vitest could not resolve it, so it fell back to
parsing the TypeScript as raw JavaScript, failed on `config?: Partial<SparkAuthConfig>`, logged
`Excluding it from coverage` and carried on. The file did not report 0% — it **disappeared**.
Fixed with a `resolve.alias` mirroring `tsconfig.base.json`, plus the spec it never had.

This is the same failure shape as everything else in this document: not an error, an absence.

**New findings not previously on record:**

- **`libs/testing/MintPlayer.Spark.Testing` — 1,784 lines — appears in ZERO reports.** It is a
  **published NuGet package**. Coverlet appears to skip it as a test assembly (it references
  xunit). *Hypothesis, not verified — see S4.*
- **The `apps/CodeCoverage/action` produces 41.3 % line coverage that is thrown away.** It has
  no `project.json`, no nx target, and no CI glob.
- **The four demo apps (77 files / 3,493 lines) are referenced by no test project**, so they
  contribute to neither numerator nor denominator.
- **`[SparkAuthorize]` is still executed by no test anywhere.** Zero source hits across all
  test projects; no `WebApplicationFactory`/`TestServer` in `CodeCoverage.Tests`. #366's
  `UploadsControllerAuthorizationTests` builds principals by hand and calls controller methods
  directly — valuable, but the filter is never in the pipeline. This is production.
- **`TimeProvider`**: one production adoption exists (`GitHubUserTokenService`). Remaining raw
  clock reads: **53 `DateTime.UtcNow` in `libs/`**, **41 in `apps/CodeCoverage`**.
- **Zero `[ExcludeFromCodeCoverage]` attributes exist in the repository.**
- `docs/coverage_95_{PRD,plan}.md` exist only on the unmerged local branch `feat/coverage-95`,
  which is now badly stale — its diff against master *deletes* 22,874 lines of since-landed
  work. **Do not rebase it.** Two of its four fixes reached master by other routes; the two
  that did not are re-specified here, and its `tools/verify-coverage-paths.mjs` is worth
  porting.

### Where the uncovered lines are

| Bucket | valid | covered | % | uncovered |
| --- | ---: | ---: | ---: | ---: |
| **`apps/CodeCoverage`** (production) | 2,891 | 1,456 | **50.4 %** | **1,435** |
| `libs/spark/MintPlayer.Spark` | 7,471 | 6,709 | 89.8 % | 762 |
| `libs/identity_provider` | 2,025 | 1,769 | 87.4 % | 256 |
| `libs/replication` | 965 | 718 | 74.4 % | 247 |
| `libs/source_generators` | 3,147 | 2,973 | 94.5 % | 174 |
| `libs/webhooks/…GitHub` | 399 | 249 | 62.4 % | 150 |
| `libs/webhooks/…DevTunnel` | 119 | 25 | **21.0 %** | 94 |
| `libs/subscription_worker/…Abstractions` | 187 | 98 | 52.4 % | 89 |
| `libs/authorization` | 898 | 894 | **99.6 %** | 4 |

Highest risk × size, concretely: `UploadsController` (122 uncovered, the ingest endpoint),
`BrowseController` (135), `TokensController` (60, 0 %), `ApiTokenAuthenticationHandler`
(34, **0 %** — the authentication handler itself), `BadgeController` + `BadgeRenderer` (72,
0 %, anonymous surface), `RepoSettingsController` (36, 0 %). Then framework:
`EntityMapper` (87), `PersistentObject/Refresh.cs` (80, 34 %), `SparkSubscriptionWorker`
(89, 41 %), `ModuleCertificateAuthentication` (58, 33 %, mTLS).

`Program.cs` at 281 uncovered lines / 0 % is the single largest uncovered file and the
strongest candidate for an argued exclusion rather than tests.

### Goals

- One published number that is defensible, with every uploaded report path resolving against
  `git ls-files` — and an **unmatched non-generated path failing CI**, not warning.
- Every project either in the denominator or excluded by a written, argued rule.
- A gate, set only after the re-baseline is stable across two master runs.

### Non-goals

- Setting the gate in this unit of work. The gate lands last, after M8's re-baseline.
- Testing the demo apps. `apps/WebhooksDemo` is slated for absorption into
  `apps/CodeCoverage`, so it is excluded from the denominator rather than tested.
- Rebasing `feat/coverage-95`.

### Target

≈24,916 valid lines excluding WebhooksDemo. A 95 % gate needs ≈23,670 covered against
≈19,063 today: **≈4,600 more covered lines**, budgeted at **300–450 new test cases** — not the
7,645 lines / 3× framing previously on record.

---

## Risks

- **Every instrumentation fix moves the number down first.** The SPA alone drops from 67 % over
  6 files to ≈7 % over 36. A gate set against today's figure would fail on the very commit that
  makes the measurement honest. Hence: instrument first, re-baseline, gate last.
- **Nx cache interaction.** `nx.json` declares `{projectRoot}/coverage` as the only `test`
  output; M2–M4 change where coverage lands for five projects. A cache hit that restores no
  report is defect 3 in a new costume — and cache replay is on record as destructive.
- **The path-suffix match is load-bearing and silent.** Until M7 lands, any report whose root
  shifts is dropped with no error.
- **`CanManageOwnerAsync` degradation is self-reinforcing**: the repositories eligible for
  deletion are exactly the ones you may have lost management of. If S1 confirms it, the fix is
  not only a message — it is a decision about what "manage" should mean for a disconnected
  repository.

## Out of scope

Genuinely not being done, not parked:

- Replacing native `confirm()` with a styled ng-bootstrap modal.
- The `DateTime` → `DateTimeOffset` model migration. Named in the roadmap and paired with the
  `TimeProvider` seam, but the "28 vs 7" count was **not verified** this session; it needs its
  own measurement before it is planned.
- Absorbing `apps/WebhooksDemo` into `apps/CodeCoverage`.
- Making the coverage gate blocking on other repositories.
