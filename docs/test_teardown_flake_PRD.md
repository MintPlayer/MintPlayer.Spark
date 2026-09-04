# The test-teardown flake, and the driver restructuring question

**Status:** SOLVED. Cause established, reproduced on demand, fixed, and validated against both the
reproduction and the full suite under the load that used to break it.

**Part 1** is the flake: cause, reproduction, fix. **Part 2** is the separate question of what the
per-test database lifecycle COSTS, and the three restructuring designs considered on 2026-09-04.

## The failure

```
Raven.Client.Exceptions.RavenException : System.TimeoutException: Waited for 00:00:15 for task with
index 2168 to complete. Last commit index is: 2744. Number of errors is: 0.
  at Raven.Server.ServerWide.AbstractRaftIndexNotifications`1.WaitForIndexNotification(Int64, TimeSpan)
  at Raven.Server.Web.System.AdminDatabasesHandler.WaitForDeletionToComplete(...)
  at Raven.Server.Web.System.AdminDatabasesHandler.Delete()
```

A test that has already **passed** fails while its database is being torn down. Intermittent, never
reproducible on demand, and blamed on five different things over three sessions.

## Cause

**CPU starvation.** `RavenTestDriver` deletes a database per test case from the store's
`AfterDispose` event, and the server's delete handler blocks on `WaitForIndexNotification` for
`TimeToWaitForConfirmation ?? TimeSpan.FromSeconds(15)`. When the machine is saturated that
notification does not arrive inside the budget, and teardown throws.

Nothing about the test that fails is special. It is whichever test happens to be disposing when the
machine is busy.

## Reproduction

`tests/MintPlayer.Spark.FlakeRepro/` — a separate project, because it saturates every core on
purpose and would wreck anything running alongside it. Roughly 90 seconds:

```
dotnet test tests/MintPlayer.Spark.FlakeRepro/MintPlayer.Spark.FlakeRepro.csproj
[repro] cores=8 workers=4 cycles=48 created=48 failures=13 slowest-delete=21,8s
```

Three ingredients, each established by removing it:

| Recipe | Result |
|---|---|
| CPU saturation, sequential, empty databases | 0 failures, slowest delete **0.1s** |
| + 4 concurrent workers | 0 failures, slowest delete **0.7s** |
| + an index, 300 documents and a query per database | **13 of 48 fail**, slowest **21.8s** |

The third ingredient is the one that was missed for a long time. An **idle** database deletes in
0.1s even on a fully starved machine. A database with an index to tear down takes 21.8s. Deleting is
not what costs — deleting something the server is still working on is.

## Four hypotheses that died on this evidence

Recorded because each looked strong, and three of them were argued confidently before being measured.

| Hypothesis | How it died |
|---|---|
| **Accumulated raft state** — ~676 create/delete commands per run overwhelm the notification pipeline | The reproduction fails at raft **index 17, last commit 28**. The theory predicted failures only late in a run at high indices |
| **Messaging pumps holding the database open** — the failures were only ever seen in the three messaging E2E classes, the only ones running background loops against the store | The reproduction has no messaging, no hosted services, no background work, and fails anyway |
| **Disk I/O / cold cache after a rebuild** | `RavenTestDriver.CreateGlobalDocumentStore()` unconditionally appends `--RunInMemory=true` (verified in the shipped 7.2.5 binary). The databases were never on disk |
| **Serialise deletes with a semaphore** | Measured: teardown went from 16–19s to **35.7–46.7s**. A client-side queue cannot make the server's single apply loop faster; it just adds a second queue in front of the first |

## The fix

`SparkTestDriver.DisposeAsync` sends its own delete first, with a zero confirmation wait:

```csharp
store.Maintenance.Server.Send(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
{
    DatabaseNames = [store.Database],
    HardDelete = true,
    TimeToWaitForConfirmation = TimeSpan.Zero,
}));
```

Server-side, `remaining = timeToWaitForConfirmation - sp.Elapsed` is negative immediately, so
`WaitForIndexNotification` is skipped: the raft delete command is submitted and the handler returns.
The driver's own `AfterDispose` delete then finds the database gone and swallows the resulting
`DatabaseDoesNotExistException` itself.

**Isolation is unchanged.** The database is still deleted; only the confirmation is unawaited. That
is safe because nothing may observe this database again — the name is unique per case
(`InitializeAsync_{N}`) and the embedded server is in-memory and dies with the process.

**This is not the fix that was rejected before.** An earlier attempt caught and swallowed the
`TimeoutException`, which would have hidden a genuine shutdown defect. Here the exception is not
caught: it is never raised, because the server is never asked to wait.

### Measured

On the reproduction:

| Variant | Failures | Slowest teardown |
|---|---|---|
| Before | 13 / 48 | 21.8s |
| After (4 consecutive runs) | **0 / 192** | 16.2 – 19.2s |

On the **full suite**, under the identical eight-spinner load that produced the 191-failure run:

| | Tests | Failures | Teardown timeouts | Duration |
|---|---|---|---|---|
| Before | 1922 | **191** | many | 23m 46s |
| After | 1922 | **0** | **0** | 27m 37s |

The suite completes on a machine with every core saturated. Wall-clock is longer because the run now
finishes its work instead of failing out of it early.

## What this retires

The historical failure recorded as *"root cause NEVER established"* — hundreds of
`TimeoutException: Server failed to start in 60 s`, attributed to wedged OS state from a mid-run
process kill, with a reboot suggested as the cheapest untried step, and five theories disproven.

It is **the same starvation, one severity up**, and it reproduces on demand: run the load generator
at `ThreadPriority.Highest` and the embedded server never finishes starting, after which
`RavenTestDriver`'s `Lazy` caches the faulted store factory and every remaining test in the process
inherits the same stale exception. That `Lazy` is the amplifier that turns one slow start into
suite-wide breakage.

No reboot is required. No degraded directory is involved. `CpuLoad`'s priority parameter selects
which of the two failures you get, which is the cleanest available proof that they are one bug.

## Still open

- **Teardown still blocks up to 19s under saturation.** The failure is gone; the wall-clock cost is
  not. That time is the driver's own `AfterDispose` delete, which cannot be suppressed through a
  supported API — `_documentStores` is private and the deleting lambda is neither virtual nor
  gated by any flag on `GetDocumentStoreOptions`. Escaping it means not using `GetDocumentStore` at
  all, which is a fork of driver behaviour for a wall-clock-only gain.
- **One database per class** (`SparkSharedDatabase`, already shipped, used by 1 of 86 classes) would
  cut database count sharply, but 52 of the 84 per-case classes contain at least one hazard it
  documents — unscoped `HaveCount(`/`BeEmpty()`, fixed ids, `StopIndexingOperation`,
  compare-exchange. No mechanical migration exists; each needs judgement.
- **Lowering `maxParallelThreads` below `0.5x`** is a mitigation, not a cure: it lowers the
  probability of the alignment and costs roughly double the wall-clock. Not recommended.

## Notes for whoever runs the reproduction

- Read the **numbers**, not the pass/fail. Zero failures on an idle machine proves nothing — compare
  with and without a candidate change under the *same* load.
- `SparkEmbeddedServer.ReportUrls` prints the server's URL once per process under
  `--logger "console;verbosity=detailed"`. The test server is not the `Raven.Server.exe` service on
  8080; it is a `dotnet.exe` hosting `RavenDBServer/Raven.Server.dll` on an ephemeral port, so
  `tasklist | findstr Raven` finds the wrong process.
- A load generator must clean up on **every** exit path. Eight spinners survived a killed script
  during this work and pinned every core until they were found by hand; anything measured in that
  window is worthless.

---

# Part 2 — Restructuring the driver: three designs, and which to build

The flake above is fixed. This part is about the **cost** that remains, which is a separate question
and was measured on 2026-09-04 rather than argued.

## The cost is real, and it is the database lifecycle

| Measurement | Value |
|---|---|
| Suite (1922 tests, idle 8-core, warm) | **92s wall / 264 CPU-s** |
| Embedded RavenDB server process | **60% of all CPU** |
| One database: create + index deploy + delete | **0.23 CPU-s** (0.28s thread-time) |
| + 300 documents and a query | +0.095 CPU-s |
| 676 databases per run | **≈155 CPU-s — 45–60% of suite CPU**, ~47s of the 92s wall |
| Serialised (`MaxParallelThreads=1`) | **205s wall / 216 CPU-s** |

So roughly **half the CPU and half the wall-clock** of this suite is the per-test database
lifecycle, and nearly all of the embedded server's CPU. Nothing else dominates.

Two corrections to the record fall out of this:

- An earlier claim in this document's Part 1 discussion — that creating and dropping databases is
  *cheap* — measured the **latency of one delete** (0.1s idle). That is true and irrelevant to
  aggregate cost. 676 × 0.23 CPU-s is the number that matters.
- `docs/test-suite-performance-PRD.md` records that TRX **does not attribute
  `IAsyncLifetime.InitializeAsync` to the test**, so per-case database creation is invisible in it.
  The "~18% is fixed setup" figure drawn from TRX is wrong: setup dominates. One class showing 0.8s
  of "test time" took 13s wall.

## Design A — one static driver, one database, per-class data cleanup

*One process-wide `RavenTestDriver` hosted by a harness; a single database; record what a class adds
and delete it afterwards; all tests serial.*

**Rejected.** Not on cost — on correctness, and on two facts that make it unbuildable as stated.

**It cannot be one database for the run.** `dotnet test` runs each test assembly in its own process,
each with its own embedded server. With 5+ test assemblies, "one database" means "one per assembly";
the churn in the others is untouched.

**Three classes cannot be migrated at all**, and they are load-bearing:

| Class | Why |
|---|---|
| `_Infrastructure/IndexWaitSemanticsTests.cs:84`, `Services/RavenIndexHelperSmokeTests.cs:88` | Call `StopIndexingOperation` — database-wide — and never restart it. Under a shared database this deadlocks every class that runs afterwards |
| `Messaging/MessagingSubscriptionCountTests.cs:103` | Enumerates subscriptions database-wide and asserts on the set. This is the test proving the single-subscription property; it cannot be weakened |
| `_Infrastructure/IndexWaitSemanticsTests.cs:31,109` | Assert on **auto-index** counts, which become non-deterministic once another class has queried the same database |

**State a document-delete cannot clean:** static index definitions, auto-indexes, index errors,
paused indexing, compare-exchange values (the cron and migration tests use *fixed keys with
`index: 0`*, so a second run in the same database throws `ConcurrencyException`), subscriptions, and
expiration configuration.

**Background writers defeat id-recording.** `MessageLanePump`, `MessageReaper`, `MessageFeeder` (a
real subscription worker), `SparkCronScheduler` and the expiration sweeper all write documents on
their own sessions. Cancellation is not a barrier, so writes land *after* a cleanup pass — and a
cleaner can delete a document a still-running pump is about to rewrite.

**Scale of rewrite:** 59 files with 285 literal fixed ids, plus 117 unscoped-cardinality assertions.

**The bug class that escapes review:** *false-pass by contamination*. An assertion that a document,
index or subscription exists is satisfied by a previous class's leftover, so a real regression goes
green — invisible in review, because the leftover is not in the file being read. Background writers
make it intermittent.

**And the performance case does not hold.** Serial execution measured **205s vs 92s**. The lifecycle
saving roughly cancels the serialisation penalty: a wash on 8 cores, a modest win on a 2-core
runner. It spends all isolation to buy approximately nothing on a developer machine.

## Design B — test lanes: a database per lane, lanes parallel, serial within

**Feasible, poor value.** Lane ≡ xUnit collection is correct — xUnit parallelises by collection and
the repo already uses that shape (`_Infrastructure/FleetE2ECollection.cs:7`). But:

- There is **no auto-assignment**. All 86 classes need a hand-written `[Collection("Lane-N")]`, and
  lane balance is static and manual — one slow lane sets the wall clock.
- Sharing a database *across classes* within a lane inherits every hazard of Design A, minus the
  serial penalty.
- Giving each lane a database *per class* is simply today's design with coarser reuse.
- New bug class: **lane-assignment drift.** A class added without a `[Collection]` attribute lands
  silently in the default collection with different isolation semantics, and nothing fails.

## Design C — adopt `SparkSharedDatabase`, class by class ✅ recommended

The per-class fixture already ships and is used by **1 of 86 classes**. It keeps parallelism, and its
isolation boundary is a real database, so classes still cannot see each other.

- **Removes ~75% of the 155 CPU-s** and cuts wall to **~50s** — better than Design A on both axes.
- Isolation lost is **class-local**, bounded by the file you are reading, and already documented at
  `SparkSharedDatabase.cs:38-62`.
- Measured precedent: the first migrated class went **13s → 647ms**.
- **51% of driver cases never write a document** — those migrate with no assertion changes at all
  and are where to start.
- The three unmigratable classes above simply stay on `SparkTestDriver`. Both drivers ship; neither
  replaces the other. That is the design's own stated intent.

Cost: per-class judgement. 52 of 84 classes contain at least one hazard (unscoped `HaveCount(` /
`BeEmpty()`, fixed ids, `StopIndexingOperation`, compare-exchange). There is no mechanical
migration — but unlike A and B, it can be done **incrementally**, one class at a time, with each
step independently verifiable and revertible.

## Spikes to run before committing to Design C

1. **Migrate the cheapest 10 classes** — pick from the 51% that never write a document. Measure suite
   CPU and wall before and after. Confirms the ~75% projection on real classes rather than a model.
2. **An analyzer or test for the hazards.** Can `HaveCount(`/`BeEmpty()`/`CountAsync()` on a shared
   database be detected statically? If yes, migration becomes mechanical for the easy majority and
   the residue is a reviewable list.
3. **Ordering independence.** Run a migrated class with its tests shuffled. xUnit does not guarantee
   order within a class; a class that passes only in declaration order is a latent contamination bug.
4. **Measure on 2 cores** (`--settings` with `MaxParallelThreads=2`) — the CI shape. The CPU floor,
   not the wall-clock, is what CI is bound by.

## What is NOT recommended, stated plainly

- **Do not** force serial execution. Measured 2.2× slower wall for an 18% CPU saving.
- **Do not** build a single shared database for the run. It cannot exist across assemblies, three
  classes cannot join it, and its failure mode is silent false-passes.
- **Do not** invent a lane scheduler. xUnit collections already are one, and the per-class fixture
  gets most of the benefit with local, reviewable risk.
