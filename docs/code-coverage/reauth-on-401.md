# Surviving GitHub user-token expiry — silent refresh + reconnect fallback

Status: **implemented** (M1 + M2, 2026-08-14, branch `reauth-on-github-401`; M3 remains optional/upstream).
Verified live in dev: hand-expired token → silent refresh (orgs listed, expiry rotated to +8h);
corrupted refresh token + restart → banner → Reconnect → silent re-auth, orgs restored.
One deviation from §M1.1: the single-flight gate also remembers the winner's fresh tokens and the
last *refused* refresh token — losers can't re-read the winner's save through their stale RavenDB
session, and a known-dead `ghr_` must not be re-spent against GitHub on every request.
Companion incident context: the 2026-08-13 "badge never clears" investigation (see git history
around commits `a05813b` and `3970d22`).

> Research basis: a three-agent investigation (2026-08-13) of the MintPlayer.Spark auth stack
> (server + ng-spark-auth client) and GitHub's token-lifecycle documentation. Key claims carry
> their source.

---

## 1. Problem

Coverage stores each user's GitHub **user-to-server token** (`ghu_…`) at sign-in and uses it for
`GET /user/installations` — the call that decides which accounts/orgs the user can see and
backfills/clears the "App installed" badge (`GitHubAccessService`).

That token dies routinely:

- **8-hour expiry.** With the App's *"Expire user authorization tokens"* option (default-on for
  new GitHub Apps) every user token expires 8 hours after sign-in
  ([refreshing-user-access-tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)).
  This is what produced the observed `GitHub /user/installations query failed: Unauthorized` in
  production on 2026-08-13 (sign-in 10:26 UTC, 401s after 20:00 UTC). **Not** the App uninstall —
  uninstalling explicitly does *not* revoke user tokens
  ([reviewing-and-modifying-installed-github-apps](https://docs.github.com/en/apps/using-github-apps/reviewing-and-modifying-installed-github-apps)).
- **Revoked authorization.** The user revoking the app under *Settings → Applications* kills all
  its tokens (including refresh tokens)
  ([token-expiration-and-revocation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/token-expiration-and-revocation)).

Today the failure is silent and sticky: since commit `3970d22` a failed query correctly degrades
to own-username visibility (nothing cleared, nothing cached), but the user just sees orgs missing
from /home with no explanation, and the only remedy is a manual sign-out/sign-in. The Identity
session cookie outlives the GitHub token by design, so this state is the *norm* for any session
older than 8 hours, not an edge case.

## 2. What the investigation established

### GitHub (docs, with citations)

- User token lifetime 8 h; refresh token (`ghr_…`) lifetime 6 months. Refresh grant:
  `POST https://github.com/login/oauth/access_token` with `client_id`, `client_secret`,
  `grant_type=refresh_token`, `refresh_token`. Response carries a **new** access token *and* a
  **new** refresh token — rotation is **single-use**: "Once you use a refresh token, that refresh
  token and the old user access token will no longer work."
  ([refreshing-user-access-tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens))
- Uninstall ≠ revoke, in both directions; only revoke deactivates tokens. A valid token whose app
  has zero installations gets `200` + `total_count: 0`, not 401.
- Re-authorization UX: while the grant still exists (expired token, or uninstalled-but-not-revoked)
  re-running the authorize URL completes **silently** — no consent screen
  ([authorizing-oauth-apps](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)).
  After a revoke the consent screen reappears. There is **no `prompt=none`**; the only documented
  `prompt` value is `select_account`, so validity can't be probed via the browser.
- Cheap server-side validity check exists if ever needed:
  `POST /applications/{client_id}/token` (Basic auth `client_id:client_secret`), `200` = valid /
  `404` = dead, exempt from failed-login rate limits
  ([oauth-applications REST](https://docs.github.com/en/rest/apps/oauth-applications)). The same
  family offers `DELETE /applications/{client_id}/grant` for a real "disconnect GitHub" feature.
- `GET /user/installations` lists installations of **the token's own app only**.

### Spark server (`C:\Repos\MintPlayer.Spark`, matches deployed packages)

- The external-login callback (`SparkAuthenticationExtensions.cs:196-204`) re-saves **all**
  `info.AuthenticationTokens` via `SetAuthenticationTokenAsync` on *every* successful callback —
  existing users included (pinned by `ExternalLoginCallbackTests.cs:125-150`). Because Coverage
  sets `options.SaveTokens = true` (`Program.cs:57`), the stored tokens include `access_token`,
  `token_type`, and — whenever GitHub returns them — `refresh_token` and `expires_at`.
  **A re-challenge is therefore already a token refresh**; no Spark change is needed.
- Tokens live in cleartext in `SparkUser.Tokens` on the user document (`UserStore.SetTokenAsync`).
- Spark has no user-token refresh machinery and never reads the stored token itself; the only
  401-reactive code is for *installation* tokens (`TokenRefreshingHandler`) — a good pattern, not
  reusable (user tokens need either the refresh grant or a browser).
- Endpoints: challenge `GET /spark/auth/external-login?provider=GitHub&returnUrl=…[&popup=1]`,
  callback `GET /spark/auth/external-login-callback`, GitHub callback path `/signin-github`.
- Known upstream edge (not blocking, Coverage users have no Identity 2FA/lockout): a non-`Success`
  `ExternalLoginSignInAsync` result falls into auto-provisioning, which fails on the duplicate
  email and **skips the token save**. ~10-line upstream fix: look up `FindByLoginAsync` before
  branching and take the existing-user path for already-linked users.

### ng-spark-auth client (v22.1.0)

- `SparkAuthService.loginWithProvider('GitHub', { returnUrl, mode: 'popup' | 'redirect' })` is the
  full flow (named popup + origin-checked `postMessage` + `checkAuth()` before resolving). Errors:
  `no_login_info | email_not_verified | account_creation_failed | popup_blocked | popup_closed`.
- **The package's HTTP interceptor swallows any 401 outside `/spark/auth` and navigates to
  `/login`.** A reauth signal must therefore be a field on a 200 response, never a 401.
- After a successful popup, `checkAuth()` writes a new `AuthUser` object into `user()`, so
  `home.component`'s existing `effect()` re-runs and reloads accounts automatically. Corollary
  hazard: never call `loginWithProvider` from inside that effect (infinite re-entry).
- Popups must be user-gesture-initiated; an automatic popup resolves `popup_blocked` and the
  redirect fallback would navigate the user away unprompted → the recovery UI must be
  **button-gated**.
- In `mode: 'redirect'` the returned promise never settles — no code after that `await` runs.

## 3. Design

Two layers: a **silent server-side refresh** that makes the routine 8-hour expiry invisible, and a
**reconnect banner** for the cases only a browser can fix (revoked grant, missing/expired/burned
refresh token). All changes are app-local to Coverage.

### M1 — Silent refresh (server) 🟦

1. New `GitHubUserTokenService` (scoped, `[Register]`), the single owner of "give me a working
   user token":
   - Read `access_token` + `refresh_token` + `expires_at` via
     `userManager.GetAuthenticationTokenAsync(user, "GitHub", …)`.
   - If `expires_at` is missing or comfortably in the future → return the access token as-is.
   - If expired or within a small skew window (~5 min), or if the caller reports a 401: call the
     refresh grant with `GitHub:{env}:ClientId` / `ClientSecret` from config, then persist **all**
     returned tokens (`access_token`, `refresh_token`, `expires_at`) via
     `SetAuthenticationTokenAsync` before returning the new access token.
   - **Single-flight per user** (e.g. per-user `SemaphoreSlim` in a static/Concurrent dictionary,
     or `Lazy<Task>` in `IMemoryCache`): refresh tokens are single-use, so two concurrent requests
     must not both spend the same `ghr_` — the loser gets the winner's fresh token.
   - Refresh failure (GitHub 4xx / no refresh token stored) → return "reauth required"; never
     throw into the caller.
2. `GitHubAccessService.GetAllowedOwnersAsync` uses the service; on a 401 from
   `/user/installations` it asks for one forced refresh and retries once (mirrors Spark's
   installation-token `TokenRefreshingHandler` pattern). The result gains a tri-state:
   owners + `TokenState ∈ { Ok, ReauthRequired, Unavailable }` (interface change; `Unavailable`
   = transient GitHub failure, keep today's null semantics: don't cache, don't clear, don't nag).
3. Verification task (first milestone step): confirm the App's *"Expire user authorization
   tokens"* is enabled and that `refresh_token`/`expires_at` actually appear in `SparkUser.Tokens`
   after a fresh sign-in (Raven Studio or a debug log listing token *names only* — never values).
   If the option is off, enable it: silent refresh depends on it, and it's GitHub's recommended
   mode anyway.

### M2 — Reauth flag + Reconnect banner 🟦

1. `MeController.AccountsResponse` gains `bool GitHubReauthRequired` (set from
   `TokenState.ReauthRequired`). Always HTTP 200 — the client interceptor hijacks 401s to /login.
2. ClientApp:
   - Extract shell's GitHub login flow (popup, `popup_blocked` → redirect retry, error-message
     map) into a shared `GitHubLoginService`; shell and home both consume it. Gate against
     concurrent invocations (the popup is a named window; two flows settle on one message).
   - `AccountsService.AccountsResponse` interface gains `gitHubReauthRequired`.
   - /home: when the flag is set, show a warning `bs-alert` ("Coverage lost access to your GitHub
     account…") with a **Reconnect GitHub** button → `loginWithProvider('GitHub',
     { returnUrl: '/home' })` → on success `resync()` (drops the 5-minute owners cache; the
     failure path never caches, but an earlier *successful* stale entry can still be live).
     Treat `popup_closed` as "not now" (no error banner). New translation keys in
     `App_Data/translations.json` (en/fr/nl).
3. While the flag is set the rest of the page keeps rendering the degraded (own-account) list —
   the banner explains why orgs are missing.

### M3 — Optional / upstream (not blocking) 🟩

- Spark: existing-user lookup before the auto-provision branch so non-`Success` sign-in results
  still re-save tokens (closes the 2FA/lockout token-save hole).
- ng-spark-auth: fold the `popup_blocked` → redirect fallback into `loginWithProvider`
  (`mode: 'popup-then-redirect'`); ship translation keys for the five error codes.
- Coverage: handle the `github_app_authorization` webhook (action `revoked`) to flag reauth
  proactively instead of waiting for the next 401. Note: Spark's webhook processor only broadcasts
  its ten overridden event types — check whether `github_app_authorization` is among them before
  planning on it.
- A "Disconnect GitHub" account action via `DELETE /applications/{client_id}/grant`.

## 4. Test plan

- `GitHubUserTokenService`: unit-test the decision table (fresh / near-expiry / expired / no
  refresh token / refresh 4xx) with a faked HTTP handler and in-memory user store; concurrency
  test that N parallel callers produce exactly one refresh call.
- `GitHubAccessService`: 401 → one forced refresh → retry → success path; refresh-fails →
  `ReauthRequired` propagated, nothing cached/cleared (extends the 3970d22 behavior).
- `MeController`: flag mapping.
- Manual E2E: sign in, hand-expire the stored token (edit the user doc in Raven Studio), reload
  /home → orgs still listed (silent refresh); then revoke the app authorization on GitHub, reload
  → banner appears, Reconnect completes and restores the list.

## 5. Explicitly out of scope

- Proactive background refresh of all users' tokens (cron): pointless — the token is only used
  on interactive requests; refresh-on-use covers it.
- Encrypting stored tokens at rest (Spark-wide concern, noted during investigation).
- Any change to webhook-driven `InstallationId` maintenance (already handled; see git history).
