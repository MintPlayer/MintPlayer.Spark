# Plan — Spark's CSRF protection

PRD: [`xsrf_minting_PRD.md`](xsrf_minting_PRD.md). Branch: `fix/api-controller-antiforgery`.

⚠️ Read PRD §0 first. The previous version of both documents asserted several things about ASP.NET
Core and this codebase that were not checked and turned out to be wrong, and two of them had been
copied into production source comments. They are corrected in that table; the milestones below are
what survives.

Status: **M0 done** (coverage — the part that closes a real hole). **M1–M5 not started** (minting
placement — ergonomics, one round trip).

---

## M0 — Coverage: every endpoint states its position ✅

The work that came out of measuring rather than assuming. Detail in PRD §2–§3.

| Change | File |
|---|---|
| `RequireAntiforgery` now defaults to `true` — the documented "next major" flip, and the only thing that protects endpoints an *application* maps | `libs/spark/MintPlayer.Spark/Extensions/SparkAntiforgeryOptions.cs` |
| ⚠️ `POST /spark/auth/login` gated — login CSRF; it is anonymous, so only explicit metadata can ever reach it | `libs/authorization/.../Extensions/LocalCredentialEndpointFilter.cs` |
| ⚠️ `POST /spark/auth/csrf-refresh` explicitly exempt — mandatory, see below | `libs/authorization/.../Endpoints/CsrfRefresh.cs` |
| `SparkClient.LoginAsync` primes and sends a token | `libs/client/MintPlayer.Spark.Client.Authorization/SparkClientAuthExtensions.cs` |
| `/spark/github/dev-ws` constrained to GET — a bare `Map()` matched every mutating verb | `libs/webhooks/.../Extensions/SparkBuilderExtensions.cs` |
| CodeCoverage's three CI uploads explicitly exempt | `apps/CodeCoverage/CodeCoverage/Controllers/{Uploads,ForkUploads}Controller.cs` |
| Two false comments corrected in place | `SparkMiddleware.cs`, `SparkAntiforgeryMiddleware.cs`, `SparkAntiforgeryOptions.cs` |

Tests added: `XsrfSurfaceTests` (metadata inventory of every mutating framework endpoint, exact
sets), `XsrfEnforcementTests` (behavioural, real HTTPS, control beside every case),
`CsrfSurfaceTests` reworked to three positions.

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

### M5 — Docs and versions

`docs/Spark-API-Specification.md:427` describes the cookie correctly; its **"Affected endpoints"**
line is now stale — it predates the passkey, external-login and `/connect` surfaces and does not
mention `login`. Rewrite it to point at `XsrfSurfaceTests` rather than re-listing routes that will
drift again.

Version bumps: `MintPlayer.Spark` `11.0.0-preview.86` → `.87` (CI gate on `libs/**`).
`MintPlayer.Spark.Authorization` and `MintPlayer.Spark.Client.Authorization` change too.
`ng-spark-auth` only if M3 lands.

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
  set and the try/catch above are its shopping list.
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
