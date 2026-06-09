# PRD — Issue #187: drag-to-reorder for AsDetail-array attributes + full inline-edit parity

- **Issue:** [#187](https://github.com/MintPlayer/MintPlayer.Spark/issues/187)
- **Family:** #182 (custom renderers in AsDetail), #185 (reference breadcrumbs in AsDetail). Same theme — "AsDetail is the overlooked edit-surface."
- **Origin:** MintPlayer.Web `Playlist.Tracks` (`List<PlaylistTrack>`) needs manual ordering and is best edited inline. This feature lets the app drop its hand-rolled drag renderer **and** its `PlaylistTrack.Index` field (order = array position).
- **Scope note:** Per review, #187 now bundles **two** related deliverables in one PR: (A) drag-to-reorder via `[Sortable]`, and (B) closing the remaining inline-edit gaps so `editMode: "inline"` is feature-complete. The `editMode` default is **not** changed (modal stays the default; inline is opt-in).
- **Status:** Draft

## Problem

Two gaps on the AsDetail-array edit surface, both surfaced by the MintPlayer.Web migration:

1. **No reordering.** Ordered child collections (playlist tracks, workflow steps, ranked lists) can't be reordered through core UI. Apps work around it with an app-specific renderer plus an explicit integer `Index` field maintained by hand. That `Index` is redundant — see "Index is dead weight" below.
2. **Inline editing is second-class.** An AsDetail array can opt into inline editing (`editMode: "inline"`, as HR's `Person.Jobs` does), but the inline renderer only handles scalars, `boolean`, and `Reference` columns. `LookupReference`, custom-renderer columns, nested AsDetail, per-cell validation, and per-column `isReadOnly` are unsupported inline — so inline is only safe for simple shapes, and the modal is the de-facto choice for anything richer. The preference for ordered collections (e.g. `Playlist.Tracks`) is inline editing **with** drag-reorder, which today is impossible.

## Current state (verified)

The order pipeline already works end-to-end **except** the reorder affordance; inline editing works **except** for the column kinds listed above.

- **Persistence round-trips in array order, both directions.** `EntityMapper.PopulateAsDetail` enumerates the CLR collection in natural order into `attr.Objects` (`EntityMapper.cs` ~L287-299); `WriteAsDetailAsync` + `BuildCollection` rebuild the `List<T>`/`T[]` position-for-position (~L653-707). **Reordering the array and saving *is* the persistence — no index field needed.**
- **Save already flags AsDetail attributes changed.** `po-edit.component.ts` rebuilds every AsDetail attribute with `isValueChanged: true` on save (~L143/L152), so a reordered array is submitted with no extra change-marking.
- **The form renders from the schema, not the wire PO.** `spark-po-form.component.ts:124` iterates `this.entityType()?.attributes` — typed `EntityAttributeDefinition[]`. Rows are iterated from the `formData()` signal (`formData()[attr.name]`, a flat `Record<string,any>[]`), **not** `attr.objects`.
- **`editMode` is a hand-edited, JSON-only `string?`** on `EntityAttributeDefinition` (`EntityTypeDefinition.cs:77-81`); `null` ⇒ modal. **There is no C# attribute and `ModelSynchronizer` never writes it** — HR `Person.Jobs` got `"editMode": "inline"` by hand-editing `Demo/HR/HR/App_Data/Model/Person.json`. It is the only file in the repo that sets `editMode`.
- **Inline-branch column support today** (`spark-po-form.component.html` ~L115-177): `boolean` → `<bs-checkbox>`, `Reference` (with `col.query`) → `<bs-select>` from `asDetailReferenceOptions`, everything else → `<input [type]="col.dataType | inputType">`. **Gaps:** `LookupReference` (falls to raw-key `<input>`), custom `renderer` columns (falls to `<input>`, ignores the registered edit component), nested AsDetail columns (falls to `<input>`, meaningless), per-cell validation (`[required]` only, no `[class.is-invalid]`/messages, no index-keyed error path), and per-column `isReadOnly` (not honored). The modal branch gets all of these free via the recursive `<spark-po-form>`.

### Index is dead weight (MintPlayer finding)

In the MintPlayer Spark target, **nothing reads, sorts, or maintains `PlaylistTrack.Index`** — zero `OrderBy(t => t.Index)`, zero writes. It is a vestige of the legacy SQL schema, where `Index` was part of a composite PK and was re-stamped from array position on every save, then used once in `OrderBy` on load — i.e. it never encoded anything beyond list position. RavenDB persists `Tracks` in document array order, so **order = array position already**. `PlaylistTrack.Index` (and its model-JSON column) can be removed cleanly.

## Deviation from the issue's proposed flow (important)

Issue #187 proposes routing the sortable flag through `PersistentObjectAttribute` (wire) + the hand-written `PersistentObjectAttributeJsonConverter`. **That targets a layer the renderer never reads** — the po-form binds `EntityAttributeDefinition` (schema/model-JSON), which serializes via default System.Text.Json. **Decision:** the flag lives on `EntityAttributeDefinition` only (`[Sortable]` → `EntityAttributeDefinition.IsSortable` → model JSON → `entity-type.ts` → template). We do **not** touch `PersistentObjectAttribute` / its converter — that hop has no consumer and matches how `editMode` (the closest precedent) already works.

## Proposed API

Developer-facing — a property-level `[Sortable]` attribute mirroring `[Reference]`/`[LookupReference]`:

```csharp
public class Playlist : Entity
{
    [Sortable]                                   // ← opt in to drag-reorder
    public List<PlaylistTrack> Tracks { get; set; } = [];
}

public class PlaylistTrack            // Index removed — order is the array position
{
    [Reference(typeof(Song), "GetSongs")]
    public string? SongId { get; set; }
}
```

`editMode` is unchanged: still a JSON-only opt-in (`"inline"` / `"modal"`, default modal). No new attribute for it (kept as-is per review). To get the preferred inline + drag experience for `Playlist.Tracks`, the model JSON carries both `"isSortable": true` and `"editMode": "inline"`.

**Flow (sortable):** `[Sortable]` → `ModelSynchronizer` sets `EntityAttributeDefinition.IsSortable` in `App_Data/Model/*.json` (only when `dataType == "AsDetail" && isArray`; null/absent otherwise) → `EntityAttributeDefinition.isSortable` (ng-spark) → `cdkDropList` + drag handles.

## Goals

**A — Drag-to-reorder**
1. New `SortableAttribute` (`[AttributeUsage(AttributeTargets.Property)]`, no ctor) in `MintPlayer.Spark.Abstractions`.
2. `EntityAttributeDefinition.IsSortable` (nullable `bool?`), set by `ModelSynchronizer` from the CLR shape — `true` only for `[Sortable]` AsDetail arrays; null/absent otherwise.
3. When `attr.isSortable`, the AsDetail-array sub-table (both inline and modal branches) renders `cdkDropList` + per-row `cdkDrag` + a `cdkDragHandle` grip cell; dropping reorders `formData()[attr.name]` via `moveItemInArray`. Non-sortable arrays unchanged.
4. Saving a reordered array persists the new order (relies on the existing round-trip + `isValueChanged`), with **no explicit index field**.
5. `@angular/cdk` declared as a `peerDependency` of `ng-spark` (`^22.0.0`), mirroring `@mintplayer/ng-bootstrap`.

**B — Inline-edit parity (close all gaps so `editMode: "inline"` is feature-complete)**
6. **LookupReference columns** render inline as a `<bs-select>` populated from lookup options (extend `loadAsDetailTypes()` to fetch child-column lookup options alongside the existing reference options).
7. **Custom-renderer columns** render inline via the column's registered **edit** component (`ngComponentOutlet`), not a raw `<input>` — matching the modal/recursive-form behavior.
8. **Nested AsDetail child columns** are editable inline via a localized escape hatch: the cell shows a summary + a pencil that opens a sub-modal scoped to that nested child (you can't flatten an arbitrarily deep grid into a row, but you can still edit it without leaving inline mode).
9. **Per-column `isReadOnly`** honored inline: read-only columns render disabled/read-only inputs.
10. **Per-cell validation**: inline cells participate in validation with an index/column-keyed error path (e.g. `"{attr.name}[{i}].{col.name}"`), rendering `[class.is-invalid]` + an inline message, consistent with top-level fields.

**C — Demo / consumer**
11. Demo: HR `Person.Jobs` (`CarreerJob[]`) annotated `[Sortable]` (it is already `editMode: "inline"`) to exercise inline + drag + persistence.
12. **Consumer adoption (MintPlayer.Web, separate repo — documented, not done in this PR):** remove `PlaylistTrack.Index` and its model-JSON column; annotate `Playlist.Tracks` `[Sortable]` + `editMode: "inline"`.

## Non-goals

- **Flipping the `editMode` default.** Modal stays the default; inline remains opt-in via `editMode: "inline"`. (Reviewer decision.)
- Touching `PersistentObjectAttribute` / `PersistentObjectAttributeJsonConverter` (no consumer — see Deviation).
- A `[Sortable(OrderField = "Index")]` numeric-order variant — order = array position; explicitly unnecessary (the `Index` finding confirms it).
- Drag-reorder or inline editing for **single-object** (non-array) AsDetail — always modal, unchanged.
- Editing the MintPlayer.Web repo from this session (cross-repo writes are blocked here; Goal 12 is recorded for the migration session).

## Acceptance criteria

**Drag-to-reorder**
- **AC1 (declaration → model):** A `[Sortable]` AsDetail-array property yields `"isSortable": true` on that attribute in the regenerated model JSON; a non-annotated AsDetail array has no `isSortable` key (not `false`); idempotent across re-sync.
- **AC2 (guard):** `[Sortable]` on a non-AsDetail or non-array property does not set `isSortable`.
- **AC3 (render):** A `[Sortable]` AsDetail array renders a drag-handle cell per row + `cdkDropList`/`cdkDrag`; a non-`[Sortable]` array renders unchanged.
- **AC4 (reorder):** Dragging a row reorders `formData()[attr.name]` via `moveItemInArray`; visible order updates.
- **AC5 (persist):** Saving after a reorder persists the new order; reload shows it. No `Index` field.

**Inline-edit parity** (each asserted with `editMode: "inline"`)
- **AC6 (LookupReference):** a `LookupReference` child column renders a select of lookup options inline and binds the chosen key into the row.
- **AC7 (custom renderer):** a child column with a registered renderer renders its **edit** component inline (not a raw input).
- **AC8 (nested AsDetail):** a nested-AsDetail child column is editable inline via the sub-modal escape hatch and persists.
- **AC9 (read-only):** an `isReadOnly` child column is non-editable inline.
- **AC10 (validation):** a required/invalid inline cell shows `[class.is-invalid]` + message and blocks save, keyed per row+column.

**Cross-cutting**
- **AC11 (permission/SSR):** no drag affordance and no editable inline inputs when the PO/attribute is read-only or the user lacks edit permission (reuse existing AsDetail gating). `cdkDropList` markup renders server-side without error (drag is browser-only).
- **AC12 (dependency):** `ng-spark` declares `@angular/cdk` as a peer; the library builds and demos run with no missing-dependency error.
- **AC13 (default unchanged):** an AsDetail array with no `editMode` still renders the modal branch (default not flipped).

## Test plan (TDD — write failing first)

**Server** — `tests/MintPlayer.Spark.Tests/Services/ModelSynchronizerTests.cs` (temp-dir `IHostEnvironment` + `EntityTypeFile` readback, established pattern):
1. AC1: `[Sortable] List<Child>` AsDetail array → `IsSortable == true`; sibling non-sortable AsDetail array → null/absent.
2. AC2: `[Sortable]` on scalar / non-array → `IsSortable` null.
3. Idempotency: sync twice → unchanged.

**Frontend** — Vitest in `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.spec.ts`:
4. AC3/AC4: sortable AsDetail array → drag-handle cells exist; `onAsDetailReorder(attr, {previousIndex, currentIndex})` reorders `formData()[attr.name]`; non-sortable → no handle.
5. AC6–AC10 (inline parity): fixture child types exercising a LookupReference column, a custom-renderer column, a nested-AsDetail column, an `isReadOnly` column, and a required column — assert each renders/binds/validates inline as specified. AC13: unset `editMode` → modal branch.

**Manual** (per Angular/Spark guidance — **do not** run `ng serve`/`ng build`/`ng test`; the HR demo host runs the embedded dev server): edit a `Person` with multiple `Jobs` inline, drag to reorder, save, reload → order + edits persist.

**Workflow:** ModelSynchronizer tests → red → `SortableAttribute` + `IsSortable` + synchronizer → green → CDK drag wiring (both branches) → inline gap-closures one at a time (each with its Vitest case) → demo annotation + model regen → manual check.
