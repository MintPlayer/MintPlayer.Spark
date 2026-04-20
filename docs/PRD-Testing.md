# PRD: Test Coverage — Unit, Integration & E2E

| | |
|---|---|
| **Version** | 1.3 |
| **Date** | 2026-04-20 |
| **Status** | In progress (481 tests landed, see §12) |
| **Owner** | MintPlayer |
| **Scope** | All non-Demo projects in `MintPlayer.Spark.sln` + Angular libraries + IDE extensions (when activated) |

> Establishes a test strategy covering unit, integration, and E2E tests across the framework. Standardizes infrastructure (xUnit + FluentAssertions + NSubstitute; RavenDB.TestDriver via `CronosCore.RavenDB.UnitTests`; **Vitest + happy-dom** for Angular unit tests; **Playwright Component Testing** for visually-sensitive components; Playwright for E2E against a dedicated test-host app).
>
> v1.1 swaps Karma → Vitest per current Angular guidance (Karma is deprecated as of Angular 20). Adds Playwright CT, affected-only testing via nx/turbo, and remote task caching to hit a <2-min PR feedback target.

---

## 1. Problem Statement

The Spark solution currently ships with **one** test project (`MintPlayer.Spark.Tests`, 8 files, plain xUnit). Coverage is heavily skewed toward pure-logic utilities:

- Core services (`QueryExecutor`, `LookupReferenceService`, `ModelLoader`, `ValidationService`, `ModelSynchronizer`, `Manager`) — **no tests**.
- All HTTP endpoints (PersistentObject CRUD, Queries, Custom Actions, Permissions, Culture, Aliases, Health) — **no tests**.
- Authorization (`AccessControlService`, `SecurityConfigurationLoader`, `ClaimsGroupMembershipProvider`) — **no tests**.
- Source generators (`ActionsRegistrationGenerator`, `CustomActionsRegistrationGenerator`, `SubscriptionWorkerRegistrationGenerator`, translations generators, `RecipientRegistrationGenerator`, `ProjectionPropertyAnalyzer`) — **no tests**.
- Messaging, Replication, SubscriptionWorker, Webhooks.GitHub, DevTunnel, SocketExtensions — **no tests**.
- Angular libraries `ng-spark` and `ng-spark-auth` — **no `*.spec.ts` files**; no Karma config exists.
- No E2E tests exist at any layer.

Risk areas that are actively shipping without test coverage:
- **Concurrency**: `GitHubInstallationService` token cache (semaphore gate, dynamic credential stores, 401-retry), subscription worker reconnect loop, messaging FIFO-within-queue.
- **Reflection-heavy code paths**: `EntityMapper`, `ReferenceResolver`, `EtlScriptCollector`, source-generator semantic analysis.
- **Security-critical**: `AccessControlService` permission evaluation (group resolution, combined actions, default deny), webhook HMAC signature verification, `sparkAuthGuard`, `sparkAuthInterceptor`, CSRF flow.
- **Cache/lifecycle**: `SecurityConfigurationLoader` hot-reload, `ETL task idempotency` on redeploy, subscription creation/update idempotency.

We need a consolidated plan before adding any tests, so the infrastructure is shared and coverage grows consistently.

---

## 2. Goals

1. **A single shared test infrastructure** for Raven-backed .NET tests, based on the reusable `CronosCore.RavenDB.UnitTests` helper — JSON-seeded in-memory RavenDB + Verify snapshots.
2. **Unit tests** for every pure-logic service, pipe, guard, and generator — mockable via NSubstitute.
3. **Integration tests** for every HTTP endpoint, source generator, RavenDB-dependent service, and external-dependency boundary (GitHub API, SMTP, WebSocket).
4. **Browser-level E2E** via Playwright against a new dedicated test host app (not the Demo apps — those stay free to evolve).
5. **Angular library tests** for `ng-spark` and `ng-spark-auth` with Karma + Jasmine (the ng-packagr default).
6. **CI integration** — all test suites run in GitHub Actions on every PR; RavenDB tests run in-memory (no Docker required).
7. **Baseline code-coverage target**: 70% line coverage on public API surface of shipped NuGet and npm packages within 2 milestones; 85% on security-critical code (Authorization, webhook signature, guards, interceptors).

### Non-goals

- Mutation testing, stress/load testing, or fuzzing (out of scope for v1).
- Testing the Demo apps themselves (`DemoApp`, `HR`, `Fleet`, `WebhooksDemo`).
- Testing the SparkEditor app or its IDE extensions until they have shippable code (currently skeleton shells).
- Cross-browser Playwright matrix — Chromium-only in v1.
- Testing against a real RavenDB cluster in CI (in-memory TestDriver only; local smoke tests against real Raven stay ad-hoc).

---

## 3. Test Infrastructure

### 3.1 .NET test stack

| Concern | Library | Version |
|---|---|---|
| Test runner | `xUnit` | 2.9.x |
| Assertions | `FluentAssertions` | 6.x |
| Mocking | `NSubstitute` | 5.x |
| RavenDB in-memory | `RavenDB.TestDriver` (via CronosCore wrapper) | 7.2.x |
| Snapshot testing | `Verify.Xunit` + `Verify.NewtonsoftJson` | latest |
| Source-generator testing | `Verify.SourceGenerators` + `Microsoft.CodeAnalysis.CSharp.Testing.XUnit` | latest |
| HTTP mocking | `WireMock.Net` | 1.6.x |
| ASP.NET Core integration | `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`) | 10.0.x |

### 3.2 CronosCore test helper — design reference

Location: `C:\Repos\CronosCore\CronosCore.RavenDB.UnitTests`. Published as NuGet (current `3.22.0`).

**Treated as a design reference, NOT a runtime dependency.** Implementation finding during milestone 1:

- `RavenDBTestDriver.cs` is NUnit-coupled (`[TestFixture]`, `[SetUp]`, `[TearDown]`) — direct `ProjectReference` from an xUnit project would pull a second test runner.
- Its `GetLicenseAsync()` fetches a private CronosCore Azure blob, not accessible from Spark's CI.

**Therefore**: `MintPlayer.Spark.Testing` depends directly on `RavenDB.TestDriver` (framework-neutral `RavenTestDriver` base class) and re-implements the small amount of value-added code following CronosCore's *patterns*:

- `SparkTestDriver : RavenTestDriver, IAsyncLifetime` — xUnit-native per-test lifecycle.
- `JsonFixtureImporter` — reads the same `{ "Results": [ { "@metadata": { "@id": "…", "@collection": "…" }, … } ] }` JSON format used by CronosCore fixtures.
- `VerifyDefaults.Initialize()` — sets `Verifier.DerivePathInfo(...)` → `VerifyResults/{ClassName}/{MethodName}.verified.*`, wired via `[ModuleInitializer]`.

Explicitly NOT ported (Vidyano-coupled):
- `VidyanoTestDriver` and its partials (`.Ex`, `.Exceptions`, `.Hooks`, `.ImportReader`, `.ImportScope`, `.Mockups`, `.Options`, `.VidyanoServer`).
- `PersistentObject`, `PersistentObjectAttribute`, `PersistentObject.ViewModelBase` — those are Vidyano view-model wrappers; Spark has its own types.

Consuming from `MintPlayer.Spark.Tests`:

```xml
<ProjectReference Include="..\MintPlayer.Spark.Testing\MintPlayer.Spark.Testing.csproj" />
```

Further `WebApplicationFactory`-based helpers (for HTTP endpoint tests) land on `SparkTestDriver` as they're needed — no premature abstraction.

Reference for CronosCore details: [reference_cronoscore_raven_tests.md](../../.claude/projects/C--Repos-MintPlayer-Spark/memory/reference_cronoscore_raven_tests.md).

### 3.3 Test project layout

Keep a **single** .NET test project (`MintPlayer.Spark.Tests`) but partition by folder — avoids the overhead of many small projects while still mapping 1:1 to source code.

```
MintPlayer.Spark.Tests/
├── Abstractions/
│   ├── Models/                         (SparkQuery, TranslatedString, ValidationRule, SortColumn)
│   ├── Attributes/                     (PersistentObjectAttribute — already exists)
│   └── Configuration/                  (CultureConfiguration, ProgramUnit)
├── Core/                               (empty — skip)
├── Spark/
│   ├── Services/
│   │   ├── EntityMapperTests.cs        (exists — expand)
│   │   ├── ReferenceResolverTests.cs   (exists — expand)
│   │   ├── IndexRegistryTests.cs       (exists — expand)
│   │   ├── QueryExecutorTests.cs       (NEW)
│   │   ├── ValidationServiceTests.cs   (NEW)
│   │   ├── LookupReferenceServiceTests.cs
│   │   ├── ModelLoaderTests.cs
│   │   ├── ModelSynchronizerTests.cs   (partial — exists)
│   │   ├── ActionsResolverTests.cs
│   │   ├── CustomActionResolverTests.cs
│   │   └── ManagerTests.cs
│   ├── Endpoints/
│   │   ├── PersistentObject/           (Create, Get, List, Update, Delete)
│   │   ├── Queries/                    (Execute, Get, List, StreamExecuteQuery)
│   │   ├── CustomActions/              (Execute, List)
│   │   └── Misc/                       (Permission, Culture, Aliases, HealthCheck)
│   └── Configuration/                  (SparkBuilder, SparkOptions — exists, expand)
├── Authorization/
│   ├── Services/
│   │   ├── AccessControlServiceTests.cs
│   │   ├── SecurityConfigurationLoaderTests.cs
│   │   └── ClaimsGroupMembershipProviderTests.cs
│   ├── Endpoints/
│   │   └── (GetCurrentUser, Logout, CsrfRefresh, Groups)
│   └── AuthorizationOptionsTests.cs    (exists)
├── SourceGenerators/
│   ├── ActionsRegistrationGeneratorTests.cs
│   ├── CustomActionsRegistrationGeneratorTests.cs
│   ├── SubscriptionWorkerRegistrationGeneratorTests.cs
│   ├── LibraryTranslationsGeneratorTests.cs
│   ├── HostTranslationsAggregatorGeneratorTests.cs
│   ├── RecipientRegistrationGeneratorTests.cs
│   ├── ProjectionPropertyAnalyzerTests.cs
│   └── Snapshots/                      (Verify.SourceGenerators .verified.cs files)
├── Messaging/
│   ├── MessageBusTests.cs
│   ├── MessageSubscriptionWorkerTests.cs
│   └── CheckpointRecipientTests.cs
├── Replication/
│   ├── ModuleRegistrationServiceTests.cs
│   ├── EtlScriptCollectorTests.cs
│   ├── EtlTaskManagerTests.cs
│   ├── SyncActionInterceptorTests.cs
│   └── SyncActionSubscriptionWorkerTests.cs
├── SubscriptionWorker/
│   ├── SparkSubscriptionWorkerTests.cs
│   └── RetryNumeratorTests.cs
├── Webhooks.GitHub/
│   ├── GitHubInstallationServiceTests.cs
│   ├── SignatureServiceTests.cs
│   └── SparkWebhookEventProcessorTests.cs
├── Webhooks.DevTunnel/
│   ├── SmeeBackgroundServiceTests.cs
│   └── WebSocketDevClientServiceTests.cs
├── AllFeatures/
│   └── SubscriptionWorkerRegistrationTests.cs
├── SocketExtensions/
│   └── WebSocketMessageTests.cs
└── Fixtures/                           (JSON seed data — RavenDB Smuggler format)
    ├── Entities/
    ├── Authorization/
    └── Messaging/
```

A new sibling project `MintPlayer.Spark.Testing` holds the reusable `SparkTestDriver`, `WebApplicationFactory` helpers, and WireMock fixtures.

### 3.4 Angular test stack

Scope: `node_packages/ng-spark` and `node_packages/ng-spark-auth`.

**Speed is a first-class requirement** — prior experience with Karma-based Angular suites hit 10+ min per PR. Target: full Angular suite under 60 s cold, under 5 s incremental (watch), under 2 min end-to-end in CI after cache.

| Concern | Library | Why |
|---|---|---|
| Unit runner | **Vitest** | ESM-native, `esbuild` + `swc` compilation (10–20× faster than `tsc`), near-instant watch reruns |
| Angular bridge | **`@analogjs/vitest-angular`** | Wires Angular's compiler into Vitest; TestBed, signals, standalone, zoneless all work |
| DOM | **`happy-dom`** | No real browser, no WebDriver; ~2× faster than jsdom for typical Angular workloads |
| Assertions | Vitest's built-in (Jest-compatible API) | `expect().toBe()` etc. |
| HTTP mocking | `HttpTestingController` (from `@angular/common/http/testing`) | Works unchanged under Vitest |
| Async/signals | `TestBed`, `fakeAsync` / `flush`, `TestBed.runInInjectionContext` | Unchanged |
| DOM queries | `@angular/cdk/testing` harnesses where available | Unchanged |
| Component tests (visual) | **Playwright Component Testing** | Real Chromium for components where CSS/layout/DOM fidelity matters |

Karma is explicitly rejected — it's deprecated as of Angular 20, boots a real browser per run, serves via webpack, and is the documented cause of the prior slow-feedback pain.

Per-library setup:
- `vitest.config.ts` next to each library (`node_packages/ng-spark/vitest.config.ts`, `node_packages/ng-spark-auth/vitest.config.ts`).
- `tsconfig.spec.json` extends the lib `tsconfig.lib.json`, includes `*.spec.ts`.
- Test files co-locate with source (`login.component.ts` + `login.component.spec.ts`).
- Root `package.json` gains `"test:ng-spark"`, `"test:ng-spark-auth"`, and `"test:ng"` (fan-out) scripts, all delegated via npm workspaces.

Example `vitest.config.ts`:

```ts
import angular from '@analogjs/vite-plugin-angular';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [angular()],
  test: {
    globals: true,
    environment: 'happy-dom',
    setupFiles: ['./src/test-setup.ts'],
    include: ['src/**/*.spec.ts'],
  },
});
```

`test-setup.ts` calls `TestBed.initTestEnvironment(...)` with the zoneless test provider (both libraries are zoneless already).

### 3.4.1 Playwright Component Testing

Used sparingly — only for components whose behavior depends on real CSS, layout, scroll, or browser-only APIs. Candidates:

- `SparkQueryListComponent` — table rendering, sticky headers, virtualization.
- `SparkRetryActionModalComponent` — overlay stacking, focus trap.
- `SparkAuthBarComponent` — responsive layout.

Everything else (services, guards, interceptors, pipes, form logic, signal reactivity) runs under Vitest — the happy-dom route is ~5× faster than spawning Chromium per test.

Configuration: `playwright-ct.config.ts` at the repo root; tests live in `*.ct.spec.ts` files alongside components. Shares the same Playwright install with E2E — no extra browser download in CI.

### 3.5 E2E stack

- **Runner**: Playwright for .NET (aligns with xUnit and keeps one language).
- **Browsers**: Chromium only in v1.
- **Target**: new project `MintPlayer.Spark.E2E.TestHost` — a minimal ASP.NET Core + Angular app with a stable, test-owned entity model (`TestEntity`, `TestChildEntity`, a `Replicated` entity, a protected entity). Test host is *not* shipped; it exists only for this test project.
- **Test project**: `MintPlayer.Spark.E2E.Tests` — uses `WebApplicationFactory` to boot the host against in-memory Raven via `SparkTestDriver`, launches Playwright against it.
- **State isolation**: fresh Raven store per test class; fixture data seeded from JSON.

---

## 4. Coverage Plan — .NET

### 4.1 MintPlayer.Spark.Abstractions

Pure-logic unit tests only (~10 tests).

- **Models**: `SparkQuery` (sorting, alias generation, projection flags), `TranslatedString` (null/empty/missing-key JSON round-trip), `ValidationRule` (pattern evaluation, error message formatting), `SortColumn` (parse/serialize), `DynamicLookupReference`, `EntityTypeDefinition` / `EntityAttributeDefinition` (attribute merging).
- **Attributes**: extend existing `PersistentObjectAttributeTests.cs` for edge cases (complex-type getters, null coercion, rule serialization).
- Authorization interfaces have no implementation here — no tests.

### 4.2 MintPlayer.Spark (core framework)

#### Services — unit

- **EntityMapper** (+8 tests): enum→string, `Color`→hex, nested `AsDetail` arrays, missing properties on projection types, null handling, ID fallback.
- **ReferenceResolver** (+6 tests): `IEnumerable` fallback matching, multiple reference properties, circular references. Integration (+4): `ApplyIncludes` against real Raven, `ResolveReferencedDocumentsAsync` batch loading, missing refs.
- **QueryExecutor** (+12 unit): source parsing (`Custom.*` vs `Database.*`), search-filter composition, pagination math. (+8 integration): full executions against Raven indexes, parent-context handoff to custom query methods.
- **ValidationService** (+5): cross-property validation, custom validators, regex rules.
- **LookupReferenceService** (+8 unit, +6 integration): dynamic lookup discovery reflection, adding/deleting/updating lookup refs.
- **ModelLoader / ModelSynchronizer** (+6 unit, +6 integration): model file parsing, attribute merging (projection + collection), synchronization (create-missing) against Raven.
- **Custom actions / queries / translations loaders** (+6 unit, +4 integration).
- **Manager / ActionsResolver / CustomActionResolver** (+4).

#### Endpoints — integration

All via `WebApplicationFactory` + in-memory Raven + seeded JSON fixtures.

- **PersistentObject** (10): path decoding, 404, permission denial (401/403), request-body parsing, validation errors.
- **Queries** (10): pagination/sort/search parsing, parent context, streaming endpoint.
- **Custom actions** (6): invocation, parameter binding, async handling.
- **Misc** (6): permission, culture, aliases, health-check.

#### Configuration — unit

- `SparkBuilder` fluent API, `SparkOptions` override precedence (+4 expanded), `SparkModuleRegistry` (existing — add order-preservation + null cases).

### 4.3 MintPlayer.Spark.Authorization

- **AccessControlService** (+14 unit, +4 integration): group resolution, combined-action expansion (e.g. `EditNew` → `Edit` + `New`), denial precedence, `Everyone` group, wildcard matching, case-insensitivity, caching, default behavior (`AllowAll` vs `DenyAll`), circular group hierarchies, overlapping patterns.
- **SecurityConfigurationLoader** (+8 unit, +3 integration): JSON parse errors, missing file, cache invalidation, `FileSystemWatcher` hot-reload, concurrent reads during reload.
- **ClaimsGroupMembershipProvider** (+5 unit): multiple claim types, case-insensitivity, unauthenticated users, empty claims.
- **Endpoints** (+6 integration): `GetCurrentUser`, `Logout`, `CsrfRefresh`, `Groups`.

### 4.4 MintPlayer.Spark.SourceGenerators

All tests are integration-style snapshot tests using `Verify.SourceGenerators` + `GeneratorDriver`. `.verified.cs` files live next to test files under `SourceGenerators/Snapshots/`.

```csharp
[UsesVerify]
public class ActionsRegistrationGeneratorTests
{
    [Fact]
    public Task Generates_registration_for_derived_actions()
    {
        const string source = """
            public class MyEntityActions : DefaultPersistentObjectActions<MyEntity> { }
            """;
        return CSharpGeneratorVerifier<ActionsRegistrationGenerator>.Verify(source);
    }
}
```

~18 tests spread across `ActionsRegistrationGenerator` (4), `CustomActionsRegistrationGenerator` (4), `SubscriptionWorkerRegistrationGenerator` (3), translation generators (4), `RecipientRegistrationGenerator` (2), `ProjectionPropertyAnalyzer` (1 diagnostic).

Edge cases to cover in all generators: abstract-class skip, generic-parameter extraction, multi-level inheritance, name derivation (strip `Action` / `Actions` / `Worker` suffixes).

### 4.5 MintPlayer.Spark.Messaging

**~8 unit + ~12 integration** (all Raven-backed).

Critical scenarios:
1. Broadcast → subscription delivery → handler invocation.
2. `DelayBroadcastAsync` with future `NextAttemptAtUtc` — not picked up until delay elapses.
3. Handler retry with backoff (5-attempt array).
4. Partial handler failure — one recipient succeeds, another fails; on retry, only the failed one re-runs.
5. Dead-lettering after 5 failed attempts.
6. FIFO within queue; parallel across queues.
7. `NonRetryableException` → immediate dead-letter.
8. `ICheckpointRecipient<T>` state preservation between retries.
9. Corrupted `PayloadJson` → dead-letter immediately.
10. No registered recipients → status `Completed` no-op.
11. `[MessageQueue("…")]` attribute parsing; fallback to FQN.

### 4.6 MintPlayer.Spark.Replication

**~5 unit + ~8 integration** (two Raven stores: app DB + SparkModules DB).

Critical scenarios: module registration discovery, `EtlScriptCollector` grouping, ETL task creation and idempotent updates, sync-action POST success, 5xx retry, 4xx non-retryable, max-retry exhaustion, module-lookup failure, ETL script validation error, sync-action interceptor on create/update with change delta.

### 4.7 MintPlayer.Spark.SubscriptionWorker

**~6 unit + ~9 integration**.

Critical scenarios: subscription-name derivation, creation, idempotent update, batch delivery, `MaxDocsPerBatch` chunking, lifecycle hooks (`OnWorkerStartedAsync`, `OnBatchCompletedAsync`), `SubscriberErrorException` retry, `SubscriptionInUseException` 2× backoff, non-recoverable exceptions (`SubscriptionClosedException`, `DatabaseDoesNotExistException`, `AuthorizationException`) → `OnNonRecoverableErrorAsync` + loop exit, graceful shutdown via `CancellationToken`, `KeepRunning=false` single-shot, `RetryNumerator` increment / `@refresh` scheduling / exhaustion / delay formula.

### 4.8 MintPlayer.Spark.Webhooks.GitHub

**~10 unit + ~10 integration** (WireMock.Net as GitHub).

Critical scenarios:
1. Token cache hit (60+ s remaining) — no HTTP call.
2. Cache refresh at <60 s remaining.
3. Concurrent refresh serialization via `SemaphoreSlim` — 5 concurrent requests → exactly 1 HTTP call.
4. Per-installation isolation (separate cache entries).
5. REST 401 retry via `TokenRefreshingHttpClient`.
6. GraphQL 401 retry via `TokenRefreshingHandler`.
7. No infinite retry — after second 401, propagate.
8. HMAC-SHA256 signature verify success / failure / empty-secret-skip / empty-header-reject.
9. JWT creation — `iat = now-60s`, `exp = iat+10min`, `iss = ClientId`.
10. Cached installation client reuse — same instance returned.
11. App-mode GraphQL never cached — fresh JWT each time.
12. `Dispose()` cleans up semaphore + HTTP clients.
13. Dynamic credential store returns current token; cached client survives refresh without rebuild.
14. Dev-tunnel forwarding when `Development:AppId` matches header.

Cross-reference [PRD-GitHubAppClientCache.md](./PRD-GitHubAppClientCache.md) — these tests lock in the concurrency + caching contract defined there.

### 4.9 MintPlayer.Spark.Webhooks.GitHub.DevTunnel

**~2 unit + ~6 integration**.

- `SmeeBackgroundService`: empty channel → no-op, event processing, JSON re-minification (Smee pretty-prints → must re-minify so HMAC signature still validates).
- `WebSocketDevClientService`: disabled when URL empty, connection + handshake (token auth), message reception, reconnect on drop (5 s backoff), graceful shutdown.

### 4.10 MintPlayer.Spark.AllFeatures + AllFeatures.SourceGenerators

**~2 integration tests**.

- Generator discovers all `SparkSubscriptionWorker<T>` subclasses, emits `AddSparkSubscriptionWorkers()` with one `AddSubscriptionWorker<T>()` per worker.
- Empty assembly → generates empty extension.
- Runtime: after calling generated extension, all workers resolvable as `IHostedService`.

### 4.11 MintPlayer.Dotnet.SocketExtensions

**~6 integration tests** using a real `ClientWebSocket`/test server pair.

- Read small message; read fragmented message (>512 B); write with correct chunking + final bit; close-frame handling; `ReadObject<T>` / `WriteObject<T>` JSON round-trip; null-payload handling.

### 4.12 MintPlayer.Spark.Core and MintPlayer.Spark.FileSystem

Currently empty — no tests until content lands.

---

## 5. Coverage Plan — Angular libraries

Default runner: **Vitest + happy-dom**. Items tagged `[CT]` go through **Playwright Component Testing** instead — only where real browser fidelity matters.

### 5.1 ng-spark

- **Services** (Vitest): `SparkService` (list, get, create, update, delete, `executeQuery`, `getEntityTypes`, `getPermissions`, `getLookupReferences`, `executeCustomAction`) — one `describe` block per endpoint method using `HttpTestingController`. `SparkLanguageService` signal updates. `SparkStreamingService` WebSocket/SSE binding. `RetryActionService` modal-show + retry cycle.
- **Components** (Vitest): form binding and validation-error display for `SparkPoFormComponent` / `SparkPoCreateComponent` / `SparkPoEditComponent` / `SparkPoDetailComponent`; navigation after save; nested detail row CRUD.
- **Components** `[CT]`: `SparkQueryListComponent` (table rendering, sticky headers, sort/pagination/search interactions); `SparkRetryActionModalComponent` (overlay stacking, focus trap, option selection).
- **Pipes** (22, Vitest): one happy-path + one edge-case test each (null input, missing attribute, formatting fallback).
- **Routes** (Vitest): `sparkRoutes()` factory param injection, query-string handling.
- **Renderers** (Vitest): `SparkAttributeRendererRegistry` registration, lookup, fallback.

### 5.2 ng-spark-auth

- **SparkAuthService** (Vitest): login (POST + CSRF refresh + checkAuth cascade); `loginTwoFactor`; register; logout (signal clear); `checkAuth` (200 → signal update; 401 → null); `forgotPassword`; `resetPassword`; `csrfRefresh`.
- **Components** (Vitest): Login (valid submit, 401 error, 401+RequiresTwoFactor redirect), Register (password-match cross-field validator, success redirect), TwoFactor (toggle recovery input, invalid-code error), ForgotPassword / ResetPassword (success + error).
- **Components** `[CT]`: `SparkAuthBarComponent` (responsive layout, user/logout template toggle).
- **Guards** (Vitest): `sparkAuthGuard` — authenticated → `true`; unauthenticated → `UrlTree` redirect to login with `returnUrl`.
- **Interceptors** (Vitest): `sparkAuthInterceptor` — 401 on non-api base → redirect with `returnUrl`; 401 on api base → no redirect loop; XSRF header added on non-GET.
- **Pipes** (Vitest): `TranslateKeyPipe` — key resolution, missing-key fallback.
- **Providers** (Vitest): `provideSparkAuth()` config merging; `withSparkAuth()` HTTP feature composition.

**Estimate**: ~45–60 Angular tests across both libraries (~95% Vitest, ~5% Playwright CT).

---

## 6. Coverage Plan — E2E

`MintPlayer.Spark.E2E.TestHost` (dedicated minimal app) + `MintPlayer.Spark.E2E.Tests` (Playwright).

Scenarios (grouped by framework feature):

**CRUD**
1. Create entity → saved ID returned → visible in list.
2. Read detail page → all fields populated.
3. Update entity → changes persisted.
4. Delete entity → removed from list.
5. Create with validation errors → fix → retry.
6. Nested detail rows: add, update, remove inline.

**Queries**
7. List with sort (column-header click).
8. List with pagination (skip/take).
9. List with search (contains filter).
10. Execute custom query with parameters.
11. Query by parent (detail page tabs).

**Auth**
12. Login (email/password) → home + auth-bar populated.
13. Login with 2FA required → submit code → logged in.
14. Register → redirect to login with registered flag.
15. Logout → auth-bar hidden.
16. Protected route without auth → redirect to login + `returnUrl`.

**Authorization**
17. Forbidden resource (403) → error page (no auto-redirect loop).

**Resilience**
18. Retry-action modal (HTTP 449 with retry payload) → select option → resubmit → completion.
19. CSRF — non-GET request carries XSRF header.
20. Concurrent list queries all resolve.

Playwright runs headless in CI; optional `--headed` mode for local debugging.

---

## 7. IDE Extensions (out of scope for v1)

`extensions/visualstudio/SparkEditor.Vsix/` currently has only a `.csproj.user`. `extensions/vscode/` has only `node_modules` + `out` — no `package.json`, no `src/`. Both are skeletons.

**Decision**: defer testing until each extension has a shippable entry point. When activated:
- **VSCode**: `@vscode/test-electron` + Mocha, covering command registration, JSON-schema validation, language-service providers.
- **VS**: MSTest + VS test host, covering tool window wiring and command handlers.

Revisit when the [Spark Editor PRD](../../.claude/projects/C--Repos-MintPlayer-Spark/memory/spark-editor-prd.md) implementation starts landing code.

---

## 8. CI Integration

**Target**: <2 min PR feedback on unchanged code (cache hit), <6 min cold. Karma-era 10+ min waits are the problem this section exists to solve.

### 8.1 Speed strategy — three layers

1. **Affected-only testing** — don't run tests for projects that weren't touched.
2. **Remote task caching** — if the same project at the same commit hash was already tested green, skip.
3. **Sharding** — split independent test runs across CI runners.

### 8.2 Tooling: Nx + Nx Cloud for the monorepo

The repo is already a hybrid (npm workspaces + .NET solution). Adopt **Nx** as the task orchestrator for both sides:

- **`@analogjs/platform`** + **Nx** — native Angular + Vitest integration.
- **`@nx-dotnet/core`** — community plugin that wraps `dotnet` CLI, exposes each `.csproj` as an Nx project, derives the build/test graph from `<ProjectReference>` edges.
- **`nx affected --target=test`** — computes affected projects from `git diff base..HEAD` and runs only those, across both ecosystems. A PR that only touches `ng-spark-auth` won't run `MintPlayer.Spark.Tests`.
- **Nx Cloud** — remote task cache keyed by `(project, target, input hash)`. Inputs include source files, deps, and tool versions. Cache hit on repeat runs of the same commit = 0 s. Free tier (500 computes/month) is typically enough for a solo/small-team OSS repo; pricing scales from there.

### 8.3 Repo setup — one-time, automatic after clone

Nx + `@nx-dotnet/core` are committed to the repo. Anyone cloning runs:

```bash
npm install
```

That's it — no interactive init. The `nx.json` file registers `@nx-dotnet/core` as a plugin; it automatically discovers every `.csproj` from the solution and surfaces it as an Nx project alongside the npm workspaces. `nx show projects` lists all 30 projects (20 .NET + 6 npm workspaces + 4 ng-packagr libs).

**Component-level Angular tests** (the future ones needing `TestBed`) need `@analogjs/vite-plugin-angular` + `@analogjs/vitest-angular`. When that day comes: add them to `devDependencies` in the root `package.json` and run `npm install`. No other ceremony.

### 8.4 Nx Cloud — remote task caching setup

Nx Cloud turns the local cache into a team-wide cache, so repeat CI runs on unchanged code complete in ~0 s. It's optional but recommended.

**First-time setup (one person, once per repo):**

1. Run the interactive connect command locally:
   ```bash
   npx nx connect
   ```
   This launches a browser, asks you to sign in / create a workspace at `nx.app`, and writes an `"nxCloudId"` into `nx.json`. Commit that change.

2. From the Nx Cloud web UI, create a read-write access token for CI.

3. Add the token to GitHub Actions repo secrets as `NX_CLOUD_ACCESS_TOKEN`.

**Per-developer setup (every contributor):** nothing. The committed `nxCloudId` is enough for read access; writes only happen when `NX_CLOUD_ACCESS_TOKEN` is present (CI).

**Opt-out:** Nx Cloud is fully optional. If `nxCloudId` is absent, Nx falls back to the local-only cache, which still works across shell sessions on the same machine.

### 8.5 Alternative if Nx is ever removed

- **Turborepo** for the JS side (`turbo.json` with `inputs` globs). Works cleanly for `npm test`, less smart than Nx's graph analysis.
- **Custom .NET affected script** — `scripts/affected-dotnet.sh` that diffs git, maps to `.csproj`, walks the reverse-reference graph, and runs `dotnet test` on affected test projects.
- **GitHub Actions cache** on `~/.nuget/packages`, `bin/`, `obj/` keyed by `hashFiles('**/*.csproj')` — covers build caching but not test-result caching.

Recorded for completeness; Nx + `@nx-dotnet/core` is the committed choice.

### 8.6 Workflow — GitHub Actions

```yaml
jobs:
  test-affected:
    strategy:
      matrix:
        shard: [1, 2, 3, 4]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: nrwl/nx-set-shas@v4
      - uses: actions/setup-node@v4
        with: { node-version: '22', cache: 'npm' }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: npm ci
      - run: npx nx affected --target=test --parallel=3 --shard=${{ matrix.shard }}/4
        env:
          NX_CLOUD_ACCESS_TOKEN: ${{ secrets.NX_CLOUD_ACCESS_TOKEN }}

  e2e:
    needs: test-affected
    runs-on: windows-latest  # Playwright + WebApplicationFactory host
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - run: npx nx affected --target=e2e
        env:
          NX_CLOUD_ACCESS_TOKEN: ${{ secrets.NX_CLOUD_ACCESS_TOKEN }}
```

### 8.7 Notes

- RavenDB.TestDriver is self-contained — no Docker in CI.
- `dotnet test` parallelizes across classes but NOT within a Raven-backed class (the embedded server is per-class).
- Coverage threshold gate: 70% on PRs touching shipped package code; 85% on Authorization + webhook signature + auth guard/interceptor. Measured via Coverlet for .NET, Vitest's built-in V8 coverage for Angular, uploaded to Codecov.
- E2E runs on `windows-latest` because the test host binds to localhost with dev-trust certs — Linux is possible but pushes cert setup into CI. Revisit if runner minutes become a cost issue.

---

## 9. Rollout

**Milestone 1 — infrastructure & low-risk** (2 weeks)
- Create `MintPlayer.Spark.Testing` project with `SparkTestDriver`.
- Migrate / extend existing `MintPlayer.Spark.Tests` to use FluentAssertions.
- Add Vitest + `@analogjs/vitest-angular` config to `ng-spark` and `ng-spark-auth`.
- Adopt Nx at the repo root (`nx.json`, project graph for both npm workspaces and `.csproj` projects via `@nx-dotnet/core`). Wire Nx Cloud.
- CI workflow (`test.yml`) up and green, running `nx affected` on PRs.

**Milestone 2 — core services & security** (3 weeks)
- All `MintPlayer.Spark.Authorization` tests (security-critical first).
- Core services: `QueryExecutor`, `EntityMapper` expansions, `ReferenceResolver`, `ModelLoader`, `ValidationService`, `LookupReferenceService`.
- All source generator snapshot tests.
- `SparkAuthService`, `sparkAuthGuard`, `sparkAuthInterceptor` Angular tests.

**Milestone 3 — endpoints & infra packages** (3 weeks)
- PersistentObject + Query + Custom Action endpoint integration tests.
- Messaging, Replication, SubscriptionWorker test suites.
- `GitHubInstallationService` concurrency tests (locks in the contract from [PRD-GitHubAppClientCache.md](./PRD-GitHubAppClientCache.md)).
- `ng-spark` component and pipe tests.

**Milestone 4 — E2E** (2 weeks)
- `MintPlayer.Spark.E2E.TestHost` app.
- `MintPlayer.Spark.E2E.Tests` with all 20 scenarios.
- DevTunnel + SocketExtensions integration tests.

---

## 10. Open Questions

1. **Per-library test projects vs. one monolithic project** — proposed: keep one (`MintPlayer.Spark.Tests`) split by folder, plus the separate `MintPlayer.Spark.Testing` reusable helpers. Revisit if build/run time exceeds ~3 min.
2. **Verify snapshot storage** — committed to git vs. `.gitignore`d and regenerated in CI? Proposed: commit; snapshot diffs are the signal that something changed intentionally or not.
3. **Raven license in CI** — the CronosCore `.targets` imports the embedded Raven server, which includes a test license valid for TestDriver use. Confirm no Azure-blob license fetch is required at test time.
4. **Coverage tool** — Coverlet (default xUnit collector) or `dotnet-coverage` (MSFT). Proposed: Coverlet, uploaded to Codecov.
5. **Nx adoption scope** — proposed: adopt Nx for the full monorepo (JS + .NET via `@nx-dotnet/core`). Fallback if too invasive: Turbo for JS + custom `affected-dotnet.sh` for .NET. Decide before milestone 1.
6. **Nx Cloud tier** — proposed: free tier initially; upgrade when free computes run out. Self-hosted remote cache (`@nx/nx-cloud` on-prem) is a fallback if commercial pricing is blocking.

---

## 12. Implementation status (as of 2026-04-20)

**481 tests passing across the monorepo** — 268 .NET (`MintPlayer.Spark.Tests`) + 4 E2E (`MintPlayer.Spark.E2E.Tests`, Playwright-driven) + 53 ng-spark-auth + 156 ng-spark.

### Done ✅

- **M1 (infrastructure)** — `MintPlayer.Spark.Testing` project, FluentAssertions everywhere, Vitest + `@analogjs/vite-plugin-angular` in both Angular libs, Nx + `@nx-dotnet/core` + Nx Cloud, license env-var wiring, CI workflow rewritten.
- **M2 partial** — `AccessControlService` (15), `ClaimsGroupMembershipProvider` (10), `ValidationService` (18), `SecurityConfigurationLoader` (11), `QueryExecutor` (9 unit + 7 integration via `SparkTestDriver`).
- **M3 endpoints** — `PersistentObject` GET/LIST/CREATE/UPDATE/DELETE (18) via `SparkEndpointFactory`; `Queries` GET/LIST/EXECUTE (12); `Authorization` — `GetCurrentUser` + `Logout` + `CsrfRefresh` + `SparkAuthGroup` (10); `Custom Actions` — `ListCustomActions` + `ExecuteCustomAction` (16); `StreamExecuteQuery` WebSocket (7) + `StreamingDiffEngine` (9).
- **M3 services** — `GitHubInstallationService` cache + JWT + decorators via reflection (15); WireMock.Net-backed end-to-end token refresh + 401-retry (5, behind a new `IGitHubClientFactory` seam); `RetryNumerator` (6); `MessageBus` (5) + `MessageCheckpoint` (3); `EtlScriptCollector` (5) + `SyncActionInterceptor` (8); `SocketExtensions` (6, via TestServer WebSocket pair); `GitHubWebhooksDevTunnelExtensions` (4, `AddSmeeDevTunnel` + `AddWebSocketDevTunnel` composition).
- **M3 subscription-worker E2E** — `MessageSubscriptionWorker` driven by real RavenDB subscriptions via `SparkTestDriver` (7: happy path, empty-recipients rollup, NonRetryable handler, MaxAttempts=1 dead-letter, single-pickup retry scheduling, unresolvable MessageType, mixed handler rollup); `SyncActionSubscriptionWorker` with co-located `SparkModulesTest` db + stub `IHttpClientFactory` (5: 200/400/404/500 paths, unknown owner module via `RetryNumerator`).
- **M3 Angular** — `SparkAuthService` + guard + interceptor (15), all 6 ng-spark-auth components (27), all 22 ng-spark pipes (70), `RetryActionService` + `IconRegistry` + `IconComponent` + `RetryActionModal` (17), `SparkPoCreate` + `SparkPoEdit` + `SparkQueryList` (21), `SparkPoFormComponent` + `SparkPoDetailComponent` + `SparkSubQueryComponent` (43).
- **M4 Playwright E2E** — New `MintPlayer.Spark.E2E.Tests` project driving the existing `Fleet` Demo app end-to-end (per relaxation of the "no-Demo-app testing" rule). `FleetTestHost` fixture owns: (a) an embedded RavenDB via `SparkTestDriver`, (b) Fleet launched as a `dotnet run` subprocess in the `E2E` environment with a `appsettings.E2E.json` override pointing at the embedded server, (c) admin-user seeding via the real `/spark/auth/register` endpoint + a direct Raven patch to add the `Administrators` group claim, (d) Playwright Chromium installation and per-test `BrowserContext`. 4 tests: SPA shell is served (Angular bundle + ng-spark), `/spark/auth/me` reports anonymous, cookie-based login → authenticated `/me` (Authorization + CSRF), `/login` route renders the ng-spark-auth sign-in form (ng-spark-auth components). Touches every browser-visible library.

### Deferred (handoff for next sessions)

| Item | Why scoped out | Notes for the next batch |
|---|---|---|
| **Source-generator snapshot tests** | Generator targets `netstandard2.0` and pulls `MintPlayer.SourceGenerators.Tools` polyfills (esp. `ModuleInitializerAttribute`) that collide with `net10.0`'s `System.Runtime` at test load time | A first attempt with `Assembly.LoadFrom` from a separate `MintPlayer.Spark.SourceGenerators.Tests` project + `ExcludeAssets="compile"` on Tools got further but still hit `Microsoft.CodeAnalysis.CSharp` version mismatch. Worth a dedicated focused session with pinned `Microsoft.CodeAnalysis.Testing` versions across the dep graph. |
| **AllFeatures source generator** | Smaller, included with the source-generator session above | |

## 13. Implementation notes (real-world findings, not in original PRD)

These are non-obvious things discovered while implementing. They cost real time to figure out — capture them here so future contributors don't re-hit them.

### 13.1 .NET infrastructure

- **RavenDB 7.x requires a license even for embedded TestDriver.** `MintPlayer.Spark.Testing/SparkTestDriver.cs` loads it from `RAVENDB_LICENSE` env var (CI) or a gitignored `raven-license.log` at the repo root (local). Throws an actionable error if neither is present. Both `pull-request.yml` and `dotnet-build-master.yml` pass the secret.
- **CronosCore.RavenDB.UnitTests is a design reference, NOT a runtime dependency.** Its base class is NUnit-coupled (`[TestFixture]`, `[SetUp]`) and its license fetch targets a private Cronos Azure blob. We re-implemented the small amount of value-added code in `MintPlayer.Spark.Testing` using xUnit's `IAsyncLifetime` pattern.
- **`InternalsVisibleTo("DynamicProxyGenAssembly2")`** must be on every project whose internal interfaces NSubstitute needs to mock. Currently set on `MintPlayer.Spark` and `MintPlayer.Spark.Authorization`.
- **`<IsTestProject>false</IsTestProject>`** must be set on `MintPlayer.Spark.Testing.csproj` — it has an xunit reference (for `IAsyncLifetime` types) but is NOT a test project. Without this, `dotnet test <sln>` tries to discover tests in it and exits non-zero with a confusing "0 Error(s), Build FAILED".
- **`WebApplicationFactory<T>` doesn't work** when the host assembly has no `Main` entry point. `MintPlayer.Spark.Tests/_Infrastructure/SparkEndpointFactory.cs` uses `TestServer + IHost` directly instead.
- **Built-in `app.UseAntiforgery()` only validates form-content bodies** (ASP.NET Core 8.0.1 breaking change). Spark's JSON API was therefore unprotected — `RequireAntiforgeryTokenAttribute(true)` metadata was effectively dead code. **Fixed** by adding a supplemental middleware in `UseSpark()` that runs BEFORE `UseAntiforgery()`, calls `IAntiforgery.ValidateRequestAsync` on any mutating method whose endpoint has `IAntiforgeryMetadata.RequiresValidation=true`, and sets a custom `IAntiforgeryValidationFeature` so (a) `FormFeature`'s "unvalidated" guard doesn't trip on later form reads and (b) `EndpointMiddleware` doesn't throw "contains anti-forgery metadata but no middleware was found". Covered by `AntiforgerySecurityTests`.
- **`SPARK001` validation must accept `<ProjectReference OutputItemType="Analyzer">`**, not just `<PackageReference>`. The original implementation only checked PackageReference and broke monorepo Demo apps.
- **Endpoint-level `IEndpointBase.Configure` metadata** (e.g., `RequireAntiforgeryTokenAttribute` on `Logout` / `ExecuteCustomAction`) is applied via a *convention*, not directly on the builder. The metadata only materializes into the `EndpointDataSource` after `app.StartAsync()`. Test shape: boot a `WebApplication`, `MapPost` a dummy endpoint, invoke the static `Configure` through a generic helper (`static void InvokeConfigure<TEndpoint>(RouteHandlerBuilder) where TEndpoint : IEndpointBase => TEndpoint.Configure(b)`), `StartAsync`, then enumerate `IEnumerable<EndpointDataSource>`.
- **Octokit base-URL quirk:** `new GitHubClient(header, uri)` treats a custom base URL as GitHub Enterprise and prepends `/api/v3/` to every request; `new Connection(header, uri, credentialStore, httpClient, serializer)` does NOT. WireMock stubs need to account for both shapes — the App client (token minting) hits `/api/v3/app/installations/.../access_tokens` but the installation REST client hits `/repos/...` directly.
- **NSubstitute can't record the "last call"** for methods whose parameter list includes a `Func<object, object>` (e.g., Octokit's `IHttpClient.Send(IRequest, CancellationToken, Func<object, object>)`). `Returns(...)` after such a call fails with `CouldNotSetReturnDueToNoLastCallException`. Workaround: use plain stub classes (`internal sealed class StubOctokitHttpClient : IHttpClient`) instead of `Substitute.For<IHttpClient>()`. This affected the `TokenRefreshingHttpClient` tests.
- **`DateTime.Parse("2026-04-20T10:00:00Z")` drops the `Z` and returns a `Local`-kind `DateTime`.** Subtracting `DateTime.UtcNow` (UTC kind) then double-counts the machine's UTC offset, inflating "delay" assertions by hours. Parse with `DateTimeStyles.RoundtripKind` to preserve UTC kind. This hit the `RetryNumerator` linear-backoff test.
- **WireMock.Net end-to-end flows use *scenarios* for sequenced responses.** For a 401 → refresh → retry test, use `.InScenario("retry").WhenStateIs(null).WillSetStateTo("after-401")` for the first call and `.WhenStateIs("after-401")` for the retry. State machine over header-matching is more robust — the stub keeps working across refactors.
- **`TestServer.CreateWebSocketClient()` pre-upgrade failures** surface as `InvalidOperationException: Incomplete handshake, status code: NNN`, NOT as `WebSocketException`. When the endpoint returns `404`/`400` before calling `AcceptWebSocketAsync` (e.g., `StreamExecuteQuery`'s "unknown query" or "non-streaming query" paths), assert on the exception message's status-code substring rather than the exception type.
- **`Microsoft.NET.Sdk.Web` implicitly stages `App_Data/**` as `Content` with `CopyToPublishDirectory=PreserveNewest`.** That propagates through `ProjectReference` chains. For libraries whose `App_Data/translations.json` only needs to be an `AdditionalFiles` input for the source generator (and NOT copied to the consuming app's publish output), add `<Content Remove="App_Data\translations.json" />`. Without it, apps that reference multiple Spark libraries hit `NETSDK1152` at publish time.
- **Subscription-worker E2E tests drive the worker directly — not through `MessageSubscriptionManager`.** Each test instantiates `MessageSubscriptionWorker` with a single queue name + a small `ServiceProvider` holding the desired `IRecipient<T>` registrations, calls `StartAsync`, and polls `SparkMessage`/`SparkSyncAction` state via `Store.OpenAsyncSession().LoadAsync(id)` until terminal. `MaxDocsPerBatch = 1` (baked into the worker) makes transitions deterministic. Nested `public record` message types work because `typeof(T).FullName` uses the same `+` separator that `Type.GetType` (via `AssemblyQualifiedName`) round-trips.
- **`RavenTestDriver` runs one shared embedded server across test instances.** Any DB created directly via `Store.Maintenance.Server.Send(new CreateDatabaseOperation(...))` persists between tests in the same xUnit process. `SyncActionSubscriptionWorkerE2ETests` addresses the co-located `SparkModules` DB by using a per-instance GUID suffix (`$"SparkModulesTest-{Guid.NewGuid():N}"`). Don't hard-code DB names when you need isolation from prior test runs.
- **Test-found production bug (fixed in this slice):** when a handler invoked via reflection throws `NonRetryableException`, it's wrapped in `TargetInvocationException`. In `MessageSubscriptionWorker`'s catch chain the two `when`-filtered catches must be ordered `TargetInvocationException`-first, then `Exception`. The original order let `IsNonRetryable(ex)` pass on the outer exception (via `InnerException is NonRetryable`) and stored the wrapped `.Message` ("Exception has been thrown by the target of an invocation.") instead of the real one. More-specific catches first, always.
- **Test-found production bug #2 (fixed in this slice):** `SyncActionSubscriptionWorker`'s retry loop was running at subscription-change-vector speed instead of the configured linear backoff. The original query `from SparkSyncActions where Status = 'Pending'` re-matched the document every time the worker saved it back — and `@refresh` metadata alone does NOT gate subscription delivery (it's consulted by Raven's Refresh *cleaner*, not by subscription queries). So `RetryNumerator` burned through `MaxAttempts` (default 5) in milliseconds, losing the intended 30s/60s/90s/... pacing entirely. A local run caught `counter=1`, CI caught `counter=4`, neither with any real cooldown. **Fix**: (a) added `NextAttemptAtUtc` to `SparkSyncAction`, (b) changed the subscription query to `... and (NextAttemptAtUtc = null or NextAttemptAtUtc <= now())` — the same pattern `MessageSubscriptionWorker` already used for its own backoff, (c) `RetryNumerator.TrackRetryAsync` now returns a `RetryOutcome` record (WillRetry, AttemptCount, NextAttemptAtUtc) so callers can project the scheduled time onto an entity field that the subscription query can see. The worker clears `NextAttemptAtUtc` on success and terminal failure. E2E assertions are now deterministic on both local and CI: first attempt fires, counter=1, `NextAttemptAtUtc` in the future, no re-delivery within the test window.

- **Test-found production bug #3 (fixed in this slice):** `ConfigurationBinder.Bind()` APPENDS array values from config to a non-empty default — it does NOT replace by index. `RavenDbOptions.Urls = ["http://localhost:8080"]` (C# default) meant an `appsettings.{env}.json` override like `["http://my-raven:8080"]` produced `["http://localhost:8080", "http://my-raven:8080"]` after binding, and Raven's client used the FIRST URL → always connected to `localhost:8080`. **Fix**: `RavenDbOptions.Urls` default is now `[]`; `SparkExtensions.AddSpark` falls back to `["http://localhost:8080"]` only when the bound array is empty. Overrides actually override now. Discovered because the E2E fixture's override couldn't redirect Fleet from a dev-machine Raven on 8080 to the embedded test Raven.
- **Test credentials must be randomized per fixture run**, not hardcoded constants — GitGuardian (and similar scanners) flag any long-lived password-looking string in source as a leaked secret, even inside test infra. `FleetTestHost` generates the admin password from `Base64(Guid.NewGuid().ToByteArray())` prefixed with `Aa1!` to satisfy ASP.NET Identity's default validator. Same principle applies to any future fixture that seeds a known-good credential.
- **E2E test fixture for Fleet** (`MintPlayer.Spark.E2E.Tests._Infrastructure.FleetTestHost`): boots embedded Raven via `SparkTestDriver`, creates GUID-suffixed `SparkFleetE2E-{...}` + `SparkModulesE2E-{...}` databases on it, writes `appsettings.E2E.json` into Fleet's **project dir** (not `bin/Debug/net10.0/` — ASP.NET Core's content root resolves from CWD, and we set `WorkingDirectory=fleetDir`), spawns `dotnet run --project Fleet.csproj --configuration Debug --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=E2E` and `ASPNETCORE_URLS=https://localhost:{ephemeral};http://...`. Admin is seeded via the real `/spark/auth/register` endpoint (so password hashing matches Identity's configured `PasswordHasher`) then patched directly in Raven to add a `"group": "Administrators"` claim that `ClaimsGroupMembershipProvider` reads. Playwright Chromium is installed via `Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"])`; per-test `BrowserContext` with `IgnoreHTTPSErrors=true` accepts the Kestrel dev cert.

### 13.2 Angular / Vitest infrastructure

- **`@angular/router/testing`'s `RouterTestingHarness` is the official recommended pattern** ([angular.dev/guide/routing/testing](https://angular.dev/guide/routing/testing)) — *"Avoid mocking Angular Router."* Spies on `router.navigate*` are anti-pattern; use `provideRouter([routes])` + assert via `TestBed.inject(Router).url` after navigation completes.
- **Components in this repo fire-and-forget `router.navigate*(...)`** without awaiting the returned `Promise`. `harness.fixture.whenStable()` alone is not reliable in zoneless mode — use `nextNavigationEnd()` from `node_packages/{ng-spark,ng-spark-auth}/src/test-utils.ts` (subscribes to `Router.events` BEFORE the trigger and awaits the next `NavigationEnd`).
- **`TestBed.runInInjectionContext`** for invoking `CanActivateFn` — NOT the imported `runInInjectionContext` function from `@angular/core` (the `TestBed` itself is not a valid `Injector`).
- **Test-utility files (`src/test-setup.ts`, `src/test-utils.ts`) MUST be excluded from `tsconfig.lib.json`** in both Angular libraries. Otherwise ng-packagr type-checks them and trips on `vitest` / `@angular/router/testing` not being declared as deps. Already excluded; new test-utility files need the same treatment.
- **`vitest.config.ts` `resolve.alias` mirrors `tsconfig.base.json`** so source files keep using their `@mintplayer/ng-spark/services`-style imports at test time without needing a built `dist/`. Pattern in `node_packages/ng-spark/vitest.config.ts`.
- **`provideNoopAnimations()`** is required for components using synthetic animations (`@fadeInOut` etc.). Without it: `NG05105: Unexpected synthetic listener`.
- **`provideHttpClient() + provideHttpClientTesting()`** is required when any tree-shakable `providedIn: 'root'` service in the injector tree auto-fetches on construction (e.g. `SparkLanguageService` hits `/spark/culture` and `/spark/translations`). Without it, unhandled rejections flood the test output even though assertions pass.
- **Sequential awaited HTTP via `firstValueFrom` needs a microtask flush between `expectOne` calls.** Pattern in `spark-auth.service.spec.ts`'s `flush()` helper: `await new Promise<void>(r => setTimeout(r, 0))`.
- **Stub `Router` with `vi.fn()` is broken** because `RouterLink` directive subscribes to `Router.events` (which is undefined on a bare stub). Always use `provideRouter([])` even when you don't care about navigation, just for the directive's DI.
- **Angular 21 signal inputs** (`input.required<T>()`) are set in tests via `fixture.componentRef.setInput(name, value)` + `fixture.detectChanges()`.
- **Zoneless `fixture.whenStable()` does NOT await `effect()`-spawned async work.** Components like `SparkPoFormComponent` / `SparkSubQueryComponent` kick off `Promise.all(...)` loaders from an `effect()` and set signals when they resolve. Use a multi-tick microtask flush after `detectChanges()`:
  ```typescript
  async function flush() {
    for (let i = 0; i < 5; i++) await new Promise<void>(r => setTimeout(r, 0));
  }
  ```
  Three ticks is usually enough; five is a cheap upper bound.
- **`RouterLink` in a non-router test setup** needs `provideRouter([])`. `SparkSubQueryComponent`'s template imports `RouterModule` for anchor-style links even though the component itself has no route, so any TestBed that renders it must include `provideRouter([])`.
- **`SparkAuthBarComponent.onLogout()` now wraps the service call in `try { ... } finally { router.navigateByUrl('/') }`** so navigation runs even when `authService.logout()` rejects (network error, already expired session). The local session state is cleared by `SparkAuthService` regardless, and the user is returned to the anonymous area. The spec asserts this behavior on both success and rejection paths.

### 13.3 CI / Nx

- **`npx nx fix-ci` is NOT a generic CI-healing command.** It analyzes failed Nx Cloud CIPE runs and needs explicit `<project>:<target>` args without CIPE context. Don't add it as a workflow step.
- **`@nx-dotnet/core` works with Nx 20–22** — confirmed in production. `nx.json` registers it as a plugin; it auto-discovers every `.csproj` in the solution.
- **Demo apps must be excluded from `nx affected --target=test`** because they have no `test` target. Root `package.json` script: `nx affected --target=test --exclude=@spark-demo/*,DemoApp,DemoApp.Library,Fleet,Fleet.Library,HR,HR.Library,WebhooksDemo,WebhooksDemo.Library`.
- **`nxCloudId` in `nx.json` is a public identifier** (safe to commit). Only `NX_CLOUD_ACCESS_TOKEN` (read-write API key) is a secret. Older Nx versions supported `nxCloudAccessToken` directly in `nx.json` — never do that.

### 13.4 Source-generator testing blocker (unresolved)

Attempted to test `ActionsRegistrationGenerator` and `CustomActionsRegistrationGenerator` via `Verify.SourceGenerators` snapshots. Sequence of problems hit:

1. `ProjectReference` to `MintPlayer.Spark.SourceGenerators` (netstandard2.0) into the net10.0 test project leaks `MintPlayer.SourceGenerators.Tools`'s `ModuleInitializerAttribute` polyfill, colliding with `System.Runtime`.
2. `Assembly.LoadFrom(generatorDll)` at test time avoided the compile-time leak BUT failed because `MintPlayer.SourceGenerators.Tools` runtime types weren't on the load path.
3. Adding `MintPlayer.SourceGenerators.Tools` as a `PackageReference` with `ExcludeAssets="compile"` solved the runtime-load issue.
4. Then hit `Microsoft.CodeAnalysis.CSharp` version conflict — `Verify.SourceGenerators` ships with 4.14.0 but the generator was compiled against 5.3.0; bypassing one breaks the other.
5. RS1035 ("Don't do file IO in analyzers") flowed through transitively and rejected the test helper code.

A dedicated focused session (with the latest `Microsoft.CodeAnalysis.Testing` versions and explicit version-pinning across the dep graph) should crack this. Until then, generator regression testing is via the actual Demo app builds — a generator regression breaks one of the Demo apps' compilation.

## 11. Success Criteria

- Green CI on `master` and all PRs for 2 consecutive weeks after milestone 4.
- 70% line coverage on shipped `.nupkg` and `.tgz` code.
- 85% line coverage on `MintPlayer.Spark.Authorization`, webhook HMAC path, `sparkAuthGuard`, `sparkAuthInterceptor`.
- All scenarios from §4 and §6 have at least one passing test.
- No production regression in the following 4 weeks attributable to an area covered by tests added under this plan.
