# PRD: Inline Editing for Array AsDetail Attributes

## Overview

Currently, array AsDetail attributes (e.g., `Person.Jobs`) are edited exclusively through a modal dialog. This PRD adds a configurable `editMode` field (`"inline"` or `"modal"`) on AsDetail array attributes, allowing inline editing directly within the table row.

## Current Behavior

- Array AsDetail items are displayed in a `<bs-table>` with read-only columns
- Each row has edit (pencil) and delete (trash) action buttons
- Clicking edit opens a `<bs-modal>` containing a recursive `<app-po-form>` for the nested entity type
- An "Add" button below the table also opens the same modal with empty form data
- On save, the modal closes and the table row is updated

## Proposed Behavior

A new `editMode` JSON field on AsDetail array attributes controls how items are edited:

| Value | Behavior |
|-------|----------|
| `"modal"` | Current behavior (default). Edit/add via modal dialog. |
| `"inline"` | All rows are always editable inline in the table. |

### Inline Mode UX

**All rows are always in edit mode:**
- Every row renders input controls directly in the table cells (no read-only display state)
- Input controls match each column's `dataType`:
  - `string` / `number` / `date` / `decimal` / `integer` → `<input>` with appropriate type
  - `boolean` → `<bs-toggle-button>`
  - `Reference` → `<bs-select>` populated from the column's query (using `asDetailReferenceOptions`)
  - `LookupReference` → `<bs-select>` populated from lookup options
- Each row has a delete (trash) button on the right, styled with `[color]="colors.secondary"`
- Changes are written directly to `formData[attr.name]` — nothing is persisted to the database until the user clicks "Save" on the main PersistentObject form

**Add button:**
- Always visible below the table as `<button [color]="colors.primary">{{ 'add' | t }}</button>`
- Clicking it appends an empty row (with default/empty values) to `formData[attr.name]`
- The new row is immediately editable inline like all other rows

**Delete:**
- Each row has a delete (trash) button on the right with `[color]="colors.secondary"`
- Clicking it removes the row from `formData[attr.name]` immediately
- Deletion is only persisted when the main PO is saved

**No separate save/cancel per row** — all inline changes are part of the main form's dirty state and saved together with the parent entity.

## Data Model Changes

### Backend: `EntityAttributeDefinition` (C#)

File: `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`

Add property to `EntityAttributeDefinition`:

```csharp
/// <summary>
/// For array AsDetail attributes, controls how items are edited.
/// "modal" (default) opens a dialog; "inline" edits directly in the table row.
/// </summary>
public string? EditMode { get; set; }
```

### JSON Model Schema

File: e.g., `Demo/HR/HR/App_Data/Model/Person.json`

```json
{
  "name": "Jobs",
  "dataType": "AsDetail",
  "isArray": true,
  "asDetailType": "HR.Entities.CarreerJob",
  "editMode": "inline"
}
```

When omitted or `null`, defaults to `"modal"` (backward compatible).

### Frontend: `EntityAttributeDefinition` (TypeScript)

File: `Demo/*/ClientApp/src/app/core/models/entity-type.ts` (all 3 demo apps)

Add field:

```typescript
/** For array AsDetail attributes: "modal" (default) or "inline" */
editMode?: 'inline' | 'modal';
```

## Implementation Milestones

### Milestone 1: Data Model & Plumbing

- [ ] Add `EditMode` property to C# `EntityAttributeDefinition`
- [ ] Add `editMode` field to TypeScript `EntityAttributeDefinition` interface (all 3 demo apps)
- [ ] Add `"editMode": "inline"` to `Person.json` Jobs attribute in HR demo
- [ ] Verify the field is serialized to the frontend (no backend logic changes needed, ModelLoader deserializes automatically)

### Milestone 2: Inline Editing State Management (TypeScript)

In `po-form.component.ts` (all 3 demo apps), add:

- [ ] `addInlineRow(attr)` — appends a new empty `{}` to `formData[attr.name]` array, emits `formDataChange`
- [ ] `removeInlineRow(attr, index)` — removes row at index from `formData[attr.name]`, emits `formDataChange` (reuse existing `removeArrayItem()`)
- [ ] `onInlineFieldChange(attr, index, colName, value)` — updates `formData[attr.name][index][colName]` with the new value, emits `formDataChange`
- [ ] Guard: when `editMode === 'inline'`, `addArrayItem()` calls `addInlineRow()` instead of opening the modal; `editArrayItem()` is not used (rows are always editable)

### Milestone 3: Inline Editing Template (HTML)

In `po-form.component.html` (all 3 demo apps), update the array AsDetail `<bs-table>`:

- [ ] Branch on `attr.editMode === 'inline'` vs default modal behavior
- [ ] For inline mode, every `<tr>` renders input controls per column based on `col.dataType`:
  - Default → `<input [type]="getInputType(col.dataType)" [(ngModel)]="row[col.name]" (ngModelChange)="onFieldChange()">`
  - `boolean` → `<bs-toggle-button [(ngModel)]="row[col.name]" (ngModelChange)="onFieldChange()">`
  - `Reference` → `<bs-select [(ngModel)]="row[col.name]" (ngModelChange)="onFieldChange()">` using `asDetailReferenceOptions[attr.name][col.name]`
  - `LookupReference` → `<bs-select>` using lookup options for the column
- [ ] Action column: delete (trash) button per row with `[color]="colors.secondary"`
- [ ] "Add" button below the table: `<button type="button" [color]="colors.primary" (click)="addInlineRow(attr)"><app-icon name="plus" /> {{ 'add' | t }}</button>`
- [ ] Keep modal mode template unchanged for `editMode !== 'inline'`

### Milestone 4: Polish & Testing

- [ ] Ensure tab-order works sensibly across inline inputs (left-to-right, top-to-bottom)
- [ ] Ensure validation errors display inline (e.g., `[class.is-invalid]` + `<div class="invalid-feedback">`)
- [ ] Ensure `isRequired` columns are enforced by the main PO save validation
- [ ] Test with HR demo: Person.Jobs as inline, Person.Address stays as modal (single AsDetail)
- [ ] Verify modal mode still works unchanged when `editMode` is omitted or `"modal"`

## Files to Modify

| File | Change |
|------|--------|
| `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` | Add `EditMode` property |
| `Demo/HR/HR/App_Data/Model/Person.json` | Add `"editMode": "inline"` to Jobs attribute |
| `Demo/*/ClientApp/src/app/core/models/entity-type.ts` | Add `editMode` field (3 files) |
| `Demo/*/ClientApp/src/app/components/po-form/po-form.component.ts` | Add inline editing methods (3 files) |
| `Demo/*/ClientApp/src/app/components/po-form/po-form.component.html` | Add inline editing template branch (3 files) |

## Acceptance Criteria

- [ ] `editMode` field is optional and defaults to modal behavior when omitted
- [ ] Setting `"editMode": "inline"` on an array AsDetail attribute renders all rows with inline input controls
- [ ] All rows are simultaneously editable (no per-row edit/save/cancel toggle)
- [ ] Changes are only persisted to the database when the main PO "Save" button is clicked
- [ ] Inline add appends an empty editable row immediately
- [ ] Inline delete removes the row from the form data immediately
- [ ] Reference columns show a `<bs-select>` dropdown populated from the column's query
- [ ] Modal mode continues to work identically for attributes without `editMode` or with `"editMode": "modal"`
- [ ] Single (non-array) AsDetail attributes are unaffected
- [ ] "Add" button uses `[color]="colors.primary"` styling
