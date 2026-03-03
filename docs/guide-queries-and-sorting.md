# Queries and Sorting

Spark queries drive the list views in the Angular frontend. Each query maps to a SparkContext property and optionally uses a RavenDB index with a projection type for computed columns. Sorting is configurable both in the query definition (default sort) and at runtime via query string parameters.

## Overview

There are two types of queries:

| Type | Index | Projection | Use Case |
|---|---|---|---|
| Collection query | None | No | Simple list of all documents in a collection |
| Index-based query | RavenDB index | Yes | Computed columns, full-text search, cross-document data |

Collection queries return the full entity. Index-based queries return a projection type with only the columns needed for the list view.

## Collection Queries

A collection query is the simplest form. It queries all documents of a type directly from the RavenDB collection.

### Step 1: Define the Entity and SparkContext

```csharp
public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

```csharp
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
```

### Step 2: Synchronize

Run `dotnet run --spark-synchronize-model`. This generates `App_Data/Queries/GetCompanies.json`:

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440001",
  "name": "GetCompanies",
  "description": { "en": "Companies", "fr": "Entreprises", "nl": "Bedrijven" },
  "contextProperty": "Companies",
  "sortBy": "Name",
  "sortDirection": "asc"
}
```

The query name follows the pattern `Get{PropertyName}`. The `contextProperty` maps back to the SparkContext property. The synchronizer picks a default `sortBy` based on the entity's attributes (preferring `Name`, `LastName`, or the first string attribute).

### Step 3: Customize the Query JSON

After generation, you can edit the query JSON to change the default sort order, add translated descriptions, or set an alias:

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440001",
  "name": "GetCompanies",
  "description": { "en": "Companies", "fr": "Entreprises", "nl": "Bedrijven" },
  "contextProperty": "Companies",
  "alias": "companies",
  "sortBy": "EmployeeCount",
  "sortDirection": "desc"
}
```

## Index-Based Queries

For list views that need computed columns, full-text search, or cross-document data (e.g. showing a referenced entity's name), you need a RavenDB index and a projection type.

### Step 1: Create the Projection Type

The projection type defines the columns shown in the list view. Annotate it with `[FromIndex]` to link it to the index:

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Data;

[FromIndex(typeof(People_Overview))]
public class VPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
```

The `[FromIndex]` attribute tells Spark:
1. This is a projection type (not a standalone entity)
2. Which RavenDB index produces it
3. Which collection type it maps to (derived from the index's generic parameter)

### Step 2: Create the RavenDB Index

The index maps entity documents to the projection type. Use `AbstractIndexCreationTask<TEntity>`:

```csharp
using Raven.Client.Documents.Indexes;

namespace MyApp.Indexes;

public class People_Overview : AbstractIndexCreationTask<Person>
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            Id = person.Id,
                            FullName = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                            IsActive = person.IsActive
                        };

        // Enable full-text search on specific fields
        Index(nameof(VPerson.FullName), FieldIndexing.Search);
        Index(nameof(VPerson.Email), FieldIndexing.Search);

        // Store all fields so they can be projected
        StoreAllFields(FieldStorage.Yes);
    }
}
```

Key points:
- The `Map` expression computes values like `FullName = person.FirstName + " " + person.LastName`
- `Index(field, FieldIndexing.Search)` enables full-text search on that field
- `StoreAllFields(FieldStorage.Yes)` is required so RavenDB stores the computed values for projection
- Use `LoadDocument<T>(id)` to pull data from related documents (see [Reference Attributes](guide-reference-attributes.md))

### Step 3: Deploy and Synchronize

In `Program.cs`, call `CreateSparkIndexes()` to deploy indexes on startup:

```csharp
app.CreateSparkIndexes();
```

Then run model synchronization (`dotnet run --spark-synchronize-model`). The synchronizer detects the `[FromIndex]` link and:
1. Merges properties from both `Person` (entity) and `VPerson` (projection) into `Person.json`
2. Sets `queryType` and `indexName` on the entity type definition
3. Marks properties that exist only in the entity as `"showedOn": "PersistentObject"` (detail/edit pages only)
4. Marks properties that exist only in the projection as `"showedOn": "Query"` (list view only)
5. Properties in both types get `"showedOn": "Query, PersistentObject"` (shown everywhere)

### Example: Merged Model JSON

For Person, where `FirstName` and `LastName` exist only on the entity, `FullName` exists only on the projection, and `Email` exists on both:

```json
{
  "name": "Person",
  "clrType": "DemoApp.Library.Entities.Person",
  "queryType": "DemoApp.Data.VPerson",
  "indexName": "People_Overview",
  "displayAttribute": "FullName",
  "attributes": [
    {
      "name": "FirstName",
      "dataType": "string",
      "inQueryType": false,
      "showedOn": "PersistentObject"
    },
    {
      "name": "LastName",
      "dataType": "string",
      "inQueryType": false,
      "showedOn": "PersistentObject"
    },
    {
      "name": "Email",
      "dataType": "string",
      "showedOn": "Query, PersistentObject"
    },
    {
      "name": "FullName",
      "dataType": "string",
      "inCollectionType": false,
      "showedOn": "Query"
    }
  ]
}
```

### Including Cross-Document Data

To show data from a related document in the list view, use `LoadDocument` in the index map:

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
                          OwnerFullName = owner != null ? owner.Name : null,
                          Status = car.Status
                      };

        Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
        Index(nameof(VCar.OwnerFullName), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
```

The projection type must include the computed property:

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
    public ECarStatus? Status { get; set; }
}
```

## Sorting

### Default Sort Order

Each query defines a default sort in its JSON file:

```json
{
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "LastName",
  "sortDirection": "asc"
}
```

The `sortBy` value must match a property name on the type that the query returns. For index-based queries, this is the projection type (e.g. `VPerson`). For collection queries, this is the entity type.

### Runtime Sort Override

The frontend can override the sort order by passing query string parameters to the query execution endpoint:

```
GET /spark/queries/{queryId}/execute?sortBy=Email&sortDirection=desc
```

The backend reads these parameters and applies them instead of the defaults:

```csharp
var sortBy = httpContext.Request.Query["sortBy"].FirstOrDefault();
var sortDirection = httpContext.Request.Query["sortDirection"].FirstOrDefault();
```

If `sortBy` or `sortDirection` are not provided, the query falls back to the values defined in the query JSON file.

### Sortable Columns in the Frontend

The Angular frontend renders clickable column headers in query list views. Clicking a column header toggles the sort direction and re-fetches the query with the new `sortBy` and `sortDirection` parameters.

Only attributes with `"showedOn"` including `"Query"` appear as sortable columns. The current sort column and direction are reflected in the column header UI.

### Sorting on Projected Fields

For index-based queries, you can sort on computed fields that exist only in the projection type. For example, sorting by `FullName` works because it is a stored field in the index:

```json
{
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "FullName",
  "sortDirection": "asc"
}
```

The query executor applies sorting after projection, so projected fields like `FullName` are available for sorting even though they do not exist on the entity type.

## Query Execution Flow

When the frontend requests a query:

1. Backend loads the `SparkQuery` definition from `App_Data/Queries/`
2. Resolves the SparkContext property (e.g. `People`)
3. Checks IndexRegistry for a projection type linked via `[FromIndex]`
4. If an index exists, queries using the index and applies `ProjectInto` for computed fields
5. Applies sorting (on the projection type for index queries, entity type otherwise)
6. Executes the query against RavenDB
7. Maps results to `PersistentObject` format using the merged entity type definition
8. Deduplicates results by ID (indexes with `FieldIndexing.Search` can produce duplicates)
9. Returns the results as JSON

## Complete Example

From the DemoApp:

```
Demo/DemoApp/
  DemoApp/
    App_Data/
      Model/Person.json          <-- merged entity + projection attributes
      Queries/GetPeople.json     <-- query definition with sort config
    Data/VPerson.cs              <-- projection type with [FromIndex]
    Indexes/People_Overview.cs   <-- RavenDB index with computed FullName
  DemoApp.Library/
    Entities/Person.cs           <-- entity with FirstName, LastName, etc.
```

See also:
- `Demo/DemoApp/DemoApp/Indexes/Cars_Overview.cs` -- index with cross-document `LoadDocument`
- `Demo/DemoApp/DemoApp/Data/VCar.cs` -- projection with `[LookupReference]`
- `MintPlayer.Spark/Services/QueryExecutor.cs` -- query execution with sorting and projection
