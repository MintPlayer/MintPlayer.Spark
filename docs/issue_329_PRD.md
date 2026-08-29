# PRD — A single-child `AsDetail` cell carries its object, so a grid renderer can read it

> Issue [#329](https://github.com/MintPlayer/MintPlayer.Spark/issues/329). Baseline: master @ `fd570906`
> (`10.0.0-preview.67`, `@mintplayer/ng-spark@22.8.0`). Status: **implemented**.

## Problem

Since `fd570906` (#327 / #328, shipped as `preview.67`), a **non-array `AsDetail` column with a custom
renderer** hands that renderer `null` in every query grid. The renderer's null fallback paints, so the
column silently goes blank — no error, no console warning, no failing build, no failing test, and
`--spark-verify-model` stays green.

`GET /spark/queries/{id}/execute` returns a correct column definition and a gutted cell:

```jsonc
{"name":"Coverage","dataType":"AsDetail","isArray":false,
 "asDetailType":"…CoverageSummary","renderer":"coverage-bar"}

{"key":"Coverage","value":null,"objectId":null,"breadcrumb":"1422","breadcrumbs":null}
```

The whole child object has collapsed to a breadcrumb string carrying one number. The sibling `decimal`
column in the same row serialises normally, and the same child on the **detail** page renders correctly
— the data is intact server-side and the asymmetry is purely in the row projection.

The array case is fine. The `po-detail` case is fine. Only `isArray: false` on a query row is broken.

Observed in production on a consumer app upgrading `.57 → .67`: three grids
(`Commit.Coverage`, `Build.Coverage`, `Repository.LatestCoverage` — all `isArray: false`, all
`renderer: "coverage-bar"`) lost their coverage column entirely.

## Cause

`QueryResultProjector.ToValue` nulls the value for every non-array `AsDetail` column:

```csharp
Value = column.IsArray ? asDetail.Objects?.Count ?? 0 : null,
```

This was deliberate and is stated in `docs/issue_327_PRD.md` — a projection does not carry the nested
object graph, so rather than render an empty cell where a count used to be, the projector emits the
child **count** and, for a single child, the server-resolved **breadcrumb**.

**The design covered the plain-text cell and missed the custom-renderer cell.** The motivating example
throughout #327 is a cell that "used to read `3 items`" — an array. For `isArray: false` the count branch
cannot apply, so the single-child branch was given `null` + a breadcrumb. That is exactly right for a text
cell and useless for a renderer, whose only input on a grid *is* the cell value.

## Why this is a contract violation, not an unlucky default

The client documents the opposite in two places, both predating #327:

1. `libs/node_packages/ng-spark/grid/src/spark-grid-cell.component.ts` — on the `rendererValue` input:
   *"a renderer receives the underlying value — for AsDetail, the nested object itself — while `display`
   has already been flattened to something printable."*
2. `docs/guide-custom-attribute-renderers.md:245-247` — *"In query-list, sub-query, and po-detail field
   hosts, `value` is the nested `PersistentObject` (single) or `PersistentObject[]` (when `isArray`)."*

Statement 2 became true only on the detail page. The two extractors in `renderer-inputs.ts` diverge
accordingly: `rendererValue` falls back `attr?.value ?? attr?.object ?? attr?.objects`, while `cellValue`
is `value?.value` with **no fallback** — correctly, since a row carries no `object`/`objects` fields to
reach for. The projector is therefore the only place that can satisfy the grid's single channel, and it
sent `null`.

So the fix is a **restoration**, not a new convention. The alternative — declaring the null intentional
for rows — would require correcting both documented statements and a migration for every renderer on a
single-child `AsDetail` column, which is the argument for fixing the projector instead.

## Solution

One expression, `QueryResultProjector.cs:133`:

```csharp
Value = column.IsArray ? asDetail.Objects?.Count ?? 0 : asDetail.Object,
```

`QueryResultItemValue.Value` is `object?`, so STJ serialises the runtime `PersistentObject` through the
same converter and the same camelCase policy the detail path already uses. The grid cell becomes
byte-identical to what `rendererValue` falls through to on `po-detail`, which is what the documented
`{ attributes: [{ name, value }] }` shape promises.

### Design decisions

**D1 — the one-token version over a leaner hand-rolled projection.** The issue offered a variant
projecting an anonymous `{ Attributes = child.Attributes.Select(a => new { a.Name, a.Value }), Breadcrumb }`
to trim per-row payload. Rejected. The one-token version *is* the detail path's value, so it cannot drift
from it; a parallel hand-rolled shape is a second implementation of the same contract that stays identical
right up until it doesn't. Payload was #327's secondary goal, and this affects only columns an app
explicitly declared as single-child `AsDetail`.

**D2 — `Breadcrumb` is left exactly as it was.** This is what keeps the change non-breaking for text
cells: `query-cell.pipe.ts:33` tests `if (cell.breadcrumb) return cell.breadcrumb;` **before** the
`AsDetail` branch, so a rendererless single-child column keeps printing the server-resolved breadcrumb and
never looks at `cell.value`. `display` and `rendererValue` are separate inputs fed by separate functions,
so changing one cannot disturb the other.

**D3 — the array branch is untouched**, so #327's `3 items` behaviour is preserved verbatim.

**D4 — no client change.** `cellValue` already returns `value?.value`, which is now the object. Two
comments that asserted "a row carries no nested objects" were corrected; no code moved.

### Serialisation safety

Verified rather than assumed, because the value is typed `object?`:

- STJ picks the **runtime** type for an `object`-declared property, so the `PersistentObject` serialises
  in full rather than as `{}`.
- `PersistentObjectAttributeJsonConverter` writes an explicit field list that **excludes** the attribute's
  `Parent` back-reference, so the attribute → PO cycle cannot recurse.
- `PersistentObject.Parent` is set on exactly one path (`Endpoints/PersistentObject/Refresh.cs:179`), never
  during query mapping, so a projected child has no parent pointer to follow.

## Scope

**In:** the projector fix; regression tests at both the object and wire level; the corrected client
comments; an amendment note on `docs/issue_327_PRD.md`; `preview.68` version bump across all 22 NuGet
projects; release notes.

**Out:** npm packages stay at `22.8.0` — nothing shipped there changed behaviour. Consumer-app adoption is
a package-reference bump in that app's own repository once `preview.68` publishes.

## Acceptance criteria

1. A single-child `AsDetail` cell's `Value` is the nested `PersistentObject`, not `null`.
2. That cell serialises with the endpoint's options to `value.attributes[]` carrying `{ name, value }`.
3. The single-child cell's `Breadcrumb` is unchanged, so rendererless text cells render as before.
4. The array branch still projects the child count (and `0` for no children).
5. No column carrying a `renderer` projects a null value from a populated attribute.
6. `docs/guide-custom-attribute-renderers.md:245-247` is true again with no edit to it.

## Risks

- **Payload growth on wide grids** with many single-child `AsDetail` columns. Bounded: only such columns
  are affected, and D1 accepts it deliberately over a shape that can drift.
- **A renderer written against the broken behaviour** — i.e. one that only ever read `breadcrumb`. It keeps
  working: `breadcrumb` is unchanged and the new `value` is additive from the client's point of view.
