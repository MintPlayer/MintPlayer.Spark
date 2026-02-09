# Skip Separate Model Files for Projection Types - PRD

## Problem Statement

The `ModelSynchronizer.SynchronizeModels()` method currently generates **separate JSON model files** for projection types (e.g., `VPerson.json`, `VCar.json`) in addition to the correctly merged collection-type files (`Person.json`, `Car.json`).

### Current Behavior

When a `SparkContext` declares both a collection type and its projection type:

```csharp
public class HRContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<VPerson> VPeople => Session.Query<VPerson, People_Overview>();
}
```

The synchronizer:
1. Processes `Person` -> finds `VPerson` as its projection via `IndexRegistry` -> correctly merges into `Person.json` with `queryType`, `indexName`, and proper `ShowedOn` flags
2. Processes `VPerson` -> `GetRegistrationForCollectionType(VPerson)` returns null (VPerson is a projection, not a collection) -> generates a **separate `VPerson.json`** as a standalone entity with no merge logic
3. Also generates a **separate `GetVPeople.json`** query file

### Why This Is Wrong

- `VPerson.json` is a duplicate/incomplete representation - it has `ShowedOn = "Query, PersistentObject"` on all attributes, which is incorrect
- The correctly merged `Person.json` already contains all VPerson attributes with proper `ShowedOn` values
- `GetVPeople.json` is a spurious query - the VPeople queryable is only meant for list-view queries, not as a standalone entity type
- These extra files pollute the `App_Data/Model` and `App_Data/Queries` directories

### Root Cause

In `ModelSynchronizer.cs` (line 52-114), the main loop iterates all `IRavenQueryable<T>` properties. Although `processedTypes` is populated when a projection type is merged (line 77-78), it is **never checked** in the main loop before generating a file. The `processedTypes` check only exists for embedded types (line 122-123).

---

## Expected Behavior

When `VPerson` is used as an index/projection type for `Person` (linked via `[FromIndex]` and `IndexRegistry`):

1. **No `VPerson.json` file** should be generated - the properties from `Person` and `VPerson` should be merged into `Person.json` only
2. **No `GetVPeople.json` query** should be generated - the VPeople context property is only used for querying, not as a standalone entity
3. **ShowedOn** should be calculated based on where each property exists:
   - Property exists on **VPerson only** (e.g., `FullName` computed by index) -> `ShowedOn = "Query"`
   - Property exists on **Person only** (e.g., `FirstName`, `LastName`) -> `ShowedOn = "PersistentObject"`
   - Property exists on **both** with compatible data types (e.g., `Email`) -> `ShowedOn = "Query, PersistentObject"`

> Note: The ShowedOn calculation logic in `CreateOrUpdateEntityTypeDefinition` (lines 292-308) already implements this correctly for the merged `Person.json`. The issue is only that VPerson is also processed as a standalone entity.

---

## Implementation Plan

### Phase 1: Skip Projection Types in Main Loop

**File: `MintPlayer.Spark/Services/ModelSynchronizer.cs`**

Add a method to `IIndexRegistry` (or use existing data) to check if a given type is registered as a projection type. Then, in the main queryable properties loop, skip any entity type that has already been processed (i.e., was merged as a projection into another entity's JSON file).

The simplest fix: after extracting `entityType` from each queryable property (line 54), check if `processedTypes` already contains it before proceeding. Since `processedTypes` is populated when a collection type is processed and its projection is merged (line 77-78), any subsequent encounter of that projection type should be skipped.

However, this relies on processing order (collection type must come before projection type). A more robust approach:

1. **First pass**: Collect all projection types by querying the `IndexRegistry` for all registrations
2. **Main loop**: Skip any `entityType` that appears as a `ProjectionType` in any registration

#### Proposed Changes

**`IIndexRegistry` - Add helper method:**
```csharp
/// <summary>
/// Checks if the given type is registered as a projection type for any index.
/// </summary>
bool IsProjectionType(Type type);
```

**`IndexRegistry` - Implement:**
```csharp
public bool IsProjectionType(Type type)
{
    lock (_lock)
    {
        return _byCollectionType.Values.Any(r => r.ProjectionType == type);
    }
}
```

**`ModelSynchronizer.SynchronizeModels` - Add skip logic in main loop:**
```csharp
foreach (var property in queryableProperties)
{
    var entityType = GetQueryableEntityType(property.PropertyType);
    if (entityType == null) continue;

    // Skip projection types - they are merged into their collection type's JSON file
    if (indexRegistry.IsProjectionType(entityType))
    {
        Console.WriteLine($"Skipping projection type: {entityType.Name} (merged into collection type)");
        continue;
    }

    // ... rest of existing logic
}
```

### Phase 2: Clean Up Stale Projection Files

During synchronization, after the main processing is done, remove any existing JSON files for projection types that should no longer exist as standalone files.

**In `SynchronizeModels`, after all processing:**
```csharp
// Clean up stale projection type files
foreach (var registration in indexRegistry.GetAllRegistrations())
{
    if (registration.ProjectionType != null)
    {
        // Remove stale model file
        var staleModelFile = Path.Combine(modelPath, $"{registration.ProjectionType.Name}.json");
        if (File.Exists(staleModelFile))
        {
            File.Delete(staleModelFile);
            Console.WriteLine($"Removed stale projection model file: {staleModelFile}");
        }

        // Remove stale query file (find query referencing the projection context property)
        // The query name pattern is "Get{PropertyName}" where PropertyName maps to the projection queryable
        var projectionPropertyName = queryableProperties
            .FirstOrDefault(p => GetQueryableEntityType(p.PropertyType) == registration.ProjectionType)
            ?.Name;

        if (projectionPropertyName != null)
        {
            var staleQueryFile = Path.Combine(queriesPath, $"Get{projectionPropertyName}.json");
            if (File.Exists(staleQueryFile))
            {
                File.Delete(staleQueryFile);
                Console.WriteLine($"Removed stale projection query file: {staleQueryFile}");
            }
        }
    }
}
```

---

## Files to Modify

| File | Change |
|------|--------|
| `MintPlayer.Spark/Services/IndexRegistry.cs` | Add `IsProjectionType(Type type)` method to interface and implementation |
| `MintPlayer.Spark/Services/ModelSynchronizer.cs` | Skip projection types in main loop; add stale file cleanup |

## Files to Delete (by the synchronizer at runtime)

| File | Reason |
|------|--------|
| `Demo/HR/HR/App_Data/Model/VPerson.json` | Merged into Person.json |
| `Demo/Fleet/Fleet/App_Data/Model/VCar.json` | Merged into Car.json |
| `Demo/HR/HR/App_Data/Queries/GetVPeople.json` | Spurious query for projection type |
| `Demo/Fleet/Fleet/App_Data/Queries/GetVCars.json` | Spurious query for projection type |

---

## Verification

After implementation, running `--spark-synchronize-model` on the HR demo app should:

1. Generate `Person.json` with merged attributes:
   - `FirstName` (Person only) -> `showedOn: "PersistentObject"`
   - `LastName` (Person only) -> `showedOn: "PersistentObject"`
   - `Email` (both) -> `showedOn: "Query, PersistentObject"`
   - `FullName` (VPerson only) -> `showedOn: "Query"`
   - `queryType: "HR.Indexes.VPerson"`, `indexName: "People_Overview"`
2. **NOT** generate `VPerson.json`
3. **NOT** generate `GetVPeople.json`
4. Print `Skipping projection type: VPerson (merged into collection type)` in the console

Similarly for Fleet with `Car.json`/`VCar.json`.

---

## Edge Cases

1. **No projection type registered**: If a context has `IRavenQueryable<VPerson>` but VPerson has no `[FromIndex]` attribute and isn't registered in IndexRegistry, it should still be treated as a standalone entity (current behavior preserved).
2. **Multiple indexes for same collection type**: Currently IndexRegistry only supports one index per collection type. If this changes in the future, the `IsProjectionType` check should still work since it checks all registrations.
3. **Stale files from previous runs**: The cleanup phase handles removing files generated by older versions of the synchronizer.
