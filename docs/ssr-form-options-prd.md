# PRD: SSR Support for Form Dropdown Options (Reference & LookupReference)

## Problem

When JavaScript is disabled and a user visits an edit page (e.g., `/po/car/Cars%2F.../edit`), the form renders server-side but `<select>` elements for **LookupReference** (e.g., Brand, Status) and **Reference** (e.g., Owner) attributes are empty. The options are only loaded client-side via API calls in `spark-po-form`'s constructor effect, which is guarded by `!isPlatformServer()`.

## Goal

Supply LookupReference and Reference option data from the server during SSR so that `<select>` elements render with all their options in the noscript case.

## Current Architecture

### Client-side loading (spark-po-form.component.ts)

The form component has three loading methods triggered by an effect when `entityType` changes:

1. **`loadLookupReferenceOptions()`** - For attributes with `lookupReferenceType` set:
   - Calls `sparkService.getLookupReference(name)` for each unique lookup type
   - Stores in signal: `lookupReferenceOptions = signal<Record<string, LookupReference>>({})`
   - Key: lookup reference type name (e.g., `"CarStatus"`)

2. **`loadReferenceOptions()`** - For attributes with `dataType === 'Reference'` and a `query`:
   - Calls `sparkService.executeQueryByName(attr.query!)` for each attribute
   - Stores in signal: `referenceOptions = signal<Record<string, PersistentObject[]>>({})`
   - Key: attribute name (e.g., `"Owner"`)

3. **`loadAsDetailTypes()`** - For attributes with `dataType === 'AsDetail'`:
   - Fetches entity types, permissions, and nested reference options
   - Stores in signals: `asDetailTypes`, `asDetailPermissions`, `asDetailReferenceOptions`

### Server-side (SpaPrerenderingService.cs)

Currently supplies for po-edit route:
- `entityTypes` - all entity type definitions
- `entityType` - the specific entity type
- `persistentObject` - the loaded persistent object
- `permissions` - CRUD permissions

**Not supplied:** `lookupReferenceOptions`, `referenceOptions`, `asDetailTypes`, `asDetailPermissions`, `asDetailReferenceOptions`

### Template rendering

- **LookupReference dropdown**: `@for (option of (attr | lookupOptions:lookupReferenceOptions()); ...)` - iterates `lookupReferenceOptions` signal
- **Reference display**: `attr | referenceDisplayValue:formData():referenceOptions()` - resolves ID to name via `referenceOptions` signal
- **Inline AsDetail Reference**: `attr | inlineRefOptions:col:asDetailReferenceOptions()` - nested reference options

## Implementation Plan

### Phase 1: Server-side data supply

**File: All 3 `SpaPrerenderingService.cs` (DemoApp, HR, Fleet)**

In the `"po-edit"` case (or a shared helper), after loading `entityType` and `persistentObject`:

1. **Inject** `ILookupReferenceService` and `IQueryExecutor` (both already available in the DI container)

2. **Supply lookup reference options**:
   - Iterate `entityType.Attributes` where `LookupReferenceType` is set and the attribute is visible/editable
   - For each unique `LookupReferenceType`, call `lookupReferenceService.GetAsync(name)`
   - Build `Dictionary<string, LookupReferenceDto>` keyed by lookup type name
   - Add to `data["lookupReferenceOptions"]`

3. **Supply reference options**:
   - Iterate `entityType.Attributes` where `DataType == "Reference"` and `Query` is set
   - For each attribute, resolve the query via `queryLoader.GetQueryByName(attr.Query)`, then execute via `queryExecutor.ExecuteQueryAsync(query)`
   - Build `Dictionary<string, List<PersistentObject>>` keyed by attribute name
   - Add to `data["referenceOptions"]`

4. **Supply AsDetail types** (stretch - only if AsDetail attributes exist):
   - Iterate attributes where `DataType == "AsDetail"` and `AsDetailType` is set
   - Resolve the entity type definition for each via `modelLoader.GetEntityTypeByClrType(attr.AsDetailType)`
   - Build `Dictionary<string, EntityTypeDefinition>` keyed by attribute name
   - Add to `data["asDetailTypes"]`
   - For array AsDetail attributes, also supply permissions and nested reference options

### Phase 2: Angular hydration (spark-po-form.component.ts)

1. **Inject** `PLATFORM_ID` (already done) and `SPARK_SERVER_DATA` (new)

2. **Add `OnInit` lifecycle** (or use constructor):
   - If `isPlatformServer()` and server data is available:
     - Hydrate `lookupReferenceOptions` from `serverData['lookupReferenceOptions']`
     - Hydrate `referenceOptions` from `serverData['referenceOptions']`
     - Hydrate `asDetailTypes` from `serverData['asDetailTypes']` (if present)

3. **Problem**: `spark-po-form` is a child component that receives `entityType` as an input. It does NOT directly inject `SPARK_SERVER_DATA`. Two approaches:

   **Option A (Preferred): Flow data through parent component**
   - `spark-po-edit` reads `lookupReferenceOptions` and `referenceOptions` from `SPARK_SERVER_DATA`
   - Pass them as new inputs to `<spark-po-form>`
   - Form component uses inputs as initial values for its signals, skipping API calls if pre-populated

   **Option B: Inject SPARK_SERVER_DATA in form component**
   - Form component directly injects `SPARK_SERVER_DATA` (optional)
   - Hydrates its own signals in constructor/ngOnInit
   - Simpler wiring but breaks component encapsulation slightly

   **Recommendation**: Option A - keeps data flow explicit and predictable.

### Phase 3: Skip client-side loading when pre-hydrated

In the form component's constructor effect, check if signals are already populated before loading:

```typescript
effect(() => {
  const et = this.entityType();
  if (et && !isPlatformServer(this.platformId)) {
    if (Object.keys(this.lookupReferenceOptions()).length === 0) {
      this.loadLookupReferenceOptions();
    }
    if (Object.keys(this.referenceOptions()).length === 0) {
      this.loadReferenceOptions();
    }
    if (Object.keys(this.asDetailTypes()).length === 0) {
      this.loadAsDetailTypes();
    }
  }
});
```

**Important**: On client-side navigation (no SSR), signals start empty and loading proceeds normally. During SSR hydration, Angular transfers state - but since the form currently skips API calls on server anyway, the client will still load fresh data. The pre-populated signals are only needed for the SSR HTML render.

## Files to Modify

### C# (Server)
1. `Demo/DemoApp/DemoApp/Services/SpaPrerenderingService.cs`
2. `Demo/HR/HR/Services/SpaPrerenderingService.cs`
3. `Demo/Fleet/Fleet/Services/SpaPrerenderingService.cs`

### TypeScript (Angular)
4. `node_packages/ng-spark/src/lib/components/po-edit/spark-po-edit.component.ts` - read server data, pass to form
5. `node_packages/ng-spark/src/lib/components/po-edit/spark-po-edit.component.html` - bind new inputs
6. `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.ts` - add inputs, hydrate signals

## Data Shape

### lookupReferenceOptions (server -> client)
```json
{
  "CarBrand": {
    "name": "CarBrand",
    "isTransient": false,
    "displayType": 0,
    "values": [
      { "key": "bmw", "values": { "en": "BMW", "nl": "BMW" }, "isActive": true },
      { "key": "audi", "values": { "en": "Audi", "nl": "Audi" }, "isActive": true }
    ]
  },
  "CarStatus": { ... }
}

```

### referenceOptions (server -> client)
```json
{
  "Owner": [
    { "id": "Companies/1", "name": "Acme Corp", "objectTypeId": "...", "attributes": [...] },
    { "id": "Companies/2", "name": "Globex", ... }
  ]
}
```

## Scope

- **In scope**: LookupReference dropdowns, Reference attribute options
- **Stretch**: AsDetail type resolution and nested reference options
- **Out of scope**: Modal-based reference/lookup selectors (these require JS interaction anyway)

## Testing

1. Navigate to edit page with JS disabled - verify `<select>` elements contain correct options
2. Navigate to edit page with JS enabled - verify client-side loading still works normally
3. Test with multiple lookup attributes on same entity type
4. Test with Reference attributes that use custom queries
