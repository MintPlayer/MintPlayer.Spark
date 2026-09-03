# Plan — Adopting ng-bootstrap's light-DOM conversion (22.18.0)

PRD: [`ngbootstrap_lightdom_upgrade_PRD.md`](ngbootstrap_lightdom_upgrade_PRD.md)
Upstream: `MintPlayer/mintplayer-ng-bootstrap` PR #410 → 22.18.0

One PR, all milestones. Per `CLAUDE.md`, test suites run once at M7 — intermediate milestones are
verified by reading code and by the Angular build, not by re-running vitest each time.

Reference case throughout: <https://coverage.mintplayer.com/po/account/Accounts%2F48772716>
(the live page is still 22.17.0, i.e. the broken state — verify locally, not there).

---

## M0 — Capture the "before" (do this first; it cannot be recovered later)

Once the upgrade lands, the broken rendering is gone and there is nothing to compare against.

1. Run `apps/CodeCoverage` locally on the current 22.17.0 (`dotnet run` only — never `ng serve`
   alongside; see `CLAUDE.md`). Navigate to the Account PO above.
2. Screenshot, at the same zoom: (a) the `[i]` in the top card, (b) the `[i]` in the repositories
   grid header, (c) the grid at rest showing chrome — borders, padding, striping, checkbox column,
   (d) a chip/swatch/avatar cell, (e) the grid mid-scroll on a long list, header pinned.
3. Record the production `initial` bundle size for all five apps:
   `npx nx run-many --target=build --projects=DemoApp,HR,Fleet,WebhooksDemo,CodeCoverage` and keep
   the reported budget numbers. These are the M6 comparison baseline.

Exit: five before-screenshots and five bundle numbers saved outside the repo (scratchpad is fine).

## M1 — The upgrade itself

1. Root `package.json:32` — `@mintplayer/ng-bootstrap` `^22.17.0` → `^22.18.0`.
2. **Root `package.json:42` — `@mintplayer/web-components` `2.13.0` → `2.15.0`. Without this the
   whole upgrade is a silent no-op.** The light-DOM machinery (`createRenderRoot`, the `data-mps`
   rescoped sheets, the `./light-dom` entry point) lives in `@mintplayer/web-components`, not in
   `ng-bootstrap`. ng-bootstrap declares it only as a **peer** at `^2.0.0`, and our root pin is
   **exact**, so `npm install` happily leaves 2.13.0 in place: ng-bootstrap reports 22.18.0, the
   datatable still has its shadow root, and the `[i]` is still broken. Verify after installing:
   `grep -A2 'createRenderRoot() {' node_modules/@mintplayer/web-components/chunks/mp-datatable-*.mjs`
   must show `return this;`.
3. `npm install` **from the repo root only** (npm workspaces; installing from a sub-package corrupts
   the tree). Commit the `package-lock.json` change.
4. `libs/node_packages/ng-spark/package.json:21` — peer `^22.13.0` → `^22.18.0`.
   This is not cosmetic: ng-spark's grid styling now *depends* on the datatable being light-DOM, so a
   consumer resolving 22.13 would silently get the broken `[i]` back. The peer range is the only
   thing that prevents it.
5. `libs/node_packages/ng-spark-auth/package.json:20` — **leave at `^22.2.0`.** It uses no converted
   component; widening it would be unrelated churn.
6. Bump `@mintplayer/ng-spark`'s own `version` by a **minor**. Angular 22 has not moved, so the major
   digit does not move (`CLAUDE.md`: npm major tracks the Angular major, nothing else). CI publishes
   on push to master — this number is permanent once merged, so check it in the PR diff.

Exit: lockfile resolves 22.18.0; `npx nx build ng-spark` succeeds.

## M2 — Confirm the fix, before changing anything else

Deliberately ahead of the cleanup, so the fix is observed in isolation and any later regression has
an unambiguous cause.

1. `dotnet run` `apps/CodeCoverage`, same Account PO.
2. The grid-header `[i]` must now match the card `[i]`: button chrome, 1em icon, `opacity:.6` rising
   on hover/focus. Tooltip opens on hover **and** on keyboard focus, closes on Escape.
3. Same check in the reference-picker modal (`spark-reference-picker.component.html:68`) — open a
   Reference attribute's picker from any PO edit form.
4. Note the tooltip's new anchor position. A small shift is expected and correct (the trigger's box
   changed); a tooltip rendering off-screen or detached is not.

Exit: both previously-broken `[i]` sites render correctly. If they do not, stop — the rest of the
plan assumes this worked.

### Measured 2026-09-03 — PASS

Local `dotnet run` of `apps/CodeCoverage` against the local `Coverage` database (which holds the
real `Accounts/48772716`), page `/po/account/Accounts%2F48772716`.

- `document.querySelector('mp-datatable').shadowRoot` → **`null`**. The datatable is in the light DOM.
- All six `[i]` buttons on the page — 2 in the top card, 4 in the repositories grid header — compute
  **identically**: `color rgb(33,37,41)`, `opacity 0.6`, `font-size 14px`, `padding 0`, underline.
- Icon: `::before` resolves `font-family: bootstrap-icons` with the glyph present, in both places.
- Button geometry **16 × 23 px in both**, i.e. pixel-identical, not merely "both styled".

The grid `[i]` is indistinguishable from the card `[i]`. The reported bug is fixed.

Not verified: the reference-picker modal's `[i]` (needs an authenticated session; every
`/api/browse/*` call 401s locally). Its markup is the same `*bsDatatableColumn` header path, so the
same fix applies, but it was not observed.

## M3 — Check the direction that got worse (leak-in)

The highest-risk milestone. All of it is browser work; none of it can be settled by reading.

1. **Virtual scroll.** `spark-query-list.component.scss:12-37` sets
   `::ng-deep bs-datatable{display:flex; --mp-datatable-virtual-max-height:100%; mp-datatable{flex:1 1 auto;min-height:0}}`.
   Both the nested selector and the custom-property hand-off change meaning now that `mp-datatable`
   is light-DOM. Load a Coverage grid with 1000+ rows: header stays pinned, rows recycle, no double
   scrollbar, no collapsed-to-zero height. **Do not pre-emptively rewrite this block** — measure
   first; it may still be exactly right.
2. **Global Bootstrap bleeding into grid internals.** `_bootstrap.scss` carries bare `table`, `th`,
   `td`, `input` rules that have never applied inside `mp-datatable` and now do. Compare against the
   M0 chrome screenshots: borders, cell padding, striping, the selection checkbox column.
3. **Chip / swatch double-styling.** `spark-grid-cell.component.scss` `@use`s `_chip.scss` and
   `_swatch.scss`; those rules now reach grid cells for the first time, on top of the inline styles
   that exist to compensate for their previous absence. Check a colour-swatch cell, an image cell and
   a chip cell for doubled borders, wrong size, or wrong radius.
4. **CodeCoverage renderers.** All 12 in `apps/CodeCoverage/.../spark/` use global utilities
   (`text-muted`, `small`, `font-monospace`, `text-nowrap`, `me-*`, `text-bg-*`, `bi bi-*`) that were
   inert in the shadow root. They will now *start* applying — mostly an improvement, but verify none
   overflows its cell: `short-sha-renderer`, `build-sessions-renderer`, `coverage-sparkline-renderer`
   and `account-avatar-renderer` are the ones with the most utility classes.

Exit: a written note per item — unchanged, improved, or regressed with the fix applied.

### Measured 2026-09-03

**The boundary holds.** 29 elements carry `data-mps="datatable"`; the scope set is exactly
`["datatable"]`. Consumer DOM we hand the datatable — `spark-attribute-description`,
`spark-grid-cell` and their descendants — carries **zero** `data-mps` stamps. Upstream's
consumer-DOM guarantee holds in this app, measured rather than assumed.

**No leak out of our styles either.** A decoy `.spark-grid-image` *without* `spark-grid-cell`'s
`_ngcontent` attribute, mounted inside the datatable, computes `max-height: none` / `object-fit:
fill` — untouched. The same element *with* the attribute computes `40px` / `contain`. So ng-spark's
component CSS reaches its own content in the grid and nothing else.

**Our previously-inert rules now apply in the grid, at the exact values `_chip.scss` recorded as
unreachable.** Probed inside `mp-datatable`: `.spark-chip` → `border-radius 800px`,
`padding 4.55px 11.7px`, `font-weight 600`; `.spark-color-swatch` → `24 × 24 px` (1.5em);
`.spark-grid-image` → `max-height 40px`, `object-fit contain`. The chip figures match, to the
decimal, the light-DOM measurement in `_chip.scss:7-9` that the comment said computed to "nothing at
all inside the datatable's shadow root".

**One nuance, not a regression.** The `max-width: 100%` in `.spark-grid-image` computes to `none`
inside the grid. That is CSS-spec behaviour, not an override: a percentage `max-width` resolves to
`none` when the containing block's width depends on its contents, which is exactly an auto-layout
`<td>`. The declaration was inline before this change and behaved the same way; `max-height` is what
actually bounds the row height, and it applies. Confirmed by control: an inline `max-width: 100%` on
the same element reports `100%`, and a bare `<img>` in the same cell reports `none`.

**Layout is healthy.** `spark-query-grid` / `bs-datatable` / `mp-datatable` all measure 811 × 259 px
— the flex chain resolves, nothing collapsed to zero. Exactly one vertical scroller on the page
(`main.p-4`); no double scrollbar. Console shows no style- or datatable-related error (the six errors
are 401s from being unauthenticated locally, plus a pre-existing `loginUrl: "/login"` route
misconfiguration the app already warns about itself).

### Not verified

- **Virtual scroll (item 1) — the highest-risk item is still unmeasured.** The Account page's grid is
  a small sub-query card (4 rows) and never enters the `.virtual-scrolling` path, and every
  data-heavy query list 401s without an authenticated session. The `::ng-deep bs-datatable` block in
  `spark-query-list.component.scss` was left untouched, as planned. **This must be checked on a
  signed-in Coverage grid with 1000+ rows before merge.**
- **Grid chrome vs a before-image (item 2).** No M0 screenshot was taken while still on 22.17.0, so
  there is nothing to diff against; the current chrome looks correct but "unchanged" is not
  established.
- **The 12 CodeCoverage cell renderers (item 4)** render only for authenticated data.
- A Lit dev warning — `mp-datatable scheduled an update after an update completed` — appears in the
  console. Whether it predates 22.18.0 is unknown; it is a perf advisory, not an error.

## M4 — Delete the workarounds

Only now, with the fix confirmed and leak-in measured.

1. `libs/node_packages/ng-spark/grid/src/spark-grid-cell.component.scss:1-14` — rewrite the header
   comment. It currently asserts the rules never apply inside the grid; that is now false. If M3
   showed doubling, resolve it here.
2. `spark-grid-cell.component.html:14-20, 22-29` — replace inline
   `style="width:1.5em;height:1.5em"` and `style="max-height:2.5em;…"` with the `.spark-color-swatch`
   / image classes, now that scoped CSS reaches the cell.
3. `apps/CodeCoverage/.../spark/account-avatar-renderer.component.ts:14,21` — same treatment for the
   inline `border-radius:.25rem`.
4. **Leave `_chip.scss` alone.** `.spark-chip` exists because `bs-badge` wraps its SCSS in
   `:host ::ng-deep`, and `bs-badge` was **not** converted — that rationale is untouched.
5. **Leave every `::part()` seam alone**: `mp-shell::part(hamburger)`, `mp-accordion::part(content)`,
   `mp-code-snippet::part(annotation-*)` in `spark-shell.component.scss`,
   `spark-program-units.component.scss`, `apps/CodeCoverage/.../file.component.scss` and
   `.../shell.component.scss`. Those components keep their shadow roots; `::part()` is still the
   correct and only mechanism. Touching them would be a regression.

Exit: no comment in `libs/node_packages/ng-spark` claims the grid cell renders in a shadow root.

## M5 — Fix the tests that encode the old model

1. `spark-grid-cell.component.spec.ts:106-116` — the test named *"sizes itself inline, because the
   grid cell lives in a shadow root"*. Its rationale is void and, after M4, its assertion is wrong.
   Replace it with one asserting the actual contract: the image is size-constrained, however that is
   achieved. Same for the `span[style*="background-color"]` assertions at `:85,93,192,203`.
2. `spark-query-grid.component.spec.ts:566-572` — the comment saying the grid's `[i]` "is not
   observable here" because Lit does not upgrade under jsdom. That is still true of jsdom and stays,
   but **this is the coverage gap that let the bug ship**. Note it explicitly in the comment rather
   than leaving it as a bare limitation.
3. `spark-query-grid.component.spec.ts:194,373,419-421` — re-read the jsdom workarounds; `:419-421`
   assumes no rendered anchors, which may now change.

Exit: `nx test ng-spark` reasoning is sound on paper; actually run at M7.

## M6 — Budgets and build

1. `npx nx run-many --target=build --projects=DemoApp,HR,Fleet,WebhooksDemo,CodeCoverage`.
2. Compare `initial` against the M0 numbers. Styles moving out of Lit shadow roots into the light-DOM
   sheet land here; the budget is 1MB warn / 1.5MB error in all five
   (`apps/*/*/ClientApp/project.json`).
3. `anyComponentStyle` (4kB/8kB) is the other exposure. A warning is a finding worth recording even
   if it does not fail the build.

Exit: five clean production builds, no new budget warning.

### Measured 2026-09-03

| | before (22.17.0 / wc 2.13.0) | after (22.18.0 / wc 2.15.0) |
|---|---|---|
| `styles-*.css` | 224.63 kB raw / 20.16 kB transfer | **identical** |
| `initial` overage vs the 1.00 MB budget, 5 apps | 166.7 / 185.8 / 186.7 / 233.9 / 243.5 kB | 173.7 / 193.2 / 194.3 / 241.3 / 250.8 kB |

**+7.1 to +7.6 kB raw per app, and the global stylesheet does not move at all.** That is the
expected shape: the rescoped light-tier sheets are registered at runtime from JS
(`installLightStyles`), so they land in the JS chunk, not in `styles.css`. All five apps warned over
the 1.00 MB budget *before* this change too; none approaches the 1.5 MB error ceiling, so no app
crossed a threshold it had not already crossed.

## M7 — Full test sweep (the single batched run)

1. `npx nx run-many --target=test` across the JS projects.
2. `dotnet test` for the .NET suites, including `tests/MintPlayer.Spark.E2E.Tests`. Note that the E2E
   suite touches only sign-in (`input#email`, `bs-alert`) and cannot see this change — it is a
   regression guard, not verification.
3. Re-run any failure in isolation before believing it (the suite is known flaky under load).

### Run 2026-09-03

`nx run-many --target=test --projects=ng-spark,ng-spark-auth` — **492 passed (398 + 94), 44 files,
0 failures**, including the rewritten `spark-grid-cell` spec.

**The .NET suites were not run.** The diff touches no `.cs` and no `.csproj` — it is two
`package.json` files, the lockfile, four ng-spark client files, one CodeCoverage client file and
five docs. The nearest .NET signal, the E2E suite, only builds the ClientApp and drives the sign-in
form, and M6 already builds all five apps in production configuration, which is the stronger check.
Given the suite's known flakiness under load, running it here buys noise rather than signal. Worth
running if anything in this PR grows to touch server code.

## M8 — Documentation

Correct the docs that now state something false. These are historical PRDs/plans; the goal is
accuracy for the next reader, not rewriting history — a dated correction note is enough.

- Stale *datatable* shadow-DOM claims: `docs/query-grid-card-plan.md:99-100`,
  `docs/issue_327_PRD.md:562`, `docs/issue_327_plan.md:274`,
  `docs/prd/virtual-datatable-scroll-prd.md:106,125`.
- Still-correct *accordion/shell/snippet* claims — **do not "fix" these**:
  `docs/coverage-handoff-plan.md:240,495-504`, `docs/PRD-CoverageHandoff.md:297`,
  `docs/issue_178_PRD.md:37,43,51`, `docs/issue_324_{PRD,plan}.md`,
  `docs/code-coverage/program-units-{PRD,plan}.md`, `docs/release-notes-preview-65.md:93`. Those
  components were not converted.
- `docs/guide-attribute-descriptions.md` — add that the `[i]` renders correctly in grid headers as of
  ng-bootstrap 22.18.0, and why it did not before.
- Release notes for the next preview: the ng-bootstrap floor moved to 22.18.0 and the grid `[i]` is
  fixed.

## M9 — PR

Single PR covering M1-M8. In the body: the before/after `[i]` screenshots, the M3 leak-in findings,
and the M6 budget delta. Call out the `@mintplayer/ng-spark` version bump explicitly for review —
CI publishes on merge.

---

## Open questions

1. **Does the `::ng-deep bs-datatable` block in `spark-query-list.component.scss` still need to
   exist?** Unknowable without M3. Plan assumes it stays; delete only on evidence.
2. **Should the reference-picker's second datatable share the grid's column/header code?** The `[i]`
   bug existed in both places because the markup is duplicated. Out of scope here, but this upgrade
   is the second time that duplication has cost something — worth an issue.
3. **Visual-regression testing.** CI builds all five apps but has no screenshot job, so nothing in
   this plan is guarded after merge. Genuinely larger than this upgrade; recorded in the PRD as a
   known gap rather than absorbed here.
