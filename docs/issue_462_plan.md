# Plan — Issue #462: dark mode (one unit of work, two repositories)

Requirements, decisions (G1–G8 from the owner, D1–D14) and spikes are in
[issue_462_PRD.md](issue_462_PRD.md). This file is the order of work.

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
- [ ] Define `--spark-shell-{topbar,sidebar,main}-{bg,color}` tokens under `[data-bs-theme=light]` and
  `[data-bs-theme=dark]`, built from Bootstrap tokens. Delete every hex value in
  `spark-shell.component.scss`.
- [ ] Hover and active tints (`spark-shell.component.scss:79,90,94`,
  `spark-program-units.component.scss:13,27,31`) → `rgba(var(--bs-emphasis-color-rgb), …)` (D6).
- [ ] **Remove `sidebarTheme`** (G8): the input, the binding at `spark-shell.component.html:23-25`, and
  the spec at `:195-200`. Re-check `spark-program-units.component.scss:8`.
- [ ] `spark-auth-bar`: change `btn-outline-light` to theme-aware buttons.
- [ ] Tune the palette in DemoApp through the MCP, in both themes and at phone width. Check WCAG AA
  for text, links, hover and active.
- [ ] **Show light and dark screenshots to the owner, and commit only after approval.**

### M2 — Theme wiring in the shell (G6, D3)
- [ ] `spark-shell` injects `BsThemeService`, so Auto is live even with the toggle hidden.
- [ ] Add the `themeToggle` input (default `true`). It renders `bs-theme-toggle` in the topbar, with
  labels from the Spark translations.
- [ ] Spec: the toggle renders or hides with the input, and the service is instantiated either way.
- [ ] Bump the `@mintplayer/ng-bootstrap` dependency in ng-spark and ng-spark-auth to `^22.20.0`, and
  bump both packages' minor versions.

### M3 — Library colour fixes and apps wiring (D5, D7, G2)
- [ ] Toasts, query-list hover, reputation badge and review-queue (D7).
- [ ] In the four apps:
  - Add the `project.json` assets glob for `bs-theme-preboot.js`.
  - In `index.html`, add `<script src="bs-theme-preboot.js">` before the stylesheets.
  - Add the `color-scheme` meta and the two `theme-color` metas.
- [ ] **S5** Check the order and the copied file in the built output.
- [ ] Add a test over every app: the built output contains `bs-theme-preboot.js`, and `index.html`
  references it before any stylesheet `<link>`.
- [ ] **S7** Check that the cookie write works through the dev proxy and under the https profile.

### M4 — CodeCoverage and identity-provider pages (D10, D11)
- [ ] CodeCoverage:
  - Delete `shell/shell.component.scss:14-16,23` and `sidebarTheme="dark"`.
  - Line tints → `--bs-*-bg-subtle`.
  - `bg-light` → `bg-body-tertiary`, and `text-bg-light` → `text-bg-secondary`.
  - Add a `BadgeRendererTests` pin: the output has no `prefers-color-scheme`, `<style` or
    `currentColor`.
- [ ] Identity-provider pages:
  - Put the page CSS into one shared C# constant, with light values, a `[data-bs-theme=dark]` block,
    `@media (prefers-color-scheme: dark) { :root:not([data-bs-theme=light]) {…} }`, and
    `color-scheme: light dark`.
  - Each endpoint reads the `bs-theme-mode` cookie and renders `data-bs-theme` for `light` or `dark`.
  - The QR code stays white.
- [ ] Identity-provider tests:
  - Cookie `dark` → `data-bs-theme="dark"` is rendered.
  - No cookie → no attribute, and the media block is present.
  - A garbage cookie value → no attribute. Never echo the value into the HTML.
- [ ] Bump the identity-provider csproj version (minor).

### M5 — E2E and docs
- [ ] Add a `ColorScheme? colorScheme` parameter to `PageFactory.NewPageAsync`, following the timezone
  pattern.
- [ ] Add a Fleet `DarkModeTests` class covering R7:
  - The attribute is present at `DOMContentLoaded` (init script).
  - The main area's computed background is dark.
  - R2 matrix case 1: in Auto, `EmulateMediaAsync(Light)` turns the page light with no reload.
  - Case 2: toggle Dark, and it is dark at once and after a reload (cookie).
  - Case 3: toggle Dark, then `EmulateMediaAsync(Light)`, and it stays dark.
  - Case 4: two pages in one context, a toggle in A re-themes B.
- [ ] Write `docs/guide-theming.md`:
  - setup (the assets glob, the script line, the metas)
  - the sticky-choice rule
  - the cookie and `cookieDomain`
  - overriding palettes with CSS custom properties (D2), and why Sass `-dark` overrides don't reach
    component CSS
  - the `--spark-shell-*` tokens
  - the badge exception
- [ ] Update memory.

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
