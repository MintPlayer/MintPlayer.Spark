# PRD: Code Coverage Improvement

## 1. Overview

The Spark repo already has a real testing foundation: 5 .NET test projects (~137 test files), Vitest specs in both Angular libraries, coverlet + codecov wiring with carryforward flags, and a custom `SparkTestDriver` for embedded RavenDB. `MintPlayer.Spark.Tests` is a deliberately broad mega-project that `ProjectReference`s most of the production libraries and tests them through `InternalsVisibleTo` — so coverage is wider than a csproj-only count suggests.

What is actually missing is narrower: a handful of large untested classes (notably `UserStore` ~614 LOC and `RoleStore` ~176 LOC), one production project that no test project references at all (`MintPlayer.Spark.Client.Authorization`), one source-generator project without any test coverage (`MintPlayer.Spark.AllFeatures.SourceGenerators`), and an absence of `Verify`-based snapshot tests for emitted source.

This PRD proposes a phased push to close those gaps and raise enforced coverage thresholds, landing the planning artifact on `feat/inject-document-session` and the implementation across follow-up PRs.

## 2. Goals

1. Cover the largest untested classes in already-referenced production projects: `UserStore` and `RoleStore` in `MintPlayer.Spark.Authorization`.
2. Bring `MintPlayer.Spark.Client.Authorization` and `MintPlayer.Spark.AllFeatures.SourceGenerators` under the test umbrella (extend an existing test project or stand up a new one — whichever fits cleanest).
3. Add snapshot-based tests for all source generators (Spark + AllFeatures) so emit changes are visible in PR diffs.
4. Deepen coverage on the highest-risk classes that already have *some* tests but not enough: `EntityMapper` Color/enum paths, `ValidationService` rule evaluation, `QueryExecutor` reflection cache.
5. Lift Vitest spec coverage in `ng-spark` and `ng-spark-auth` for auth state, HTTP interceptor, route guard, and the core `SparkService`.
6. Tighten codecov thresholds in step with each phase so regressions become PR-blocking, not advisory.

## 3. Non-Goals

- Demo apps (`Demo/{DemoApp, Fleet, HR, WebhooksDemo}`) remain excluded from coverage. They exist to exercise the framework end-to-end, not as production code.
- No change to the e2e Playwright suite scope; it already covers cross-cutting flows and is intentionally slow.
- No introduction of mutation testing (Stryker) in this round — revisit once line coverage stabilises.
- No migration off Vitest, off Verify, or off `RavenDB.TestDriver`; the toolchain is fine.
- Not adopting `CronosCore.RavenDB.UnitTests` here. The repo's own `MintPlayer.Spark.Testing` + `SparkTestDriver` already fills the same role for in-tree code; CronosCore is for downstream apps.

## 4. Current State

Inventory taken on `feat/inject-document-session` against `master`.

### 4.1 Test projects that exist
| Project | Framework | Files | Flag |
|---|---|---|---|
| `MintPlayer.Spark.Tests` | xUnit + Verify | ~71 | `unit` |
| `MintPlayer.Spark.SourceGenerators.Tests` | xUnit + Verify | ~19 | `sourcegen` |
| `MintPlayer.Spark.Client.Tests` | xUnit | ~17 | `client` |
| `MintPlayer.Spark.E2E.Tests` | xUnit + Playwright | ~30 | `e2e` |
| `MintPlayer.Spark.Testing` | helper library | n/a | n/a |

### 4.2 Coverage routing — what is tested by which project
`MintPlayer.Spark.Tests` is the de facto unit-test home and `ProjectReference`s `Authorization`, `Webhooks.GitHub` (+ DevTunnel), `SocketExtensions`, `Messaging`, `Replication`, `SubscriptionWorker.Abstractions`, `AllFeatures`, `Client`, plus the `MintPlayer.Spark` core. Authorization adds `InternalsVisibleTo("MintPlayer.Spark.Tests")` so internal classes (`AccessControlService`, `SecurityConfigurationLoader`, `ClaimsGroupMembershipProvider`) are reachable. Existing test files in `MintPlayer.Spark.Tests` cover all those projects today — the absence of a dedicated `<Project>.Tests.csproj` per library does **not** mean zero coverage.

### 4.3 Real gaps — production code with no tests
- **`MintPlayer.Spark.Authorization/Identity/UserStore.cs`** (~614 LOC) — RavenDB-backed ASP.NET Identity adapter implementing 14 store interfaces; zero tests.
- **`MintPlayer.Spark.Authorization/Identity/RoleStore.cs`** (~176 LOC) — RavenDB-backed role store; zero tests.
- **`MintPlayer.Spark.Client.Authorization`** (2 source files: `SparkClientAuthExtensions.cs`, `SparkUserInfo.cs`) — no test project references this assembly at all.
- **`MintPlayer.Spark.AllFeatures.SourceGenerators`** (4 generator/model files) — no test project; emit unverified.
- **`MintPlayer.Spark/Streaming/`** (~169 uncovered lines, 28.7% line / 25.0% branch) — `StreamingDiffEngine` and `StreamingQueryExecutor` have no direct unit tests. Stateful per-connection diff logic, `ValuesEqual` equality semantics, and the streaming dispatch loop are unverified.
- **`MintPlayer.Spark.Authorization/Extensions/`** (150 uncovered lines, 0%) — DI-registration extensions: `SparkAuthenticationExtensions`, `GitHubAuthenticationExtensions`, OpenID/Identity setup. Configuration-shape regressions ship silently today.
- **`MintPlayer.Spark/Endpoints/ProgramUnits/`** (84 uncovered lines, 1.2% line / 0% branch) — endpoint surface is effectively un-exercised by unit tests.
- **`MintPlayer.Spark/Endpoints/LookupReferences/`** (49 uncovered lines, 22.2% line / 0% branch) — `LookupReferenceServiceTests` covers service-level behavior but the endpoint handlers themselves are thin on coverage.

### 4.4 Tested projects with thin coverage on critical paths
- `MintPlayer.Spark/Services/EntityMapper.cs` (~935 LOC) — has tests for breadcrumb / inverse / asDetail / factory; enum and Color conversion paths are thinner.
- `MintPlayer.Spark/Services/ValidationService.cs` (~287 LOC) — has a `ValidationServiceTests` file; rule-evaluation depth worth auditing once UserStore lands.
- `MintPlayer.Spark/Services/QueryExecutor.cs` — has unit + integration tests; reflection-based custom-query dispatch cache eviction worth widening.

### 4.4 Angular libraries
~23 `.spec.ts` files spread across ~186 source files (~12% spec ratio). Both libraries run under Vitest with `--coverage`, but several auth-critical surfaces are untested: `SparkAuthService`, `spark-auth.interceptor`, `spark-auth.guard`, `SparkService` HTTP wrapper, `SparkStreamingService`, dispatcher.

### 4.5 Source generator emit verification
`MintPlayer.Spark.SourceGenerators.Tests` exists but uses `CSharpGeneratorDriver` with hand-written assertions. There is no `Verify`-based snapshot of the emitted source, so accidental output drift (renamed helpers, dropped translation keys, attribute name mismatches) only surfaces when a downstream test happens to break.

**Coverlet instrumentation gap.** The test csproj references the generator with `ReferenceOutputAssembly="false"` and copies the netstandard2.0 DLL into `bin/Generators/`, then `Assembly.LoadFrom`s it at test time. Coverlet only instruments project references that flow into the test bin normally, so generator code is invisible to the coverage collector — `MintPlayer.Spark.SourceGenerators` reports **0% line coverage** despite 34 passing tests. Adding `Verify` snapshot tests (§5.2) is necessary but **not sufficient**; a separate instrumentation strategy is also required (sidecar test project that references the generator output assembly directly with a non-`Analyzer` reference, or `coverlet.msbuild` with explicit module include patterns).

### 4.7 Measured baseline (April 2026)

Merged Cobertura reports across `MintPlayer.Spark.Tests`, `MintPlayer.Spark.SourceGenerators.Tests`, and `MintPlayer.Spark.Client.Tests` runs from 2026-04-27. E2E flag not included.

| Assembly | Line | Branch | Notes |
|---|---|---|---|
| `MintPlayer.Spark.Abstractions` | **86.5%** | 97.1% | Healthy; continue maintenance only. |
| `MintPlayer.Spark` | **65.7%** | 66.8% | 1664 uncovered lines — the dominant target. |
| `MintPlayer.Spark.Authorization` | **74.7%** | 89.1% | Branch coverage is good; line gaps are concentrated in `Extensions/` and `Identity/` (UserStore/RoleStore). |
| `MintPlayer.Spark.SourceGenerators` | **0%** | 0% | Instrumentation gap (see §4.5), not a real zero. |
| `MintPlayer.Spark.IdentityProvider` | n/a | n/a | Project does not exist yet (PRD only). |

Top-10 namespaces by uncovered-line count (highest leverage first):

| Namespace | Line % | Uncovered |
|---|---|---|
| `MintPlayer.Spark.Services` | 70.2% | **908** |
| `MintPlayer.Spark.Streaming` | 28.7% | 169 |
| `MintPlayer.Spark.Authorization.Extensions` | 0.0% | 150 |
| `MintPlayer.Spark` (root) | 63.6% | 118 |
| `MintPlayer.Spark.Endpoints.ProgramUnits` | 1.2% | 84 |
| `MintPlayer.Spark.Actions` | 74.3% | 75 |
| `MintPlayer.Spark.Endpoints.Queries` | 67.4% | 63 |
| `MintPlayer.Spark.Endpoints.PersistentObject` | 64.2% | 59 |
| `MintPlayer.Spark.Authorization` (endpoints) | 0.0% | 51 |
| `MintPlayer.Spark.Endpoints.LookupReferences` | 22.2% | 49 |

### 4.6 Codecov configuration
- `coverage.project.default.target = auto`, `threshold = 2%`.
- `coverage.patch.default.target = 40%`, `threshold = 0%`.
- All four flags carryforward across `nx affected` partial runs.
- Demo apps, generated code, source-generator emit, and test projects are ignored.

## 5. Approach

### 5.1 Where new tests live
Default is to extend `MintPlayer.Spark.Tests` rather than spawn parallel `<Project>.Tests.csproj` projects — that mega-test pattern already works, the codecov `unit` flag already covers it, and splitting would only fragment shared infrastructure (`SparkTestDriver`, `SparkEndpointFactory`, `JsonFixtureImporter`).

Exceptions where a new test project **is** justified:
- `MintPlayer.Spark.Client.Authorization` — currently un-referenced. Add a `ProjectReference` to `MintPlayer.Spark.Client.Tests` and put tests there.
- `MintPlayer.Spark.AllFeatures.SourceGenerators` — separate target framework (`netstandard2.0`) and isolated emit testing; either fold into `MintPlayer.Spark.SourceGenerators.Tests` or stand up `MintPlayer.Spark.AllFeatures.SourceGenerators.Tests` (decision deferred to Phase 2).

### 5.2 Snapshot tests for source generators
Add `Verify.Xunit` to both source-generator test projects (Spark already uses Verify; AllFeatures.SourceGenerators needs a fresh test project). Per generator: run `CSharpGeneratorDriver` against a representative input compilation, then `Verifier.Verify(driver)` to snapshot the emitted syntax trees. Diffs become reviewable in PRs.

Generators in scope: `ActionsRegistrationGenerator`, `CustomActionsRegistrationGenerator`, `PersistentObjectNamesGenerator` (Names + Ids + AttributeNames), `HostTranslationsAggregatorGenerator`, `RecipientsRegistrationGenerator`, `SubscriptionWorkerRegistrationGenerator`, plus the AllFeatures equivalents.

### 5.3 Identity store tests (the biggest single gap)
`UserStore` and `RoleStore` are RavenDB-backed and need integration-style tests. Pattern: subclass `SparkTestDriver`, instantiate the store against `Store`, and exercise each `IUserStore`/`IRoleStore` interface method.

Coverage targets:
- `RoleStore` — Create/Update/Delete, deterministic ID generation from name, Find by id/name, claim add/remove, `Roles` queryable, `Dispose` idempotency.
- `UserStore` — Create with email reservation (compare-exchange path), update with email change, delete with reservation cleanup, password/security-stamp set/get, role membership, claims, logins, lockout, two-factor + recovery codes, tokens, phone, `Users` queryable.

### 5.4 Critical-path depth in already-tested code
Targeted unit tests, not new infrastructure:
- `EntityMapper` — enum-to-string, Color round-trip, edge cases not covered by existing breadcrumb/inverse/asDetail/factory tests.
- `ValidationService` — gaps after auditing the existing `ValidationServiceTests`.
- `QueryExecutor` — custom-query method resolution cache eviction and concurrency.
- `Client.Authorization` — `SparkClientAuthExtensions` request shaping and `SparkUserInfo` deserialization.

### 5.5 Angular library coverage
- `SparkAuthService` — token storage, refresh-on-401, login/logout state transitions, signal updates.
- `spark-auth.interceptor` — Bearer injection, retry-after-refresh, error propagation.
- `spark-auth.guard` — redirect-to-login on unauthenticated, return-url preservation.
- `SparkService` — HTTP request shape, error handling, parent/child query params.
- `SparkStreamingService` — subscription lifecycle, reconnect.
- `dispatcher.service` — operation queue ordering and execution.

Use `HttpTestingController` for HTTP surfaces and Angular's `provideRouter` test harness for guard/redirect tests.

### 5.6 Threshold ratchet
Codecov thresholds tighten as each phase lands:

| Phase | `patch` target | `project` threshold |
|---|---|---|
| Today | 40% | auto, 2% tolerance |
| End of Phase 1 | 55% | auto, 2% tolerance |
| End of Phase 2 | 70% | auto, 1% tolerance |
| End of Phase 3 | 80% | auto, 1% tolerance |

Threshold bumps are their own one-line PRs, gated on the prior phase's coverage uploads having stabilised.

## 6. Phasing

### Phase 1 — Identity stores + Client.Authorization (the un-tested code)
1. **Fix the source-generator instrumentation gap first** (§4.5). Without this, the `sourcegen` flag stays at 0% no matter how many `Verify` snapshots get added — measured progress is impossible. Pick one of: (a) sidecar test project that `ProjectReference`s the generator output assembly with `OutputItemType="Compile"` instead of `Analyzer`, or (b) `coverlet.msbuild` with `Include="[MintPlayer.Spark.SourceGenerators]*"` against the `Assembly.LoadFrom`'d module.
2. `RoleStoreTests` in `MintPlayer.Spark.Tests/Authorization/Identity/` — full `IRoleStore` + `IRoleClaimStore` + `IQueryableRoleStore` coverage against `SparkTestDriver`.
3. `UserStoreTests` in the same folder — full coverage of all 14 store interfaces, with explicit cases for the email compare-exchange uniqueness path and email-change reconciliation in `UpdateAsync`.
4. Add `MintPlayer.Spark.Client.Authorization` as a `ProjectReference` from `MintPlayer.Spark.Client.Tests` and write tests for `SparkClientAuthExtensions` and `SparkUserInfo`.
5. Add `Verify`-based snapshot tests to `MintPlayer.Spark.SourceGenerators.Tests` for the four highest-traffic generators (`ActionsRegistration`, `CustomActionsRegistration`, `PersistentObjectNames`, `HostTranslationsAggregator`).
6. Bump `patch` threshold to 55%.

### Phase 2 — AllFeatures.SourceGenerators + critical-path depth + Streaming
1. Decide between folding `AllFeatures.SourceGenerators` tests into `MintPlayer.Spark.SourceGenerators.Tests` vs. a dedicated test project; implement either way.
2. `EntityMapper` enum/Color depth and `ValidationService` audit pass.
3. `QueryExecutor` reflection cache concurrency tests.
4. **`Streaming/` namespace** (§4.3) — `StreamingDiffEngine` unit tests covering null handling, `ValuesEqual` semantics for Reference/AsDetail/TranslatedString attributes, ordering invariants, and new-item detection. `StreamingQueryExecutor` integration tests for the per-connection state machine and reconnect path.
5. **`Authorization/Extensions/` namespace** (§4.3) — DI-shape tests for `SparkAuthenticationExtensions`, `GitHubAuthenticationExtensions`, OpenID/Identity setup. Pattern: `ServiceCollection` → `BuildServiceProvider()` → assert expected services + `AuthenticationOptions` schemes are wired. Avoids needing a full host.
6. Vitest specs for the six Angular surfaces in §5.5.
7. Bump `patch` threshold to 70%, `project` tolerance to 1%.

### Phase 3 — Infrastructure-heavy (higher effort, ongoing)
1. `WebApplicationFactory`-based endpoint tests covering the thin-coverage namespaces from §4.7: `Endpoints.ProgramUnits` (1.2%), `Endpoints.LookupReferences` (22.2%), `Endpoints.PersistentObject` (64.2% → 80%), `Endpoints.Queries` (67.4% → 80%). Uses `SparkTestDriver` + `SparkEndpointFactory`; reuses the `SeedDynamicDocAsync` pattern from `LookupReferenceServiceTests` for endpoint-level fixtures.
2. CSRF, session, auth interaction tests at the endpoint layer (extends but does not replace Playwright e2e).
3. Cross-module Replication/Messaging integration scenarios with two `SparkTestDriver` instances.
4. `SubscriptionWorker` lifecycle tests (start/stop, batch error recovery) beyond the existing `RetryNumeratorTests`.
5. Bump `patch` threshold to 80%.

## 7. Success Criteria

- Every production .NET project has a corresponding `*.Tests.csproj` and a codecov flag.
- Every source generator has at least one `Verify` snapshot test exercising a representative input.
- The four critical service classes in §5.3 each carry ≥80% line coverage.
- Auth-critical Angular surfaces in §5.4 each carry ≥75% line coverage.
- `patch` threshold sits at 80% with no informational override; PRs that drop project coverage by >1% block.

## 8. Risks & Mitigations

- **Test flakiness from `RavenDB.TestDriver`.** Embedded Raven occasionally hangs on Windows. Mitigation: scope it to integration-tagged tests only; keep Phase 1/2 unit tests pure in-memory.
- **Source generator snapshot churn.** Every emit-format change becomes a PR diff. That's the point, but it means generator refactors carry test-update overhead. Mitigation: keep snapshots small and per-feature; don't snapshot whole compilation outputs.
- **Coverage carryforward masking real regressions.** Today's `carryforward: true` design hides flag drops on partial runs. Mitigation: tighten `project.threshold` to 1% in Phase 2 so a real regression in any uploaded flag still trips the gate.
- **Codecov PR123-class incidents recurring.** `docs/codecov-pr123-diagnosis.md` already captured the carryforward semantics. Each new flag added in this PRD must be explicitly listed under `flag_management.individual_flags` to inherit the same behavior.

## 9. Out of Scope / Follow-ups

- Mutation testing (Stryker.NET, Stryker for JS) — evaluate after Phase 3 lands.
- Unifying `MintPlayer.Spark.Testing` with `CronosCore.RavenDB.UnitTests` into a shared NuGet — separate cross-repo discussion.
- Demo-app coverage — only worth doing if a demo starts shipping production logic.
- Coverage SLAs per package (e.g., per-module thresholds in codecov) — revisit once breadth is solved.

## 10. File Pointers

- `codecov.yml` — flag list, ignore patterns, thresholds.
- `.github/workflows/pull-request.yml` — per-flag coverage upload steps.
- `nx.json` — `targetDefaults.test` defines `coverage` outputs path.
- `MintPlayer.Spark.Testing/` — `SparkTestDriver` and shared fixtures.
- `MintPlayer.Spark.Tests/` — house style for unit + Verify snapshot tests.
- `node_packages/ng-spark*/project.json` — Vitest test targets and coverage config.
- `docs/codecov-pr123-diagnosis.md`, `docs/codecov-pr123-remediation.md` — prior context on carryforward semantics.
