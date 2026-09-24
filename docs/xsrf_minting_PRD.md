# PRD — Spark's CSRF protection: coverage first, then where the token is minted

Branch: `fix/api-controller-antiforgery`.

Evidence in this repository:

- `tests/MintPlayer.Spark.Tests/Extensions/XsrfSurfaceTests.cs` — the metadata inventory of every
  mutating framework endpoint.
- `tests/MintPlayer.Spark.E2E.Tests/Security/XsrfEnforcementTests.cs` — the behavioural pinning,
  over real HTTPS with a real cookie jar, with a failing control beside every passing case.
- `tests/MintPlayer.Spark.Tests/Extensions/XsrfMintingPlacementTests.cs` — the A/B that decided §5.
- `apps/CodeCoverage/CodeCoverage.Tests/_Infrastructure/CsrfSurfaceTests.cs` — the same inventory
  for the production app's own controllers.

---

## 0. ⚠️ Corrections to the previous version of this document

The earlier draft of this PRD asserted a number of things about ASP.NET Core, Angular and this
codebase that were not checked. They have now been checked — against `dotnet/aspnetcore`
`release/11.0` (cross-read against `release/11.0-rc1`, which matches the installed
`11.0.0-rc.1.26425.128`), against `node_modules/@angular/common` 22.1.3, and against this repo's own
route table. Wrong claims are listed before anything else, because two of them had been copied out of
this document and into production source comments, where they were teaching the next reader the same
mistake.

| Claim as previously written | Verdict | What is actually true |
|---|---|---|
| "The built-in `UseAntiforgery()` was **narrowed in 8.0.1** to validate **only form-content bodies**" | ❌ **False, twice** | There is no 8.0.1 source change at all (`v8.0.0...v8.0.1` touches only `PublicAPI.*.txt`). The filter is by **HTTP method**, not content type — `IsValidHttpMethodForForm` is POST/PUT/PATCH — and it shipped in 8.0.0. **A JSON POST *is* validated by it.** This sentence was in `SparkMiddleware.cs` and in `SparkAntiforgeryMiddleware`'s XML doc; both are corrected. |
| "`[ValidateAntiForgeryToken]` produces `IAntiforgeryPolicy`, not `IAntiforgeryMetadata`" | ⚠️ **Right conclusion, wrong mechanism** | The attribute implements `IFilterFactory, IOrderedFilter` — **neither** interface. It is a filter *factory*; the DI-resolved `ValidateAntiforgeryTokenAuthorizationFilter` carries the empty `IAntiforgeryPolicy` marker, which exists only for MVC's most-effective-filter resolution. The conclusion (routing reads `IAntiforgeryMetadata` and never sees it) stands. |
| "`GetAndStoreTokens` **unconditionally** stamps `Cache-Control: no-cache, no-store`" | ⚠️ **Partly** | Guarded by `if (!httpContext.Response.HasStarted)` (`DefaultAntiforgery.cs:66`). It matters less than it sounds: `FireOnStarting` runs *before* headers commit, so inside an `OnStarting` callback `HasStarted` is still false and the stamp **does** happen. D5 survives. |
| "A throw inside `OnStarting` **aborts the response**" | ⚠️ **Partly — and the truth is worse** | `HttpProtocol.FireOnStarting` catches it and calls `ReportApplicationError`. The exception never reaches the pipeline, so `UseExceptionHandler` cannot see it; remaining callbacks are **skipped**; and `ProduceEnd` emits a **bare 500 with every header reset**. An abort happens only if the response had already started. Silent 500 beats abort for damage and loses to it for debuggability. |
| "`provideSpark()` is called by no app, so putting `withXsrfConfiguration` there reaches nobody" | ⚠️ **True but badly misleading** | `provideSpark()` really is uncalled. But `withSparkAuth()` (`libs/node_packages/ng-spark-auth/src/lib/provide-spark-auth.ts:32`) **already calls `withXsrfConfiguration`**, and CodeCoverage, Fleet and HR all spread it; DemoApp calls it directly. The client-side half of the original request was already shipped. The old §4 discussed adding it as though it were absent. |
| "8 vitest specs pin the `csrf-refresh` call sequence" | ⚠️ **Miscounted** | 8 `expectOne` assertions across **2** spec files. |
| "`SignInManager.SignOutAsync` — `SignInManager.cs:311-330`" | ⚠️ **Range short** | The method spans **311–333**. The behaviour claim is correct. |
| "Spark's path-prefix gate protects nothing, so the surface is unprotected" | ⚠️ **True clause, false implication** | `RequireAntiforgery` was indeed never assigned, so the *default* branch protected nothing and `WarnOnly` could never log. But **24 endpoints carry explicit metadata** and were enforced the whole time. What was actually exposed was (a) anything an application maps itself and (b) `POST /spark/auth/login`. §2 is the real gap list. |

Claims that were checked and **held**: `SignInManager.cs:297` is exactly `Context.User = userPrincipal;`
and is the file's only such assignment; `SignOutAsync` never resets `Context.User`, nor does
`AuthenticationService.SignOutAsync` or `CookieAuthenticationHandler.HandleSignOutAsync`;
`AntiforgeryMiddleware.InvokeAwaited` records and calls `_next` regardless;
`DefaultAntiforgery.CheckSSLConfig` throws on `SecurePolicy.Always` over HTTP; Kestrel pops
`OnStarting` **LIFO** (`HttpProtocol` holds a `Stack`, and IIS/HTTP.sys match);
`UseExceptionHandler` calls `Response.Clear()` → `Headers.Clear()` and re-executes only `_next`;
Angular 22.1.3's `XSRF_ENABLED` factory is `() => true` with defaults `XSRF-TOKEN` /
`X-XSRF-TOKEN`; combining `withXsrfConfiguration` and `withNoXsrfProtection` throws in dev mode;
and the XSRF interceptor skips GET/HEAD and compares real URL origins.

### Two things .NET 11 changes that were not previously known

- **`AntiforgeryApplicationModelProvider` is new.** A controller action carrying
  `[RequireAntiforgeryToken]` now also gets MVC's rejecting filter, which makes it the *supported*
  annotation on controllers — and applying it together with `[ValidateAntiForgeryToken]` **throws at
  startup**.
- **A second CSRF middleware ships in `DefaultBuilder`** (`CsrfProtectionMiddleware`,
  origin/`Sec-Fetch-Site`-based). Like the antiforgery middleware it records a verdict and calls
  `next` without rejecting.

---

## 1. What this branch is for

Two separable things, in priority order.

1. **Coverage** — every framework endpoint, and every endpoint the Angular apps call, is either
   protected or explicitly and defensibly exempt. This is the part that closes an actual hole.
2. **Minting placement** — whether the `XSRF-TOKEN` cookie is written before the handler or at
   response start. This is an ergonomics question about one round trip, and it is settled in §5.

The original request was framed entirely as (2). Measuring it produced (1), which matters more.

---

## 2. The coverage gap, measured

Read off the live route table (`XsrfSurfaceTests` builds a real host and enumerates
`EndpointDataSource`, rather than grepping for attributes).

**Protected before this branch: 22 endpoints**, all by explicit metadata — the six PersistentObject
writes, `actions/execute`, the three `lookupref` verbs, `auth/logout`, `auth/{forgotPassword,
resetPassword, manage/2fa, manage/info}`, four passkey routes, `external-logins/unlink` and four
`/connect/*` form posts.

⚠️ `sync/apply` and `etl/deploy` were stamped earlier **on this branch** and are stamped no longer —
see §3.3. They are the one case where adding the attribute was a regression rather than a hardening.

**Genuinely exposed, and fixed here:**

| Endpoint | Why it mattered |
|---|---|
| `POST /spark/auth/login` | ⚠️ **Login CSRF.** Excluded from the gated list on the stated grounds that "there is no session yet, so there is no XSRF-TOKEN cookie to validate" — a false premise, since `UseSpark()` mints the cookie on *every* response including anonymous ones. The attack is real and well known: the attacker's page POSTs the attacker's own credentials, the victim's browser silently acquires a session belonging to the attacker, and everything the victim does next — uploading coverage, linking a repository, changing a setting — lands in an account the attacker can log into and read. |
| Anything an application `MapPost`s itself | The inverted default existed precisely for this and was switched off. `AddControllers()` attaches no metadata and MVC's own attribute is not read, so an app's cookie-authenticated POST was checked by nobody. |
| `/spark/github/dev-ws` | Mapped with a bare `endpoints.Map(...)`, so it matched POST/PUT/PATCH/DELETE as well as GET — a mutating verb under `/spark` that no gate could ever cover. Not exploitable (the handler 400s non-WebSocket requests) but it had no business being on the mutating surface. |

**Deliberately left open, with the reason now written down** — five core reads that are POSTs only
because they need a request body (`po/load`, `queries/{execute,get,distinct-values}`,
`actions/list`), and three anonymous auth endpoints (`register`, `refresh`,
`resendConfirmationEmail`). CSRF does not threaten a read the attacker cannot see the response to,
and it does not threaten an endpoint with no ambient authority to ride — an attacker can POST those
from their own server and gain exactly as much.

⚠️ `login` is the one anonymous endpoint that is different, because its *effect* is to create
ambient authority in the victim's browser. That distinction is why "it's anonymous" was the wrong
reason to skip it.

### 2.1 ⚠️ How much of this `SameSite` already covers — stated honestly

`CookieAuthenticationOptions` defaults `Cookie.SameSite` to `SameSiteMode.Lax` (verified against
`release/11.0`), and nothing in this repository overrides it. Lax means the browser does **not**
attach the authentication cookie to a cross-site `POST`. So for the classic case — an attacker page
on `attacker.example` forging an authenticated action — a modern browser already refuses, and the
request arrives unauthenticated.

That is a real mitigation and it should not be overstated away. Three reasons it is not enough:

1. **Same-site is not same-origin.** Any host under `mintplayer.com` is *same-site* with
   `coverage.mintplayer.com`, so a Lax cookie **is** sent on requests originating from a sibling
   subdomain. Anything hostile served from any `*.mintplayer.com` — an XSS, a user-content page, a
   forgotten staging host — forges these requests with the victim's full authority.
2. **It is a browser default, not a server control.** The application cannot observe or enforce it,
   and it is exactly the kind of protection that is absent on the client you did not think about.
3. ⚠️ **Login CSRF is not mitigated by it at all.** That attack does not rely on any cookie the
   victim already holds — the attacker supplies their *own* credentials, and the response is what
   plants the session. `SameSite` on the auth cookie is irrelevant to a request that is trying to
   *create* one. The only defence is the antiforgery token, and Spark's `XSRF-TOKEN` is
   `SameSite=Strict`, so it is correctly absent from a cross-site POST and the gate refuses.

**Measured, not argued** (2026-09-24, against the deployed `coverage.mintplayer.com`, from an
authenticated same-origin context): `POST /api/me/accounts/resync` with **no** `X-XSRF-TOKEN` header
returned **200**. The endpoint is genuinely unguarded in the deployed build; the
`[RequireAntiforgeryToken]` that fixes it is commit `82bad0b2` on this branch and is not released
yet. The probe was chosen because both outcomes are harmless — a resync of the caller's own accounts.

---

## 3. Design

### 3.1 `RequireAntiforgery` now defaults to `true`

`SparkAntiforgeryOptions.RequireAntiforgery` was documented as "the default becomes `true` at the
next major". Packages moved to `11.0.0-preview.*` when the solution retargeted `net11.0`. That is
this major; this is that flip.

Within `PathPrefixes` (`/spark`, `/connect` by default), a mutating request carrying an **ambient**
credential is now checked without any per-endpoint annotation. Explicit metadata still wins in both
directions.

⚠️ This is the only change that protects endpoints **an application writes itself**, which is why it
is preferred over stamping metadata onto framework endpoints — `SparkAntiforgeryOptions` already
argued this: metadata can only be attached by something that knows the endpoint exists, so a
stamping design covers controllers and leaves the app's own `MapPost` silently open, "which is the
shape of the defect rather than its fix."

### 3.2 Two explicit positions the flip made mandatory

- ⚠️ **`POST /spark/auth/csrf-refresh` is explicitly exempt.** A token is bound to a principal, so a
  client whose identity just changed necessarily holds a stale one. Requiring a valid token from the
  endpoint whose entire job is to issue a valid token is a deadlock with no recovery: the client
  cannot make another mutating call for the rest of the session. It is marked rather than left
  unmarked because it is a cookie-authenticated POST under `/spark` — precisely what the inverted
  default now catches.
- ⚠️ **`POST /spark/auth/login` is explicitly required.** It is anonymous, so the inverted default
  never reaches it; only explicit metadata can.

### 3.3 ⚠️ The replication endpoints go the other way — the stamp was the regression

`/spark/sync/apply` and `/spark/etl/deploy` were stamped `RequireAntiforgeryTokenAttribute(true)`
earlier on this branch as defence in depth. That broke cross-module replication, and it is worth
recording why, because the reasoning that produced it sounds correct.

Explicit metadata is enforced **before authentication**, and it applies to **anonymous** callers.
These endpoints are machine endpoints authenticated by a pinned module client certificate over mTLS,
so every caller that had not yet presented a certificate stopped getting the `401`/`403` that says
what was wrong and started getting a bare `400` instead — the antiforgery gate answering on behalf of
a check that had not run yet. `CrossModuleSyncTests` and `ReplicationEndpointAuthTests` both fail on
that, which is how it was caught.

The stamps are removed, and they were unnecessary anyway once §3.1 landed: a caller arriving with an
**ambient** credential — the browser the stamp was guarding against — is now checked by the default
branch with no annotation, and a module presenting its certificate is non-ambient and exempt either
way. A caller presenting nothing has no authority to forge and should hear that from the auth check.

Pinned from both sides so it cannot come back:
`Replication_endpoints_do_not_require_an_antiforgery_token` asserts the metadata (and asserts the two
routes exist, so it cannot pass vacuously), and `ReplicationEndpointAuthTests`' exact-status
assertions fail on a 400 from the other direction.

### 3.4 Client changes that follow

`SparkClient.LoginAsync` sent no token by design. It now primes one (`requiresAntiforgery: true`,
a warmup `GET /spark` cached for the client's lifetime). The Angular client needed **no** change: it
POSTs `/login` first and holds the anonymous cookie from any earlier response, and `withSparkAuth()`
already configures the interceptor.

### 3.5 CodeCoverage keeps `WarnOnly = true`

Production behaviour is deliberately unchanged for its own `/api` surface: unannotated endpoints are
logged, not rejected. The difference is that this now *works* — previously `WarnOnly` sat inside the
branch guarded by `RequireAntiforgery`, so it logged nothing and always would have.

All five of its mutating actions are annotated explicitly (two required, three exempt), and
`CsrfSurfaceTests` asserts there are no unannotated ones — so `WarnOnly` decides nothing for this
app and turning it off later cannot break it.

⚠️ The three exempt uploads are marked rather than left bare because `GitHubOidc` is deliberately
**not** a credential scheme: it records no scheme feature, and `HasAmbientCredential` reads "nothing
recorded" as ambient. Without the explicit exemption, turning `WarnOnly` off would start rejecting
CI uploads.

---

## 4. What is NOT being done

- **`withXsrfConfiguration` is not being added to `ng-spark`.** It is already in `ng-spark-auth`'s
  `withSparkAuth()`, used by three of the four apps; DemoApp calls it directly. It is also a no-op
  for wire behaviour — Angular 22.1.3's defaults are exactly `XSRF-TOKEN` / `X-XSRF-TOKEN` and
  `XSRF_ENABLED` defaults true. It is not *inert* (it flips `CustomXsrfConfiguration` into the
  feature set, which is what makes the `withNoXsrfProtection` guard throw), which is a further
  reason not to inject it from a framework bundle.
- **`MintPlayer.AspNetCore.SpaServices.Xsrf` is not changed.** Its middleware sets only `Path` and
  `HttpOnly`, leaves `RequestToken` unguarded for null, and has no try/catch. If it is ever
  hardened, §3.2 of the plan and the flag set below are the shopping list. Spark keeps its own
  flags: `HttpOnly=false`, `SameSite=Strict`, `Secure=IsHttps`, `Path=/` — a swap was already
  rejected on evidence once (`docs/coverage-handoff-plan.md:667-676`).
- **The client's existing `csrfRefresh()` calls stay.** Redundant for sign-in after §5, but removing
  them breaks 8 assertions across 2 spec files and creates a client/server version floor, for a
  round trip that is cheap.

---

## 5. Minting placement: the answer is "it only half helps"

An antiforgery token is bound to the caller's identity. Spark mints **before** the handler
(`SparkMiddleware.cs:338-358`); `MintPlayer.AspNetCore.SpaServices.Xsrf` mints in
`Response.OnStarting`, i.e. **after**.

| Protected call immediately after… | `BeforeHandler` (today) | `OnStarting` |
|---|---|---|
| sign-in | 400 | **200** |
| sign-out | 400 | **400** |
| *control:* garbage token | 400 | 400 |
| *control:* no token | 400 | 400 |

Mechanism, asserted rather than inferred:

- `SignInManager.SignInWithClaimsAsync` assigns `Context.User` (`SignInManager.cs:297`, the only
  such assignment in the file). Every Identity sign-in funnels through it, so a token minted at
  response-start is already bound to the **new** principal.
- `SignInManager.SignOutAsync` (`:311-333`) issues three `Context.SignOutAsync` calls and never
  resets `Context.User`; neither does `AuthenticationService.SignOutAsync` nor
  `CookieAuthenticationHandler.HandleSignOutAsync`, which only deletes the cookie. At response-start
  the principal is still the departed user.

**Therefore `/spark/auth/csrf-refresh` stays.** For sign-out it is structural, not a workaround for
placement: only a fresh request, where authentication re-evaluates from cookies that no longer
exist, can mint an anonymous-bound token.

⚠️ The controls are load-bearing. Three earlier runs of that A/B were green and meaningless because
a garbage token and a missing token both returned 200 — `UseAntiforgery()` records a verdict and
calls the next delegate, and a hand-mapped minimal API that binds no form has no consumer to reject
on its behalf.

### Why move it anyway

1. Correct binding after sign-in (above) — measured.
2. **A rejection can carry a fresh cookie.** `SparkAntiforgeryMiddleware` returns 400 *without*
   calling `next`, and the mint is registered after it, so today a client holding a stale token is
   refused and handed nothing — it cannot self-heal from a mutating call.
3. **The cookie survives an error response.** `UseExceptionHandler` calls `Response.Clear()`, which
   calls `Headers.Clear()` and drops every `Set-Cookie`; the re-executed branch is `_next`, so
   middleware above it never runs again.

### Constraints on the move

- ⚠️ **Mint unconditionally.** "Only mint when the cookie is absent" breaks login permanently: the
  browser keeps the cookie across sign-in, so `csrf-refresh` — whose whole job is to re-mint — would
  find it present and emit nothing.
- ⚠️ **Wrap in try/catch.** `CheckSSLConfig` throws on `SecurePolicy.Always` over HTTP, and
  DataProtection key-ring failures are real. A throw inside `OnStarting` is swallowed by Kestrel,
  skips every callback registered before it, and produces a bare 500 with all headers reset — a
  failure `UseExceptionHandler` cannot see.
- ⚠️ **Restore `Cache-Control` / `Pragma` (D5).** `GetAndStoreTokens` stamps `no-cache, no-store`
  whenever the response has not started, and inside `OnStarting` it has not. Kestrel pops callbacks
  **LIFO**, so a callback registered early runs late and would overwrite a downstream writer's
  headers instead of being overwritten by them.

---

## 6. Acceptance criteria

1. Every mutating framework endpoint is in exactly one of three states — required, explicitly
   exempt, or knowingly unstated with a written reason — and `XsrfSurfaceTests` asserts the exact
   membership of all three sets.
2. `POST /spark/auth/login` is refused without a token, succeeds with one, and is refused with a
   forged one.
3. `POST /spark/auth/csrf-refresh` is reachable without a token, and demonstrably repairs a session
   whose token went stale at sign-in.
4. An authenticated mutating call is refused without a token and succeeds with one.
5. Every CodeCoverage controller action states its position; none relies on `WarnOnly`.
6. Cookie flags unchanged — `HttpOnly=false`, `SameSite=Strict`, `Secure` over HTTPS, `Path=/`.
   `XsrfCookieFlagTests` passes untouched.
7. After the §5 move: a protected call immediately after sign-in succeeds with no `csrf-refresh`
   between; a response that set its own `Cache-Control` still has it; a throw in the callback does
   not kill the response; the gate's 400 carries a fresh cookie.
8. `SparkEndpointFactory.MintAntiforgeryAsync` and `SparkClient.EnsureAntiforgeryAsync` still find
   the cookie on warmup.
9. All four apps still sign in and out through a browser.

## 7. Blast radius

`SparkMiddleware.cs` and `SparkAntiforgeryOptions.cs` are in every Spark app, and §3.1 changes a
default. The constraining tests are `XsrfCookieFlagTests.cs`, `SparkAntiforgeryTests.cs`,
`LocalCredentialModeTests.cs`, `SparkEndpointFactory.MintAntiforgeryAsync`,
`SparkClient.EnsureAntiforgeryAsync`, and the 8 vitest assertions pinning the `csrf-refresh`
sequence.

⚠️ Downstream applications on 10.x that mapped their own cookie-authenticated `POST` without a token
will start receiving 400s on upgrade. That is the entire point of the change, and it is what
`WarnOnly` is for; it belongs in the release notes in those words.
