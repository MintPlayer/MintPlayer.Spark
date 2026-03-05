# Development Plan: Issue #37 — Custom Queries

## Milestones

### Milestone 1: Core Abstractions
- [ ] Add `CustomQueryArgs` to `MintPlayer.Spark.Abstractions/Queries/`
- [ ] Replace `ContextProperty` with `Source` on `SparkQuery`, add `EntityType` property
- [ ] Update all existing query JSON files in Demo apps to use `"source": "Database.X"`
- [ ] Update `QueryLoader` to work with the new `Source` property
- [ ] Update `ExecuteQuery` endpoint to use `Source` instead of `ContextProperty`

### Milestone 2: Custom Query Resolution & Execution
- [ ] Create `ICustomQueryMethodResolver` / `CustomQueryMethodResolver` in `MintPlayer.Spark/Services/`
- [ ] Extend `QueryExecutor` with `ResolveSource()` and `ExecuteCustomQueryAsync()`
- [ ] Update `IQueryExecutor` interface to accept optional `PersistentObject? parent` parameter
- [ ] Add method invocation pipeline (invoke, sort, materialize, map)

### Milestone 3: Endpoint Updates
- [ ] Update `ExecuteQuery` endpoint to accept `parentId`/`parentType` query parameters
- [ ] Load parent PO via `IDatabaseAccess` when parent params are present
- [ ] Pass parent to `QueryExecutor.ExecuteQueryAsync()`

### Milestone 4: Demo App
- [ ] Add a custom query to DemoApp (e.g., `Company_People` on `PersonActions`)
- [ ] Add corresponding query JSON to `App_Data/Queries/`
- [ ] Verify end-to-end: API call with parentId → custom method → filtered results

### Milestone 5: Build & Verify
- [ ] `dotnet build` succeeds
- [ ] Manual API test passes
