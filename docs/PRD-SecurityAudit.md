# PRD — Security Audit (master)

**Status:** Draft — awaiting triage
**Scope:** `master` branch, commit `ea596e9` (2026-04-20). Excludes `feat/authorization` and IdentityProvider work.
**Method:** 5 parallel Explore agents covering AuthN/AuthZ, injection/input-validation, endpoint surface, Angular frontend, and source-generators/supply-chain. Findings dedeped and sanity-checked against master HEAD before inclusion.

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
