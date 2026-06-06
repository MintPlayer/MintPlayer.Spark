# PRD: AsDetail Reference with Parent Context

## Problem

On the `GitHubProject` edit page, the `EventMappings` AsDetail table has a `TargetColumnOptionId` field that is currently a plain text input (`dataType: "string"`). Users must manually type the GitHub column option ID (e.g., `"f75ad846"`), which is error-prone and requires looking up the ID elsewhere.

The available column option IDs are already stored on the same `GitHubProject` entity in the `Columns` AsDetail array (`ProjectColumn[]`), each with an `OptionId` and `Name`. The goal is to render `TargetColumnOptionId` as a reference picker (dropdown in inline mode) whose options come from the parent entity's `Columns` via a custom query.

## Current State

### Models

**EventColumnMapping** (AsDetail on GitHubProject):
- `WebhookEvent` (string, LookupReference to WebhookEventType) 
- `TargetColumnOptionId` (string) -- plain text, should become a Reference
- `MoveLinkedIssues` (bool)

**ProjectColumn** (AsDetail on GitHubProject):
- `OptionId` (string) -- the GitHub single-select option ID
- `Name` (string) -- display name (e.g., "Todo", "In Progress")
- No `Id` property

**GitHubProject**:
- `Columns` (ProjectColumn[]) -- cached from GitHub, read-only
- `EventMappings` (EventColumnMapping[]) -- user-configured rules

### How AsDetail Reference Loading Works Today

In `SparkPoFormComponent.loadAsDetailTypes()`, when an AsDetail column has `dataType: "Reference"` and a `query`, the form calls:
```typescript
const result = await this.sparkService.executeQueryByName(col.query!);
```
This calls `executeQuery(query.id)` with **no parent context** (`parentId`/`parentType` are not passed). The query results populate a `<bs-select>` dropdown for inline editing.

### The Gap

1. **No parent context for AsDetail reference queries**: `executeQueryByName()` doesn't accept or forward `parentId`/`parentType`, so the custom query has no way to know which `GitHubProject`'s columns to return.

2. **ProjectColumn has no `Id` property**: The Reference picker expects items with an `id` field. `ProjectColumn` only has `OptionId` and `Name`. When the QueryExecutor maps `ProjectColumn` entities to `PersistentObject`, the `id` will be null.

3. **The value stored must be `OptionId`, not a document ID**: Standard References store the referenced document's ID. Here, `TargetColumnOptionId` stores a GitHub option ID string, not a RavenDB document ID.

## Proposed Solution

### Phase 1: Backend -- Custom Query

**Add a custom query method** on `GitHubProjectActions` that returns the parent project's columns:

```csharp
// In GitHubProjectActions.cs
public IEnumerable<ProjectColumn> GetProjectColumns(CustomQueryArgs args)
{
    args.EnsureParent("GitHubProject");
    
    var columnsAttr = args.Parent!.Attributes
        .FirstOrDefault(a => a.Name == "Columns");
    
    // Extract column objects from the AsDetail attribute value
    // Return them so QueryExecutor can map them to PersistentObjects
}
```

**Query definition** in `GitHubProject.json` (or a separate query file):
```json
{
  "name": "GetProjectColumns",
  "source": "Custom.GetProjectColumns",
  "sortColumns": [],
  "renderMode": "Pagination",
  "useProjection": false
}
```

**Challenge**: The custom query returns `IEnumerable<ProjectColumn>`, which the `QueryExecutor` maps to `PersistentObject[]`. But `ProjectColumn` has no `Id` property, so the mapped PO's `id` will be null.

**Solution**: Add an `Id` property to `ProjectColumn` that maps to `OptionId`:
```csharp
public class ProjectColumn
{
    public string Id => OptionId;
    public string OptionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
```

Since `ProjectColumn` is an embedded AsDetail type (not a standalone RavenDB document), adding `Id` as a computed property won't create a collection. RavenDB only uses `Id` for top-level documents. The `Id` getter ensures the QueryExecutor produces PersistentObjects with proper `id` fields for the reference picker.

### Phase 2: Model Definition Changes

**EventColumnMapping.json** -- change `TargetColumnOptionId`:
```json
{
  "name": "TargetColumnOptionId",
  "label": { "en": "Target Column" },
  "dataType": "Reference",
  "query": "GetProjectColumns",
  "referenceType": "WebhooksDemo.Entities.ProjectColumn",
  "isRequired": false,
  "isVisible": true,
  "isReadOnly": false,
  "showedOn": "Query, PersistentObject"
}
```

This tells the Spark form to render a reference picker (dropdown in inline mode) and load options from the `GetProjectColumns` query.

### Phase 3: Frontend -- Pass Parent Context for AsDetail Reference Queries

**Modify `SparkPoFormComponent`** to pass the current entity's ID and type when loading reference options for AsDetail columns.

The component already has access to the entity being edited via its form data. The `parentId` is the current entity's document ID, and `parentType` is the entity type name.

**Changes to `loadAsDetailTypes()`** in `spark-po-form.component.ts`:

```typescript
// Current (no parent context):
const result = await this.sparkService.executeQueryByName(col.query!);

// Proposed (with parent context):
const result = await this.sparkService.executeQueryByName(col.query!, {
  parentId: this.formData()?.id,
  parentType: this.entityType()?.name,
});
```

**Changes to `SparkService.executeQueryByName()`**:

```typescript
// Current:
async executeQueryByName(queryName: string): Promise<QueryResult> {
  const query = await this.getQueryByName(queryName);
  return query ? this.executeQuery(query.id) : { data: [], totalRecords: 0, skip: 0, take: 50 };
}

// Proposed:
async executeQueryByName(queryName: string, options?: {
  parentId?: string;
  parentType?: string;
}): Promise<QueryResult> {
  const query = await this.getQueryByName(queryName);
  return query
    ? this.executeQuery(query.id, { parentId: options?.parentId, parentType: options?.parentType })
    : { data: [], totalRecords: 0, skip: 0, take: 50 };
}
```

No backend changes needed for the query execution endpoint -- it already supports `parentId`/`parentType` query parameters and loads the parent as a full `PersistentObject` in `CustomQueryArgs.Parent`.

### Phase 4: Handle Value Mapping

Standard Reference attributes store a document ID (e.g., `"companies/1-A"`). Here, `TargetColumnOptionId` stores a GitHub option ID (e.g., `"f75ad846"`). Since we're adding `Id => OptionId` on `ProjectColumn`, the reference picker will store `OptionId` as the value -- which is exactly what `TargetColumnOptionId` needs. No special value mapping is required.

## Summary of Changes

| Layer | File | Change |
|-------|------|--------|
| Entity | `ProjectColumn.cs` | Add `public string Id => OptionId;` |
| Actions | `GitHubProjectActions.cs` | Add `GetProjectColumns(CustomQueryArgs)` method |
| Model | `GitHubProject.json` or queries section | Add `GetProjectColumns` query definition |
| Model | `EventColumnMapping.json` | Change `TargetColumnOptionId` to `dataType: "Reference"` with `query` |
| Frontend | `spark.service.ts` | Extend `executeQueryByName` to accept parent context options |
| Frontend | `spark-po-form.component.ts` | Pass `parentId`/`parentType` in `loadAsDetailTypes()` |

## Risks and Considerations

1. **`ProjectColumn.Id` as a computed property**: Adding `Id => OptionId` is safe for an embedded type, but verify that RavenDB serialization doesn't produce a redundant `Id` field in the stored JSON. If it does, add `[JsonIgnore]` (or `JsonPropertyName` to control serialization).

2. **Backward compatibility of `executeQueryByName`**: The new `options` parameter is optional, so all existing callers are unaffected. The `SparkPoDetailComponent` also calls `executeQueryByName` in `loadAsDetailTypes()` -- it should also be updated to pass parent context.

3. **Refresh after Sync Columns**: When the user clicks "Sync Columns" on the detail page and new columns appear, the EventMappings editor needs updated reference options. Since `refreshOnCompleted: true` is set on the action, the detail page reloads. On re-entering edit mode, `loadAsDetailTypes()` will fetch fresh column options.

4. **Empty Columns edge case**: If no columns have been synced yet (empty `Columns` array), the custom query returns an empty list and the dropdown shows no options. This is the correct behavior -- the user should sync columns first.
