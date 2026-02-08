# PRD: LookupReference Refactor — Move to Library & Type-Based Attribute

**Date:** February 8, 2026
**Status:** Draft

---

## Summary

Move the `LookupReferences/` folder from `DemoApp` to `DemoApp.Library`, eliminate the string-based `LookupReferenceNameAttribute`, and use the existing type-based `LookupReferenceAttribute` exclusively. Also add `Status` to `VCar` so the query/overview page can display it.

No backward compatibility is required.

---

## Current State

| Item | Location | Notes |
|------|----------|-------|
| `LookupReferenceAttribute` | `MintPlayer.Spark.Abstractions` | Accepts `Type lookupType` — **already exists** |
| `LookupReferenceNameAttribute` | `MintPlayer.Spark.Abstractions` | Accepts `string name` — exists because entities in the Library couldn't reference lookup types in DemoApp |
| `CarStatus.cs`, `CarBrand.cs` | `Demo/DemoApp/LookupReferences/` | Concrete lookup reference classes |
| `Car.cs` | `Demo/DemoApp.Library/Entities/` | Uses `[LookupReferenceName("CarStatus")]` (string-based) |
| `VCar.cs` | `Demo/DemoApp/Data/` | Missing `Status` property entirely |
| `Cars_Overview` index | `Demo/DemoApp/Indexes/` | Does not project `Status` |
| `ModelSynchronizer` | `MintPlayer.Spark/Services/` | Handles both `LookupReferenceAttribute` and `LookupReferenceNameAttribute` |

### Why the string-based attribute exists

`DemoApp.Library` only references `MintPlayer.Spark.Abstractions`. It cannot reference `DemoApp` (where the lookup classes live), so `Car.cs` had to use `[LookupReferenceName("CarStatus")]` with a string name instead of `[LookupReference(typeof(CarStatus))]`.

---

## Proposed Changes

### 1. Move `LookupReferences/` folder to `DemoApp.Library`

**Action:** `git mv Demo/DemoApp/LookupReferences Demo/DemoApp.Library/LookupReferences`

- Move `CarStatus.cs` and `CarBrand.cs` to `DemoApp.Library/LookupReferences/`
- Update namespace from `DemoApp.LookupReferences` to `DemoApp.Library.LookupReferences`
- Since `DemoApp.Library` already references `MintPlayer.Spark.Abstractions` (which contains `TransientLookupReference`, `DynamicLookupReference`, etc.), no new project references are needed

### 2. Switch `Car.cs` to type-based `LookupReferenceAttribute`

**File:** `Demo/DemoApp.Library/Entities/Car.cs`

```csharp
// Before
[LookupReferenceName("CarStatus")]
public string? Status { get; set; }

[LookupReferenceName("CarBrand")]
public string? Brand { get; set; }

// After
[LookupReference(typeof(CarStatus))]
public string? Status { get; set; }

[LookupReference(typeof(CarBrand))]
public string? Brand { get; set; }
```

### 3. Delete `LookupReferenceNameAttribute`

**File to delete:** `MintPlayer.Spark.Abstractions/LookupReferenceNameAttribute.cs`

No backward compatibility needed.

### 4. Simplify `ModelSynchronizer` — remove `LookupReferenceNameAttribute` fallback

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs`

Keep the existing `LookupReferenceAttribute` handling, but remove the `LookupReferenceNameAttribute` fallback:

- Remove the `property.GetCustomAttribute<LookupReferenceNameAttribute>()` call
- Remove the `lookupRefNameAttr` variable
- Simplify the lookup reference type resolution from `lookupRefAttr?.LookupType.Name ?? lookupRefNameAttr?.Name` to just `lookupRefAttr?.LookupType.Name`

### 5. Add `Status` to `VCar`

**File:** `Demo/DemoApp/Data/VCar.cs`

```csharp
[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }

    [LookupReference(typeof(CarStatus))]
    public string? Status { get; set; }
}
```

Note: `VCar` lives in `DemoApp` which already references `DemoApp.Library`, so it can use `typeof(CarStatus)`.

### 6. Update `Cars_Overview` index to project `Status`

**File:** `Demo/DemoApp/Indexes/Cars_Overview.cs`

```csharp
Map = cars => from car in cars
              let owner = LoadDocument<Company>(car.Owner)
              select new VCar
              {
                  Id = car.Id,
                  LicensePlate = car.LicensePlate,
                  Model = car.Model,
                  Year = car.Year,
                  OwnerFullName = owner != null ? owner.Name : null,
                  Status = car.Status
              };
```

---

## Files Changed

| File | Action |
|------|--------|
| `Demo/DemoApp/LookupReferences/CarStatus.cs` | `git mv` to `Demo/DemoApp.Library/LookupReferences/`, update namespace |
| `Demo/DemoApp/LookupReferences/CarBrand.cs` | `git mv` to `Demo/DemoApp.Library/LookupReferences/`, update namespace |
| `Demo/DemoApp.Library/Entities/Car.cs` | Replace `[LookupReferenceName("...")]` with `[LookupReference(typeof(...))]`, add using |
| `Demo/DemoApp/Data/VCar.cs` | Add `Status` property with `[LookupReference(typeof(CarStatus))]` |
| `Demo/DemoApp/Indexes/Cars_Overview.cs` | Add `Status = car.Status` to projection |
| `MintPlayer.Spark.Abstractions/LookupReferenceNameAttribute.cs` | **Delete** |
| `MintPlayer.Spark/Services/ModelSynchronizer.cs` | Remove `LookupReferenceNameAttribute` handling |

### Files NOT changed

- `MintPlayer.Spark.Abstractions/LookupReferenceAttribute.cs` — already correct, accepts `Type`
- `DemoApp.Library.csproj` — already references Abstractions, no new dependencies
- Frontend files — no changes needed, the lookup resolution logic remains the same (works by name)

---

## Verification

After implementing, verify:
1. Car-Show page displays translated Status value (e.g. "In gebruik")
2. Car-Edit page dropdown displays translated Status values
3. Car-Query/overview page displays translated Status values (was empty before)
4. CarBrand (dynamic lookup) still works correctly
5. Application builds without errors (no remaining references to `LookupReferenceNameAttribute`)
