# Spark 11.0.0-preview.100 — audit, soft-delete and moderation members are generated (#271)

**Packages:** `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.SourceGenerators`,
`MintPlayer.Spark.LibraryGenerators`, `MintPlayer.Spark.History`, `MintPlayer.Spark.History.Abstractions`, `MintPlayer.Spark.SoftDelete` (README only),
`MintPlayer.Spark.Replication`, `MintPlayer.Spark.Replication.Abstractions` and
`MintPlayer.Spark.AllFeatures` → `11.0.0-preview.100`. No npm package changed. The majors do not move:
the packages still target .NET 11 and Angular 22.

An entity now declares only the interface: `public partial class Invoice : IAuditable, ISoftDeletable { }`.
The generator writes `CreatedBy`, `CreatedAt`, `ModifiedBy`, `ModifiedAt`, `IsDeleted`, … into a partial
half, read-only in the model, with the user ids as references to `SparkUser`.

Guide: [guide-auditing.md](guide-auditing.md). Decisions and evidence:
[audit_fields_PRD.md](audit_fields_PRD.md) (§7 decisions, §9 spike results) and
[audit_fields_plan.md](audit_fields_plan.md).

---

## ⚠️ Breaking changes

### 1. `IAuditable` is now `IAuditCreated` + `IAuditModified`

`IAuditable` keeps exactly its four members, but now inherits them from two new interfaces:
`IAuditCreated` (`CreatedBy`, `CreatedAt`) and `IAuditModified` (`ModifiedBy`, `ModifiedAt`). A class
that implements the members implicitly (`public string? CreatedBy { get; set; }`) compiles unchanged.
A class that implemented one **explicitly** (`string? IAuditable.CreatedBy`) does not compile, and would
never have been stored by RavenDB anyway: write it as a public property.

### 2. `AddHistory()` refuses audit members RavenDB would not store

`UseSpark()` now refuses to start when a model type's `IAuditCreated`/`IAuditModified` member is not a
public read/write property of the interface's type, as `AddSoftDelete()` already did for
`ISoftDeletable`.

### 3. `ISyncActionHandler.HandleSaveAsync` gains an optional `initiatorId`

The new parameter is optional. Only a hand-written `ISyncActionHandler` implementation needs to add it.

---

## New

- **Generated members (#271).** `MintPlayer.Spark.LibraryGenerators` fills in the undeclared members of
  `IAuditCreated`, `IAuditModified`, `ISoftDeletable` and `IModeratable` on a `partial` type:
  - A member you declare yourself, including an explicit implementation, is left alone.
  - A contract a base type already implements is skipped.
  - Records, generic and nested types are reopened correctly.
- **`[ReadOnly(true)]` is honoured by synchronization.** A property marked with
  `System.ComponentModel.ReadOnlyAttribute(true)` is created read-only, not required, and on the object
  page only. The generator marks every member it emits this way. Creation only: your later edits to the
  model stand.
- **`[Reference(typeof(SparkUser))]` on generated user ids** when the library references
  `MintPlayer.Spark.Authorization.Abstractions`. Otherwise they are plain strings.
- **SPARK038** (Error, with a code fix): the type, or a type containing it, must be `partial`.
- **SPARK039** (Warning): an application's entity implements a stamped interface whose package
  (`History`, `SoftDelete`, `Moderation`) the application does not reference.
- **SPARK040** (Info): user ids were generated without `[Reference]`, and why.

## Fixed

- **A replica's write-back lost its editor (F2).** The owner applies a write-back under the replica
  module's certificate, which has no user id, and History did not stamp a `Sync` write at all. So the
  owner kept the previous `ModifiedBy`, and replication then copied that stale value back over the
  replica's own stamp. `SyncAction` now carries `InitiatorId`. The owner exposes it as
  `ISparkSyncInitiator`, and History stamps the `Sync` write with it. A replica on an older version
  sends none and is treated as before.
- **`[ValueObject] record` failed to compile (F1).** The row-key half was always written as
  `partial class`, which a record rejects with CS0261.

## Apps

- **QnA:** `Question` and `Answer` declare only `IModeratable, ISoftDeletable, IAuditable`. The model
  diff is exactly `AuthorId` and `DeletedBy` becoming `SparkUser` references.
- **Fleet:** `Car : IAuditCreated` replaces the hand-stamped `[IgnoreProperty] CreatedBy`, and Fleet
  registers `AddHistory()`.
  - `CreatedBy`/`CreatedAt` are now read-only model attributes and part of `VCar`, so the row filter on
    `Recent_Cars` is pushed into RQL.
  - `CreatedBy` stays a plain id: `Fleet.Library` does not reference Authorization.Abstractions, so
    SPARK040 applies.
