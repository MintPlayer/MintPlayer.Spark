# PRD: PO/Query Alias (Friendly URL Slugs)

## Problem Statement

Currently, all Angular routes in the Spark framework use GUIDs to identify entity types (POs) and queries:

- **PO detail**: `/po/facb6829-f2a1-4ae2-a046-6ba506e8c0ce/Cars%2F0a797e8e-e8aa-4f55-ae8c-990f2e61ddf8`
- **Query list**: `/query/a20e8400-e29b-41d4-a716-446655440001`

These URLs are:
1. **Unreadable** - users cannot understand what page they're on from the URL
2. **Not shareable** - GUIDs are meaningless to humans
3. **Not bookmarkable** - users can't remember or type them
4. **Not SEO-friendly** - search engines prefer descriptive URLs

### Desired State

Users should be able to visit friendly URLs like:
- `/po/car/Cars%2F0a797e8e-e8aa-4f55-ae8c-990f2e61ddf8` - view a specific car
- `/po/car` - list all cars (via the associated query)
- `/query/cars` - query alias for the cars query

While maintaining full backward compatibility with GUID-based URLs.

## Current Architecture Summary

### JSON Configuration Files (`App_Data/`)

| File | Purpose | Key Fields |
|------|---------|------------|
| `Model/{Name}.json` | Entity type (PO) definition | `id` (guid), `name`, `clrType`, `attributes[]` |
| `Queries/{Name}.json` | Query definition | `id` (guid), `name`, `contextProperty`, `sortBy` |
| `programUnits.json` | Navigation menu config | `programUnitGroups[].programUnits[]` with `queryId`/`persistentObjectId` |

### Backend API Endpoints (`SparkMiddleware.MapSpark()`)

All PO/query endpoints use `{:guid}` route constraints:

```
GET  /spark/po/{objectTypeId:guid}            - List POs of type
GET  /spark/po/{objectTypeId:guid}/{id}       - Get specific PO
POST /spark/po/{objectTypeId:guid}            - Create PO
PUT  /spark/po/{objectTypeId:guid}/{id}       - Update PO
DELETE /spark/po/{objectTypeId:guid}/{id}      - Delete PO
GET  /spark/queries/{id:guid}                 - Get query definition
GET  /spark/queries/{id:guid}/execute         - Execute query
```

### Angular Routing (`app.routes.ts`)

```typescript
{ path: 'query/:queryId', loadComponent: () => import('./pages/query-list/query-list.component') },
{ path: 'po/:type/new', loadComponent: () => import('./pages/po-create/po-create.component') },
{ path: 'po/:type/:id', loadComponent: () => import('./pages/po-detail/po-detail.component') },
{ path: 'po/:type/:id/edit', loadComponent: () => import('./pages/po-edit/po-edit.component') },
```

Components use the `:type` param as a GUID to:
1. Call `SparkService.get(type, id)` which hits `/spark/po/{type}/{id}`
2. Find the entity type definition via `entityTypes.find(t => t.id === type)`

### Key Services
- **`IQueryLoader`** - Loads queries from `App_Data/Queries/*.json`, already has `GetQueryByName(string name)` method
- **`IProgramUnitsLoader`** - Loads `programUnits.json`
- **`IDatabaseAccess`** - CRUD operations using `objectTypeId` (Guid)

## Proposed Solution

### Approach: Add `alias` field to JSON definitions + resolve alias-to-GUID on the backend

This approach adds a new optional `alias` property to entity type and query JSON definitions. The backend receives alias-or-GUID and resolves it before processing. The Angular frontend uses aliases when available.

### Why This Approach

1. **Configuration-driven** - aliases are defined in the same JSON files that define the entities/queries
2. **Backward compatible** - GUIDs still work; aliases are optional
3. **Single source of truth** - the backend resolves aliases, so frontend doesn't need lookup logic
4. **Framework-level** - works for all demo apps (Fleet, HR, DemoApp) without app-specific code

### Alternative Considered: Frontend-only Resolution

Could resolve aliases purely in Angular by loading entity types/queries and mapping aliases to GUIDs before API calls. **Rejected** because:
- Requires an extra API call on every page load
- Duplicates resolution logic across frameworks
- Doesn't support direct API access with aliases (e.g., for external integrations)

## Detailed Design

### 1. JSON Schema Changes

#### Entity Type Definition (`App_Data/Model/*.json`)

Add optional `alias` field:

```json
{
  "id": "facb6829-f2a1-4ae2-a046-6ba506e8c0ce",
  "name": "Car",
  "alias": "car",
  "clrType": "Fleet.Entities.Car",
  ...
}
```

#### Query Definition (`App_Data/Queries/*.json`)

Add optional `alias` field:

```json
{
  "id": "a20e8400-e29b-41d4-a716-446655440001",
  "name": "GetCars",
  "alias": "cars",
  "contextProperty": "Cars",
  ...
}
```

#### Program Units (`programUnits.json`)

Add optional `alias` field to program units, allowing navigation to use the alias:

```json
{
  "id": "a10e8400-e29b-41d4-a716-446655440001",
  "name": "Cars",
  "icon": "bi-car-front-fill",
  "type": "query",
  "queryId": "a20e8400-e29b-41d4-a716-446655440001",
  "alias": "cars",
  "order": 1
}
```

### 2. C# Model Changes

#### `EntityTypeDefinition` (Abstractions)

```csharp
public sealed class EntityTypeDefinition
{
    // ... existing fields ...

    /// <summary>
    /// Optional URL-friendly alias for this entity type.
    /// Used as an alternative to the GUID in URLs (e.g., /po/car instead of /po/{guid}).
    /// Must be unique across all entity types. Lowercase, no spaces, alphanumeric + hyphens.
    /// </summary>
    public string? Alias { get; set; }
}
```

#### `SparkQuery` (Abstractions)

```csharp
public sealed class SparkQuery
{
    // ... existing fields ...

    /// <summary>
    /// Optional URL-friendly alias for this query.
    /// Used as an alternative to the GUID in URLs (e.g., /query/cars instead of /query/{guid}).
    /// Must be unique across all queries. Lowercase, no spaces, alphanumeric + hyphens.
    /// </summary>
    public string? Alias { get; set; }
}
```

#### `ProgramUnit` (Abstractions)

```csharp
public sealed class ProgramUnit
{
    // ... existing fields ...

    /// <summary>
    /// Optional URL-friendly alias for this program unit's target.
    /// If set, the frontend navigation will use this alias instead of the GUID.
    /// </summary>
    public string? Alias { get; set; }
}
```

### 3. Backend Endpoint Changes

#### Remove `:guid` constraint from routes

Change the route parameter from `{objectTypeId:guid}` to `{objectTypeId}` (string), and add resolution logic:

```csharp
// Before:
persistentObjectGroup.MapGet("/{objectTypeId:guid}", ...)

// After:
persistentObjectGroup.MapGet("/{objectTypeId}", ...)
```

#### Add `IEntityTypeResolver` service

New service that resolves either a GUID or an alias to the entity type definition:

```csharp
public interface IEntityTypeResolver
{
    EntityTypeDefinition? Resolve(string idOrAlias);
}
```

Implementation:
- If the input parses as a `Guid`, look up by ID (existing behavior)
- Otherwise, look up by alias (case-insensitive)
- Returns null if not found

#### Add `IQueryResolver` service

Similarly for queries:

```csharp
public interface IQueryResolver
{
    SparkQuery? Resolve(string idOrAlias);
}
```

#### Update All Endpoint Handlers

Each endpoint handler changes from accepting `Guid objectTypeId` to `string objectTypeId`, then uses the resolver:

```csharp
// Before:
public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId, string id)
{
    var obj = await databaseAccess.GetPersistentObjectAsync(objectTypeId, decodedId);
    ...
}

// After:
public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string id)
{
    var entityType = entityTypeResolver.Resolve(objectTypeId);
    if (entityType is null) { /* 404 */ return; }
    var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);
    ...
}
```

### 4. Angular Frontend Changes

#### TypeScript Models

Add `alias` to the interfaces:

```typescript
export interface EntityType {
  // ... existing ...
  alias?: string;
}

export interface SparkQuery {
  // ... existing ...
  alias?: string;
}

export interface ProgramUnit {
  // ... existing ...
  alias?: string;
}
```

#### Route Changes (`app.routes.ts`)

No changes needed! The routes already use `:type` and `:queryId` as string params. The only difference is what value is passed.

#### Shell Component (Navigation)

Update `getRouterLink()` to prefer aliases:

```typescript
getRouterLink(unit: ProgramUnit): string[] {
  if (unit.type === 'query') {
    return ['/query', unit.alias || unit.queryId!];
  } else if (unit.type === 'persistentObject') {
    return ['/po', unit.alias || unit.persistentObjectId!];
  }
  return ['/'];
}
```

#### Query List Component

Update to use entity type alias for PO navigation:

```typescript
onRowClick(item: PersistentObject): void {
  if (this.entityType) {
    this.router.navigate(['/po', this.entityType.alias || this.entityType.id, item.id]);
  }
}

onCreate(): void {
  if (this.entityType) {
    this.router.navigate(['/po', this.entityType.alias || this.entityType.id, 'new']);
  }
}
```

#### PO Detail/Edit Components

Update entity type matching to work with both alias and ID:

```typescript
// Before:
this.entityType = result.entityTypes.find(t => t.id === this.type) || null;

// After:
this.entityType = result.entityTypes.find(t => t.id === this.type || t.alias === this.type) || null;
```

#### SparkService (API calls)

No changes needed! The service already passes the type as a string to the API. The backend resolver handles whether it's a GUID or alias.

### 5. New API Endpoint: Alias Map

Add a new endpoint that returns the GUID-to-alias mapping:

```
GET /spark/aliases
```

Response:
```json
{
  "entityTypes": {
    "facb6829-f2a1-4ae2-a046-6ba506e8c0ce": "car",
    "ff87918c-1e9f-4467-a47e-831d49c81f25": "person",
    "f0f91878-bc14-4f33-bb42-2e4c6eabe88c": "company"
  },
  "queries": {
    "a20e8400-e29b-41d4-a716-446655440001": "cars",
    "a20e8400-e29b-41d4-a716-446655440002": "people",
    "a20e8400-e29b-41d4-a716-446655440003": "companies"
  }
}
```

Included in Phase 1. Useful for debugging, tooling, and potential client-side caching.

### 6. `/po/{alias}` Route for List View

Visiting `/po/car` (with alias, no document ID) renders the **same list page** as visiting `/po/{guid-of-car}`. No redirect - the same Angular component handles both.

#### How it works:

**Backend**: The existing `GET /spark/po/{objectTypeId}` endpoint already returns a list of POs for a given type. With the resolver change (accepting alias or GUID), this endpoint works for both `/spark/po/car` and `/spark/po/{guid}`.

**Frontend**: A new route is needed since the current routes don't have a `po/:type` list route (lists are currently accessed via `/query/:queryId`):

```typescript
// app.routes.ts - add new route for PO list by type
{ path: 'po/:type', loadComponent: () => import('./pages/query-list/query-list.component') },
```

The `QueryListComponent` needs to be updated to support two entry points:
1. **Via query route** (`/query/:queryId`): existing flow - resolve query by ID, find entity type, execute query
2. **Via PO type route** (`/po/:type`): new flow - resolve entity type by alias/ID, find associated query, execute that query

The component detects which route it came from and resolves the query accordingly. The entity type's associated query can be found by matching `query.contextProperty` against the entity type name (singular/plural matching that already exists in the component).

## Alias Rules

- **Auto-generated by default** - if no explicit `alias` in JSON, the framework generates one from `name` by lowercasing (e.g., `"Car"` -> `"car"`, `"GetCars"` -> `"getcars"`). For queries, a smarter default could strip the `Get` prefix and lowercase (e.g., `"GetCars"` -> `"cars"`).
- **Explicit override** - setting `"alias": "my-cars"` in the JSON file overrides the auto-generated value
- **Unique** - aliases must be unique within their scope (entity types, queries). Startup validation detects collisions and throws/warns.
- **Format** - lowercase, alphanumeric, hyphens allowed, no spaces: `^[a-z0-9-]+$`
- **Reserved words** - cannot use `new`, `edit`, or other route segment names as aliases
- **Startup validation** - loaders validate uniqueness and format at startup, logging warnings for duplicates

## Migration / Backward Compatibility

- **No breaking changes** - all existing GUID-based URLs continue to work
- **Gradual adoption** - aliases can be added to JSON files one at a time
- **No database changes** - aliases are in JSON config files only, not stored in RavenDB
- **API backward compatible** - endpoints accept both GUID and alias formats

## Implementation Plan

### Phase 1: Backend (Framework)
1. Add `Alias` property to `EntityTypeDefinition`, `SparkQuery`, and `ProgramUnit` models
2. Add auto-generation logic: when `Alias` is null, generate from `Name` (lowercase for entity types; strip `Get` prefix + lowercase for queries)
3. Create `IEntityTypeResolver` and `IQueryResolver` services
4. Update entity type loader (ModelLoader) to build an alias-to-definition lookup dictionary
5. Update query loader to build an alias-to-query lookup dictionary
6. Change endpoint route constraints from `{:guid}` to `{string}`
7. Update all endpoint handlers to use resolvers (accept string, resolve to entity type/query)
8. Add `/spark/aliases` endpoint returning GUID-to-alias mapping
9. Add startup validation for alias uniqueness and format

### Phase 2: Frontend (Angular)
1. Add `alias` fields to TypeScript models (`EntityType`, `SparkQuery`, `ProgramUnit`)
2. Add new route: `{ path: 'po/:type', loadComponent: () => import('./pages/query-list/query-list.component') }`
3. Update `QueryListComponent` to support two entry points: via `/query/:queryId` (existing) and via `/po/:type` (new - resolve entity type, find query, execute)
4. Update shell component navigation to prefer aliases in `getRouterLink()`
5. Update query-list component navigation to prefer aliases in `onRowClick()` and `onCreate()`
6. Update po-detail/po-edit/po-create components to match entity type by alias or ID
7. Update any other components that build PO/query URLs

### Phase 3: Demo App Configuration
1. Add aliases to Fleet demo `App_Data/Model/*.json` files (or rely on auto-generation)
2. Add aliases to Fleet demo `App_Data/Queries/*.json` files (or rely on auto-generation)
3. Add aliases to Fleet demo `programUnits.json`
4. Repeat for HR and DemoApp demos

## Files to Modify

### Framework (MintPlayer.Spark.Abstractions)
- `EntityTypeDefinition.cs` - add `Alias` property
- `SparkQuery.cs` - add `Alias` property
- `ProgramUnit.cs` - add `Alias` property

### Framework (MintPlayer.Spark)
- `SparkMiddleware.cs` - remove `:guid` constraints, update handler signatures, add `/spark/aliases` route
- `Services/ModelLoader.cs` - add alias lookup dictionary, auto-generate aliases from `Name`
- `Services/QueryLoader.cs` - add alias lookup dictionary, auto-generate aliases from `Name`
- New: `Services/EntityTypeResolver.cs` - IEntityTypeResolver implementation
- New: `Services/QueryResolver.cs` - IQueryResolver implementation
- New: `Endpoints/Aliases/GetAliases.cs` - `/spark/aliases` endpoint handler
- `Endpoints/PersistentObject/Get.cs` - use resolver
- `Endpoints/PersistentObject/List.cs` - use resolver (if it exists)
- `Endpoints/PersistentObject/Create.cs` - use resolver
- `Endpoints/PersistentObject/Update.cs` - use resolver
- `Endpoints/PersistentObject/Delete.cs` - use resolver
- `Endpoints/Queries/Execute.cs` - use resolver
- `Endpoints/Queries/Get.cs` - use resolver (if it exists)

### Demo App (Fleet) - same changes apply to HR and DemoApp
- `ClientApp/src/app/app.routes.ts` - add `po/:type` list route
- `ClientApp/src/app/core/models/entity-type.ts` - add `alias`
- `ClientApp/src/app/core/models/spark-query.ts` - add `alias`
- `ClientApp/src/app/core/models/program-unit.ts` - add `alias`
- `ClientApp/src/app/shell/shell.component.ts` - prefer alias in navigation
- `ClientApp/src/app/pages/query-list/query-list.component.ts` - support `/po/:type` entry point, prefer alias in navigation
- `ClientApp/src/app/pages/po-detail/po-detail.component.ts` - match by alias
- `ClientApp/src/app/pages/po-edit/po-edit.component.ts` - match by alias
- `ClientApp/src/app/pages/po-create/po-create.component.ts` - match by alias
- `App_Data/Model/Car.json` - optionally add explicit `"alias"` (auto-generated if omitted)
- `App_Data/Model/Person.json` - optionally add explicit `"alias"`
- `App_Data/Model/Company.json` - optionally add explicit `"alias"`
- `App_Data/Queries/GetCars.json` - optionally add explicit `"alias"`
- `App_Data/Queries/GetCompanies.json` - optionally add explicit `"alias"`
- `App_Data/Queries/GetPeople.json` - optionally add explicit `"alias"`
- `App_Data/programUnits.json` - add aliases to program units

## Resolved Design Decisions

1. **Auto-generated aliases**: Yes. When no explicit `alias` is set in JSON, the framework auto-generates one from the `name` field by lowercasing it (e.g., `"name": "Car"` -> alias `"car"`). Explicit `alias` values in JSON override the auto-generated one. Startup validation detects and warns about collisions.

2. **`/spark/aliases` endpoint**: Yes, included in Phase 1. Returns a GUID-to-name mapping object for both entity types and queries. Useful for debugging, tooling, and potential future client-side caching.

3. **`/po/{alias}` without document ID renders the list page**: Yes. Visiting `/po/car` (no document ID) renders the **same list page** as `/po/{guid}` would - not a redirect, but literally the same component. The Angular route `po/:type` already matches this (the existing `ListPersistentObjects` endpoint handles it). The frontend component resolves the entity type by alias or GUID, finds the associated query, and renders the query list. This means the `po/:type` route needs a component that can detect whether it's showing a list (no `:id`) or a detail (with `:id`), or a new route `po/:type` is added for the list case.
