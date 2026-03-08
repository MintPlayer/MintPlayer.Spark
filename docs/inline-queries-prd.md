# PRD: Inline Query Definitions in Entity Type Files

**Status:** Draft
**Last Updated:** 2026-03-08
**Issue:** N/A (internal improvement)

## Problem

Spark stores query definitions as separate JSON files in `App_Data/Queries/`. The framework must determine each query's return type:
- **Database queries** (`Database.People`): Return type is inferred from the `IRavenQueryable<T>` property on SparkContext — works fine.
- **Custom queries** (`Custom.MethodName`): Return type **cannot** be inferred from the source string alone. Requires an explicit `entityType` property in the JSON.

This is fragile and error-prone. The Vidyano approach solves this by embedding queries in the entity type definition file of their return type, making the association implicit.

## Solution

Move query definitions from separate `App_Data/Queries/*.json` files into the `App_Data/Model/*.json` entity type files. Each query lives in the file of the entity type it returns.

**No backward compatibility required** — this is a breaking change to the file format.

### New File Format

Current `App_Data/Model/Person.json` (flat EntityTypeDefinition):
```json
{
  "id": "550e8400-...",
  "name": "Person",
  "clrType": "DemoApp.Library.Entities.Person",
  ...
  "attributes": [...]
}
```

New format (wrapper with queries):
```json
{
  "persistentObject": {
    "id": "550e8400-...",
    "name": "Person",
    "clrType": "DemoApp.Library.Entities.Person",
    ...
    "queries": ["company-people"],
    "attributes": [...]
  },
  "queries": [
    {
      "id": "880e8400-...",
      "name": "GetPeople",
      "description": { "en": "People", "nl": "Personen" },
      "source": "Database.People",
      "sortBy": "LastName",
      "sortDirection": "asc"
    },
    {
      "id": "d40f9500-...",
      "name": "Company_People",
      "description": { "en": "People in Company" },
      "source": "Custom.Company_People",
      "sortBy": "FullName",
      "sortDirection": "asc",
      "alias": "company-people"
    }
  ]
}
```

Key differences:
- Top-level wrapper with `persistentObject` and `queries` keys
- `persistentObject` contains the existing `EntityTypeDefinition` (unchanged)
- `queries` is an array of `SparkQuery` objects whose return type is this entity
- `persistentObject.queries` (string[]) still references sub-query aliases for the detail page
- `entityType` property on `SparkQuery` becomes optional in JSON (populated at load time from parent entity)

## Functional Requirements

### FR-1: New JSON file format
- [ ] Model JSON files use wrapper format: `{ "persistentObject": {...}, "queries": [...] }`
- [ ] `queries` array contains full `SparkQuery` objects
- [ ] `entityType` on each query is auto-populated from the parent entity during loading
- [ ] Entity types without queries have an empty `queries` array (or omit it)

### FR-2: Backend — ModelLoader changes
- [ ] `ModelLoader` deserializes the new wrapper format from `App_Data/Model/*.json`
- [ ] Extracts `EntityTypeDefinition` from `persistentObject` key
- [ ] Extracts `SparkQuery[]` from `queries` key
- [ ] Populates `query.EntityType` with the parent entity's `Name` for each query

### FR-3: Backend — QueryLoader changes
- [ ] `QueryLoader` no longer reads from `App_Data/Queries/` directory
- [ ] `QueryLoader` receives queries from `ModelLoader` (or reads from Model files directly)
- [ ] All existing query resolution methods remain functional: `GetQueries()`, `GetQuery(id)`, `GetQueryByAlias(alias)`, `ResolveQuery(idOrAlias)`

### FR-4: Backend — QueryExecutor changes
- [ ] `QueryExecutor` continues to work with `SparkQuery` objects as before
- [ ] For Custom queries, `entityType` is always populated (from parent entity type)
- [ ] No changes to execution logic — only the source of query definitions changes

### FR-5: API endpoint changes
- [ ] `GET /spark/queries/` — returns all queries (aggregated from all entity types)
- [ ] `GET /spark/queries/{id}` — returns single query by ID or alias
- [ ] `GET /spark/queries/{id}/execute` — executes query (unchanged)
- [ ] Endpoints continue to work identically from the frontend's perspective

### FR-6: Frontend changes
- [ ] No changes to `SparkQuery` TypeScript model
- [ ] No changes to query list/detail/sub-query components
- [ ] Frontend still fetches queries via the same API endpoints
- [ ] Entity type resolution from `query.entityType` now always works (always populated)

### FR-7: Demo app migration
- [ ] All three demo apps (DemoApp, HR, Fleet) migrated to new format
- [ ] `App_Data/Queries/` directories removed
- [ ] Queries moved into corresponding `App_Data/Model/*.json` files
- [ ] Existing functionality preserved

## Migration Guide

For each query file in `App_Data/Queries/`:
1. Determine the return entity type (from `entityType` property or `Database.X` source)
2. Move the query into the `queries` array of that entity's Model JSON file
3. Remove the `entityType` property from the query (now implicit)
4. Wrap the existing entity definition in `{ "persistentObject": {...}, "queries": [...] }`
5. Delete the original query file and `App_Data/Queries/` directory

## Technical Design

### Deserialization model

New wrapper class:
```csharp
public sealed class EntityTypeFile
{
    public required EntityTypeDefinition PersistentObject { get; set; }
    public SparkQuery[] Queries { get; set; } = [];
}
```

### ModelLoader changes

`ModelLoader` reads `App_Data/Model/*.json`, deserializes as `EntityTypeFile`, extracts the `PersistentObject` and populates each query's `EntityType` from the entity name.

### QueryLoader changes

`QueryLoader` receives the aggregated query list from `ModelLoader` instead of reading files itself. The lazy-loading pattern remains but sources from model data instead of separate files.

## Open Questions

None — design is straightforward following the Vidyano pattern.

## Milestones

### M1: Backend changes
- New `EntityTypeFile` wrapper class
- Update `ModelLoader` to parse new format and extract queries
- Update `QueryLoader` to source queries from model data
- Verify all query endpoints still work

### M2: Demo app migration
- Migrate all 3 demo app data files to new format
- Remove `App_Data/Queries/` directories

### M3: Build verification
- `dotnet build` passes
- `npm run build` passes (frontend unchanged, should pass trivially)
