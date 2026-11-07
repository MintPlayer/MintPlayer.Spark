# PRD + plan — The column filter panel forgets its ticks when reopened

Status: **implemented** (2026-11-07), on ng-bootstrap **22.35.0** / ui-core 1.9.0 / web-components
3.7.0, which ship ng-bootstrap#465 (PR #466). Found the same day, right after #594 made the checkboxes
visible. Evidence in §9.
Follows [`query_column_filter_PRD.md`](query_column_filter_PRD.md) (#431), which introduced the panel.

## 1. The bug

On a query list, open a column's filter popover, tick one or more values, close it and open it again.
Every checkbox is unticked, but the grid is still filtered. The filter is intact; the panel just doesn't
show it. The "≠" (inverse) toggle and a non-listable column's free-text box reset the same way.

That is worse than cosmetic. Ticking another value on the reopened panel calls `ctx.apply([thatValue])`
and **replaces** the existing selection instead of extending it, so the user silently loses the values
they had ticked.

## 2. Evidence

Measured with Playwright on DemoApp `/query/people` (ng-bootstrap 22.34.0, ui-core 1.8.0), on the first
column's filter:

| Step | `spark-column-filter-panel` DOM node | `ctx().selection.values` | panel `selected()` | boxes ticked | rows |
|---|---|---|---|---|---|
| Tick "Jonas Dewulf" | original (tagged) | `[Jonas Dewulf]` | `[Jonas Dewulf]` | `[true, false]` | 1 |
| Close | removed | — | — | — | 1 |
| Reopen | **new instance** (tag gone) | `[Jonas Dewulf]` | **`[]`** | **`[false, false]`** | 1 |

So the datatable hands the new panel the correct selection, and the panel ignores it.

## 3. Cause

`libs/node_packages/ng-spark/column-filter/src/spark-column-filter-panel.component.ts`:

- `selected = signal<DistinctValue[]>([])` (:59), `inverse = signal(false)` (:61) and
  `freeText = signal('')` (:63) are local state that starts empty.
- `isChecked` (:86-88) reads only `selected()`; `ctx().selection` is never read anywhere.

The panel was written as if it lived as long as the grid. It doesn't: the datatable renders the
`bsDatatableFilter` slot only while that column's popover is open. In ng-bootstrap master `6ad1d95c8`:

- `@if (fc.open && fc.open.custom)` around the `ngTemplateOutlet`
  (`datatable/src/generated/datatable.view.generated.html:97-101`).
- On close, `toggle` sets `filterColumn: null` (`mintplayer-ui-core/datatable/src/datatable.core.ts:1036-1039`),
  so the embedded view, and our panel with it, is destroyed. Reopening creates a new one.

## 4. Which repository owns the fix: **both**, independently

**This repository** owns the data bug: the panel must render from `ctx.selection` (§6). That is needed
whatever ng-bootstrap does, because the selection can also change from outside the panel (URL presets,
`clear`, a `filters` model write).

**ng-bootstrap** owns the lifecycle: filed as
[mintplayer-ng-bootstrap#465](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/465). The
owner's convention since the overlay refactor is that overlays render their content **once** and then only
show or hide it, so state not bound to data (scroll position, unapplied input, focus) survives a close. The
datatable's filter slot breaks that. Modal and offcanvas still keep their content in Angular, but only
because it is projected through `<ng-content>`, which Angular creates once even inside the
`@if (p_view.rendered)` that every overlay core has used since #458/#459
(`ui-core/overlay/src/presence.ts:26`). The filter slot is a template rendered through `ngTemplateOutlet`
inside `@if (fc.open && fc.open.custom)`, so it gets a new view on every open.

The Spark fix does not wait for #465, and does not depend on it.

What ng-bootstrap does today, and documents as its design (as of master `6ad1d95c8`):

- `docs/prd/datatable-filter-row.md:489`: "The search term resets on close, the selection does not …
  a reopened panel having forgotten the selection would be a data-loss bug."
- The slot is "invoked once per open" (`:411`, `:526`), and `FilterContext`'s doc comment
  (`ui-core/datatable/src/types/filter.ts`) says "The slot re-renders with a fresh snapshot on every change".
- The datatable owns the state (`filters` + `filterDrafts`, `datatable.core.ts:164-176`) and rebuilds
  `ctx.selection` from it on every open (`selectionsOf`, `datatable.core.ts:281-290`;
  `selectionFromFilter`, `datatable.ts:533-547`).
- An inverse toggled with **no** values selected is kept too: `filterDrafts` stores `{ inverse }`
  (`datatable.core.ts:973`) and `selectionFromFilter` returns it (`datatable.ts:539`).

## 5. Options considered

| Option | Verdict |
|---|---|
| **A. Panel reads its state from `ctx.selection`** (no local copy of what the datatable owns) | **Chosen.** It is the contract ng-bootstrap documents, Spark-only, and leaves a single source of truth. It also fixes the same problem for a selection restored from the URL or set programmatically, which a kept-alive panel would not. |
| B. Keep the popover content alive across closes (ng-bootstrap) | **Filed separately as ng-bootstrap#465**, as the default behaviour rather than an option, to match the overlay convention. On its own it is **not** a fix for this bug: a kept panel with a local copy of the selection still drifts when the selection changes elsewhere (URL presets, `clear`, a `filters` model write). It complements A. |
| C. Seed the local signals once on init from `ctx.selection` | Rejected. It fixes reopening but keeps two copies of the state, which drift whenever the datatable changes the selection while the panel is open. |

## 6. Design

In `SparkColumnFilterPanelComponent`:

1. **`selected`** becomes `computed(() => this.ctx().selection.values)`. `toggle` builds `next` from it
   and calls `ctx().apply(next, inverse())` without a local `set`; the datatable's fresh snapshot updates
   the view. (The row in §2 shows `ctx.selection` already reflects the tick before the next interaction.)
2. **`inverse`** becomes `computed(() => this.ctx().selection.inverse)`. `toggleInverse` calls
   `ctx().apply(this.selected(), !this.inverse())`.
3. **`freeText`** stays a writable signal, because it holds what the user is typing before Apply. It
   becomes a `linkedSignal` over the applied text: `String(this.ctx().selection.values[0]?.value ?? '')`.
   Because a `linkedSignal`'s source is compared by value, a new `ctx` snapshot carrying the same applied
   text does not overwrite the user's typing; a changed applied text (Apply, Clear, or a restore) does.
4. **`clear`** only calls `ctx().clear()` (plus resetting `freeText`, which the link then confirms).
5. **Search box** (`spark-column-filter-panel.component.html:15-19`): bind `[value]="ctx().term"`. It is
   uncontrolled today and only starts empty because the panel is recreated per open. Once
   ng-bootstrap#465 / PR #466 keeps the panel, the box would keep the old text after a reopen while the
   table has reset `term` to `''` and lists every value.
6. Doc comment on the class: add that the panel holds **no** filter state and renders from
   `ctx.selection`, so it is correct whether the datatable creates it per open (today) or keeps it
   (ng-bootstrap#465). That way, the next edit won't reintroduce a local copy.

Template: unchanged. `isChecked` keeps comparing by value with `sameValue`, so a value the datatable
labels with its fallback label (`labels.filterValue(value)`, when it is not in the current distinct list)
still matches.

## 7. Plan

| # | Milestone | Files |
|---|---|---|
| M1 | Derive `selected`/`inverse` from `ctx`, `freeText` as `linkedSignal`; update `toggle`, `toggleInverse`, `applyFreeText`, `clear` and the class doc | `ng-spark/column-filter/src/spark-column-filter-panel.component.ts` |
| M2 | Unit spec (the panel has none yet): mount with a stub `FilterContext` whose `selection` already holds a value, with `inverse: true`, and with a free-text value → box ticked / toggle pressed / text shown; ticking a second value calls `apply` with **both**; unticking calls `apply` without it | new `spark-column-filter-panel.component.spec.ts` |
| M3 | Browser check in DemoApp `/query/people`: tick, close, reopen → still ticked; tick a second → both applied, rows widen; ≠ survives a reopen; Clear empties it | — |
| M4 | Note in `query_column_filter_PRD.md` §5.8 that the panel is per-open and stateless, linking here | `docs/query_column_filter_PRD.md` |

One PR. It changes only `ng-spark`, so the merged PR gets `backport: release/angular-22.x`.

## 8. Acceptance criteria

- Reopening a filter popover shows exactly the values that are applied, ticked.
- Ticking another value on a reopened panel adds to the selection; nothing is silently dropped.
- The inverse toggle and the free-text box show the applied state after a reopen.
- Typing in the free-text box is not reset by unrelated re-renders while the panel is open.
- Works on the current ng-bootstrap (per-open views) and keeps working once #465 keeps the view alive:
  nothing in the panel may assume it is freshly created on each open.

## 9. As built (2026-11-07)

**Why the upgrade alone was not enough.** On 22.35 the panel survives a close, so its old local copy of
the ticks would have survived too, and the reported symptom with it. Three cases still needed this
change: a selection set from outside the panel (URL presets never showed as ticks), the search box (an
uncontrolled input kept its old text after the table reset `term`), and `ctx.readonly` (new in 22.35).

**Beyond §6**, from the ng-bootstrap session's handoff: the grid binds `[filters]` both ways
(`datatableFilters` became a signal; presets and a query switch write it; `toDatatableFilters` maps the
wire shape), and the panel disables its editing controls under `ctx.readonly`, keeping the search box.

**Browser check**, Playwright on DemoApp `/query/people` (2 people), after a host restart:

| Step | Same panel node | Ticks | ≠ | Search box | Rows |
|---|---|---|---|---|---|
| Tick Jonas | yes | `[true, false]` | off | `''` | 1 |
| Type `zzz`, close, reopen | **yes** | `[true, false]` | off | **`''`** | 1 |
| Tick Pieterjan | yes | `[true, true]` | off | `''` | 2 |
| ≠, close, reopen | yes | `[true, true]` | **on** | `''` | "No data" |
| Clear | yes | `[false, false]` | off | `''` | 2 |
| `?FullName=Jonas%20Dewulf`, open | — | **`[true, false]`** (chip shown) | off | — | 1 |
| …then tick Pieterjan | — | `[true, true]` | off | — | 2 |
| Switch to `/query/companies` and back, open | — | `[false, false]` | off | — | 2 |

**Tests:** new `spark-column-filter-panel.component.spec.ts` (8 tests: seeded ticks, tick adds, untick
removes, outside change followed, inverse shown and flipped, search term bound, free text seeded,
read-only). Ran with the grid spec: 2 files, 115 tests passed. `ngc -p tsconfig.lib.json` clean.
