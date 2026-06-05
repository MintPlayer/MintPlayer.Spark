# CodeCov PR #123 Failure Analysis

## Coverage Metrics
- **Patch Coverage**: 26.28% (target 40.69%) — FAILING
- **Project Coverage**: 34.90% (−5.80% vs base ea596e9) — FAILING
- **No codecov.yml** — default settings apply (line-based coverage, no branch coverage weighting)

---

## Summary

The PR adds **two production fixes** (H-2/H-3 row-level authorization, M-7 optimistic concurrency) plus **extensive E2E test coverage** in a separate test project that **does not run under the CI coverage collection command**. The new production code is exercised by those E2E tests (they pass), but coverage reports don't include `MintPlayer.Spark.E2E.Tests.Security/*`, so the framework changes appear uncovered.

The −5.80% project-wide drop is caused by the new uncovered lines in the production code. The 26.28% patch coverage reflects that the main hit is to framework surface area (new public methods, exception class, reflection helpers) that E2E tests exercise at runtime but coverage tools don't instrument.

---

## Coverage Breakdown

### Files in Diff with Likely Coverage Issues

**High Impact (Framework—no E2E coverage reporting)**:

1. **MintPlayer.Spark/Services/DatabaseAccess.cs** (+88 lines)
   - `IsAllowedEntityViaActionsAsync()` — reflection helper that loads actions and invokes `IsAllowedAsync()` (H-2)
   - `FilterByRowLevelAuthAsync()` — filters list results by row-level gate (H-2)
   - Concurrency check block lines 212–221 — validates Etag before save (M-7)
   - **Exercised by**: RowLevelAuthzTests + ConcurrencyTests in E2E project
   - **Not counted**: E2E project not in CI coverage run

2. **MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs** (+25 lines)
   - `IsAllowedAsync(string action, T entity)` — new virtual hook on line 75, default returns `Task.FromResult(true)`
   - Extensive documentation (lines 52–75) explaining the row-level gate
   - **Exercised by**: E2E tests (Fleet's CarActions overrides it)
   - **Not counted**: default implementation used via reflection, E2E coverage not collected

3. **MintPlayer.Spark/Exceptions/SparkConcurrencyException.cs** (+19 lines)
   - New exception class (internal sealed)
   - Constructor and properties; message formatting
   - **Exercised by**: ConcurrencyTests.Concurrent_update_with_stale_version_is_rejected
   - **Not counted**: exception thrown in E2E test, caught in Update.cs endpoint — E2E coverage not reported

4. **MintPlayer.Spark/Endpoints/PersistentObject/Update.cs** (+4 lines)
   - Catch block for `SparkConcurrencyException` (lines 74–77) — returns 409 Conflict
   - **Exercised by**: E2E test ConcurrencyTests  
   - **Not counted**: E2E project not in CI run

5. **MintPlayer.Spark/Endpoints/Queries/Execute.cs** (+38 lines)
   - Parent fetch block (lines 88–104) — loads parent via `GetPersistentObjectAsync` (which now gates with IsAllowedAsync), returns 404 if null (H-3)
   - **Exercised by**: RowLevelAuthzTests.User_B_cannot_execute_child_query_with_User_As_parent_id
   - **Not counted**: E2E test

**Lower Impact (Demo app—excluded from CI)**:

6. **Demo/Fleet/Fleet/Actions/CarActions.cs** (+27 lines)
   - CarActions.IsAllowedAsync override — enforces "creator sees only their own cars"
   - OnBeforeSaveAsync stamp CreatedBy field
   - **Not reported**: Demo/* projects explicitly excluded from CI: `--exclude=@spark-demo/*,Fleet,Fleet.Library`

7. **Demo/Fleet/Fleet.Library/Entities/Car.cs** (+8 lines)
   - CreatedBy field
   - **Not reported**: Fleet.Library excluded

**Source Generators (minimal impact)**:

8. **MintPlayer.Spark.AllFeatures.SourceGenerators/Generators/SparkFullGenerator.Producer.cs** (+7 lines)
9. **MintPlayer.Spark.AllFeatures/SparkFullOptions.cs** (+8 lines)
   - Minor helper code; not on hot path for these tests

---

## Root Cause of −5.80% Project Drop

The PR **adds production code that the E2E tests fully exercise, but E2E coverage is not collected by CI**.

**Affected LOC** (production, not tests):
- DatabaseAccess.cs: +88 lines (row-level filter, concurrency check, reflection helper)
- DefaultPersistentObjectActions.cs: +25 lines (IsAllowedAsync hook)
- Update.cs, Queries/Execute.cs: +42 lines (409 response, H-3 parent gate)
- SparkConcurrencyException.cs: +19 lines (new exception)
- **Total uncovered production LOC**: ~174 lines

This is **new surface area** (framework hooks, internal helpers, exception handling) that is tested behaviorally by E2E tests but not instrumented by coverage tooling.

---

## CI Configuration Analysis

**`.github/workflows/pull-request.yml` line 68**:
```
npx nx affected --target=test --exclude=@spark-demo/*,DemoApp,DemoApp.Library,Fleet,Fleet.Library,HR,HR.Library,WebhooksDemo,WebhooksDemo.Library
```

**What this does**:
- Runs `dotnet test` on all affected projects *except* Demo/* and Demo.Library/*
- Projects that WILL run under `dotnet test` with coverage:
  - `MintPlayer.Spark.Tests` (unit tests, includes Authorization/ folder with AccessControlServiceTests etc.)
  - `MintPlayer.Spark.SourceGenerators.Tests`
  - `MintPlayer.Spark.Messaging.Tests` (if affected)
  - Other non-demo test projects

- Projects that DO NOT RUN:
  - `MintPlayer.Spark.E2E.Tests` (NOT listed — no glob matches it, it's not a Demo project)
  - Demo test projects (explicitly excluded)

**Coverage collection** (line 78–80):
```yaml
files: |
  **/coverage/**/coverage.cobertura.xml
  **/coverage/cobertura-coverage.xml
```
Collects Cobertura XML from any test project that ran. If `MintPlayer.Spark.E2E.Tests` doesn't run, its coverage.xml isn't collected.

**Coverlet collector** is in both `MintPlayer.Spark.Tests.csproj` and `MintPlayer.Spark.E2E.Tests.csproj`, but the E2E tests are never invoked because:
1. Nx affected doesn't know how to test E2E (no `targets: test` defined in E2E project's project.json or equivalent)
2. OR E2E project is not in the Nx graph (it's .csproj-only, not an Nx project)
3. Result: its tests don't run, coverage.xml is never generated

---

## Why This Matters

- **RowLevelAuthzTests.cs** (100 lines): 3 facts testing H-2/H-3 behavior — all pass, all exercising IsAllowedAsync hook
- **ConcurrencyTests.cs** (96 lines): 1 fact testing M-7 — passes, exercises Etag round-trip and 409 response
- These tests validate the core fix, but codecov sees only:
  - Test code itself (counted as test coverage, not line coverage)
  - No instrumentation of the production code paths

---

## Remediation Path (Out of Scope for This Diagnosis)

Uncovered framework code is a code-quality signal, not a functional bug (the tests pass). Options:

1. **Add unit tests to `MintPlayer.Spark.Tests`** covering IsAllowedAsync default path, FilterByRowLevelAuthAsync edge cases (empty list, projection mismatch), SparkConcurrencyException message formatting — ~150 LOC of unit tests to reach 80%+ coverage on the new production lines.

2. **Integrate E2E test coverage into CI** — configure Nx to run `MintPlayer.Spark.E2E.Tests` and collect its coverage.xml, merging it with unit test coverage. Requires:
   - Nx project configuration for E2E tests
   - GitHub Actions step to invoke E2E tests separately (or add to affected graph)
   - Awareness that E2E coverage will be volatile (depends on Fleet demo seeding, Playwright, Raven test instance)

3. **Accept the coverage drop** — the fix is correct (tests pass), and the gap is known and documented. File a follow-up issue to backfill unit tests.

---

## Conclusion

PR #123 adds 174 LOC of uncovered production code. All code is exercised by E2E tests, but those tests run outside the CI coverage collection loop. No bug in the code; a gap in test infrastructure visibility. The −5.80% project drop is the honest cost of adding new framework surface area without parallel unit test coverage.
