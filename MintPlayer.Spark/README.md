# MintPlayer.Spark

A low-code .NET library for building data-driven web applications with minimal boilerplate. Spark uses a PersistentObject pattern to eliminate DTOs and repository layers, letting you focus on your domain logic.

## Installation

```bash
dotnet add package MintPlayer.Spark
```

## Quick Start

### 1. Configure Services

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, MySparkContext>();
builder.Services.AddSparkActions(); // Auto-discovered Actions

var app = builder.Build();

app.UseSpark();
app.SynchronizeSparkModelsIfRequested<MySparkContext>(args);
app.CreateSparkIndexes();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
});

app.Run();
```

### 2. Configuration (appsettings.json)

```json
{
  "Spark": {
    "RavenDb": {
      "Urls": ["http://localhost:8080"],
      "Database": "MyApp"
    }
  }
}
```

### 3. Define SparkContext

Create a context class that exposes your entity collections:

```csharp
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
```

### 4. Define Entities

```csharp
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    [Reference(typeof(Company), "GetCompanies")]
    public string? Company { get; set; }

    public Address? Address { get; set; } // Nested object (AsDetail)
}

public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
}
```

### 5. Synchronize Models

Run with the synchronization flag to generate JSON model files:

```bash
dotnet run --spark-synchronize-model
```

This creates entity definitions in `App_Data/Model/` that the framework uses at runtime.

## Core Concepts

### PersistentObject

The universal data container that replaces traditional DTOs. Contains an ID, type name, and a collection of attributes with their values and metadata.

### SparkContext

A registry pattern (similar to EF Core's DbContext) that tracks available entity types through `IRavenQueryable<T>` properties. The framework discovers entities by reflecting over these properties.

### Actions Classes

Customization hooks for entity-specific business logic. Inherit from `DefaultPersistentObjectActions<T>` to add validation or custom behavior:

```csharp
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    public override Task OnBeforeSaveAsync(Person entity)
    {
        if (string.IsNullOrEmpty(entity.FirstName))
            throw new ValidationException("FirstName is required");
        return Task.CompletedTask;
    }

    public override Task OnAfterSaveAsync(Person entity)
    {
        // Post-save logic (notifications, logging, etc.)
        return Task.CompletedTask;
    }
}
```

Available hooks:
- `OnQueryAsync` - Customize list queries
- `OnLoadAsync` - Customize single entity loading
- `OnSaveAsync` - Customize save operation
- `OnDeleteAsync` - Customize delete operation
- `OnBeforeSaveAsync` - Pre-save validation/logic
- `OnAfterSaveAsync` - Post-save logic
- `OnBeforeDeleteAsync` - Pre-delete logic

## Entity Attributes

| Attribute | Purpose | Example |
|-----------|---------|---------|
| `[Reference]` | Foreign key to another entity | `[Reference(typeof(Company), "GetCompanies")]` |
| `[LookupReferenceName]` | Reference to lookup values by name | `[LookupReferenceName("CarStatus")]` |
| `[LookupReference]` | Reference to lookup values by type | `[LookupReference(typeof(CarStatus))]` |
| `[FromIndex]` | Links projection class to RavenDB index | `[FromIndex(typeof(People_Overview))]` |

## API Endpoints

The `MapSpark()` extension creates these REST endpoints:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/` | GET | Health check |
| `/spark/types` | GET | List all entity types |
| `/spark/types/{id}` | GET | Get entity type definition |
| `/spark/queries` | GET | List all queries |
| `/spark/queries/{id}` | GET | Get query definition |
| `/spark/queries/{id}/execute` | GET | Execute query and return results |
| `/spark/po/{typeId}` | GET | List entities of a type |
| `/spark/po/{typeId}/{id}` | GET | Get entity by ID |
| `/spark/po/{typeId}` | POST | Create new entity |
| `/spark/po/{typeId}/{id}` | PUT | Update entity |
| `/spark/po/{typeId}/{id}` | DELETE | Delete entity |
| `/spark/program-units` | GET | Get navigation structure |
| `/spark/lookupref` | GET | List lookup reference types |
| `/spark/lookupref/{name}` | GET | Get lookup reference values |
| `/spark/lookupref/{name}` | POST | Add lookup value |
| `/spark/lookupref/{name}/{key}` | PUT | Update lookup value |
| `/spark/lookupref/{name}/{key}` | DELETE | Delete lookup value |

## Extension Methods

| Method | Description |
|--------|-------------|
| `AddSpark(IConfiguration)` | Register Spark services with configuration |
| `AddSpark(Action<SparkOptions>)` | Register Spark services with options delegate |
| `AddSparkActions()` | Register all entity-specific Actions classes |
| `AddSparkActions<TActions, TEntity>()` | Register specific Actions class |
| `UseSpark()` | Add Spark middleware to the pipeline |
| `MapSpark()` | Map Spark REST endpoints |
| `SynchronizeSparkModels<T>()` | Sync entity models from SparkContext |
| `SynchronizeSparkModelsIfRequested<T>(args)` | Sync if `--spark-synchronize-model` flag present |
| `CreateSparkIndexes()` | Deploy RavenDB indexes from loaded assemblies |
| `CreateSparkIndexesAsync()` | Async version of index deployment |

## JSON Model Structure

### Entity Type Definition (App_Data/Model/*.json)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Person",
  "clrType": "MyApp.Data.Person",
  "displayAttribute": "FullName",
  "attributes": [
    {
      "name": "FirstName",
      "dataType": "string",
      "isRequired": true,
      "showedOn": "Query, PersistentObject"
    },
    {
      "name": "Company",
      "dataType": "Reference",
      "referenceType": "Company",
      "referenceQuery": "GetCompanies",
      "showedOn": "PersistentObject"
    }
  ]
}
```

### Spark Query (App_Data/Queries/*.json)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440001",
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "LastName",
  "sortDirection": "asc"
}
```

### Program Units (App_Data/programUnits.json)

```json
{
  "programUnitGroups": [
    {
      "name": "Master Data",
      "icon": "bi-database",
      "programUnits": [
        {
          "id": "990e8400-e29b-41d4-a716-446655440001",
          "name": "People",
          "type": "query",
          "queryId": "550e8400-e29b-41d4-a716-446655440001"
        }
      ]
    }
  ]
}
```

## Data Types

| DataType | CLR Types | Description |
|----------|-----------|-------------|
| `string` | `string` | Text input |
| `number` | `int`, `long` | Integer number input |
| `decimal` | `decimal`, `double`, `float` | Decimal number input |
| `boolean` | `bool` | Checkbox |
| `datetime` | `DateTime` | Date and time picker |
| `date` | `DateOnly` | Date picker |
| `guid` | `Guid` | GUID input |
| `Reference` | `string` with `[Reference]` | Lookup to another entity |
| `AsDetail` | Nested object | Inline nested form |

## ShowedOn Visibility

Control where attributes appear using the `showedOn` property:

| Value | Description |
|-------|-------------|
| `Query` | Show only in list views |
| `PersistentObject` | Show only in detail/edit views |
| `Query, PersistentObject` | Show in both (default) |

## Index-Based Queries

For optimized list views with projections, use RavenDB indexes:

```csharp
// Projection class
[FromIndex(typeof(People_Overview))]
public class VPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
}

// Index definition
public class People_Overview : AbstractIndexCreationTask<Person>
{
    public People_Overview()
    {
        Map = people => from p in people
                        let company = LoadDocument<Company>(p.Company)
                        select new VPerson
                        {
                            Id = p.Id,
                            FullName = p.FirstName + " " + p.LastName,
                            CompanyName = company != null ? company.Name : null
                        };
    }
}
```

Then reference the projection in your SparkContext:

```csharp
public IRavenQueryable<VPerson> PeopleOverview => Session.Query<VPerson, People_Overview>();
```

## Requirements

- .NET 10.0+
- RavenDB 6.2+

## License

MIT License
