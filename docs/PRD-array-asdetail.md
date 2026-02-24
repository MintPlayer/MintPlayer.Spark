# PRD: Array AsDetail Support (CarreerJob[] on Person)

## Problem Statement

The current AsDetail implementation only supports **single nullable objects** (e.g., `Address? Address` on Person). It does not support **arrays/collections of detail objects** (e.g., `CarreerJob[] Jobs` on Person).

The user wants to demonstrate:
1. A `Profession` entity (standalone PO with Description and Regime)
2. A `CarreerJob` entity (embedded detail with a Reference to Profession, ContractStart, ContractEnd)
3. A `Jobs` property on `Person` of type `CarreerJob[]` -- rendered as an editable grid in the UI
4. Permission-gated "add row" capability: only users with `New/CarreerJob` permission can add lines

## Current State Analysis

### What Already Works

| Area | Status | Details |
|------|--------|---------|
| **Single-object AsDetail** | Working | `Address?` on Person renders with pencil-button modal |
| **Recursive AsDetail** | Working | `Address.Description` (nested AsDetail) renders recursively via self-referencing `<app-po-form>` |
| **AsDetail detection** | Working | `ModelSynchronizer.IsComplexType()` auto-detects complex types as `AsDetail` |
| **AsDetail mapping** | Working | `EntityMapper` serializes/deserializes single nested objects via `JsonElement` |
| **Reference inside entities** | Working | `[Reference(typeof(Company))]` on Person.Company works with breadcrumb resolution |
| **Permission enforcement** | Working | `PermissionService.EnsureAuthorizedAsync("New", "Person")` gates Create operations |
| **Permission UI gating** | Working | `/spark/permissions/{entityTypeId}` endpoint returns `canCreate`/`canEdit`/`canDelete` |

### What Does NOT Work (Gaps)

#### Gap 1: Array/Collection AsDetail Detection
**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 471-498)

`GetDataType()` and `IsComplexType()` do not handle array types (`T[]`), `List<T>`, or `IEnumerable<T>`. If you define `CarreerJob[] Jobs` on Person:
- The property type is `CarreerJob[]`, which is **not** a class with public properties
- `IsComplexType(typeof(CarreerJob[]))` returns `false` (arrays are not classes with properties in the check)
- The property would fall through to the default `"string"` data type -- **wrong behavior**

**Required change:** Detect array/collection types, unwrap the element type, and mark as `AsDetail` with an `isArray: true` flag.

#### Gap 2: `EntityAttributeDefinition` Missing `IsArray` Flag
**File:** `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` (lines 39-80)

No `IsArray` property exists on `EntityAttributeDefinition`. The schema has no way to distinguish between:
- `Address? Address` (single object AsDetail)
- `CarreerJob[] Jobs` (array of AsDetail objects)

**Required change:** Add `public bool IsArray { get; set; }` to `EntityAttributeDefinition`.

#### Gap 3: `CollectEmbeddedTypes` Doesn't Unwrap Arrays
**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 189-204)

`CollectEmbeddedTypes()` checks `IsComplexType(propType)` directly, which won't match array types. The element type of `CarreerJob[]` is never enqueued for processing, so no `CarreerJob.json` model file would be generated.

**Required change:** If property type is an array or `IEnumerable<T>`, unwrap element type before `IsComplexType` check.

#### Gap 4: EntityMapper Doesn't Handle Array AsDetail
**File:** `MintPlayer.Spark/Services/EntityMapper.cs`

**Outbound (Entity -> PO):** `ConvertToSerializableDictionary()` (line 87-90) handles single objects only. For an array property, it would need to serialize each element to a dictionary and return `List<Dictionary<string, object?>>`.

**Inbound (PO -> Entity):** `SetPropertyValue()` (line 144) checks `je.ValueKind == JsonValueKind.Object && IsComplexType(targetType)`. For arrays, `je.ValueKind` would be `JsonValueKind.Array` and `targetType` would be `CarreerJob[]`. This case is unhandled.

**Required change:** Add array handling in both directions.

#### Gap 5: Angular UI -- No Grid/Table Rendering for Array AsDetail
**File:** `Demo/HR/HR/ClientApp/src/app/components/po-form/po-form.component.html` (lines 65-80)

The current AsDetail rendering shows a single readonly input + pencil button for editing a single object in a modal. For array AsDetail, the UI should show:
- A **table/grid** displaying all rows (each CarreerJob)
- An **"Add" button** to add a new row (permission-gated)
- A **"Delete" button** per row to remove entries (permission-gated)
- An **"Edit" button** per row to open the modal editor for that item

**Required change:** New template branch for `attr.dataType === 'AsDetail' && attr.isArray`.

#### Gap 6: Angular UI -- No Permission Check for Detail Row Operations
**File:** `Demo/HR/HR/ClientApp/src/app/components/po-form/po-form.component.ts`

Currently, AsDetail operations inherit parent entity permissions only. There is no mechanism to check whether the user has `New/CarreerJob` permission to gate the "Add row" button.

**Required change:** For array AsDetail attributes, fetch permissions for the detail entity type and use `canCreate` to show/hide the Add button, `canDelete` to show/hide Delete buttons.

#### Gap 7: Reference Inside AsDetail (ProfessionId)
**File:** `MintPlayer.Spark/Services/EntityMapper.cs`

`CarreerJob` has `string ProfessionId` with `[Reference(typeof(Profession))]`. When CarreerJob is embedded as AsDetail inside Person:
- The Reference resolution (breadcrumb loading via `.Include()`) only works for top-level entity properties in `DatabaseAccess.GetPersistentObjectAsync()`
- References inside embedded AsDetail objects are not resolved -- the user would see a raw document ID instead of a breadcrumb

**Required change:** Either recursively resolve references inside AsDetail objects, or ensure the po-form modal for array items can resolve references independently.

## Proposed C# Entities

### Profession (Standalone PO)
```csharp
// Demo/HR/HR.Library/Entities/Profession.cs
namespace HR.Entities;

public class Profession
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Regime { get; set; } = string.Empty;
}
```

### CarreerJob (Embedded Detail)
```csharp
// Demo/HR/HR.Library/Entities/CarreerJob.cs
using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class CarreerJob
{
    [Reference(typeof(Profession))]
    public string? ProfessionId { get; set; }
    public DateOnly ContractStart { get; set; }
    public DateOnly? ContractEnd { get; set; }
}
```

### Person (Updated)
```csharp
// Demo/HR/HR.Library/Entities/Person.cs (updated)
using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    [Reference(typeof(Company))]
    public string? Company { get; set; }

    public Address? Address { get; set; }
    public CarreerJob[] Jobs { get; set; } = [];  // NEW: Array AsDetail
}
```

### HRContext (Updated)
```csharp
// Demo/HR/HR/HRContext.cs (updated)
public class HRContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<VPerson> VPeople => Session.Query<VPerson, People_Overview>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
    public IRavenQueryable<Profession> Professions => Session.Query<Profession>();  // NEW
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
}
```

## Implementation Plan

### Phase 1: Backend -- Array AsDetail Support

#### Step 1.1: Add `IsArray` to `EntityAttributeDefinition`
**File:** `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`

Add property:
```csharp
public bool IsArray { get; set; }
```

#### Step 1.2: Update `ModelSynchronizer` to Detect Arrays
**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs`

1. Update `GetDataType()` to unwrap array/collection element types:
   - If type is `T[]`, `List<T>`, `IEnumerable<T>`, etc., get element type `T`
   - If `T` is a complex type, return `"AsDetail"`
   - Set `isArray = true` on the attribute definition

2. Update `CollectEmbeddedTypes()` to unwrap array/collection element types before calling `IsComplexType()`

3. When creating/updating `EntityAttributeDefinition`, set `IsArray = true` when the property type is an array/collection of complex types

#### Step 1.3: Update `EntityMapper` for Array Serialization
**File:** `MintPlayer.Spark/Services/EntityMapper.cs`

**Outbound (Entity -> PO):**
- When `attrDef.DataType == "AsDetail" && attrDef.IsArray && value != null`:
  - Cast value to `IEnumerable`, iterate, call `ConvertToSerializableDictionary()` per element
  - Return `List<Dictionary<string, object?>>`

**Inbound (PO -> Entity):**
- When `je.ValueKind == JsonValueKind.Array`:
  - Get element type from the target array/collection type
  - Deserialize each element to the target element type
  - Construct the target array/collection

#### Step 1.4: Handle Reference Resolution Inside AsDetail
**File:** `MintPlayer.Spark/Services/EntityMapper.cs` and `DatabaseAccess.cs`

For the initial implementation, breadcrumb resolution for references inside AsDetail arrays can be deferred to the frontend (the po-form modal already loads reference options independently when rendering a form). Document this as a known limitation.

### Phase 2: HR Demo -- Add Profession and CarreerJob

#### Step 2.1: Create Entity Classes
- Create `Demo/HR/HR.Library/Entities/Profession.cs`
- Create `Demo/HR/HR.Library/Entities/CarreerJob.cs`
- Update `Demo/HR/HR.Library/Entities/Person.cs` to add `Jobs` property

#### Step 2.2: Update HRContext
- Add `Professions` queryable to `HRContext.cs`

#### Step 2.3: Update security.json
**File:** `Demo/HR/HR/App_Data/security.json`

Add rights for Profession and CarreerJob:
```json
{ "resource": "QueryReadEditNewDelete/Profession", "groupId": "<Administrators>" },
{ "resource": "QueryReadEditNewDelete/CarreerJob", "groupId": "<Administrators>" },
{ "resource": "QueryReadEditNew/Profession", "groupId": "<HR managers>" },
{ "resource": "QueryReadEditNew/CarreerJob", "groupId": "<HR managers>" },
{ "resource": "QueryRead/Profession", "groupId": "<Viewers>" },
{ "resource": "QueryRead/CarreerJob", "groupId": "<Viewers>" }
```

#### Step 2.4: Run Synchronize
This should generate:
- `Profession.json` with Description (string) and Regime (string) attributes
- `CarreerJob.json` with ProfessionId (Reference), ContractStart (date), ContractEnd (date) attributes
- Updated `Person.json` with Jobs attribute having `dataType: "AsDetail"`, `isArray: true`, `asDetailType: "HR.Entities.CarreerJob"`
- A `GetProfessions` query

### Phase 3: Angular Frontend -- Array AsDetail Grid

#### Step 3.1: Update `EntityAttributeDefinition` TypeScript Model
Add `isArray?: boolean` to the TypeScript interface.

#### Step 3.2: Array AsDetail Template
**File:** `po-form.component.html`

For `attr.dataType === 'AsDetail' && attr.isArray`, render:
```html
<div class="table-responsive">
  <table class="table table-sm table-bordered">
    <thead>
      <tr>
        @for (col of getAsDetailColumns(attr); track col.name) {
          <th>{{ (col.label | translate) || col.name }}</th>
        }
        <th style="width: 80px"></th>  <!-- action buttons -->
      </tr>
    </thead>
    <tbody>
      @for (row of formData[attr.name] || []; track $index) {
        <tr>
          @for (col of getAsDetailColumns(attr); track col.name) {
            <td>{{ getAsDetailCellValue(row, col) }}</td>
          }
          <td>
            <button (click)="editArrayItem(attr, $index)"><app-icon name="pencil" /></button>
            @if (canDeleteDetailRow(attr)) {
              <button (click)="removeArrayItem(attr, $index)"><app-icon name="trash" /></button>
            }
          </td>
        </tr>
      }
    </tbody>
  </table>
  @if (canCreateDetailRow(attr)) {
    <button (click)="addArrayItem(attr)"><app-icon name="plus" /> Add</button>
  }
</div>
```

#### Step 3.3: Array AsDetail Component Logic
**File:** `po-form.component.ts`

Add methods:
- `getAsDetailColumns(attr)` -- returns visible attributes of the detail entity type
- `getAsDetailCellValue(row, col)` -- resolves display value for a cell (including Reference breadcrumbs)
- `addArrayItem(attr)` -- opens modal with empty form, pushes result to array
- `editArrayItem(attr, index)` -- opens modal with row data, updates on save
- `removeArrayItem(attr, index)` -- removes item from array
- `canCreateDetailRow(attr)` -- checks `New` permission for the detail entity type
- `canDeleteDetailRow(attr)` -- checks `Delete` permission for the detail entity type

#### Step 3.4: Permission Fetching for Detail Entity Types
When `loadAsDetailTypes()` finds an array AsDetail attribute, also fetch permissions for the detail entity type:
```typescript
this.sparkService.getPermissions(asDetailType.id).subscribe(p => {
  this.asDetailPermissions[attr.name] = p;
});
```

#### Step 3.5: Apply to All Demo Apps
Repeat changes for DemoApp and Fleet `po-form` components (or extract shared library).

### Phase 4: PersistentObjectAttribute TypeScript Model

#### Step 4.1: Update Models
Ensure the `PersistentObjectAttribute` TypeScript interface supports array values:
```typescript
export interface PersistentObjectAttribute {
  name: string;
  value: any;  // Can be: string, number, boolean, object (single AsDetail), array (array AsDetail)
  dataType: string;
  isArray?: boolean;
  // ...
}
```

## Expected Synchronize Output

### Person.json (Jobs attribute)
```json
{
  "name": "Jobs",
  "label": { "en": "Jobs" },
  "dataType": "AsDetail",
  "isArray": true,
  "asDetailType": "HR.Entities.CarreerJob",
  "isRequired": false,
  "isVisible": true,
  "showedOn": "PersistentObject"
}
```

### CarreerJob.json
```json
{
  "id": "<guid>",
  "name": "CarreerJob",
  "clrType": "HR.Entities.CarreerJob",
  "attributes": [
    {
      "name": "ProfessionId",
      "dataType": "Reference",
      "referenceType": "HR.Entities.Profession",
      "query": "GetProfessions"
    },
    {
      "name": "ContractStart",
      "dataType": "date",
      "isRequired": true
    },
    {
      "name": "ContractEnd",
      "dataType": "date",
      "isRequired": false
    }
  ]
}
```

### Profession.json
```json
{
  "id": "<guid>",
  "name": "Profession",
  "clrType": "HR.Entities.Profession",
  "attributes": [
    {
      "name": "Description",
      "dataType": "string",
      "isRequired": true
    },
    {
      "name": "Regime",
      "dataType": "string",
      "isRequired": true
    }
  ]
}
```

## RavenDB Document Structure

A Person document with Jobs would look like:
```json
{
  "FirstName": "John",
  "LastName": "Doe",
  "Email": "john@example.com",
  "Company": "companies/1-A",
  "Address": { "Street": "Main St", "PostalCode": "1000", "City": "Brussels" },
  "Jobs": [
    {
      "ProfessionId": "professions/1-A",
      "ContractStart": "2020-01-15",
      "ContractEnd": "2022-06-30"
    },
    {
      "ProfessionId": "professions/2-A",
      "ContractStart": "2022-07-01",
      "ContractEnd": null
    }
  ],
  "@metadata": { "@collection": "People" }
}
```

## Out of Scope

- Inline editing of grid cells (edit via modal only)
- Drag-and-drop row reordering
- Server-side validation of individual array items (validate entire Person on save)
- Pagination of detail rows within the grid
- Backend breadcrumb resolution for references inside AsDetail arrays (frontend resolves independently)
- Changes to po-detail (read-only view) -- array AsDetail can display as comma-separated summary or "N items" text

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Breaking existing single-object AsDetail | `IsArray` defaults to `false`; all existing behavior unchanged |
| Array serialization performance | Arrays are embedded in parent doc, so no extra DB calls. RavenDB handles nested arrays natively |
| Reference resolution in detail rows | Frontend already loads reference options in modal; may need to pre-load for grid cell display |
| Permission granularity | Detail-type permission check is additive; parent entity Edit permission is still required to save |

## Acceptance Criteria

1. Adding `Profession` as a standalone entity with Description and Regime works end-to-end (CRUD)
2. Adding `CarreerJob[] Jobs` to Person triggers `isArray: true` AsDetail during synchronize
3. `CarreerJob.json` is generated with ProfessionId as Reference to Profession
4. Person edit form shows a grid/table for Jobs
5. Users with `New/CarreerJob` permission see an "Add" button on the Jobs grid
6. Users without `New/CarreerJob` permission do NOT see the "Add" button
7. Clicking "Add" or "Edit" opens a modal with the CarreerJob form (including Profession reference selector)
8. Saving the Person persists all Jobs as an embedded array in RavenDB
9. Existing single-object AsDetail (Address on Person) continues to work unchanged
