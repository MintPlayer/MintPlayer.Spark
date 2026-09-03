# PRD — Adopting ng-bootstrap's light-DOM conversion (22.18.0)

Status: proposed, not implemented
Upstream: `MintPlayer/mintplayer-ng-bootstrap` PR #410 (fixes their #408), squashed as `dbe4808b`
Related here: #348 attribute descriptions (PR #357), #324 program units

## Summary

ng-bootstrap 22.18.0 drops the shadow root on the four components that adopt consumer DOM —
`mp-datatable`, `mp-treeview`, `mp-tree-select` and the `mp-query-builder` family — and replaces the
browser-enforced boundary with build-time attribute rescoping (`[data-mps="…"]`, the same device as
Angular's `_ngcontent-c3`). The other ~30 slot-based components keep their shadow roots.

For this repository the change is **overwhelmingly a fix we want**, and it arrives whether we plan
for it or not: our root `package.json` declares `^22.17.0`, so 22.18.0 is already inside the range
and lands on the next `npm install`. The work in this PRD is therefore not "should we take it" but
"take it deliberately, delete the workarounds it obsoletes, and check the one direction it makes
worse".

## The bug this closes for us

On the CodeCoverage **Account** page, the `[i]` attribute-description buttons from #348 render
correctly in the top card and render broken inside the grid. That is not two bugs — it is one
boundary, observed from both sides.

`SparkAttributeDescriptionComponent`
(`libs/node_packages/ng-spark/attribute-description/src/spark-attribute-description.component.ts`)
depends on three separate stylesheets, **all of which live in `document.head`**:

| layer | source | where it comes from |
|---|---|---|
| `btn btn-link p-0 ms-1 align-baseline` (line 29) | global Bootstrap | `_bootstrap.scss`, injected via `project.json` `styles[]` |
| `spark-icon`'s `span ::ng-deep svg{width:1em;height:1em}`, and the `bi bi-*` font fallback | its component CSS + global icon font | `spark-icon.component.ts:25-29`; `bootstrap-icons.css` |
| `:host{display:inline-block}`, `.spark-attribute-description-trigger{opacity;font-size}` (lines 38-58) | its own component CSS | Angular emulated encapsulation → `document.head` |

Where it works — `spark-po-detail.component.html:86`, a `<dt>` in a card — the component is in the
document tree, so all three reach it.

Where it breaks — `spark-query-grid.component.html:45`, inside `*bsDatatableColumn` — the Angular
embedded view is handed to `mp-datatable` as a header renderer and mounted **inside its shadow
root**. Document CSS does not cross a shadow boundary, so all three layers are inert at once: no
button chrome, no icon sizing, no glyph. The markup was always correct; only the CSS was out of
reach. `spark-reference-picker.component.html:68` is the same path, a second instance of the same
break.

One detail worth keeping straight, because it narrows what to look at: **the tooltip popup was never
broken.** `BsTooltipDirective` is CDK-Overlay-based and attaches its panel to the overlay container
in `document.body`, carrying its own styles with it — so it already escaped the shadow root. Only the
`[i]` *trigger* misrendered, which is exactly what was observed. The upgrade will, however, shift the
tooltip's *anchor geometry*: once the trigger gains its `.btn-link` padding and 1em SVG sizing its
box changes, so the popup will sit slightly differently. That is a correction, not a regression, but
it is a visible change.

On the Coverage **Account** page specifically there is no hand-written template — it is the generic
Spark detail page (`app.routes.ts:45` → `sparkRoutes({ poDetail: … })`, page component
`apps/CodeCoverage/.../spark/po-detail-page.component.ts:38-59`). The working `[i]` is the `<dt>` in
`spark-po-detail.component.html:85-86`; the broken one is in the sub-query grid reached via
`spark-po-detail` → `spark-query-card.component.html:48` → `spark-query-grid.component.html:33,45` —
on Account, the repositories grid.

After the upgrade the grid renders in the light DOM and all three layers apply by ordinary cascade.
**No code change of ours is required to fix it.** That is the point of the upstream design: the
problem stops existing rather than being bridged.

## Where we touch the converted components

Narrower than feared. Two of the four converted components are unused here.

| component | usages |
|---|---|
| `bs-datatable` | `spark-query-grid.component.html:33-81` (the one Spark grid); `spark-reference-picker.component.html:61-77` (modal row picker) |
| `bs-tree-select` | `spark-po-form.component.html:84-93` (Reference-array editor) |
| `bs-treeview` | none |
| `bs-query-builder` | none |

Indirect hosts of the grid: `spark-query-card.component.html:48`, `spark-query-list.component.html:85`.
Every CodeCoverage custom cell renderer (12 of them, under
`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/spark/`) renders inside the grid's row template and
is therefore on the changed path.

Two things we do **not** have to do, both worth stating because they are the expensive parts of this
kind of migration elsewhere:

- **No `adoptLightStyles` call is needed.** That API is only required when a consumer hosts a
  light-tier component inside *its own* shadow root. This repo defines no custom elements and uses
  no `ViewEncapsulation.ShadowDom` — grep for `attachShadow` / `customElements.define` /
  `encapsulation:` returns nothing outside `node_modules`. `spark-shell` *slots* into `mp-shell`,
  which is light DOM on our side and needs no mirror.
- **No SSR injector wiring.** No app here is server-rendered; all five ClientApps are browser-only
  builds behind an ASP.NET Core SPA proxy.

## What we get to delete

Several places in ng-spark are *written around* the boundary and say so in their own comments. They
are not merely now-unnecessary; they are now **actively misleading**, and one of them will start
double-styling.

- `libs/node_packages/ng-spark/grid/src/spark-grid-cell.component.scss:1-14` — the entire file is a
  comment explaining that its `@use 'chip' / 'swatch'` rules apply only on the PO-detail path and
  never inside the grid. That premise is now false, and the rules will begin applying in grid cells
  for the first time, on top of the inline styles written to compensate for their absence.
- `spark-grid-cell.component.html:14-20, 22-29` — inline `style="width:1.5em;height:1.5em"` (colour
  swatch) and `style="max-height:2.5em…"` (image), written specifically because scoped CSS could not
  reach the shadow root.
- `apps/CodeCoverage/.../spark/account-avatar-renderer.component.ts:14,21` — inline
  `border-radius:.25rem` with the same stated rationale.
- `spark-grid-cell.component.spec.ts:106-116` — a test literally named *"sizes itself inline, because
  the grid cell lives in a shadow root"*. It will keep passing while its reason evaporates, which is
  the worst failure mode a test has.

## The direction this makes worse

Emulated encapsulation is one-way: it stops our styles leaking *out*, but page CSS now leaks *in* to
component internals. Upstream accepted this explicitly (it is the same property Angular's
`ViewEncapsulation.Emulated` has). Three concrete exposures here:

1. **Load-bearing virtual-scroll CSS.**
   `spark-query-list.component.scss:12-37` does
   `::ng-deep bs-datatable { display:flex; --mp-datatable-virtual-max-height:100%; mp-datatable { flex:1 1 auto; min-height:0 } }`.
   Both halves change meaning: the nested `mp-datatable` selector now matches a light-DOM element
   whose own rescoped rules also apply, and the custom property no longer crosses a boundary to be
   consumed. Virtual scrolling on large Coverage grids depends on this height chain resolving. This
   is the single highest-risk item in the change and must be checked in a browser, not by reading.

2. **Global Bootstrap now reaches datatable internals.** `_bootstrap.scss` ships bare-element rules
   for `table`, `th`, `td` and `input`. Those have never applied inside `mp-datatable` before and now
   will — borders, padding, striping and checkbox chrome in the grid can shift.

3. **Bundle budgets.** All five apps set `anyComponentStyle` 4kB/8kB and `initial` 1MB/1.5MB
   (e.g. `apps/CodeCoverage/CodeCoverage/ClientApp/project.json:36`). Styles moving out of Lit shadow
   roots into a light-DOM sheet land on the `initial` budget. CI builds all five apps on every PR, so
   a budget breach fails the build rather than shipping — but it fails *us*, and we should know
   before we push.

## Verification reality

There are **no visual-regression or screenshot tests in this repo**, and the .NET Playwright E2E
suite touches only sign-in forms (`input#email`, `bs-alert`) — nothing in a grid. The vitest specs
deliberately route around the datatable because Lit does not upgrade under jsdom
(`spark-query-grid.component.spec.ts:194,373,419,566-572`).

So CI cannot confirm this worked. Verification is necessarily browser-driven and manual, and the
**Coverage Account page is the named acceptance case**: the `[i]` in the grid must render identically
to the `[i]` in the top card.

## Goals

1. Move to ng-bootstrap 22.18.0 deliberately, with the peer range updated so downstream consumers of
   `@mintplayer/ng-spark` cannot resolve a version where the grid `[i]` is broken.
2. Verify the #348 `[i]` renders correctly inside the grid on the Coverage Account page.
3. Delete the shadow-DOM workarounds and correct the comments and the test that assert the old model.
4. Confirm no leak-in regression: virtual scroll, grid chrome, cell renderers, bundle budgets.
5. Correct the documentation that now states something false.

## Non-goals

- Adopting `adoptLightStyles` or the SSR injector — neither applies here (justified above).
- Touching the shadow-DOM seams that are still correct: `mp-shell::part(hamburger)`,
  `mp-accordion::part(content)`, `mp-code-snippet::part(annotation-*)`. Those components were **not**
  converted and their `::part()` selectors remain the right mechanism. Changing them would be a
  regression.
- Any `bs-treeview` / `bs-query-builder` migration — unused here.
- Bumping `@mintplayer/ng-spark-auth`'s ng-bootstrap peer. It uses no converted component; `^22.2.0`
  stays.

## Versioning

`@mintplayer/ng-spark` takes a **minor** bump. Per `CLAUDE.md` the npm major tracks the Angular major
and nothing else — Angular 22 has not moved, so the major digit does not move, exactly as upstream
shipped its own breaking change as 22.17.0 → 22.18.0. CI publishes on push to master, so the version
in `package.json` must be right in the PR.

## Risks

| risk | likelihood | impact | mitigation |
|---|---|---|---|
| Virtual-scroll height chain breaks | medium | high — large Coverage grids unusable | browser check on a 1k+ row grid before merge; keep the `::ng-deep` block until measured |
| Global `table`/`td` rules restyle grid chrome | medium | medium | side-by-side visual check of the Coverage grids |
| Chip/swatch rules double up with inline styles | high | low | remove the inline styles in the same PR |
| `initial` bundle budget breach | low | medium — CI build fails | build all five apps locally before pushing |
| Fix silently regresses later | medium | medium | replace the shadow-root-premise test with one asserting the real contract |

## Acceptance criteria

- [ ] Coverage **Account** page — the reference case is
      <https://coverage.mintplayer.com/po/account/Accounts%2F48772716> (live, currently on 22.17.0, so
      it shows the *broken* state; verify the fix against the same PO on a local `dotnet run` of
      `apps/CodeCoverage` before it reaches production). The `[i]` inside the repositories grid
      renders with button chrome, icon sizing and glyph identical to the `[i]` in the top card, and
      its tooltip opens on hover and on focus.
- [ ] The reference-picker modal's `[i]` (`spark-reference-picker.component.html:68`) likewise.
- [ ] Virtual scrolling still works on a large Coverage grid; header stays put, rows recycle.
- [ ] All five Angular apps build in production config without a budget warning or error.
- [ ] `nx run-many --target=test` green across the JS projects.
- [ ] No `::part()` / `::slotted()` selector targeting a converted component remains (there are none
      today — this is a guard, not a task).
- [ ] The still-valid `::part()` seams on `mp-shell` / `mp-accordion` / `mp-code-snippet` are
      untouched and those UIs are unchanged.
- [ ] No comment or doc in the repo still claims the datatable renders into a shadow root.

## Out of scope / genuinely not doing

- Upstream's own unfinished measurements (their S5 component-internal restyling inventory, and S8
  virtual-scroll frame time vs master). Those are theirs, and neither blocks us.
- Adding visual-regression testing to CI. It is the real gap this change exposes, and it is a
  materially larger piece of work than this upgrade; recording it here as a known gap, not parking
  scope.
