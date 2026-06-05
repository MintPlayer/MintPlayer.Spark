# PRD: Custom Queries for MintPlayer.Spark

**Issue:** [#37 — Custom Queries](https://github.com/MintPlayer/MintPlayer.Spark/issues/37)

## 1. Overview

Custom Queries allow developers to define query data sources as C# methods rather than simple RavenDB collection/index references. Currently, a `SparkQuery` must point to a property on the `SparkContext` (e.g., `Session.Query<Car>()`), which limits queries to straightforward collection scans with optional index projections.

Custom Queries introduce an alternative source type (`Custom.MethodName`) that routes to a developer-defined method, enabling:
- **Filtered detail queries** — e.g., "People for this Company" (parent-child relationships)
- **Cross-entity aggregations** — joining data from multiple collections
- **External data sources** — calling external APIs or services
- **Computed results** — in-memory transformations, empty sentinels, etc.

### Inspiration

Directly inspired by Vidyano's `Custom.MethodName` pattern used extensively in Fleet (492 custom query references) and HR. Adapted to Spark's conventions:
- **No dual location** — methods live on the Actions class only (not on SparkContext, which stays a pure query-property container)
- **`IQueryable<T>` return type** — framework applies sorting uniformly; in-memory results use `.AsQueryable()`
- **Same `[Inject]` DI pattern** — no context constructor, uses `[Inject]` attribute
- **Same authorization model** — reuses `security.json` and `IPermissionService`

## 2. Goals

1. Replace the existing `ContextProperty` on `SparkQuery` with a unified `Source` property that distinguishes between database queries (`Database.PropertyName`) and custom queries (`Custom.MethodName`)
2. Enable developers to define custom query methods on Actions classes with a `CustomQueryArgs` parameter
3. Support parent context (for detail/sub-queries that filter by a parent object)
4. Auto-discover custom query methods at startup (cached reflection, no source generator needed)
5. Integrate with existing authorization, sorting, and entity mapping pipeline

## 3. Non-Goals (Out of Scope for v1)

- Server-side filtering/pagination (existing limitation, separate feature)
- Custom query parameters beyond parent context (e.g., search terms, date ranges)
- Source generator for custom query discovery (reflection + caching is sufficient given the existing pattern)
- Client-side Angular changes for passing parent context (deferred — v1 focuses on backend)

## 4. Architecture

### 4.1 SparkQuery Model Changes

The `SparkQuery` model replaces the existing `ContextProperty` with a unified `Source` property that supports both database and custom query sources.

**Updated `SparkQuery`** in `MintPlayer.Spark.Abstractions/SparkQuery.cs`:

```csharp
public sealed class SparkQuery
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Description { get; set; }

    /// <summary>
    /// Query data source. Two formats supported:
    /// - "Database.PropertyName" — resolves to an IRavenQueryable property on SparkContext
    /// - "Custom.MethodName" — resolves to a method on the entity's Actions class
    /// </summary>
    public required string Source { get; set; }

    public string? Alias { get; set; }
    public string? SortBy { get; set; }
    public string SortDirection { get; set; } = "asc";
    public string? IndexName { get; set; }
    public bool UseProjection { get; set; }

    /// <summary>
    /// The entity/view-model type name this query returns (e.g., "Person", "CompanyProductsOverview").
    /// Optional. When set, the framework uses the corresponding EntityTypeDefinition from
    /// App_Data/Model/ to map results via IEntityMapper. When not set, the type is inferred:
    /// - For Database queries: from the IRavenQueryable&lt;T&gt; generic parameter
    /// - For Custom queries: from the method return type's generic parameter
    /// The type does not need to correspond to a RavenDB collection — view-model types
    /// that only exist as model definitions (no ContextProperty) are fully supported.
    /// </summary>
    public string? EntityType { get; set; }
}
```

### 4.2 Source Resolution Logic

The `Source` property is parsed at execution time using `GeneratedRegex`:

| Source value | Resolution |
|---|---|
| `"Database.Cars"` | Resolves `SparkContext.Cars` property |
| `"Custom.Company_People"` | Resolves `PersonActions.Company_People(CustomQueryArgs)` method |

### 4.3 CustomQueryArgs

Located in `MintPlayer.Spark.Abstractions/Queries/CustomQueryArgs.cs`:

```csharp
namespace MintPlayer.Spark.Abstractions.Queries;

/// <summary>
/// Context passed to a custom query method when executed.
/// </summary>
public sealed class CustomQueryArgs
{
    /// <summary>
    /// The parent PersistentObject (for detail/sub-queries).
    /// Null for top-level queries.
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// The SparkQuery being executed (for conditional behavior based on query metadata).
    /// </summary>
    public required SparkQuery Query { get; set; }

    /// <summary>
    /// The RavenDB async document session for database access.
    /// </summary>
    public required IAsyncDocumentSession Session { get; set; }

    /// <summary>
    /// Validates that a parent is present and of the expected type.
    /// Throws InvalidOperationException if the parent is missing or wrong type.
    /// </summary>
    public void EnsureParent(string expectedTypeName)
    {
        if (Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' requires a parent object.");
        if (!string.Equals(Parent.Type, expectedTypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' expects parent of type '{expectedTypeName}', got '{Parent.Type}'.");
    }

    /// <summary>
    /// Validates that a parent is present and one of the expected types.
    /// </summary>
    public void EnsureParent(params string[] expectedTypeNames)
    {
        if (Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' requires a parent object.");
        if (!expectedTypeNames.Any(t => string.Equals(Parent.Type, t, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' expects parent of type [{string.Join(", ", expectedTypeNames)}], got '{Parent.Type}'.");
    }
}
```

### 4.4 Custom Query Method Conventions

Custom query methods are defined on **Actions classes** (e.g., `PersonActions`, `CarActions`). The framework discovers them by method name matching.

**Method signature:**

Custom query methods always return `IQueryable<T>`. The framework applies sorting, materialization, and entity mapping on top.

```csharp
// Standard (recommended — framework applies sorting/paging)
public IQueryable<TQueryEntity> MethodName(CustomQueryArgs args)

// RavenDB-specific (equivalent, IRavenQueryable<T> implements IQueryable<T>)
public IRavenQueryable<TQueryEntity> MethodName(CustomQueryArgs args)

// Zero-arg (for simple queries that don't need parent context)
public IQueryable<TQueryEntity> MethodName()
```

**Return type:** `IQueryable<T>` (or `IRavenQueryable<T>`). The element type `T` must match the query's `EntityType` — this can be a RavenDB entity type (e.g., `Person`), a projection/index type (e.g., `VPerson`), or a view-model type with no backing collection (e.g., `CompanyProductsOverview`). In all cases, a corresponding `EntityTypeDefinition` must exist in `App_Data/Model/` so the framework can map results to `PersistentObject[]`.

For in-memory results, use `.AsQueryable()`:
```csharp
public IQueryable<CompanyProductsOverview> CompanyUser_Products(CustomQueryArgs args)
{
    args.EnsureParent("CompanyUser");
    var results = BuildProductsList(args.Parent!.Id!);
    return results.AsQueryable();
}
```

**Discovery rules:**
1. Parse `Source = "Custom.MethodName"` → extract method name (using `GeneratedRegex`)
2. Determine entity type from `EntityType` field on the query, or infer from the method return type's generic parameter
3. Resolve the Actions class via `IActionsResolver` for that entity type
4. Find a public method named `MethodName` on the resolved Actions class
5. Method must return `IQueryable<T>` (or `IRavenQueryable<T>`)
6. Method may accept zero parameters or one `CustomQueryArgs` parameter

**Naming convention:** Typically `{ParentType}_{ChildType}` for detail queries (e.g., `Company_People`, `Person_Contracts`), but any name is valid.

### 4.5 Custom Query Method Resolution & Caching

Located in `MintPlayer.Spark/Services/CustomQueryMethodResolver.cs`:

```csharp
public interface ICustomQueryMethodResolver
{
    /// <summary>
    /// Resolves a custom query method on the given actions instance.
    /// Returns a delegate that, when invoked, returns the query results.
    /// </summary>
    CustomQueryMethod? Resolve(object actionsInstance, string methodName);
}

public sealed class CustomQueryMethod
{
    /// <summary>The element type T from IQueryable&lt;T&gt;.</summary>
    public required Type ResultElementType { get; init; }

    /// <summary>Whether the method accepts a CustomQueryArgs parameter.</summary>
    public required bool AcceptsArgs { get; init; }

    /// <summary>The MethodInfo for invocation.</summary>
    public required MethodInfo Method { get; init; }
}
```

Method metadata is cached in a `ConcurrentDictionary<string, CustomQueryMethod?>` keyed by `"{ActionsTypeName};{MethodName}"`, following Vidyano's caching pattern.

### 4.6 QueryExecutor Changes

The `QueryExecutor.ExecuteQueryAsync` method is extended to handle custom queries:

```csharp
public async Task<IEnumerable<PersistentObject>> ExecuteQueryAsync(
    SparkQuery query, PersistentObject? parent = null)
{
    // 1. Resolve the effective source
    var source = ResolveSource(query);

    if (source.IsCustom)
    {
        return await ExecuteCustomQueryAsync(query, source.Name, parent);
    }
    else
    {
        return await ExecuteDatabaseQueryAsync(query, source.Name);
    }
}

private (bool IsCustom, string Name) ResolveSource(SparkQuery query)
{
    // Use GeneratedRegex to parse "Database.X" or "Custom.X"
    if (source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase))
        return (true, source[7..]);

    if (source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
        return (false, source[9..]);

    throw new InvalidOperationException(
        $"Query '{query.Name}' has invalid Source '{query.Source}'. " +
        "Expected 'Database.PropertyName' or 'Custom.MethodName'.");
}
```

**Custom query execution pipeline:**

```
ExecuteCustomQueryAsync(query, methodName, parent)
  → Resolve Actions class via IActionsResolver (using query.EntityType)
  → Resolve method via ICustomQueryMethodResolver
  → Determine entity type from query.EntityType or infer from method's IQueryable<T> generic parameter
  → Get EntityTypeDefinition from IModelLoader
  → Authorize via IPermissionService.EnsureAuthorizedAsync("Query", entityTypeName)
  → Build CustomQueryArgs { Parent = parent, Query = query, Session = session }
  → Invoke method (with or without args) → returns IQueryable<T>
  → Apply sorting if sortBy specified (reuse existing ApplySorting)
  → Materialize results (ToListAsync for IRavenQueryable, or ToList for in-memory IQueryable)
  → Map via IEntityMapper.ToPersistentObject()
  → DistinctBy(po => po.Id)
```

### 4.7 Execute Endpoint Changes

The execute endpoint needs to accept an optional `parentId` and `parentType` for custom queries that require a parent context:

```
GET /spark/queries/{id}/execute?sortBy=X&sortDirection=Y&parentId=Z&parentType=W
```

**Updated `Execute.cs`:**

```csharp
public async Task HandleAsync(HttpContext httpContext, string id)
{
    var query = queryLoader.ResolveQuery(id);
    if (query is null) { /* 404 */ return; }

    // Read optional sort overrides from query string
    var sortBy = httpContext.Request.Query["sortBy"].FirstOrDefault();
    var sortDirection = httpContext.Request.Query["sortDirection"].FirstOrDefault();

    // Read optional parent context for custom queries
    PersistentObject? parent = null;
    var parentId = httpContext.Request.Query["parentId"].FirstOrDefault();
    var parentType = httpContext.Request.Query["parentType"].FirstOrDefault();
    if (!string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(parentType))
    {
        parent = await databaseAccess.GetPersistentObjectAsync(parentType, parentId);
    }

    var effectiveQuery = query with
    {
        SortBy = !string.IsNullOrEmpty(sortBy) ? sortBy : query.SortBy,
        SortDirection = !string.IsNullOrEmpty(sortDirection) ? sortDirection : query.SortDirection,
    };
    var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery, parent);
    await httpContext.Response.WriteAsJsonAsync(results);
}
```

### 4.8 Authorization

Custom queries use the existing `IPermissionService`:
- Action = `"Query"` (same as database queries)
- Target = entity type name (e.g., `"Person"`)

This means custom queries are authorized identically to database queries — if a user can query People via a database source, they can also query People via a custom source. Fine-grained per-method authorization is not needed for v1 (the custom method itself can perform additional checks if needed).

### 4.9 Package Placement

| Component | Package |
|-----------|---------|
| `CustomQueryArgs` | `MintPlayer.Spark.Abstractions` |
| `ICustomQueryMethodResolver`, `CustomQueryMethodResolver` | `MintPlayer.Spark` |
| `QueryExecutor` changes | `MintPlayer.Spark` |
| `SparkQuery.Source` / `SparkQuery.EntityType` properties | `MintPlayer.Spark.Abstractions` |
| Execute endpoint changes | `MintPlayer.Spark` |

No source generator changes needed — custom query methods are discovered at runtime via reflection on the already-registered Actions classes.

## 5. Query JSON Format

### Database Query

```json
{
  "id": "a20e8400-e29b-41d4-a716-446655440001",
  "name": "GetCars",
  "description": {"en": "Cars", "nl": "Auto's"},
  "source": "Database.Cars",
  "sortBy": "LicensePlate",
  "sortDirection": "asc"
}
```

### Custom Query

```json
{
  "id": "b30f9500-f39c-52e5-b827-557766551002",
  "name": "Company_People",
  "description": {"en": "People in Company", "nl": "Personen in Bedrijf"},
  "source": "Custom.Company_People",
  "entityType": "Person",
  "sortBy": "LastName",
  "sortDirection": "asc",
  "alias": "company-people"
}
```

## 6. Developer Experience

### Example 1: Detail Query (Parent-Child)

**Scenario:** Show people belonging to a specific company.

**`App_Data/Queries/Company_People.json`:**
```json
{
  "id": "b30f9500-f39c-52e5-b827-557766551002",
  "name": "Company_People",
  "description": {"en": "People in Company"},
  "source": "Custom.Company_People",
  "entityType": "Person",
  "sortBy": "LastName",
  "sortDirection": "asc"
}
```

**`PersonActions.cs`:**
```csharp
public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    public IRavenQueryable<Person> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return args.Session.Query<Person>()
            .Where(p => p.CompanyId == args.Parent!.Id);
    }

    // ... existing CRUD overrides
}
```

### Example 2: View-Model Query (No RavenDB Collection)

**Scenario:** Show a computed product overview for a company user. The `CompanyProductsOverview` type has no backing RavenDB collection — it's built in-memory from multiple sources.

**`App_Data/Model/CompanyProductsOverview.json`** (defines the PO type with attributes):
```json
{
  "PersistentObject": {
    "Type": "CompanyProductsOverview",
    "Label": {"en": "Products", "nl": "Producten"},
    "Attributes": [
      {"Name": "Name", "DataType": "String"},
      {"Name": "ProductUsed", "DataType": "Boolean"},
      {"Name": "ValidContract", "DataType": "Boolean"}
    ]
  }
}
```

**`CompanyUserActions.cs`:**
```csharp
public partial class CompanyUserActions : DefaultPersistentObjectActions<CompanyUser>
{
    public IQueryable<CompanyProductsOverview> CompanyUser_Products(CustomQueryArgs args)
    {
        args.EnsureParent("CompanyUser");
        var companyUser = args.Session.LoadAsync<CompanyUser>(args.Parent!.Id!).Result;
        var products = args.Session.Query<Product>().ToList();

        return products.Select(p => new CompanyProductsOverview
        {
            Id = p.Id,
            Name = p.Name,
            ProductUsed = companyUser.HasProduct(p.Id),
            ValidContract = companyUser.Product(p.Id)?.ValidContract ?? false,
        }).AsQueryable();
    }
}
```

**`App_Data/Queries/CompanyUser_Products.json`:**
```json
{
  "id": "d51b1700-b50e-74g7-da49-779988773004",
  "name": "CompanyUser_Products",
  "source": "Custom.CompanyUser_Products",
  "entityType": "CompanyProductsOverview",
  "sortBy": "Name"
}
```

### Example 3: Zero-Arg Custom Query

**Scenario:** Query with custom logic that doesn't need a parent.

**`CarActions.cs`:**
```csharp
public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    public IRavenQueryable<Car> ActiveCars()
    {
        return Session.Query<Car>()
            .Where(c => c.IsActive && c.ExpiryDate > DateTime.UtcNow);
    }
}
```

**`App_Data/Queries/ActiveCars.json`:**
```json
{
  "id": "c40a0600-a49d-63f6-c938-668877662003",
  "name": "ActiveCars",
  "source": "Custom.ActiveCars",
  "entityType": "Car",
  "sortBy": "LicensePlate"
}
```

## 7. Implementation Plan

### Phase 1: Core Abstractions
1. **Add `CustomQueryArgs`** to `MintPlayer.Spark.Abstractions/Queries/`
2. **Replace `ContextProperty` with `Source`** on `SparkQuery`, add `EntityType` property
3. **Update all existing query JSON files** in Demo apps to use `"source": "Database.X"` instead of `"contextProperty": "X"`
4. **Update `QueryLoader`** to work with the new `Source` property

### Phase 2: Query Resolution & Execution
4. **Create `ICustomQueryMethodResolver`** / `CustomQueryMethodResolver` in `MintPlayer.Spark/Services/`
5. **Extend `QueryExecutor`** with `ResolveSource()` and `ExecuteCustomQueryAsync()` methods
6. **Update `IQueryExecutor` interface** to accept optional `PersistentObject? parent` parameter
7. **Add method invocation pipeline** — invoke method (with/without args), apply sorting on returned `IQueryable<T>`, materialize, map to `PersistentObject[]`

### Phase 3: Endpoint Updates
8. **Update `ExecuteQuery` endpoint** to accept `parentId`/`parentType` query parameters
9. **Load parent PO** via `IDatabaseAccess` when parent params are present
10. **Pass parent** to `QueryExecutor.ExecuteQueryAsync()`

### Phase 4: Demo App
11. **Add a custom query** to the DemoApp (e.g., `Company_People` on `PersonActions`)
12. **Add corresponding query JSON** to `App_Data/Queries/`
13. **Verify** end-to-end: API call with parentId → custom method → filtered results

### Phase 5: Angular Client (Future)
14. **Update `SparkService.executeQuery()`** to accept optional parent parameters
15. **Update `SparkQueryListComponent`** to pass parent context when rendering sub-queries
16. **Support detail-view sub-query tabs** that show custom query results for the current object

## 8. Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Methods on Actions class only (not SparkContext) | SparkContext is a clean query-property container. Custom logic belongs in Actions classes alongside CRUD overrides. Simpler discovery — Actions are already resolved per entity type. |
| `Source` property with prefix (`Custom.` / `Database.`) | Clear, unambiguous routing. Follows Vidyano convention. No ambiguity — prefix is required. |
| `EntityType` optional on SparkQuery | Not mandatory — inferred from `IRavenQueryable<T>` (Database) or method return type (Custom). Supports view-model types with no RavenDB collection (e.g., `CompanyProductsOverview`). |
| Runtime reflection (not source generator) | Custom query methods are on Actions classes already discovered at startup. A dedicated source generator would add complexity for marginal benefit. Reflection results are cached. |
| `CustomQueryArgs` includes `Session` | Allows custom query methods to perform RavenDB queries without injecting `IAsyncDocumentSession` separately. Matches Vidyano's pattern where context methods access the session. |
| Always `IQueryable<T>` return type | Framework applies sorting uniformly. In-memory results use `.AsQueryable()`. Consistent with Vidyano convention. |
| Zero-arg methods supported | Simple custom queries (filters, computed sets) don't need parent context. Supporting both signatures reduces boilerplate. |
| Parent passed via query string params | RESTful, cacheable. Detail queries are just regular queries with extra context. No POST body needed. |
| Authorization reuses "Query" action | Custom queries are still queries — same permission model. Per-method auth can be added later if needed. |

## 9. Resolved Design Questions

1. **`EntityType` is optional for all queries.** For `Database.` sources it's inferred from `IRavenQueryable<T>`. For `Custom.` sources it's inferred from the method return type's `IQueryable<T>` generic parameter. Can be explicitly set to override inference.

2. **Custom queries always return `IQueryable<T>`.** The framework applies sorting uniformly. For in-memory results, use `.AsQueryable()`. This matches the Vidyano convention where `IQueryable<TQueryEntity>` is the standard return type.

3. **Parent context validation is developer-side.** The framework passes `CustomQueryArgs.Parent` but does not validate the parent type. Developers use `args.EnsureParent("TypeName")` inside their method. This is flexible and matches Vidyano's approach.

4. **View-model types (no RavenDB collection) are fully supported.** A custom query can return a type like `CompanyProductsOverview` that exists only as an `EntityTypeDefinition` in `App_Data/Model/` with no backing RavenDB collection. The framework maps results via `IEntityMapper` using the model definition. See Example 2 in section 6.
