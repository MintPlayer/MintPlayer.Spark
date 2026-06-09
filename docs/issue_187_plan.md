# Implementation plan — Issue #187 (drag-to-reorder + inline-edit parity for AsDetail arrays)

See [issue_187_PRD.md](./issue_187_PRD.md). TDD: failing test first, then fix. Two bundled workstreams in one PR: **A** drag-to-reorder (`[Sortable]`), **B** close all inline-edit gaps. The `editMode` default is **not** flipped.

## Design

The persistence and save paths already preserve and submit array order (PRD "Current state") — **no server persistence change**. The reorder work is a metadata flag + CDK wiring. The inline work is purely in the inline template branch + `loadAsDetailTypes()` options loading + a per-cell validation path; modal mode and the default are untouched.

### Key facts (verified)
- Renderer reads **`EntityAttributeDefinition`** (`spark-po-form.component.ts:124`), which serializes via default STJ in `ModelSynchronizer` (camelCase, `WhenWritingNull`). Adding a C# property is sufficient — **no `PersistentObjectAttributeJsonConverter` edit**.
- `IsSortable` is nullable `bool?` so `WhenWritingNull` omits it when unset → no `isSortable: false` churn across existing model files (minimal diff). A plain `bool` would write `false` everywhere on next sync.
- `ModelSynchronizer` reads CLR attributes via `property.GetCachedCustomAttribute<T>()` (`ModelSynchronizer.cs:326`); `dataType == "AsDetail"` + `isArray` computed ~L335-355. It **never** writes `EditMode` (round-trips it from JSON) — leave that as-is.
- Two AsDetail-array template branches: **inline** `editMode === 'inline'` (`spark-po-form.component.html` ~L115-177) and **modal/default** `attr.dataType === 'AsDetail' && attr.isArray` (~L178-215). Both iterate `formData()[attr.name]` with `track $index`.
- Drop handler mutates `formData()[attr.name]` (flat dict array), not `attr.objects`; re-setting the `formData` `model` signal propagates; `po-edit` flags AsDetail `isValueChanged` on save → reorder persists.
- Inline cell binding pattern is `[(ngModel)]="row[col.name]"` + `(ngModelChange)="onFieldChange()"` (which re-emits the signal). New inline cell kinds must follow this.
- Inline gaps to close (`spark-po-form.component.html` inline cell block ~L129-153): `LookupReference`, custom `renderer`, nested AsDetail, `isReadOnly`, per-cell validation. Modal gets these via the recursive `<spark-po-form>`.
- `@angular/cdk@22.0.0` already installed transitively (via ng-bootstrap, pinned in root `overrides`); just needs a `peerDependency` entry. No new install.

## Steps — Workstream A (drag-to-reorder)

### A1 — Failing test (ModelSynchronizer) — `tests/MintPlayer.Spark.Tests/Services/ModelSynchronizerTests.cs`
Add a context with a `[Sortable]` AsDetail array, a non-sortable AsDetail array, and a `[Sortable]` scalar (negative guard):
```csharp
private sealed class MS_OrderedParent {
    public string Id { get; set; } = "";
    [Sortable] public List<MS_Step> Steps { get; set; } = [];   // → isSortable: true
    public List<MS_Step> Notes { get; set; } = [];              // → absent
    [Sortable] public string Name { get; set; } = "";           // → ignored
}
private sealed class MS_Step { public string Label { get; set; } = ""; }
```
Assert (via `EntityTypeFile` readback): `Steps.IsSortable == true`; `Notes.IsSortable` null + no JSON key; `Name.IsSortable` null; sync twice → unchanged.
Run `dotnet test tests/MintPlayer.Spark.Tests --filter ModelSynchronizerTests` → **red**.

### A2 — `SortableAttribute` — `libs/spark/MintPlayer.Spark.Abstractions/SortableAttribute.cs` (new)
```csharp
namespace MintPlayer.Spark.Abstractions;

/// <summary>Marks an AsDetail array property as drag-to-reorderable in PO-edit.
/// Order is the array position; no explicit index field is required.
/// Ignored on non-AsDetail or non-array properties.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SortableAttribute : Attribute;
```

### A3 — `EntityAttributeDefinition.IsSortable` — `libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`
Add next to `IsArray` / `AsDetailType` / `EditMode`: `public bool? IsSortable { get; set; }` (nullable → omitted when unset; no converter change).

### A4 — Wire in `ModelSynchronizer` — `libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs`
Alongside the other `GetCachedCustomAttribute` reads (~L326): `var sortableAttr = property.GetCachedCustomAttribute<SortableAttribute>();`
After `isArray`/`dataType` (~L355): `bool? isSortable = (sortableAttr != null && dataType == "AsDetail" && isArray) ? true : null;`
Set in both the existing-attribute update path (~L398, next to `existingAttr.IsArray = isArray;`) and the new-attribute initializer (~L443, next to `IsArray = isArray,`). `null` (not `false`) keeps it out of JSON. Run A1 → **green**.

### A5 — Frontend model — `libs/node_packages/ng-spark/models/src/entity-type.ts`
Add `isSortable?: boolean;` to `EntityAttributeDefinition` (next to `isArray`/`editMode`/`asDetailType`).

### A6 — `@angular/cdk` peer dependency — `libs/node_packages/ng-spark/package.json`
Add `"@angular/cdk": "^22.0.0"` to `peerDependencies`. No `npm install` (already resolved).

### A7 — Component drag wiring — `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.ts`
1. `import { CdkDropList, CdkDrag, CdkDragHandle, CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';`
2. Add `CdkDropList, CdkDrag, CdkDragHandle` to `@Component({ imports: [...] })`.
3. Handler (after `removeArrayItem`, ~L466):
```ts
onAsDetailReorder(attr: EntityAttributeDefinition, event: CdkDragDrop<Record<string, any>[]>): void {
  const data = { ...this.formData() };
  const arr = [...(data[attr.name] ?? [])];
  moveItemInArray(arr, event.previousIndex, event.currentIndex);
  data[attr.name] = arr;
  this.formData.set(data);
}
```

### A8 — Template drag wiring — `libs/node_packages/ng-spark/po-form/src/spark-po-form.component.html`
For **both** branches, gated on `attr.isSortable`: `cdkDropList (cdkDropListDropped)="onAsDetailReorder(attr, $event)"` on `<tbody>`; `cdkDrag` on each `<tr>`; a leading drag-handle `<th>` + `<td cdkDragHandle>` with a grip icon (verify against `SparkIconRegistry`, e.g. `grip-vertical`; fall back to an existing icon). Gate the handle on the existing edit/permission gating (`canDeleteDetailRow`/`canCreateDetailRow:asDetailPermissions()` or `isReadOnly`) — AC11. Keep non-sortable DOM byte-identical. `track $index` is fine (array is reordered + signal re-set); any CDK flicker is cosmetic.

## Steps — Workstream B (inline-edit parity)

All edits are in the inline cell block (`spark-po-form.component.html` ~L129-153), `spark-po-form.component.ts`, and `loadAsDetailTypes()`. Each gap gets a Vitest case in `spark-po-form.component.spec.ts` first (red → green).

### B1 — `isReadOnly` per column (AC9) — smallest, do first
Inline inputs/selects/checkbox: add `[readonly]="col.isReadOnly"` to text inputs and `[disabled]="col.isReadOnly"` to `<bs-select>`/`<bs-checkbox>`. Test: read-only column renders non-editable.

### B2 — LookupReference columns (AC6)
- `loadAsDetailTypes()` (`spark-po-form.component.ts`): also fetch lookup options for child columns where `col.lookupReferenceType` is set, into a signal map (sibling of `asDetailReferenceOptions`, e.g. `asDetailLookupOptions`). Mirror how top-level lookup options are loaded.
- Inline cell block: add `@else if (col.lookupReferenceType)` rendering a `<bs-select [(ngModel)]="row[col.name]" (ngModelChange)="onFieldChange()">` over those options.
- Test: LookupReference child column → select of lookup values, binds the key.

### B3 — Custom-renderer columns (AC7)
- The inline block currently ignores `col.renderer`. Add `@if (getAsDetailCellEditRenderer(col); as editRenderer)` (a new method returning the renderer's **edit** component from the renderer registry — distinct from the modal/display `getAsDetailCellRendererComponent`) → `ngComponentOutlet` with inputs that two-way the value back into `row[col.name]` and call `onFieldChange()`.
- If a renderer exposes only a display (column) component and no edit component, fall back to the existing modal-style read-only display + the nested editor (consistent with B4's escape hatch).
- Test: a registered edit-renderer column renders its component inline.

### B4 — Nested AsDetail child columns (AC8) — escape hatch
- A nested AsDetail (`col.dataType === 'AsDetail'`) can't flatten into a row. Inline cell: render a summary (`asDetailDisplayValue`/`asDetailCellValue`) + a pencil that opens the existing modal editor **scoped to that nested child** (reuse `editArrayItem`/`openAsDetailEditor` machinery, targeting `row[col.name]` instead of the top-level attr). On modal save, write back into `row[col.name]` + `onFieldChange()`.
- Test: nested-AsDetail child column editable via the sub-modal; value persists into the row.

### B5 — Per-cell validation (AC10) — highest complexity, do last
- Establish an inline error-path convention: `"{attr.name}[{i}].{col.name}"`. Add an `hasInlineError(attr, i, col)` helper that consults the `validationErrors` signal under that key.
- Inline cells: bind `[class.is-invalid]="hasInlineError(attr, $index, col)"` + an inline `invalid-feedback` message; surface `[required]` validity too.
- Ensure save is blocked when any inline cell is invalid (extend the existing form-validity gate to walk inline AsDetail rows). Confirm whether server validation already emits errors keyed per-row; if not, this is client-side required/rule validation only for v1 (document the boundary).
- Test: required-but-empty inline cell shows invalid state and blocks save.

> Per Angular/Spark guidance: do **not** run `ng serve`/`ng build`/`ng test`. Run FE tests via the workspace Vitest target (`nx test ng-spark` or the project's configured runner) — confirm the command before invoking.

## Steps — Workstream C (demo + wrap-up)

### C1 — Demo — `Demo/HR/HR.Library/Entities/Person.cs`
Annotate `[Sortable] public CarreerJob[] Jobs { get; set; } = [];` (already `editMode: "inline"`). Regenerate the HR model (the documented `--spark-synchronize-model` run) so `Demo/HR/HR/App_Data/Model/Person.json`'s `Jobs` gains `"isSortable": true`. Commit the regenerated JSON. If `CarreerJob` lacks a column exercising B2–B5, consider adding one demo column (e.g. a LookupReference) to visibly exercise the inline parity work — optional.

### C2 — Verify & wrap up
- `dotnet test tests/MintPlayer.Spark.Tests` (full) green; Vitest cases green.
- Manual: HR demo host (it starts the embedded dev server — **do not** run `ng serve`/`ng build`), edit a `Person` with multiple `Jobs` inline, edit cells, drag-reorder, save, reload → edits + order persist (AC4/AC5/AC6–AC10). Confirm an AsDetail array with no `editMode` still renders modal (AC13).
- Bump preview versions per the repo's release convention (NuGet `preview.*` on touched `libs/spark` projects; `@mintplayer/ng-spark` patch) **only at publish/merge time** — confirm the existing pattern. CI auto-publishes on push to master; do not hand-publish from the feature branch.

### C3 — Consumer adoption (MintPlayer.Web — separate repo, NOT this PR)
Recorded for the migration session (cross-repo writes blocked from this repo): remove `PlaylistTrack.Index` + its `App_Data/Model/PlaylistTrack.json` column; annotate `Playlist.Tracks` `[Sortable]` and set `editMode: "inline"`. After upgrading to the `ng-spark` build that ships #187, `Playlist.Tracks` gets inline editing + drag-reorder with no hand-rolled renderer and no index field.

## Risk / cost notes
- **Workstream A is small and additive.** No server persistence change, no JSON-converter change. `track $index` + CDK drag → at most cosmetic flicker.
- **Workstream B is the bulk of the effort.** B5 (per-cell validation) is the riskiest — the current `validationErrors` model is keyed by `attr.name` with no per-row indexing; introducing an index-keyed path is a real change. Scope v1 to client-side required/rule validation if server-side per-row errors aren't already emitted, and document the boundary. B4 (nested AsDetail) deliberately uses a sub-modal escape hatch rather than attempting to flatten nested grids inline.
- **Default unchanged** (AC13) — modal stays default; inline opt-in is unchanged, so existing apps are unaffected. Only the deliberate HR `Person.Jobs` model change differs.
- **Model-JSON churn avoided** by nullable-`bool?` + `WhenWritingNull`.
- **First CDK-drag usage in the repo:** verify the grip icon name; SSR-safe (drag is browser-only).
