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
2. **ETL replication** -- an owner module publishes **contracts**; a consumer module declares `[Replicated]` replica types bound to a contract, and on startup asks the owner for them via the durable message bus. The owner generates every ETL transform itself
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

## Step 2: Publish a Contract (the owner)

The module that owns the data decides what leaves it. It publishes a **contract**: a permanent
lower-kebab name, a version, the source type, and a C# projection into the record consumers
receive.

```csharp
// Fleet.Library/Contracts/CarV1.cs
public sealed record CarV1(string LicensePlate, string Model, int Year, Color? Color);

// Fleet's Program.cs
spark.AddReplication(opt =>
{
    opt.Publish<Car, CarV1>("cars", 1,
        car => new CarV1(car.LicensePlate, car.Model, car.Year, car.Color));
});
```

The owner turns the projection into its RavenDB ETL transform at startup. For the example above
it deploys:

```js
loadToCars({
    LicensePlate: this.LicensePlate,
    Model: this.Model,
    Year: this.Year,
    Color: this.Color
});
```

**What a projection may contain.** Only what JavaScript computes identically:

| Accepted | Example |
|---|---|
| A member initializer of the contract record | `c => new CarV1 { Plate = c.LicensePlate }` |
| A positional record constructor; each argument initializes the property of the same name | `c => new CarV1(c.LicensePlate, c.Model, c.Year, c.Color)` |
| Renames | `Plate = c.LicensePlate` → `Plate: this.LicensePlate` |
| Nested paths, read null-safely | `Street = c.Address.Street` → `(this.Address == null ? null : this.Address.Street)` |
| Lifting to `Nullable<T>`, or an upcast | `Year = c.Year` into an `int?` |

Anything else is **refused at startup**, with a message naming the contract and the member: method
calls, arithmetic, string concatenation, conditionals, constants, captured variables, and
`Nullable.Value`. Store a computed value on the source document and select it instead. An `Id`
member is skipped: the document id travels with the ETL itself.

Names (`cars`) are lower-kebab and permanent. Versions run side by side: publish `("cars", 2, …)`
next to version 1 when the record changes, and move consumers over one at a time.

## Step 3: Declare Replica Types (the consumer)

In the consumer module, a class decorated with `[Replicated]` binds to the owner's contract. It has
the contract record's shape: when the owner ships its contract records in a package, the replica
can reuse the record type itself.

```csharp
using MintPlayer.Spark.Replication.Abstractions;

namespace HR.Replicated;

[Replicated(SourceModule = "Fleet", Contract = "cars", SourceCollection = "Cars")]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public Color? Color { get; set; }
}
```

The `[Replicated]` attribute tells the framework:
- `SourceModule` -- which module owns the original data
- `Contract` and `Version` (default 1) -- which published contract this type reads
- `SourceCollection` / `OriginalType` -- the owner's collection that write-back (sync actions) targets

The consumer sends the owner only `(contract, version, collection)`. It never sends JavaScript: the
fields that leave the owner are the owner's choice, and nothing a consumer writes runs on the
owner's database.

## Step 4: Wire Up the Pipeline

In each module's `Program.cs`, opt in on the builder. There are no separate startup or
endpoint-mapping calls: `AddReplication()` registers the module, requests its contracts from their
owners, and exposes the endpoints as part of `UseSpark()` / `MapSpark()`.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddSparkDiscovered();
    spark.AddMessaging();
    spark.AddReplication(options => { /* … */ });
});

var app = builder.Build();

app.UseSpark();          // also translates published contracts, registers the module and requests contracts

app.UseEndpoints(endpoints => endpoints.MapSpark());   // also exposes /spark/etl/deploy and /spark/sync/apply
```

Both the source and consumer modules need `AddReplication()` -- the source receives ETL deployment
requests, and both modules can receive sync action requests.

## How ETL Replication Works

When Fleet starts, it translates every contract it publishes. A projection the ETL transform cannot
express stops startup here, not at a consumer's first deployment.

When the HR module starts:

1. It registers itself as `moduleInformations/HR` in the shared SparkModules database, with its database name and URLs
2. It scans assemblies for `[Replicated]` types and finds `Car`: contract `cars` v1 from `Fleet`, stored in HR's `Cars` collection
3. It broadcasts an `EtlScriptDeploymentMessage` via the durable message bus, carrying `{ requestingModule: "HR", contracts: [{ contract: "cars", version: 1, targetCollection: "Cars" }] }`
4. The message bus recipient looks up Fleet's URL in SparkModules and POSTs the request to Fleet's `POST /spark/etl/deploy` endpoint
5. Fleet's endpoint checks, in order:
   - HR's client certificate (mTLS, see [guide-replication-mtls.md](guide-replication-mtls.md));
   - that `targetCollection` is a plain identifier (it becomes part of `loadTo{Collection}`);
   - that Fleet publishes `cars` v1 (else **404**);
   - that `Module:HR` holds `Replicate/{collection}` on the contract's **source** collection, which Fleet takes from its own contract, never from the request (else **403**);
   - that HR's registry entry has a database (else **409**).
6. Fleet creates or updates one RavenDB ETL task per consumer, `spark-etl-HR`:
   - its connection string points at the database and URLs in **HR's SparkModules entry**. The request cannot name a destination;
   - its transforms are the scripts Fleet generated from its own contracts.
7. RavenDB in Fleet now continuously pushes the projected Car documents to HR's `Cars` collection

If Fleet is not yet running when HR starts, the message bus retries automatically with backoff until Fleet becomes available.

**Refusals that are not retried.** A 400 (malformed request), 404 (a contract the owner does not
publish) or 409 is the owner's settled answer, so the message is dead-lettered once with the
owner's reason. 401, 403 and 5xx are retried: registration and grants can still land.

**The licence.** RavenDB ETL needs a licence that includes it; **Community does not**. Before it
deploys, the owner reads `/license/status` (`HasRavenEtl`). Without ETL it answers **409** with
`ETL_NOT_LICENSED` and logs why, instead of failing on the deployment with a generic 500. Local
development and CI hold a Developer licence, which has ETL, so a Community production server is
where this shows up first.

> **Grants still name collections in this version.** The owner checks `Replicate/{SourceCollection}`
> (`Replicate/Cars`), with the collection taken from its own contract. Contract-named grants
> (`Replicate/cars`), the HTTP transport for licences without ETL, and removing ETL tasks when a
> consumer withdraws are part of the warehouse replication redesign (`docs/warehouse_picking_PRD.md` §4.2).

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
    public string? InitiatorId { get; set; }                 // The editing user's id (#271), for stamping
}
```

`InitiatorId` is set from the sending module's `ISparkCurrentUser` for writes that go through the
interceptors. The owner stamps `ModifiedBy` (and `CreatedBy` on an insert) of an audited entity with it.
Without it the owner would keep the previous editor, and replication would then copy that stale value
back over the replica's own stamp. See [guide-auditing.md](guide-auditing.md).

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

The architecture is fully symmetrical. Each module can publish contracts and declare replicas of the other's:

```csharp
// In HR's Program.cs: HR owns people
opt.Publish<Person, PersonV1>("people", 1,
    person => new PersonV1(person.FirstName, person.LastName, person.Email));

// In Fleet.Library/Replicated/Person.cs
[Replicated(SourceModule = "HR", Contract = "people", SourceCollection = "People")]
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

Receives contract requests from other modules. Generates the transforms from this module's own
contracts, and creates or updates the requesting module's RavenDB ETL task and connection string.

**Request body:**

```json
{
  "requestingModule": "HR",
  "contracts": [ { "contract": "cars", "version": 1, "targetCollection": "Cars" } ]
}
```

There is no script and no target database or URL in the body: the transform comes from the
owner's contract, the destination from the requesting module's SparkModules entry.

**Response:** `200 OK` with `{ success, tasksCreated, tasksUpdated }`; `400` for a malformed body
or target collection; `401`/`403` for the certificate or a missing `Replicate` grant; `404` for a
contract this module does not publish; `409` when the requesting module has no registered database
or this server's licence has no ETL (`"error": "ETL_NOT_LICENSED"`).

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
- **Module not registered**: throws a retryable `HttpRequestException` so delivery is retried until the owner module registers

### How a retry is actually redelivered

Worth understanding, because the obvious mental model is wrong and it caused a real bug ([#258](https://github.com/MintPlayer/MintPlayer.Spark/issues/258)).

A RavenDB subscription is **change-vector-driven**: a document is tested against the subscription query only when it is *written*. Time passing is not a write. So a backoff elapsing cannot, by itself, make a parked action visible again — and the query cannot ask whether the backoff has elapsed either, because `now()` is not evaluable in a subscription expression (RavenDB 7.2.1 silently answered `false`; 7.2.2+ rejects the query outright).

Redelivery therefore needs a component that *can* evaluate time:

1. On a retryable failure the worker parks the action: `Status = Pending`, `NextAttemptAtUtc` set to the next attempt, `WakeUp = false`. The subscription query does not match it.
2. `SyncActionRetrySweeper` wakes every `FallbackPollInterval` (default **30 seconds**), finds Pending actions whose `NextAttemptAtUtc` has passed, and patches `WakeUp = true`. That patch is also the write that makes RavenDB re-evaluate the document.
3. The subscription now matches, the action is redelivered, and the worker clears `WakeUp` on pickup.

Two consequences for operators:

- **Retry granularity is `FallbackPollInterval`.** A backoff expiring just after a sweep waits up to one more interval. Tune it via `Spark:Replication:FallbackPollInterval`.
- **First delivery is unaffected** — a new action is delivered immediately by the subscription and never waits for a sweep.

If you are writing your own subscription worker with a time-based pickup condition, use this pattern rather than a time comparison in the query.

## Complete Example

See the Fleet and HR demo apps for a working example:

- `apps/Fleet/Fleet.Library/Contracts/CarV1.cs` and `apps/Fleet/Fleet/Program.cs` -- the `cars` contract Fleet publishes
- `apps/HR/HR.Library/Replicated/Car.cs` -- the replica type bound to it with `[Replicated]`
- `apps/Fleet/Fleet.Library/Replicated/Person.cs` -- bidirectional replication (HR publishes `people` and `companies`)
- `apps/HR/HR/Program.cs` -- replication service registration and published contracts
- `MintPlayer.Spark.Replication/Services/EtlTransformGenerator.cs` -- projection → ETL transform, and what it refuses
- `MintPlayer.Spark.Replication/Services/SyncActionInterceptor.cs` -- write-back dispatcher, called after every committed write of a `[Replicated]` type by `ReplicationInterceptor.cs` (a persistence interceptor, #482)
- `MintPlayer.Spark.Replication/Workers/SyncActionSubscriptionWorker.cs` -- sync action delivery
