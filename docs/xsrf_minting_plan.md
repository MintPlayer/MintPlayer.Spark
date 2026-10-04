# Plan — Spark's CSRF protection

PRD: [`xsrf_minting_PRD.md`](xsrf_minting_PRD.md). Branch: `fix/api-controller-antiforgery`.

⚠️ Read PRD §0 first. The previous version of both documents asserted several things about ASP.NET
Core and this codebase that were not checked and turned out to be wrong, and two of them had been
copied into production source comments. They are corrected in that table; the milestones below are
what survives.

Status: **M0 done** (coverage — the part that closes a real hole). ~~**M1–M5 not started** (minting
placement — ergonomics, one round trip).~~

**Status 2026-10-04 (second pass): M1–M5 done through #452 / PR #485** (branch
`feat/452-adopt-xsrf-package`). Spark's own mint was deleted and replaced by `UseAntiforgeryGenerator()`
from `MintPlayer.AspNetCore.SpaServices.Xsrf` `11.0.0-rc.3`. The package does everything M1 lists
(mints in `OnStarting`, same flags, null guard, try/catch, snapshot and restore of the cache headers),
plus `[SkipXsrfToken]`, which the coverage badge uses.

| | State | Evidence |
|---|---|---|
| M1 position | ✅ The generator is registered **above** `UseSparkAntiforgery()`, so the gate's 400 carries a fresh cookie (PRD §5, reason 2). | **Red → green.** `XsrfEnforcementTests.The_gates_refusal_carries_a_fresh_token_so_signing_in_again_after_sign_out_recovers` failed with the generator in its old position, below the gate: 1 failed, 8 passed, at "Did not expect session.XsrfToken to be …". It passes with the move. The unit twin is `XsrfMintingPlacementTests.The_gates_refusal_carries_a_fresh_token_that_works_on_retry`. |
| M1 reason 1 (sign-in) | ✅ | `XsrfMintingPlacementTests.Token_minted_on_the_sign_in_response_is_usable_immediately`, plus the Fleet case under M2. |
| M1 reason 3 (survives `UseExceptionHandler`) | ✅ Now measured. | `XsrfMintingPlacementTests.A_response_rewritten_by_the_exception_handler_still_carries_the_cookie`. |
| M2 | ✅ `XsrfEnforcementTests.A_mutating_call_right_after_sign_in_succeeds_without_csrf_refresh` (real Fleet host, real HTTPS, real cookie jar). | The `BeforeHandler` arm is **not** restored. Claude's decision while implementing the remaining work (2026-10-04); the owner did not rule on it. The code it measured no longer exists, so a copy kept only for the A/B would test nothing in the product. The measurement it produced (sign-in: eager 400, `OnStarting` 200) stays in the class remarks. |
| M3 | ✅ `externalFlow` calls `csrfRefresh()` before `checkAuth()`. A failing refresh is swallowed, because a rejection there would leave the popup promise unresolved. `ng-spark-auth` → `22.18.0`. | `spark-auth.external-login.spec.ts`: the success case now expects `csrf-refresh` before `/me`, plus a new case where the refresh fails and the sign-in still resolves. |
| M4 | ✅ Browser run on HR (`dotnet run --launch-profile https`, `playwright_node` MCP, `fetch` from the page with the browser's own cookie jar). | Register 200 → login 200 (token changed) → **logout right after login, no refresh: 200** → login with the token from the sign-out response: **400, and the 400 changed the token** → retry 200 → `/me` authenticated. |
| M5 | ✅ `MintPlayer.Spark` → `11.0.0-preview.96`, `@mintplayer/ng-spark-auth` → `22.18.0`. | Release notes: `docs/release-notes-preview-96.md`. |

**MintPlayer.Assertions migration (owner request, same PR):** all 568 xUnit `Assert.*` calls in 83
files are converted, by a script that parses each call's arguments and maps them in order
(`Equal(e, a)` → `a.Should().Be(e)`, collection literal → `.Equal`, `ThrowsAsync` → `ThrowExactlyAsync`
because xUnit's is exact-type, `StringComparison.OrdinalIgnoreCase` → `…EquivalentOf`, `Single`/`IsType`
→ `.Which` where the value is used). The compiler caught the rest: 24 nullable value types
(`BeNull` → `NotHaveValue`), 6 ordered collection comparisons (`.Be` → `.Equal`), and one
expression-bodied `Single`. Seven call sites were converted by hand.

`Microsoft.CodeAnalysis.BannedApiAnalyzers` bans `T:Xunit.Assert` as an **error** (RS0030) in every
`*.Tests` project (`Directory.Build.targets`, `BannedSymbols.Tests.txt`). ⚠️ The first version keyed on
`IsTestProject` and **did nothing**: restore does not import package props, so the analyzer was never
restored, and a probe `Xunit.Assert.True(true)` built clean. Keyed on the project name, the same probe
fails with RS0030.

**Found along the way, fixed in the same PR:**
- **32 assertions that could never fail on null.** In `x?.Value.Should().Be(…)`, the `?.` skips the
  whole chain, assertion included. 24 were already in the suite and 8 came from the conversion. Their
  receivers are now parenthesised. Found by reading the diff of `ApiTokenRepositoryIdListMigrationTests`.
- **Migrations that throw "Cannot perform bulk operation. Index is stale."** RavenDB refuses a
  patch or delete by query on a stale index unless `QueryOperationOptions` allows a wait, and under
  load the auto-index behind the query can still be catching up. All 18 sites (CodeCoverage, HR,
  `MintPlayer.Spark.Authorization` → `11.0.0-preview.96`) now pass `StaleTimeout = 5 min`.
  **Explained from the exception, not reproduced:** sweep 2's only failure was
  `ApiTokenRepositoryIdListMigrationTests.A_token_without_the_legacy_field_is_left_alone`, after 49 s.

- **`MessageQueueRouter.DisposeAsync` raced with itself.** PR #485 CI run `37220579446` failed
  `RegistrationInventoryTests.The_forge_services_are_registered` with a `NullReferenceException` at
  `lifetime.Dispose()` during host shutdown. The router is disposed by both `StopAsync` and the
  container, and the null check was separated from the dispose by an `await`. Red → green:
  `MessageQueueRouterTests.Concurrent_disposal_is_safe` threw the same exception before the
  `Interlocked.Exchange` fix. `MintPlayer.Spark.Messaging` → `11.0.0-preview.96`.

**Local verification:**
- **Sweep 2** (`npm run test:affected`, Developer licence, 8m40s), run before the two fixes above.
  Green: MintPlayer.Spark.Tests, E2E, Client, SourceGenerators, `ng-spark-auth` and the CodeCoverage
  client. CodeCoverage.Tests: 1090/1091, the stale-index failure.
- **Sweep 3**, after the fixes, was stopped at the owner's request (2026-10-04, "takes too long").
  Everything compiled. CodeCoverage.Tests finished at 1090/1091, and the failure was the stale-timeout
  fix itself: `ForgeQualifiedDocumentIdsMigrationTests.A_count_that_cannot_catch_up_refuses_rather_than_guesses`
  sets the migration's own `IndexCatchUpBudget` to zero and expects a refusal, and a flat 5 minutes
  overrode it. That migration now uses `StaleTimeout = IndexCatchUpBudget`. Both migration test classes
  are green (7/7). The other projects did not finish in sweep 3, so for the parenthesised assertions
  outside CodeCoverage, **CI is the first run**. A newly failing wrapped assertion means its value
  really was null: investigate it rather than revert the wrap.

<details><summary>Superseded: the first-pass status (commit eb021a7e)</summary>

**Status 2026-10-04: M1 is in progress through #452 / PR #485** (branch `feat/452-adopt-xsrf-package`,
commit `f8dd2fc1`). Spark's own mint was deleted and replaced by `UseAntiforgeryGenerator()` from
`MintPlayer.AspNetCore.SpaServices.Xsrf` `11.0.0-rc.3`. The package already does everything M1 lists
(mints in `OnStarting`, same flags, null guard, try/catch, snapshot and restore of the cache headers),
plus `[SkipXsrfToken]`, which the coverage badge uses. What is still open:

| | State | Evidence / next step |
|---|---|---|
| M1 position | ⚠️ **Open decision.** PR #485 kept the old position, *after* `UseSparkAntiforgery()`, as #452 instructed. M1 below says *above* it, so the gate's own 400 carries a fresh cookie (PRD §5, reason 2). In the current position that reason is **not delivered**: the gate returns 400 without calling `next`, so the generator never registers its callback. | Move `app.UseAntiforgeryGenerator()` above `app.UseSparkAntiforgery()` in `SparkMiddleware.cs`, and add a test: a stale token gets a 400 that carries a new `XSRF-TOKEN`. |
| M1 reason 1 (sign-in) | ✅ | `XsrfMintingPlacementTests.Token_minted_on_the_sign_in_response_is_usable_immediately` drives the real package. |
| M1 reason 3 (survives `UseExceptionHandler`) | Expected, **not measured.** `Response.Clear()` does not remove `OnStarting` callbacks. | Add a test, or measure it. |
| M2 | ❌ Not done: no `XsrfEnforcementTests` case on the real Fleet host. ⚠️ PR #485 also **removed** the `BeforeHandler` arm from `XsrfMintingPlacementTests`, against the instruction in M2 below. The measurement is kept in the class remarks. | Decide whether to restore the arm. Add the Fleet case either way. |
| M3 | ❌ Not done | `spark-auth.service.ts` `externalFlow` |
| M4 | ❌ Not done | Browser run via the `playwright_node` MCP |
| M5 | ❌ **CI is red on it.** The `Verify a changed package was version-bumped` gate failed on run `37215922818`, so no tests ran. Release notes are not written either. | Bump `libs/**` `<Version>`s following the previous bump (`git log -S"preview." -- 'libs/**/*.csproj'`), then write release notes. |

Local verification so far: the Spark library builds against rc.3. CodeCoverage.Tests had 1090/1091
passing; the failure was a bug in the new `BadgeHttpTests` (string comparison of `Cache-Control`), now
fixed in `f8dd2fc1`, 3/3 green. MintPlayer.Spark.Tests, E2E and Client tests have **not
run**: the local sweep was interrupted, and CI stopped at the version gate.

**Also pending, same PR (owner proposal 2026-10-04):** convert the remaining 568 xUnit `Assert.*` calls
in 83 files to MintPlayer.Assertions. By project: CodeCoverage.Tests 468, Spark.Tests 76, Client 13,
E2E 11. Add `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `T:Xunit.Assert` so new ones cannot appear.
FluentAssertions is already gone; every test project references `MintPlayer.Assertions` `11.0.0-rc.5`.
Traps: `Assert.Equal(expected, actual)` argument order; collection `Assert.Equal` is ordered, so it maps
to `.Equal`, not `BeEquivalentTo`; `Assert.Contains(sub, str)` order; `Single`/`IsType`/`ThrowsAsync`
map to `.Which`; precision `Equal` maps to `BeCloseTo`. Verify with identical per-test results before
and after. Awaiting the owner's go.

</details>

---

## M0 — Coverage: every endpoint states its position ✅

The work that came out of measuring rather than assuming. Detail in PRD §2–§3.

| Change | File |
|---|---|
| `RequireAntiforgery` now defaults to `true` — the documented "next major" flip, and the only thing that protects endpoints an *application* maps | `libs/spark/MintPlayer.Spark/Extensions/SparkAntiforgeryOptions.cs` |
| ⚠️ `POST /spark/auth/login` gated — login CSRF; it is anonymous, so only explicit metadata can ever reach it | `libs/authorization/.../Extensions/LocalCredentialEndpointFilter.cs` |
| ⚠️ `POST /spark/auth/csrf-refresh` explicitly exempt — mandatory, see below | `libs/authorization/.../Endpoints/CsrfRefresh.cs` |
| ⚠️ Replication stamps **removed** — they fired before authentication, so an anonymous caller got a bare 400 instead of 401/403, breaking cross-module replication (PRD §3.3) | `libs/replication/.../Endpoints/{SyncApply,EtlDeploy}.cs` |
| `SparkClient` primes and sends a token on login and on the two reads that are POSTs only because they need a body | `libs/client/MintPlayer.Spark.Client{,.Authorization}/` |
| `/spark/github/dev-ws` constrained to GET — a bare `Map()` matched every mutating verb | `libs/webhooks/.../Extensions/SparkBuilderExtensions.cs` |
| CodeCoverage's three CI uploads explicitly exempt | `apps/CodeCoverage/CodeCoverage/Controllers/{Uploads,ForkUploads}Controller.cs` |
| Three false comments corrected in place | `SparkMiddleware.cs`, `SparkAntiforgeryMiddleware.cs`, `SparkAntiforgeryOptions.cs` |
| E2E sign-in helpers prime a token (five call sites) | `tests/MintPlayer.Spark.E2E.Tests/**` |

Tests added: `XsrfSurfaceTests` (metadata inventory of every mutating framework endpoint, exact
sets), `XsrfEnforcementTests` (behavioural, real HTTPS, control beside every case),
`Replication_endpoints_do_not_require_an_antiforgery_token`, and `CsrfSurfaceTests` reworked to three
positions.

**Verified:** unit 2491/2491, CodeCoverage 773/773, E2E 105/105. Shipped as PR #451.

⚠️ **The one thing not to "tidy up" later.** `csrf-refresh` must never require a token. A token is
bound to a principal; a client whose identity just changed holds a stale one by definition; demanding
a valid token before issuing a valid token is a deadlock the client cannot escape for the rest of the
session. `XsrfEnforcementTests.A_token_minted_before_sign_in_no_longer_works_after_it` exists to make
that concrete rather than merely asserted.

---

## M1 — Move the mint into `Response.OnStarting`

`libs/spark/MintPlayer.Spark/SparkMiddleware.cs:338-358`. Replace the eager append with a callback
registered in the same place, doing the same work.

- Register the callback **above** `UseSparkAntiforgery()` (`:301`) so the gate's own 400 carries a
  fresh cookie (PRD §5, reason 2). Note LIFO: registering earlier means running *later*.
- Keep every flag and the null guard (PRD §4).
- Wrap `GetAndStoreTokens` + append in `try/catch`. ⚠️ A throw here is **swallowed by Kestrel**, not
  raised: it skips every callback registered before it and produces a bare 500 with all headers
  reset, and `UseExceptionHandler` never sees it. Degrade to "no cookie on this response" instead.
- Snapshot and restore `Cache-Control` / `Pragma` around the call (D5). `GetAndStoreTokens` stamps
  them whenever the response has not started — and inside `OnStarting` it has not.

### M2 — Prove it in the real pipeline

`XsrfMintingPlacementTests` builds its **own** pipeline, so it will keep passing whatever M1 does —
it proves the principle, not the product. Add a case to `XsrfEnforcementTests` (real Fleet host)
asserting a protected call immediately after sign-in succeeds with no `csrf-refresh` between.

⚠️ Do not extend the A/B file for this. Its `BeforeHandler` case documents the behaviour being
removed and should keep passing unchanged, as the record of why the move happened.

### M3 — External login stops relying on an accident

`libs/node_packages/ng-spark-auth/core/src/spark-auth.service.ts:184` — `externalFlow`'s success
branch calls `checkAuth()` and not `csrfRefresh()`. It works because `checkAuth()` is a GET whose
response passes the mint; add the explicit refresh so it stops depending on "we happen to mint on
every response". Check whether the existing specs pin the request sequence there.

### M4 — Verify in a browser

Build the Angular libs (nx handles the graph), launch an app, drive it with the `playwright_node`
MCP: sign out, F5, sign in, then call a protected endpoint immediately and confirm no 400/401 in the
network log. HR is the easiest target (local credentials, no GitHub secrets); Fleet is what the E2E
harness drives.

⚠️ This was recorded as "blocked on the MCP server" — it is **not blocked any more**, the server
connects. What stands in for it today is `XsrfEnforcementTests`, which drives the same sequence over
real HTTPS with a real cookie jar. The browser run is still worth doing for M1, because the thing M1
changes (*when* the cookie is written relative to the response) is exactly what a `fetch` from a real
page exercises and an `HttpClient` does not.

### M5 — Docs and versions ✅ (for M0; redo for M1-M3)

Done in PR #451: `docs/Spark-API-Specification.md`'s **"Affected endpoints"** line no longer lists
routes — it had drifted past the passkey, external-login and `/connect` surfaces and omitted `login`
— and now points at `XsrfSurfaceTests`, which cannot drift because it is executable.
`docs/release-notes-preview-87.md` covers the breaking default flip, the gated `/login`, the
replication fix and the test-driver change.

Seven packages moved (CI gates each one separately, and it caught a miss): `MintPlayer.Spark`
→ `.87`; `.Authorization` and `.Client` → `.86`; `.Client.Authorization`, `.Replication`, `.Testing`
and `.Webhooks.GitHub` → `.85`. ⚠️ `ng-spark-auth` only if M3 lands.

---

## Risks

| Risk | Handling |
|---|---|
| ⚠️ **A downstream 10.x app that maps its own cookie-authenticated POST starts getting 400s** | This is the intended effect of M0. `WarnOnly = true` is the migration path and *now actually logs* — it never could before. Must be stated plainly in the release notes. |
| Gating `/login` breaks a programmatic client | `SparkClient.LoginAsync` primes a token. A third-party client POSTing `/login` directly must now do a warmup GET first — API churn, acceptable while in preview, belongs in the release notes. |
| The mint stops running for some response shape | `SparkEndpointFactory.MintAntiforgeryAsync` and `SparkClient.EnsureAntiforgeryAsync` both throw when warmup yields no cookie, so whole suites fail loudly rather than subtly. |
| Cache headers regress | D5 restores them. |
| ⚠️ Someone later "optimises" the mint to run only when the cookie is absent | Breaks login permanently and silently — PRD §5 says why, and the comment goes in the code, not only the doc. |
| ⚠️ `KnownIdentityApiRoutes` was measured against **10.0.12** | The solution now targets net11.0. `GuardAgainstUnrecognizedIdentityRoutes` throws if Microsoft adds a route, which is the designed behaviour — but it means an Identity servicing update can fail startup. Worth knowing before a bump. |

## Out of scope

- Changing `MintPlayer.AspNetCore.SpaServices.Xsrf` (PRD §4) — if it is hardened later, Spark's flag
  set and the try/catch above are its shopping list. **Done 2026-10-04:** hardened upstream (#86,
  #89) and adopted by Spark in #452. See PRD §4.
- Removing the client's now-redundant `csrfRefresh()` calls after sign-in — 8 assertions across 2
  spec files and a version floor, for a cheap round trip.
- Turning `WarnOnly` off in CodeCoverage. Deliberately deferred: production should log its `/api`
  surface for one deploy first. Every mutating action there is annotated explicitly, so the flag
  decides nothing and flipping it later is safe.

## Found along the way

### F1 — `UseAntiforgery()` validates but never rejects

`AntiforgeryMiddleware.InvokeAwaited` records the failure on `IAntiforgeryValidationFeature` and
calls `_next` regardless. Rejection belongs to whoever binds the form — `RequestDelegateFactory` for
a minimal API, an MVC filter for a controller. An endpoint that binds no form has neither. This
invalidated three runs of the A/B before the controls caught it, and it is the real reason Spark
ships its own middleware — not the "narrowed in 8.0.1" story that was written here before.

### F2 — `UseSpark()` already makes every downstream response uncacheable

`GetAndStoreTokens` stamps `no-cache, no-store` on any response that has not started. True today,
independent of this change; bounded only because all four apps register static files upstream of
`UseSpark()`. An app that puts `UseOutputCache()` after it loses caching with no diagnostic.

### F3 — A rejection leaves the client wedged

`SparkAntiforgeryMiddleware` returns 400 without calling `next`, and the mint is registered after it,
so no fresh cookie is minted on a rejection. M1's placement fixes this as a side effect.

### F4 — `KnownIdentityApiRoutes` is a load-bearing inventory

`LocalCredentialEndpointFilter` classifies Microsoft's Identity routes **by name**, and `IsAllowed`
ends in `return true` — so a route Microsoft adds is published in every mode including `Disabled`.
The startup guard catches it, which is the right trade, but it makes an Identity version bump a
startup-affecting change.
