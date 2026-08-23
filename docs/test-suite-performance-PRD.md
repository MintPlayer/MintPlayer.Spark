# Test-suite performance and reliability — findings

**Status:** investigation complete; some work shipped, the rest deliberately not.
**Branch of record:** `feat/security-json-in-core` (the shipped parts are in commits up to `fa2897d`).
**Companion:** `docs/test-suite-performance-plan.md`.

The suite was slow (3m20s wall for 1704 tests) and intermittently flaky. This records what was
measured, what was fixed, what was rejected, and — at length — the diagnostic mistakes, because
they cost more than the work did.

---

## 1. How to measure this suite

```
dotnet test <proj> --logger "trx;LogFileName=baseline.trx"
```
then parse `UnitTestResult/@duration` per test.

⚠️ **TRX does not attribute `IAsyncLifetime.InitializeAsync` to the test.** Per-case database
creation and `SparkEndpointFactory` host boot are therefore invisible in it. One class showing
26 cases / 0.8s of "test time" actually took **13s** wall. An early conclusion here — "only ~18% of
the cost is fixed setup" — was drawn from TRX alone and was badly wrong. Setup dominates.

Look at the **distribution**, not the total: `min / median / max` per class finds the single
pathological test. `min≈0, median≈0, max=47.5` across 18 cases is one bad test, not a slow class.

## 2. Where the time went

Baseline: **429s of CPU across 1704 cases, 200s wall** — parallelism was buying only ~2.1×.

- **The top 20 tests were 49% of all CPU time.**
- **The OIDC family alone was 42%** (212 cases, 182s). `OidcTestHost.Factory` is an *instance*
  field, so each of ~144 tests boots a complete host including ASP.NET Identity and an RSA key
  generation.
- **676 database create/delete cycles per run** (674 driver cases + 2), plus ~19 more that tests
  create directly and never drop.
- **337 `SparkEndpointFactory` host boots**, 207 of them per test case rather than per class.
- **342 of 674 driver-derived cases (51%) never write a document at all.** They take a private
  RavenDB database purely to obtain an `IDocumentStore` for a service constructor or a factory.

## 3. What shipped

| Change | Effect |
|---|---|
| Dedicated threads in `ReflectionCacheTests` concurrency test | **47.5s → 0.09s** |
| `AsyncWait` timeout guidance + cron timeout raised | flake removed |
| `WaitForIndexingAsync` deadline moved to loop bottom | fixed a **misdiagnosis** |
| `IndexWaitSemanticsTests` asserts the property, not elapsed time | flake removed |
| `RqlRecorder` + four handler leaks fixed | correctness |
| `SparkSharedDatabase` / `SparkSharedTestDriver` | first migrated class **13s → 647ms** |
| `AGENTS.md` shipped + synced by MSBuild | — |

Net: **3m20s → 1m54s**, verified green five consecutive times.

### The single worst test

`ReflectionCacheTests.GetOrAdd_factory_runs_exactly_once_under_concurrent_access` was **11% of the
entire suite**, and none of that time tested the cache. A `Barrier(64)` only releases once every
participant is *simultaneously blocked*; `Task.Run` puts them on the thread pool, which starts near
core count and injects further threads at ~1–2/second. It spent ~40s on thread injection to
exercise a 20ms sleep. Dedicated `new Thread` removed the dependency on pool heuristics entirely.

### A real defect found behind a flaky assertion

`WaitForIndexingAsync` tested its deadline at the **top** of the loop, so a caller could time out
having never looked: stopwatch starts, thread is descheduled, body never runs, `statistics` stays
null — and `MissingIndexes(null, expected)` then reports **every** expected index as missing. The
failure said *"this index was never deployed"* about a database nobody had queried, pointing at the
wrong subsystem entirely.

General form: **a loop that can exit before doing any work, whose failure path then interprets "no
data" as evidence.**

## 4. The two drivers

|  | `SparkTestDriver` | `SparkSharedDatabase` + `SparkSharedTestDriver` |
|---|---|---|
| Database | per test **case** | per test **class** |
| Derives from `RavenTestDriver` | the test class does | **only the fixture does** |
| Isolation | total | class-local |

**The design move:** put the `RavenTestDriver` inheritance on an `IClassFixture<>`, not on the test
base class. `RavenTestDriver` ties store lifetime to the instance, and xUnit builds a fresh
test-class instance per case — so inheriting it on the test class *forces* per-case. Both still
share one embedded server (`ConfigureServer` is `public static`; the server is a static field,
verified by reflecting over `Raven.TestDriver.dll`).

**Rejected: CronosCore's single process-wide database.** xUnit runs test classes concurrently;
NUnit is sequential by default. Their design is safe there and would not be here. Keeping the
database as the isolation boundary means parallelism is untouched and no id-scoping scheme is
needed at all.

**Rejected: their lazy-init primitive.** `static bool isInitialized` checked then set is
check-then-act; two threads both configure and the second throws. Use a type initialiser (the CLR
guarantees once, with publication). If async init is ever needed, note that plain `Lazy<Task<T>>`
**caches a faulted task forever**, turning one transient server-start failure into an entirely red
run.

## 5. What was NOT shipped, and why

15 further class migrations, a `Dispose` override on the shared fixture, and a RavenDB artifact
cleanup MSBuild target are **stashed, not committed** — they build clean and passed a 68-case
targeted run, but no full green suite was ever obtained, because the environment broke mid-session
(§6). Shipping test infrastructure that has not been demonstrated green is not worth the risk.

`git stash list` → *"test-suite: 15 shared-driver migrations, Dispose override, RavenDB cleanup
target (unverified — suite could not be run green)"*.

## 6. ⚠️ The diagnostic failure — read this before resuming

Mid-session the suite went from green to **690 of 1704 failing**, all with
`TimeoutException: Server failed to start in 60 s`, in classes nobody had touched. It appeared with
no code change, shortly after every `dotnet`/`testhost` process was killed mid-run.

**Five hypotheses were proposed before the decisive measurement was taken. All five were wrong:**

| Hypothesis | How it was eliminated |
|---|---|
| Contaminated by a concurrent build | The failures were real |
| `RavenTestDriver.Dispose` tears down the shared server | Override changed 690 → 690 |
| The developer's local `Raven.Server` was interfering | Innocent; it binds 8080, and a port clash fails **fast**, not silently |
| Degraded output directory | A clean directory still failed |
| Licence validation hanging | The server starts fine with the licence, manually |

**The bisect that settled ownership took four minutes** — `git stash`, re-run: **691 failures
without the changes, 690 with them.** It should have been the first thing done, not the sixth.

Worse: the second hypothesis was written into a memory file *and* into the shipped `AGENTS.md` as
established fact, with a "measured at 690 failures" figure that was never measured. Both were
corrected.

**One failure mode was genuinely self-inflicted and is worth knowing.** A drafted cleanup target
deleted `$(OutDir)RavenDBServer` on `BeforeTargets="VSTest"`. The `RavenDB.Embedded` package copies
that ~600MB server in via its own `CopyRavenDBServer` target on `PrepareForRunDependsOn`, which runs
during **build** — so the cleanup deleted the server between the build that provided it and the
tests that needed it. Every test then failed instantly with `FileNotFoundException: Server file was
not found`, and **a plain rebuild does not restore it**, because the copy is incrementally tracked
via `FileWrites`; `obj/` must be deleted too. **Never clean `RavenDBServer`; clean only the
`RavenDB` data directory and the `.raven-cluster-topology` files.**

### Still unexplained — and the degraded-directory theory is ALSO disproven

Do not read the table above as "the directory was the cause". **A completely clean directory still
failed, 692 of 1704.** The root cause was never established.

What is known: the server prints its banner and then emits nothing for 60s under `EmbeddedServer`,
while the same binary launched by hand (`--RunInMemory --Setup.Mode=None`) starts in seconds, with
or without the licence. One class did once pass in **2m25s**, so the server *can* start — just far
too slowly for the 60s limit, especially under parallel load. `Lazy` then caches that first failure
for the rest of the process, which is why one transient fault presents as suite-wide breakage.

Most likely wedged OS state from the mid-run process kill. **A reboot was never tried** and is the
cheapest next step.

### Recovering a directory Windows will not let you rebuild

Clearing the output directory left a **delete-pending** file: marked for deletion, but an open
handle keeps the name reserved. The tell is that `ls` (MSYS layer) shows the file while PowerShell
`Test-Path` returns **False** — the two disagree all the way through this class of problem, and
**Windows is the authoritative one**. MSBuild then fails `MSB3021 … Access to the path is denied`.

Fix: **rename the parent directory** (`Rename-Item bin bin.stale`). A delete-pending *file* cannot
be recreated, but its parent *directory* can still be renamed, sidestepping the reserved name. Two
`rmdir /s /q` passes were also needed first; the first reported the tree non-empty and left it.

## 7. Rules that came out of this

- **Bisect before theorising.** Stash and re-run is cheap and decisive.
- **Failures in classes you did not touch are a clue about *scope*, not proof of contamination.**
  "Everything after a point, regardless of class" points at shared process state.
- **Never write an unverified diagnosis into documentation**, and never attach a measured-sounding
  number to a mechanism that was not measured.
- **A polling timeout is a failure bound, not a success bound.** The wait returns the instant the
  condition holds, so a generous timeout costs a passing run nothing. Tight ones buy only flakiness.
- **Never assert on elapsed time to prove something was fast.** Under contention that measures the
  machine.
- Killing `dotnet`/`testhost` does **not** clean up the embedded RavenDB server or its directories,
  and may leave them unusable.
- A process with no readable path/owner and access-denied on kill is running in **another security
  context** — not necessarily an orphan of yours.
