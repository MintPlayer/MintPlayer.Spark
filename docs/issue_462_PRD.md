# PRD — Issue #462: dark mode (`prefers-color-scheme`) in the Spark frontend

The order of work is in [issue_462_plan.md](issue_462_plan.md).

**Goal:** every Spark app follows the OS colour scheme out of the box. The visitor can switch the theme
**at any moment**, from the app or by changing the OS dark-mode setting (owner hard requirement,
2026-09-30, see D14).
- **Auto** follows the OS live.
- **Light or Dark** stays until the visitor changes it in the app. The framework does this, so an app writes no theming code. The work spans two
repositories and lands as one unit: `mintplayer-ng-bootstrap` first, then its release, then Spark. This
order was already recorded in `issue_460_PRD.md:1784`.

---

## 1. What already exists (investigated 2026-09-30)

### 1.1 Spark (what #460/#461 already did)
- **#460 M15 (commit 75ab0395, in squash d661d80b).** Adds `ng-spark/grid/src/spark-query-grid.component.scss`.
  Links and the `⋮` toggle on a selected row now take `--mp-datatable-row-selected-color` instead of
  showing blue on blue. No colour is hard-coded, so this works under `[data-bs-theme=dark]` unchanged.
  It is the only dark-mode step taken so far.
- **`spark-shell` `sidebarTheme` input.** It takes `'dark' | 'light' | null` and defaults to `'dark'`
  (`shell/src/spark-shell.component.ts:69`). It sets `[attr.data-bs-theme]` on the sidebar `<nav>` only
  (`spark-shell.component.html:23-25`) and is tested at `spark-shell.component.spec.ts:195-200`.
  `spark-program-units.component.scss:8` relies on that ancestor attribute.
- **Nothing sets `data-bs-theme` on `<html>`.** Nothing reads `prefers-color-scheme` or `matchMedia`.
  No `index.html` has a `color-scheme` or `theme-color` meta. No app uses Angular SSR; a grep for
  `@angular/ssr` / `provideServerRendering` under `apps/*/*/ClientApp/src` is empty.
- **The ng-spark libraries are mostly theme-safe already.** Most styles use `--bs-*` variables. All
  built-in icons use `fill="currentColor"`. No component sets `ViewEncapsulation`. The only `[style.*]`
  colour bindings are data colour swatches, which are correct as they are. The hard-coded exceptions:

  | Where | What | Problem in dark |
  |---|---|---|
  | `shell/src/spark-shell.component.scss:52` | `--spark-shell-main-bg` fallback `#f8f9fa` | **Main area stays light.** This is the main blocker. |
  | `spark-shell.component.scss:63` | light sidebar fallback `#f8f9fa` | light sidebar stays light |
  | `spark-shell.component.scss:15,28,67-68` | topbar `var(--bs-dark,#212529)`, toggler `rgba(255,255,255,.85)`, dark sidebar `#333`/`#fff` | intentionally always dark; fine, but should be tokens |
  | `spark-shell.component.scss:79,90,94`, `spark-program-units.component.scss:13,27,31` | white-alpha hover/active tints | assume a dark sidebar; wrong on a light sidebar in either page theme |
  | `client-operations/src/toast-container.component.ts:38-50` | raw Bootstrap hex values (`#0d6efd`, `#198754`, `#ffc107`/`#000`, `#dc3545`), `white` | do not follow `-dark` palette |
  | `query-list/src/spark-query-list.component.scss:55` | `tr:hover` `rgba(0,0,0,.05)` | invisible on dark |
  | `moderation/src/spark-reputation-badge.component.ts:20` | `bg-light text-dark` | light patch on dark page |

- **Styles wiring.** The library ships no global stylesheet. Every app's `ClientApp/project.json:25-29`
  lists `@mintplayer/ng-bootstrap/_bootstrap.scss`, `bootstrap-icons.css`, and an empty
  `src/styles.scss`, as separate entries.
- **Portalled overlays.** The column-filter panel is portalled to a document-root overlay
  (`column-filter/...panel.component.scss:1-9`). **So the theme must be set on `<html>`**, not on
  `spark-shell`.
- **Server-rendered HTML outside Angular.** `libs/identity_provider/.../Endpoints/{Login,TwoFactor,Consent,ConnectedApplications}.cs`
  build standalone pages with hard-coded light CSS (e.g. `Login.cs:30-38`, `TwoFactor.cs:105-115`,
  `Consent.cs:78-105`, `ConnectedApplications.cs:200-206`).
- **The 2FA QR code** is a server SVG, dark on white (`SparkQrCodeRenderer.cs:19`), shown as an `<img>`.
- **Type hints.** `typeHints` exists on the client models but nothing renders colours from it, so no
  server-sent colours need a dark equivalent today.

### 1.2 Apps
- **DemoApp, HR, Fleet:** no bespoke colours. Fleet's `color-edit-renderer.component.ts:36` `'#000000'`
  is only a default value.
- **CodeCoverage (production):**
  - `shell/shell.component.scss:14-16` sets `--spark-shell-main-bg: #f8f9fa`, which forces the main
    area light. It also sets `#212529` and `#333`.
  - `shell/shell.component.scss:23` sets the toggler colour.
  - `pages/file/file.component.scss:5-15` sets the covered/partial/uncovered line tints as fixed `rgba`.
  - `repo-badge-panel.component.ts:36` uses `bg-light`, and `build-sessions-renderer.component.ts:28`
    uses `text-bg-light`.
  - Coverage bars, sparklines, trend charts and `text-bg-*` badges are theme-safe, as long as the
    ng-bootstrap internals are (spike S3).
- **CodeCoverage SVG badges** (`Badges/BadgeRenderer.cs:33-75`) are self-contained shields.io-style SVGs
  with no `<style>`. They are embedded in GitHub READMEs. **They must stay theme-independent.**

### 1.3 ng-bootstrap (`C:\Repos\mintplayer-ng-bootstrap`, 22.19.0; Spark consumes `^22.19.0`)
- **Theme switching already exists.** `BsThemeService` is exported as `@mintplayer/ng-bootstrap/theming`
  (`theming/src/lib/service/bs-theme.service.ts`):
  - `setMode('auto'|'light'|'dark'|custom)`, with `mode` and `effectiveMode` signals.
  - `auto` follows `matchMedia('(prefers-color-scheme: dark)')` live.
  - It persists to localStorage key `'bs-theme-mode'` (`bs-theme-mode.ts:25`), writes
    `<html data-bs-theme>`, and is SSR-safe.
  - The ng-bootstrap demo has a no-flash pre-boot script (`apps/ng-bootstrap-demo/src/index.html:9-30`)
    and a theme-toggle component (`apps/ng-bootstrap-demo/src/app/components/theme-toggle/`).
  - **Neither is exported for consumers.**
- **Components mostly read `--bs-*` variables**, and those inherit through shadow roots. Where
  Bootstrap's `[data-bs-theme=dark] .x` selectors can't reach inside a shadow root or `:host`, the
  library already has workarounds (accordion, dropdown, close via `:host-context`).
- **Known remaining light-only spots:**
  - `mintplayer-web-components/scheduler/src/styles/scheduler.styles.scss:1575,1579,1584`: scrollbar hex.
  - `query-builder/src/mp-query-builder.light.scss:109`: `--bs-btn-color: #646b72`.
  - `datatable/src/styles/datatable.light.scss:410,421,615`: hover fallback `rgba(0,0,0,.04)`.
  - `card/src/card-global.styles.scss:32-33,39-40`: `rgba(0,0,0,.03)` and `#dee2e6`.
  - `card/src/mp-card.element.scss:87`: `$card-bg` literal.
  - `_styles/form-select.styles.scss:9-13` (select caret) and `_styles/form-check.styles.scss` (switch
    image): Bootstrap swaps these via `[data-bs-theme=dark] .form-select` / `.form-switch`, which
    probably never matches inside a shadow root. **Unverified, see spike S2.**

### 1.4 Bootstrap `_variables-dark.scss` (the owner's question)
- **What it is.** It is `node_modules/bootstrap/scss/_variables-dark.scss` in Bootstrap 5.3.8, 102
  lines. It defines the dark palette as Sass variables:
  - `$body-bg-dark`, `$body-color-dark`, `$body-{secondary,tertiary,emphasis}-*-dark`
  - `$border-color(-translucent)-dark`
  - `$link-*-dark`, `$headings-color-dark`, `$code-color-dark`, `$mark-*-dark`
  - `$*-text-emphasis-dark`, `$*-bg-subtle-dark`, `$*-border-subtle-dark`
  - `$form-select-indicator-dark`, `$form-switch-bg-image-dark`, `$form-{valid,invalid}-*-dark`
  - accordion, carousel, `$btn-close-filter-dark`
- **How it's wired.** `_variables.scss:387-388` sets `$enable-dark-mode: true` and
  `$color-mode-type: data`. `_root.scss:132-133` emits
  `@include color-mode(dark, true) { color-scheme: dark; --bs-body-bg: #{$body-bg-dark}; … }`.
  The `color-mode()` mixin (`mixins/_color-mode.scss`) produces either
  `@media (prefers-color-scheme: dark) { :root {…} }` (in `media-query` mode) or
  `[data-bs-theme=dark] {…}` (in `data` mode).
- **We already use it.** ng-bootstrap's `_bootstrap.scss` imports `bootstrap-utilities.scss` and
  `root`, and both pull in `_variables-dark.scss`. So the full `[data-bs-theme=dark]` block, with every
  `--bs-*` dark value, **is already in every Spark app's global CSS**. It just never activates, because
  nothing sets the attribute. 23 of 24 ng-bootstrap component SCSS files also import `variables-dark`.
- **Overriding the `-dark` Sass variables only works where the SCSS is compiled.**
  - The app's global stylesheet can take overrides. It would need a `styles.scss` that sets
    `$body-bg-dark: …` before `@import "@mintplayer/ng-bootstrap/bootstrap"` (with `@import` and
    `!default`; Bootstrap 5.3 has no `@use` path), replacing the separate `_bootstrap.scss` entry.
  - ng-bootstrap's component styles are **precompiled when the library is built**, with default
    values. An app's Sass override never reaches them.
  - So an override only changes what flows through `--bs-*` custom properties. That covers most
    things, but not the literal values baked into precompiled component CSS.
- **Consequence (decision D2).** Use `_variables-dark.scss` exactly as shipped for the palette. It is
  what supplies every `--bs-*-dark` value we will rely on. Customise per app with **CSS custom
  properties under `[data-bs-theme=dark]`**, not Sass variables, because only CSS properties reach both
  the global CSS and the precompiled component CSS.

### 1.5 Prior art: the legacy MintPlayer app
- **Where it is.** The owner's hand-written dark mode is in `C:\Repos\MintPlayer\legacy\MintPlayer.Web`
  (Angular 13, Bootstrap 5.0, commits `ff9bb31` and `1d96ed6`, June 2021). The current
  `MintPlayer.Web\ClientApp` (Bootstrap 5.3.8) has **no** dark mode. Its `shell.scss` hard-codes the
  same `#f8f9fa` / `#333` values that Spark's shell has.
- **How the legacy version works.** It is CSS only: two `@media (prefers-color-scheme: light|dark)`
  blocks in `styles/variables.scss` define app-specific custom properties, and ~12 Bootstrap components
  are then restyled by hand (`styles/general.scss`).
- **What to keep.** It has no flash of the wrong theme and no JS dependency.
- **What to drop:**
  - Hand-picked hex values (Bootstrap 5.0 had no `-dark` variables).
  - `!important` hacks.
  - `.btn-close { filter: invert(1) }` applied in light mode too.
  - No `color-scheme`, so native scrollbars, inputs and `select` popups stay light.
  - No user override.
  - Per-component overrides that don't scale to a library.
- Bootstrap 5.3 colour modes plus a pre-boot script keep the no-flash property and fix all of these.

---

## 2. Decisions

The owner settled G1–G8 and D14 in a grilling session on 2026-09-30. D1, D2, D5, D7, D11 (apart from its shell
part) and D12 are Claude's recommendations, with reasons recorded; the owner may override them.

### Owner decisions (grilling, 2026-09-30)
- **G1 — ng-bootstrap owns the pre-boot script AND the toggle (less duplicated code).**
  - ng-bootstrap exports `bs-theme-toggle`, promoted from the demo. Spark has **no wrapper component**:
    `spark-shell` renders `bs-theme-toggle` directly and passes its translated labels as inputs.
  - There is exactly one theme service, one script and one toggle, all in ng-bootstrap.
- **G2 — The pre-boot script is an external, blocking file, not an inline copy.**
  - ng-bootstrap ships `theming/bs-theme-preboot.js`. Each app copies it at build time through an
    `assets` glob in `project.json`. `index.html` gets one line: `<script src="bs-theme-preboot.js">`
    in `<head>`, before the stylesheets, with no `async`/`defer`.
  - A classic script in `<head>` blocks rendering exactly like an inline one, so there's no flash.
  - It is CSP-friendly under `'self'`, so there's no hash to maintain.
  - **Guard:** a test asserts that each app's built output contains the file and that `index.html`
    references it before any stylesheet. A 404 would silently bring the flash back.
- **G3 — The choice is stored in a readable cookie, NOT localStorage.**
  - The cookie is `bs-theme-mode`, not HttpOnly, with `Path=/`, `SameSite=Lax`, `Max-Age` of one year,
    and `Secure` on HTTPS. The value is `auto | light | dark | <custom>`.
  - **Why a cookie:** a server that renders HTML can read it, and so write `data-bs-theme` without any
    script. This covers ng-bootstrap SSR apps (MintPlayer.Web) and the identity-provider pages.
  - **What the cookie can't do:** CSS can't read cookies, and a static SPA `index.html` has no server
    render step. So Spark SPAs still need the pre-boot script (G2), which now reads the cookie. The
    server also can't know the OS scheme for Auto: `Sec-CH-Prefers-Color-Scheme` is Chromium-only and
    missing on the first request. So Auto is always resolved in the browser, or by a CSS media query in
    pages whose CSS we own.
  - **No migration** of a 22.19 localStorage value (G8).
  - **Cross-tab sync:** cookies fire no `storage` event, so tabs sync with a `BroadcastChannel`.
- **G4 — The cookie is host-only by default, with an opt-in `cookieDomain`.** For example,
  `provideBsTheme({ cookieDomain: '.mintplayer.com' })` shares one choice across subdomains. The domain
  is never derived automatically, because that needs the public-suffix list.
- **G5 — Cookie only, no account field.** The theme is a per-device choice: dark phone, light desktop.
  No `PreferredColorScheme` on the profile.
- **G6 — The toggle is on by default in the `spark-shell` topbar.** An app hides it with
  `[themeToggle]="false"`, or places `bs-theme-toggle` somewhere else itself. It is checked at phone
  width next to `spark-auth-bar`.
- **G7 — Claude designs the shell palette.** The topbar, sidebar and main area each get a light and a
  dark value. They are not fixed as "topbar always dark".
  - They are defined as `--spark-shell-{topbar,sidebar,main}-{bg,color}` tokens under
    `[data-bs-theme=light]` and `[data-bs-theme=dark]`, built from Bootstrap tokens.
  - They are tuned in the browser through the `playwright_node` MCP and checked for WCAG AA contrast on
    text, links, and the hover and active states.
  - They are **shown to the owner (screenshots of light and dark) for approval before M2 is committed.**
  - **`sidebarTheme` is removed** (G8). The palette tokens replace it. An app that wants a different
    look overrides the `--spark-shell-*` tokens. Delete the input, its binding
    (`spark-shell.component.html:23-25`), its spec (`spark-shell.component.spec.ts:195-200`) and
    CodeCoverage's `sidebarTheme="dark"`. Then check `spark-program-units.component.scss:8`, which
    relied on the sidebar's `data-bs-theme`.
  - `spark-auth-bar` switches from `btn-outline-light` to theme-aware buttons, because the topbar is no
    longer guaranteed to be dark.
  - CodeCoverage drops its own shell colours and uses the framework palette.
- **G8 — No backward compatibility** (owner, 2026-09-30: the libraries are still in preview). Break
  APIs, storage formats and inputs freely, with no shims, migrations or deprecation periods. Breaks
  are listed in the release notes, and the versioning rule still applies: minor bumps only.
- **D14 — Switch any time (owner hard requirement, 2026-09-30).**
  - **Auto** (the default) follows `matchMedia('(prefers-color-scheme: dark)')` live, with no reload.
  - **Light or Dark is sticky.** An OS change does not reset it. The owner rejected "last action
    wins" as unnecessary and too complex; sticky is also the common pattern (GitHub, MDN).
  - The toggle offers Auto, Light and Dark, so handing control back to the OS is one click.
  - **Verified in the ng-bootstrap source (S6):** `BsThemeService` already follows the OS live in Auto
    (`mql.addEventListener('change', …)`), keeps an explicit mode sticky, and stores a plain string.
    The only changes are the storage (G3) and cross-tab sync.

### Technical decisions
- **D1 — `$color-mode-type: data` (Bootstrap's default), not `media-query`.** `media-query` removes the
  `[data-bs-theme=dark]` selector from the CSS, which would break three things:
  - scoped themes that set `data-bs-theme` on an element, such as ng-bootstrap's navbar
  - `close.component.scss:22`
  - the precompiled component styles, which were built in `data` mode
  
  It would also rule out a user choice.
- **D2 — Use `_variables-dark.scss` as shipped; customise with CSS custom properties** (see §1.4). Spark
  ships no Sass wrapper. `docs/guide-theming.md` shows how an app overrides the dark palette:
  `[data-bs-theme=dark] { --bs-body-bg: …; }` in `src/styles.scss`, which already exists and is
  already loaded after `_bootstrap.scss`.
- **D3 — `provideSparkTheme()` is not needed.** `BsThemeService` is `providedIn: 'root'`.
  **`spark-shell` injects it itself**, not only through the toggle. Otherwise `[themeToggle]="false"`
  would leave Auto without its live `change` listener. That instantiates it at boot in every app that
  uses `spark-shell`. An app without the shell calls ng-bootstrap's own `provideBsTheme(options?)`, which is added in NB1
  for `cookieDomain` and the default mode. Spark adds no theme API of its own.
- **D5 — `index.html` metas in all four apps.** Add `<meta name="color-scheme" content="light dark">`,
  so the canvas, scrollbars and native controls follow the OS before CSS loads. Also add two
  `theme-color` metas with `media="(prefers-color-scheme: light|dark)"`.
- **D6 — Other shell tints** (`spark-shell.component.scss:79,90,94`,
  `spark-program-units.component.scss:13,27,31`) become `rgba(var(--bs-emphasis-color-rgb), .1|.2)`, so
  they flip with whatever theme scope they are in. The #460 selected-row rule stays unchanged.
- **D7 — Small library fixes, all using `--bs-*` tokens.**
  - **Toasts:** use `var(--bs-{primary,success,warning,danger})`, with text colour from `--bs-white`
    or `--bs-dark` for warning.
  - **Query-list hover:** `rgba(var(--bs-emphasis-color-rgb), .05)`.
  - **Reputation badge:** `bg-body-tertiary text-body border`.
  - **Review-queue:** `bg-warning text-dark` → `text-bg-warning`.
- **D10 — Identity-provider pages (C#) read the cookie on the server. No script.**
  - An explicit `light`/`dark` cookie is rendered as `<html data-bs-theme="…">`.
  - For `auto`, or no cookie, the page's own CSS applies a `@media (prefers-color-scheme: dark)` block
    scoped to `:root:not([data-bs-theme=light])`. That also switches live when the OS changes.
  - `:root { color-scheme: light dark }`.
  - The shared CSS lives in one C# constant, rather than four copies.
  - The QR code keeps its white background.
  - Being host-only (G4), the cookie reaches these pages when they're on the SPA's host. Otherwise
    they follow the OS.
- **D11 — CodeCoverage.**
  - Delete `shell/shell.component.scss:14-16,23` (G7).
  - Line tints → `var(--bs-{success,warning,danger}-bg-subtle)`.
  - `bg-light` → `bg-body-tertiary`, and `text-bg-light` → `text-bg-secondary`.
  - **The SVG badges do not change.** A test pins that `BadgeRenderer` output contains no
    `prefers-color-scheme`, `<style` or `currentColor`.
- **D12 — ng-bootstrap fixes (in that repo):**
  - G3 cookie storage (localStorage code removed, no migration), `BroadcastChannel`, and `provideBsTheme({ cookieDomain,
    defaultMode })`.
  - SSR: on the server, read the cookie from Angular's `REQUEST` token and render `data-bs-theme` for an
    explicit mode.
  - G2 `bs-theme-preboot.js`, and G1 `bs-theme-toggle` with label inputs.
  - Form-select caret and form-switch in shadow DOM, if S2 confirms they're broken.
  - Scheduler scrollbar, query-builder `#646b72`, datatable and card fallbacks → `--bs-*`.
  - Anything S3 finds.
- **D13 — Versions.**
  - ng-bootstrap `22.19.0` → `22.20.0` (minor: Angular stays 22).
  - `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` bump minor. Both depend on
    `@mintplayer/ng-bootstrap ^22.20.0`; ng-spark-auth is still on `^22.2.0`.
  - The identity-provider csproj gets a minor bump (CI requires one for every touched `libs/` project).
  - No major changes anywhere.

---

## 3. Requirements

**R1 — OS scheme at first paint.**
- With no stored preference, a browser set to dark shows every Spark app dark from the first frame:
  `html[data-bs-theme=dark]` is present before the Angular bundle runs.
- Switching the OS scheme while the app is open re-themes it live.

**R2 — Switch any time, either way (hard requirement).** See D14.
- **From the app.** `bs-theme-toggle` in the shell topbar re-themes the page immediately. The choice persists across
  reloads.
- **From the OS.** In Auto mode (the default), changing the system dark-mode setting re-themes the page
  immediately, with no reload.
- **An explicit Light or Dark is sticky.** It persists across reloads and OS changes until the visitor
  picks something else in the app. Picking Auto hands control back to the OS.
- **Other open tabs of the same app** follow a change made in one tab.

**R3 — The framework does the work.** An app that uses `spark-shell` gets R1 and R2 with:
- the `bs-theme-preboot.js` assets glob in `project.json`
- one `<script src>` line and the metas in `index.html`

No TypeScript and no SCSS are needed.

**R4 — Every ng-spark and ng-spark-auth surface is legible in both themes.** The shell palette (G7) has
to meet WCAG AA and be owner-approved. This covers: shell (topbar, sidebar, main), program units, query list, query grid (including selected rows),
column-filter overlay, PO form and detail, toasts, modals, the moderation components, and the auth
pages.

**R5 — Every CodeCoverage page is legible in both themes, including line highlighting.** The badge SVGs
do not change.

**R6 — The identity-provider pages follow the theme.** They honour the cookie when it reaches them
(server-rendered, no script), and otherwise follow the OS live through CSS. The QR code stays scannable.

**R7 — Tests: unit tests plus a Fleet E2E dark-mode test.** The E2E test checks:
- the attribute is set before the app bootstraps
- the computed background of the main area is dark
- the override persists across a reload
- **the R2 matrix:**
  1. In Auto, emulate the OS switching dark → light while the page is open. The page turns light with
     no reload.
  2. Pick Dark in the app, and the page turns dark at once. After a reload it is still dark.
  3. Pick Dark, then emulate the OS switching to light. The page stays dark (sticky).
  4. With two tabs open, a toggle in tab A re-themes tab B.

  Playwright's `EmulateMediaAsync(new() { ColorScheme = … })` fires the `matchMedia` change event,
  which is what makes cases 1 and 3 testable.

---

## 4. Spikes (answer before the milestone that depends on them)

- **S1 — Is anything precompiled that an app override needs to reach?** Confirm that ng-packagr ships
  ng-bootstrap component CSS compiled with default Sass values. Inspect
  `node_modules/@mintplayer/ng-bootstrap/fesm2022/*.mjs` for literal `#212529` / `--bs-body-bg` usage.
  This confirms or refutes D2's "only CSS properties reach components" claim.
- **S2 — Form controls inside shadow DOM under dark.** In the ng-bootstrap demo, set
  `data-bs-theme=dark` and check the computed `background-image` of a `bs-select` caret and a
  `form-switch`. This decides whether D12's first fix is needed. The static read did not verify it.
- **S3 — Visual sweep of ng-bootstrap components in dark.** Cover: datatable (incl. virtual), modal,
  offcanvas, dropdown, select, datepicker, tab control, toast, sparkline, trend chart, progress, code
  snippet, scheduler, query builder, card, accordion. Use the ng-bootstrap demo with its theme toggle
  and list what is illegible. Whatever it finds joins D12.
- **S4 — What does the demo's theme-toggle depend on?** Read
  `apps/ng-bootstrap-demo/src/app/components/theme-toggle/`. It gets promoted either way (G1). The spike
  only decides how much of it is rewritten: label inputs, and no demo-only dependencies.
- **S5 — The pre-boot script runs before the stylesheet.** In a Spark app's built `index.html`,
  confirm that `<script src="bs-theme-preboot.js">` precedes the injected `<link rel="stylesheet">`,
  because Angular inserts its links at the end of `<head>`. Also confirm that the assets glob copies the
  file to the output root.
- **S6 — ✅ Answered 2026-09-30 by reading `bs-theme.service.ts`.**
  - Auto follows the `change` event live, and an explicit mode is sticky.
  - There is no `storage` listener.
  - The value is stored as a plain string under `localStorage['bs-theme-mode']`.
  - Still open: confirm that Playwright .NET `EmulateMediaAsync` fires a `matchMedia` `change`
    listener. R7 cases 1 and 3 depend on it.
- **S7 — `BroadcastChannel` and cookie writes on the SPA host.** Confirm that a `document.cookie` write
  with `SameSite=Lax; Path=/` survives the `UseAngularCliServer` dev proxy and the production
  `https` profile. Also confirm that `Secure` is only set on HTTPS, so local HTTP dev still works.

Browser checks go through the `playwright_node` MCP. It is connected again as of the grilling session.

---

## 5. Open questions for the owner

None. All were resolved in the 2026-09-30 grilling, recorded as G1–G8 and D14. The one pending owner
action is **approving the shell palette** (G7) during M2.

---

## 6. Risks

- **A missing `bs-theme-preboot.js` gives a silent flash.** A wrong assets glob returns a 404 and the
  page still works, just with a flash. The built-output test in G2 guards this.
- **The script and the service must read the cookie the same way.** Both live in ng-bootstrap (G1), and
  a jsdom test there runs the script against each cookie value and compares the result with the
  service.
- **Cookie scope.** A host-only cookie doesn't reach identity-provider pages served from another host.
  They fall back to the OS, which is acceptable (D10).
- **Precompiled ng-bootstrap literals (S1)** can only be fixed in ng-bootstrap, so an S3 finding means
  more work in that repo before its release.
- **The release order is a hard dependency.** Spark's CI resolves `^22.20.0` from npm, so the Spark PR
  can't go green until ng-bootstrap 22.20.0 is published.
- **CodeCoverage is production.** The new shell palette (G7) changes its look in light mode too. That
  is intended and owner-approved, but it will be visible to existing users.

## 7. Out of scope (genuinely not being done)

- **Themeing the SVG coverage badges.** They are embedded in third-party pages and must not follow the
  viewer's theme.
- **Custom named themes beyond light and dark.** `BsThemeService` supports them, but no app needs one.
