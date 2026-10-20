# Cross-Module Synchronization

Spark modules (separate apps with separate RavenDB databases) share data through **replicas**: read-only copies of
another module's documents, declared once in the consuming module, approved by the owning module, delivered by
RavenDB ETL or over Spark messaging, and written back with a per-property compare-and-set. Design and decisions:
[warehouse_picking_PRD.md §4.2](warehouse_picking_PRD.md). Certificates: [guide-replication-mtls.md](guide-replication-mtls.md).

## Overview

| | Consumer (reads) | Owner (is read) |
|---|---|---|
| Declares | a `partial` class with `[Replicated]` | a `Replicate/{Collection}` right for the consumer's module group |
| On startup | posts its replicas to each owner, only when their hashes changed | records one request per new shape; approves at once only in Development |
| Delivery | stores pushes (`/spark/replicas/apply`) or receives ETL loads | ETL task (licence with ETL) or pushes + hourly reconcile (Community) |
| Write-back | a save calls the owner within the request; queued if it does not answer | applies with a per-property compare-and-set in its normal save path |

## Step 1: Configure replication

```json
{
  "Spark": {
    "Replication": {
      "ModuleName": "HR",
      "ModuleUrl": "https://localhost:5005",
      "SparkModulesUrls": [ "http://localhost:8080" ],
      "SparkModulesDatabase": "SparkModules",
      "Transport": "Auto"
    }
  }
}
```

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();       // required: registrations, pushes and queued write-backs ride its queue
    spark.AddReplication();     // binds Spark:Replication; code options are rarely needed
});
```

`Transport` (owner side): `Auto` reads the RavenDB licence (ETL when it has it, HTTP otherwise), `Http`, or `Etl`
(refuses startup on a licence without ETL). Replication registers no hosted service and creates no RavenDB
subscription: it shares messaging's.

## Step 2: Declare a replica (the consumer)

```csharp
// HR.Library: a read-only copy of Fleet's cars. Nothing else declares this shape.
[Replicated(SourceCollection = "Cars", SourceModule = "Fleet", WriteBack = true)]
public partial class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }   // a renamed or nested source path
}
```

- The library references `MintPlayer.Spark.LibraryGenerators` as an analyzer. The generator emits the manifest, the
  ETL script and its hash as constants (`ReplicationManifest`, `ReplicationScript`, `ReplicationHash`, …) and lists
  the class in `[assembly: SparkReplicaTypes]`. SPARK065–SPARK068 report a non-partial class, a bad identifier, a
  path missing on `OriginalType`, and a hand-written member named like a generated one.
- `SourceModule` is optional: a generic library leaves it out and the host names the owner(s) in
  `Spark:Replication:Sources:{TargetCollection}`. Startup refuses a replica with neither.
- `TargetCollection` defaults to the pluralised class name; startup refuses when the store names it differently.
- Every public settable property except `Id` is replicated. Keep consumer state in your own documents keyed by the
  replica id.

## Step 3: Grant and approve (the owner)

The consumer's module group needs `Replicate/{SourceCollection}` in the owner's `security.json`, and the read right
on every source attribute the manifest reads (a field it may not read is refused by name):

```json
{ "key": "…", "resource": "Replicate/Cars", "groupId": "<Module:HR group>" }
```

Replication ships a library layer with the **"Replication requests"** program unit. Bind its slot to the group that
approves:

```json
"bindings": { "replication:approvers": [ "Administrators" ] }
```

A pending request reaches the slot's members by default: each user holding a bound group gets a private
`replication:requests` frame on their open pages and, when MailManager is registered, a mail to a confirmed address;
the request itself waits on "Replication requests". `IReplicationRequestObserver` adds any other channel.

Each new manifest a consumer registers becomes a request (pending first in the list) with a readable diff against
the shape delivered now. `ApproveReplication` deploys it; `RejectReplication` suspends the subscription. A host in
the **Development** environment approves automatically (recorded as `system (Development)`); no build configuration
or setting can. While a newer shape waits, the consumer keeps receiving the approved one.

## How delivery works

- **ETL** (owner licence with ETL): one task per consumer (`spark-etl-{consumer}`) with one transform per approved
  replica, rendered by the owner from the approved manifest, to the database in the consumer's registry entry. An
  unchanged deploy is skipped; a changed registry entry redeploys.
- **HTTP** (Community): a committed save on the owner is captured in the save's own commit and pushed
  (`ReplicaPushMessage`, queue `spark-replication`, retried 1 m / 5 m / 30 m / 2 h for 48 h). The consumer applies a
  push only when its source change vector is newer than what it holds; deletions leave a 7-day tombstone. An hourly
  reconcile (`Spark:Replication:Reconcile:Schedule`) compares the consumer's digest with the owner's documents and
  pushes what is missing or older. Raw session writes on the owner call `IReplicaPublisher.MarkChangedAsync`.
- Consumers react to changes durably with `IReplicaChanged<T>` (HTTP transport).

## Write-back

Only replicas declared with `WriteBack = true` can be edited; others are read-only, and replicas are never created
on the consumer.

1. The save sends the changed properties, each with its **base** (the value the person saw) and its new value, to the
   owner's `/spark/sync/apply` within the request (`Spark:Replication:WriteBack:SynchronousTimeout`, 5 s).
2. The owner compares each listed property's stored value with its base. All equal → saved through its normal path
   (rights, attribute rights, validation, interceptors). Any difference → a conflict naming the properties; nothing is
   applied. Properties the edit does not list never conflict.
3. The answer is the save's: applied → `202 { submitted: { kind: "replication" } }`; conflict → the usual `409`;
   forbidden or invalid → the validation answer. The replica itself changes only when the owner's delivery arrives.
4. When the owner does not answer, the edit is queued in the save's commit (`202`, kind `replication-pending`) and
   retried for 48 hours. A conflict, a refusal or giving up is recorded in the initiator's **"My pending changes"**,
   pushed to their open pages (`replication:outcomes`), and a give-up is mailed when MailManager is registered.
   `RetrySyncAction` sends a given-up change again.

A row with a change waiting carries `metadata.replication`; a second edit of it is refused until the change is
delivered. In Angular: `provideSparkReplication()` (title badge) and `<spark-pending-changes-link />` in the shell
(`@mintplayer/ng-spark/replication`).

## Endpoints

All are module-to-module and require the caller's pinned client certificate (no antiforgery token).

| Endpoint | Side | Purpose |
|---|---|---|
| `POST /spark/replicas/register` | owner | a consumer's replicas: manifest, script, hash per target collection |
| `POST /spark/replicas/apply` | consumer | a push of current documents (or tombstones) with their source change vectors |
| `GET /spark/replicas/digest?module=&collection=&after=&take=` | consumer | the reconcile's view of what the consumer holds |
| `POST /spark/sync/apply` | owner | one write-back: 200 applied, 409 conflict, 403 forbidden, 422 invalid, 400 otherwise |
