# PRD — Where Spark mints the XSRF-TOKEN cookie

Branch: `fix/api-controller-antiforgery`. Evidence:
`tests/MintPlayer.Spark.Tests/Extensions/XsrfMintingPlacementTests.cs` (committed before this
document, because it is what the document rests on).

---

## 1. What was asked, and what measurement did to it

The request was: inline `MintPlayer.AspNetCore.SpaServices.Xsrf` into Spark, wire
`withXsrfConfiguration` into `ng-spark` so XSRF is configured everywhere automatically, and confirm
that the better middleware removes the need for a blank refresh call after sign-in and sign-out.

Three claims went in. Measurement kept one and a half.

| Claim | Verdict |
|---|---|
| The package's placement removes the refresh round trip | **Half.** Fixes sign-in, cannot fix sign-out (§2) |
| `withXsrfConfiguration` should move into `ng-spark` so it applies everywhere | **Refuted.** It restates Angular's own defaults and changes nothing (§4) |
| Inline the package's middleware into Spark | **Inverted.** Take its *placement*, keep Spark's *flags* — the package's are strictly weaker (§3) |

⚠️ Two of my own claims were wrong along the way and are corrected here rather than quietly dropped:
that a JSON POST bypasses ASP.NET Core's antiforgery middleware (it does not — it gates on the HTTP
method), and that `[ValidateAntiForgeryToken]` does not work (it does, in an MVC pipeline; what is
true is narrower — it produces `IAntiforgeryPolicy`, not the `IAntiforgeryMetadata` Spark's gate
reads).

---

## 2. The measured answer: placement fixes sign-in, never sign-out

An antiforgery token is bound to the caller's identity, so a token minted while anonymous is refused
for an authenticated request and vice versa. Spark mints **before** the handler runs
(`SparkMiddleware.cs:338-358`); the package mints in `Response.OnStarting`, i.e. **after**.

| Protected call immediately after… | `BeforeHandler` (today) | `OnStarting` |
|---|---|---|
| sign-in | 400 | **200** |
| sign-out | 400 | **400** |
| *control:* garbage token | 400 | 400 |
| *control:* no token | 400 | 400 |

The mechanism, asserted directly rather than inferred:

- `SignInManager.SignInWithClaimsAsync` assigns `Context.User` on the current request
  (`SignInManager.cs:297`). Every Identity sign-in funnels through it — password, 2FA, passkey,
  external login. So a token minted at response-start is **already bound to the new principal**.
- `SignInManager.SignOutAsync` (`:311-330`) issues three `Context.SignOutAsync` calls and **never
  resets `Context.User`**. Neither does `AuthenticationService.SignOutAsync` nor
  `CookieAuthenticationHandler.HandleSignOutAsync`, which only deletes the cookie. At response-start
  the principal is still the departed user.

**Therefore `/spark/auth/csrf-refresh` stays.** It is not a workaround for bad placement; for sign-out
it is structural — only a fresh request, where authentication re-evaluates from cookies that no longer
exist, can mint an anonymous-bound token.

⚠️ The controls are load-bearing. Three earlier runs of the A/B were green and meaningless: a garbage
token and a missing token both returned 200, because `UseAntiforgery()` **validates but does not
reject** — on failure it records a verdict on `IAntiforgeryValidationFeature` and calls the next
delegate anyway (`AntiforgeryMiddleware.InvokeAwaited`). Rejection belongs to whoever reads that
feature: an MVC filter, or `SparkAntiforgeryMiddleware`, which short-circuits with a 400. A
hand-mapped minimal API has neither.

---

## 3. Design

### 3.1 Move the mint into `Response.OnStarting`, registered *above* the gate

Three reasons, only the first of which was the original motivation:

1. **Correct binding after sign-in** (§2) — measured.
2. **A rejection can carry a fresh cookie.** `SparkAntiforgeryMiddleware` returns 400 *without*
   calling `next`, so today a client holding a stale token gets refused and receives no new one — it
   cannot self-heal from a mutating call. A callback registered above the gate still fires on that
   response.
3. **The cookie survives an error response.** `UseExceptionHandler` calls `Response.Clear()`, which
   drops `Set-Cookie`; an eagerly-appended cookie is discarded and the re-executed error branch does
   not re-run the middleware. An `OnStarting` callback runs after the clear.

### 3.2 Keep Spark's cookie flags exactly as they are

`HttpOnly = false` (the SPA must read it), `SameSite = Strict`, `Secure = Request.IsHttps`,
`Path = "/"`, and the `RequestToken != null` guard.

⚠️ The package sets only `Path` and `HttpOnly`. Adopting its flag set would drop `Secure` and
`SameSite` — a regression, already rejected on evidence once
(`docs/coverage-handoff-plan.md:667-676`), and `XsrfCookieFlagTests.cs:26-40` would fail it. **Take
the placement, keep the flags.**

### 3.3 Mint unconditionally

⚠️ "Only mint when the request carries no `XSRF-TOKEN` cookie" is the obvious optimisation and it
**breaks login permanently**: the browser keeps the cookie across sign-in, so `csrf-refresh` — whose
entire job is to re-mint — would find the cookie present and emit nothing. Any useful conditionality
would have to key on the *binding*, which costs the same as minting.

Cost is not the argument anyway: `GetTokensInternal` caches per request, and `GetCookieTokens` reuses
a valid incoming cookie token rather than minting a new one. The steady-state cost is one `Unprotect`,
one SHA-256 and one `Protect` — microseconds beside the RavenDB session every Spark request opens.

### 3.4 Wrap the callback in try/catch

⚠️ A throw inside `OnStarting` is **unhandleable** — Kestrel reports it and aborts the response,
unlike a throw from ordinary middleware which `UseExceptionHandler` can catch. Real triggers exist:
`DefaultAntiforgery.CheckSSLConfig` throws when `Cookie.SecurePolicy == Always` over HTTP, and
DataProtection key-ring failures. Degrade to "no cookie on this response" rather than killing it.

### 3.5 ⚠️ Cache headers — a pre-existing problem the move makes visible

`GetAndStoreTokens` unconditionally stamps `Cache-Control: no-cache, no-store` and `Pragma: no-cache`.
So **`UseSpark()` already makes every response downstream of it uncacheable**, today, before this
change. It is bounded only because all four apps register static files *upstream* of `UseSpark()`.

Moving to `OnStarting` makes this strictly worse in one respect: Kestrel pops `OnStarting` callbacks
**LIFO**, so the earliest-registered callback runs **last** — and the no-store stamp would then
override whatever the response had set, rather than being overridable by a downstream writer.

**Decision (D5): snapshot `Cache-Control` and `Pragma` before `GetAndStoreTokens` and restore them
after.** The alternative — documenting that `UseSpark()` decides your caching — is a worse promise for
a framework to make, and the restore is three lines.

### 3.6 External login is the one principal change with no explicit refresh

`externalFlow`'s success path calls `checkAuth()` and not `csrfRefresh()`
(`spark-auth.service.ts:186`). It works **by accident**: `checkAuth()` is `GET /spark/auth/me`, whose
response passes the mint, which sits after `UseAuthentication()`. Add the explicit refresh so it stops
depending on "we happen to mint on every response".

---

## 4. What is NOT being done, and why

- **`withXsrfConfiguration` is not moving into `ng-spark`.** Measured in Angular 22.1.3:
  `XSRF_ENABLED` has `factory: () => true`, `XSRF_DEFAULT_COOKIE_NAME = 'XSRF-TOKEN'`,
  `XSRF_DEFAULT_HEADER_NAME = 'X-XSRF-TOKEN'`. The call assigns the values that are already the
  defaults. `withNoXsrfProtection()` is the only switch that changes behaviour — which is what the
  SpaServices demo's own comment says, and what its commented-out line does.
- ⚠️ And it would be actively harmful: having both features in one `provideHttpClient(...)` **throws**
  in dev mode, so a framework bundle that injects `withXsrfConfiguration` would make
  `withNoXsrfProtection()` unreachable for a bearer-token SPA.
- **`provideSpark()` is called by no app in this repository**, so putting anything there reaches
  nobody.
- **The client's existing `csrfRefresh()` calls stay.** After the move they are redundant for sign-in,
  but removing them breaks 8 vitest specs and creates a client/server version floor, for a round trip
  that is cheap.
- **`MintPlayer.AspNetCore.SpaServices.Xsrf` is not changed here.** If it is ever hardened, the flags
  in §3.2 and the try/catch in §3.4 are what it needs.

---

## 5. Acceptance criteria

1. A protected call immediately after sign-in succeeds with the cookie minted on the sign-in response
   — no `csrf-refresh` in between.
2. A protected call immediately after sign-out is still refused, and `csrf-refresh` still fixes it.
3. Controls hold: garbage token and missing token are both refused.
4. Cookie flags unchanged — `HttpOnly=false`, `SameSite=Strict`, `Secure` over HTTPS, `Path=/`.
   `XsrfCookieFlagTests` passes untouched.
5. A response that sets its own `Cache-Control` still has it after the mint (D5).
6. A throw inside the callback does not abort the response.
7. `SparkAntiforgeryMiddleware`'s 400 carries a fresh `XSRF-TOKEN` cookie.
8. `SparkEndpointFactory.MintAntiforgeryAsync` and `SparkClient.EnsureAntiforgeryAsync` still find the
   cookie on warmup.
9. All four apps still sign in and out through the browser.

## 6. Blast radius

`SparkMiddleware.cs` is in every Spark app. The constraining tests are
`XsrfCookieFlagTests.cs:26-40`, `SparkEndpointFactory.cs:212-244`, `SparkClient.cs:853-867`, and the
8 vitest specs pinning the `csrf-refresh` call sequence. `SparkAntiforgeryTests` tests the gate, not
the mint, and is unaffected.
