# PRD: Update ModelSynchronizer to Use Inline Query Format

## Problem Statement

PR #50 ("Inline query definitions in entity type files") changed the `App_Data/Model/*.json` file format from a flat `EntityTypeDefinition` to a new wrapper format:

```json
{
  "persistentObject": { /* EntityTypeDefinition */ },
  "queries": [ /* SparkQuery[] */ ]
}
```

The `ModelLoader` and `QueryLoader` were updated to read this new format via the `EntityTypeFile` wrapper class, and all demo apps were migrated. However, the `ModelSynchronizer` — which **writes** these files during development — was **not updated** and still:

1. **Serializes `EntityTypeDefinition` directly** (line 91 of `ModelSynchronizer.cs`), producing the old flat format without the `{ "persistentObject": ..., "queries": [...] }` wrapper
2. **Writes queries to separate `App_Data/Queries/*.json` files** (lines 117-134), which are no longer read by the updated `QueryLoader` (it now delegates to `ModelLoader.GetQueries()` which reads inline queries)
3. **Reads queries from `App_Data/Queries/` for duplicate detection** (lines 43, 118), which won't find queries that are already inlined in entity type files

This means running model synchronization after PR #50 will:
- Overwrite the correctly-formatted entity type files with the old flat format, breaking `ModelLoader`
- Create orphaned query files in `App_Data/Queries/` that nothing reads
- Fail to detect existing inline queries, creating duplicates

## Scope

**In scope:**
- Update `ModelSynchronizer.SynchronizeModels()` to write the `EntityTypeFile` wrapper format
- Update `ModelSynchronizer.LoadExistingEntityTypes()` to read the new wrapper format (so it can merge with existing data)
- Update `ModelSynchronizer` query handling to write queries inline in entity type files instead of separate files
- Update `ModelSynchronizer` to load existing inline queries for duplicate detection
- Remove the `App_Data/Queries/` directory handling (creation, reading, writing, cleanup)

**Out of scope:**
- Changes to `ModelLoader`, `QueryLoader`, or `EntityTypeFile` (already correct)
- Frontend changes
- Migration tooling for old-format files (PR #50 already migrated all demo apps)

## Current Behavior (Broken)

### ModelSynchronizer writes:

```
App_Data/
├── Model/
│   ├── Car.json          ← flat EntityTypeDefinition (OLD format)
│   └── Company.json      ← flat EntityTypeDefinition (OLD format)
└── Queries/
    ├── GetCars.json       ← separate query file (ORPHANED)
    └── GetCompanies.json  ← separate query file (ORPHANED)
```

### ModelLoader reads (expects):

```
App_Data/
└── Model/
    ├── Car.json           ← { "persistentObject": {...}, "queries": [...] }
    └── Company.json       ← { "persistentObject": {...}, "queries": [...] }
```

## Desired Behavior

### ModelSynchronizer should write:

```
App_Data/
└── Model/
    ├── Car.json           ← { "persistentObject": {...}, "queries": [...] }
    └── Company.json       ← { "persistentObject": {...}, "queries": [...] }
```

No `App_Data/Queries/` directory should be created or referenced.

## Implementation Plan

### 1. Update `LoadExistingEntityTypes()` to parse `EntityTypeFile` wrapper

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 214-242)

Currently deserializes `EntityTypeDefinition` directly. Must deserialize `EntityTypeFile` and extract `PersistentObject`.

```csharp
// BEFORE:
var entityType = JsonSerializer.Deserialize<EntityTypeDefinition>(json, ...);

// AFTER:
var entityTypeFile = JsonSerializer.Deserialize<EntityTypeFile>(json, ...);
var entityType = entityTypeFile?.PersistentObject;
```

Also return/track the existing queries from each file for merge/duplicate detection.

### 2. Update `LoadExistingQueries()` to read from entity type files

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 244-272)

Replace the method that reads from `App_Data/Queries/*.json` with one that extracts queries from the already-loaded `EntityTypeFile` data. Alternatively, combine it into `LoadExistingEntityTypes()` to return both entity types and queries together.

### 3. Update the serialization to use `EntityTypeFile` wrapper

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 89-92)

```csharp
// BEFORE:
var json = JsonSerializer.Serialize(entityTypeDef, JsonOptions);

// AFTER:
var entityTypeFile = new EntityTypeFile
{
    PersistentObject = entityTypeDef,
    Queries = queriesForThisType  // collected queries for this entity
};
var json = JsonSerializer.Serialize(entityTypeFile, JsonOptions);
```

### 4. Move query creation inline

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs` (lines 117-134)

Instead of writing queries to `App_Data/Queries/{queryName}.json`, collect the query in a dictionary keyed by entity type name and include it in the `EntityTypeFile.Queries` array when serializing.

The query's `EntityType` property should be set to the entity type name (matching `ModelLoader` behavior at line 77).

### 5. Remove `App_Data/Queries/` directory handling

- Remove `Directory.CreateDirectory(queriesPath)` (line 39)
- Remove the `queriesPath` variable (line 35)
- Remove the `LoadExistingQueries(queriesPath)` call (line 43)
- Remove the stale projection query file cleanup (lines 176-184)

### 6. Update stale file cleanup

The existing cleanup for stale projection model files (lines 161-186) should remain for model files. The query file cleanup portion should be removed since queries no longer have separate files.

## Test Plan

- [ ] Run `--spark-synchronize-model` on DemoApp — verify `App_Data/Model/*.json` files use the new wrapper format
- [ ] Verify `ModelLoader` correctly reads the synchronized files (queries are loaded, entity types resolve)
- [ ] Verify no `App_Data/Queries/` directory is created
- [ ] Verify existing inline queries are preserved (not duplicated) on re-sync
- [ ] Verify existing custom fields (labels, descriptions, rules, tabs, groups) are preserved on re-sync
- [ ] Run all 3 demo apps (DemoApp, HR, Fleet) and verify entity listing and detail pages work
- [ ] `dotnet build` passes for all projects
