# Guide: installable Spark apps (PWA)

Every Spark app in `apps/` is an installable progressive web app: a web app manifest, icons and
Angular's service worker (`@angular/service-worker`), wired through `@mintplayer/ng-spark/pwa`.
This guide covers how to add that to a new app, why each piece is there, and how to switch the
service worker off again in production.

Background and decisions: `docs/pwa_external_login_PRD.md` (#464, #490), sections D3, D8, D10, Q1
and Q1b.

## Adding PWA support to a new Spark app

**Don't use the `@angular/pwa` schematic** (`ng add @angular/pwa`, `nx g @angular/pwa:ng-add`).
It assumes an `angular.json`. On the `@nx/angular:application` executor it fails (`Path "undefined"
does not exist`), and forced through, Nx's `angular.json` shim rewrites **every** app's
`project.json`. Write the files by hand; there are six.

1. **`ngsw-config.json`** at the project root (next to `project.json`). Copy one from an existing
   app, e.g. `apps/Fleet/Fleet/ClientApp/ngsw-config.json`:
   - `$schema`: `../../../../node_modules/@angular/service-worker/config/schema.json` (relative to
     the file)
   - `index`: `/index.html`
   - `appData`: `{ "build": "<@mintplayer/ng-spark version>" }`. Not a commit sha: a sha in this
     file changes the Nx cache key on every commit, and patched into `ngsw.json` after the build it
     makes every client see an update on every deploy. The guard test only requires it to be set
     and not sha-shaped; it is not pinned, so an ng-spark bump doesn't have to touch every app.
   - `assetGroups`: `app` (prefetch: `/favicon.ico`, `/index.html`, `/manifest.webmanifest`,
     `/*.css`, `/*.js`, `/bs-theme-preboot.js`, minus `/safety-worker.js` and
     `/worker-basic.min.js`) and `assets` (lazy, `updateMode: prefetch`: icons, images, fonts).
     `worker-basic.min.js` is a second copy of `safety-worker.js` that `@angular/build` emits for
     backward compatibility (`@angular/build/src/utils/service-worker.js`); neither is ever needed
     by the running app, so neither is prefetched.
   - `navigationUrls`: see [the exclusions](#the-navigation-exclusions) below. No `dataGroups` for
     `/spark/**`: Spark's API answers depend on who is signed in and must never come from a cache.
2. **`project.json`**: in `targets.build.configurations.production`, add
   `"serviceWorker": "<projectRoot>/ngsw-config.json"` (the workspace-relative path, e.g.
   `"apps/Fleet/Fleet/ClientApp/ngsw-config.json"`). Development builds stay without a worker.
3. **`public/manifest.webmanifest`**: `name`, `short_name`, `id: "/"`, `start_url: "/"`,
   `scope: "/"`, `display: "standalone"`, `theme_color` and `background_color` equal to the
   light-mode `<meta name="theme-color">` in `index.html` (`#f8f9fa`), and icons: 192×192 and
   512×512 (`purpose: any`) plus a 512×512 `maskable` one whose content stays inside the centre
   80 %. ASP.NET Core already serves `.webmanifest` as `application/manifest+json`.
4. **Icons** in `public/icons/`: `icon-192x192.png`, `icon-512x512.png`,
   `icon-maskable-512x512.png` and a 180×180 `apple-touch-icon.png` (opaque: iOS rounds the
   corners itself and shows transparency as black).
5. **`src/index.html`**: a real `<title>`, and in `<head>`
   ```html
   <link rel="manifest" href="manifest.webmanifest">
   <link rel="apple-touch-icon" href="icons/apple-touch-icon.png">
   ```
   plus a `<noscript>` line after `<app-root>`.
6. **`src/app/app.config.ts`**: add `provideSparkServiceWorker()` from `@mintplayer/ng-spark/pwa`
   to the providers. `@angular/service-worker` must be installed (it is in the root
   `package.json`); it is an **optional** peer dependency of `@mintplayer/ng-spark`, and only the
   `/pwa` entry point imports it.

The service worker only exists in **production** builds (`enabled: !isDevMode()`), so a PWA check
needs a production build served by the ASP.NET host, never the dev server.

## The navigation exclusions

The service worker answers every navigation it matches with the cached `index.html`. That is what
makes deep links work offline, and what would break every URL the **server** must answer. A
navigation to `/signin-github?code=…` answered by the worker never reaches the OAuth handler, so
external login silently fails, but only in an installed production build.

Setting `navigationUrls` **replaces** Angular's defaults, so the defaults are restated first:

| Entry | Why |
|---|---|
| `/**` | Angular default: every navigation is the SPA… |
| `!/**/*.*` | …except URLs with a file extension (Angular default) |
| `!/**/*__*`, `!/**/*__*/**` | …and URLs containing `__` (Angular default) |
| `!/spark/**` | Spark's API, the external-login challenge (`/spark/auth/external-login`) and its callback page |
| `!/signin-*` | the remote authentication handlers' callback paths (`/signin-github`, `/signin-oidc`, …) |
| `!/signout-*` | the OpenID Connect sign-out callbacks (`/signout-oidc`, `/signout-callback-oidc`) |
| `!/connect/**` | Spark's own identity provider (`/connect/authorize`, `/connect/login`, …) |
| `!/.well-known/**` | OpenID discovery and JWKS |

An app adds its own server-only paths. CodeCoverage adds `!/api`, `!/api/**`, `!/badge`,
`!/badge/**`, `!/health` and `!/health/**`; `/**` alone does not exclude the bare path, so both
forms are listed.

As a second line of defence, the external-login challenge and callback URLs carry
`ngsw-bypass=true`. The provider's redirect back to `/signin-*` cannot carry it, which is why the
exclusion list is what actually protects it.

**Guard test:** `tools/verify-ngsw-config.test.mjs` (`npm run test:tools`, run in CI by
`pull-request.yml`) finds every `apps/**/ClientApp` and fails when its `ngsw-config.json` is missing,
lacks one of these exclusions, has a `dataGroup` for `/spark`, has an empty or sha-shaped `appData.build`, or when the production build does not use the file.

## `provideSparkServiceWorker()`

```ts
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';

providers: [provideSparkServiceWorker()]
// or: provideSparkServiceWorker({ enabled, script, registrationStrategy })
```

It calls `provideServiceWorker('ngsw-worker.js', { enabled: !isDevMode(), registrationStrategy:
'registerWhenStable:30000' })` (each option can be overridden) and starts Spark's update policy,
`SparkServiceWorkerUpdates`:

- **A new version is ready (`VERSION_READY`):** nothing happens immediately. The **next** router
  navigation is aborted and becomes a full page load of its target (`location.assign`, with the
  `<base href>` applied), so the user arrives where they were going, in the new version. A
  back/forward navigation reloads instead, because the address bar already shows the target. The
  version a load was made for is remembered in `sessionStorage`, so if the reloaded page still
  reports that same version as ready, it does not reload again.
- **Unrecoverable state (`unrecoverable`):** the running version's files are gone from the cache
  and the server, so the page reloads at once.
- **Checks:** `checkForUpdate()` once the application is stable, every 6 hours, and when the tab
  becomes visible again (at most once a minute).
- **A hidden tab never reloads.** Spark has no unsaved-changes tracking to consult, so a reload
  nobody sees could discard an open edit. An `unrecoverable` in a hidden tab waits until the tab is
  visible; a navigation in a hidden tab stays an in-app navigation.
- Disabled worker (development builds, unsupported browsers) or server rendering: no-op.

`SparkServiceWorkerUpdates` exposes `updateReady`, `currentVersion` and `latestVersion` signals;
`latestVersion()?.appData?.['build']` is the ng-spark version of the new build, e.g. for a footer.

## Server: no-cache for the worker files

`AddSpark()` registers a startup filter (`SparkServiceWorkerCacheHeaders`, in
`libs/spark/MintPlayer.Spark/Services/`) that sends `Cache-Control: no-cache` for
`/ngsw-worker.js`, `/ngsw.json`, `/safety-worker.js`, `/worker-basic.min.js` and
`/manifest.webmanifest`. Without it a cached `ngsw.json` hides a release, or the kill switch below,
for as long as the HTTP cache entry lives.

It is a startup filter, not a step in `UseSpark()`, because `UseSpaStaticFilesImproved` runs before
`UseSpark()` and ends the request. The header is set in `Response.OnStarting`, so it wins over
whatever the static-file middleware wrote. Nothing is needed in the app.

## Headers: no `Cross-Origin-Opener-Policy: same-origin`

Spark never sends `COOP: same-origin` on SPA or authentication pages, and an app must not add it.
The external-login popup hands its result back to the opening tab through `window.opener`,
`BroadcastChannel` and `localStorage`; `same-origin` on the callback page severs the opener and
breaks the popup flow. (Providers that send `same-origin-allow-popups` on their own login pages
already cut the opener; Spark's hand-off survives that through the other two channels.)

There is no Content-Security-Policy either. If one is ever added, the external-login callback page
needs a nonce for its inline script.

## External login inside an installed app

- **Standalone mode uses redirects.** When the app runs installed
  (`display-mode: standalone`, or `navigator.standalone` on iOS), the shipped sign-in and account
  pages sign in by full-page redirect instead of a popup (`provideSparkAuth({ externalLoginMode:
  'auto' })`, the default). A popup from inside an installed app lands in a Custom Tab or a
  separate window whose relationship to the app is unpredictable.
- **Android, popup from a Chrome tab while the app is installed:** Android captures every
  navigation into the app's scope, including the provider's redirect to `/signin-*`, so the
  callback page opens **inside the installed app** rather than in the popup. The installed app
  shares Chrome's profile, so the callback hands the result back to the Chrome tab over
  `BroadcastChannel`/`localStorage`, tries to close itself, and otherwise shows "Signed in — switch
  back to your browser". Reopening the app later returns it to the page it was on.
- **Firefox on Android with the app installed: known limitation.** Firefox's installed web app
  shares no storage or channels with the Firefox browser tab, and `window.open` into the app's
  scope returns no window. The sign-in page reports the failure and offers **Continue in this
  tab**, which retries the same provider by redirect.
- **iOS:** a home-screen app shares no cookies or storage with Safari. Sign-in inside the
  home-screen app works through the redirect mode above; it has not been verified on a device.

## Kill switch: removing the service worker from production

If a broken worker reaches users (for example it serves an `index.html` that no longer matches the
server), replace it with Angular's safety worker, which unregisters itself and clears its caches:

1. On the server, copy `safety-worker.js` over `ngsw-worker.js` in the deployed `browser/` output
   (both files ship in every production build; `node_modules/@angular/service-worker/safety-worker.js`
   is the source). Keep serving it at `/ngsw-worker.js`.
2. Browsers fetch the worker script on their next navigation (it is served `no-cache`), install the
   safety worker, which deletes every `ngsw:` cache, unregisters itself and reloads its clients.
   From then on the app runs from the network like a plain SPA.
3. Leave it in place until the fixed build is deployed, and preferably a while after, so that
   clients who were away also pick it up. To leave PWA mode for good, also remove
   `provideSparkServiceWorker()` (or pass `enabled: false`) and the `serviceWorker` entry in
   `project.json`, but keep serving the safety worker at `/ngsw-worker.js` for a few weeks.

Do not delete `ngsw-worker.js` instead: a 404 leaves the installed worker running from its cache.
