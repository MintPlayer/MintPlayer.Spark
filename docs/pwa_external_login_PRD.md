# PRD / Plan — PWA support (#464) and external login that survives it (#490)

Status: **implemented** on `feat/464-490-pwa-external-login`; full local sweep green on 2026-10-08
(4m07s, 16 projects / 66 tasks, E2E included). Release notes:
[release-notes-preview-103.md](release-notes-preview-103.md). Manual device acceptance (§7 step 9)
not done: the owner ended the device experiments.

One PR closes #464 and #490 and everything below. Nothing here is a follow-up.

**Extension (owner request, 2026-10-08):** the work to make the IdentityProvider a full
identity-provider plugin is planned in
[identity_provider_platform_PRD.md](identity_provider_platform_PRD.md). It covers developer
sign-up, app management, API resources and granular consent. It lands in this PR (its Q0, grilled 2026-10-08).

## 1. Goals

| # | Goal | Status on master (bd7ef6a9) |
|---|---|---|
| G1 | Developers enable Facebook, X/Twitter, Google, Microsoft, LinkedIn and GitHub login **selectively**, with minimal code of their own | **Partial.** All six presets exist; each still needs code plus manual secret binding (§2.1) |
| G2 | Developers can easily **host their own identity provider** | **Hosting is done** (HR and Fleet host one). **Using it is missing:** no "sign in with my Spark IdP" preset, and the IdP's login page has no external buttons (§2.2) |
| G3 | Backend and frontend implement the proper external sign-in flow from the shipped ng-spark-auth login page | **Desktop popup only.** No fallback without an opener, provider-side cancel never reports back, errors are never shown (§2.3) |
| G4 | The site can be installed as a PWA on a phone | **Missing.** No app has a service worker or manifest (§2.4) |
| G5 | On Android with the PWA installed, "Login with …" from a Chrome tab completes in that tab, with the result handed back to the window that started it | **Broken** (#490). The design is proven on a device in the prototype (§3) |

## 2. Current state (evidence)

### 2.1 Social providers (G1)

- **Entry point:** `spark.AddAuthentication<TUser>(configure, configureIdentity, configureProviders)`, in `libs/authorization/MintPlayer.Spark.Authorization/Extensions/SparkBuilderExtensions.cs:24-58`.
- **Presets:**
  - `AddGitHub`: `Extensions/GitHubAuthenticationExtensions.cs:14-45`, callback `/signin-github`.
  - In `Extensions/SparkExternalProviderExtensions.cs`:
    - `AddSparkGoogle` :41-54
    - `AddSparkMicrosoftAccount` :60-82 (the email counts as verified only for a personal account, via `tid`)
    - `AddSparkFacebook` :87-96 (no verified-email signal, so a confirmation mail is sent)
    - `AddSparkTwitter` :103-113 (OAuth **1.0a**, through `Microsoft.AspNetCore.Authentication.Twitter`, csproj:65)
    - `AddSparkLinkedIn` :125-163 (generic OAuth handler, `/signin-linkedin`)
- **Per-provider email policy:** `SparkExternalProviderRegistration` (`Configuration/SparkExternalProviderRegistration.cs:14-28`). An app can override it via `SparkAuthenticationOptions.ExternalProviders[scheme]`.
- **The provider list is derived from the registered schemes** (`ExternalAuthenticationSchemes.cs:34-40`) and served by `GET /spark/auth/capabilities`.
- **Client side:**
  - `sign-in/src/spark-sign-in.component.ts:146-161` renders one button per server-reported scheme.
  - `sparkAuthRoutes(withExternalLogin(githubProvider(), …))` only decorates those buttons with label, icon and order (`routes/src/spark-auth-routes.ts:96-140, 311-356`).
- **Only CodeCoverage uses a provider:** `apps/CodeCoverage/CodeCoverage/Program.cs:118-198`, with hand-bound ClientId/Secret and a hand-written `OnRemoteFailure`.
- **Gaps:**
  - No configuration-driven enabling.
  - `AddGitHub` versus `AddSpark*` naming.
  - The XML doc at `SparkAuthenticationExtensions.cs:38-43` still shows raw `.AddGoogle()`, which skips the policy.
  - **No library `OnRemoteFailure`/`AccessDeniedPath`.** A cancel at the provider shows an error page inside the popup and posts nothing.
  - Only GitHub has had a live round trip.
  - X 1.0a: X's current guidance is OAuth 2.0 + PKCE (`AspNet.Security.OAuth.Twitter` 10.0.0 implements it). Email needs the `users.email` scope.
  - LinkedIn's generic handler already targets the OIDC-era `/v2/userinfo` (`r_liteprofile` is gone for apps created after 2023-08).

### 2.2 Own identity provider (G2)

- **Package:** `libs/identity_provider/MintPlayer.Spark.IdentityProvider`, hand-built with no OpenIddict.
- **App code:** `spark.AddIdentityProvider(o => { o.Issuer; o.SigningKeyPath; })` (`Extensions/SparkIdentityProviderExtensions.cs:38`), plus `IOidcApplicationContext` for the admin screens.
- **Endpoints:** `/connect/*` covers authorize, token, userinfo, jwks, discovery, consent, login, 2FA, logout, revoke and introspect. Discovery advertises `code` + PKCE S256, `refresh_token` and `client_credentials` (`Discovery.cs:48-53`).
- **Hosted by:** HR (`apps/HR/HR/Program.cs:45-49`) and Fleet (`apps/Fleet/Fleet/Program.cs:91-104`, when an issuer is configured).
- **Gaps:**
  - **No relying-party preset.** `AddOidcLogin` and `SPARK_OIDC_PROVIDERS` exist only on unmerged branches; the memory `identity-provider-progress.md` is stale on this point. So one Spark app cannot offer "Sign in with <our IdP>" with one line of code.
  - **The IdP's own login is password-only.**
    - `/connect/authorize` sends anonymous users to `/connect/login` (`Authorize.cs:118-123`).
    - That page shows no external-provider buttons.
    - It is not mapped when LocalCredentials=Disabled (`Endpoints/Oidc/Groups.cs:43-48`), so a social-only IdP redirects to a 404.
  - Whether the IdP emits `email_verified` has not been checked yet. The default Spark policy needs it (spike S6).

### 2.3 The sign-in flow (G3, G5)

**Server: challenge**
- `ExternalLoginChallenge.cs:27-60`.
- returnUrl goes through `SanitizeReturnUrl` (`SparkAuthenticationExtensions.cs:440-447`).
- `popup=1` rides on the callback URL, which becomes `AuthenticationProperties.RedirectUri` (protected inside OAuth state).
- The link variant: `LinkExternalLoginChallenge.cs:45`.

**Server: callback**
- `ExternalLoginCallback.cs:46-201` (and the link variant) funnels every exit into `ExternalLoginOutcome` (`SparkAuthenticationExtensions.cs:398-429`).
  - Popup mode emits `if (window.opener) window.opener.postMessage({...}, origin); window.close();`.
  - There is no fallback.
  - The payload is a hand-built JS literal; this is safe only because `error` is a constant (doc comment :393-396).

**Client: `externalFlow`** (`ng-spark-auth/core/src/spark-auth.service.ts:193-252`)
- Opens `window.open(url+'&popup=1', 'spark-external-login', …)`.
- Listens only to `message`, checking origin and type, with no nonce.
- Polls `popup.closed` every 400 ms and resolves `popup_closed` at once, even while the tab is hidden.
- `checkAuth()` runs only on success.
- `loginWithProvider` (:144) and `linkProvider` (:167) both go through it, so one fix covers both flows.

**Client: sign-in page**
- `spark-sign-in.component.ts:194-198` throws away `result.error`. The comment at :191 and the spec at :119 claim an error is shown; neither is true.
- **Redirect mode** exists (:202-205) but nothing uses it, and nothing reads the `?sparkExternalLogin=<code>` the server appends (:406-408).

**Security headers**
- No COOP anywhere in Spark.
- The only CSP is `frame-ancestors 'none'` on `/connect/*`. An inline callback script works today.

### 2.4 PWA (G4)

- **No app has PWA setup.** CodeCoverage, DemoApp, Fleet, HR and QnA all lack:
  - `@angular/service-worker` (it is not even in the root `package.json`)
  - `ngsw-config.json`
  - a manifest
  - icons beyond `public/favicon.ico`
- Builds are Nx `project.json` (`@nx/angular:application`, no `angular.json`).
- There is no SSR. The host serves `dist/ClientApp/browser` through `UseSpaStaticFilesImproved`; in Development it uses `UseAngularCliServer`.
- **Consequence:** with the schematic's `enabled: !isDevMode()`, the service worker only exists in production builds, so every PWA check needs a production build served by the host.
- ASP.NET Core already maps `.webmanifest` to `application/manifest+json`.
- Angular is 22.2.0, which is above the CVE-2026-50169 fix (22.0.0-rc.2).

## 3. Platform facts that shape the design

From the prototype `C:\Repos\aa\pwa_window_open` (`PLAN.md`), measured on Android 10 with Chrome 154 and a WebAPK, live at https://pwa.mintplayer.com. Plus web research done on 2026-10-08.

| # | Fact | Evidence |
|---|---|---|
| F1 | Android capture is a pure **scope path-prefix** match. Every `window.open` variant into scope is captured (`popup`, `noopener`, `noreferrer`, `about:blank`, `<a target=_blank>`) | Device, exp A–D, G, H |
| F2 | No manifest switch prevents capture. `capture_links` is dead, `handle_links` never shipped, `launch_handler` only picks a client, `scope_extensions` only widens. w3c/manifest#989 is still open | Research |
| F3 | An in-scope OAuth callback is captured even when the popup started out of scope | Device, exp J |
| F4 | The WebAPK shares Chrome's profile: BroadcastChannel, the `storage` event and the SW relay all reach the Chrome tab, and the captured popup can `window.close()` itself on request | Device, exp A/J |
| F5 | A captured popup **replaces** the open PWA's page. After `window.close()` the PWA is only backgrounded and still shows the callback page; `location.replace(app)` on re-show fixes that | Device, exp L |
| F6 | **COOP cuts the opener on desktop too.** Facebook `login.php`, X `i/flow/login` and LinkedIn `/login` enforce `Cross-Origin-Opener-Policy: same-origin-allow-popups`. A popup that navigates into such a page gets a new browsing-context group, so the opener reads `popup.closed === true` while the tab stays **visible**, and the callback has `opener === null`. Google sends `same-origin` only as Report-Only (it could enforce at any time). Microsoft and GitHub send none | curl of the headers 2026-10-08, plus the WPT `popup-redirect-same-origin-allow-popups` test. **Not yet observed end to end: spike S1** |
| F7 | Desktop Chrome (M134+) captures navigations into installed PWAs, but not `window.open` with an opener from a normal tab. From **Chrome 151**, `window.open` triggered *inside* a desktop PWA to an in-scope URL can be captured | blink-dev PSAs 2025-04-02 and 2026-06-25 |
| F8 | iOS home-screen apps share **no** storage or cookies with Safari. iOS does not capture Safari popups into the app, but a popup or redirect *from inside* the standalone app is unreliable. Firebase and MSAL recommend redirect login in standalone or embedded contexts | Research, not device-checked: spike S3 |
| F9 | ngsw answers GET navigations without a dot with `index.html`, so it would swallow `/signin-github?code=…` and `/spark/auth/external-login*`. Setting `navigationUrls` **replaces** the defaults, so the defaults must be restated | angular.dev SW config docs |

**Experiment M/N, first run (2026-10-08, build #11 `d58dcf3`, Firefox 157 on Android 16, not Chrome):**

| Id | Finding | Evidence |
|---|---|---|
| F6a | COOP **does** sever the opener. The callback after the COOP stand-in page had `opener missing`, yet the hand-off still delivered over BroadcastChannel and `storage`. The #490 rule was "ok" here: on a phone the popup is a separate tab, so the main tab stays hidden until the user returns | M1 |
| F10 | Firefox Android: every in-scope `window.open` (A, B, G, H, D, L) returned **no handle** and nothing arrived; M2 and J (in-scope callback) never delivered; `about:blank`-then-navigate (C) escaped and worked with an opener. Whether the captured pages ever ran **Send message** still needs confirming | A–L, M2 |
| F11 | `window.open('https://www.facebook.com/login.php')` returned **no handle** (N1 "does nothing"). That points to the native Facebook app (or Firefox's app-link handling) taking the URL. Native provider apps may capture the provider step itself | N1 |
| — | N4/N5/N6 used account or landing URLs rather than OAuth authorize URLs, so they only measure `handle.closed`, not a round trip (the experiment spec's mistake) | N4–N6 |

**Experiment M/N, second run (2026-10-08, build #11): desktop Chrome 154, Edge 154 and Firefox 157 on Windows, plus Chrome 154 on Android 10.**

| Id | Finding | Evidence |
|---|---|---|
| **F6 verified** | On **all three desktop browsers** the COOP stand-in severs the opener while the main tab stays **visible**: `handle.closed` after 0.5–2.5 s, the message after 3.0–5.8 s. **#490's rule gives a FALSE `popup_closed` in every browser** (Chrome at 2.0 s, Firefox at 2.2 s, Edge at 4.0 s). The hand-off still delivers over BroadcastChannel (+ storage) | M1 in Chrome, Edge, Firefox |
| F6 real providers | The real X and LinkedIn login pages flip `handle.closed` within 0.9–5.3 s while the tab is visible, in all three desktop browsers. With #490's rule, every signed-out X or LinkedIn popup login would be cancelled | N2, N3 |
| F12 | Chrome Android M2 (COOP page, then the in-scope callback captured into the PWA): the payload **was delivered** (BroadcastChannel, storage, SW relay), but the captured PWA page **did not close** after the BroadcastChannel request, so the user stays in the PWA. Earlier, J (the same flow without COOP) closed fine. Whether `window.close()` was refused or the request never arrived is **unconfirmed**; the popup's status line answers it | M2 Chrome Android |

**Third run (2026-10-08):**
- **Desktop Chrome M1:** after the COOP switch, the popup **closed itself** on the BroadcastChannel request. So `window.close()` is still allowed on desktop, which answers that part of S1.
- **Firefox Android M1:** delivered (BroadcastChannel + storage, opener missing), and the popup **closed itself**. The hand-off and self-close work in the Firefox browser. Only pages **captured into the installed app** are cut off.
- **Firefox Android M2:** nothing was delivered on any channel, and the captured page did not close (no request ever reached it).

Together with F10, this means Firefox's installed web app shares **no** BroadcastChannel or storage with the Firefox browser tab, and probably no cookies either (assumption: the owner pressed Send in the captured page).

**Consequence:** Spark's challenge URL `/spark/auth/external-login` is in scope. On Firefox Android with the app installed, the popup flow behaves like experiment A there:
- `window.open` returns `null`, which today is reported as `popup_blocked`;
- no hand-off is possible.

**Candidate (spike S8):** treat a `null` handle as "use redirect mode" instead of `popup_blocked`. That only works if the null handle did **not** also start the flow inside the installed app; otherwise the user gets two flows. It also needs a full-page redirect in a Firefox tab whose in-scope callback is **not** captured.

**Experiments closed by the owner (2026-10-08): no further device runs.** The remaining unknowns are settled by designs that work whichever way they fall:
- **F12, the captured page in Chrome Android does not close.** Two possible causes: the close was refused, or the request never arrived. Do **both** remedies:
  - in standalone mode, the callback calls `window.close()` right after writing the channels, and again on the acknowledgement;
  - it always shows the static fallback "Signed in — switch back to your browser" with a Close button.

  The done-marker restore (F5) still cleans the page up when the app is reopened.
- **F10, the Firefox Android installed app shares nothing.** There is **no automatic fallback**. Automatically redirecting on a `null` handle risks a second flow inside the app, and that was never measured.
  - When `window.open` returns `null`, the sign-in page reports the error and offers **"Continue in this tab"**, which retries the same provider in redirect mode.
  - That is a user-initiated fallback, and it also helps people whose popups are genuinely blocked.
  - Firefox Android with the web app installed is documented as a known limitation in `guide-pwa.md`.
- **S2, S3 and S8 are dropped**:
  - D3 (redirect when standalone) ships on the strength of Firefox and MSAL's guidance;
  - iOS standalone works through redirect or is documented as unverified;
  - Firefox Android is covered by the limitation note above.

**D2 is settled by evidence: `popup.closed` is a hint, not an outcome** (see D2 final: the poll drives UI only, listeners stay attached).
Spike S1 is answered for the opener and `closed` questions. Still open: whether the desktop callback closes itself after the COOP switch, and F12's cause.

**Consequence of F6:** the #490 rule "don't resolve `popup_closed` while hidden, then 1.5 s grace" is not enough. On desktop, a user who is signed out at Facebook, X or LinkedIn would get `popup_closed` while they are still typing their password. `popup.closed` is therefore **not authoritative** in any mode.

## 4. Design

### D1 — Channel hand-off (G5, proven: port from the prototype)

**Nonce**
- `externalFlow` generates a nonce (`crypto.getRandomValues`, base64url, 32 chars) and sends `?popup=1&nonce=<n>`.
- The challenge endpoints (login and link) validate it against `^[A-Za-z0-9_-]{16,64}\z` through one shared helper:
  - invalid → 400 `invalid_nonce`
  - missing → today's behaviour
- The nonce is forwarded on the callback URL, exactly like `popup`.

**Payload**
- `{type:'spark:external-login', success, error, nonce}`, serialized with `System.Text.Json` (its default encoder escapes `< > & '`). This replaces the JS literal.
- The restore URL is the sanitized returnUrl, encoded the same way.
- This follows the repo rule against splicing caller data into script.

**Callback page** (`ExternalLoginOutcome`, popup branch; shared by login and link)
1. If there is an `opener`, call `postMessage(msg, location.origin)`. Also write the channels below: under F6 the opener may be gone even on desktop. Close only once acknowledged.
2. Without an opener:
   - write `localStorage['spark:external-login:<nonce>']` (with `at`)
   - post on `BroadcastChannel('spark:external-login')`
   - on `{type:'spark:external-login-ack', nonce}`, set the done-marker `spark:external-login-done:<nonce>` and call `window.close()`
3. **Restore:** on load, or on `visibilitychange` → visible, when the done-marker exists, clear it and call `location.replace(returnUrl)` (F5).
4. **Visible fallback** if `close()` is refused or no ack arrives within ~3 s: show "Signed in, you can close this window and return to your browser" plus a Close button. The page is static HTML, with no SPA bundle.

**Client** (`externalFlow`, popup mode)
- Listen on four sources:
  - `message` (origin, type and **nonce**)
  - the BroadcastChannel
  - the `storage` event for the key
  - a re-read of the key on `visibilitychange`/`focus` (the background tab may be frozen)
- On the first delivery:
  - post the ack
  - write the done-marker
  - remove the key
  - `handle.close()` if a handle exists
  - then `csrfRefresh()` + `checkAuth()`, as today
- Every listener and the channel are torn down on settle.

### D2 (final, 2026-10-08) — legacy MintPlayer's pattern: the closed-poll drives UI only

Supersedes the waiting-state and Cancel-button text further down.

Legacy MintPlayer (`base-login.component.ts:47-52`) polls `authWindow.closed` with `setInterval`. When it reads closed, it only sets `isOpen = false` and clears the timer; the result listener stays attached. Spark does the same:
- **The poll** (400 ms) only flips a `pending` signal on `SparkAuthService` (`externalLoginPending`). The sign-in page uses it to disable and re-enable the buttons and show a spinner. It **never settles** the flow and **never removes** listeners.
- **The listeners** (message, BroadcastChannel, storage, the visibility re-read) stay attached until one of these happens:
  - a result arrives → settle with it;
  - a new external sign-in starts → the old attempt settles `popup_closed` and is torn down;
  - 10 minutes pass → settle `popup_closed`.
- **No Cancel button and no false error.** Under COOP (F6) the buttons simply re-enable early, and a late result still signs the user in.

**Note for direct API callers:** `loginWithProvider()`/`linkProvider()` settle only when a result arrives, another attempt starts, or the 10-minute timeout fires. Consuming the promise with `.then(...)` or `await` is the same and never blocks. But **button and spinner state must come from `externalLoginPending`, not from the promise settling**: under COOP the popup can look closed long before the result arrives. Document this in the README.

### D2 (superseded) — `popup.closed` is a hint, not an outcome (G3/G5, new; driven by F6)

**Recommendation** (needs owner approval and spike S1):
- `popup.closed` moves the flow into a **waiting** state; it does not settle it.
- The flow settles when:
  - a message arrives,
  - the caller cancels (`externalFlow` takes an `AbortSignal`), or
  - a long timeout passes (default 10 min) → `popup_closed`.
- The sign-in page shows "Waiting for <Provider>… [Cancel]" while a flow is pending, so a genuine close costs the user one click.
- **Rejected alternative:** resolve `popup_closed` after a 1.5 s grace whenever the tab is visible. Under F6 that cancels every signed-out Facebook, X or LinkedIn login on desktop.
- **Fallback if S1 shows that F6 does not trigger in practice:** keep the #490 rule (hidden → wait for visible, then 1.5 s grace).

### D3 — Inside the installed app: redirect, not popup (G4/G5, new)

- When `matchMedia('(display-mode: standalone)').matches` or `navigator.standalone`, the shipped sign-in and account pages use **redirect mode**.
  - Covers Android: inside the PWA, a popup lands in a Custom Tab whose opener is unknown.
  - Covers iOS, per F8.
  - Covers desktop Chrome 151+ capturing `window.open` from inside the PWA, per F7.
- **Redirect mode must become first-class:**
  - The sign-in page reads `?sparkExternalLogin=<code>` on return, shows the error and strips the parameter.
  - The account page does the same for linking.
- **API:** `provideSparkAuth({ externalLoginMode: 'auto' | 'popup' | 'redirect' })`, with `auto` as the default (popup in a browser tab, redirect when standalone).
- Spike S2 (Android) and S3 (iOS) confirm the redirect returns into the app with the cookie set.

### D4 — Provider-side cancel and failure report back (G3)

- Every Spark preset gets `OnRemoteFailure` (`access_denied`, user cancel, correlation failure). It routes into `ExternalLoginOutcome` with a new constant `remote_failure` (`access_denied` keeps `no_login_info`), reading `popup`/`nonce`/`returnUrl` from the failure's `AuthenticationProperties.RedirectUri`. It then calls `HandleResponse()`.
- The CodeCoverage hand-written `OnRemoteFailure` (`Program.cs:118-198`) is replaced by the library's, keeping any app-specific part as a wrapper.

### D5 — Selective providers from configuration (G1)

**Code path:** one call enables every provider that has credentials in configuration.

```csharp
spark.AddAuthentication<SparkUser>(…, providers => providers.AddSparkExternalProviders(configuration));
```

```json
"Spark": { "Auth": { "Providers": {
  "GitHub":   { "ClientId": "…", "ClientSecret": "…" },
  "Google":   { "ClientId": "…", "ClientSecret": "…" }
} } }
```

- A provider with no section, or with an empty ClientId, is **not registered**. The capabilities endpoint, and so the buttons, follow automatically.
- Per-provider code hooks remain: `AddSparkExternalProviders(configuration, o => o.GitHub(g => g.Scope.Add("read:org")))`.
- The individual presets stay public for code-only setups.
- **Rename `AddGitHub` → `AddSparkGitHub`** for consistency. There is no backward compatibility, so this is a minor version bump with a release note.
- **Fix the XML doc** at `SparkAuthenticationExtensions.cs:38-43`.
- **Client:** `withExternalLogin()` with no arguments already falls back to server order and the server's display name. Add icons for all six presets by default, so the minimal app writes **no** client code beyond `withExternalLogin()`.

### D6 — X on OAuth 2.0 (G1)

Move `AddSparkTwitter` from 1.0a to OAuth 2.0 + PKCE.

- **Option A (recommended):** a generic `OAuthHandler`, like LinkedIn already uses. No new dependency; fits [[feedback_prefer_aspnet_identity_over_third_party]].
- **Option B:** `AspNet.Security.OAuth.Twitter` 10.0.0 (net10 only so far).

**Email:** request `users.email` and use `confirmed_email`. The policy stays NoSignal (confirmation mail), unless X documents `confirmed_email` as verified.

### D7 — Own IdP: usable as a provider, and federating (G2)

1. **Relying-party preset.** Uses `Microsoft.AspNetCore.Authentication.OpenIdConnect`: code flow, PKCE, `email_verified` honoured by the policy.
   ```csharp
   providers.AddSparkOpenIdConnect("MintPlayer", "MintPlayer ID", o => { o.Authority = "…"; o.ClientId = "…"; o.ClientSecret = "…"; });
   ```
   - Also bindable through D5: `Providers:OpenIdConnect:<scheme>:{Authority,ClientId,ClientSecret,DisplayName}`.
   - Client: `oidcProvider('MintPlayer', 'MintPlayer ID', iconClass?)`.
2. **Federation on the IdP's login page.**
   - `/connect/login` lists the host's external schemes, the same list as `/spark/auth/capabilities`, as buttons.
   - They run the challenge in **redirect mode**, with returnUrl = the pending `/connect/authorize?...`.
   - With LocalCredentials=Disabled, the page is still mapped and shows only the buttons. This fixes the 404.
3. **Spike S6:** HR as the IdP, DemoApp as the RP, all locally. It confirms discovery, PKCE, `email_verified` and the userinfo claims against the stock OIDC handler.

### D8 — PWA for the apps (G4)

- Add `@angular/service-worker@22.2.0` to the root `package.json`.
- Per PWA-enabled app:
  - run `npx nx g @angular/pwa:ng-add --project=<app>`, then hand-check the output (spike S4: the schematic assumes `angular.json`)
  - fix `project.json` (`serviceWorker` in the production configuration)
  - add the icons, `manifest.webmanifest` (`id`, `start_url: "/"`, `scope: "/"`, `display: standalone`, theme colours matching the existing `theme-color` metas) and `apple-touch-icon`
  - `provideServiceWorker(… registerWhenStable:30000)`
- **`ngsw-config.json` `navigationUrls`**, with the defaults restated (F9):
  ```json
  ["/**", "!/**/*.*", "!/**/*__*", "!/**/*__*/**",
   "!/spark/**", "!/signin-*", "!/signout-*", "!/connect/**", "!/.well-known/**"]
  ```
  CodeCoverage adds `!/api`, `!/api/**`, `!/badge`, `!/badge/**`, `!/health` and `!/health/**` (S4: `/**` alone misses the bare path). Add no `dataGroups` for `/spark/**`.
- `bs-theme-preboot.js` goes in the prefetch asset group.
- **Belt and braces:** the challenge URL and the callback `RedirectUri` carry `ngsw-bypass=true`. The provider's `/signin-*` redirect cannot carry it, hence the exclusion above.
- **Host:** serve `ngsw-worker.js` and `ngsw.json` with `Cache-Control: no-cache`. One Spark helper (in `UseSpaStaticFilesImproved` or a Spark extension) applies it in every app.
- **Guard test:** a Node test, like `tools/verify-coverage-paths.test.mjs`, asserts that every `apps/**/ngsw-config.json` contains the Spark exclusions.
- **Docs:** `docs/guide-pwa.md`, covering the steps, the exclusions and why.
- **Which apps:** owner decision Q1.

### D9 — Errors visible on the sign-in page (G3)

- `signInWith` shows `result.error` through the existing alert pattern, translated.
- So does a `?sparkExternalLogin` code on load (D3).
- The false spec at `sign-in/...spec.ts:119` is fixed to actually assert the message.

### D10 — Headers

- Spark never sends `COOP: same-origin` on SPA or auth pages. Document this in `guide-pwa.md`, with a test asserting that the callback response carries no COOP.
- No CSP change. If one is ever added, the callback page needs a nonce'd script; note it in the code comment.

## 5. Spikes (before or alongside implementation)

| Id | Question | How | Blocks |
|---|---|---|---|
| S1 | Desktop Chrome, Edge, Firefox and Safari, popup from a normal tab, **signed out** at Facebook, X or LinkedIn: does `popup.closed` flip early? Is the opener null at the callback? Is `window.close()` still allowed? Is Google's COOP still report-only? | Add a "COOP" experiment to the prototype (popup → a page sending COOP `same-origin-allow-popups` → back to our origin), plus one real Facebook or LinkedIn test app | D2 (choice between long wait + Cancel and the 1.5 s grace) |
| S2 | Android, **inside** the WebAPK: where does `window.open` (in scope) land, and is there an opener? With redirect mode: does the provider render inside the app or a Custom Tab, and does `/signin-*` return into the WebAPK with the cookie? | Prototype + a Spark app deployed with D8 | D3 |
| S3 | iOS 17/18/26 home-screen app: does redirect login work end to end? A popup from a Safari tab? | Owner device | D3 (iOS scope) |
| S4 | Does `nx g @angular/pwa:ng-add` work on an Nx `project.json` app? Does the production build served by the ASP.NET host register the SW, and does `ngsw-bypass` plus the exclusions keep auth navigations on the network? | Local, Fleet production build + Playwright | D8 |
| S5 | End-to-end #490 on the owner's Android device with the real Spark implementation: Chrome tab → GitHub → captured callback → Chrome tab signed in → PWA reopened shows the app | Owner device, after M5 | Acceptance |
| S6 | Spark IdP as an upstream for the stock OpenIdConnect handler (`email_verified`, PKCE, userinfo) | Local: HR as IdP, DemoApp as RP | D7 |
| S8 | Firefox Android with the web app installed: when `window.open(in-scope)` returns `null`, does anything open in the installed app? Does a full-page redirect in the browser tab (tab → provider → in-scope `/signin-*`) stay in the tab and sign it in? | Prototype: add an experiment R (same-tab redirect via the COOP stand-in to an in-scope callback) | Firefox-Android support in D3 |
| S7 (info only) | Samsung Internet and Edge-installed PWAs on Android: they share nothing with a Chrome tab | Optional | none: documented limitation |

## 5a. Spike results

### D8 spike S4 result (2026-10-08, Fleet `@spark-demo/fleet-demo`, Angular 22.2.0, Nx 23.2.1)

**Don't use the `@angular/pwa` schematic.** It fails on the `@nx/angular:application` executor (`getMainFilePath` reads `options.main` → `Path "undefined" does not exist`). Forced through a temporary executor swap, Nx's angular.json shim rewrote **every** app's project.json. M5 hand-writes per app:
- `configurations.production.serviceWorker: "<projectRoot>/ngsw-config.json"` in project.json
- `ngsw-config.json`
- `public/manifest.webmanifest` (real name/short_name, `id`, `start_url`/`scope` `"/"`, theme colours) and icons
- `<link rel="manifest">` + `<noscript>` in index.html
- `provideSparkServiceWorker()`

**Production build verified:**
- emits ngsw-worker.js, ngsw.json, safety-worker.js and manifest.webmanifest
- the navigationUrls list sends `/spark/auth/external-login`, `/signin-github?code=x`, `/connect/authorize`, `/.well-known/*` and `/signout-oidc` to the network, and serves `/po/car/123` from the SW
- `?ngsw-bypass` works

**Correction:** `!/health/**` does not exclude the exact `/health`. CodeCoverage adds `!/health`, `!/api`, `!/badge` as well as the `/**` forms.

**appData: don't stamp a commit sha.**
- In ngsw-config.json it busts the Nx cache on every commit.
- Patched into ngsw.json after the build, it makes every client see an update.

Use the ng-spark package version (it moves only on release) or nothing. Revises Q1b's "CI writes appData.build".

**no-cache for ngsw-worker.js/ngsw.json/safety-worker.js:**
- **The trap:** `UseSpaStaticFilesImproved` is the external `MintPlayer.AspNetCore.SpaServices` 10.7.1 package, and runs before `UseSpark`. A step inside `UseSpark` would therefore run too late.
- **Where it goes:** one Spark `IStartupFilter` (pattern: `libs/spark/MintPlayer.Spark/Services/SparkForwardedHeaders.cs`) setting `Cache-Control: no-cache` in `OnStarting` for those three paths.
- The `.webmanifest` MIME type is already correct by default.

**Nx:** ngsw-config.json is under projectRoot, so it is already a build input; `/bs-theme-preboot.js` is listed in the prefetch group. Unexplained: `worker-basic.min.js` in the output. Find out who ships it and exclude it from prefetch.

**Not covered:** registration under the real ASP.NET host, and Playwright checks on auth navigations. These move to M7.

### D7 spike S6 result (code read 2026-10-08; live run happens in M7)

Compatible already:
- `sub` = user id (stable)
- PKCE S256 required per client
- nonce echoed in the id_token
- client_secret_post (the stock handler's default)
- RS256 + kid
- single `aud`
- exact-match redirect URIs
- `SanitizeReturnUrl` accepts `/connect/authorize?...`

M6 must fix in the IdP (paths under `libs/identity_provider/MintPlayer.Spark.IdentityProvider/`):
1. **Logout with the stock handler:** `/connect/logout` needs `client_id` to honour `post_logout_redirect_uri` (`Endpoints/Logout.cs:315-324`); the stock handler sends `id_token_hint`. Derive the client from `id_token_hint`'s `aud` (validate the signature). The preset also sets `ProtocolMessage.ClientId` on sign-out, as belt and braces.
2. **`/connect/login`** (`Endpoints/Login.cs:101-164`, StringBuilder HTML):
   - move the GET into `OidcConnectGroup`, so it stays mapped when LocalCredentials=Disabled (`Endpoints/Oidc/Groups.cs:43-48`)
   - render the password form only when not Disabled; POST and 2FA stay gated
   - add buttons from `ExternalAuthenticationSchemes.GetInteractiveAsync` linking to `/spark/auth/external-login?provider=X&returnUrl=<pending authorize url>` (redirect mode); HTML-encode everything
3. **External-login refusal codes are lost:** the callback appends `sparkExternalLogin=<code>` to `/connect/authorize?...`, and Authorize drops it on the bounce to `/connect/login`. Forward it, and show it on the login page.
4. **Scopes:** none are seeded and claims come only from `OidcScope.ClaimTypes` (`Services/OidcTokenGenerator.cs:160-197`). Document, and seed in HR, `profile`=[name, preferred_username, given_name, family_name] and `email`=[email, email_verified]. Map given_name/family_name, which are dropped today (`:192-196`).
5. **Discovery** (`Endpoints/Discovery.cs:38-57`): add `response_modes_supported: ["query"]` and `claims_supported`. The preset sets `ResponseMode = "query"`, since the IdP never does form_post (`Services/OidcAuthorizationFlow.cs:419-421`).
6. **Consistency:** `email_verified` is a string in the id_token (`:182`) but a bool in userinfo (`:235`); make it a bool in both. Roles are `role` in the id_token but `roles` in userinfo (`:239`); pick `role` in both.
7. **Not emitted:** `at_hash`, `auth_time`, `azp`. Add `at_hash` (the stock validator tolerates its absence for code flow; confirm live) and `auth_time`.
8. **id_token lifetime** reuses `AccessTokenLifetimeMinutes` (`Endpoints/Token.cs:243`). Give it its own `IdTokenLifetimeMinutes` (default 5).

DemoApp RP registration in HR (for M6/M7):
- confidential, grants [authorization_code]
- RedirectUris [`https://localhost:<port>/signin-<scheme>`]
- PostLogoutRedirectUris [`https://localhost:<port>/signout-callback-oidc`]
- scopes [openid, profile, email]
- ConsentType implicit + AutoApproveImplicitConsent

### M6 result (2026-10-08; built, tests written, sweep in M7)

- All eight S6 gaps fixed in the IdP. Logout accepts `id_token_hint` (own key + issuer, expiry ignored, a token with `scope` refused, a hint contradicting `client_id` refused). `/connect/login` GET lives in `OidcConnectGroup`, and only POST and two-factor stay gated. The federation buttons carry `returnUrl` and `errorUrl`. `Authorize` also lifts a stray `sparkExternalLogin` out of the pending URL onto the login redirect.
- `given_name`/`family_name` come from `SparkUser.Claims` (OIDC name or `ClaimTypes.GivenName`/`Surname`), and are emitted only when stored. `SparkUser` has no name fields.
- `auth_time` is the cookie ticket's `.spark.authenticated_at`, falling back to `IssuedUtc`. It is carried request → code → refresh token.
- `IdTokenLifetimeMinutes` (default 5) is on `OidcApplication`, beside `AccessTokenLifetimeMinutes`. It is not a provider-wide option, because the access-token lifetime is per client too.
- `errorUrl` on both challenges and in `SparkExternalLoginRemoteFailure`. Redirect-mode failure → `errorUrl` + `sparkExternalLogin`; success → `returnUrl`.
- **RP = QnA, not DemoApp.** DemoApp has no authentication, and Fleet also hosts an IdP.
  - QnA config is `Spark:Auth:Providers:OpenIdConnect:HR` in `appsettings.Development.json`, plus `spark.AddExternalProviders`. E2E runs as environment `E2E`, so it is unaffected.
  - HR seeds `openid`/`profile`/`email` and the `qna` client through migration `M_202610081200_QnARelyingParty`, in Development only. The secret is a published dev-only constant.
  - Redirect URIs are `/signin-HR` and `/signout-callback-HR`, with the scheme's casing, because the IdP compares ordinally.
  - HR's issuer fallback was `https://localhost:5002`, a port HR never listens on. It is now `:5005`.

### M0 result (80e22e1c)

- Nested entry points work: `@mintplayer/ng-spark/auth/<name>`, plus a root `@mintplayer/ng-spark/auth` re-exporting `provideSparkAuth`/`withSparkAuth`/`sparkAuthClientMethods` (the apps import the bare name).
- Fleet, HR and QnA now depend on `@mintplayer/ng-spark`; DemoApp never used auth. The SPARK030 default package is `@mintplayer/ng-spark`.
- **Release-note item (M8):** the generated, gitignored `spark-auth.setup.ts` is never overwritten, so every existing consumer copy still imports `@mintplayer/ng-spark-auth` and breaks the build. Either the generator rewrites a stale import, or the release notes tell people to delete the file.
- **Owner:** `CLAUDE.md` still names `@mintplayer/ng-spark-auth` in the versioning section.
- Kept on purpose: log prefixes `[ng-spark-auth]` (no behaviour change in M0) and test fixtures modelling the old two-package layout.

## 6. Owner decisions (grilled 2026-10-08)

- **Q1 — decided: all five apps** (CodeCoverage, DemoApp, Fleet, HR, QnA). CodeCoverage is the only deployed app with a real provider (GitHub), so S5 runs there; Fleet carries S4 and E2E.
  - Production safeguards:
    - the guard test
    - no-cache on `ngsw-worker.js`/`ngsw.json`
    - S4 against a CodeCoverage production build before merge
    - a kill-switch section in `guide-pwa.md` (ship `safety-worker.js` as `ngsw-worker.js`)
- **Q1b — decided: one `provideSparkServiceWorker()` in a new entry point `@mintplayer/ng-spark/pwa`.**
  - It wraps `provideServiceWorker('ngsw-worker.js', { enabled: !isDevMode(), registrationStrategy: 'registerWhenStable:30000' })` plus the update policy:
    - `VERSION_READY` → the next router navigation becomes a full page load of its target
    - `unrecoverable` → immediate reload
    - `checkForUpdate()` when stable, every 6 h, and on `visibilitychange` → visible
  - **No hidden-tab reload:** ng-spark has no unsaved-changes guard (no `canDeactivate`/dirty tracking) to consult, so it is not safe.
  - `@angular/service-worker` becomes an **optional** peer of `ng-spark`, imported only by `/pwa`.
  - **Replaces legacy MintPlayer's `app.component.ts:88-100`,** which used the removed `available`/`activated` API and reloaded immediately (losing open edits).
  - **Legacy's `"version": 701` (`ngsw-config.json:4`) is not carried over.** Update detection uses the content hashes in the generated `ngsw.json`, so it is unnecessary. A human-readable build goes in `appData.build`, set to the ng-spark package version, never a commit sha (S4), and the footer can show it via `VersionReadyEvent.latestVersion.appData`.
- **Q6 — decided: `ng-spark-auth` merges into `ng-spark` in this PR, keeping its granularity.**
  - All 15 entry points (account, auth-bar, confirm-email, core, forgot-password, guards, interceptors, login, models, pipes, register, reset-password, routes, sign-in, two-factor) move to nested secondary entry points `@mintplayer/ng-spark/auth/<name>`.
  - `libs/node_packages/ng-spark-auth` is deleted, and CI stops publishing it.
  - The `SPARK_AUTH_STATE` and shell-slot seams stay, so an app without auth pulls no auth code.
  - All five apps' imports are rewritten.
  - **The first commit is a pure `git mv` plus path rewrite** with no behaviour change, so history and review survive.
  - **Fallback** if ng-packagr/Nx refuses nested entry points: flat `@mintplayer/ng-spark/auth-<name>`.
  - **Owner action after merge:** `npm deprecate @mintplayer/ng-spark-auth "Moved to @mintplayer/ng-spark/auth/*"`.
- **Q2 — RESOLVED 2026-10-08:** the closed-poll only drives the button state, as in legacy MintPlayer; the listeners stay attached (D2 final). There is no Cancel button. Experiment M/N showed why: the closed-poll gave a false `popup_closed` with the tab visible in desktop Chrome, Edge and Firefox. #490's grace rule gave a false `popup_closed` in desktop Chrome, Edge and Firefox (§3, second run). Original decision text follows.
- **Q2 — decided: measure first.** Experiment M/N goes into the prototype (spec:
  `~/.claude/pending-plans/pwa-window-open-experiment-M.md`):
  - a stand-in IdP page served with COOP `same-origin-allow-popups`
  - real provider login pages
  - a 400 ms closed-poll that simulates #490's rule
  - a build stamp in the page footer

  Outcome:
  - **If `handle.closed` flips while the tab is visible, before the message:** D2 as written (a hint, Cancel button, 10-minute timeout).
  - **Otherwise:** #490's rule (hidden → visible → 1.5 s grace).

  Evidence: the prototype's `main.js` has no closed-poll, and its `/out/idp.html` (via httpbin) sends no COOP, so J/K never covered this.
- **Q3 — decided: generic `AddOAuth` (owner preference; no third-party package).** Details:
  - PKCE on
  - a `BackchannelHttpHandler` adds the Basic client-auth header on the token request (X's requirement for confidential clients; to be confirmed in the manual round trip)
  - `GET /2/users/me?user.fields=confirmed_email`
  - the policy stays NoSignal
  - the M4 manual round trip uses a real X developer app, or the PR states it is untested
- **Q4 — decided: both the relying-party preset and IdP federation are in scope (D7).**
- **Q4b — decided: every preset moves to `ISparkBuilder`, and the `configureProviders` parameter of `AddAuthentication` is deleted** (no backward compatibility; minor bump):
  - `spark.AddGitHub/AddGoogle/AddMicrosoftAccount/AddFacebook/AddTwitter/AddLinkedIn/AddOpenIdConnect/AddExternalProviders(configuration)`
  - `spark.AddExternalScheme(scheme, policy)` declares a raw ASP.NET handler
  - each method throws when `AddAuthentication` was not called first
  - a registered remote scheme with no Spark declaration **throws at startup** (recommended; owner did not object)

  This supersedes the `AddSparkGitHub` rename and `AddSparkOpenIdConnect` in D5/D7.
- **Q5 — decided: `Spark:Auth:Providers`.** It matches the existing `Spark:Auth:PublicBaseUrl` (`SparkAuthLinkBuilder.cs:67`) and `Spark:Auth:AllowUnconfirmedRegistration` (`LocalCredentialEndpointFilter.cs:263`). Shape:
  - social providers are keyed by fixed scheme
  - OIDC providers sit under `OpenIdConnect:<scheme>:{DisplayName,Authority,ClientId,ClientSecret}`
  - an **unknown key throws at startup**

## 7. Milestones (one PR, tests batched at the end)

**Done** (all on `feat/464-490-pwa-external-login`):
- ✅ M0 — 80e22e1c (pure move), 60437f57 (ng-spark 22.31.0)
- ✅ M1 — ff5a000f
- ✅ M2 — 3226e2e3
- ✅ M3 — 2c66ede6
- ✅ M4 — 72f3d32f
- ✅ M5 — d093a07f, e85ffb87, b0785864
- ✅ M6 — 2a43a6e9, dc0b32a9
- ✅ M7 — 190748a0, e2646389 (sweep green, 4m07s); the planned no-opener E2E in the commit "E2E: external-login hand-off without an opener (#490)": `QnA/ExternalLoginHandoffBrowserTests` (2/2 green, all 37 QnA E2E tests green). It runs on QnA, not Fleet: QnA's sign-in page already declares the `HR` provider, and `QnATestHost` now configures that scheme with a dummy authority that is never contacted. The callback page is opened with `window.open(url, '_blank', 'noopener')`, because Chromium ignores `window.close()` in a tab the browser opened (`history.length == 2`, measured), so the ack's close could not be asserted there. The SW-exclusion check against a production build was not added; the Node guard covers the `ngsw-config` exclusions.
- ✅ M8 — the "Release notes and versions for preview.103" commit
- ✅ Login page lists external providers (owner request) — the commit "Login page lists external providers via a shared spark-external-login-buttons component (#490)": the provider block of `SparkSignInComponent` moved into `<spark-external-login-buttons>` (`@mintplayer/ng-spark/auth/external-login`), hosted by the sign-in and login pages; concurrent `capabilities()` calls share one request; `takeExternalLoginResult()` answers once. Affected vitest specs 178/178 green, lib/spec `tsc` and `nx build @mintplayer/ng-spark` clean
- ⏹ Manual acceptance (step 9) — not done; the owner ended the device experiments

The milestones below still refer to files by their current `ng-spark-auth/...` paths. After M0 they
live under `ng-spark/auth/...`.

0. **M0 Library merge (Q6):**
   - `git mv` all `ng-spark-auth` entry points to `ng-spark/auth/<name>`
   - rewrite the imports in libs and the five apps
   - drop the package from CI publish
   - pure move, its own commit, type-checked; no behaviour change
1. **M1 Server hand-off:**
   - nonce helper and validation (login and link challenge)
   - JSON payload
   - the new callback page (D1)
   - `remote_failure` and the preset `OnRemoteFailure` (D4)
   - `ngsw-bypass` on the challenge and callback URLs
2. **M2 Client hand-off:**
   - `externalFlow` with the nonce, the four channels, the ack, the done-marker, the D2-final `externalLoginPending` signal (the poll drives UI only), supersede-on-new-attempt + 10-min timeout, and teardown
   - the `externalLoginMode` option and standalone detection (D3)
   - model changes (`nonce`, new codes)
3. **M3 Shipped pages:** sign-in and account pages get the error display (D9), `?sparkExternalLogin` handling and buttons disabled with a spinner while `externalLoginPending` is set (no Cancel button).
4. **M4 Providers:** `AddSparkExternalProviders(configuration)`, the `AddSparkGitHub` rename, X on OAuth 2.0, default icons for all six (D5, D6). Migrate CodeCoverage to it.
5. **M5 PWA:**
   - root dependency
   - `@mintplayer/ng-spark/pwa` with `provideSparkServiceWorker()` and its update policy (Q1b), with vitest coverage for: next navigation becomes a full load after `VERSION_READY`, `unrecoverable` reloads, the check schedule
   - `appData.build` = ng-spark package version (no commit sha: S4)
   - per-app generator + manifest + icons + `ngsw-config.json`
   - host no-cache helper
   - guard test
   - `docs/guide-pwa.md` (D8, D10)
6. **M6 Own IdP:**
   - the `AddSparkOpenIdConnect` preset and client `oidcProvider`
   - federation buttons on `/connect/login`, with the Disabled-mode mapping fixed
   - one app wired as RP against HR (D7)
7. **M7 Tests** (single sweep at the end, `npm run test:affected` with the Developer licence):
   - **.NET:**
     - nonce valid, invalid and missing (login and link)
     - payload JSON-encoded (update `MapSparkIdentityApiTests.cs:142-158`)
     - remote failure posts `remote_failure` in popup mode and redirects with the code otherwise
     - config-driven registration registers exactly the configured schemes
     - capabilities listing
     - no COOP on the callback
     - `/connect/login` lists external schemes and is mapped when Disabled
     - OIDC preset policy
   - **vitest** (`spark-auth.external-login.spec.ts`):
     - delivery via each of message, BroadcastChannel, storage and the visibility re-read
     - nonce mismatch ignored
     - ack and done-marker written
     - closed-while-visible does **not** settle (D2)
     - closed-poll only clears `externalLoginPending` and a late result still settles; a new attempt settles the old one `popup_closed`
     - timeout
     - full teardown
     - standalone → redirect
     - sign-in page shows the error and reads `?sparkExternalLogin`
   - **E2E (Fleet):**
     - stub `window.open` through an init script so the handle reports `closed`
     - open `/spark/auth/external-login-callback?popup=1&nonce=…` in a second page of the same context (no opener)
     - assert the first page settles with the server's code (`no_login_info`) through BroadcastChannel/storage, not `popup_closed`
     - SW exclusions against a production build, if S4 makes that cheap
   - **Node guard:** the `ngsw-config` exclusions.
8. **M8 Docs and versions:**
   - Authorization README (providers, config, modes)
   - `guide-pwa.md`
   - update the IdP docs
   - update the stale memories (`reference_spark_external_login_gaps`, `identity-provider-progress`)
   - **versions:** `@mintplayer/ng-spark` 22.30.0 → **22.31.0**; `@mintplayer/ng-spark-auth` is no longer published (deprecated on npm by the owner); NuGet stays 11.x (minor/preview bump), since no platform major changed
9. **Manual acceptance:** S5 on the owner's Android device, plus desktop Chrome with the GitHub popup.

## 7a. Pull-request description: required items (owner request, 2026-10-08)

The PR body must contain a **"After merge (owner)"** section with:

```
- [ ] npm deprecate @mintplayer/ng-spark-auth "Moved to @mintplayer/ng-spark/auth/* — see the ng-spark release notes"
```

The session that sees the PR merged must **remind the owner** to run it. The PR closes #464 and #490.

## 8. Acceptance criteria

- A Chrome-tab sign-in on Android with the PWA installed completes in that tab. The captured popup closes itself, and reopening the PWA shows the app signed in. Same for linking.
- A desktop popup sign-in through Facebook, X or LinkedIn while signed out at the provider is **not** reported as cancelled.
- Cancelling at the provider shows a message on the sign-in page. Closing the popup re-enables the buttons without an error; a result that arrives later still signs the user in.
- Inside the installed app (Android; iOS per S3), "Login with …" uses redirect mode and returns signed in, or showing an error.
- An app enables any subset of the six providers through configuration alone, plus `withExternalLogin()` on the client.
- A second Spark app signs in against a Spark-hosted IdP with one preset call. The IdP's login page offers its own external providers.
- Each PWA-enabled app is installable (manifest, icons, SW in production), and no auth or `/spark` navigation is served from the SW.

## 9. Out of scope (genuinely not done)

- Narrowing the manifest scope / moving SPA routes under a prefix (rejected: Spark apps use scope `/`).
- An `external.<host>` subdomain, the legacy workaround (made unnecessary by D1).
- Samsung Internet and Edge-installed PWAs sharing state with a Chrome tab (impossible: separate browser profiles; documented only).
- FedCM (covers Google's ID-token flow only; no Safari support).
- Filing the crbug and commenting on w3c/manifest#989 belongs to the owner (prototype `PLAN.md` milestone 6), not to this PR.

## Sources

- **Prototype:** `C:\Repos\aa\pwa_window_open\PLAN.md` (device results A–L) and `site/shared/popup.js`, `site/shared/main.js:55-113`.
- **Legacy:** `C:\Repos\MintPlayer` `Startup.cs:82-89,233-259,334-336`, `AccountController.cs:341-526`, `Views/Account/ExternalLoginCallback.cshtml:14`. The subdomain shares the identity cookie across every `*.mintplayer.com` subdomain and has an unanchored CORS regex at `Startup.cs:135`. Not ported.
- **Web:**
  - https://stackoverflow.com/questions/68518730
  - https://github.com/w3c/manifest/issues/989
  - https://developer.chrome.com/docs/capabilities/pwa-navigation-management
  - https://web.dev/articles/webapks
  - https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Cross-Origin-Opener-Policy
  - https://angular.dev/ecosystem/service-workers/config
  - https://firebase.google.com/docs/auth/web/redirect-best-practices
  - https://www.nuget.org/packages/AspNet.Security.OAuth.Twitter/
