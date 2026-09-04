# The test-teardown flake: cause, fix, and what it retires

**Status:** cause established and reproduced on demand; fix applied to `SparkTestDriver` and
validated against the reproduction. Full-suite-under-load validation in progress at time of writing.

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

| Variant | Failures | Slowest teardown |
|---|---|---|
| Before | 13 / 48 | 21.8s |
| After (4 consecutive runs) | **0 / 192** | 16.2 – 19.2s |

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
