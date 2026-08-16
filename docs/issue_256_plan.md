# Plan — Issue #256: deterministic test waiting

**PRD:** [issue_256_PRD.md](issue_256_PRD.md) · **PR:** [#257](https://github.com/MintPlayer/MintPlayer.Spark/pull/257)
**Branch:** `fix/issue-256-test-parallelism`

**All milestones complete.** M7 was added during implementation and was not in the original plan.

| | Milestone | Commit |
|---|---|---|
| M0 | Parallelism cap + init-failure masking | `0c1873b` |
| M1 | One index-wait implementation | `c278ee3` |
| M2 | Single timeout, dead options deleted | `c278ee3` |
| M3 | `SeedAsync` (`WaitForIndexesAfterSaveChanges`) | `5afd607` |
| M4 | One polling helper (`AsyncWait`) | `5afd607` |
| M5 | Fixed sleeps removed | `5afd607` |
| M6 | Deploy-time index verification | `5afd607` |
| M7 | Async all the way; sync API deleted | `9d86cc3` |

---

## M0 — Parallelism cap + init-failure masking ✅ DONE (`0c1873b`)

`xunit.runner.json` with `maxParallelThreads: "0.5x"`, null-guarded `DisposeAsync`, corrected
per-test-case documentation.

---

## M1 — One index-wait implementation

**Files:** `libs/testing/MintPlayer.Spark.Testing/RavenIndexingExtensions.cs`,
`libs/testing/MintPlayer.Spark.Testing/RavenIndexHelper.cs`,
`libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs`

- Make the async form the real implementation (`await Task.Delay`, `SendAsync`), with the sync
  extension as a thin blocking wrapper for the ~55 existing sync call sites. Do **not** keep
  `Task.Run` over blocking code (PRD F9).
- Single done-condition, taking the better half of each: filter `IndexState.Disabled`, require every
  remaining index non-stale, and require no `ReplacementOf/` index to remain (PRD D2 — keep the
  behaviour, **fix the comment** at `RavenIndexingExtensions.cs:30`, which currently says the
  opposite of what the code does).
- Fail fast on `IndexState.Error` from **both** entry points, with the `GetIndexErrorsOperation` dump
  in the message. `RavenIndexHelper`'s current `InvalidOperationException` + "check Raven Studio"
  loses the one piece of information the caller needs.
- Timeout message lists the still-stale index names *and* the elapsed time — the sync path currently
  reports neither.
- `RavenIndexHelper.WaitForNonStaleAsync` becomes a forwarder so its 9 call sites keep working.
- Fix the `SparkTestDriver` shadow comment: it claims "one implementation, one behaviour", which only
  becomes true with this milestone.

**Tests:** extend `_Infrastructure/RavenIndexHelperSmokeTests.cs` — both entry points agree; a
disabled index does not hang the wait; the timeout message names the stale indexes.

---

## M2 — Single configurable timeout, and delete the dead options

**Files:** the two above, plus
`libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/SparkSubscriptionOptions.cs`,
`tests/MintPlayer.Spark.Tests/SubscriptionWorker/SparkSubscriptionOptionsTests.cs`

- One `DefaultTimeout` on the testing library, overridable per call. Remove the second independent
  `TimeSpan.FromMinutes(1)` in `RavenIndexHelper.cs:32`.
- Delete `SparkSubscriptionOptions.WaitForNonStaleIndexes` and `NonStaleIndexTimeout` — nothing in
  `libs/` reads them (PRD F2). Drop the assertions that pin their defaults.
- Check the docs for references to the removed options before deleting.

---

## M3 — `WaitForIndexesAfterSaveChanges` seeding helper

**File:** `libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs` (+ the testing README)

Add a seeding helper that opens a session, applies the caller's writes, and saves with
`session.Advanced.WaitForIndexesAfterSaveChanges(timeout, throwOnTimeout: true)` — the server holds
the write until the indexes covering that transaction have caught up (PRD F4/D4).

```csharp
protected async Task SeedAsync(Func<IAsyncDocumentSession, Task> seed, TimeSpan? timeout = null)
```

- `throwOnTimeout: true` is the point: the default swallows the timeout and returns a write that may
  not be queryable, which is the failure mode we are removing.
- Document when it does **not** apply: Smuggler imports and writes made by background workers have
  no owning session, so those keep the global wait.
- Convert a representative handful of seed-then-query fixtures to it rather than all 55 at once —
  enough to prove the pattern without an unreviewable diff. Note the remainder in the PR.

**Tests:** a fixture that seeds via the helper and queries immediately **without** any explicit
index wait must pass reliably.

---

## M4 — One polling helper

**New:** `tests/MintPlayer.Spark.Tests/_Infrastructure/AsyncWait.cs`

`WaitUntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout, TimeSpan? interval)`
that **always throws** on expiry, with the description and elapsed time.

Replace the four current helpers (PRD F6):
- `WaitForMessageAsync` (`MessageSubscriptionWorkerE2ETests.cs:140`) — keep its rich per-handler
  diagnostics by passing them through the description.
- `WaitForAsync` (`SyncActionSubscriptionWorkerE2ETests.cs:150`) — same, with `Status`/`LastError`.
- `WaitUntilAsync` (`SparkCronSchedulerTests.cs:134`) — **behaviour change**: currently returns
  `bool`, so a timeout asserts as "expected True". Callers switch to relying on the throw.
- `WaitForCondition` (`SecurityConfigurationLoaderTests.cs:243`) — **behaviour change**: currently
  swallows the timeout entirely.

These two behaviour changes are the actual value here; the deduplication is secondary.

---

## M5 — Remove the two fixable fixed sleeps

- **`SyncActionSubscriptionWorkerE2ETests.cs:278`** (`Task.Delay(500)`, negative assertion). Per PRD
  D7, replace elapsed time with observed progress: push a second action through the same worker,
  wait for *it* to complete, then assert the first was not redelivered. If the worker has processed
  a later action, it has demonstrably had its chance at the earlier one.
- **`MessageSubscriptionManagerLifecycleTests.cs:70`** (`Task.Delay(200)`, "let the worker attach").
  Wait on an observable attached signal via M4's helper. If no such signal is exposed, say so in the
  PR rather than swapping one arbitrary constant for another.

`RateLimitTests.cs:47` stays (PRD D8) — fixing it needs Fleet's rate-limit window to be
test-configurable, which is a production-config change.

---

## M6 — Sweep and document

- Full suite: `dotnet test tests/MintPlayer.Spark.Tests/MintPlayer.Spark.Tests.csproj`, plus the
  source-generator and client projects. Compare wall-clock against the M0 baseline (1m50s) — the
  point is no regression.
- Re-run any failure in isolation before treating it as real (the suite has a load-sensitivity
  history — that is what this whole issue is about).
- Update `libs/testing/MintPlayer.Spark.Testing/README.md`: the seeding helper, when to use it
  versus the global wait, and the one-implementation guarantee.

---

## M7 — Async all the way (added during implementation)

Not in the original plan; added once M1 made an async implementation available.

- Deleted the synchronous `WaitForIndexing` extension **and** the `protected new WaitForIndexing`
  shadow on `SparkTestDriver`. No deprecation — no back-compat is required here, and leaving it
  would have left the trap in place: declared `new` rather than `override`, so calling it through a
  `RavenTestDriver`-typed reference silently got Raven's own implementation instead.
- Converted all 46 surviving call sites to `await …WaitForIndexingAsync()`. Blocking a thread-pool
  thread from an `async` test was always wasteful; it is worse now that M0 halves the pool available
  to tests.
- The `SeedAsync` conversion (M3) removed 24 waits outright before this pass, so the mechanical
  edit was smaller than the original 63-site count suggested.

## Benchmark

See the PRD's Results table. Headline: the flake **reproduced on master locally** (1 of 3 runs),
the branch was 3 for 3, and the cost is ~8% wall-clock — not the "no cost" claimed earlier in the
PR from uncontrolled measurements.

## Risks

- **M3 is the behavioural one.** `WaitForIndexesAfterSaveChanges` makes writes slower but
  deterministic. If a converted fixture starts timing out, that is a genuine finding — it means the
  index never covered that write — not a reason to revert to polling.
- **M4 changes failure modes on purpose.** Two helpers currently hide timeouts; tests that were
  quietly passing on a swallowed timeout will start failing. Investigate each rather than restoring
  the swallow.
- **Scope.** M1/M2 are self-contained. If M3–M5 grow, they split into a follow-up PR — M0–M2 are
  independently shippable and already reduce the flake surface.
