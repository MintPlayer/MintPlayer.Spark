# PRD: Test Coverage — Unit, Integration & E2E

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-04-18 |
| **Status** | Proposed |
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

### 3.2 CronosCore test helper — adoption

Location: `C:\Repos\CronosCore\CronosCore.RavenDB.UnitTests`. The library already publishes to NuGet (current `3.22.0`).

**Adopt only the RavenDB-generic surface — NOT the Vidyano-specific types:**

Reuse:
- `RavenDBTestDriver` (base class: embedded server, license provisioning, per-test store lifecycle).
- `ImportScopeCollection` + `ImportReader` (JSON fixture I/O; RavenDB Smuggler format — `{ "Results": [ { "@metadata": { "@id": "...", "@collection": "..." }, ... } ] }`).
- Snapshot setup pattern (`DerivePathInfo` → `VerifyResults/{ClassName}/{MethodName}.verified.*`).
- `.targets` file (auto-copies the RavenDB server binaries, cleans run artifacts).

Do NOT reuse (Vidyano-coupled):
- `VidyanoTestDriver` and its partials (`.Ex`, `.Exceptions`, `.Hooks`, `.ImportReader`, `.ImportScope`, `.Mockups`, `.Options`, `.VidyanoServer`).
- `PersistentObject`, `PersistentObjectAttribute`, `PersistentObject.ViewModelBase` — those are Vidyano view-model wrappers; Spark has its own types.

**Implementation**: introduce a small `MintPlayer.Spark.Testing` project (new) that exposes:

```csharp
public abstract class SparkTestDriver : RavenDBTestDriver
{
    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;
    protected HttpClient Client => Factory.CreateClient();

    protected void SeedFixtures(params string[] jsonFiles) { /* uses ImportReader */ }
    protected Task<T> SendAsync<T>(HttpRequestMessage req) { /* JSON round-trip */ }
}
```

This project is consumed as a `ProjectReference` by `MintPlayer.Spark.Tests` (and any future per-library test projects).

Reference for `CronosCore.RavenDB.UnitTests` details: [reference_cronoscore_raven_tests.md](../../.claude/projects/C--Repos-MintPlayer-Spark/memory/reference_cronoscore_raven_tests.md).

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

### 8.3 Alternative if Nx is too invasive

If adopting Nx is too large a repo change:

- **Turborepo** for the JS side (`turbo.json` with `inputs` globs). Works cleanly for `npm test`, less smart than Nx's graph analysis.
- **Custom .NET affected script** — `scripts/affected-dotnet.sh` that:
  1. Runs `git diff --name-only $BASE` to find changed files.
  2. Maps files to `.csproj` via file-system walk.
  3. Walks the reverse-dependency graph using `dotnet list <proj> reference` to find all test projects transitively depending on a changed project.
  4. Runs `dotnet test <affected-test-project>` for each.
- **GitHub Actions cache** on `~/.nuget/packages`, `bin/`, `obj/` keyed by `hashFiles('**/*.csproj', '**/packages.lock.json')` — covers build caching but not test-result caching.

This route is workable but reinvents what Nx already does. Recommend Nx unless there's a strong aversion to it.

### 8.4 Workflow — recommended (Nx-based)

```yaml
jobs:
  setup:
    runs-on: ubuntu-latest
    outputs:
      nx-base: ${{ steps.nx-base.outputs.base }}
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: nrwl/nx-set-shas@v4
        id: nx-base  # computes base SHA for `nx affected`

  test-affected:
    needs: setup
    strategy:
      matrix:
        shard: [1, 2, 3, 4]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
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

### 8.5 Notes

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

## 11. Success Criteria

- Green CI on `master` and all PRs for 2 consecutive weeks after milestone 4.
- 70% line coverage on shipped `.nupkg` and `.tgz` code.
- 85% line coverage on `MintPlayer.Spark.Authorization`, webhook HMAC path, `sparkAuthGuard`, `sparkAuthInterceptor`.
- All scenarios from §4 and §6 have at least one passing test.
- No production regression in the following 4 weeks attributable to an area covered by tests added under this plan.
