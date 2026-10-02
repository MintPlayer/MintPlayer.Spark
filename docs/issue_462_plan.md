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
  done** (S5/S7 ticked, Fleet dark-mode E2E written but not run, theming guide). ~~Left: M6.~~
  **Reconciled 2026-10-02:** Part A is merged and published (ng-bootstrap PR #421, merged
  2026-10-01T06:55Z as `67244d46`; `@mintplayer/ng-bootstrap@22.20.0` and
  `@mintplayer/web-components@2.17.0` are on npm). M6 is done except the manual browser check of
  every app (R4/R5) and the hand-run R2 matrix, neither of which has a recorded result, and the PR
  still being a draft. `DarkModeTests` (the automated R2/R7 matrix) ran green in CI run 36888101219.
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

*(Reconciled 2026-10-02: all of Part A shipped in MintPlayer/mintplayer-ng-bootstrap#421, merged
2026-10-01 as `67244d46`, published as ng-bootstrap 22.20.0 / web-components 2.17.0 (`npm view`
confirms both; this repo's `package.json:33,43` and `package-lock.json` resolve them). Deviations
the owner accepted are in PRD §2b; items they replace are marked superseded below.)*

### NB0 — Spikes ✅
- [x] **S6** Record `BsThemeService` behaviour. Answered: Auto is live, an explicit mode is sticky,
  there's no `storage` listener, and the value is a plain string.
- [x] **S1** Confirm that the component CSS is precompiled with default Sass values. *(done: PRD §4.1
  "S1 — ✅ confirmed (2026-09-30)")*
- [x] **S4** Check what the demo theme-toggle depends on. *(done: PRD §4.1 "S4 — ✅ read")*
- [x] **S2** Check the select caret and form-switch inside shadow DOM under `data-bs-theme=dark`,
  measured in the demo through the MCP. *(done: PRD §4.1 "S2 — ❌ both broken (measured 2026-09-30)")*
- [x] **S3** Do the dark visual sweep of the component list in PRD §4. Record findings in the PRD
  (§4.1, "Spike results"). *(done: PRD §4.1 "S3 — dark sweep (measured 2026-09-30)")*

### NB1 — Cookie-backed theme service (G3, G4, D14) ✅ (ng-bootstrap #421)
- [x] `BsThemeService` reads and writes the `bs-theme-mode` cookie: not HttpOnly, `Path=/`,
  `SameSite=Lax`, `Max-Age=31536000`, and `Secure` only on HTTPS. **Delete the localStorage code, with
  no migration.** *(done: #421, document store writes `bs-theme-mode`, `Path=/`, `SameSite=Lax`, one
  year, `Secure` on https; `BS_THEME_STORAGE_KEY` removed. Measured in Spark by S7, M3 below.)*
- [x] Add `provideBsTheme({ cookieDomain?, defaultMode? })`. It is optional; without it the service
  uses a host-only cookie and `'auto'`. *(done with an accepted deviation, PRD §2b: `provideBsTheme`
  takes only `cookieDomain`; the default mode is the `<meta name="bs-theme-default-mode">`.)*
- [x] Cross-tab sync: post on `setMode` through `BroadcastChannel('bs-theme-mode')` and apply what
  other tabs send. *(done: #421 "Tabs stay in sync over `BroadcastChannel`"; Spark's
  `DarkModeTests` case 4 ran green in CI run 36888101219.)*
- [x] SSR: on the server, read the cookie from Angular's `REQUEST` token. For an explicit mode, set
  `data-bs-theme` on the server-rendered `<html>`. *(done: #421, "On the server it reads `REQUEST` +
  the meta and writes `data-bs-theme` synchronously".)*
- [x] Unit tests (stubbing `matchMedia`, `document.cookie` and `BroadcastChannel`): *(done: #421,
  web-components/ng/react/vue unit suites and the theme e2e specs green, per the PR body.)*
  - Auto + OS change re-themes.
  - Explicit + OS change has no effect.
  - A reload restores the mode from the cookie.
  - The `cookieDomain` option is applied.
  - The broadcast reaches another instance.
  - SSR with a cookie renders the attribute; SSR with Auto renders none.

### NB2 — `bs-theme-preboot.js` (G2) ✅ (ng-bootstrap #421)
- [x] Ship `theming/bs-theme-preboot.js` in the package. It is a plain ES5 IIFE with no module syntax.
  It reads the cookie, resolves Auto with `matchMedia`, and sets `data-bs-theme`. *(done, accepted
  deviation PRD §2b: shipped as `@mintplayer/web-components/theming/bs-theme-preboot.js`, generated
  by esbuild, build fails above 1 KB or on non-ES5; Spark's apps serve it, S5 below.)*
- [x] Add a jsdom test that runs the file against each cookie value and asserts the same result as the
  service's `effectiveMode`. *(done: the agreement test; its silent skip was review must-fix 2, fixed
  in `d075383b`, PRD §2b.)*
- [x] Switch the demo to the file, using an assets glob plus `<script src>`, and delete its inline
  copy. *(done: #421 "The shipped pre-boot script and the meta tag replace the inline scripts".)*

### NB3 — `bs-theme-toggle` (G1) ✅ (ng-bootstrap #421)
- [x] ~~Promote the demo toggle into `@mintplayer/ng-bootstrap/theming`: an Auto / Light / Dark dropdown,
  with an icon for the effective scheme. Its labels (`autoLabel`, `lightLabel`, `darkLabel`) are
  inputs.~~ *(Superseded, accepted deviation PRD §2b: `bs-theme-toggle` wraps `<mp-theme-toggle>`, a
  cycle button with a `modes` input `{ mode, label, announcement, icon }[]`; Spark uses it in M2.)*
- [x] Switch the demo to the exported component and delete its copy. *(done: #421, the toggle sits in
  each demo's navbar.)*
- [x] Add a unit test: selecting an option calls `setMode`. *(done: #421 `theme-toggle.spec.ts` e2e in
  all three demos plus the unit suites; Spark's `DarkModeTests` drives the toggle and ran green.)*

### NB4 — Component colour fixes (D12) ✅ (ng-bootstrap #421)
- [x] Scheduler scrollbar (`scheduler.styles.scss:1575,1579,1584`): convert to `--bs-*` tokens.
  *(done: ng-bootstrap master `scheduler.styles.scss:1573` `scrollbar-color: var(--bs-secondary-color)
  var(--bs-tertiary-bg)`.)*
- [x] Query-builder `--bs-btn-color: #646b72` (`mp-query-builder.light.scss:109`): convert to a
  `--bs-*` token. *(done: #421 "Hard-coded light values are now tokens: scheduler, query-builder, …")*
- [x] Datatable hover fallback (`datatable.light.scss:410,421,615`): use
  `rgba(var(--bs-emphasis-color-rgb), .04)`. *(done: #421, "datatable/treeview hover".)*
- [x] ~~Card fallbacks (`card-global.styles.scss:32-33,39-40`, `mp-card.element.scss:87`): convert to
  `--bs-*` tokens.~~ *(Superseded: left unchanged, accepted deviation PRD §2b — the fallbacks never
  fire because `--mp-card-border-color` / `--mp-card-bg` already resolve to `--bs-*` tokens.)*
- [x] S2 (confirmed broken): repaint the `mp-select` caret and the `mp-checkbox` switch knob as masks
  coloured with `currentColor` / `var(--bs-*)`, following the accordion pattern
  (`accordion.styles.scss:100-112`). Apply the same to the navbar toggler (`navbar.styles.ts:425`) and
  the carousel indicators (`carousel.styles.ts:205`). Delete the dead `[data-bs-theme=dark]`
  shadow-sheet rules. *(done with an accepted deviation, PRD §2b: CSS style queries
  `@container style(--mp-color-mode: dark)` instead of masks, for the select caret, the
  query-builder caret and the switch knob; every dead `[data-bs-theme` rule stripped, with a
  conformance spec that fails the build if one returns.)*
- [x] S3 findings:
  - scheduler scrollbar → `scrollbar-color` *(done, above)*
  - code-snippet "Copied!" colour → `var(--bs-white)` *(done: #421)*
  - ~~dropdown overlay pane → give it a `var(--bs-body-bg)` surface and a border~~ *(Superseded, PRD
    §2b: rejected because it would affect every dropdown; the calendar got its own opaque surface.)*
  - demo tab-control glyph → `var(--bs-body-color)` *(done: #421 changes
    `tab-control.component.scss`)*
- [x] Calendar header regression from #393: add height, padding, background and border to
  `.calendar-nav` (`mp-calendar.element.scss:71`). *(done: #421 "The calendar month header is
  restored"; `.calendar-nav` at `mp-calendar.element.scss:75` on ng-bootstrap master.)*
- [x] Verify each fix in the demo in both themes through the MCP. *(done: #421 "Manual browser pass
  (Chromium): light, dark, nested light and forced colours on every changed surface".)*

### NB5 — Release ✅
- [x] Add a CHANGELOG entry: the storage is now a cookie (breaking: stored choices reset), plus
  `provideBsTheme`, SSR, the preboot file, the toggle, and the colour fixes. *(done: #421 changes
  `CHANGELOG.md`, "Breaking changes (listed in the CHANGELOG)".)*
- [x] Bump the version to `22.20.0`. *(done: ng-bootstrap master `libs/mintplayer-ng-bootstrap/package.json`
  is `22.20.0`.)*
- [x] Run the unit tests and build all libraries (log to file). *(done: #421 unit tests web-components
  5,771, ng 1,286, React 319, Vue 242, … green.)*
- [x] Do the demo browser check in both schemes, including the R2 matrix and a cross-tab check.
  *(done: #421 theme e2e specs in every engine in all three demos cover the toggle cycle, cookie,
  SSR attribute, `Vary`; plus the manual Chromium pass.)*
- [x] Open the PR, merge it, and confirm `22.20.0` is on npm before M6. *(done: #421 merged
  2026-10-01T06:55Z; `npm view @mintplayer/ng-bootstrap@22.20.0 version` → 22.20.0; publish run
  36827354045 "Published: 16, failed: 0", PRD §2b.)*

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
  `spark-shell bs-theme-toggle button` (open shadow root). ~~Built, not yet run — the run is M6.~~
  Ran green in CI run 36888101219 at `9296653b`: E2E 138 passed, 0 skipped.)*
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
- [x] Switch to the published `@mintplayer/ng-bootstrap@^22.20.0` and run `npm install` from the repo
  root. *(done: `package.json:33` `^22.20.0`, `:43` web-components `^2.17.0`; `package-lock.json`
  resolves 22.20.0 / 2.17.0.)*
- [x] Run all five test projects of the `.slnx`, the ng-spark and ng-spark-auth vitest suites, and the
  app specs (logs to file). *(done: CI run 36888101219 on `9296653b`, all green — Spark.Tests 3509
  (+1 skipped), CodeCoverage 1045, SourceGenerators 523, Client 106, E2E 138, vitest ng-spark 1009,
  ng-spark-auth 221. The later local commits (M8 test speed, docs; 28 as of `95c113b8`) passed the local
  `npm run test:affected` sweep on HEAD, all 13 projects green, 2026-10-02; they reach CI when the
  branch is pushed.)*
- [ ] Browser check through the MCP of every app in light and dark, against PRD R4 (shell, program
  units, query list and grid with selected rows, column-filter overlay, PO form and detail, toasts,
  modals, moderation, auth pages) and R5 (CodeCoverage file view, README box, badges).
  *(**Open, 2026-10-02: no recorded result.** What was measured is partial: the palette and action
  bar in DemoApp, the auth bar in HR, the query grid at 390px (B-2, PRD §2b), and the cookie in
  DemoApp (S7). No record covers Fleet, QnA or CodeCoverage (R5) in both themes, nor the moderation
  components, modals and column-filter overlay.)*
- [ ] Do the R2 matrix by hand in one app. *(Open as a manual step, no recorded result. Its four cases
  are automated in `DarkModeTests` (R7) and ran green in CI run 36888101219, so only the by-hand
  confirmation remains.)*
- [x] Check the version diff: minor bumps only. *(done 2026-10-02 against `origin/master`: ng-spark
  22.24.0 → 22.25.0, ng-spark-auth 22.14.0 → 22.15.0; the touched NuGet packages 11.0.0-preview.91 →
  preview.92 and the three new Contributions packages at preview.92. No major changed: npm stays 22
  (Angular 22), NuGet stays 11 (.NET 11). Untouched packages stay at preview.91.)*
- [ ] Open the PR against `master`, with the PRD linked and the breaking changes listed (removed
  `sidebarTheme`; stored theme choices reset). *(Partly: draft PR #465 is open against `master` and
  its body lists both breaks and closes #462. Still open: it is a draft, its body links the plans and
  `contributions_PRD.md` but not `issue_462_PRD.md`, and the head on GitHub is `9296653b`, 28+ commits
  behind the local branch.)*
