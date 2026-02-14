# Plan: Refactor Actions Pipeline to PersistentObject-Based

## Problem

Today in Spark, the information flow is:

```
Frontend sends PersistentObject (with rich attribute metadata)
  -> DatabaseAccess.SavePersistentObjectAsync()
    -> EntityMapper.ToEntity()        <-- ALL METADATA LOST HERE
      -> Actions.OnSaveAsync(entity)  <-- receives bare entity, no change info
        -> RavenDB save
```

`EntityMapper.ToEntity()` at `DatabaseAccess.cs:142` discards everything except property values.
Actions classes never see which attributes changed, and can't do attribute-level business logic.

## What Vidyano Does Differently

In the Fleet project (`C:\Repos\Fleet`), the save pipeline is:

```
OnSave(PersistentObject obj)           <-- receives full PO with IsValueChanged
  -> UpdateEntity(obj, entity)         <-- maps PO values to entity
    -> CheckRules(obj, type, entity)   <-- validation with full context
      -> PrePersistChanges(obj, entity)<-- side effects with full context
        -> PersistToContext            <-- RavenDB save
          -> PreClient(obj)            <-- configure UI response
```

Key differences:
- Actions receive `PersistentObject`, not `T` entity
- `PersistentObjectAttribute.IsValueChanged` tracks per-attribute changes
- Entity mapping happens INSIDE the pipeline (step 2), not before it
- Actions have access to both PO (new values + metadata) and entity (old values from DB)

## How This Connects to SyncActions

If `PersistentObject` carries `IsValueChanged`:

1. **Frontend -> Middleware**: The frontend already knows which attributes changed. We add `IsValueChanged` to `PersistentObjectAttribute`.

2. **Middleware -> Actions**: `DatabaseAccess.SavePersistentObjectAsync` passes the PO directly to the actions pipeline instead of converting to entity first. Actions can inspect `IsValueChanged`.

3. **Actions -> SyncAction interceptor**: When the interceptor detects a replicated entity, it reads `IsValueChanged` from the PO's attributes to build the `Properties` array -- only truly changed properties, not all replicated properties.

4. **SyncAction -> Owner module**: The owner receives a SyncAction with only the changed properties. The handler constructs a PO, marks those attributes as changed, and runs it through the owner's actions pipeline -- where the owner's actions also see `IsValueChanged`.

## Implementation Steps

### Step 1: Add `IsValueChanged` to `PersistentObjectAttribute`

File: `MintPlayer.Spark.Abstractions/PersistentObject.cs`

```csharp
public class PersistentObjectAttribute
{
    // ... existing properties ...
    public bool IsValueChanged { get; set; }
}
```

### Step 2: Change `IPersistentObjectActions<T>` to receive `PersistentObject`

File: `MintPlayer.Spark/Actions/IPersistentObjectActions.cs`

Breaking change. The interface becomes:

```csharp
public interface IPersistentObjectActions<T> where T : class
{
    Task<IEnumerable<T>> OnQueryAsync(IAsyncDocumentSession session);
    Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id);
    Task<T> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj);       // PO instead of T
    Task OnDeleteAsync(IAsyncDocumentSession session, string id);
    Task OnBeforeSaveAsync(PersistentObject obj, T entity);    // both PO and entity
    Task OnAfterSaveAsync(PersistentObject obj, T entity);     // both PO and entity
    Task OnBeforeDeleteAsync(T entity);                         // unchanged (no PO on delete)
}
```

### Step 3: Move entity mapping inside `DefaultPersistentObjectActions`

File: `MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs`

```csharp
public virtual async Task<T> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
{
    var entity = entityMapper.ToEntity<T>(obj);    // mapping moves here
    await OnBeforeSaveAsync(obj, entity);
    await session.StoreAsync(entity);
    await session.SaveChangesAsync();
    await OnAfterSaveAsync(obj, entity);
    return entity;
}
```

`DefaultPersistentObjectActions<T>` needs `IEntityMapper` injected. Since it's instantiated
by `ActionsResolver` (sometimes via `new DefaultPersistentObjectActions<T>()`), either:
- Resolve `IEntityMapper` from DI in `ActionsResolver` and pass it to the constructor
- Or resolve from `IServiceProvider` inside the actions class

### Step 4: Update `DatabaseAccess.SavePersistentObjectAsync`

File: `MintPlayer.Spark/Services/DatabaseAccess.cs`

Stop calling `entityMapper.ToEntity()`. Pass PO directly to actions:

```csharp
public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject po)
{
    var entityTypeDefinition = modelLoader.GetEntityType(po.ObjectTypeId);
    var entityType = ResolveType(entityTypeDefinition.ClrType);

    // Check replication interceptor (now receives PO with IsValueChanged)
    var interceptor = serviceProvider.GetService(typeof(ISyncActionInterceptor)) as ISyncActionInterceptor;
    if (interceptor != null && interceptor.IsReplicated(entityType))
    {
        await interceptor.HandleSaveAsync(po);  // can read IsValueChanged
        return po;
    }

    using var session = documentStore.OpenAsyncSession();

    // Pass PO directly to actions -- entity mapping happens inside
    var savedEntity = await SaveEntityViaActionsAsync(session, entityType, po);

    var idProperty = entityType.GetProperty("Id", ...);
    po.Id = idProperty?.GetValue(savedEntity)?.ToString();
    return po;
}
```

`SaveEntityViaActionsAsync` changes from `(session, entityType, entity)` to
`(session, entityType, persistentObject)` and calls `actions.OnSaveAsync(session, po)`.

### Step 5: Update `ISyncActionInterceptor` and `SyncActionInterceptor`

Files:
- `MintPlayer.Spark.Abstractions/ISyncActionInterceptor.cs`
- `MintPlayer.Spark.Replication/Services/SyncActionInterceptor.cs`

```csharp
public interface ISyncActionInterceptor
{
    bool IsReplicated(Type entityType);
    Task HandleSaveAsync(PersistentObject po);          // receives PO instead of (object, string?)
    Task HandleDeleteAsync(Type entityType, string documentId);  // unchanged
}
```

The interceptor reads `IsValueChanged` to determine which properties to sync:

```csharp
public async Task HandleSaveAsync(PersistentObject po)
{
    var entityType = ResolveEntityType(po.ObjectTypeId);
    var attr = GetReplicatedAttribute(entityType);

    var changedProperties = po.Attributes
        .Where(a => a.IsValueChanged)
        .Select(a => a.Name)
        .ToArray();

    var syncAction = new SyncAction
    {
        ActionType = po.Id == null ? SyncActionType.Insert : SyncActionType.Update,
        Collection = attr.SourceCollection ?? InferCollectionName(...),
        DocumentId = po.Id,
        Data = SerializePoAttributesToJson(po, changedProperties),
        Properties = changedProperties,
    };

    await DispatchAsync(attr.SourceModule, collection, syncAction);
}
```

### Step 6: Update `SyncActionHandler`

File: `MintPlayer.Spark/Services/SyncActionHandler.cs`

Construct a `PersistentObject` from the incoming sync action data, mark the specified
properties as `IsValueChanged`, and pass it through the actions pipeline:

```csharp
public async Task<string?> HandleSaveAsync(string collection, string? documentId,
    JsonElement data, string[]? properties)
{
    var entityType = ResolveEntityType(collection);
    var entityTypeDef = FindEntityTypeDefinition(collection);

    // Build PersistentObject from the sync action data
    var po = BuildPersistentObject(entityTypeDef, documentId, data, properties);

    // Run through actions pipeline (which now receives PO)
    using var session = documentStore.OpenAsyncSession();
    var savedEntity = await SaveEntityViaActionsAsync(session, entityType, po);

    // Extract ID
    return entityType.GetProperty("Id")?.GetValue(savedEntity)?.ToString();
}

private PersistentObject BuildPersistentObject(EntityTypeDefinition typeDef,
    string? documentId, JsonElement data, string[]? properties)
{
    var po = new PersistentObject
    {
        Id = documentId,
        ObjectTypeId = typeDef.Id,
        Name = typeDef.Name,
        Attributes = typeDef.Attributes.Select(attrDef =>
        {
            var hasValue = data.TryGetProperty(attrDef.Name, out var jsonValue);
            var isChanged = properties?.Contains(attrDef.Name) ?? hasValue;

            return new PersistentObjectAttribute
            {
                Name = attrDef.Name,
                Value = hasValue ? DeserializeJsonValue(jsonValue, attrDef.DataType) : null,
                IsValueChanged = isChanged,
                DataType = attrDef.DataType,
                // ... other metadata from attrDef
            };
        }).ToArray()
    };
    return po;
}
```

### Step 7: Update `ActionsResolver` reflection calls

File: `MintPlayer.Spark/Services/ActionsResolver.cs`

The `ResolveForType(Type)` method uses `MakeGenericMethod` to call `OnSaveAsync`.
Update the reflected method signatures to match the new parameters.

Also in `DatabaseAccess.cs`, the helper methods `SaveEntityViaActionsAsync` and
related methods need to pass `PersistentObject` instead of entity.

### Step 8: Update demo `PersonActions`

File: `Demo/DemoApp/DemoApp/Actions/PersonActions.cs`

```csharp
// Before (current):
public override Task OnBeforeSaveAsync(Person entity) { ... }
public override Task OnAfterSaveAsync(Person entity) { ... }

// After:
public override Task OnBeforeSaveAsync(PersistentObject obj, Person entity) { ... }
public override Task OnAfterSaveAsync(PersistentObject obj, Person entity) { ... }
```

## Cascading Changes Summary

| File | Change |
|------|--------|
| `PersistentObject.cs` (Abstractions) | Add `IsValueChanged` to `PersistentObjectAttribute` |
| `IPersistentObjectActions.cs` | `OnSaveAsync`, `OnBeforeSaveAsync`, `OnAfterSaveAsync` receive PO |
| `DefaultPersistentObjectActions.cs` | Entity mapping moves inside; needs `IEntityMapper` |
| `DatabaseAccess.cs` | Stop mapping to entity; pass PO to actions |
| `ActionsResolver.cs` | Update reflection calls for new method signatures |
| `ISyncActionInterceptor.cs` (Abstractions) | `HandleSaveAsync(PersistentObject)` instead of `(object, string?)` |
| `SyncActionInterceptor.cs` (Replication) | Accept PO, read `IsValueChanged` for property filtering |
| `SyncActionHandler.cs` (Spark) | Construct PO from JSON + properties, pass to actions pipeline |
| `PersonActions.cs` (Demo) | Update signature to `OnBeforeSaveAsync(PersistentObject, Person)` |
| `EntityMapper.cs` | May need `ToEntity<T>(PersistentObject)` generic variant |

## What Stays the Same

- `PersistentObject` model structure (just adds `IsValueChanged` to attribute)
- `EntityTypeDefinition` / model JSON files
- RavenDB storage and conventions
- ETL replication
- Message bus infrastructure
- REST endpoints (they already work with PersistentObject)
- Validation (already works with PersistentObject)
- `OnDeleteAsync`, `OnBeforeDeleteAsync` signatures (no PO on delete path)
- `OnQueryAsync`, `OnLoadAsync` signatures
