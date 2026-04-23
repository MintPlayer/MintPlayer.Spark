# PRD: MintPlayer.Spark.Client — Post-Merge Follow-ups

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-04-21 |
| **Status** | Proposed |
| **Owner** | MintPlayer |
| **Package** | `MintPlayer.Spark.Client`, `MintPlayer.Spark.E2E.Tests`, `MintPlayer.Spark.Tests` |
| **Supersedes** | n/a — first iteration |
| **Precedes** | continues the work started in the security-audit PR (merged 2026-04-21) |

> The security-audit PR landed `MintPlayer.Spark.Client` with the minimum surface the new unit tests needed (PersistentObject CRUD + query execute). This PRD captures the **four** follow-ups required to make the client the single canonical way C# code — production consumers, unit tests, and the Playwright e2e suite — talks to a Spark backend.

---

## 1. Problem Statement

The merged security-audit PR introduced `MintPlayer.Spark.Client` to eliminate hand-built JSON bodies and CSRF plumbing across ~40 lines per endpoint test. Two retrofitted test classes (`UpdateEndpointConcurrencyTests`, `ExecuteQueryParentGateTests`) proved the pattern works, but the codebase today has **three parallel ways** to call a Spark backend:

1. **`SparkClient`** — new, typed, ~270 LOC. Used by 2 test classes.
2. **Raw `HttpClient` + hand-built JSON** — used by 7 older endpoint test classes in `MintPlayer.Spark.Tests/Endpoints/` (`UpdateEndpointTests`, `GetEndpointTests`, `CreateEndpointTests`, `DeleteEndpointTests`, `ListEndpointTests`, `AntiforgerySecurityTests`, and the three query endpoint tests).
3. **`SparkApi` wrapper over Playwright's APIRequestContext** — in `MintPlayer.Spark.E2E.Tests/Security/_SecurityTestHelpers.cs` (~60 LOC), used by every Security/*.cs e2e test.

This tri-modal state has four costs:

- **Duplicated CSRF handling.** Antiforgery warmup + cookie threading is re-implemented in `SparkApi` and was implemented a third time by the older endpoint tests (via the now-private `SparkEndpointFactory.MintAntiforgeryAsync`).
- **No strong typing end-to-end.** `SparkApi` returns `Response.JsonAsync()` → `JsonElement`; tests navigate anonymous JSON. Refactors in `PersistentObject` or `QueryResult` don't surface as compile errors.
- **Surface gap.** `SparkClient` currently lacks methods for the operations the e2e suite relies on: login/session, `ExecuteActionAsync`, registered-stream download, query by alias with parent context, and the full action-arg shape. Without those, the e2e `SparkApi` can't be retrofitted.
- **No dedicated test coverage.** `SparkClient` gets incidental coverage from the two retrofitted tests, but branches like the `baseUrl` constructor, delete, warmup-failure paths, and query-by-alias are untested. The `ignore` entry on `MintPlayer.Spark.Testing/**` in `codecov.yml` means the testing helpers themselves will never surface a regression either.

---

## 2. Goals

1. **Single canonical client** — every C# caller (tests, prod) goes through `SparkClient`. Delete `SparkApi`, fold its logic (or the bits worth keeping) into `SparkClient`.
2. **Surface parity with the endpoints Spark actually exposes** — not full Vidyano.Core parity (that's a separate project), but parity with every endpoint the e2e suite and the existing endpoint tests currently hit.
3. **Typed return values** — no caller should need to read `JsonElement` to assert on a Spark response.
4. **Dedicated test project `MintPlayer.Spark.Client.Tests`** that covers the client's branches (ctor overloads, CSRF warmup paths incl. failure, non-success statuses, query-by-alias).
5. **Zero regression in existing test runtime** — retrofitted tests must still pass and not slow down noticeably.

### Non-goals

- **Vidyano.Core API parity.** That library has `SignIn*`, `GetStreamAsync`, `ExecuteActionAsync` with full argument surface, action hooks, etc. — far more than Spark currently implements server-side. We mirror the parts Spark already serves; no stub methods for things that don't exist server-side yet.
- **Breaking changes to `SparkClient`'s current public API.** The two retrofitted test classes must keep working with no changes.
- **Caching, retry, or circuit-breaker behaviour.** Interesting, but out of scope — this PRD is about surface and adoption, not resilience. File separately if needed.
- **Distributed-tracing / OpenTelemetry integration.** Add later if operational need arises.
- **Finishing the security-audit test matrix** (H-4 fail-closed host, M-2 token tampering, M-4/L-4 marker attributes). Tracked in `docs/PRD-SecurityAudit.md` §8 and the merged PR's test plan; may reuse the expanded `SparkClient` once it lands, but are not gated by this PRD.

---

## 3. Design

Four work streams, in dependency order.

### 3.1 Surface expansion

Add the endpoint methods that `SparkApi` and the older endpoint tests need.

| Method | Backing endpoint | Shape |
|---|---|---|
| `LoginAsync(string email, string password)` | `POST /spark/auth/login` | Returns the session PersistentObject; stores the auth cookie in the client's cookie header so subsequent calls are authenticated. |
| `LogoutAsync()` | `POST /spark/auth/logout` | Clears the auth cookie. |
| `RegisterAsync(string email, string password)` | `POST /spark/auth/register` | Returns the created user PersistentObject or throws on validation failure. |
| `ExecuteActionAsync(string action, PersistentObject parent, IDictionary<string, object>? parameters = null)` | `POST /spark/actions/{action}` | Typed result — see §3.1.1 for the JSON shape. Covers custom-actions tests the e2e suite runs. |
| `GetStreamAsync(PersistentObject registeredStream)` | `GET /spark/streams/{id}` | Returns `(Stream, string contentType)` — the Spark equivalent of Vidyano's registered-stream download. Mark **deferred** until the server actually exposes the endpoint; today it's absent, so the method stays out of the client until it's needed. |
| `GetPersistentObjectByAliasAsync(string alias, string id)` | `GET /spark/po/{alias}/{id}` | Convenience overload of `GetPersistentObjectAsync` — the existing typed-by-Guid overload stays; adding the string alias variant removes a `.ToString()` round-trip per call in callers that only know the alias. |

`SparkClient` is `public class` (not sealed) so downstream consumers can subclass for org-specific extensions without forking; overloads go directly on the class.

#### 3.1.1 Action response shape

Actions can return one of three shapes (see `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs`):

1. A full `PersistentObject` (e.g., an action that produces a new record).
2. A JSON object with `{ "persistentObject": { ... }, "retryResults": [ ... ] }` — the retry-action protocol.
3. An empty 200 (fire-and-forget action).

`ExecuteActionAsync` returns a `SparkActionResult` wrapper:

```csharp
public sealed class SparkActionResult
{
    public PersistentObject? PersistentObject { get; init; }
    public RetryActionOption[]? RetryOptions { get; init; }  // null when not a retry-action
    public int StatusCode { get; init; }
}
```

Callers that know the action returns a PO navigate `.PersistentObject`; the retry-action case is observable via `.RetryOptions`. The client translates server-side `SparkRetryActionException` (HTTP 449) into a populated `SparkActionResult` rather than a thrown `SparkClientException`, because the "retry action" is a valid in-protocol response, not an error.

### 3.2 Retrofit the e2e `SparkApi` helper

`MintPlayer.Spark.E2E.Tests/Security/_SecurityTestHelpers.cs` currently wraps Playwright's `APIRequestContext` with a custom CSRF handler. After §3.1 lands, `SparkApi` becomes a thin adapter — or disappears entirely.

**Decision**: delete `SparkApi`. Replace with `SparkClient` constructed from an `HttpClient` whose handler accepts the Fleet self-signed cert. The change touches:

- `_SecurityTestHelpers.cs` — delete `SparkApi`. Add a `SparkClientFactory.ForFleet(FleetTestHost)` helper that returns a ready-to-use `SparkClient` with certificate validation disabled.
- Every `Security/*.cs` e2e test — replace `var api = await SparkApi.LoginAsync(...)` with `var client = await SparkClientFactory.ForFleet(_fixture.Host); await client.LoginAsync(...)`. Bodies that used `api.PostJsonAsync("/spark/po/Car", new {...})` become `client.CreatePersistentObjectAsync(new PersistentObject {...})`.

**Playwright interaction**: Playwright's `APIRequestContext` is retained **only** for browser-session tests that must observe cookies through the browser (XSRF cookie flag tests, return-URL validation). HTTP-only tests use `SparkClient`. Where both need to coexist (a test that logs in via UI and then makes an API call), the `SparkClient` accepts a pre-populated cookie header via a new `SparkClient.WithCookies(string)` constructor hook — aligns with the existing `(HttpClient, ownsClient)` overload.

### 3.3 Retrofit the older endpoint tests

18 call sites across 7 test classes (enumerated in the security-audit PR's grep output — `UpdateEndpointTests`, `GetEndpointTests`, `CreateEndpointTests`, `DeleteEndpointTests`, `ListEndpointTests`, `AntiforgerySecurityTests`, `ListQueriesEndpointTests`, `GetQueryEndpointTests`, `ExecuteQueryEndpointTests`).

Each currently does:

```csharp
_factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
_client = await _factory.CreateAuthorizedClientAsync();  // returns SparkTestClient
// ...
var response = await _client.PutJsonAsync($"/spark/po/{PersonTypeId}/people%2F1", UpdatePersonRequest("A", "B"));
```

After retrofit:

```csharp
_factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
_client = new SparkClient(_factory.CreateClient(), ownsClient: true);
// ...
po.SetAttribute("FirstName", "A");
var saved = await _client.UpdatePersistentObjectAsync(po);
```

The `UpdatePersonRequest` / `CreatePersonRequest` static helpers inside each test class go away.

**Migration strategy**: one test class per commit, with a final commit to delete `SparkTestClient` + `SparkEndpointFactoryExtensions.CreateAuthorizedClientAsync` from `MintPlayer.Spark.Testing`. Mechanical; reviewable.

**Tests that can't fully retrofit**: `AntiforgerySecurityTests` asserts on raw 400/403 responses when the CSRF header is **missing** or **wrong** — `SparkClient`'s purpose is to *send* the header, so those specific assertions still want raw `HttpClient`. Keep the raw-`HttpClient` path for those 3-4 facts; everything else moves over.

### 3.4 Dedicated `MintPlayer.Spark.Client.Tests`

New test project `MintPlayer.Spark.Client.Tests/` — net10.0, xunit, references `MintPlayer.Spark.Client` and `MintPlayer.Spark.Testing` (for `SparkEndpointFactory` to spin up a TestServer-backed client).

Coverage targets (branches not exercised by the endpoint tests):

| Branch | Test |
|---|---|
| `new SparkClient(baseUrl)` constructor | Constructs, disposes internal HttpClient on `Dispose()`. |
| `new SparkClient(httpClient, ownsClient: false)` | `Dispose()` does **not** dispose the externally-owned client. |
| Warmup success | First mutating call triggers exactly one GET `/spark/po/__warmup__`; subsequent mutating calls don't repeat it. |
| Warmup failure: no Set-Cookie | Throws `SparkClientException` with the "not a Spark backend" message. |
| Warmup failure: missing antiforgery or XSRF cookie | Throws `SparkClientException` with the "did not yield both antiforgery cookies" message. |
| Delete happy path | `DeletePersistentObjectAsync` emits `DELETE` with the X-XSRF-TOKEN header and succeeds. |
| Delete 404 | Throws `SparkClientException` with `StatusCode == NotFound`. |
| `ExecuteQueryAsync(Guid, ...)` vs `ExecuteQueryAsync(string alias, ...)` | Both reach the same endpoint; alias variant URL-encodes its input. |
| Query with all optional parameters (search, skip, take, parentId, parentType) | All surface as query-string params in the right order. |
| `SparkActionResult` decoding (once §3.1 lands) | Happy PO response, retry-action response, empty-200 response. |
| `LoginAsync` side effect (once §3.1 lands) | After login, subsequent GETs carry the auth cookie. |

Uses `SparkEndpointFactory<TestSparkContext>` plus a dedicated `MintPlayer.Spark.Client.Tests._Infrastructure.TestSparkContext` — **does not** reference `MintPlayer.Spark.Tests` to avoid cross-project coupling.

CI: picked up automatically by `nx affected --target=test` (the `@nx-dotnet/core` plugin auto-generates a test target for any .csproj with a test SDK reference).

Coverage: **remove `MintPlayer.Spark.Client/**` from any future `codecov.yml ignore` list** if added — this project makes Client a first-class citizen with its own coverage number.

---

## 4. Implementation Plan

Each phase ships as its own PR against `master`. Each is green in isolation.

### Phase 1 — Surface expansion

1. Add `LoginAsync`, `LogoutAsync`, `RegisterAsync` to `SparkClient`. These are auth operations, so they tolerate a pre-warmup cookie state — adjust `EnsureAntiforgeryAsync` to skip if the caller is mid-auth (detected by flag set inside the auth methods).
2. Add `ExecuteActionAsync` returning `SparkActionResult`. Create `SparkActionResult.cs` + `RetryActionOption.cs` in `MintPlayer.Spark.Client/`.
3. Add `GetPersistentObjectByAliasAsync` string-alias overload.
4. Extend `SparkClient.Tests` with facts covering the new methods as part of the same PR.

**Gate**: new tests green; no regression in the existing two retrofitted tests.

### Phase 2 — Retrofit e2e `SparkApi` → `SparkClient`

1. Add `SparkClientFactory.ForFleet(FleetTestHost)` to `MintPlayer.Spark.E2E.Tests/_Infrastructure/`.
2. Rewrite each `Security/*.cs` test to use `SparkClient`. Twelve files; commit one per file for reviewability.
3. Delete `_SecurityTestHelpers.cs`'s `SparkApi` class once every caller migrates.
4. Keep Playwright's `APIRequestContext` for the 3 tests that genuinely need browser-session cookie observation.

**Gate**: full e2e suite (29 tests) green locally and in CI.

### Phase 3 — Dedicated `MintPlayer.Spark.Client.Tests`

1. Create `MintPlayer.Spark.Client.Tests/` project + add to `MintPlayer.Spark.sln`.
2. Write the test matrix from §3.4.
3. Verify codecov picks the project up; coverage for `MintPlayer.Spark.Client` rises above whatever baseline Phase 1's endpoint tests establish.

**Gate**: project-level coverage for `MintPlayer.Spark.Client/**` ≥ 80%.

### Phase 4 — Retrofit older endpoint tests

1. One commit per `MintPlayer.Spark.Tests/Endpoints/*Tests.cs` class; mechanical swap from raw HTTP + static-helper-JSON to `SparkClient` calls.
2. After the last class migrates, delete `SparkTestClient.cs` and the `CreateAuthorizedClientAsync` extension from `MintPlayer.Spark.Testing/`.
3. `AntiforgerySecurityTests` keeps its raw-HttpClient facts for the missing-header / wrong-header negative cases.

**Gate**: `MintPlayer.Spark.Tests/_Infrastructure/` contains no leftover helper code replaced by `SparkClient`.

---

## 5. Risks + Open Questions

| Risk | Mitigation |
|---|---|
| Adding `LoginAsync` means `SparkClient` now owns an auth-cookie lifecycle, not just CSRF. Subtle bug: if the same client instance is reused across logins, the old auth cookie leaks. | Clear the session cookie inside `LoginAsync` before issuing the new request. Add a test for "login → logout → login as different user → GET reflects new identity." |
| The e2e retrofit changes what Playwright-driven tests observe about cookies. Some Security/*.cs tests explicitly assert on `Secure` and `SameSite` cookie flags through the browser. | Keep Playwright for those tests (§3.2 last bullet). `SparkClient` replaces the **API-only** flow; browser-session assertions stay on `APIRequestContext`. |
| `ExecuteActionAsync` return shape is heterogenous (PO vs retry-action vs empty-200). A naïve `PersistentObject` return type collapses the retry case. | `SparkActionResult` wrapper (§3.1.1) preserves all three shapes. |
| CSRF warmup happens lazily on first mutating call — an uncaught exception in warmup surfaces inside the first mutating call, making it look like the mutating operation itself failed. | Already thrown as `SparkClientException` with a message that names "Warmup" — tests can distinguish by substring match, and in practice the message ("did not yield both antiforgery cookies") makes the cause clear. No change needed. |
| Tests that currently use raw `HttpClient` to assert on exact response body structure (e.g. `body.Should().Contain("\"etag\":\"...\"")`) can't migrate to `SparkClient` without losing that assertion. | Accept: those assertions belong on the raw HTTP path. Typed-PO assertions (`po.Etag.Should().NotBeNullOrEmpty()`) are the migration target; raw-body string-matching stays where it lives. |
| Missing server-side endpoint for streams means `GetStreamAsync` would be a stub. | Deferred (see §3.1 table). Add only when a caller needs it and the server exposes it. |

---

## 6. Acceptance Criteria

### Phase 1 (Surface expansion)
- [ ] `SparkClient.LoginAsync`, `LogoutAsync`, `RegisterAsync`, `ExecuteActionAsync`, `GetPersistentObjectByAliasAsync` implemented and covered by unit tests.
- [ ] `SparkActionResult` distinguishes PO response, retry-action response, and empty-200 response.
- [ ] `SparkClient` is `public class` (unsealed).

### Phase 2 (E2E retrofit)
- [ ] `SparkApi` class in `_SecurityTestHelpers.cs` is removed.
- [ ] Every `Security/*.cs` e2e test passes through `SparkClient` for HTTP calls.
- [ ] Browser-session cookie-flag assertions still run via Playwright's `APIRequestContext`.
- [ ] Full e2e suite: 29 pass / 0 fail / 0 skip, identical to pre-retrofit baseline.

### Phase 3 (Dedicated test project)
- [ ] `MintPlayer.Spark.Client.Tests/MintPlayer.Spark.Client.Tests.csproj` exists, added to solution.
- [ ] Branch coverage for `MintPlayer.Spark.Client/**` ≥ 80% (measured by coverlet + codecov).
- [ ] Every entry in §3.4's table has at least one `[Fact]`.

### Phase 4 (Old endpoint tests retrofit)
- [ ] All 7 older endpoint test classes use `SparkClient` (except the 3-4 `AntiforgerySecurityTests` facts that deliberately send wrong/missing CSRF).
- [ ] `MintPlayer.Spark.Testing/SparkTestClient.cs` is deleted.
- [ ] `SparkEndpointFactoryExtensions.CreateAuthorizedClientAsync` is deleted.
- [ ] All 290 current tests still pass; no tests added purely as replacements.

### Cross-cutting
- [ ] No breaking changes to `SparkClient`'s public API from v0.1 (the shipped version).
- [ ] `codecov/patch` passes on every phase PR (current target 40%).
- [ ] `codecov/project` passes on every phase PR (current threshold 2%).

---

## 7. References

- **Current `SparkClient`**: `MintPlayer.Spark.Client/SparkClient.cs` — shipped at PR #123.
- **Current `SparkApi`**: `MintPlayer.Spark.E2E.Tests/Security/_SecurityTestHelpers.cs`.
- **Vidyano.Core inspiration**: `C:\Repos\Vidyano.Core\Vidyano.Core\Client.cs` — the public method signatures (`SignInUsingCredentialsAsync`, `GetPersistentObjectAsync`, `ExecuteActionAsync`) inform naming and ergonomics here, but server surface differs.
- **Related PRDs**: `docs/PRD-SecurityAudit.md` (owns the remaining security test-matrix items — H-4, M-2, M-4/L-4), `docs/PRD-Testing.md` (canonical shape of the test-helper story).
- **Endpoint definitions** (to verify shape compatibility):
  - `MintPlayer.Spark/Endpoints/PersistentObject/{Get,Create,Update,Delete}.cs`
  - `MintPlayer.Spark/Endpoints/Queries/{List,Get,Execute}.cs`
  - `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs`
  - `MintPlayer.Spark.Authorization` identity endpoints (login/logout/register) — `/spark/auth/*`

---

## 8. Out of Scope

Tracked elsewhere; listed here so they aren't accidentally added to this PRD's scope:

- **H-4 fail-closed host** — `docs/PRD-SecurityAudit.md` §8, test plan. Blocked on the remediation itself, not on `SparkClient` surface.
- **M-2 token tampering** — blocked on the identity-provider work (`docs/PRD-IdentityProvider.md`), which exposes the tokens that can be tampered with.
- **M-4 / L-4 marker attributes** — `docs/PRD-SecurityAudit.md` §8.
- **Vidyano.Core full API parity** — the server would need to grow many of those endpoints (action hooks, registered streams, service-provider auth flows) before client-side surface has anything to bind to. Out of scope here; tracked as a future PRD if/when demand surfaces.
- **Caching / retry / circuit-breaker inside `SparkClient`** — interesting operational hardening; separate concern.
