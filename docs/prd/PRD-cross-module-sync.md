# PRD: Cross-Module Entity Synchronization (Write-Back)

## Problem Statement

Today, MintPlayer.Spark supports **one-way data replication** between modules using RavenDB ETL tasks. When the Fleet module defines a `Car` entity and the HR module replicates it via `[Replicated]`, ETL continuously pushes `Car` changes from Fleet's database to HR's database. This works well for read-only access.

However, there is no mechanism for a **non-owner module to write back changes** to the owner module. If HR modifies a replicated `Car` record, that change is local to HR's database and will be overwritten the next time ETL runs. There is currently no way for HR to tell Fleet "I updated this Car" or "please delete this Car."

## Goal

Enable non-owner modules to perform Insert, Update, and Delete operations on replicated entities, with changes automatically forwarded to the owner module for processing. The design must require **minimal additional code** from module developers.

## Architecture Overview

```
┌──────────────────┐                          ┌──────────────────┐
│    HR Module      │                          │   Fleet Module   │
│  (non-owner of   │                          │  (owner of Car)  │
│     Car)          │                          │                  │
│                   │   1. SyncAction          │                  │
│  User updates     │ ──────────────────────►  │  POST /spark/    │
│  replicated Car   │   (via message bus       │  sync/apply      │
│                   │    + HTTP POST)           │                  │
│                   │                          │  2. Apply CRUD   │
│                   │                          │  on real entity   │
│                   │   3. ETL pushes          │                  │
│                   │ ◄──────────────────────  │  3. RavenDB ETL  │
│  Local copy       │   updated data back      │  pushes change   │
│  updated via ETL  │                          │  back to HR      │
└──────────────────┘                          └──────────────────┘
```

**Flow:**
1. HR performs a CRUD operation on a replicated `Car` entity
2. The framework detects the entity type has a `[Replicated]` attribute (i.e., it's not owned locally)
3. Instead of saving locally, it creates a `SyncAction` message and sends it via the durable message bus
4. The `SyncActionRecipient` (message bus handler) POSTs the action to the owner module's `/spark/sync/apply` endpoint
5. The owner module receives the request and performs the actual CRUD operation on its local database
6. The existing ETL task automatically propagates the change back to the non-owner module's database

## Detailed Design

### 1. SyncAction Model

```csharp
// MintPlayer.Spark.Replication.Abstractions/Models/SyncAction.cs
public enum SyncActionType
{
    Insert,
    Update,
    Delete
}

public class SyncAction
{
    /// <summary>The type of operation to perform on the owner module.</summary>
    public required SyncActionType ActionType { get; set; }

    /// <summary>The RavenDB collection name (e.g., "Cars").</summary>
    public required string Collection { get; set; }

    /// <summary>
    /// The document ID. Required for Update and Delete.
    /// For Insert, can be null (owner generates the ID).
    /// </summary>
    public string? DocumentId { get; set; }

    /// <summary>
    /// The entity data as a JSON object. Required for Insert and Update.
    /// Null for Delete.
    /// </summary>
    public object? Data { get; set; }
}
```

### 2. SyncActionRequest (HTTP Payload)

```csharp
// MintPlayer.Spark.Replication.Abstractions/Models/SyncActionRequest.cs
public class SyncActionRequest
{
    /// <summary>Name of the module sending the sync action.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The sync actions to apply.</summary>
    public required List<SyncAction> Actions { get; set; }
}
```

### 3. Detecting Replicated Entities at Write Time

The key insight: when a CRUD operation targets an entity whose CLR type has a `[Replicated]` attribute, the framework should intercept and redirect the operation.

**Option A — Intercept in `DatabaseAccess` (Recommended)**

Modify `SavePersistentObjectAsync` and `DeletePersistentObjectAsync` in `DatabaseAccess` to check if the resolved CLR type has `[ReplicatedAttribute]`. If it does:
- Do NOT save/delete locally
- Instead, create a `SyncAction` and dispatch it via `IMessageBus`

This approach is transparent: no changes needed in endpoint handlers, no changes needed in module code.

```csharp
// In DatabaseAccess.SavePersistentObjectAsync:
var entity = entityMapper.ToEntity(persistentObject);
var entityType = entity.GetType();

var replicatedAttr = entityType.GetCustomAttribute<ReplicatedAttribute>();
if (replicatedAttr != null)
{
    // This is a replicated entity — forward to owner module
    await syncActionDispatcher.DispatchAsync(replicatedAttr, new SyncAction
    {
        ActionType = persistentObject.Id == null ? SyncActionType.Insert : SyncActionType.Update,
        Collection = replicatedAttr.SourceCollection ?? Pluralize(entityType.Name),
        DocumentId = persistentObject.Id,
        Data = entity
    });
    return persistentObject;
}

// Otherwise: save locally (existing code)
```

### 4. SyncAction Dispatch (Message Bus)

Sync actions are queued through the existing durable message bus (`IMessageBus`), giving us retry with exponential backoff, dead-lettering, and persistence for free.

**Queue naming strategy**: Each collection gets its own queue — `"spark-sync-{Collection}"` (e.g., `"spark-sync-Cars"`, `"spark-sync-People"`). This ensures a poisoned message for one collection does not block sync actions for other collections. The `MessageProcessor` processes different queues in parallel.

```csharp
// MintPlayer.Spark.Replication/Messages/SyncActionDeploymentMessage.cs
[MessageQueue("spark-sync")] // Base name — overridden per-collection at dispatch time
public class SyncActionDeploymentMessage
{
    public required string OwnerModuleName { get; set; }
    public required string OwnerModuleUrl { get; set; }
    public required SyncActionRequest Request { get; set; }
}
```

The `[MessageQueue]` attribute provides a base name. At dispatch time, the dispatcher appends the collection name to produce the actual queue name (e.g., `"spark-sync-Cars"`). This requires the `IMessageBus.BroadcastAsync` method to accept an optional queue name override, or alternatively the dispatcher sets the queue name on the message before broadcasting.

```csharp
// MintPlayer.Spark.Replication/Services/SyncActionDispatcher.cs
public interface ISyncActionDispatcher
{
    Task DispatchAsync(ReplicatedAttribute replicatedAttr, SyncAction action, string requestingModule);
}
```

The dispatcher:
1. Looks up the owner module's URL from the SparkModules database (same pattern as `EtlScriptDeploymentRecipient`)
2. Creates a `SyncActionDeploymentMessage` and sends it via `IMessageBus.BroadcastAsync()` with queue name `"spark-sync-{Collection}"`
3. The message bus provides retry with exponential backoff (same as ETL deployment)

```csharp
// MintPlayer.Spark.Replication/Messages/SyncActionDeploymentRecipient.cs
// (IRecipient<SyncActionDeploymentMessage>)
// POSTs to {ownerModuleUrl}/spark/sync/apply
```

### 5. Receiving Endpoint (Owner Module)

```csharp
// MintPlayer.Spark.Replication/Endpoints/SyncEndpoints.cs
// POST /spark/sync/apply
```

Mapped via the existing `MapSparkReplication()` extension (add alongside ETL deploy endpoint).

The handler:
1. Deserializes the `SyncActionRequest`
2. For each `SyncAction`:
   - Resolves the CLR entity type from the collection name
   - For **Insert/Update**: deserializes `Data` to the entity type, uses `IPersistentObjectActions<T>.OnSaveAsync()` (preserving lifecycle hooks)
   - For **Delete**: calls `IPersistentObjectActions<T>.OnDeleteAsync()`
3. Returns 200 OK on success, appropriate error codes on failure

This is critical: the owner module processes the sync action through the same `IPersistentObjectActions<T>` pipeline, ensuring all business logic (validation, before/after hooks) runs exactly as if the operation was performed locally.

### 6. Collection-to-Type Resolution

The owner module needs to resolve a RavenDB collection name (e.g., "Cars") back to the CLR entity type (e.g., `Fleet.Entities.Car`).

RavenDB stores collection→type mappings in the document store conventions. We can use `IDocumentStore.Conventions.FindClrType` or maintain a registry built during `CreateSparkIndexes()` / model synchronization. The `IModelLoader` already has all entity type definitions with `ClrType` — we can add a lookup by collection name.

### 7. Changes to Existing Code

| File | Change |
|------|--------|
| `DatabaseAccess.cs` | Check `[Replicated]` attribute in `SavePersistentObjectAsync` and `DeletePersistentObjectAsync`; redirect to `ISyncActionDispatcher` |
| `SparkReplicationExtensions.cs` | Register `ISyncActionDispatcher`, `SyncActionDeploymentRecipient`; add `/spark/sync/apply` endpoint in `MapSparkReplication()` |
| `SparkReplicationOptions.cs` | No changes needed (module name and URL already configured) |

### 8. What Module Developers Need to Do

**Nothing extra.** If a module already has:
- `[Replicated]` entities defined
- `AddSparkReplication()` and `UseSparkReplication()` in startup
- `MapSparkReplication()` for endpoints

Then write-back sync works automatically. The framework:
- Detects replicated entities at write time (via `[Replicated]` attribute)
- Forwards operations to the owner module (via message bus + HTTP)
- Owner module applies changes through the standard actions pipeline
- ETL propagates the result back

### 9. Error Handling and Queue Resilience

A key requirement is that **a single failing sync action must not block the queue**. The design addresses this at multiple levels:

**Retryable vs non-retryable errors:**

The `SyncActionDeploymentRecipient` inspects the HTTP response from the owner module and distinguishes:

| HTTP Status | Classification | Behavior |
|-------------|---------------|----------|
| 2xx | Success | Mark completed |
| 400 Bad Request | **Non-retryable** | Dead-letter immediately (bad data won't become good on retry) |
| 404 Not Found | **Non-retryable** | Dead-letter immediately (document/type doesn't exist on owner) |
| 408/429/5xx | **Retryable** | Retry with exponential backoff |
| Connection refused / timeout | **Retryable** | Retry with exponential backoff (owner module may be down) |

Non-retryable errors are dead-lettered immediately rather than wasting retry attempts. This prevents a validation error from occupying the queue for 5 retry cycles (~1h14m).

**Per-collection queue isolation:**

Each collection has its own message bus queue (`"spark-sync-Cars"`, `"spark-sync-People"`). The `MessageProcessor` already processes different queues in parallel (`Task.WhenAll`). This means:
- A poisoned `Car` sync does not block `Person` syncs
- Within a collection, ordering is preserved (you don't want a delete processed before an update for the same document)

**Existing message bus guarantees (already in place):**
- **Owner module unreachable**: Retries with exponential backoff (5s, 30s, 2m, 10m, 1h)
- **Max retries exceeded**: Dead-lettered after 5 attempts (configurable via `SparkMessagingOptions.MaxAttempts`)
- **Failed messages don't block during backoff**: The `MessageProcessor` query filters out `Failed` messages whose `NextAttemptAtUtc` is in the future, allowing newer messages in the same queue to proceed
- **Dead-lettered messages expire**: Cleaned up after `RetentionDays` (default 7) via RavenDB document expiration
- **Conflict (document modified concurrently)**: RavenDB's optimistic concurrency handles this at the owner. The sync endpoint returns 409, which is retryable
- **ETL propagation delay**: After the owner saves, ETL eventually pushes the update back. This is eventually consistent (typically sub-second with RavenDB ETL)

### 10. API Response to the Caller

When a non-owner module's UI performs a CRUD operation on a replicated entity, the HTTP response should indicate the operation was **accepted for processing** rather than immediately completed:

- Return **202 Accepted** instead of 200/201
- Include a message: `"Operation forwarded to owner module '{moduleName}' for processing"`
- The local copy will be updated when ETL propagates the change back

Alternatively, if synchronous behavior is desired, the `SyncActionDispatcher` could make a direct HTTP call (bypassing the message bus) and return the owner's response. This trades durability for immediacy. This could be a configuration option.

### 11. Security Considerations

- Sync endpoints should validate that the requesting module is a known registered module (check SparkModules database)
- Consider adding a shared secret or module authentication token to `SyncActionRequest`
- Rate limiting on the `/spark/sync/apply` endpoint to prevent abuse

### 12. New Projects/Files

| File | Project | Purpose |
|------|---------|---------|
| `SyncAction.cs` | `Replication.Abstractions` | Model: action type, collection, document ID, data |
| `SyncActionRequest.cs` | `Replication.Abstractions` | HTTP payload model |
| `SyncActionDeploymentMessage.cs` | `Replication` | Message bus message |
| `SyncActionDeploymentRecipient.cs` | `Replication` | Message bus handler → HTTP POST |
| `SyncActionDispatcher.cs` | `Replication` | Orchestrates: build request, resolve owner URL, send message |
| `SyncEndpoints.cs` | `Replication` | `POST /spark/sync/apply` handler |

No new projects needed — all new code goes into the existing `MintPlayer.Spark.Replication` and `MintPlayer.Spark.Replication.Abstractions` projects, plus a small modification to `MintPlayer.Spark` (DatabaseAccess).

### 13. Demo App Updates

The HR and Fleet demo apps already have bidirectional `[Replicated]` entities. The only changes needed are to verify that the existing startup registrations cover the new sync functionality (they should, since `AddSparkReplication` and `MapSparkReplication` will register the new services and endpoints automatically).

**No code changes in demo `Program.cs` files.** The new `ISyncActionDispatcher` and `SyncActionDeploymentRecipient` are registered inside `AddSparkReplication()`, and the `/spark/sync/apply` endpoint is mapped inside `MapSparkReplication()`.

**Verification scenario** (HR ↔ Fleet):

1. **HR user updates a Car's color** via the HR module's UI
2. HR's `DatabaseAccess.SavePersistentObjectAsync` detects `Car` has `[Replicated(SourceModule = "Fleet")]`
3. HR creates a `SyncAction { ActionType = Update, Collection = "Cars", DocumentId = "Cars/abc-123", Data = { Color = "Red", ... } }`
4. Message bus delivers it via queue `"spark-sync-Cars"` → `SyncActionDeploymentRecipient` POSTs to Fleet's `/spark/sync/apply`
5. Fleet's handler deserializes, resolves `Car` type, calls `OnSaveAsync` → saved to Fleet's database
6. Fleet's RavenDB ETL task pushes the updated Car back to HR's database
7. HR's local Car copy now reflects the change

## Out of Scope

- **Conflict resolution policies** (last-write-wins is the default via RavenDB)
- **Partial updates / field-level sync** (full entity replacement for simplicity)
- **Cross-module event notifications** (the message bus is local; cross-module eventing is a future feature)
- **Bidirectional ETL write-back** (this uses application-level HTTP, not RavenDB-level features)
- **Batch sync operations** (single-entity operations per SyncAction; batching can be added later)

## Success Criteria

1. A non-owner module can Insert, Update, and Delete replicated entities with zero additional code beyond what exists today
2. Changes are reliably forwarded to the owner module via the durable message bus
3. The owner module's business logic (validation, lifecycle hooks) runs for all sync operations
4. ETL propagates changes back to the non-owner module automatically
5. Failed sync operations are retried with exponential backoff and eventually dead-lettered
