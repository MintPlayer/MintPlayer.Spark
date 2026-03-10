# PRD: Resolve Reference Breadcrumbs in Query Results

**Status:** Complete
**Last Updated:** 2026-03-10
**Issue:** Regression — References on query-list pages show raw GUIDs instead of breadcrumbs

## Problem

When viewing a query-list page (e.g., `/query/people`), reference columns display raw RavenDB document IDs like `Companies/1d509ef3-28a1-42eb-9fba-7a24fc776d44` instead of resolved breadcrumbs (e.g., "Acme Corp").

The root cause: `QueryExecutor` and `StreamingQueryExecutor` call `EntityMapper.ToPersistentObject(entity, objectTypeId)` **without** passing `includedDocuments`. Without that dictionary, the EntityMapper cannot resolve reference attribute breadcrumbs (see `EntityMapper.cs:131`).

By contrast, `DatabaseAccess.GetPersistentObjectsAsync()` correctly:
1. Discovers `[Reference]` properties via `GetReferenceProperties()`
2. Queries with `.Include()` chains via `QueryEntitiesWithIncludesAsync()`
3. Extracts referenced documents from session cache via `ExtractIncludedDocumentsFromSessionAsync()`
4. Passes the `includedDocuments` dictionary to `EntityMapper.ToPersistentObject()`

This pattern is entirely missing from both `QueryExecutor` and `StreamingQueryExecutor`.

## Solution

### Phase 1: Fix QueryExecutor (non-streaming queries)

**File:** `MintPlayer.Spark/Services/QueryExecutor.cs`

Both `ExecuteDatabaseQueryAsync()` and `ExecuteCustomQueryAsync()` need to load referenced documents after materializing query results, then pass them to `EntityMapper.ToPersistentObject()`.

#### Approach

1. **Extract a shared helper** (or inject `IDatabaseAccess` private methods as a new internal service) for:
   - `GetReferenceProperties(Type entityType)` — already exists on `DatabaseAccess`, extract to a shared utility or duplicate on `QueryExecutor`
   - `ExtractIncludedDocumentsFromSessionAsync(session, entities, referenceProperties)` — same pattern

2. **In `ExecuteDatabaseQueryAsync()`** (line 91):
   - After materializing `entities` (line 164), determine the entity CLR type's reference properties
   - Load referenced documents from the **same session** (the documents should already be in cache if `.Include()` was used, but since the query doesn't use `.Include()` chains today, we need to either add `.Include()` to the queryable or load references in a second pass)
   - **Recommended:** Add `.Include(propertyName)` chains to the queryable before `ExecuteQueryableAsync()`, matching the pattern in `DatabaseAccess.QueryEntitiesWithIncludesAsync()`
   - Pass `includedDocuments` to `entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, includedDocuments)`

3. **In `ExecuteCustomQueryAsync()`** (line 175):
   - Same approach: after materializing entities, extract reference properties, load referenced documents, pass to mapper
   - For custom queries, the queryable may be in-memory (not RavenDB), so `.Include()` won't work. Use the second-pass approach: collect all reference IDs, batch-load them via session

#### Key Considerations

- The `resultType` may differ from `entityType` when index projections are used (e.g., `VPerson` vs `Person`). Reference properties may be on either. Check `queryType`/`resultType` for `[Reference]` attributes, not just the base entity type.
- The session is created at the top of each method — reference loading must happen within the same `using` scope.
- For Database queries, `.Include()` chains are the most efficient (single round-trip). For Custom queries where the queryable isn't always a RavenDB query, fall back to batch-loading references after materialization.

### Phase 2: Fix StreamingQueryExecutor

**File:** `MintPlayer.Spark/Streaming/StreamingQueryExecutor.cs`

In `ExecuteStreamingQueryAsync()`, after mapping each batch (line 88-90):
- For each batch, collect reference IDs from the entities
- Load referenced documents from the session
- Pass `includedDocuments` to `entityMapper.ToPersistentObject()`

**Note:** Streaming queries open a single session for the entire stream. Referenced documents loaded in earlier batches remain in session cache, so subsequent batches with the same references won't trigger additional database calls.

### Phase 3: Refactor shared reference-loading logic

Extract the reference-loading pattern from `DatabaseAccess` into a shared internal service (e.g., `IReferenceResolver`) to avoid code duplication:

```csharp
internal interface IReferenceResolver
{
    List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType);

    Task<Dictionary<string, object>> LoadReferencedDocumentsAsync(
        IAsyncDocumentSession session,
        IEnumerable<object> entities,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties);
}
```

Both `DatabaseAccess`, `QueryExecutor`, and `StreamingQueryExecutor` should use this shared service.

### Phase 4: Regression test

Add an integration/unit test that verifies reference breadcrumbs are populated in query results. The test should:
1. Set up an entity type with a `[Reference]` property (e.g., `Person` with `Company` reference)
2. Execute a query via `QueryExecutor`
3. Assert that the resulting `PersistentObject.Attributes` contains a reference attribute with a non-null `Breadcrumb` (not the raw document ID)

This prevents future regressions when query execution code is refactored.

## Files to Modify

| File | Change |
|------|--------|
| `MintPlayer.Spark/Services/QueryExecutor.cs` | Add reference loading to both `ExecuteDatabaseQueryAsync` and `ExecuteCustomQueryAsync` |
| `MintPlayer.Spark/Streaming/StreamingQueryExecutor.cs` | Add reference loading to batch mapping loop |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` | Extract shared reference-loading methods (or create new service) |
| New: `MintPlayer.Spark/Services/ReferenceResolver.cs` | Shared reference resolution service |

## Implementation Order

1. Extract `ReferenceResolver` from `DatabaseAccess` (no behavior change)
2. Wire `ReferenceResolver` into `QueryExecutor`, fix both query paths
3. Wire `ReferenceResolver` into `StreamingQueryExecutor`, fix streaming path
4. Update `DatabaseAccess` to use `ReferenceResolver` instead of private methods
5. Add regression test
6. Manual verification on `/query/people` page

## Out of Scope

- Frontend changes (the pipes already handle breadcrumbs correctly when present)
- Changes to the `EntityMapper` (breadcrumb resolution logic is correct, it just needs the data)
- Performance optimization with `.Include()` on custom queries (nice-to-have, not blocking)
