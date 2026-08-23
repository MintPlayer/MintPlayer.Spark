# Plan — test-suite performance and reliability

**PRD:** `docs/test-suite-performance-PRD.md`
**Shipped through:** `fa2897d` on `feat/security-json-in-core`
**Not shipped:** stash entry *"test-suite: 15 shared-driver migrations, Dispose override, RavenDB
cleanup target (unverified — suite could not be run green)"*

---

## Before anything: restore a working environment

The suite could not be run green at the end of the last session (PRD §6). Do this first, in order,
and do not start migrating until a full run is green:

1. **Reboot.** The embedded RavenDB server hangs after printing its banner, and the most likely
   cause is wedged OS state from killing every `dotnet`/`testhost` process mid-run. Everything
   cheaper than this was tried.
2. `dotnet test tests/MintPlayer.Spark.Tests` — expect **1704 passed**. If not, do not proceed;
   diagnose with a bisect (`git stash`, re-run) before forming any hypothesis.
3. If the output directory is unusable: two `rmdir /s /q` passes, then `Rename-Item bin bin.stale`
   if a delete-pending file blocks the rebuild, then delete `obj/` too so
   `CopyRavenDBServer` re-runs. Verify with PowerShell `Test-Path`, **not** `ls` — they disagree.

## Milestones

| M | Title | State |
|---|---|---|
| M1 | Measure, and fix the pathological tests | **done**, shipped |
| M2 | Fix the flaky tests properly | **done**, shipped |
| M3 | `RqlRecorder` + handler leaks | **done**, shipped |
| M4 | The shared driver | **done**, shipped |
| M5 | `AGENTS.md` + MSBuild sync | **done**, shipped |
| M6 | Wave 1 — the no-write classes | **stashed**, unverified |
| M7 | RavenDB artifact cleanup target | **stashed**, needs the fix below |
| M8 | Wave 2 — the NEAR classes | not started |
| M9 | Wave 3 — per-class sharing where intra-class ids allow | not started |
| M10 | Wave 4 — the OIDC family | not started; largest prize |

## M6 — Wave 1 (stashed)

15 classes that write no documents and do nothing database-wide, migrated to
`SparkSharedDatabase` + `SparkSharedTestDriver`. 14 are a pure base-class swap:

```csharp
public class XTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
```

`GetQueryEndpointTests` needed its own fixture, because its factory is class-scoped.
`DenyAllEndpointMirrorTests` (already shipped) is the worked example: **26 cases, 13s → 647ms.**

⚠️ **Do not trust a grep for "writes documents".** An early filter marked `UserStoreTests`,
`RoleStoreTests`, `MessageBusTests`, `SyncActionInterceptorTests` and `CreateEndpointTests` as
write-free. They all write — through the *code under test* (`UserManager.CreateAsync`,
`bus.PublishAsync`), not through a seeding helper. Classify by reading, not by pattern.

## M7 — Cleanup target (stashed, and the draft is WRONG as written)

The stashed version deletes `$(OutDir)RavenDBServer`. **It must not.** The `RavenDB.Embedded`
package copies that ~600MB server in via `CopyRavenDBServer` on `PrepareForRunDependsOn`, which runs
during **build**; a target on `BeforeTargets="VSTest"` therefore deletes it between the build that
provided it and the run that needs it. Every test then fails with `FileNotFoundException: Server
file was not found`, and a plain rebuild does not restore it (the copy is `FileWrites`-tracked —
`obj/` must go too).

Clean **only**:
- `$(OutDir)RavenDB` — the data directory
- `$(OutDir)*.raven-cluster-topology` — one per database ever created

`ContinueOnError="WarnAndContinue"`, because a file the OS has not released yet must not fail the
build; the next run clears it.

## M8 — Wave 2, the NEAR classes

One targeted change each, then they qualify:

| Class | Change |
|---|---|
| `SparkTestDriverSmokeTests` | `"widgets/1"` → a GUID |
| `ContextPropertyRerootTests` | GUID the `OwnerId` |
| `NaturalIdConventionTests` | class-unique plate prefix (`IHasNaturalId` ignores `Id(...)`) |
| `ReferenceIncludeTests` | confirm/GUID the author id |
| `SyncActionHandlerIntegrationTests` | move off the shared `GuardedDoc` type |
| `EtlScriptDeploymentRecipientTests` | hoist side-database creation from per-case to per-class |

## M9 — Wave 3

Classes with point-load-only assertions and generated ids, *after* checking each class's own tests
for **intra-class** id reuse — per-class sharing still requires tests within the class not to
collide. Expect roughly half to fail that check.

## M10 — Wave 4, the OIDC family (largest prize, highest risk)

**165 cases, 42% of suite CPU, 11 classes**, all inheriting `OidcTestHost`. Payoff: 11 host boots
instead of 165, each of which currently includes ASP.NET Identity setup and an RSA key generation.

Blocked on seeding: `"webapp"` and `alice@test.local` are seeded literally across tests *and*
classes. Lookup goes through the `OidcApplications_ByClientId` index, so a second document with the
same `ClientId` silently changes which application a flow resolves; `SeedUserAsync` throws outright
on a duplicate email.

Needs `SeedApplicationAsync`/`SeedUserAsync` to take per-test unique values. That is a large
mechanical diff across 11 files **with real risk of changing what a security test asserts** —
several deliberately seed two applications and check cross-client isolation. Do it as its own
reviewed change, or not at all.

## Never move to a shared database

Not defects — this is what the per-case driver exists for:

`IndexWaitSemanticsTests`, `RavenIndexHelperSmokeTests`, `ComplexFieldIndexingTests` (empty-database
and index-state subjects) · `SubscriptionQueryCapabilityTests`, `MessageSubscriptionWorkerE2ETests`,
`MessageSubscriptionManagerLifecycleTests` (subscription enumeration) · `SyncActionRetrySweeperTests`,
`SyncActionSubscriptionWorkerE2ETests`, `ModuleRegistrationServiceTests` (collection-wide sweeps,
server-level database creation) · `SparkCronSchedulerTests`, `SparkMigrationRunnerTests`
(compare-exchange on fixed keys) · `SparkTestDriverLicenseTests` (its subject is the driver's own
lifecycle) · `RateLimiterPlacementTests` (process-global limiter + a mutable static counter) ·
`MessageBusTests`, `SyncActionInterceptorTests` (`SingleAsync()` over framework-written collections
— no id scheme can scope those) · `UserStoreTests`, `RoleStoreTests` (Identity's own uniqueness
constraints plus collection queries) · `LookupReferenceServiceTests` (one document per lookup name,
mutated in place) · `SearchPushdownTests`, `SortCompanionRedirectTests`,
`BreadcrumbCompanionRuntimeTests`, `TranslatedStringPersistedShapeTests` (complete index result
sets).

## Verification

- Full suite green **twice** before committing any migration wave — once proves nothing about
  order-dependence.
- Re-measure with TRX and compare against the 429s CPU / 200s wall baseline, remembering that TRX
  hides `InitializeAsync`.
- For each migrated class, read its tests for intra-class id collisions. A wrong SHAREABLE verdict
  creates exactly the intermittent failure this work exists to remove.
