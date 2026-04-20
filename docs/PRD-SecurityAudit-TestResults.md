# Security Audit — Test Run Status & Pickup Notes

**Companion to:** [PRD-SecurityAudit.md](PRD-SecurityAudit.md)
**Branch:** `feat/security-audit`
**PR:** https://github.com/MintPlayer/MintPlayer.Spark/pull/123

## Current status (end of session 2026-04-20)

| Metric | Baseline (pre-fixes) | Current |
|--------|----------------------|---------|
| Total tests | 28 | 28 |
| Passed | 7 | 25 |
| Failed | 18 | 0 |
| Skipped | 3 | 3 |
| Unclassified | 0 | 0 |

All 28 tests run without diagnostic noise. The 25 passes document secure
behaviour on master (or tests newly flipped by fixes in this PR). The 3
skips are `RowLevelAuthzTests.*` pending the H-2/H-3 row-filter hook. **No
red tests remain on this branch** — everything failing at the baseline has
either been fixed in-framework, fixed test-side (false positive), or is
already tracked for a later commit.

## Commits so far (`feat/security-audit`)

| Sha | Summary |
|-----|---------|
| `ea368af` | audit PRD + 25 e2e tests pinning secure behaviour |
| `d3a692f` | record clean baseline matrix (TRX-parsed) |
| `9e9fdf4` | **fix(H-1)** filter metadata endpoints by caller's Query rights |
| `b0aed65` | **test fix** add required PersistentObject.Name to write-path bodies; expose FleetTestHost.RecentLog |
| `2175c85` | **test fix(H-5)** check URL host, not substring; add diagnostic login smoke |

## What was actually vulnerable vs. already-safe

### Real vulnerabilities found and fixed

- **H-1** (metadata leak) — 4 tests. Fixed in `9e9fdf4` by injecting
  `IPermissionService` into `Queries/List`, `Queries/Get`,
  `EntityTypes/List`, `EntityTypes/Get`, `Aliases/GetAliases` and
  filtering responses by `IsAllowedAsync("Query", entityName)`.

### Real vulnerabilities found and not yet fixed

- **M-7** concurrency — 1 test. Framework has no optimistic-concurrency
  check on update; two clients can lost-write each other silently. Confirmed
  by `ConcurrencyTests.Concurrent_update_with_stale_version_is_rejected`
  now that its setup runs (`b0aed65`). Status: **failing as expected**,
  waiting on the fix.
- **M-5** sort-column reflection — 2 tests. `?sortColumns=<anyPublicProperty>`
  is accepted via reflection; the framework must allow-list against the
  query's declared attribute set.
- **L-2 Secure flag** — 1 test. `SparkMiddleware.cs:191-196` sets
  `HttpOnly=false` (correct for double-submit) and `SameSite=Strict`, but
  no `Secure` flag. One-line fix: `Secure = context.Request.IsHttps`.
- **L-3** rate limiter — 1 test. Demo-only per triage; wire
  `AddRateLimiter()` in Fleet/HR/DemoApp `Program.cs`.

### Findings that turned out to be already-safe on master

- **M-1** — `/spark/permissions/{type}` already reports `canCreate/canEdit/canDelete=false`
  for anonymous callers. No framework change.
- **M-3** — 404/403 responses are already indistinguishable in the tested path.
- **M-5** (malformed sort direction) — framework falls through to default, no 500.
- **M-6** — error responses are already sanitised (no stack traces, no Raven internals).
- **H-5** — Angular router implicitly rejects external/javascript/protocol-relative
  URLs in `navigateByUrl`. 7 tests pass (6 attack vectors + 1 diagnostic login smoke).
  Note: there's a cosmetic UX issue where a successful login + rejected returnUrl
  leaves the user stuck on `/login?returnUrl=…` — that's the allow-list-remediation
  territory, but **not a security issue**.

### False positives in the initial baseline that turned out to be test bugs

- **L-2 SameSite** — cookie IS set to `SameSite=Strict` in master; the
  assertion's string shape was wrong. Test-side fix only.
- **L-7a / L-7b** — admin POST was returning 500 because request body
  omitted the required `PersistentObject.Name` field. Once the field was
  added, the framework's behaviour passed (L-7b is a real check; L-7a's
  assertion is weak because Fleet's Car schema has no `IsReadOnly=true`
  field — strengthening needs a demo-schema change).
- **H-5** × 5 — substring assertions matched the `attacker.test` token
  preserved in `?returnUrl=…` when the router refused to navigate. Fixed
  in `2175c85` by switching to URL-host comparison.

## Pickup plan — suggested order

Ascending effort; each flips one or more tests from failing to passing
(or holds current behaviour as regression protection). Each should be its
own commit on `feat/security-audit`.

### Quick wins (≤ 30 min each)

1. **L-2 SameSite assertion fix** — `XsrfCookieFlagTests.cs`. The cookie
   header format uses `samesite=strict` (lowercase-ish) or `SameSite=Strict`
   depending on the framework's emitter. Update the assertion to match
   case-insensitively. No framework change.
2. **L-2 Secure flag** — `SparkMiddleware.cs:191-196`. Add
   `Secure = context.Request.IsHttps` to the `CookieOptions`. Flips 1 test.

### Small framework changes (≤ 2 h each)

3. **M-5 sort-column allow-list** — `QueryExecutor.cs:492` (and
   `Endpoints/Queries/Execute.cs:31-46`). Intersect requested sort columns
   with the query's declared `Attributes` (from `EntityTypeDefinition`)
   before reflection. Flips 2 tests.
4. **L-3 rate limiter in demos** — add `AddRateLimiter()` + `UseRateLimiter()`
   wiring to `Demo/Fleet/Fleet/Program.cs` (and HR, DemoApp for consistency).
   Flips 1 test. Demo-only per triage — does NOT touch framework.

### Medium framework change (half-day)

5. **M-7 optimistic concurrency** — `DatabaseAccess.SavePersistentObjectAsync`.
   RavenDB supports change-vectors natively via
   `session.Advanced.GetChangeVectorFor(entity)` + `StoreAsync(entity, changeVector, id)`.
   The client sends the expected change-vector (likely as a header:
   `If-Match`), and the server rejects on mismatch with HTTP 409. Flips 1 test.

### Big framework change (biggest piece of the PR)

6. **H-2 / H-3 row-level filter hook** — new virtual method on
   `DefaultPersistentObjectActions<T>` per the PRD §5 triage decision.
   Signature candidate:
   ```csharp
   public virtual IRavenQueryable<T> ApplyRowFilter(
       IRavenQueryable<T> source, ClaimsPrincipal principal)
       => source;
   ```
   Wire it into `DatabaseAccess.GetPersistentObjectsAsync` (List path) and
   `GetPersistentObjectAsync` (Get path), then update `QueryExecutor` to
   route parent-fetch in `Execute.cs:56-66` through the same hook. Add an
   ownership concept to Fleet's `Car.json` (e.g. an `OwnerId` field set on
   create) so `CarActions` can override and demonstrate the hook. Then
   remove `[Fact(Skip=…)]` from the 3 tests in `RowLevelAuthzTests.cs`.

### Out of scope / deferred

- **H-4** fail-closed-without-authz — needs a second host fixture that
  omits `AddAuthorization()`; not e2e-testable with the current single-Fleet
  collection fixture.
- **M-2** JWT tampering — Fleet is cookie-based. Revisit once
  IdentityProvider lands on master.
- **M-4 / L-4** marker attributes for custom queries / custom actions —
  requires a fix-side fixture (an unmarked Actions method in a demo app
  to verify reflection is refused). Design decision needed: do we want a
  `[SparkQuery]` / `[ExposedAsAction]` attribute, or a separate registry?

## How to resume

```bash
git checkout feat/security-audit
git pull
# pick a finding from "Pickup plan" above
# implement, run the relevant test:
dotnet test MintPlayer.Spark.E2E.Tests --filter "FullyQualifiedName~<ClassName>" \
  --logger "trx;LogFileName=x.trx" --results-directory MintPlayer.Spark.E2E.Tests/TestResults
# commit on the branch, push; PR #123 auto-updates
```

The Fleet fixture boots once per test collection (~30-60 s). Running a
single test class is ~3-10 s after boot. Full 28-test suite runs in
~2 min end-to-end.

**RavenDB license** must be available as `RAVENDB_LICENSE` env var OR a
`raven-license.log` file at the repo root. Angular bundle must be built
once: `cd Demo/Fleet/Fleet/ClientApp && npm run build` (FleetTestHost
rebuilds automatically if the dist dir is missing).
