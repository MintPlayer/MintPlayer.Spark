# Plan — Issue #258: replication retry redelivery + package updates

**PRD:** [issue_258_PRD.md](issue_258_PRD.md) ·
**PR:** [#259](https://github.com/MintPlayer/MintPlayer.Spark/pull/259) ·
**Branch:** `chore/update-package-references`

**All milestones complete.** M0 was the whole intended scope; M1–M4 emerged from it. Written after the
fact — the work was not planned up front, which is itself the reason M2 and M3 were found late rather
than designed for.

| | Milestone | Commit |
|---|---|---|
| M0 | Every package to latest stable + lockstep `preview.49` | `0cf47c9` |
| M1 | Replication retry redelivery: `WakeUp` gate + sweeper | `e7b5eb1` |
| M2 | Resolve the Tools DLL copy path from the package reference | `9bb04c8` |
| M3 | Rescue actions parked before `WakeUp` existed | `254ab16` |
| M4 | Drop the obsolete `now()`-rejection test | `7b42787` |
| M5 | Docs + PRD/plan | this commit |

---

## M0 — Packages to latest stable ✅ `0cf47c9`

Every `PackageReference` verified against nuget.org rather than assumed. 21 libs bumped to
`10.0.0-preview.49` in lockstep. Highlights and the two deliberate exceptions are in the PRD (R7, D4)
and the PR body.

Notable side effect: **WireMock.Net 1.8.0 → 2.15.0 cleared all 14 NU19xx advisories** (one critical),
which arrived transitively via `WireMock.Net.OpenApiParser` → `RamlToOpenApiConverter` →
`Scriban.Signed 5.5.0` plus `Microsoft.OpenApi 2.0.0-preview.16`.

## M1 — Retry redelivery ✅ `e7b5eb1`

**Files:** `Models/SparkSyncAction.cs`, `Services/SyncActionRetrySweeper.cs` (new),
`Workers/SyncActionSubscriptionWorker.cs`, `Indexes/SparkSyncActions_ByStatus.cs`,
`Extensions/SparkReplicationExtensions.cs`,
`Replication.Abstractions/Configuration/SparkReplicationOptions.cs`

- `SparkSyncAction.WakeUp` / `LastWakeUpUtc` — the subscription-visible gate (PRD R1/R2).
- `SyncActionRetrySweeper` patches `WakeUp` on Pending actions whose `NextAttemptAtUtc` has passed. The
  patch is field-level (never load-modify-save: last-write-wins would resurrect a completed action) and
  is simultaneously the write that triggers re-evaluation — one operation doing both jobs.
- Query → `Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true)`; the worker clears
  `WakeUp` on pickup, covering success, rejection and re-park alike.
- `FallbackPollInterval` (default 30s) paces retries only (R5).

**Tests:** `SyncActionRetrySweeperTests` (selection rules + idempotency, R4);
`SubscriptionQueryCapabilityTests` characterizing the RavenDB semantics;
`SyncActionSubscriptionWorkerE2ETests.A_parked_retry_is_actually_redelivered_once_the_sweeper_declares_it_due`
— the assertion the suite never made (PRD F3).

## M2 — Copy path follows the package reference ✅ `9bb04c8`

CI-only `MSB3030` (PRD F5). Replaced the hardcoded cache path with
`$(PkgMintPlayer_SourceGenerators_Tools)` via `GeneratePathProperty`, so it cannot drift on the next
bump.

`ExcludeAssets="all"`, **not** `PrivateAssets="all"` — we want the path, not the assembly. Compile
assets put a second `ModuleInitializerAttribute` in scope and every use becomes ambiguous with
`System.Runtime`'s (CS0433). Verified by hiding 10.19.0 from the local cache to reproduce CI conditions.

Audited: this was the only hardcoded package cache path in source.

## M3 — Rescue pre-fix parked actions ✅ `254ab16`

PRD F4. `a.WakeUp == false` → `a.WakeUp != true`.

The test reproduces a pre-fix document by **deleting the property** via a patch script rather than
trusting that a default-valued field is equivalent to an absent one — that equivalence was the actual
question, and assuming it is what nearly shipped the bug.

## M4 — Delete the obsolete test ✅ `7b42787`

PRD D5.

## M5 — Documentation ✅ this commit

- `docs/issue_258_PRD.md` + `docs/issue_258_plan.md` (were missing entirely).
- `docs/guide-cross-module-sync.md` — the retry section described backoff-and-retry that **did not
  actually happen**. Now documents the real mechanism and that retry granularity is
  `FallbackPollInterval`.
- `docs/prd/PRD-SubscriptionWorker.md` — §"one subscription per queue" still showed the `now()` query
  that #233 replaced with `WakeUp`. Corrected in place, because a stale query in a "how it works"
  section is a recipe for rewriting the bug.

## Risks

- **The index change forces a side-by-side reindex** on deploy (PRD D3). Expected, not silent.
- **Retry granularity is now bounded below by `FallbackPollInterval`.** A retry whose backoff expires
  just after a sweep waits up to one more interval. Acceptable: previously it waited forever.
- **`RetryNumerator`'s `@refresh` is now redundant** rather than load-bearing (D2). Left in place; if it
  is ever removed, this is the note explaining why it looked unused.
