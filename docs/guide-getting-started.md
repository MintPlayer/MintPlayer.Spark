# Getting Started with MintPlayer.Spark

MintPlayer.Spark is a framework built on RavenDB and ASP.NET Core that auto-generates CRUD endpoints, model JSON files, and a data-driven Angular frontend from your C# entity classes. You define entities, register them in a SparkContext, and the framework takes care of REST API routing, persistence, and UI rendering.

## Overview

A typical Spark application has three layers:

1. **Entity classes** -- plain C# classes stored in RavenDB
2. **SparkContext** -- exposes queryable collections of your entities
3. **Model JSON files** -- auto-generated metadata that drives the Angular frontend

The Angular frontend reads the model JSON at runtime to render list views, detail pages, and create/edit forms -- without writing any page-specific code.

## Prerequisites

- .NET 10 SDK (or later)
- RavenDB instance running locally (default: `http://localhost:8080`)
- Node.js 20+ and npm (for the Angular frontend)

## Step 1: Create the Project

Create a new ASP.NET Core project and add the Spark NuGet packages:

```
MintPlayer.Spark
MintPlayer.Spark.Abstractions
MintPlayer.Spark.SourceGenerators
```

A typical solution also has a separate library project for entity classes (e.g. `MyApp.Library`) so they can be shared across modules.

## Step 2: Define Entity Classes

Each entity is a plain C# class with an `Id` property and public read/write properties. Spark inspects these properties to generate the model JSON.

```csharp
// MyApp.Library/Entities/Company.cs
namespace MyApp.Library.Entities;

public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

```csharp
// MyApp.Library/Entities/Person.cs
using MintPlayer.Spark.Abstractions;

namespace MyApp.Library.Entities;

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public bool IsActive { get; set; }
}
```

Spark maps C# types to model data types automatically:

| C# Type | Model `dataType` |
|---|---|
| `string` | `string` |
| `int`, `long` | `number` |
| `decimal`, `double`, `float` | `decimal` |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset` | `datetime` |
| `DateOnly` | `date` |
| `Guid` | `guid` |
| `System.Drawing.Color` | `color` |
| Complex class (e.g. `Address`) | `AsDetail` |

Nullable properties (e.g. `int?`, `string?`) are treated as optional. Non-nullable value types (e.g. `int`, `bool`) are marked as required.

## Step 3: Create the SparkContext

The SparkContext is the central registry of your entity collections. Each public `IRavenQueryable<T>` property defines a collection that Spark will manage.

```csharp
// MyApp/MySparkContext.cs
using MyApp.Library.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace MyApp;

public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
```

The property names (`People`, `Companies`) are used to generate default query names (`GetPeople`, `GetCompanies`).

## Step 4: Configure Program.cs

### Option A: AllFeatures (recommended)

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

### Option B: Granular setup

If you only need a subset of features, reference individual packages and wire them up explicitly:

```csharp
// Program.cs
using MyApp;
using MintPlayer.Spark;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddActions();                                   // Source-generated
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSpark(o => o.SynchronizeModelsIfRequested<MySparkContext>(args));

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();                                 // Map Spark REST endpoints
});

app.Run();
```

### Key extension methods

| Method | Purpose |
|---|---|
| `AddSparkFull(configuration)` | **AllFeatures**: Registers all Spark services, actions, auth, messaging in one call |
| `UseSparkFull(args)` | **AllFeatures**: Adds middleware + synchronizes models if `--spark-synchronize-model` is passed |
| `MapSparkFull()` | **AllFeatures**: Maps all Spark REST endpoints |
| `AddSpark(configuration, configure)` | Registers Spark services and connects to RavenDB with builder callback |
| `UseSpark(o => ...)` | Adds Spark middleware with options (e.g. `o.SynchronizeModelsIfRequested<T>(args)`) |
| `MapSpark()` | Maps all Spark REST endpoints (`/spark/...`) |

## Step 5: Configure RavenDB Connection

Add the RavenDB connection settings to `appsettings.json`:

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

## Step 6: Synchronize Models

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

### Model JSON Structure

A generated model JSON file looks like this (from the DemoApp):

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

### Query JSON Structure

A generated query file:

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

## Step 7: Verify the REST API

After starting the application normally (`dotnet run`), Spark exposes these endpoints:

| Endpoint | Method | Description |
|---|---|---|
| `/spark/types` | GET | List all entity type definitions |
| `/spark/types/{id}` | GET | Get a single entity type definition |
| `/spark/queries` | GET | List all query definitions |
| `/spark/queries/{id}` | GET | Get a single query definition |
| `/spark/queries/{id}/execute` | GET | Execute a query (returns paginated results) |
| `/spark/po/{entityTypeId}` | POST | Create a new entity |
| `/spark/po/{entityTypeId}/{id}` | GET | Get entity by ID |
| `/spark/po/{entityTypeId}/{id}` | PUT | Update an entity |
| `/spark/po/{entityTypeId}/{id}` | DELETE | Delete an entity |

All mutation endpoints (POST, PUT, DELETE) require an `X-XSRF-TOKEN` header. The XSRF token is automatically provided as a cookie by the `UseSpark()` middleware.

## Step 8: Angular Frontend

The Angular frontend uses the `@mintplayer/ng-spark` library which reads entity type definitions and query definitions from the API and renders list pages, detail pages, and create/edit forms automatically.

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

## Next Steps

- [Reference Attributes](guide-reference-attributes.md) -- link entities together with navigation properties
- [Queries and Sorting](guide-queries-and-sorting.md) -- define RavenDB indexes and sortable list views
- [Authorization](guide-authorization.md) -- add permission-based access control
- [Custom Attribute Renderers](guide-custom-attribute-renderers.md) -- replace default UI rendering with custom Angular components
