# @mintplayer/ng-spark/auth

The Angular half of [`MintPlayer.Spark.Authorization`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/authorization/MintPlayer.Spark.Authorization/README.md):
sign-in (email or user name, two-factor, external providers, passkeys), registration, password reset,
the account pages (`withAccount()`), route guards and the HTTP interceptor that carries the session
and the antiforgery token to `/spark/auth`.

The auth entry points of [`@mintplayer/ng-spark`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/node_packages/ng-spark/README.md); they ship inside that package.

## Install

```bash
npm install @mintplayer/ng-spark @mintplayer/ng-bootstrap
```

The major version follows Angular's: `22.x` targets Angular 22. When the server references
`MintPlayer.Spark.Authorization`, the build warns with **SPARK030** if the SPA's `package.json` does
not declare `@mintplayer/ng-spark` (the generated `spark-auth.setup.ts` imports it).

## Setup

```ts
// app.config.ts
import { provideHttpClient } from '@angular/common/http';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(...withSparkAuth()),   // interceptor + XSRF-TOKEN / X-XSRF-TOKEN
    provideSparkAuth(),                      // optional: { apiBasePath, loginUrl, defaultRedirectUrl, externalLoginMode }
  ],
};

// app.routes.ts
import { sparkAuthRoutes, withLocalLogin, withRegistration, withAccount,
         withExternalLogin, githubProvider } from '@mintplayer/ng-spark/auth/routes';
import { sparkAuthGuard } from '@mintplayer/ng-spark/auth/guards';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';

export const routes: Routes = [
  ...sparkAuthRoutes(
    withLocalLogin(),                        // login, two-factor, forgot/reset password
    withRegistration(),                      // only with SparkLocalCredentials.Full on the server
    withExternalLogin(githubProvider()),     // sign-in page with a button per provider
    withAccount(),                           // confirm-email + the account pages
  ),
  { path: 'orders', canActivate: [sparkAuthGuard], loadComponent: () => import('./orders.component') },
  ...sparkRoutes(),
];
```

Mount only the features the server maps: see the route × mode table in
[guide-authentication-schemes.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-authentication-schemes.md#choosing-how-much-of-the-local-credential-surface-to-mount).
The server-generated `spark-auth.setup.ts` wires the same calls for you
([Authorization README § Angular Frontend Setup](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/authorization/MintPlayer.Spark.Authorization/README.md#angular-frontend-setup)).

## `withAccount()`

Mounts the account pages; each is also a standalone component from `/account` (confirm-email from
`/confirm-email`). The passkeys path is no component: it forwards to the generic passkeys page.

| Page | Default path | Component |
|---|---|---|
| Confirm email (public; the mail link target) | `confirm-email` | `SparkConfirmEmailComponent` |
| Overview | `account` | `SparkAccountOverviewComponent` |
| Profile — user name, email change (only with the server's `SparkEmailChange.Enabled`), mail language, app fields | `account/profile` | `SparkAccountProfileComponent` |
| Change / set password | `account/password` | `SparkChangePasswordComponent` |
| Two-factor — authenticator (server-rendered QR), recovery codes | `account/two-factor` | `SparkTwoFactorSetupComponent` |
| Connected logins | `account/logins` | `SparkExternalLoginsComponent` |
| Passkeys — forwards to `/po/passkeys/me`, the Authorization library's `Passkeys` page (list, add, rename, remove) | `account/passkeys` | — |
| Personal data export + account deletion | `account/personal-data` | `SparkPersonalDataComponent` |

```ts
withAccount({ exclude: ['externalLogins', 'passkeys'] })   // drop pages the server does not support
withAccount({ canActivate: [myGuard] })                     // default: [sparkAuthGuard]
```

The overview (`/account`) shows who is signed in and links each mounted page. Pages behind a
server switch are also checked against `GET /spark/auth/capabilities`: password and two-factor appear
only when `LocalCredentials` is not `Disabled`, passkeys only when the server reports `passkeys` and
the browser supports WebAuthn.

`<spark-auth-bar>` renders **Account** and **Logout** side by side in a `<bs-button-group>` when
`withAccount()` is mounted (the bar reads the mounted path from the router configuration, so a custom
`account` path is followed), and Logout alone otherwise. It no longer shows the user name or a passkey
button; both live on the account page.

`confirm-email` must stay at the server's `Spark:Auth:Links:ConfirmEmailPath` (default
`/confirm-email`). App-specific profile fields are declared with
`provideSparkAccountProfileFields(...)` (`/models`) and validated on the server by an
`ISparkProfileContributor<TUser>`.

## External sign-in: popup, redirect and installed apps

`SparkAuthService.loginWithProvider(scheme, options?)` signs in through a provider;
`linkProvider(scheme, options?)` attaches one to the signed-in account. Both use one handshake.

**Mode.** `provideSparkAuth({ externalLoginMode })` sets the default, and a call's own
`{ mode: 'popup' | 'redirect' }` overrides it.

| `externalLoginMode` | Behaviour |
|---|---|
| `'auto'` (default) | A full-page **redirect** inside an installed web app (`display-mode: standalone`, or iOS's `navigator.standalone`), a **popup** in a browser tab |
| `'popup'` | Always a popup |
| `'redirect'` | Always a redirect |

Inside an installed app a popup is captured into the app, or opens in a custom tab with no usable
opener, so only a redirect is reliable there.

**Popup mode.** Each attempt sends a fresh nonce. The callback page hands the result back over four
channels, and the first one that brings this attempt's nonce wins:
- `postMessage` to the opener;
- the `spark:external-login` BroadcastChannel;
- `localStorage` (`spark:external-login:<nonce>`) and its `storage` event;
- a re-read of that key when the tab becomes visible or gains focus.

The opener then acknowledges, so the callback page closes itself. If `window.open` returns no window,
the result is `popup_blocked`. There is no automatic fallback: the shipped sign-in page shows the
error and offers **Continue in this tab**, which retries the same provider in redirect mode.

**When the promise settles.** A popup attempt settles only when one of these happens:
- its result arrives;
- another attempt starts (the old one settles `popup_closed`);
- 10 minutes pass (`popup_closed`).

A popup that *looks* closed does not settle it. Facebook, X and LinkedIn send
`Cross-Origin-Opener-Policy`, which cuts the opener's handle, so the popup reads `closed` while the
user is still typing their password. A late result still signs the user in.

> ⚠️ **Button and spinner state must come from `externalLoginPending`, not from the promise
> settling.** `SparkAuthService.externalLoginPending()` is a signal that is `true` while an attempt is
> open and its popup has not been seen closed. Under COOP it turns `false` early, which re-enables the
> buttons, while the attempt keeps listening. Awaiting the promise is fine; disabling a button until it
> settles is not. The shipped sign-in and account pages disable their buttons and show a spinner from
> this signal, with no Cancel button.

**Redirect mode.** On success the provider round trip lands on `returnUrl`, signed in. On failure it
lands on `returnUrl?sparkExternalLogin=<code>`. `SparkAuthService.takeExternalLoginResult()` reads that
code and strips it from the address bar with `replaceUrl`. A code it does not know is answered as
`no_login_info`. The shipped sign-in and account pages call it on load. If your `returnUrl` is another
page, call it there, or in the app shell, to show the failure.

**Errors.** The pages show a failure as `auth.externalLoginError.<code>`, translated. The codes are
in `SparkExternalLoginError` and include `remote_failure` (the provider reported an error) and
`invalid_nonce`. `popup_closed` is "not now" and shows nothing. `link_confirmation_sent` is shown as
a notice, not an error.

**Known limitation: Firefox on Android with the web app installed.** Firefox's installed web app
shares no BroadcastChannel or storage with the Firefox browser tab, and probably no cookies either.
In a browser tab where the app is installed, `window.open` into the app's scope returns no window and
no hand-off is possible, so the user gets `popup_blocked`. **Continue in this tab** (redirect mode)
is the way through there. Inside the installed app, `'auto'` already uses redirect.

## Entry points

| Entry point | What it provides |
|---|---|
| `@mintplayer/ng-spark/auth` | `provideSparkAuth(config?)`, `withSparkAuth()` |
| `/routes` | `sparkAuthRoutes(...)`, `withLocalLogin`, `withRegistration`, `withExternalLogin`, `withPasskeys`, `withAccount`; provider presets `githubProvider`, `googleProvider`, `microsoftProvider`, `facebookProvider`, `twitterProvider`, `linkedInProvider`, `externalProvider(scheme)` |
| `/guards` | `sparkAuthGuard` — lets a signed-in user through, otherwise redirects to sign-in with `returnUrl`; waits for the session check on a hard reload. `sparkAuthenticatedGuard` is the same guard |
| `/interceptors` | `sparkAuthInterceptor` (included by `withSparkAuth()`) |
| `/core` | `SparkAuthService` (current user signal, `checkAuth()`, sign-in/out, `loginWithProvider`/`linkProvider`, `externalLoginPending`, `takeExternalLoginResult()`), `SparkAuthTranslationService` |
| `/models` | `SPARK_AUTH_CONFIG`, `SPARK_AUTH_ROUTE_PATHS`, `SPARK_EXTERNAL_PROVIDERS`, `SPARK_ACCOUNT_PROFILE_FIELDS`, `provideSparkAccountProfileFields(...)` and the wire types |
| `/login`, `/two-factor`, `/forgot-password`, `/reset-password`, `/register`, `/sign-in` | The individual pages the route features mount |
| `/account`, `/confirm-email` | The account pages above |
| `/auth-bar` | `<spark-auth-bar>` — sign-in / user menu for a top bar |
| `/pipes` | `TranslateKeyPipe` |

## More

- [MintPlayer.Spark.Authorization README](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/authorization/MintPlayer.Spark.Authorization/README.md) — the server side, routes and options
- [Passkeys guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-passkeys.md)
- [Authentication schemes guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-authentication-schemes.md)
- [Release notes 11.0.0-preview.91 / ng-spark-auth 22.14.0](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/release-notes-preview-91.md)
