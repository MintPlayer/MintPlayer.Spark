# MintPlayer.Spark

A low-code .NET library for building data-driven web applications with minimal boilerplate. Spark uses a PersistentObject pattern to eliminate DTOs and repository layers, letting you focus on your domain logic.

Spark is built on RavenDB and ASP.NET Core. It auto-generates CRUD endpoints, model JSON files, and a data-driven Angular frontend from your C# entity classes. You define entities, register them in a SparkContext, and the framework takes care of REST API routing, persistence, and UI rendering.

## Overview

A typical Spark application has three layers:

1. **Entity classes** -- plain C# classes stored in RavenDB
2. **SparkContext** -- exposes queryable collections of your entities
3. **Model JSON files** -- auto-generated metadata that drives the Angular frontend

The Angular frontend reads the model JSON at runtime to render list views, detail pages, and create/edit forms -- without writing any page-specific code.

## Installation

```bash
dotnet add package MintPlayer.Spark
```

A typical solution also references:

```
MintPlayer.Spark
MintPlayer.Spark.Abstractions
MintPlayer.Spark.SourceGenerators
```

It is common to keep entity classes in a separate library project (e.g. `MyApp.Library`) so they can be shared across modules.

## Quick Start

> **Tip:** For the fastest setup, use [`MintPlayer.Spark.AllFeatures`](../MintPlayer.Spark.AllFeatures/README.md) which source-generates `AddSparkFull` / `UseSparkFull` / `MapSparkFull` — three calls instead of the granular setup below.

### 1. Configure Services

You can wire Spark up in two ways.

#### Option A: AllFeatures (recommended)

Reference `MintPlayer.Spark.AllFeatures` and use the source-generated convenience methods. The generator discovers your `SparkContext`, Actions, and Recipients at compile time:

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSparkFull(builder.Configuration);     // Everything in one call

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSparkFull(args);                                   // Middleware + model sync

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSparkFull();                             // Map all Spark endpoints
});

app.Run();
```

#### Option B: Granular setup

If you only need a subset of features, reference individual packages and wire them up explicitly:

```csharp
// Program.cs
using MyApp;
using MintPlayer.Spark;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddActions();       // Source-generated: registers all Actions classes
    spark.AddMessaging();     // Optional: durable message bus
    spark.AddRecipients();    // Source-generated: registers all IRecipient<T> classes
});

var app = builder.Build();

app.UseRouting();
app.UseSpark(o => o.SynchronizeModelsIfRequested<MySparkContext>(args));

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

In development mode, Spark automatically creates the database if it does not exist.

### 3. Define SparkContext

Create a context class that exposes your entity collections. Each public `IRavenQueryable<T>` property defines a collection that Spark will manage:

```csharp
using MyApp.Library.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
```

The property names (`People`, `Companies`) are used to generate default query names (`GetPeople`, `GetCompanies`).

### 4. Define Entities

Each entity is a plain C# class with an `Id` property and public read/write properties. Spark inspects these properties to generate the model JSON.

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

public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

Nullable properties (e.g. `int?`, `string?`) are treated as optional. Non-nullable value types (e.g. `int`, `bool`) are marked as required.

### 5. Synchronize Models

Run with the synchronization flag to generate JSON model files:

```bash
dotnet run --spark-synchronize-model
```

This creates entity definitions in `App_Data/Model/` (and default queries in `App_Data/Queries/`) that the framework uses at runtime. See [Model Synchronization](#model-synchronization) below for details. The process exits after synchronization — run it again whenever you add or change entity properties.

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
| `/spark/queries/{id}/execute` | GET | Execute query and return results (paginated) |
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

All mutation endpoints (POST, PUT, DELETE) require an `X-XSRF-TOKEN` header. The XSRF token is automatically provided as a cookie by the `UseSpark()` middleware.

## Extension Methods

| Method | Description |
|--------|-------------|
| `AddSparkFull(IConfiguration)` | **AllFeatures**: Registers all Spark services, actions, auth, messaging in one call |
| `UseSparkFull(args)` | **AllFeatures**: Adds middleware + synchronizes models if `--spark-synchronize-model` is passed |
| `MapSparkFull()` | **AllFeatures**: Maps all Spark REST endpoints |
| `AddSpark(IConfiguration)` | Register Spark services with configuration |
| `AddSpark(Action<SparkOptions>)` | Register Spark services with options delegate |
| `AddSparkActions()` | Register all entity-specific Actions classes |
| `AddSparkActions<TActions, TEntity>()` | Register specific Actions class |
| `UseSpark()` | Add Spark middleware to the pipeline |
| `UseSpark(Action<UseSparkOptions>)` | Add Spark middleware with options (e.g. `SynchronizeModelsIfRequested`) |
| `MapSpark()` | Map Spark REST endpoints |
| `SynchronizeSparkModels<T>()` | Sync entity models from SparkContext |
| `SynchronizeSparkModelsIfRequested<T>(args)` | Sync if `--spark-synchronize-model` flag present |
| `CreateSparkIndexes()` | Deploy RavenDB indexes from loaded assemblies |
| `CreateSparkIndexesAsync()` | Async version of index deployment |

## Model Synchronization

Model synchronization scans your SparkContext, inspects the entity classes, and generates JSON files under `App_Data/Model/` and `App_Data/Queries/`.

Run the application with the `--spark-synchronize-model` flag:

```bash
dotnet run --spark-synchronize-model
```

This generates:
- `App_Data/Model/Person.json` -- entity type definition with attributes, data types, and validation rules
- `App_Data/Model/Company.json` -- same for Company
- `App_Data/Queries/GetPeople.json` -- default query definition for People
- `App_Data/Queries/GetCompanies.json` -- default query definition for Companies

The process exits after synchronization. Run it again whenever you add or change entity properties.

### What Model Synchronization Does

For each `IRavenQueryable<T>` property on your SparkContext:

1. Reflects over the entity type's public properties
2. Creates or updates `App_Data/Model/{EntityName}.json` with attribute definitions
3. Generates a default query file `App_Data/Queries/Get{PropertyName}.json` (if it does not already exist)
4. Preserves any manual edits to existing JSON files (labels, validation rules, ordering, tabs, groups)
5. Detects and merges projection types (if an index-based query exists)

### Customizing Generated Files

After synchronization, you can manually edit the JSON files to:
- Add translated labels (`label`, `description`) in multiple languages
- Add validation rules (`rules` array with `minLength`, `maxLength`, `regex`, `email`, `url`, `range`)
- Change attribute ordering (`order`)
- Change visibility (`isVisible`, `showedOn`)
- Mark attributes as read-only (`isReadOnly`)
- Set the display attribute (`displayAttribute`)
- Add tabs and groups for organizing attributes on detail pages

These manual edits are preserved when you re-run model synchronization. The synchronizer only adds new attributes and updates data types -- it does not overwrite labels, rules, or ordering.

## JSON Model Structure

### Entity Type Definition (App_Data/Model/*.json)

A generated model JSON file looks like this:

```json
{
  "id": "660e8400-e29b-41d4-a716-446655440000",
  "name": "Company",
  "description": {
    "en": "Company",
    "fr": "Entreprise",
    "nl": "Bedrijf"
  },
  "clrType": "DemoApp.Library.Entities.Company",
  "queryType": "DemoApp.Data.VCompany",
  "indexName": "Companies_Overview",
  "displayAttribute": "Name",
  "tabs": [],
  "groups": [],
  "attributes": [
    {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "name": "Name",
      "label": { "en": "Company Name", "fr": "Nom de l'entreprise", "nl": "Bedrijfsnaam" },
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 1,
      "isArray": false,
      "showedOn": "Query, PersistentObject",
      "rules": [
        { "type": "minLength", "value": 2 },
        { "type": "maxLength", "value": 200 }
      ]
    }
  ]
}
```

Key fields:
- `id` -- stable GUID for the entity type (auto-generated, preserved across syncs)
- `clrType` -- the full CLR type name used for serialization/deserialization
- `queryType` -- the projection type used for list queries (set when a `[FromIndex]` projection exists)
- `indexName` -- the RavenDB index name for list queries
- `displayAttribute` -- which attribute to show as the entity's display name
- `description` -- translated strings for UI labels (editable after generation)

A reference attribute is represented like this:

```json
{
  "name": "Company",
  "dataType": "Reference",
  "referenceType": "Company",
  "referenceQuery": "GetCompanies",
  "showedOn": "PersistentObject"
}
```

### Spark Query (App_Data/Queries/*.json)

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

- `contextProperty` -- maps to the SparkContext property name
- `sortBy` / `sortDirection` -- default sort order for list views

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

Spark maps C# types to model data types automatically:

| DataType | CLR Types | Description |
|----------|-----------|-------------|
| `string` | `string` | Text input |
| `number` | `int`, `long` | Integer number input |
| `decimal` | `decimal`, `double`, `float` | Decimal number input |
| `boolean` | `bool` | Checkbox |
| `datetime` | `DateTime`, `DateTimeOffset` | Date and time picker |
| `date` | `DateOnly` | Date picker |
| `guid` | `Guid` | GUID input |
| `color` | `System.Drawing.Color` | Color picker |
| `Reference` | `string` with `[Reference]` | Lookup to another entity |
| `AsDetail` | Nested object (complex class, e.g. `Address`) | Inline nested form |

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

## Angular Frontend

The Angular frontend uses the `@mintplayer/ng-spark` library, which reads entity type definitions and query definitions from the API and renders list pages, detail pages, and create/edit forms automatically.

The SPA is configured in `Program.cs` with the standard ASP.NET Core SPA middleware:

```csharp
app.MapWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start");
            }
        });
    });
```

Any request that does not start with `/spark` is routed to the Angular application.

## Project Structure

A complete Spark application typically looks like this:

```
MyApp/
  MyApp.Library/
    Entities/
      Company.cs
      Person.cs
      Car.cs
    LookupReferences/
      CarStatus.cs
  MyApp/
    App_Data/
      Model/
        Company.json
        Person.json
        Car.json
      Queries/
        GetCompanies.json
        GetPeople.json
        GetCars.json
    Actions/
      PersonActions.cs
    Data/
      VPerson.cs
      VCompany.cs
    Indexes/
      People_Overview.cs
      Companies_Overview.cs
    ClientApp/
      src/app/...
    MySparkContext.cs
    Program.cs
    appsettings.json
```

## Requirements

- .NET 10.0+ SDK
- RavenDB 6.2+ (default: `http://localhost:8080`)
- Node.js 20+ and npm (for the Angular frontend)

## Next Steps

- [Reference Attributes](../docs/guide-reference-attributes.md) -- link entities together with navigation properties
- [Queries and Sorting](../docs/guide-queries-and-sorting.md) -- define RavenDB indexes and sortable list views
- [Authorization](../MintPlayer.Spark.Authorization/README.md) -- add permission-based access control
- [Custom Attribute Renderers](../docs/guide-custom-attribute-renderers.md) -- replace default UI rendering with custom Angular components

## License

MIT License
