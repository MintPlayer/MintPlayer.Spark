# Cross-Module Synchronization

Spark supports replicating data between independent modules using RavenDB ETL tasks. A **Fleet** module can push Cars to an **HR** module automatically, and HR can write changes back. This guide covers both directions: owner-to-consumer ETL replication and non-owner write-back via sync actions.

## Overview

Cross-module synchronization involves two packages:

| Package | Purpose |
|---|---|
| `MintPlayer.Spark.Replication.Abstractions` | Shared types: `[Replicated]` attribute, `SyncAction`, `ModuleInformation` |
| `MintPlayer.Spark.Replication` | Implementation: module registration, ETL deployment, sync action processing |

The system uses three mechanisms:

1. **Module registration** -- each module registers itself (name, URL, database) in a shared `SparkModules` RavenDB database on startup
2. **ETL replication** -- a consumer module declares `[Replicated]` attributes; on startup the framework sends ETL scripts to the source module via the durable message bus
3. **Sync actions** -- when a non-owner module edits a replicated entity, the change is forwarded to the owner module via a `SparkSyncAction` document and subscription worker

## Step 1: Configure Replication Options

In each module's `Program.cs`, register the replication services with the module's identity:

```csharp
// HR module's Program.cs
builder.Services.AddSparkReplication(opt =>
{
    opt.ModuleName = "HR";
    opt.ModuleUrl = "https://localhost:5002";
    opt.SparkModulesUrls = ["http://localhost:8080"];
    opt.SparkModulesDatabase = "SparkModules";
    opt.AssembliesToScan = [typeof(HR.Replicated.Car).Assembly];
});
```

All modules must point to the same shared `SparkModules` database. `AssembliesToScan` tells the framework where to look for `[Replicated]` attributes.

## Step 2: Declare Replicated Entities

In the consumer module, create a class decorated with `[Replicated]` that describes which fields to pull from the source:

```csharp
using MintPlayer.Spark.Replication.Abstractions;

namespace HR.Replicated;

[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            '@metadata': {
                '@collection': 'Cars'
            }
        });
    """)]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
}
```

The `[Replicated]` attribute tells the framework:
- `SourceModule` -- which module owns the original data
- `SourceCollection` -- the RavenDB collection in the source database
- `EtlScript` -- a JavaScript ETL transformation script using RavenDB ETL syntax

The consumer's C# class only needs the properties it cares about -- the ETL script controls what is projected.

## Step 3: Wire Up the Pipeline

In each module's `Program.cs`, add the startup calls and endpoint mappings:

```csharp
var app = builder.Build();

app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();     // Register module + deploy ETL scripts

// ... in endpoint mapping:
endpoints.MapSpark();
endpoints.MapSparkReplication();   // Expose /spark/etl/deploy and /spark/sync/apply
```

Both the source and consumer modules need `MapSparkReplication()` -- the source receives ETL deployment requests, and both modules can receive sync action requests.

## How ETL Replication Works

When the HR module starts:

1. It registers itself as `moduleInformations/HR` in the shared SparkModules database
2. It scans assemblies for `[Replicated]` attributes and finds `Car` with `SourceModule = "Fleet"`
3. It looks up Fleet's URL from the SparkModules database
4. It broadcasts an `EtlScriptDeploymentMessage` via the durable message bus
5. The message bus recipient POSTs the ETL scripts to Fleet's `POST /spark/etl/deploy` endpoint
6. Fleet's endpoint creates a RavenDB ETL task:
   - A connection string pointing to HR's database
   - The JavaScript transformation script from the `[Replicated]` attribute
7. RavenDB in Fleet now continuously pushes transformed Car documents to HR's database

If Fleet is not yet running when HR starts, the message bus retries automatically with exponential backoff until Fleet becomes available.

## Non-Owner Write-Back (Sync Actions)

When a user edits a replicated entity in the consumer module (e.g., editing a Car in HR), the framework intercepts the write and forwards it to the owner module:

1. `SyncActionInterceptor` detects that the entity type has a `[Replicated]` attribute
2. Instead of saving locally, it creates a `SparkSyncAction` document in RavenDB with the changed data
3. `SyncActionSubscriptionWorker` picks up the document and POSTs it to the owner module's `POST /spark/sync/apply` endpoint
4. The owner module applies the changes through its normal actions pipeline
5. Because the owner's data changed, the ETL task pushes the updated document back to the consumer

### Partial Property Updates

The sync action only sends the properties that exist on the replicated entity type. This prevents a consumer with a subset of fields from overwriting properties it does not know about:

```csharp
// HR's Car only has: LicensePlate, Model, Year, Color
// Fleet's Car also has: Status, Brand, InteriorColor, Mileage

// When HR updates a Car, only LicensePlate, Model, Year, and Color
// are sent in the sync action -- Fleet's other properties are untouched.
```

The `Properties` array on each `SyncAction` lists the field names to merge. The owner module performs a partial update, only changing the listed properties.

### Change Tracking

When editing through the Spark UI, the framework uses `IsValueChanged` metadata on each attribute to determine which properties actually changed, sending only those in the sync action. When saving programmatically (without PersistentObject metadata), all properties from the replicated type are sent.

## The SyncAction Model

```csharp
public class SyncAction
{
    public required SyncActionType ActionType { get; set; }  // Insert, Update, Delete
    public required string Collection { get; set; }          // e.g., "Cars"
    public string? DocumentId { get; set; }                  // Required for Update/Delete
    public Dictionary<string, object?>? Data { get; set; }   // Property values
    public string[]? Properties { get; set; }                // Properties to merge (partial update)
}
```

For type-safe construction, use `SyncAction<T>` and call `ToTransport()`:

```csharp
var action = new SyncAction<Car>
{
    ActionType = SyncActionType.Update,
    Collection = "Cars",
    DocumentId = car.Id,
    Data = car,
}.ToTransport();
```

## Bidirectional Replication

The architecture is fully symmetrical. Both modules can declare `[Replicated]` attributes pointing to each other:

```csharp
// In Fleet.Library/Replicated/Person.cs
[Replicated(
    SourceModule = "HR",
    SourceCollection = "People",
    EtlScript = """
        loadToPeople({
            FirstName: this.FirstName,
            LastName: this.LastName,
            Email: this.Email,
            '@metadata': {
                '@collection': 'People'
            }
        });
    """)]
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
}
```

In this setup, Fleet replicates People from HR, and HR replicates Cars from Fleet. Each module can read and write-back the replicated entities.

## API Endpoints

The replication package maps two endpoints:

### POST /spark/etl/deploy

Receives ETL script deployment requests from other modules. Creates or updates RavenDB ETL tasks and connection strings.

### POST /spark/sync/apply

Receives sync action requests from non-owner modules. Applies Insert, Update, or Delete operations on locally owned entities through the normal actions pipeline.

**Request body:**

```json
{
  "requestingModule": "HR",
  "actions": [
    {
      "actionType": "Update",
      "collection": "Cars",
      "documentId": "Cars/123-A",
      "data": { "LicensePlate": "ABC-123", "Color": "Blue" },
      "properties": ["LicensePlate", "Color"]
    }
  ]
}
```

**Response:** `200 OK` when all actions succeed, `207 Multi-Status` for partial success.

## Retry and Error Handling

Sync actions use the `SyncActionSubscriptionWorker` with built-in retry logic:

- **Retryable errors** (5xx, connection refused): the document stays as `Pending` and is retried with exponential backoff
- **Non-retryable errors** (400, 404): the document is marked `Failed` immediately
- **Module not registered**: throws a retryable `HttpRequestException` so the message bus retries until the owner module registers

## Complete Example

See the Fleet and HR demo apps for a working example:

- `Demo/HR/HR.Library/Replicated/Car.cs` -- replicated entity with `[Replicated]` attribute
- `Demo/Fleet/Fleet.Library/Replicated/Person.cs` -- bidirectional replication
- `Demo/HR/HR/Program.cs` -- replication service registration and startup
- `MintPlayer.Spark.Replication/Services/SyncActionInterceptor.cs` -- write-back interceptor
- `MintPlayer.Spark.Replication/Workers/SyncActionSubscriptionWorker.cs` -- sync action delivery
