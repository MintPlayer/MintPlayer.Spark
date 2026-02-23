# PRD: Recursive AsDetail Rendering via Self-Referencing po-form

## Problem

The AsDetail modal in `po-form` manually renders each attribute with basic `<input>` elements (lines 126-151 of `po-form.component.html`). This means nested AsDetail properties (e.g. `Address.Description` of type `AddressDescription`) render as plain text inputs instead of showing a pencil button that opens another modal. The modal does not reuse the general-purpose `<app-po-form>` component, so it lacks support for all the data types that the main form handles (AsDetail, Reference, LookupReference, boolean toggle, etc.).

**Concrete example:** Person has an `Address` (AsDetail). Address now has a `Description` of type `AddressDescription` (also AsDetail). When the user opens the Address modal, the Description field should show a pencil button that opens a second-level modal — but instead it renders as a plain text input.

## Solution

Replace the hand-rolled attribute loop inside the AsDetail modal with a recursive `<app-po-form>` instance. The modal body should simply render:

```html
<app-po-form
  [entityType]="getAsDetailType(editingAsDetailAttr)"
  [(formData)]="asDetailFormData">
</app-po-form>
```

This makes the AsDetail modal automatically support every data type that `po-form` already handles, including nested AsDetail (which will open its own modal), References, LookupReferences, booleans, etc. — with no depth limit.

## Component Analysis

| Component | Has AsDetail code? | Needs changes? | Why |
|---|---|---|---|
| **po-form** | Yes — modal with manual attribute loop | **Yes** | Core fix: replace manual loop with recursive `<app-po-form>` |
| **po-edit** | Only `formData` init (`= {}`) | No | Already delegates all rendering to `<app-po-form>` |
| **po-create** | Only `formData` init (`= {}`) | No | Already delegates all rendering to `<app-po-form>` |
| **po-detail** | `formatAsDetailValue()` for display string | No | Read-only view — shows flat display string via `displayFormat`, no editing. Nested sub-objects won't appear in the formatted string but that's acceptable for a summary view. |
| **query-list** | `formatAsDetailValue()` for column display | No | Same as po-detail — flat display string in table column. AsDetail attributes are typically excluded from queries (`inQueryType: false`). |

## Scope

Only `po-form` needs changes. All three demo apps share the same component with the same manual modal template:
- `Demo/HR/HR/ClientApp/src/app/components/po-form/`
- `Demo/DemoApp/DemoApp/ClientApp/src/app/components/po-form/`
- `Demo/Fleet/Fleet/ClientApp/src/app/components/po-form/`

All three must be updated.

## Implementation Steps

### Step 1: Make po-form self-referencing

In `po-form.component.ts`, add a self-import so the component can use itself recursively:

```typescript
imports: [...existing imports..., PoFormComponent],
```

Angular standalone components support self-referencing in their `imports` array.

### Step 2: Replace the AsDetail modal body

In `po-form.component.html`, replace the manual attribute loop (lines 125-152) with a single `<app-po-form>`:

**Before (current):**
```html
@if (editingAsDetailAttr) {
  <bs-grid bsModalBody class="d-block">
    @for (detailAttr of getAsDetailAttributes(editingAsDetailAttr); ...) {
      <div bsRow ...>
        <label ...>{{ detailAttr.label || detailAttr.name }}</label>
        <div [md]="8">
          @if (detailAttr.dataType === 'boolean') { ... }
          @else { <input .../> }
        </div>
      </div>
    }
  </bs-grid>
}
```

**After:**
```html
@if (editingAsDetailAttr) {
  <div bsModalBody>
    <app-po-form
      [entityType]="getAsDetailType(editingAsDetailAttr)"
      [(formData)]="asDetailFormData">
    </app-po-form>
  </div>
}
```

### Step 3: Remove dead code

After the replacement, `getAsDetailAttributes()` in `po-form.component.ts` is no longer used and can be removed.

### Step 4: Apply to all three demo apps

Repeat Steps 1-3 for DemoApp and Fleet `po-form` components.

## Out of Scope

- Validation error propagation into nested AsDetail modals (can be added later)
- Backend changes (model sync already handles nested embedded types correctly)
- Any changes to po-edit, po-create, po-detail, or query-list components
