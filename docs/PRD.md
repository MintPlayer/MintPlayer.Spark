# Product Requirements Document: MintPlayer.Spark

**Version:** 1.0
**Date:** December 20, 2025
**Status:** Draft

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Product Vision](#3-product-vision)
4. [Goals and Objectives](#4-goals-and-objectives)
5. [Target Users](#5-target-users)
6. [Core Concepts](#6-core-concepts)
7. [Functional Requirements](#7-functional-requirements)
8. [Technical Architecture](#8-technical-architecture)
9. [API Specification](#9-api-specification)
10. [Frontend Requirements](#10-frontend-requirements)
11. [Data Model](#11-data-model)
12. [Implementation Status](#12-implementation-status)
13. [Non-Functional Requirements](#13-non-functional-requirements)
14. [Future Considerations](#14-future-considerations)

---

## 1. Executive Summary

**MintPlayer.Spark** is a low-code web application framework inspired by Vidyano. It enables developers to build data-driven web applications with minimal boilerplate code by using a unified **PersistentObject** model instead of traditional DTOs and eliminating the need for the Repository/Service/Controller pattern.

The framework consists of:
- A .NET backend library that provides generic CRUD operations via REST endpoints
- JSON-based model definitions stored in `App_Data/Model`
- An Angular frontend that dynamically renders UI based on available entity types
- RavenDB as the document database

---

## 2. Problem Statement

Traditional web application development requires significant boilerplate code:

| Traditional Approach | Lines of Code per Entity |
|---------------------|-------------------------|
| DTO classes | 20-50 |
| Repository interface | 10-20 |
| Repository implementation | 50-100 |
| Service interface | 10-20 |
| Service implementation | 50-100 |
| Controller | 50-100 |
| **Total** | **190-390** |

For an application with 20 entity types, this results in **3,800-7,800 lines** of repetitive code.

**Spark reduces this to near-zero** by using a generic PersistentObject approach where entities are defined in JSON configuration files.

---

## 3. Product Vision

> Build web applications in minutes, not months. Define your data model in JSON, and Spark handles the rest.

### Key Principles

1. **Zero DTOs**: Use `PersistentObject` as a universal data container
2. **Zero Repositories**: Generic middleware handles all CRUD operations
3. **Configuration over Code**: Entity definitions live in JSON files
4. **Dynamic UI**: Angular frontend adapts to available entity types automatically
5. **Type Safety**: Strong typing through attribute metadata and CLR type mapping

---

## 4. Goals and Objectives

### Primary Goals

| Goal | Success Metric |
|------|----------------|
| Reduce boilerplate code | 90% reduction in entity-related code |
| Accelerate development | New entity types added in < 5 minutes |
| Maintain flexibility | Support for custom business logic via hooks |
| Ensure type safety | Compile-time and runtime validation |

### Objectives

1. **Phase 1**: Core framework with CRUD operations (Current)
2. **Phase 2**: Model-driven persistence with JSON definitions
3. **Phase 3**: Full Angular UI with dynamic entity management
4. **Phase 4**: Validation rules and business logic hooks
5. **Phase 5**: Reference handling between entities

---

## 5. Target Users

### Primary Users

| User Type | Description | Key Needs |
|-----------|-------------|-----------|
| Enterprise Developers | Building internal business applications | Rapid development, maintainability |
| Startups | Need to iterate quickly on MVPs | Speed, flexibility |
| Consultants | Delivering client projects | Reusability, customization |

### User Stories

1. **As a developer**, I want to add a new entity type by creating a JSON file, so that I don't have to write boilerplate code.

2. **As a developer**, I want the Angular UI to automatically show CRUD pages for my entities, so that I have immediate functionality.

3. **As a developer**, I want to define validation rules in the model, so that data integrity is enforced consistently.

4. **As a developer**, I want to define relationships between entities, so that I can build complex data models.

---

## 6. Core Concepts

### 6.1 PersistentObject

The `PersistentObject` is the fundamental data container that replaces traditional DTOs.

```csharp
public class PersistentObject
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string ClrType { get; set; }
    public PersistentObjectAttribute[] Attributes { get; set; }
}
```

### 6.2 PersistentObjectAttribute

Each attribute represents a property of the entity with metadata.

```csharp
public class PersistentObjectAttribute
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public object? Value { get; set; }
    public string DataType { get; set; }        // string, number, decimal, boolean, datetime, guid, reference, embedded
    public bool IsRequired { get; set; }
    public string? Query { get; set; }          // SparkQuery name for reference lookups in edit mode
    public string? Breadcrumb { get; set; }     // Computed display value for references (read-only)
    public ValidationRule[] Rules { get; set; }
}
```

**Supported Data Types:**

| DataType | Description | CLR Types |
|----------|-------------|-----------|
| `string` | Text values | `string` |
| `number` | Integer values | `int`, `long` |
| `decimal` | Floating-point values | `decimal`, `double`, `float` |
| `boolean` | True/false values | `bool` |
| `datetime` | Date and time values | `DateTime`, `DateTimeOffset` |
| `guid` | Unique identifiers | `Guid` |
| `reference` | Foreign key to another entity (stored as string ID) | `string` with `[Reference]` attribute |
| `embedded` | Nested complex object stored within the parent document | Classes with an `Id` property |

### 6.3 Model Definition (JSON)

Entity types are defined in JSON files under `App_Data/Model/`:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Person",
  "clrType": "Demo.Data.Person",
  "attributes": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "name": "FirstName",
      "dataType": "string",
      "isRequired": true,
      "rules": [
        { "type": "maxLength", "value": 100 }
      ]
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "name": "LastName",
      "dataType": "string",
      "isRequired": true
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440003",
      "name": "DateOfBirth",
      "dataType": "datetime",
      "isRequired": false
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440004",
      "name": "Company",
      "dataType": "reference",
      "referenceType": "Demo.Data.Company",
      "isRequired": false
    }
  ]
}
```

### 6.4 PersistentObject Categories

Not all PersistentObjects represent top-level database collections. The framework supports several categories:

| Category | Description | Example |
|----------|-------------|---------|
| **Collection Entity** | Maps directly to a RavenDB collection via `IRavenQueryable<T>` property on SparkContext | `Person`, `Company` |
| **Embedded Object** | Stored as a property within another document (dataType: `embedded`), not in its own collection. JSON model file is auto-generated during synchronization. | `Address` on a `Person` document |
| **Virtual** | Completely unrelated to the database; used for UI-only data | Modal dialog data, toast notifications, wizard state |

**Examples:**

Embedded object definition (auto-generated, can be used on multiple parent types like `Person.Address` or `Company.Address`):
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Address",
  "clrType": "Demo.Data.Address",
  "displayAttribute": "Street",
  "attributes": [
    { "id": "...", "name": "Street", "dataType": "string", "isVisible": true, "order": 1 },
    { "id": "...", "name": "City", "dataType": "string", "isVisible": true, "order": 2 },
    { "id": "...", "name": "State", "dataType": "string", "isVisible": true, "order": 3 }
  ]
}
```

Parent entity referencing an embedded object:
```json
{
  "name": "Person",
  "attributes": [
    { "name": "FirstName", "dataType": "string" },
    { "name": "LastName", "dataType": "string" },
    { "name": "Address", "dataType": "embedded" }
  ]
}
```

Virtual object (for UI-only purposes):
```json
{
  "id": "770e8400-e29b-41d4-a716-446655440000",
  "name": "ConfirmDeleteDialog",
  "attributes": [
    { "name": "Title", "dataType": "string" },
    { "name": "Message", "dataType": "string" },
    { "name": "ConfirmButtonText", "dataType": "string" }
  ]
}
```

### 6.5 DbContext Concept

While not a traditional EF DbContext, Spark uses a registry pattern to track available entity types:

```csharp
public class SparkContext
{
    public IRavenQueryable<Person> People { get; set; }
    public IRavenQueryable<Company> Companies { get; set; }
    // Add a property for each collection in your RavenDB database
}
```

This pattern provides:
- **Discoverability**: The framework can reflect over the context to find all available collections
- **Type Safety**: Strong typing for each entity collection
- **Familiar Pattern**: Similar to EF Core's DbContext with DbSet<T> properties
- **Functional Queryables**: Properties are injected with actual `IRavenQueryable` instances from `IDocumentSession` for direct querying

### 6.6 Spark Queries

A **Spark Query** defines how to retrieve a list of entities. It references a property on the `SparkContext` to determine which collection to query.

**Definition** (`App_Data/Queries/{QueryName}.json`):
```json
{
  "id": "880e8400-e29b-41d4-a716-446655440000",
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "LastName",
  "sortDirection": "asc"
}
```

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440001",
  "name": "GetCompanies",
  "contextProperty": "Companies",
  "sortBy": "Name",
  "sortDirection": "asc"
}
```

Spark Queries are used:
- By Program Units to determine what data to show in list views
- By reference attributes (`Query` property) to populate lookup/autocomplete options

### 6.7 Program Units

**Program Units** define the navigation structure of the application. They are configured in `App_Data/programUnits.json`.

**Structure:**
```json
{
  "programUnitGroups": [
    {
      "id": "990e8400-e29b-41d4-a716-446655440000",
      "name": "Master Data",
      "icon": "bi-database",
      "order": 1,
      "programUnits": [
        {
          "id": "990e8400-e29b-41d4-a716-446655440001",
          "name": "People",
          "icon": "bi-people",
          "type": "query",
          "queryId": "880e8400-e29b-41d4-a716-446655440000",
          "order": 1
        },
        {
          "id": "990e8400-e29b-41d4-a716-446655440002",
          "name": "Companies",
          "icon": "bi-building",
          "type": "query",
          "queryId": "880e8400-e29b-41d4-a716-446655440001",
          "order": 2
        }
      ]
    },
    {
      "id": "990e8400-e29b-41d4-a716-446655440010",
      "name": "Settings",
      "icon": "bi-gear",
      "order": 2,
      "programUnits": [
        {
          "id": "990e8400-e29b-41d4-a716-446655440011",
          "name": "Application Settings",
          "icon": "bi-sliders",
          "type": "persistentObject",
          "persistentObjectId": "abc12345-...",
          "order": 1
        }
      ]
    }
  ]
}
```

**Program Unit Types:**

| Type | Description |
|------|-------------|
| `query` | References a Spark Query; navigates to a list view showing query results |
| `persistentObject` | References a specific PersistentObject by ID; navigates directly to that object's detail/edit view |

The Angular frontend reads this configuration to build the sidebar navigation with accordion groups.

### 6.8 Actions Classes

**Actions Classes** provide a customization mechanism for entity-specific behavior. They follow an inheritance chain that allows overriding default CRUD logic.

**Inheritance Chain:**
```
MintPlayer.Spark.DefaultPersistentObjectActions (library base)
    └── DemoApp.DefaultPersistentObjectActions (application default)
            └── DemoApp.Actions.PersonActions (entity-specific)
            └── DemoApp.Actions.CompanyActions (entity-specific)
```

**Library Base Class** (`MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs`):
```csharp
public class DefaultPersistentObjectActions<T> where T : class
{
    protected IDocumentSession Session { get; }

    public virtual async Task<IEnumerable<T>> OnQuery()
        => await Session.Query<T>().ToListAsync();

    public virtual async Task<T?> OnLoad(string id)
        => await Session.LoadAsync<T>(id);

    public virtual async Task<T> OnSave(T entity)
    {
        await Session.StoreAsync(entity);
        await Session.SaveChangesAsync();
        return entity;
    }

    public virtual async Task OnDelete(string id)
    {
        Session.Delete(id);
        await Session.SaveChangesAsync();
    }

    public virtual Task OnBeforeSave(T entity) => Task.CompletedTask;
    public virtual Task OnAfterSave(T entity) => Task.CompletedTask;
    public virtual Task OnBeforeDelete(T entity) => Task.CompletedTask;
}
```

**Application Default** (`DemoApp/Actions/DefaultPersistentObjectActions.cs`):
```csharp
public class DefaultPersistentObjectActions<T> : Spark.DefaultPersistentObjectActions<T>
    where T : class
{
    // Application-wide customizations (logging, auditing, etc.)
}
```

**Entity-Specific Actions** (`DemoApp/Actions/PersonActions.cs`):
```csharp
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    public override async Task OnBeforeSave(Person entity)
    {
        // Custom validation or business logic for Person
        if (string.IsNullOrEmpty(entity.Email))
            throw new ValidationException("Email is required");
    }
}
```

**Registration:**
Actions classes are discovered via naming convention (`{TypeName}Actions`) or explicit registration.

---

## 7. Functional Requirements

### 7.1 Backend Requirements

#### FR-BE-001: Model Synchronization
- **Description**: System shall synchronize entity definitions between `SparkContext` and `App_Data/Model/*.json` when the application is started with the `--spark-synchronize-model` command-line parameter in Development mode
- **Priority**: High
- **Status**: Implemented

**Synchronization Process:**
1. Reflect over `SparkContext` properties to find all `IRavenQueryable<T>` collections
2. For each entity type `T`, generate or update the corresponding JSON model file. Do not remove any attributes. Only add/update attributes.
3. **Embedded Type Discovery**: For each property on an entity that is a complex type (a class with an `Id` property), the synchronizer:
   - Sets the attribute's `dataType` to `"embedded"`
   - Queues the embedded type for processing
   - Generates a separate JSON model file for the embedded type (e.g., `Address.json`)
   - Recursively discovers nested embedded types within embedded types
4. JSON files remain static during normal runtime (no runtime discovery)
5. Developers explicitly trigger synchronization during development

**Complex Type Detection:**
A type is considered a complex/embedded type if:
- It is a class (not a value type, enum, or primitive)
- It is not `string`
- It has a public `Id` property with both getter and setter

#### FR-BE-002: CRUD Endpoints
- **Description**: System shall expose REST endpoints for Create, Read, Update, Delete operations
- **Priority**: High
- **Status**: Implemented

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/po/{type}` | GET | List all objects of type |
| `/spark/po/{type}/{id}` | GET | Get single object by ID |
| `/spark/po/{type}` | POST | Create new object |
| `/spark/po/{type}/{id}` | PUT | Update existing object |
| `/spark/po/{type}/{id}` | DELETE | Delete object |

#### FR-BE-003: Entity Type Registry
- **Description**: System shall provide an API to query available entity types
- **Priority**: High
- **Status**: Not Implemented

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/types` | GET | List all registered entity types |
| `/spark/types/{id}` | GET | Get entity type definition by ID |

#### FR-BE-004: Attribute Mapping
- **Description**: System shall provide extension methods for bidirectional mapping between entities and PersistentObjects
- **Priority**: High
- **Status**: Not Implemented

```csharp
// Map entity properties to PersistentObject attributes
public static void PopulateAttributeValues<T>(this PersistentObject po, T entity);

// Map PersistentObject attributes to entity properties
public static void PopulateObjectValues<T>(this PersistentObject po, T entity);
```

#### FR-BE-005: Validation
- **Description**: System shall validate PersistentObject data against model rules before persistence
- **Priority**: Medium
- **Status**: Not Implemented

Supported validation rules:
- `required`: Field must have a value
- `maxLength`: Maximum string length
- `minLength`: Minimum string length
- `range`: Numeric value range
- `regex`: Pattern matching
- `email`: Email format validation

#### FR-BE-006: Reference Handling
- **Description**: System shall support references between entity types
- **Priority**: Medium
- **Status**: Not Implemented

**RavenDB Reference Constraint:**
Reference properties on entity classes **must be of type `string`** (RavenDB restriction). The `[Reference]` attribute indicates the expected target type:

```csharp
public class Person
{
    public Guid Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }

    [Reference(typeof(Company))]
    public string Company { get; set; }  // Stores the RavenDB document ID
}
```

**Reference Behavior:**
- The middleware shall call RavenDB's `.Include<T>()` method to eager-load referenced documents
- The `Breadcrumb` property on the attribute contains the computed display value (resolved on backend)
- In edit mode, the `Query` property specifies which SparkQuery to use for lookup/autocomplete
- Frontend does not need to know the reference target type; it only displays Breadcrumb and uses Query for selection

#### FR-BE-007: Spark Queries
- **Description**: System shall support Spark Query definitions that specify which SparkContext property to use for retrieving entity lists
- **Priority**: High
- **Status**: Not Implemented

**Query Definition Location:** `App_Data/Queries/*.json`

**Endpoints:**
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/queries` | GET | List all registered Spark Queries |
| `/spark/queries/{id}` | GET | Get query definition by ID |
| `/spark/queries/{id}/execute` | GET | Execute query and return results as PersistentObjects |

#### FR-BE-008: Program Units
- **Description**: System shall support Program Unit configuration for defining application navigation structure
- **Priority**: High
- **Status**: Not Implemented

**Configuration Location:** `App_Data/programUnits.json`

**Endpoints:**
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/program-units` | GET | Return the full program units configuration (groups + units) |

#### FR-BE-009: Actions Classes
- **Description**: System shall support customizable Actions classes for entity-specific business logic
- **Priority**: Medium
- **Status**: Not Implemented

**Discovery Mechanism:**
1. Look for `{TypeName}Actions` class in application assembly
2. Fall back to application's `DefaultPersistentObjectActions<T>`
3. Fall back to library's `DefaultPersistentObjectActions<T>`

**Lifecycle Hooks:**
- `OnQuery()` - Called when listing entities
- `OnLoad(id)` - Called when loading a single entity
- `OnSave(entity)` - Called when creating/updating
- `OnDelete(id)` - Called when deleting
- `OnBeforeSave(entity)` - Hook before save
- `OnAfterSave(entity)` - Hook after save
- `OnBeforeDelete(entity)` - Hook before delete

### 6.9 RavenDB Indexes

RavenDB indexes allow projecting entity data into optimized query views. Spark supports custom indexes using RavenDB's `AbstractIndexCreationTask`.

**Index Definition:**
```csharp
public class People_Overview : AbstractIndexCreationTask<Data.Person>
{
    public People_Overview() {
        Map = people => from person in people
                        select new Data.VPerson
                        {
                            Name = person.FirstName + ' ' + person.LastName,
                            Age = person.Age,
                            City = person.City
                        };
    }
}
```

**Entity Class with QueryType Attribute:**
```csharp
[QueryType(typeof(Data.VPerson))]
public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Age { get; set; }
    public string City { get; set; }
}
```

**Projection Class (View Model):**
```csharp
public class VPerson
{
    public string FullName { get; set; }
    public int Age { get; set; }
    public string City { get; set; }
}
```

**QueryType Attribute:**

The `[QueryType]` attribute is used to specify the type returned by the RavenDB index. This enables the framework to:
- Know which projection type to use when querying via the index
- Map index results to the appropriate view model
- Support different shapes for stored documents vs. queried results

### 7.2 Frontend Requirements

#### FR-FE-001: Shell Layout
- **Description**: Application shall use `@mintplayer/ng-bootstrap` BsShellComponent
- **Priority**: High
- **Status**: Not Implemented

#### FR-FE-002: Dynamic Sidebar (Program Units)
- **Description**: Sidebar shall display navigation based on Program Units configuration
- **Priority**: High
- **Status**: Not Implemented

Requirements:
- Fetch program units from `/spark/program-units` on initialization
- Display Program Unit Groups as accordion sections
- Each group contains Program Units as navigation links
- Program Unit with `type: "query"` → navigates to list page for that query
- Program Unit with `type: "persistentObject"` → navigates directly to that object's detail view
- Display icons from the configuration (Bootstrap Icons)

#### FR-FE-003: Entity List Page
- **Description**: Display paginated list of entities for a given type using `BsDatatableComponent` from `@mintplayer/ng-bootstrap`
- **Priority**: High
- **Status**: Not Implemented

Features:
- Use `BsDatatableComponent` for the data grid
- Columns generated dynamically based on entity attributes
- Built-in search/filter functionality
- Sorting by column
- Pagination
- "New" button to create entity
- Row click navigates to detail/edit page

#### FR-FE-004: Entity Detail Page
- **Description**: Display single entity with all attributes
- **Priority**: High
- **Status**: Not Implemented

Features:
- Read-only view of entity
- Edit button to switch to edit mode
- Delete button with confirmation
- Back navigation

#### FR-FE-005: Entity Create Page
- **Description**: Form to create new entity
- **Priority**: High
- **Status**: Not Implemented

Features:
- Dynamic form generation based on entity type definition
- Appropriate input controls per data type
- Validation based on attribute rules
- Reference field with lookup/search
- Save and Cancel buttons

#### FR-FE-006: Entity Edit Page
- **Description**: Form to edit existing entity
- **Priority**: High
- **Status**: Not Implemented

Features:
- Pre-populated form with current values
- Same functionality as create page
- Update confirmation

---

## 8. Technical Architecture

### 8.1 System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Angular Frontend                         │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐    │
│  │  Shell   │  │   List   │  │  Detail  │  │  Create  │    │
│  │Component │  │   Page   │  │   Page   │  │   Page   │    │
│  └──────────┘  └──────────┘  └──────────┘  └──────────┘    │
│                         │                                    │
│                    SparkService                              │
└─────────────────────────┼───────────────────────────────────┘
                          │ HTTP/REST
┌─────────────────────────┼───────────────────────────────────┐
│                    ASP.NET Core                              │
│  ┌──────────────────────┴───────────────────────────────┐   │
│  │                  SparkMiddleware                      │   │
│  └──────────────────────┬───────────────────────────────┘   │
│                         │                                    │
│  ┌──────────────────────┴───────────────────────────────┐   │
│  │                 Endpoint Handlers                     │   │
│  │  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐        │   │
│  │  │  List  │ │  Get   │ │ Create │ │ Update │ Delete │   │
│  │  └────────┘ └────────┘ └────────┘ └────────┘        │   │
│  └──────────────────────┬───────────────────────────────┘   │
│                         │                                    │
│  ┌──────────────────────┴───────────────────────────────┐   │
│  │                 IDatabaseAccess                       │   │
│  │            (RavenDB Implementation)                   │   │
│  └──────────────────────┬───────────────────────────────┘   │
└─────────────────────────┼───────────────────────────────────┘
                          │
┌─────────────────────────┼───────────────────────────────────┐
│                      RavenDB                                 │
│  ┌──────────────────────┴───────────────────────────────┐   │
│  │              Document Collections                     │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 8.2 Technology Stack

| Layer | Technology | Version |
|-------|------------|---------|
| Frontend | Angular | 21.0.0 |
| UI Components | @mintplayer/ng-bootstrap | Latest |
| Backend | ASP.NET Core | .NET 10.0 |
| Database | RavenDB | 6.2.6 |
| DI Generation | MintPlayer.SourceGenerators | 5.2.2 |

### 8.3 Project Structure

```
MintPlayer.Spark/
├── MintPlayer.Spark.Abstractions/
│   ├── PersistentObject.cs
│   ├── PersistentObjectAttribute.cs
│   ├── IDatabaseAccess.cs
│   └── ISparkContext.cs
│
├── MintPlayer.Spark/
│   ├── Configuration/
│   │   └── SparkOptions.cs
│   ├── Endpoints/
│   │   └── PersistentObject/
│   │       ├── List.cs
│   │       ├── Get.cs
│   │       ├── Create.cs
│   │       ├── Update.cs
│   │       └── Delete.cs
│   ├── Services/
│   │   ├── DatabaseAccess.cs
│   │   ├── ModelLoader.cs
│   │   └── SparkContext.cs
│   ├── Extensions/
│   │   └── PersistentObjectExtensions.cs
│   └── SparkMiddleware.cs
│
└── Demo/DemoApp/
    ├── App_Data/
    │   └── Model/
    │       ├── Person.json
    │       └── Company.json
    ├── Data/
    │   ├── Person.cs
    │   └── Company.cs
    ├── ClientApp/
    │   └── src/
    │       ├── app/
    │       │   ├── core/
    │       │   │   ├── services/
    │       │   │   │   └── spark.service.ts
    │       │   │   └── models/
    │       │   │       ├── persistent-object.ts
    │       │   │       └── entity-type.ts
    │       │   ├── pages/
    │       │   │   ├── entity-list/
    │       │   │   ├── entity-detail/
    │       │   │   ├── entity-create/
    │       │   │   └── entity-edit/
    │       │   ├── app.ts
    │       │   └── app.routes.ts
    │       └── ...
    └── Program.cs
```

### 8.4 Dependency Injection with Source Generators

The framework uses [MintPlayer.SourceGenerators](https://github.com/MintPlayer/MintPlayer.Dotnet.Tools/tree/master/SourceGenerators/SourceGenerators) for compile-time dependency injection registration. This eliminates manual service registration boilerplate.

**Registering Services:**

Use the `[Register]` attribute to automatically register services with the DI container:

```csharp
[Register(ServiceLifetime.Scoped)]
internal class DatabaseAccess : IDatabaseAccess
{
    // Implementation
}
```

**Injecting Dependencies:**

Use the `[Inject]` attribute on partial classes to generate constructor injection:

```csharp
[Inject]
internal partial class SparkMiddleware
{
    private readonly IDatabaseAccess databaseAccess;
    private readonly ISparkContext sparkContext;
}
```

The source generator creates the constructor automatically:

```csharp
// Generated code
internal partial class SparkMiddleware
{
    public SparkMiddleware(IDatabaseAccess databaseAccess, ISparkContext sparkContext)
    {
        this.databaseAccess = databaseAccess;
        this.sparkContext = sparkContext;
    }
}
```

**Benefits:**
- No manual `services.AddScoped<>()` calls needed
- Compile-time validation of dependencies
- Reduced boilerplate code
- Consistent registration patterns across the framework and applications

**NuGet Packages:**
- `MintPlayer.SourceGenerators` - The source generator
- `MintPlayer.SourceGenerators.Attributes` - Contains `[Register]` and `[Inject]` attributes

---

## 9. API Specification

### 9.1 Entity Type Endpoints

#### GET /spark/types

Returns all registered entity types.

**Response:**
```json
{
  "entityTypes": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "name": "Person",
      "clrType": "Demo.Data.Person",
      "attributes": [...]
    }
  ]
}
```

#### GET /spark/types/{id}

Returns a single entity type definition.

**Parameters:**
- `id` (path, Guid): Entity type ID

**Response:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Person",
  "clrType": "Demo.Data.Person",
  "attributes": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "name": "FirstName",
      "dataType": "string",
      "isRequired": true,
      "rules": []
    }
  ]
}
```

### 9.2 PersistentObject Endpoints

#### GET /spark/po/{type}

List all objects of a given type.

**Parameters:**
- `type` (path, string): CLR type name or entity type GUID

**Response:**
```json
[
  {
    "id": "123e4567-e89b-12d3-a456-426614174000",
    "name": "John Doe",
    "clrType": "Demo.Data.Person",
    "attributes": [
      { "id": "...", "name": "FirstName", "value": "John" },
      { "id": "...", "name": "LastName", "value": "Doe" }
    ]
  }
]
```

#### GET /spark/po/{type}/{id}

Get a single object by ID.

**Parameters:**
- `type` (path, string): CLR type name or entity type GUID
- `id` (path, Guid): Object ID

**Response:**
```json
{
  "id": "123e4567-e89b-12d3-a456-426614174000",
  "name": "John Doe",
  "clrType": "Demo.Data.Person",
  "attributes": [...]
}
```

#### POST /spark/po/{type}

Create a new object.

**Parameters:**
- `type` (path, string): CLR type name or entity type GUID

**Request Body:**
```json
{
  "attributes": [
    { "name": "FirstName", "value": "John" },
    { "name": "LastName", "value": "Doe" }
  ]
}
```

**Response:** 201 Created with created object

#### PUT /spark/po/{type}/{id}

Update an existing object.

**Parameters:**
- `type` (path, string): CLR type name or entity type GUID
- `id` (path, Guid): Object ID

**Request Body:**
```json
{
  "attributes": [
    { "name": "FirstName", "value": "Jane" },
    { "name": "LastName", "value": "Doe" }
  ]
}
```

**Response:** 200 OK with updated object

#### DELETE /spark/po/{type}/{id}

Delete an object.

**Parameters:**
- `type` (path, string): CLR type name or entity type GUID
- `id` (path, Guid): Object ID

**Response:** 204 No Content

---

## 10. Frontend Requirements

### 10.1 Angular Application Structure

#### Routes

```typescript
export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'entity/:typeId', loadComponent: () => import('./pages/entity-list/entity-list.component') },
      { path: 'entity/:typeId/new', loadComponent: () => import('./pages/entity-create/entity-create.component') },
      { path: 'entity/:typeId/:id', loadComponent: () => import('./pages/entity-detail/entity-detail.component') },
      { path: 'entity/:typeId/:id/edit', loadComponent: () => import('./pages/entity-edit/entity-edit.component') }
    ]
  }
];
```

### 10.2 Core Services

#### SparkService

```typescript
@Injectable({ providedIn: 'root' })
export class SparkService {
  private baseUrl = '/spark';

  // Entity Types
  getEntityTypes(): Observable<EntityType[]>;
  getEntityType(id: string): Observable<EntityType>;

  // PersistentObjects
  list(typeId: string): Observable<PersistentObject[]>;
  get(typeId: string, id: string): Observable<PersistentObject>;
  create(typeId: string, data: Partial<PersistentObject>): Observable<PersistentObject>;
  update(typeId: string, id: string, data: Partial<PersistentObject>): Observable<PersistentObject>;
  delete(typeId: string, id: string): Observable<void>;
}
```

### 10.3 UI Components

#### Shell Component (using @mintplayer/ng-bootstrap)

```typescript
@Component({
  selector: 'app-shell',
  template: `
    <bs-shell
      [navItems]="navItems$ | async"
      [sidebarMode]="'accordion'">
      <router-outlet />
    </bs-shell>
  `
})
export class ShellComponent {
  navItems$ = this.sparkService.getEntityTypes().pipe(
    map(types => types.map(t => ({
      title: t.name,
      icon: 'fa-database',
      routerLink: ['/entity', t.id]
    })))
  );
}
```

#### Dynamic Form Generation

The create/edit pages shall dynamically generate form controls based on attribute definitions:

| Data Type | Angular Control |
|-----------|-----------------|
| string | `<input type="text">` |
| number | `<input type="number">` |
| decimal | `<input type="number" step="0.01">` |
| boolean | `<input type="checkbox">` |
| datetime | `<input type="datetime-local">` |
| reference | `<select>` or autocomplete |

---

## 11. Data Model

### 11.1 Entity Definitions (App_Data/Model)

#### Person.json
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Person",
  "clrType": "Demo.Data.Person",
  "displayAttribute": "FullName",
  "attributes": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "name": "FirstName",
      "label": "First Name",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 1,
      "rules": [
        { "type": "maxLength", "value": 100 }
      ]
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440002",
      "name": "LastName",
      "label": "Last Name",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 2,
      "rules": [
        { "type": "maxLength", "value": 100 }
      ]
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440003",
      "name": "Email",
      "label": "Email Address",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 3,
      "rules": [
        { "type": "email" },
        { "type": "maxLength", "value": 255 }
      ]
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440004",
      "name": "DateOfBirth",
      "label": "Date of Birth",
      "dataType": "datetime",
      "isRequired": false,
      "isVisible": true,
      "isReadOnly": false,
      "order": 4
    },
    {
      "id": "550e8400-e29b-41d4-a716-446655440005",
      "name": "Company",
      "label": "Company",
      "dataType": "reference",
      "query": "GetCompanies",
      "isRequired": false,
      "isVisible": true,
      "isReadOnly": false,
      "order": 5
    }
  ]
}
```

#### Company.json
```json
{
  "id": "660e8400-e29b-41d4-a716-446655440000",
  "name": "Company",
  "clrType": "Demo.Data.Company",
  "displayAttribute": "Name",
  "attributes": [
    {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "name": "Name",
      "label": "Company Name",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 1,
      "rules": [
        { "type": "maxLength", "value": 200 }
      ]
    },
    {
      "id": "660e8400-e29b-41d4-a716-446655440002",
      "name": "Website",
      "label": "Website",
      "dataType": "string",
      "isRequired": false,
      "isVisible": true,
      "isReadOnly": false,
      "order": 2,
      "rules": [
        { "type": "url" }
      ]
    },
    {
      "id": "660e8400-e29b-41d4-a716-446655440003",
      "name": "EmployeeCount",
      "label": "Number of Employees",
      "dataType": "number",
      "isRequired": false,
      "isVisible": true,
      "isReadOnly": false,
      "order": 3,
      "rules": [
        { "type": "range", "min": 1, "max": 1000000 }
      ]
    }
  ]
}
```

### 11.2 Data Entity Classes

#### Person.cs
```csharp
namespace Demo.Data;

public class Person
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public Guid? CompanyId { get; set; }

    // Computed property for display
    public string FullName => $"{FirstName} {LastName}";
}
```

#### Company.cs
```csharp
namespace Demo.Data;

public class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

---

## 12. Implementation Status

### 12.1 Current State

| Component | Status | Notes |
|-----------|--------|-------|
| Solution Structure | Complete | 3 projects configured |
| PersistentObject Model | Complete | Basic implementation |
| PersistentObjectAttribute | Partial | Needs rules, dataType |
| IDatabaseAccess | Complete | RavenDB implementation |
| CRUD Endpoints | Complete | All 5 operations |
| SparkMiddleware | Complete | Pre/post processing |
| AddSpark/UseSpark/MapSpark | Complete | Extension methods |
| Model Synchronization | Complete | FR-BE-001, includes embedded type discovery |
| ISparkContext | Not Started | FR-BE-003 |
| PopulateAttributeValues | Not Started | FR-BE-004 |
| PopulateObjectValues | Not Started | FR-BE-004 |
| Validation | Not Started | FR-BE-005 |
| Reference Handling | Not Started | FR-BE-006 |
| Angular Shell | Not Started | FR-FE-001 |
| Dynamic Sidebar | Not Started | FR-FE-002 |
| Entity List Page | Not Started | FR-FE-003 |
| Entity Detail Page | Not Started | FR-FE-004 |
| Entity Create Page | Not Started | FR-FE-005 |
| Entity Edit Page | Not Started | FR-FE-006 |

### 12.2 Implementation Roadmap

```
Phase 1: Core Framework (Current)
├── [x] Solution structure
├── [x] PersistentObject abstractions
├── [x] RavenDB integration
├── [x] CRUD endpoints
└── [x] Middleware setup

Phase 2: Model-Driven Persistence
├── [x] Enhanced PersistentObjectAttribute with metadata
├── [x] JSON model file synchronization (App_Data/Model)
├── [x] Embedded type discovery and generation
├── [ ] ISparkContext implementation
├── [ ] Entity type API endpoints
├── [ ] PopulateAttributeValues extension
└── [ ] PopulateObjectValues extension

Phase 3: Validation & Rules
├── [ ] Validation rule definitions
├── [ ] Server-side validation
├── [ ] Validation error responses
└── [ ] Reference validation

Phase 4: Angular Frontend
├── [ ] @mintplayer/ng-bootstrap integration
├── [ ] SparkService implementation
├── [ ] Shell component with sidebar
├── [ ] Entity list page
├── [ ] Entity detail page
├── [ ] Entity create page
├── [ ] Entity edit page
└── [ ] Dynamic form generation

Phase 5: Advanced Features
├── [ ] Search and filtering
├── [ ] Sorting and pagination
├── [ ] Reference field lookups
├── [ ] Computed/derived attributes
└── [ ] Custom actions/hooks
```

---

## 13. Non-Functional Requirements

### 13.1 Performance

| Requirement | Target |
|-------------|--------|
| API response time (list) | < 200ms for 1000 records |
| API response time (single) | < 50ms |
| Page load time | < 2 seconds |
| Concurrent users | 100+ |

### 13.2 Security

- All API endpoints require authentication (configurable)
- Input validation on all user-provided data
- Protection against common attacks (XSS, CSRF, injection)
- Secure RavenDB connection (TLS)

### 13.3 Scalability

- Stateless API design for horizontal scaling
- RavenDB clustering support
- CDN-friendly static assets

### 13.4 Maintainability

- Clean separation of concerns
- Comprehensive logging
- Configuration via appsettings.json
- Docker support

### 13.5 Compatibility

| Platform | Requirement |
|----------|-------------|
| .NET Runtime | 10.0+ |
| Node.js | 20+ |
| Browsers | Chrome, Firefox, Safari, Edge (latest 2 versions) |
| RavenDB | 6.0+ |

---

## 14. Future Considerations

### 14.1 Potential Enhancements

1. **Query Builder**: Visual query builder for complex filters
2. **Bulk Operations**: Import/export CSV/Excel
3. **Audit Trail**: Track changes to entities
4. **Versioning**: Entity version history
5. **Workflows**: State machine for entity lifecycle
6. **Notifications**: Real-time updates via SignalR
7. **Multi-tenancy**: Tenant isolation
8. **Localization**: Multi-language support for labels
9. **Custom Views**: Saved list configurations
10. **Dashboard**: Charts and metrics

### 14.2 Integration Points

- **Authentication**: Support for OAuth2/OIDC, Azure AD
- **Storage**: File attachment support (Azure Blob, S3)
- **Email**: Notification templates
- **Export**: PDF generation for reports

---

## Appendix A: Glossary

| Term | Definition |
|------|------------|
| PersistentObject | Universal data container replacing traditional DTOs |
| Attribute | A named property with value and metadata |
| Entity Type | A definition of a data structure (stored as JSON) |
| ClrType | The .NET type name used for entity identification |
| Reference | A relationship between two entity types |

## Appendix B: Related Documents

- [Vidyano Documentation](https://vidyano.com/docs) (inspiration)
- [RavenDB Documentation](https://ravendb.net/docs)
- [@mintplayer/ng-bootstrap](https://github.com/MintPlayer/mintplayer-ng-bootstrap)

---

*This document is a living specification and will be updated as the project evolves.*
