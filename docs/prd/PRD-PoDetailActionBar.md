---
title: PRD — PO Sticky Action Bar (Detail + List)
status: draft (open questions resolved 2026-04-27)
author: pieterjan@2sky.be
created: 2026-04-27
updated: 2026-04-27
related:
  - node_packages/ng-spark/po-detail/src/spark-po-detail.component.html
  - node_packages/ng-spark/query-list/src/spark-query-list.component.html
  - https://github.com/MintPlayer/mintplayer-ng-bootstrap commit 1bec50c6 (feat(priority-nav), released in @mintplayer/ng-bootstrap@21.18.0)
---

# PO Sticky Action Bar (Detail + List)

## 1. Summary

Replace the inline row of action buttons on the persistent-object **detail** and **list** pages with a **sticky grey action bar** anchored to the top of each page's container. The bar uses the new `bs-priority-nav` component from `@mintplayer/ng-bootstrap@21.18.0` so that buttons collapse into a "More" overflow menu when there isn't enough horizontal space. The page title becomes the only element at the top of the actual page content.

## 2. Motivation

Today, actions and custom-actions are rendered as a flat horizontal `<bs-button-group>` next to the page title on the detail page (`spark-po-detail.component.html:9-35`), and the list page mixes a "New" button + `extraActionsTemplate` slot into its title row (`spark-query-list.component.html:1-20`). Three problems:

1. **Actions scroll out of view** on long detail pages (which now include sub-query tables, multi-section forms, etc.). Users must scroll back up to delete, edit, or trigger a custom action.
2. **No graceful overflow.** When an entity has many custom-actions, the button row wraps awkwardly or pushes the title off-screen on narrow viewports.
3. **List-page custom actions are unreachable.** `CustomActionDefinition.showedOn === 'list' | 'both'` is part of the model but the list component doesn't render them today — server-side actions defined for list scope are simply invisible.

A sticky bar with priority-nav overflow solves all three — actions stay reachable, the layout adapts, the page gains a clear visual "command surface", and list-scoped custom actions get a home.

## 3. Current Behavior (baseline)

### 3.1 Detail page

**File:** `node_packages/ng-spark/po-detail/src/spark-po-detail.component.html:9-35`

```html
<div class="d-flex justify-content-between align-items-center mb-4">
  <h2>{{ currentItem.breadcrumb || currentItem.name }}</h2>
  <bs-button-group>
    <button (click)="onBack()">…</button>             <!-- Back: always visible -->
    @if (canEdit()) { <button>Edit</button> }
    <ng-container *ngTemplateOutlet="extraActionsTemplate" />
    @for (action of customActions(); track action.name) {
      <button class="btn btn-outline-primary" (click)="onCustomAction(action)">…</button>
    }
    @if (canDelete()) { <button>Delete</button> }
  </bs-button-group>
</div>
```

Filtering today:
- **Edit / Delete:** gated by `canEdit()` / `canDelete()` signals from `SparkService.getPermissions()`.
- **Custom actions:** `customActions()` is pre-filtered client-side to `showedOn === 'detail' || 'both'` at load time (`spark-po-detail.component.ts:104`). **The backend already filters by `security.json` permission per request** in `MintPlayer.Spark/Endpoints/Actions/ListCustomActions.cs:42` (`permissionService.IsAllowedAsync(actionName, typeName)`), so no additional client-side authz is needed — actions absent from `security.json` for the current user are never returned.
- **Back button:** always shown.
- **`extraActionsTemplate`:** consumer-injected slot (`spark-po-detail.component.ts:55`).

### 3.2 List page

**File:** `node_packages/ng-spark/query-list/src/spark-query-list.component.html:1-20`

```html
<div class="d-flex flex-column h-100">
  <div class="d-flex justify-content-between align-items-center mb-4 flex-shrink-0"
       [class.px-4]="isVirtualScrolling()">
    <h2>…</h2>
    <div class="d-flex gap-2">
      @if (extraActionsTemplate(); as extraActionsTpl) {
        <ng-container *ngTemplateOutlet="extraActionsTpl" />
      }
      @if (canCreate()) {
        <button class="btn btn-primary" (click)="onCreate()">
          <spark-icon name="plus-lg" /> {{ 'common.new' | t }}
        </button>
      }
    </div>
  </div>
  …
</div>
```

State today:
- **Only one built-in button:** "New" (gated by `canCreate()` from `SparkService.getPermissions()`).
- **Consumer slot:** `extraActionsTemplate` input.
- **No custom actions are loaded or rendered** on the list page, even though `CustomActionDefinition.showedOn === 'list' | 'both'` is part of the model.
- **No row selection / bulk-action concept** exists today. (Out of scope for v1 — see §8.)

## 4. Proposed Behavior

A sticky grey strip is rendered as the **first child of each page's outer container**, before the title. Inside it sits one `<bs-priority-nav>`. Each currently-allowed action becomes one `*bsPriorityNavItem`. The title remains in place but is no longer a flex-between row — the title is the only element left at the top of the actual page content.

### 4.1 Layout — Detail page

```
┌─ <bs-container> ───────────────────────────────────────────┐
│  ┌─ .spark-po-detail-actionbar (sticky, bg-body-tertiary) ┐│
│  │  <bs-priority-nav>                                     ││
│  │    [Back] [Edit] [<extra slot>] [custom×N] [Delete]    ││
│  │                                            [More ▾]   ││
│  └────────────────────────────────────────────────────────┘│
│  ┌─ .container ───────────────────────────────────────────┐│
│  │  <h2>{{ title }}</h2>                                  ││
│  │  …form / sub-queries / detail content…                 ││
│  └────────────────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────┘
```

The bar spans the **width of `<bs-container>`** (the PO-detail's outer wrapper). The title row reverts to a plain `<h2>` once the buttons are gone — no more flex-between row.

### 4.1b Layout — List page

```
┌─ <bs-container> (or whatever the list outer wrapper is) ───┐
│  ┌─ .spark-po-list-actionbar (sticky, bg-body-tertiary) ──┐│
│  │  <bs-priority-nav>                                     ││
│  │    [<extra slot>] [New] [list custom×N]                ││
│  │                                            [More ▾]   ││
│  └────────────────────────────────────────────────────────┘│
│  ┌────────────────────────────────────────────────────────┐│
│  │  <h2>{{ title }}</h2>                                  ││
│  │  …search box / result count…                           ││
│  │  …table / virtual-scroll viewport…                     ││
│  └────────────────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────┘
```

The list page already has `display: flex; flex-direction: column; h-100` on its root (`spark-query-list.component.html:1`). The action-bar becomes the first flex child (`flex-shrink-0`), the existing title/search row drops to the second slot, and the scrollable table viewport keeps its current `flex: 1; overflow: auto` behavior. Sticky positioning works against the table's scroll context for the bar to stay anchored.

**Note on virtual scrolling:** The list page already conditionally adds `px-4` to the title row when virtual-scrolling is active (to compensate for scrollbar offset). The same conditional padding rule must apply to the new action-bar so it horizontally aligns with the table content.

### 4.2 Sticky positioning

- **`position: sticky; top: 0; z-index: 400;`** on `.spark-po-detail-actionbar`. (Below `bs-shell`'s sidebar overlay at `z-index: 500` — using Bootstrap's sticky convention of `1020` would cover the mobile sidebar.)
- **Scroll context:** the demo shell's `<main>` element (`Demo/*/ClientApp/src/app/shell/shell.component.scss:50-57`) already owns `overflow: auto; height: 100vh`. Sticky binds to the nearest scrolling ancestor, which is `<main>`. No global layout changes needed.
- **No additional content padding** is required: a sticky element occupies its own slot in normal flow until it sticks; the title row naturally sits below it.
  - *(Counter-option: use `position: fixed`. Rejected — it would require manual width tracking to honor "width of the inner-page", and would force a hand-tuned `padding-top` on the content. Sticky gives both behaviors for free.)*

### 4.3 Items in the priority-nav

Each item becomes a `*bsPriorityNavItem` with a numeric priority. **Lower priority number = stays visible longer** (per `BsPriorityNavItemDirective` semantics).

**Detail page** — proposed default priorities and order:

| Item                       | Priority | Visible when                                  |
|---                         |---       |---                                            |
| Back                       | 1        | always                                        |
| Edit                       | 2        | `canEdit()`                                   |
| Delete                     | 3        | `canDelete()`                                 |
| Custom actions (loop)      | 10 + `action.offset` | `showedOn ∈ {'detail','both'}` (server-pre-filtered by `security.json`) |
| `extraActionsTemplate` slot| 50       | when consumer provides one                    |

**List page** — proposed default priorities and order:

| Item                       | Priority | Visible when                                  |
|---                         |---       |---                                            |
| New                        | 1        | `canCreate()`                                 |
| Custom actions (loop)      | 10 + `action.offset` | `showedOn ∈ {'list','both'}` (server-pre-filtered by `security.json`) |
| `extraActionsTemplate` slot| 50       | when consumer provides one                    |

Overflow defaults (both pages): `overflowFrom="end"` (the default — More menu anchored to the right edge), `hideEmptyMore=true` (no More button when nothing overflows), `[moreLabel]="lang.t('common.more')"`.

### 4.4 Styling

- **Background:** Bootstrap utility `bg-body-tertiary` (resolves to `--bs-tertiary-bg`, a subtle grey distinct from the demo shell's `#f8f9fa` `<main>` background — picking `bg-light` would make the strip blend into `<main>`).
- **Border:** `border-bottom` only, using `--bs-border-color`, to delineate from content while scrolling.
- **Padding:** `px-3 py-2` (matches the existing `mb-4` rhythm).
- **No new theme tokens.** Use Bootstrap CSS variables only.

The strip styling lives in:
- A new `spark-po-detail.component.scss` (the detail component currently has no stylesheet — wire it via the `@Component` decorator's `styleUrl`).
- The existing `spark-query-list.component.scss` for the list page (extend, don't replace).

A shared `.spark-actionbar` class (defined once, e.g. in a small shared SCSS partial under `node_packages/ng-spark/`) keeps both pages visually identical and avoids drift.

## 5. Implementation Outline

1. **Bump `@mintplayer/ng-bootstrap` to `^21.18.0`** in `node_packages/ng-spark/package.json` and root `package.json` (peer + dev). This is the version that ships commit `1bec50c6` (priority-nav). Run `npm install` from repo root.
2. **Update `spark-po-detail.component.ts`** imports:
   ```ts
   import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
   ```
   Add both to the component's `imports` array. Drop `BsButtonGroupComponent` import if no longer used.
3. **Restructure `spark-po-detail.component.html`:**
   - Insert the new `<div class="spark-actionbar">` as the **first child of the `@if (entityType(); as et)` gate** (per resolution of question 7.5 — inside the gate).
   - Inside it, render `<bs-priority-nav>` with one `*bsPriorityNavItem` per allowed action, replacing the current `<bs-button-group>`.
   - Remove the flex-between wrapper from the title row; leave `<h2>` standalone.
4. **Update `spark-query-list.component.ts` and `.html`:**
   - Add the same imports.
   - Add a `customActions` signal and load it from `SparkService.getCustomActions(entityType.id)`, filtering client-side to `showedOn === 'list' || 'both'` (mirrors the detail page; backend already gates by `security.json`).
   - Insert the `<div class="spark-actionbar">` as the first flex-child of the root `.d-flex.flex-column.h-100`. Apply the same `[class.px-4]="isVirtualScrolling()"` conditional padding the existing title row uses.
   - Move the `extraActionsTemplate` slot and the "New" button into the new bar as `*bsPriorityNavItem`s; drop them from the title row.
   - Wire `(click)="onCustomAction(action)"` to a new handler that calls `SparkService.executeCustomAction(this.type, action.name)` **without** a parent argument (list-scope actions have no specific entity in scope). Emit a new `customActionExecuted` output for symmetry with the detail page.
5. **Add `spark-po-detail.component.scss`** and extend `spark-query-list.component.scss` with `.spark-actionbar` rules — or extract to a shared partial (see §4.4). Wire via `styleUrl(s)` in the `@Component` decorators.
6. **Translation key:** add `'common.more'` to all language files used by ng-spark's `LanguageService`.
7. **No backend changes.** All filtering already exists server-side; the bar is a pure presentation refactor on the detail page and a presentation + new-feature change on the list page (rendering list-scoped custom actions).
8. **Sweep demo apps** for selectors that depend on the old DOM structure: grep `Demo/**/*.scss` and `Demo/**/*.ts` for `spark-po-detail`, `spark-query-list`, and any `extraActionsTemplate` consumers. Adjust if any consumer relied on the old flex-between row.

## 6. Acceptance Criteria

### Detail page
- [ ] A grey strip is visible at the top of the PO-detail container, spanning its full width.
- [ ] Scrolling the page down keeps the strip visible at the top of `<main>`.
- [ ] All actions previously rendered (Back, Edit when allowed, custom actions, Delete when allowed, `extraActionsTemplate`) appear inside the strip.
- [ ] The page title (`currentItem.breadcrumb || currentItem.name`) is the only element at the top of the actual page content, below the bar.
- [ ] Existing `customActionExecuted` output still emits with the same payload.
- [ ] No console errors when the entity has zero custom actions (just Back / Edit / Delete render).

### List page
- [ ] A grey strip with the same visual style is visible at the top of the list page.
- [ ] The "New" button (when `canCreate()`) renders inside the strip, not in the title row.
- [ ] `extraActionsTemplate` consumers render inside the strip.
- [ ] Custom actions with `showedOn === 'list' || 'both'` render as buttons in the strip (this is new functionality — verify against Fleet's `CarCopy/Car` example, which is a `'detail'`-scope action so should NOT appear; add a list-scope action to a demo for end-to-end verification).
- [ ] Clicking a list-scope custom action calls `SparkService.executeCustomAction(type, name)` with **no** parent argument.
- [ ] The strip respects the `[class.px-4]="isVirtualScrolling()"` rule so it horizontally aligns with the table content.
- [ ] The bar stays anchored when the table inside the list page is scrolled.

### Both pages
- [ ] On a narrow viewport where buttons would not fit, lower-priority buttons collapse into a "More ▾" overflow menu anchored to the right.
- [ ] Below the `sm` breakpoint, all items collapse into the More menu (`collapseAt="sm"`).
- [ ] Resizing the window dynamically reshuffles which buttons are visible vs. in the More menu.
- [ ] Clicking any button — visible or in the More menu — invokes the same handler it did before.
- [ ] The More label is rendered via `lang.t('common.more')`, and the key exists in every language file.
- [ ] Keyboard a11y: the More menu opens on Enter/Space and closes on Escape (priority-nav supplies `aria-haspopup` / `role=menu` natively).

## 7. Resolved Decisions

All initial open questions resolved on 2026-04-27:

1. **ng-bootstrap version availability — RESOLVED.** Use `@mintplayer/ng-bootstrap@21.18.0` (released with priority-nav). Bump in `package.json`.
2. **Scope of "action" — RESOLVED.** Built-ins **and** custom actions in the bar (Back, Edit, Delete, `extraActionsTemplate`, custom-actions on detail; New, `extraActionsTemplate`, list-scope custom-actions on list).
3. **Title-row buttons that survive — RESOLVED.** None. Title is the only element at the top of the actual page content. Confirmed: the detail view is read-only and no in-place save flow is planned.
4. **Translation of "More" — RESOLVED.** Bind `[moreLabel]="lang.t('common.more')"` and add the `common.more` key to all language files.
5. **Bar position relative to the entity-loaded gate — RESOLVED.** Inside the gate (consistency with the rest of the page).
6. **Z-index stacking — RESOLVED at `400`.** Initial implementation used Bootstrap's sticky convention (`1020`), but the bar then overlaid `bs-shell`'s mobile sidebar (`z-index: 500`). `400` keeps the bar above page content while staying below the sidebar overlay. If a sub-query sticky table-header clashes (`SparkSubQueryComponent` is the candidate), it'd need to sit below 400.
7. **Mobile behavior — RESOLVED.** `[collapseAt]="'sm'"` — below `sm`, all items collapse into the More menu.
8. **Per-action authz (custom actions) — RESOLVED.** Already handled server-side. `MintPlayer.Spark/Endpoints/Actions/ListCustomActions.cs:42` calls `permissionService.IsAllowedAsync(actionName, typeName)` per request, so actions denied by `security.json` are never returned to the client. No client-side authz needed; client-side `showedOn` filter remains the only client filter.
9. **List-page custom actions — RESOLVED.** In scope for v1. The list component currently does not render `showedOn === 'list' | 'both'` actions; this PRD adds that rendering as part of the list-page bar implementation (§5 step 4).

## 8. Out of Scope

- **Row selection / bulk actions on the list page.** No checkbox column or `selectedRows` signal exists today. List-scope custom actions in v1 operate without a parent (entity-type-level operations only). A future PRD can add per-row selection and selection-aware actions (using `CustomActionDefinition.selectionRule`, currently unused).
- **Per-action permission UI** beyond what `security.json` already enforces server-side.
- Animating the strip when it transitions from "in flow" to "stuck" (no shadow / elevation animation in v1).
- Customizing the priority-nav's More-menu styling (use the ng-bootstrap default).
- Persisting which actions a user has overflowed (priority is a static design-time concern, not user-tunable).

## 9. References

- Priority-nav source: `C:\Repos\mintplayer-ng-bootstrap\libs\mintplayer-ng-bootstrap\priority-nav\src\priority-nav\priority-nav.component.ts`
- Priority-nav demo: `C:\Repos\mintplayer-ng-bootstrap\apps\ng-bootstrap-demo\src\app\pages\advanced\priority-nav\priority-nav.component.{ts,html}`
- ng-bootstrap commit introducing priority-nav: `1bec50c6` (PR #283, 2026-04-26), released in `@mintplayer/ng-bootstrap@21.18.0`
- Current PO-detail template: `node_packages/ng-spark/po-detail/src/spark-po-detail.component.html`
- Current PO-detail handler logic: `node_packages/ng-spark/po-detail/src/spark-po-detail.component.ts:215-231`
- Current PO-list template: `node_packages/ng-spark/query-list/src/spark-query-list.component.html`
- Backend custom-action authz: `MintPlayer.Spark/Endpoints/Actions/ListCustomActions.cs:42` (`permissionService.IsAllowedAsync(actionName, typeName)`)
- security.json schema example: `Demo/Fleet/Fleet/App_Data/security.json:88-98` (resource format `"{ActionName}/{EntityTypeName}"`)
- Demo shell scroll context: `Demo/DemoApp/DemoApp/ClientApp/src/app/shell/shell.component.scss:50-57`
