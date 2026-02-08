# PRD: Cars_Overview and Companies_Overview RavenDB Indexes

**Version:** 1.0
**Date:** February 8, 2026
**Status:** Draft
**Related:** [Main PRD](./PRD.md) - Section 6.10 (FromIndex Attribute), FR-BE-010

---

## 1. Summary

Add RavenDB indexes for the Cars and Companies SparkQueries, following the established pattern from `People_Overview`. Currently, `GetCars` and `GetCompanies` query the raw RavenDB collections directly. This change introduces `Cars_Overview` and `Companies_Overview` indexes with projection classes (`VCar` and `VCompany`) to enable computed fields and optimized list views.

---

## 2. Current State

| SparkQuery | Has Index | Projection Type | Behavior |
|------------|-----------|-----------------|----------|
| GetPeople | Yes (`People_Overview`) | `VPerson` | Queries index, shows `FullName` on list, `FirstName`/`LastName` on detail |
| GetCars | No | None | Queries raw `Car` collection, shows all fields on both list and detail |
| GetCompanies | No | None | Queries raw `Company` collection, shows all fields on both list and detail |

---

## 3. Requirements

### 3.1 Cars_Overview Index

**Goal:** Create a RavenDB index that projects `Car` documents into a `VCar` view model with a computed `OwnerFullName` field that resolves the `Owner` reference (Company document ID) to the Company's `Name`.

**VCar Projection Class** (`DemoApp/Data/VCar.cs`):

```csharp
[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }  // Computed: Company.Name via LoadDocument
}
```

**Cars_Overview Index** (`DemoApp/Indexes/Cars_Overview.cs`):

```csharp
public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      let owner = LoadDocument<Company>(car.Owner)
                      select new VCar
                      {
                          Id = car.Id,
                          LicensePlate = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          OwnerFullName = owner != null ? owner.Name : null
                      };

        Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
        Index(nameof(VCar.OwnerFullName), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
```

**Attribute Visibility (ShowedOn) after synchronization:**

| Attribute | In `Car` | In `VCar` | ShowedOn | Notes |
|-----------|----------|-----------|----------|-------|
| LicensePlate | Yes | Yes | Query, PersistentObject | Shown everywhere |
| Model | Yes | Yes | Query, PersistentObject | Shown everywhere |
| Year | Yes | Yes | Query, PersistentObject | Shown everywhere |
| Color | Yes | No | PersistentObject | Detail only |
| Status | Yes | No | PersistentObject | Detail only (lookup ref) |
| Brand | Yes | No | PersistentObject | Detail only (lookup ref) |
| Owner | Yes | No | PersistentObject | Detail only (reference to Company) |
| OwnerFullName | No | Yes | Query | List only (computed from Company.Name) |

### 3.2 Companies_Overview Index

**Goal:** Create a RavenDB index that projects `Company` documents into a `VCompany` view model for optimized list views.

**VCompany Projection Class** (`DemoApp/Data/VCompany.cs`):

```csharp
[FromIndex(typeof(Companies_Overview))]
public class VCompany
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

**Companies_Overview Index** (`DemoApp/Indexes/Companies_Overview.cs`):

```csharp
public class Companies_Overview : AbstractIndexCreationTask<Company>
{
    public Companies_Overview()
    {
        Map = companies => from company in companies
                           select new VCompany
                           {
                               Id = company.Id,
                               Name = company.Name,
                               Website = company.Website,
                               EmployeeCount = company.EmployeeCount
                           };

        Index(nameof(VCompany.Name), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
```

**Attribute Visibility:** All Company attributes exist in both `Company` and `VCompany`, so all remain `ShowedOn: Query, PersistentObject`. No new computed fields are added.

---

## 4. Files to Create

| File | Description |
|------|-------------|
| `Demo/DemoApp/Data/VCar.cs` | Projection class for Cars_Overview index |
| `Demo/DemoApp/Data/VCompany.cs` | Projection class for Companies_Overview index |
| `Demo/DemoApp/Indexes/Cars_Overview.cs` | RavenDB index for Car collection |
| `Demo/DemoApp/Indexes/Companies_Overview.cs` | RavenDB index for Company collection |

## 5. Files to Update (via Model Synchronization)

After creating the index and projection classes, running `--spark-synchronize-model` will automatically update:

| File | Changes |
|------|---------|
| `App_Data/Model/Car.json` | Adds `queryType`, `indexName`, `OwnerFullName` attribute with `inCollectionType: false` and `showedOn: "Query"`. Updates existing attributes with `showedOn` based on projection membership. |
| `App_Data/Model/Company.json` | Adds `queryType` and `indexName`. Existing attributes remain unchanged. |

---

## 6. Expected Model JSON After Synchronization

### Car.json (key changes)

```json
{
  "name": "Car",
  "clrType": "DemoApp.Library.Entities.Car",
  "queryType": "DemoApp.Data.VCar",
  "indexName": "Cars_Overview",
  "displayAttribute": "LicensePlate",
  "attributes": [
    { "name": "LicensePlate", "showedOn": "Query, PersistentObject" },
    { "name": "Model", "showedOn": "Query, PersistentObject" },
    { "name": "Year", "showedOn": "Query, PersistentObject" },
    { "name": "Color", "showedOn": "PersistentObject", "inQueryType": false },
    { "name": "Status", "showedOn": "PersistentObject", "inQueryType": false },
    { "name": "Brand", "showedOn": "PersistentObject", "inQueryType": false },
    { "name": "Owner", "showedOn": "PersistentObject", "inQueryType": false },
    { "name": "OwnerFullName", "showedOn": "Query", "inCollectionType": false }
  ]
}
```

### Company.json (key changes)

```json
{
  "name": "Company",
  "clrType": "DemoApp.Library.Entities.Company",
  "queryType": "DemoApp.Data.VCompany",
  "indexName": "Companies_Overview",
  "displayAttribute": "Name",
  "attributes": [
    { "name": "Name", "showedOn": "Query, PersistentObject" },
    { "name": "Website", "showedOn": "Query, PersistentObject" },
    { "name": "EmployeeCount", "showedOn": "Query, PersistentObject" }
  ]
}
```

---

## 7. No Framework Changes Required

The existing Spark framework already fully supports this pattern:
- `FromIndexAttribute` is implemented
- `IndexRegistry` auto-discovers indexes and projections at startup
- `QueryExecutor` automatically uses indexes when a projection is registered
- `ModelSynchronizer` merges collection and projection types into a single JSON model
- `CreateSparkIndexes()` deploys indexes at startup

Only new DemoApp-level files (indexes + projections) need to be created.

---

## 8. Verification Steps

1. Create the 4 new files (VCar, VCompany, Cars_Overview, Companies_Overview)
2. Run `--spark-synchronize-model` to regenerate `Car.json` and `Company.json`
3. Start the application - indexes are auto-deployed via `CreateSparkIndexes()`
4. Verify Cars list view shows: LicensePlate, Model, Year, OwnerFullName (resolved Company name)
5. Verify Cars detail view shows: LicensePlate, Model, Year, Color, Status, Brand, Owner (reference picker)
6. Verify Companies list view shows: Name, Website, EmployeeCount (all fields, via index)
7. Verify Companies detail view shows: Name, Website, EmployeeCount (same fields, from collection)
