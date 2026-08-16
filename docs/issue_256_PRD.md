# PRD — Issue #256: deterministic test waiting

**Status:** Implemented — see [Results](#results)
**Issue:** [#256](https://github.com/MintPlayer/MintPlayer.Spark/issues/256)
**PR:** [#257](https://github.com/MintPlayer/MintPlayer.Spark/pull/257)
**Branch:** `fix/issue-256-test-parallelism`

## Problem

Two related sources of CI flakiness in `MintPlayer.Spark.Tests`:

1. **Resource pressure** — ~530 databases per run (one per test case) on a single embedded server,
   with unconstrained xUnit parallelism. Addressed by the parallelism cap already committed on this
   branch.
2. **Timing** — how tests wait for asynchronous work. Two divergent index-wait implementations,
   three separate "default timeouts", four hand-rolled polling helpers, and three fixed-duration
   sleeps. This document covers that second half.

## Investigation findings (three-agent sweep, 2026-08-16)

### F1 — CronosCore has no Sleep-to-deterministic transition to copy

Searched `origin/development` commit messages and pickaxed `-S` for `WaitForIndexing`,
`WaitForNonStale`, `WaitForIndexesAfterSaveChanges`, `Thread.Sleep`, `Task.Delay`.

`WaitForIndexing` arrived **already formed** when the test project was moved in (`0683a08`,
2024-02-29 — pure additions from `/dev/null`, no pre-move history carried across) and has never been
a fixed-duration wait in that repo. `WaitForNonStale` and `WaitForIndexesAfterSaveChanges` have
**never appeared on any branch**.

So there is no prior art here to port wholesale. What CronosCore does offer is a well-refined poll
and two lessons learned the hard way:

- **`3222181`** (2024-03-22, "Wait For Indexes before enabling Subscriptions") — moved the wait into
  `ImportScope.EndScope` so indexing settles *before* subscriptions are re-enabled. Ordering
  matters: subscription workers query indexes, so they must not start against a stale one.
- **`9a6f11c`** (2026-08-04) — `WaitForIndexing` read a lazily-assigned field, so the first test of
  a run hit a `NullReferenceException` and the rest passed. It read as flakiness; it was
  initialization order.
- Their written policy: *"Wait for indexing when querying right after a write — use the configured
  timeout, don't `Thread.Sleep`."*

### F2 — Our two index-wait implementations genuinely diverge

Both poll `GetStatisticsOperation` every 100 ms, but agree on almost nothing else:

| | `RavenIndexingExtensions.WaitForIndexing` (sync) | `RavenIndexHelper.WaitForNonStaleAsync` (async) |
|---|---|---|
| Done condition | per-index `!IsStale` | `stats.StaleIndexes.Length == 0` |
| Disabled indexes | filtered out (`:59`) | **not filtered** — a disabled index can hang the wait |
| Side-by-side swaps | waits for the swap to finish (`:61-62`) | not considered |
| On `IndexState.Error` | `TimeoutException` **+ full index-error dump** (`:69-76`) | `InvalidOperationException`, **no error detail** — "check Raven Studio" (`:44-46`) |
| On timeout | dumps index errors | lists stale index names |
| Default timeout | `DefaultTimeout` (`:33`) | its own `TimeSpan.FromMinutes(1)` (`:32`) |

`SparkTestDriver.cs:84-88` shadows Raven's own `WaitForIndexing` with `new` (not `override`), so
which implementation you get depends on the static type of the reference — while its comment claims
"one implementation, one behaviour".

A third default exists and is **dead**: `SparkSubscriptionOptions.NonStaleIndexTimeout` (2 minutes)
plus `WaitForNonStaleIndexes` are read by nothing in `libs/`; only a test asserts their defaults.

### F3 — The side-by-side comment contradicts its code (documentation defect, not a logic bug)

`RavenIndexingExtensions.cs:30` says side-by-side replacements "must not hold up a wait", but the
predicate at `:61-62` requires that **no** `ReplacementOf/` index exists before returning — i.e. it
deliberately waits for the swap to complete.

The *code* is right and matches CronosCore exactly: while a replacement index exists, queries still
resolve against the old definition, so returning early would hand the test a stale view. The comment
is a mis-paraphrase introduced during the port. Fix the comment, keep the behaviour.

(An earlier reading of this as "the wait can only ever time out" is wrong — the replacement index
disappears when Raven completes the swap, which is exactly the condition being waited for.)

### F4 — `WaitForIndexesAfterSaveChanges` is unused in BOTH repos

Zero occurrences in MintPlayer.Spark and zero in CronosCore, ever. Likewise `Changes()` /
`ForAllIndexes` / `ForIndex` for index notifications, and `WaitForNonStaleResults`.

This is the genuinely better mechanism neither codebase adopted. `session.Advanced.WaitForIndexesAfterSaveChanges(timeout, throwOnTimeout: true)`
makes the **server** block the write until the indexes covering *that transaction* have caught up:

- **Targeted** — only the indexes touched by this write, not every index in the database.
- **Deterministic** — no sampling window. A global poll can observe a momentarily-clean snapshot and
  return while a concurrent writer's document is still unindexed.
- **No client polling** — no thread blocked, no 100 ms granularity.

### F5 — Only three genuinely indefensible fixed sleeps

Notably, **none of them is a substitute for index waiting** — all 65 index-wait call sites go
through a real implementation. The offenders are elsewhere:

| Site | Wait | Why it is wrong |
|---|---|---|
| `SyncActionSubscriptionWorkerE2ETests.cs:278` | `Task.Delay(500)` | Asserts a **negative** (`CallCount == 1` — nothing redelivered). Polling cannot fix a negative assertion; it needs a deterministic signal or an explicit consistency point. Highest flake risk in the repo. |
| `RateLimitTests.cs:47` (E2E) | `Task.Delay(11s)` | Waits out a 10 s fixed-window rate-limit bucket so the next test isn't poisoned. 11 s of dead time, and silently wrong if the window config changes. |
| `MessageSubscriptionManagerLifecycleTests.cs:70` | `Task.Delay(200)` | "Give the worker a beat to attach" — no attached-signal is checked. |

### F6 — Four hand-rolled polling helpers, four sets of semantics

`WaitForMessageAsync` (100 ms / 20 s, throws with rich diagnostics), `WaitForAsync` (100 ms / 20 s,
throws with `Status`/`LastError`), `WaitUntilAsync` (50 ms / 8 s, **returns `bool`** so failures
assert as "expected True" with no timing context), `WaitForCondition` (50 ms / 3 s, **swallows the
timeout entirely** — a hung file-watcher surfaces as a confusing downstream assertion).

Inconsistent failure behaviour is the problem, not the duplication: two of the four turn a timeout
into a misleading assertion message.

### F7 — Deploy-time index waiting is a weak guarantee

`RavenIndexHelper.DeployIndexesAsync:80` waits for non-stale immediately after
`IndexCreation.CreateIndexesAsync` on a **fresh, empty** database. With no documents, "nothing is
stale" is satisfied trivially — and there is a window where the new index definition has not yet
appeared in `stats` at all, so the first poll can return before the index is even registered. It
buys ordering, not freshness. Every fixture still waits again after seeding, which is what actually
matters.

### F8 — The `ImportScope` trick ports BETTER here than it works in CronosCore

CronosCore's `ImportScope` stops indexing and disables subscriptions for the duration of a bulk
import, then restarts indexing, waits once, and only then re-enables subscriptions. It turns N racy
incremental catch-ups into one deterministic settle.

Its stated limitation is that `StopIndexingOperation` is **database-global**, so a parallel test
would see indexing frozen — CronosCore gets away with it only because its fixtures are serial and
share one database.

**Spark has a database per test case.** Maintenance operations are database-scoped, so the same trick
is naturally isolated here: freezing indexing on `InitializeAsync_410` cannot affect any other test.
The constraint that makes it awkward in CronosCore does not apply to us.

### F9 — Synchronous waits block the thread pool under the new parallelism cap

`RavenIndexingExtensions.WaitForIndexing` is synchronous (`Thread.Sleep(100)`, blocking
`admin.Send`), and ~55 of the 65 call sites invoke it from `async` test methods.
`WaitForIndexingAsync` is only `Task.Run(...)` over the same blocking code — it moves the block, it
does not remove it. With `maxParallelThreads: "0.5x"` those threads are now scarcer by design.

### F10 — "Wait until no index is stale" is vacuous on an empty database

Found by probing rather than reading, after the question "if there are no indexes yet, how can it
wait for the replacement swap?". Measured on a fresh per-test database:

| Probe | Result |
|---|---|
| Indexes present | **0** |
| `WaitForIndexingAsync()` after a write | returns in **3 ms** |
| Indexes after the wait | still **0** |
| Then query | 1 hit, and it *creates* `Auto/Things/ByName` |

The staleness condition is universally quantified over the index set, so an empty set satisfies it
instantly. On a fresh database — the starting point of **every** fixture, since each test gets its
own — the wait promised nothing.

What actually made the ubiquitous seed-then-query pattern correct was **RavenDB**, which blocks on
the first creation of an auto-index. Not our wait. The same vacuity applies to `SeedAsync`: with no
index yet covering the write, the server-side wait also has nothing to wait on.

### F11 — Deployment failure and slowness were the same exception

A missing or faulted index surfaced as `TimeoutException`, identical to "indexing could not keep
up". The two have nothing in common: one is fixed by retrying or raising a limit, the other never
is. The message had to carry a distinction the type should have been making.

## Decisions

- **D1 — One implementation, async-first.** Collapse both into a single async implementation with a
  synchronous wrapper (not the reverse). Keep the sync path's better semantics: filter `Disabled`,
  wait out side-by-side swaps, fail fast on `IndexState.Error`, and always dump index errors.

- **D2 — Keep the side-by-side wait; fix the comment (F3).** Behaviour is correct and battle-tested;
  only the description is wrong.

- **D3 — One shared, configurable timeout.** A single default on the testing library, overridable
  per call, replacing the two independent 1-minute constants. Delete the dead
  `SparkSubscriptionOptions.NonStaleIndexTimeout`/`WaitForNonStaleIndexes` rather than leave a third
  number that looks authoritative and does nothing.

- **D4 — Adopt `WaitForIndexesAfterSaveChanges` for seeding (F4).** Add a seeding helper that opts
  the session into a server-side wait with `throwOnTimeout: true`. This removes the *need* for a
  post-seed global poll in the common "write then query" case, which is the bulk of the 65 call
  sites. Keep the global wait for cases with no single owning session (Smuggler imports, writes made
  by background workers).

- **D5 — Do not make timeouts CI-aware.** Tempting, but it treats the symptom: a wait that needs
  longer on CI is either racing something or genuinely broken. Correct waits are fast on both.
  CronosCore reached the same conclusion (its options are not environment-aware).

- **D6 — Consolidate the polling helpers, prioritising failure behaviour (F6).** One shared
  `WaitUntilAsync` that always throws with elapsed time and a caller-supplied description. The two
  helpers that currently swallow timeouts are the actual bug.

- **D7 — The negative assertion needs a real signal, not a longer sleep.** For
  `SyncActionSubscriptionWorkerE2ETests.cs:278`, waiting longer only makes the test slower and
  equally unsound. Establish a positive consistency point — process a *subsequent* action through
  the same worker and assert the first was not redelivered — so the assertion rests on observed
  progress rather than elapsed time.

- **D8 — Rate-limit test: out of scope here.** `RateLimitTests.cs:47` is an E2E test whose 11 s wait
  is coupled to Fleet's configured window. Fixing it properly means making that window configurable
  for tests, which is a production-config change. Flagged, not done.

- **D9 — "Settled" means deployed AND up to date (F10).** `WaitForIndexingAsync` takes
  `expectedIndexes`; a wait cannot succeed until those definitions exist. `SparkTestDriver` tracks
  what it deployed and passes the names automatically via `WaitForIndexesAsync`, so the safe thing
  is also the default thing. `DeployIndexesAsync` folds its separate registration poll into this
  one wait — one condition, one mechanism.

  **Auto-indexes sit on one side of this and not the other.** They are held to the same *staleness*
  bar as declared indexes (a stale auto-index is exactly what returns the wrong rows), but they
  cannot participate in the *deployment* check, because they do not exist until a query creates
  them and their names are not knowable up front. Nothing can close that gap from our side; RavenDB
  blocking on first creation is what covers it.

- **D10 — Deployment failures throw `RavenIndexDeploymentException`, not `TimeoutException` (F11).**
  Timeout keeps its ordinary meaning: healthy indexes that did not catch up in time. The new type
  exposes `FaultedIndexes` and `MissingIndexes` so a test can assert on the cause rather than
  string-matching a message.

## Acceptance criteria

1. One index-wait implementation; both entry points route to it and agree on semantics.
2. Async-first — no blocking of thread-pool threads from `async` tests.
3. Disabled indexes filtered; side-by-side swaps awaited; `IndexState.Error` fails fast with the
   actual index errors in the message, from every entry point.
4. A single configurable default timeout; the dead 2-minute options removed.
5. A seeding helper using `WaitForIndexesAfterSaveChanges(throwOnTimeout: true)`, with the
   write-then-query race closed at the write rather than by a later global poll.
6. One shared polling helper that always throws with elapsed time and a description; no helper
   silently swallows a timeout.
7. `SyncActionSubscriptionWorkerE2ETests.cs:278` and
   `MessageSubscriptionManagerLifecycleTests.cs:70` no longer depend on a fixed sleep.
8. A wait cannot succeed while an index it was told to expect is absent; a missing or faulted
   index throws `RavenIndexDeploymentException`, while healthy-but-stale still throws
   `TimeoutException`.
9. The vacuity of an unqualified wait on an empty database, and RavenDB's auto-index blocking that
   compensates for it, are both pinned by tests rather than left as folklore.
10. Full suite green, with the wall-clock cost measured and stated rather than assumed.

## Results

Controlled benchmark, same 8-core machine, build excluded from timing (`--no-build`), runs
back-to-back:

| | Run 1 | Run 2 | Run 3 | Outcome |
|---|---|---|---|---|
| **master** (unconstrained → 8-way here) | 132 s ❌ **1 failed** | 126 s | 120 s | **2 / 3 green** |
| **branch** (`0.5x` → 4-way here) | 146 s | 141 s | 122 s | **3 / 3 green** |

Two things worth stating plainly:

1. **The flake reproduced locally on master** — run 1 failed, on a machine with no CI load at all.
   That is the strongest evidence we have that this is a real resource/concurrency problem and not
   a CI-environment quirk.
2. **It is not free.** Mean wall-clock goes from ~126 s to ~136 s, roughly **8% slower**. An earlier
   note in this PR claimed the cost was nil; that was based on uncontrolled runs taken between
   builds, and the controlled comparison does not support it. ~10 s on a two-minute suite is a fair
   price for removing a class of failure that costs a full CI re-run (~4 min) whenever it hits, but
   it is a real trade, not a free win.

Sample size is three runs per side — enough to establish the direction and to catch the flake, not
enough to put a confidence interval on the 8%.

Final suite: **1376 tests** (four added for the wait semantics above).

Secondary effects, not separately timed: 24 explicit index waits removed (writes now settle
server-side as part of the transaction), and every remaining wait is awaited rather than blocking a
thread-pool thread — which matters more on CI's 4 cores than on the 8 measured here.

## Out of scope

- `RateLimitTests.cs:47` (D8).
- Adopting the `ImportScope` stop-indexing pattern (F8) — a real opportunity, but it belongs with a
  bulk-import workload we do not currently have in the unit-test suite.
- CI-aware timeouts (D5).
- Reworking `MintPlayer.Spark.E2E.Tests`' host-readiness polling, which is already a bounded poll
  with good diagnostics.
