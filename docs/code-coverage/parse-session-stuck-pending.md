# Bug: every real-world upload parses zero files and stays `Pending` forever

For `C:\Repos\Coverage` (suggested home: `docs/parse-session-stuck-pending.md`).

Diagnosed 2026-08-13 from `mintplayer-ng-bootstrap`'s first upload (run
31694883768, commit `67262d58`). The upload is accepted, the build finalizes,
and **nothing is ever parsed** — `parseStatus: "Pending"`, `filesCount: 0`,
`error: null`, zero `FileCoverage` documents, badge `unknown`.

The messaging layer is **not** the problem. See "What this is not" below.

## Root cause

`ParseSessionRecipient` runs on the DI-scoped `IAsyncDocumentSession`, whose
`MaxNumberOfRequestsPerSession` is RavenDB's default **30**. That default is
deliberate and documented in Spark:

> `SparkMiddleware.cs:107-108` — "MaxNumberOfRequestsPerSession stays at Raven's
> default (30) — if a single method needs more headroom, use
> `SessionExtensions.IgnoreMaxRequests()`."

`ParseSessionRecipient` never takes that scope, and it spends one server
round-trip **per unique source file**:

| Line | Call | Requests |
|---|---|---|
| `:24` | `LoadAsync<Build>` | 1 |
| `:122` (via `:34`) | attachment read — fileList | 1 |
| `:122` (via `:43`) | attachment read — one per uploaded report | 13 |
| **`:67`** | **`LoadAsync<FileCoverage>` — one per unique file path** | **616+** |
| `:108` | `StreamAsync` for the summary | 1 |

The `touched` dictionary prevents *repeat* loads of the same document, so it is
exactly one round-trip per distinct file — not a cache miss that warms up.

616 is only the three libraries counted locally
(react 41 + vue 56 + web-components 519); the real 13-project upload is larger.
Either way the budget is gone **~28 files into the first report**, and Raven
throws:

```
InvalidOperationException: The maximum number of requests (30) allowed for this
session has been reached.
```

### Why the failure is invisible

`ParseSessionRecipient.cs:95-100` catches it and sets
`ParseStatus = "Failed"` + `Error = ex.Message` — but the next statement is

```csharp
await session.SaveChangesAsync(cancellationToken);   // :102
```

on the **same exhausted session**. `SaveChanges` is another request, so it
throws too and the diagnosis is never persisted. The exception escapes
`HandleAsync`, the worker marks the handler failed, retries on backoff, fails
identically each time, and dead-letters. What is left in the database is the
untouched `"Pending"` the upload controller wrote, with `error: null` — a
status that reads like "the worker never ran".

### Why it passes locally

The dev database holds **12** `FileCoverage` documents in total. Every fixture
upload is comfortably under 30 requests, so the ceiling is never reached. The
bug is invisible until the first upload from a real repository — which is
exactly when it fired.

## What this is not

Ruled out, so nobody re-treads it:

- **The parse worker is running.** `FinalizeBuildMessage` carries the *same*
  `[MessageQueue("coverage-parse-session")]` (`ParseSessionMessage.cs:9` and
  `:22`), and the build shows `status: "Finalized", finalizeReason: "Explicit"`
  — that message was consumed by the same worker, minutes after the parse
  message was not.
- **`AddRecipients()` registered fine.** The commit document carries
  `message` and `authoredAt`, which only `GitHubEventsRecipient.OnPush`
  (`:147-149`) ever writes — the upload controller sets only `Branch`
  (`UploadsController.cs:66`). So `spark-github-all` was consumed in production
  the same morning.
- **Not the deploy race.** The container was torn down at 11:33:56–11:34:18 UTC,
  7.5 minutes *after* the 11:26:09 upload. (See the latent bug below, though.)
- **Not the invalid-generic-queue-name bug** from `spark-handoff.md` §1: this app
  keeps only the non-generic catch-all recipient, so no invalid name is
  discovered and the manager starts normally.

## Fix

1. **Bulk-load the `FileCoverage` documents.** `session.LoadAsync<FileCoverage>(ids)`
   fetches up to 1024 ids in *one* round-trip. Collect the document ids for a
   parsed report first, load them in one call (chunk at ~512), then merge from
   the in-session cache. This turns 616 round-trips into 2 and removes the
   ceiling problem at its source rather than raising the ceiling.
2. **Take `using (session.IgnoreMaxRequests())` around `HandleAsync` anyway.**
   Even after (1), a 13-report upload spends 14 requests on attachments alone,
   and the count grows with the number of uploaded files. This is the remedy
   Spark documents for exactly this case.
3. **Persist the failure on a fresh session.** The `catch` at `:95` must not
   report through the session that just failed. Open a new session from the
   `IDocumentStore`, load the build, write `ParseStatus`/`Error`, save. As it
   stands, *any* session-level fault reports as `Pending` forever — the failure
   mode that made this take a diagnosis instead of a glance.
4. **Consider surfacing stuck sessions.** `FinalizeBuildsCronJob` already
   distinguishes `Timeout` from `Debounce`; a build finalized by `Timeout` with
   `Pending` sessions could mark those sessions `Failed ("never parsed")` so the
   UI and the badge stop implying work is still in flight.

## Latent bug found while diagnosing (separate, unverified)

`MessageSubscriptionWorker.ProcessBatchAsync` sets `Status = Processing` and
persists it *before* invoking the handler (`:70`, saved at `:141`), but the
subscription query only matches `Status = 'Pending'`
(`MessageSubscriptionWorker.cs:56`). A process that dies between those two
points leaves the message in `Processing`, where **no query will ever match it
again** — there is no lease expiry, visibility timeout, or reaper. A container
restart during a long handler silently drops the message. Not what happened
here, but a deploy during any slow parse would produce an identical-looking
symptom.

## Re-verification after the fix

No `mintplayer-ng-bootstrap` change is needed — the workflow uploads correctly
already. Either re-run the `publish-master` workflow, or push any commit; then:

```
curl -s https://coverage.mintplayer.com/api/browse/repos/MintPlayer/mintplayer-ng-bootstrap/commits/<sha>
```

`sessions[].parseStatus` should read `Parsed` with a non-zero `filesCount`, and
`https://coverage.mintplayer.com/badge/MintPlayer/mintplayer-ng-bootstrap.svg`
should render a percentage instead of `unknown`.
