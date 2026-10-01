# Plan — Issue #462: dark mode (one unit of work, two repositories)

Requirements, decisions (G1–G8 from the owner, D1–D14) and spikes are in
[issue_462_PRD.md](issue_462_PRD.md). This file is the order of work.

**Status (2026-09-30):**
- **Part A (ng-bootstrap)** is specified in MintPlayer/mintplayer-ng-bootstrap#420, including the
  calendar-header regression from #393. It is implemented in a session running in that repository,
  because this session's hook refuses cross-repo edits.
- **Part B (Spark)**: ng-bootstrap 22.20.0 and web-components 2.17.0 are on npm. **B-1 is done**
  (2026-10-01): dependencies, shell toggle and service (M2), D7 fixes and app wiring (M3), CodeCoverage
  tints and the badge pin, and the identity-provider pages (M4). **B-2 is done** (64c8816b): the shell palette
  (M1, `--spark-shell-*` tokens, removing `sidebarTheme`, CodeCoverage's shell overrides). **M5 is
  done** (S5/S7 ticked, Fleet dark-mode E2E written but not run, theming guide). Left: M6.
- **The same Spark branch also carries the Contributions / attribute-rights / concurrency work**
  ([contributions_plan.md](contributions_plan.md)). Owner decision: everything stays on
  `feat/462-dark-mode`, in one PR.
- Spikes S1–S6 and the browser sweep are done (PRD §4.1).
- **ng-bootstrap PR MintPlayer/mintplayer-ng-bootstrap#421** implements #420, with argued deviations.
  It was reviewed on 2026-09-30, and the review comment is
  https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/421#issuecomment-5916913690.
  **Part B must use the API the PR actually ships, not #420's original text:**
  - **Pre-boot script:** `@mintplayer/web-components/theming/bs-theme-preboot.js` (owner: keep that
    path). The Spark apps' assets glob copies it from `node_modules/@mintplayer/web-components/theming/`.
  - **New peer dependency: `@mintplayer/web-components ^2.17.0`.** Add it to the Spark workspace and
    to ng-spark's peer dependencies.
  - **`provideBsTheme` takes only `cookieDomain`.** The default mode is set with
    `<meta name="bs-theme-default-mode" content="auto">` in each app's `index.html`.
  - **The toggle takes a `modes` array input,** not `autoLabel`/`lightLabel`/`darkLabel`, so
    `spark-shell` passes translated labels through `modes`. An `<mp-theme-toggle>` web component
    also exists. *(Measured in 22.20.0: it is a button that cycles auto → light → dark, not a
    dropdown; each entry is `{ mode, label, announcement, icon }`, where `label` names the NEXT
    mode's action and `announcement` the current one. The pre-boot script reads the default-mode
    meta with `querySelector` while `<head>` is still parsing, so the meta must come before it.)*
  - **Shadow-DOM dark fixes use CSS style queries** (`@container style(--mp-color-mode: dark)`).
    Engines without them keep light carets and knobs.
  - **Open review items** (must-fix 2 and 3 in the comment) are the agreement-test skip, and a
    host-only cookie shadowing a `Domain` cookie. Spark's M3 cookie test (S7) should include the
    `cookieDomain` case.

**Rules for executing this plan**
- **Branches.** One branch per repository, landed together:
  - `feat/462-dark-mode` in `C:\Repos\mintplayer-ng-bootstrap`
  - `feat/462-dark-mode` in `C:\Repos\MintPlayer.Spark`
  
  Sequencing is forced: ng-bootstrap merges and **publishes 22.20.0 first**, because Spark's CI
  resolves the dependency from npm. The work is not split any further.
- **No backward compatibility (G8).** Remove what is replaced: no shims, no migrations, no
  deprecations. List the breaks in the release notes.
- **Testing.** Commit per milestone. **Do not run test suites per milestone.** Verify with a build and
  by reading the code. Run spikes when a milestone depends on their answer. There is one full test
  sweep per repository at the end (NB5, M6).
- **Logs.** Write logs raw to the scratchpad (`cmd > x.log 2>&1; echo "EXIT: $?"`), then grep them.
- **Running the apps.** `dotnet run` is the whole command for a Spark app. Never run `ng serve` or
  `ng build` next to a running host. CodeCoverage needs `--launch-profile https`.
- **Browser checks** go through the `playwright_node` MCP, never the `dcg:playwright` skill.
- **Versions** (D13). ng-bootstrap goes to `22.20.0`. `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth`
  bump minor. The identity-provider csproj bumps minor. **No major changes.**

---

## Part A — `mintplayer-ng-bootstrap`

Tracked as **[MintPlayer/mintplayer-ng-bootstrap#420](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/420)**,
which contains the full spec. It is implemented in a session running in that repo, because this
session's hook refuses cross-repo edits.

### NB0 — Spikes
- [x] **S6** Record `BsThemeService` behaviour. Answered: Auto is live, an explicit mode is sticky,
  there's no `storage` listener, and the value is a plain string.
- [ ] **S1** Confirm that the component CSS is precompiled with default Sass values.
- [ ] **S4** Check what the demo theme-toggle depends on.
- [ ] **S2** Check the select caret and form-switch inside shadow DOM under `data-bs-theme=dark`,
  measured in the demo through the MCP.
- [ ] **S3** Do the dark visual sweep of the component list in PRD §4. Record findings in the PRD
  (§4.1, "Spike results").

### NB1 — Cookie-backed theme service (G3, G4, D14)
- [ ] `BsThemeService` reads and writes the `bs-theme-mode` cookie: not HttpOnly, `Path=/`,
  `SameSite=Lax`, `Max-Age=31536000`, and `Secure` only on HTTPS. **Delete the localStorage code, with
  no migration.**
- [ ] Add `provideBsTheme({ cookieDomain?, defaultMode? })`. It is optional; without it the service
  uses a host-only cookie and `'auto'`.
- [ ] Cross-tab sync: post on `setMode` through `BroadcastChannel('bs-theme-mode')` and apply what
  other tabs send.
- [ ] SSR: on the server, read the cookie from Angular's `REQUEST` token. For an explicit mode, set
  `data-bs-theme` on the server-rendered `<html>`.
- [ ] Unit tests (stubbing `matchMedia`, `document.cookie` and `BroadcastChannel`):
  - Auto + OS change re-themes.
  - Explicit + OS change has no effect.
  - A reload restores the mode from the cookie.
  - The `cookieDomain` option is applied.
  - The broadcast reaches another instance.
  - SSR with a cookie renders the attribute; SSR with Auto renders none.

### NB2 — `bs-theme-preboot.js` (G2)
- [ ] Ship `theming/bs-theme-preboot.js` in the package. It is a plain ES5 IIFE with no module syntax.
  It reads the cookie, resolves Auto with `matchMedia`, and sets `data-bs-theme`.
- [ ] Add a jsdom test that runs the file against each cookie value and asserts the same result as the
  service's `effectiveMode`.
- [ ] Switch the demo to the file, using an assets glob plus `<script src>`, and delete its inline
  copy.

### NB3 — `bs-theme-toggle` (G1)
- [ ] Promote the demo toggle into `@mintplayer/ng-bootstrap/theming`: an Auto / Light / Dark dropdown,
  with an icon for the effective scheme. Its labels (`autoLabel`, `lightLabel`, `darkLabel`) are
  inputs.
- [ ] Switch the demo to the exported component and delete its copy.
- [ ] Add a unit test: selecting an option calls `setMode`.

### NB4 — Component colour fixes (D12)
- [ ] Scheduler scrollbar (`scheduler.styles.scss:1575,1579,1584`): convert to `--bs-*` tokens.
- [ ] Query-builder `--bs-btn-color: #646b72` (`mp-query-builder.light.scss:109`): convert to a
  `--bs-*` token.
- [ ] Datatable hover fallback (`datatable.light.scss:410,421,615`): use
  `rgba(var(--bs-emphasis-color-rgb), .04)`.
- [ ] Card fallbacks (`card-global.styles.scss:32-33,39-40`, `mp-card.element.scss:87`): convert to
  `--bs-*` tokens.
- [ ] S2 (confirmed broken): repaint the `mp-select` caret and the `mp-checkbox` switch knob as masks
  coloured with `currentColor` / `var(--bs-*)`, following the accordion pattern
  (`accordion.styles.scss:100-112`). Apply the same to the navbar toggler (`navbar.styles.ts:425`) and
  the carousel indicators (`carousel.styles.ts:205`). Delete the dead `[data-bs-theme=dark]`
  shadow-sheet rules.
- [ ] S3 findings:
  - scheduler scrollbar → `scrollbar-color`
  - code-snippet "Copied!" colour → `var(--bs-white)`
  - dropdown overlay pane → give it a `var(--bs-body-bg)` surface and a border
  - demo tab-control glyph → `var(--bs-body-color)`
- [ ] Calendar header regression from #393: add height, padding, background and border to
  `.calendar-nav` (`mp-calendar.element.scss:71`).
- [ ] Verify each fix in the demo in both themes through the MCP.

### NB5 — Release
- [ ] Add a CHANGELOG entry: the storage is now a cookie (breaking: stored choices reset), plus
  `provideBsTheme`, SSR, the preboot file, the toggle, and the colour fixes.
- [ ] Bump the version to `22.20.0`.
- [ ] Run the unit tests and build all libraries (log to file).
- [ ] Do the demo browser check in both schemes, including the R2 matrix and a cross-tab check.
- [ ] Open the PR, merge it, and confirm `22.20.0` is on npm before M6.

---

## Part B — `MintPlayer.Spark`

M1–M5 can be built against a local ng-bootstrap build (`npm pack`) while NB5 is in flight.

### M1 — Shell palette (G7; needs owner approval before commit)
- [x] Define `--spark-shell-{topbar,sidebar,main}-{bg,color}` tokens under `[data-bs-theme=light]` and
  `[data-bs-theme=dark]`, built from Bootstrap tokens. Delete every hex value in
  `spark-shell.component.scss`. *(B-2 as built: declared under `:root, [data-bs-theme=light]` and
  `[data-bs-theme=dark]` through `::ng-deep`, same token expressions in both schemes —
  topbar/sidebar bg `--bs-tertiary-bg` (#f8f9fa / #2b3035), their colour and main colour
  `--bs-body-color` (#212529 / #dee2e6), main bg `--bs-body-bg` (#fff / #212529). Topbar and sidebar
  are one chrome surface, separated from main by a `--bs-border-color` border. The `theme-color`
  metas of all five apps follow the chrome: #f8f9fa / #2b3035.)*
- [x] Hover and active tints (`spark-shell.component.scss:79,90,94`,
  `spark-program-units.component.scss:13,27,31`) → `rgba(var(--bs-emphasis-color-rgb), …)` (D6).
  *(Also: the program-units accordion's `--bs-body-bg` is set to the sidebar colour, so the menu is
  not a darker card on the sidebar in dark mode.)*
- [x] **Remove `sidebarTheme`** (G8): the input, the binding at `spark-shell.component.html:23-25`, and
  the spec at `:195-200`. Re-check `spark-program-units.component.scss:8`.
- [x] Action bar (owner decision, 2026-10-01): `.spark-actionbar` paints
  `var(--spark-shell-main-bg, var(--bs-body-bg))`, exactly the page surface — not a lighter, darker or
  transparent bar, so it never appears to float, and the main-coloured 24px above it at rest is not a
  band. Opaque, with its `--bs-border-color` bottom border. Measured in DemoApp at rest and stuck, both
  schemes, query and detail page: bar bg ≡ `<main>` bg (rgb(255,255,255) / rgb(33,37,41)), the bar
  is the top element at its own centre while content scrolls underneath.
- [x] Found during B-2: at phone width the query grid's column headers ran into each other. The
  datatable sizes its columns once, from the first render with both columns and rows; the grid's
  data columns ship with the first page and so register one render after the rows, so only the
  `__sparkRowActions` column was measured and the rest shared the leftover equally (45px each at
  390px). Fixed in `spark-query-grid`: the row-actions column waits for the data columns, and
  `loadData` clears the previous query's columns.
- [x] `spark-auth-bar`: change `btn-outline-light` to theme-aware buttons. *(B-1: a
  `spark-auth-bar-btn` outline drawn in `currentColor`, hover/active via `color-mix`, so it reads on
  whatever topbar B-2 settles on.)* *(B-2: measured in HR on the final topbar, 14.6:1 light /
  10.2:1 dark for text and border. Signed in at 390px the user name pushed Passkeys/Logout onto a
  second line below the topbar; the bar is now one non-wrapping flex line, the name is hidden below
  `sm` and truncated (max 16rem, full name in `title`) above it.)*
- [x] Tune the palette in DemoApp through the MCP, in both themes and at phone width. Check WCAG AA
  for text, links, hover and active.
- [x] **Show light and dark screenshots to the owner, and commit only after approval.** *(Approved by
  the owner 2026-10-01.)*

### M2 — Theme wiring in the shell (G6, D3)
- [x] `spark-shell` injects `BsThemeService`, so Auto is live even with the toggle hidden.
- [x] Add the `themeToggle` input (default `true`). It renders `bs-theme-toggle` in the topbar, with
  labels from the Spark translations. *(Shipped API: the toggle is a cycling button whose `modes`
  input takes `{ mode, label, announcement, icon }[]`; the shell maps `BS_THEME_DEFAULT_MODES` and
  overrides `label` ← `theme.switchTo{Auto,Light,Dark}` and `announcement` ← `theme.{auto,light,dark}`,
  new keys in `App_Data/translations.json`. The toggle sits outside the trailing-edge `@if`, so an app
  that replaces `*sparkShellTopbarEnd` keeps it.)*
- [x] Spec: the toggle renders or hides with the input, and the service is instantiated either way.
- [x] Bump the `@mintplayer/ng-bootstrap` dependency in ng-spark and ng-spark-auth to `^22.20.0`, and
  bump both packages' minor versions. *(Also `@mintplayer/web-components ^2.17.0`: root dependency
  and an ng-spark peer dependency. ng-spark 22.25.0, ng-spark-auth 22.15.0.)*

### M3 — Library colour fixes and apps wiring (D5, D7, G2)
- [x] Toasts, query-list hover, reputation badge and review-queue (D7).
- [x] In the four apps *(five: QnA too)*:
  - Add the `project.json` assets glob for `bs-theme-preboot.js`
    (`node_modules/@mintplayer/web-components/theming` → `/`).
  - In `index.html`, add `<script src="bs-theme-preboot.js">` before the stylesheets. *(After
    `<base href>`, because the src is relative: before `<base>` a deep link would resolve it against
    its own path and get the SPA fallback.)*
  - Add the `color-scheme` meta and the two `theme-color` metas, plus
    `<meta name="bs-theme-default-mode" content="auto">` **before** the script, which reads it
    synchronously.
- [x] **S5** Check the order and the copied file in the built output. *(Verified in B-2 against the
  served app: `index.html` has `<base>` → default-mode meta → `bs-theme-preboot.js` → stylesheets, and
  `/bs-theme-preboot.js` answers 200 with the script on a deep link.)*
- [x] Add a test over every app: the built output contains `bs-theme-preboot.js`, and `index.html`
  references it before any stylesheet `<link>`. *(B-1: `ng-spark/shell/src/apps-theme-preboot-wiring.spec.ts`
  checks the sources — `index.html` order and attributes, the `project.json` asset entry, and that the
  file exists in `node_modules` — not a built output; S5 still checks one real build.)*
- [x] **S7** Check that the cookie write works through the dev proxy and under the https profile.
  *(Measured 2026-10-01 in DemoApp through the MCP, both launch profiles, read with `cookieStore.get`
  and the context's cookie jar. Auto → click → `light` → click → `dark`; the attribute is `dark` at
  `DOMContentLoaded` after a reload and on a deep link. `https` profile (`https://localhost:5007`;
  its http URL 307s to https, so plain http is unreachable there): `Path=/`, `SameSite=Lax`,
  `Secure`, host-only, ~1-year expiry. `http` profile (`http://localhost:5008`): the same without
  `Secure`. The `cookieDomain` case was not measured: no Spark app sets it, and on `localhost` a
  `Domain` cookie is not meaningful; the shipped writer deletes the host-only cookie first when a
  domain is set (read in `theming/index.mjs`).)*

### M4 — CodeCoverage and identity-provider pages (D10, D11)
- [x] CodeCoverage:
  - [x] Delete `shell/shell.component.scss:14-16,23` and `sidebarTheme="dark"`. *(Done in B-2, with
    the palette; the toggler override went too.)*
  - [x] Line tints → `--bs-*-bg-subtle`.
  - [x] `bg-light` → `bg-body-tertiary`, and `text-bg-light` → `text-bg-secondary`.
  - [x] Add a `BadgeRendererTests` pin: the output has no `prefers-color-scheme`, `<style` or
    `currentColor`.
- [x] Identity-provider pages:
  - Put the page CSS into one shared C# constant, with light values, a `[data-bs-theme=dark]` block,
    `@media (prefers-color-scheme: dark) { :root:not([data-bs-theme=light]) {…} }`, and
    `color-scheme: light dark`. *(`Endpoints/ConnectPageTheme.cs`: `--idp-*` tokens, one dark-token
    string used by both blocks; the pages keep their layout rules, written against the tokens.
    Consent's inline emphasised-scope colours became a class.)*
  - Each endpoint reads the `bs-theme-mode` cookie and renders `data-bs-theme` for `light` or `dark`.
  - The QR code stays white. *(There is no QR code on these four pages; the enrolment QR is in
    ng-spark-auth's account pages. Logout's two bare messages are not themed.)*
- [x] Identity-provider tests (`tests/MintPlayer.Spark.Tests/IdentityProvider/OidcPageThemeTests.cs`,
  over `/connect/login` and `/connect/two-factor`, plus `ExplicitTheme` unit cases):
  - Cookie `dark` → `data-bs-theme="dark"` is rendered.
  - No cookie → no attribute, and the media block is present.
  - A garbage cookie value → no attribute. Never echo the value into the HTML.
- [x] Bump the identity-provider csproj version *(preview number: 11.0.0-preview.92)*.

### M5 — E2E and docs
- [x] Add a `ColorScheme? colorScheme` parameter to `PageFactory.NewPageAsync`, following the timezone
  pattern. *(Plus `NewContextAsync`, for case 4's two pages in one context.)*
- [x] Add a Fleet `DarkModeTests` class covering R7: *(`tests/MintPlayer.Spark.E2E.Tests/DarkModeTests.cs`,
  four tests for the six checks, to keep Angular boots down in the shared rate-limit bucket: the
  first two checks share one page, cases 2 and 3 share one. The toggle is
  `spark-shell bs-theme-toggle button` (open shadow root). Built, not yet run — the run is M6.)*
  - The attribute is present at `DOMContentLoaded` (init script).
  - The main area's computed background is dark.
  - R2 matrix case 1: in Auto, `EmulateMediaAsync(Light)` turns the page light with no reload.
  - Case 2: toggle Dark, and it is dark at once and after a reload (cookie).
  - Case 3: toggle Dark, then `EmulateMediaAsync(Light)`, and it stays dark.
  - Case 4: two pages in one context, a toggle in A re-themes B.
- [x] Write `docs/guide-theming.md`: *(linked from README; also covers the `<head>` order, the
  toggle's `themeToggle` input, the action bar, the identity-provider pages and the style-query
  limit)*
  - setup (the assets glob, the script line, the metas)
  - the sticky-choice rule
  - the cookie and `cookieDomain`
  - overriding palettes with CSS custom properties (D2), and why Sass `-dark` overrides don't reach
    component CSS
  - the `--spark-shell-*` tokens
  - the badge exception
- [x] Update memory.

### M6 — Full sweep, browser check, PR
- [ ] Switch to the published `@mintplayer/ng-bootstrap@^22.20.0` and run `npm install` from the repo
  root.
- [ ] Run all five test projects of the `.slnx`, the ng-spark and ng-spark-auth vitest suites, and the
  app specs (logs to file).
- [ ] Browser check through the MCP of every app in light and dark, against PRD R4 (shell, program
  units, query list and grid with selected rows, column-filter overlay, PO form and detail, toasts,
  modals, moderation, auth pages) and R5 (CodeCoverage file view, README box, badges).
- [ ] Do the R2 matrix by hand in one app.
- [ ] Check the version diff: minor bumps only.
- [ ] Open the PR against `master`, with the PRD linked and the breaking changes listed (removed
  `sidebarTheme`; stored theme choices reset).
