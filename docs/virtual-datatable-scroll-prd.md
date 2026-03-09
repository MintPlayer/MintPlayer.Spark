# PRD: Virtual Datatable Horizontal Scrolling & Column Alignment

## Problem

The `bs-virtual-datatable` component renders the header and body as **two separate `<table>` elements**:
- Header: inside `<bs-table [isResponsive]="true">` (which wraps in a `.table-responsive` div with `overflow-x: auto`)
- Body: inside `<cdk-virtual-scroll-viewport>` containing its own `<table>`

Both tables use `table-layout: fixed` with equal column count, so columns are distributed evenly. However:

1. **No horizontal scrolling**: With `table-layout: fixed` and narrow columns, text is truncated. The table should scroll horizontally when content exceeds the available width, rather than clipping text.
2. **Column misalignment**: The header and body tables can have different widths due to the vertical scrollbar in `cdk-virtual-scroll-viewport`. Currently mitigated with `scrollbar-gutter: stable`, but horizontal scroll positions are not synchronized.
3. **Scroll sync**: When the body scrolls horizontally, the header must scroll in tandem (and vice versa), so columns stay aligned.

## Current DOM Structure (ng-bootstrap 21.10.0)

```
bs-virtual-datatable
  div.virtual-datatable-container          (flex column, height: 100%)
    bs-table [isResponsive]="true"         (flex-shrink: 0)
      div.table-responsive                 (overflow-x: auto  -- from bs-table)
        table (table-layout: fixed)
          thead > tr > th*N
    bs-table-styles                        (display: contents)
      cdk-virtual-scroll-viewport          (flex: 1 1 auto, min-height: 0, overflow: auto)
        table (table-layout: fixed)
          tbody > tr*N > td*N
```

Source files (ng-bootstrap repo):
- Template: `libs/mintplayer-ng-bootstrap/virtual-datatable/src/virtual-datatable/virtual-datatable.component.html`
- Styles: `libs/mintplayer-ng-bootstrap/virtual-datatable/src/virtual-datatable/virtual-datatable.component.scss`
- Component: `libs/mintplayer-ng-bootstrap/virtual-datatable/src/virtual-datatable/virtual-datatable.component.ts`

## Requirements

### R1: Horizontal scroll instead of text truncation
- Columns must NOT use `text-overflow: ellipsis` or `overflow: hidden` on `<td>`.
- When column content is wider than the available space, the table scrolls horizontally.
- Both header and body tables must have the same total width (driven by content or a shared minimum).

### R2: Synchronized horizontal scrolling
- Scrolling the body horizontally must scroll the header by the same amount.
- Scrolling the header horizontally must scroll the body by the same amount.
- Implementation: listen to `scroll` events on both containers and synchronize `scrollLeft`.

### R3: Vertical scrollbar alignment
- The header must account for the body's vertical scrollbar width so columns stay aligned.
- Use `scrollbar-gutter: stable` on both containers, or dynamically measure and pad.

### R4: Column width consistency
- Both tables must produce identical column widths.
- `table-layout: fixed` can remain, but both tables must share the same total width.
- Alternative: use `table-layout: auto` with `white-space: nowrap` on cells, and synchronize column widths via JavaScript after render.

## Proposed Approach

### Option A: CSS-only with `table-layout: fixed` (simpler, less flexible)
- Keep `table-layout: fixed` on both tables.
- Remove `overflow-x: auto` from the header's `.table-responsive` wrapper.
- Wrap both the header `bs-table` and `cdk-virtual-scroll-viewport` in a single horizontally-scrollable container.
- Both tables scroll together since they share the same scroll parent.
- Vertical scrolling remains on `cdk-virtual-scroll-viewport` only.
- **Limitation**: All columns are equal width; content may still be clipped.

### Option B: JavaScript-synced scroll with `table-layout: auto` (more flexible)
- Use `table-layout: auto` with `white-space: nowrap` on `<td>` and `<th>` elements.
- After each render/data change, measure body column widths and apply them to header columns (or vice versa) using inline styles or CSS custom properties.
- Synchronize `scrollLeft` between the header's `.table-responsive` div and the `cdk-virtual-scroll-viewport` via `scroll` event listeners.
- The header's responsive wrapper handles horizontal scroll for the header; the viewport handles both horizontal and vertical scroll for the body.
- **Pro**: Columns size to content. **Con**: Requires JS measurement after render.

### Option C: Single scroll container wrapping both (recommended)
- Remove horizontal scroll from the header's `.table-responsive` wrapper (`overflow-x: visible` or remove `isResponsive`).
- The `cdk-virtual-scroll-viewport` already has `overflow: auto` — it handles both horizontal and vertical scrolling for the body.
- Add a **wrapper div** around both the header table and the viewport, with `overflow-x: auto`. This single container scrolls both tables horizontally in sync.
- The header table sits above the viewport, outside the vertical scroll, but inside the horizontal scroll container.
- Both tables use the same width (either `table-layout: fixed` for equal columns, or `auto` with synced widths).

```
div.virtual-datatable-container
  div.horizontal-scroll-wrapper             (overflow-x: auto)
    bs-table [isResponsive]="false"         (no own horizontal scroll)
      table
        thead > tr > th*N
    cdk-virtual-scroll-viewport             (overflow-x: hidden, overflow-y: auto)
      table
        tbody > tr*N > td*N
```

- The viewport's horizontal overflow is disabled (`overflow-x: hidden`); horizontal scrolling is handled by the wrapper.
- Vertical scrolling is handled by the viewport alone.
- **Pro**: No JS sync needed, columns always aligned, single scroll position.
- **Con**: Requires changes to the ng-bootstrap virtual-datatable component template and styles.

## Scope

This fix should be implemented in the **@mintplayer/ng-bootstrap** virtual-datatable component, not in ng-spark. The current `scrollbar-gutter` and `text-overflow: ellipsis` workarounds in `spark-query-list.component.scss` should be removed once the upstream fix is in place.

## Current Workarounds in ng-spark (to be removed)

File: `node_packages/ng-spark/src/lib/components/query-list/spark-query-list.component.scss`

```scss
// These should be removed once the ng-bootstrap fix ships:
::ng-deep bs-virtual-datatable {
  td {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .virtual-datatable-container > bs-table .table-responsive,
  cdk-virtual-scroll-viewport {
    scrollbar-gutter: stable;
  }
}
```

## Acceptance Criteria

- [ ] Virtual datatable scrolls horizontally when content exceeds available width
- [ ] Header columns align with body columns at all scroll positions
- [ ] No text truncation/ellipsis — full cell content is visible when scrolled into view
- [ ] Vertical scrolling continues to work via the CDK virtual scroll viewport
- [ ] Regular (non-virtual) `bs-datatable` is unaffected
- [ ] Works with `table-layout: fixed` (equal columns) and `table-layout: auto` (content-sized columns)
