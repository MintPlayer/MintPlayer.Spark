# PRD — Issue #258: replication retries are never redelivered

**Issue:** [#258](https://github.com/MintPlayer/MintPlayer.Spark/issues/258) ·
**PR:** [#259](https://github.com/MintPlayer/MintPlayer.Spark/pull/259) ·
**Branch:** `chore/update-package-references` · **Ships in:** `10.0.0-preview.49`

## Origin

This started as a routine chore — move every `PackageReference` to its latest stable version. Bumping
`RavenDB.Client` 7.2.1 → 7.2.5 turned 5 replication E2E tests red, and investigating that turned up a
bug that had been live since the feature shipped.

Recorded here because the *chore* was trivial and the *finding* was not, and because the mistakes made
while diagnosing it are the reusable part.

## F1 — `now()` cannot gate a subscription query, and never could

`SyncActionSubscriptionWorker` gated redelivery on:

```csharp
Query = "from SparkSyncActions where Status = 'Pending' and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())"
```

RavenDB subscriptions are **change-vector-driven**: a document is tested against the query only when it
is *written*. A backoff elapsing writes nothing, so the predicate was evaluated exactly once — at
creation, when the retry time is still in the future — and never again.

**The backoff did not delay retries. It cancelled them.** Live since the feature shipped, on every
RavenDB version.

Same defect as #233, which fixed messaging and explicitly left this worker outstanding.

## F2 — the RavenDB version changes only the symptom

| RavenDB | Clause behaviour | Effect |
|---|---|---|
| ≤ 7.2.1 | parser accepts `now()`; comparison silently evaluates false | **partial** — the `NextAttemptAtUtc = null` branch still matches, so new actions flow; retries silently stranded |
| ≥ 7.2.2 | server **rejects the query**: `NotSupportedException: 'now()' function is not supported in filter or subscription expressions` | **total** — subscription never delivers, replication stops |

Bisected: 7.2.1 → 5/5 pass, 7.2.2/7.2.4/7.2.5 → 0/5. Confirmed causal by deleting only the `now()`
fragment on 7.2.5, which restores delivery.

**RavenDB's change is correct.** The question is unanswerable in that position, so refusing it beats
silently answering false. It exposed our bug rather than causing it.

## F3 — the test suite was uniformly green and proved nothing

`ServerError_500_..._gating_redelivery` asserts the parked action is **not** redelivered *early*. That
stayed true for the wrong reason: it was never redelivered at all. Every replication retry assertion
was about bookkeeping — `AttemptCount`, `NextAttemptAtUtc`, `@refresh` — and none about delivery.

**A negative assertion that also holds when the feature is entirely dead is not a test.** If a retry
path is under test, something must assert the retry *arrives*.

## F4 — a missing JSON field does not match `== false`

The first version of the fix filtered `a.WakeUp == false`. Actions parked by a pre-fix build have no
`WakeUp` property in their JSON at all, and a missing field does not match `== false` — measured, the
sweeper selected **0** of them.

This would have shipped a fix that worked for every future failure and left the entire existing parked
backlog stranded permanently — the exact population the fix exists for. Every test passed, because
tests build documents through the C# model, which always writes the property.

Fix: `a.WakeUp != true`, which matches an absent field and still excludes already-woken actions.

**Generalises:** add a field to an existing document type, query on its default value, and every
pre-existing document is silently excluded.

## F5 — stale local state masked two CI failures

Both passed locally and failed on CI, for the same underlying reason:

- **`MSB3030`** — the generator-asset copy target hardcoded
  `$(NuGetPackageRoot)mintplayer.sourcegenerators.tools\10.19.0\...` alongside the `PackageReference`.
  The bump updated the reference, not the string. It resolved locally only because 10.19.0 was still
  in the local NuGet cache.
- **RavenDB embedded server** — an incremental build does not refresh RavenDB.Embedded's
  `RavenDBServer/` content folder, so the 7.2.0 server layout survived (193 files vs 319) and failed to
  boot with missing `pgsqlparser` / `Azure.Messaging.ServiceBus`.

**A green local run on a machine with history is weak evidence.** Reproduce by removing the state, not
by reasoning about it.

## F6 — diagnosis errors worth not repeating

- I described 7.2.5 as *silently* matching nothing and filed #258 saying "no error". It raises an
  explicit server-side error. The word came from a memory note about 7.2.1, and once written it framed
  how I read every subsequent piece of evidence. #258 has been corrected.
- I initially proposed **pinning RavenDB to 7.2.1** and treating the rest as shippable. That preserves
  a broken retry path and hides it again. Rejected on review — correctly.

## Requirements

- **R1** Retry redelivery must work: a parked action becomes deliverable once its backoff elapses.
- **R2** No `now()` in any subscription query.
- **R3** Actions parked by a pre-fix build must be rescued (F4).
- **R4** Waking must be idempotent — re-patching each interval would bump the change vector every pass
  and turn one retry into a redelivery loop.
- **R5** First delivery stays immediate; only retries are sweep-paced.
- **R6** `Failed` stays terminal for replication. Reviving it would change the retry contract.
- **R7** Every package at latest stable, RavenDB included. No pinning around R1.

## Decisions

- **D1 — mirror messaging's sweeper, don't share it.** Duplicating `MessageRetrySweeper`'s shape is
  one copy more than ideal, but the two live in independent packages over different documents;
  sharing would couple Replication to Messaging for ~40 lines.
- **D2 — a boolean gate, not `@refresh` alone.** `RetryNumerator` already writes `@refresh`, and the
  server touch it causes would trigger re-evaluation — but `@refresh` is a per-database opt-in and the
  community licence floors its frequency, so it cannot be the primary mechanism. It stays as
  belt-and-braces.
- **D3 — index the sweeper's fields.** `SparkSyncActions_ByStatus` gains `NextAttemptAtUtc` and
  `WakeUp` rather than leaning on an auto-index; the sweeper runs on a timer against a collection that
  can be large. Costs a side-by-side reindex on deploy.
- **D4 — FluentAssertions stops at 7.2.2.** v8 drops Apache-2.0 for the Xceed Community License, which
  is not free for commercial use. Not a technical call; made by the user.
- **D5 — characterization over guard for third-party behaviour.** An early test asserted RavenDB
  rejects `now()`. It never touched our code, so it could not fail if `now()` were reintroduced — and
  could only fail if RavenDB reworded an error message we no longer depend on. Deleted. The guard is
  the E2E redelivery test.

## Out of scope

- **`MessageRetrySweeper` has no `WakeUp` predicate**, so it is immune to F4 — but it therefore
  re-patches every interval where replication does not. Possibly deliberate at-least-once behaviour.
  Not touched inside a dependency-update PR; worth a separate look.
- **`docs/prd/PRD-cross-module-sync.md:235`** describes a `MessageProcessor` filtering
  `NextAttemptAtUtc` — the pre-`SparkSyncAction` design. Left as the historical design record it is.
- **`IndexWaitSemanticsTests.cs:97` CS0184** — pre-existing dead `is` check from #257 (the `Where`
  clause is a tautology because `RavenIndexDeploymentException` does not derive from
  `TimeoutException`). Harmless, unrelated, not touched.

## Results

**1480 tests pass** — 1382 + 60 + 38, seven of them new. Clean restore, zero NU19xx advisories (down
from 14, one critical). CI green.
