# Plan — Move the XSRF-TOKEN mint into `Response.OnStarting`

PRD: [`xsrf_minting_PRD.md`](xsrf_minting_PRD.md). Branch: `fix/api-controller-antiforgery`.

Status: **spike done, implementation not started.** The A/B that decides the design is committed and
green (8 tests, controls included).

## Shape of the work

Small — one middleware registration in `SparkMiddleware.cs`, one client call, one test. It is small
*because* the investigation removed two thirds of the original request: the client-side
`withXsrfConfiguration` work is unnecessary (PRD §4) and the package's flags are a regression to
adopt (PRD §3.2).

⚠️ `SparkMiddleware.cs` runs in every Spark app, so the constraining tests in PRD §6 are the real
gate, not the new ones.

Version bumps: `MintPlayer.Spark` `11.0.0-preview.86` → `.87` (CI gate on `libs/**`). `ng-spark-auth`
only if M3 lands.

---

## Milestones

### M1 — Move the mint

`libs/spark/MintPlayer.Spark/SparkMiddleware.cs:338-358`. Replace the eager append with a callback
registered in the same place, doing the same work.

- Register the callback **above** `UseSparkAntiforgery()` (`:301`) so the gate's own 400 carries a
  fresh cookie (PRD §3.1, reason 2). Note LIFO: registering earlier means running *later*.
- Keep every flag and the null guard (PRD §3.2).
- Wrap `GetAndStoreTokens` + append in `try/catch` — a throw here aborts the response unrecoverably
  (PRD §3.4).
- Snapshot and restore `Cache-Control` / `Pragma` around the call (PRD §3.5, D5).

### M2 — Prove it in the real pipeline

The committed A/B builds its **own** pipeline, so it will keep passing whatever M1 does — it proves
the principle, not the product. Add a test against Spark's actual middleware asserting AC1, AC2, AC5
and AC7.

⚠️ Do not extend the A/B file for this. Its `BeforeHandler` case documents the behaviour being
removed and should keep passing unchanged, as the record of why the move happened.

### M3 — External login stops relying on an accident

`libs/node_packages/ng-spark-auth/core/src/spark-auth.service.ts:186` — add `await this.csrfRefresh()`
to `externalFlow`'s success branch (PRD §3.6). Check whether the existing specs pin the request
sequence there; if so they need the extra expectation.

### M4 — Verify in a browser

Per the original request: build the Angular libs (nx handles the graph), launch an app, drive it with
the `playwright_node` MCP — sign out, F5, sign in, then call a protected endpoint immediately and
confirm no 401/400 in the network log.

⚠️ **Blocked as of now:** the `playwright_node` MCP server failed to connect this session
(`CONNECT_TIMEOUT`), as did `github`. This needs the server back before M4 can run. HR is the easiest
target (local credentials, no GitHub secrets); Fleet is what the E2E harness drives.

### M5 — Docs and versions

`docs/Spark-API-Specification.md:427` already describes the cookie correctly; only the timing changes,
so check whether it needs a word. Bump `MintPlayer.Spark`.

---

## Risks

| Risk | Handling |
|---|---|
| The mint stops running for some response shape | AC8 — `SparkEndpointFactory.MintAntiforgeryAsync` and `SparkClient.EnsureAntiforgeryAsync` both throw if warmup yields no cookie, so a whole class of suites fails loudly rather than subtly |
| Cache headers regress | D5 restores them; AC5 pins it |
| A throw in the callback aborts responses | try/catch (M1); AC6 |
| ⚠️ Someone later "optimises" to mint only when the cookie is absent | It breaks login permanently and silently — PRD §3.3 says why, and the comment goes in the code, not only the doc |

## Out of scope

- Changing `MintPlayer.AspNetCore.SpaServices.Xsrf` (PRD §4) — if it is hardened later, §3.2 and §3.4
  are its shopping list.
- Removing the client's now-redundant `csrfRefresh()` calls after sign-in — 8 specs and a version
  floor, for a cheap round trip.
- `RequireAntiforgery = true` — still deferred, still needs the GitHubOidc question settled first
  (commit `82bad0b2`).

## Found along the way

### F1 — `UseAntiforgery()` validates but never rejects

`AntiforgeryMiddleware.InvokeAwaited` records the failure on `IAntiforgeryValidationFeature` and calls
`next` regardless. Rejection is an MVC filter's job, or Spark's gate. A hand-mapped minimal API with
antiforgery metadata and neither rejector **serves the request**. This invalidated three runs of the
A/B before the controls caught it, and it is the reason Spark ships its own middleware.

### F2 — `UseSpark()` already makes every downstream response uncacheable

`GetAndStoreTokens` stamps `no-cache, no-store` unconditionally. True today, independent of this
change; bounded only because all four apps register static files upstream of `UseSpark()`. An app that
puts `UseOutputCache()` after it loses caching with no diagnostic but an antiforgery log line.

### F3 — A rejection leaves the client wedged

`SparkAntiforgeryMiddleware` returns 400 without calling `next`, so no fresh cookie is minted on a
rejection and a client with a stale token cannot self-heal from a mutating call. M1's placement fixes
this as a side effect.

### F4 — `csrf-refresh` would break login if `RequireAntiforgery` were ever turned on

It carries an ambient cookie credential and a stale anonymous-bound token, so the gate would reject
it — and `login()` awaits it *before* `checkAuth()`, so the whole login promise fails even though the
sign-in succeeded. The M1 move removes this trap before anyone flips that flag.
