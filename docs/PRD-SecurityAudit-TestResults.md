# Security Audit — Test Run Status & Final Report

**Companion to:** [PRD-SecurityAudit.md](PRD-SecurityAudit.md)
**Branch:** `feat/security-audit`
**PR:** https://github.com/MintPlayer/MintPlayer.Spark/pull/123
**Final state:** `0d466d9`

## Summary

| Metric | Baseline (pre-fixes) | Final |
|--------|----------------------|-------|
| Total tests | 28 | 29 |
| Passed | 7 | **29** |
| Failed | 18 | **0** |
| Skipped | 3 | **0** |

Every originally-failing and every originally-skipped test is green. The 29th
test is `ReturnUrlValidationTests.Diagnostic_admin_can_log_in_via_the_form`,
added mid-audit to catch test-infrastructure regressions before they produce
mystery failures across six returnUrl tests.

## Commits on `feat/security-audit`

| Sha | Summary |
|-----|---------|
| `ea368af` | audit PRD + 25 e2e tests pinning secure behaviour |
| `d3a692f` | record clean baseline matrix (TRX-parsed) |
| `9e9fdf4` | **fix(H-1)** filter metadata endpoints by caller's Query rights |
| `b0aed65` | **test fix** add required `PersistentObject.Name`; expose `FleetTestHost.RecentLog` |
| `2175c85` | **test fix(H-5)** assert URL host not substring; add diagnostic login smoke |
| `a08f9e1` | pickup notes (superseded by this doc) |
| `c457e0e` | **fix(L-2)** Secure flag on XSRF-TOKEN + SameSite case-insensitive assertion |
| `b819836` | **fix(M-5)** allow-list sort columns against declared schema |
| `c781bef` | **fix(L-3)** opt-in rate limiter on `ISparkBuilder` + Fleet opt-in |
| `8d21b98` | **fix(M-7)** optimistic concurrency via `PersistentObject.Etag` |
| `c206740` | **test fix** RateLimitTests waits out its 10 s window to avoid bucket bleed |
| `5e29a1d` | ci: bump webhooks-demo-deploy to `checkout@v5` + `setup-dotnet@v5` |
| `0d466d9` | **fix(H-2)(H-3)** row-level auth hook on `DefaultPersistentObjectActions` |

## Per-finding outcome

### Fixed in-framework

| Finding | Remediation summary |
|---------|---------------------|
| **H-1** metadata leak | Five metadata endpoints inject `IPermissionService` and filter responses by `IsAllowedAsync("Query", entityName)`. Single-entity Get returns 404 (not 403) on denial to avoid existence oracle (M-3). |
| **H-2** broken object-level authz | Virtual `IsAllowedAsync(action, T entity)` on `DefaultPersistentObjectActions<T>`; `DatabaseAccess` calls it on single Get and list Get (projection path loads base entity through session cache). |
| **H-3** parent-fetch IDOR | Execute.cs's parent fetch goes through `DatabaseAccess.GetPersistentObjectAsync`, which enforces the row gate. If parent is requested but resolves to null (missing or forbidden), endpoint returns 404 rather than running the query unscoped. |
| **L-2** no `Secure` on XSRF-TOKEN | `SparkMiddleware` sets `Secure = context.Request.IsHttps`. HttpOnly stays false (by design for double-submit). |
| **M-5** sort-column reflection | `Execute.cs` intersects requested sort columns with entity-type attributes + query's declared sort columns; mismatches return 400. |
| **M-7** lost-update races | New `PersistentObject.Etag` (string) surfaces Raven's change vector on Get; `SavePersistentObjectAsync` validates against current vector via a side session. Mismatch → `SparkConcurrencyException` → 409. Opt-in per request (null etag == last-write-wins, keeps existing clients). |

### Fixed demo-side

| Finding | Remediation summary |
|---------|---------------------|
| **L-3** no rate limiter | New `SparkBuilderRateLimiterExtensions.AddRateLimiter(ISparkBuilder, ...)` + `SparkRateLimiterOptions`; `SparkFullOptions.RateLimiter` threads through the AllFeatures source generator. Fleet opts in via `options.RateLimiter = _ => { }`. Scoped to `/spark/` only so static assets stay unthrottled. |

### Already secure on master (tests document current defence)

- **M-1** `/spark/permissions/{type}` correctly denies anon mutations
- **M-3** 404/403 unified in the tested path
- **M-5 malformed sort direction** falls through to default (no 500)
- **M-6** error responses already sanitised (no stack traces, no Raven internals in body)
- **H-5** Angular router implicitly rejects external/javascript/protocol-relative URLs in `navigateByUrl`. The UX side-effect (user stuck on `/login?returnUrl=…` after rejected redirect) is documented but not a security issue.
- **L-7b** unknown attributes are silently dropped by the entity mapper

### False positives in the initial baseline

- **L-2 SameSite** — master emits `samesite=strict` (lowercase); assertion was strict-case. Fixed with `BeEquivalentTo`.
- **L-7a** — test couldn't observe the `IsReadOnly` contract because Fleet's Car schema had no `IsReadOnly=true` attribute. The H-2 PR introduces `CreatedBy` (`IsReadOnly=true`, `IsVisible=false`), so L-7a now has a real field to exercise.
- **H-5** × 5 — substring assertions matched `attacker.test` in the preserved returnUrl query string when the router refused the nav. Fixed with URL-host comparison.
- **L-7a/L-7b/M-7 500s** — admin-POST body was missing the required `PersistentObject.Name` field. Fixed test-side.

## Deferred (documented, not in scope for this PR)

- **H-4** fail-closed-without-authz — needs a separate host fixture that omits `AddAuthorization()`; not e2e-testable with the current single-Fleet collection fixture. Framework already contains the shape (permission service is a scoped singleton); what's missing is the start-up assertion that fails the app if authorization is expected but not wired.
- **M-2** JWT tampering — Fleet is cookie-based. Revisit when the IdentityProvider lands on master and bearer tokens become a real surface.
- **M-4 / L-4** marker attributes for custom queries / custom actions — requires a design decision on the attribute name (`[SparkQuery]` / `[ExposedAsAction]` vs. a registry) and a fix-side demo fixture to exercise "unmarked method is not reachable".

## Testing patterns established

- **`MintPlayer.Spark.E2E.Tests/Security/_SecurityTestHelpers.cs`** — `SparkApi` wrapper that auto-injects `X-XSRF-TOKEN` on mutating calls, so tests don't manually plumb antiforgery.
- **`FleetTestHost.RecentLog(maxLines)`** — surfaces the tail of the Fleet subprocess log in assertion failure messages, so production-500s (empty body) can still be diagnosed.
- **`FleetTestHost.SeedUserAsync(email, password, group)`** — registers an additional non-admin account; email-confirms it and patches the group claim directly in Raven. Used by row-level-authz tests that need a second identity.
- **Diagnostic smoke test** (`ReturnUrlValidationTests.Diagnostic_admin_can_log_in_via_the_form`) — pins that form-based login actually completes, so a broken selector can't quietly poison every downstream assertion.

## How to resume

```bash
git checkout feat/security-audit
git pull

# full suite
dotnet test MintPlayer.Spark.E2E.Tests --filter "FullyQualifiedName~Security" \
  --logger "trx;LogFileName=full.trx" --results-directory MintPlayer.Spark.E2E.Tests/TestResults

# single class
dotnet test MintPlayer.Spark.E2E.Tests --filter "FullyQualifiedName~<ClassName>" \
  --no-build
```

Fleet boots once per collection (~30–60 s). Full 29-test suite ≈ 50 s after boot. **Requires** a RavenDB license (env `RAVENDB_LICENSE` or `raven-license.log` at the repo root) and, on first run, the Angular bundle under `Demo/Fleet/Fleet/ClientApp/dist/` (the fixture rebuilds automatically if the dir is missing).
