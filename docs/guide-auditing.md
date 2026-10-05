# Audit fields: declare the interface, the members are generated

Who created a row and when, who changed it last, who deleted it, who posted it: Spark stamps these for
you, and since #271 you do not write the properties either. This guide covers the interfaces, the
generator that fills them in, who stamps the values, and the writes that are **not** stamped.

Decisions and evidence: [audit_fields_PRD.md](audit_fields_PRD.md) and
[audit_fields_plan.md](audit_fields_plan.md).

## The interfaces

| Interface | Package (`.Abstractions`, for entity libraries) | Members | Stamped by (runtime package) |
|---|---|---|---|
| `IAuditCreated` | `MintPlayer.Spark.History.Abstractions` | `string? CreatedBy`, `DateTimeOffset? CreatedAt` | History (`AddHistory()`) |
| `IAuditModified` | `MintPlayer.Spark.History.Abstractions` | `string? ModifiedBy`, `DateTimeOffset? ModifiedAt` | History |
| `IAuditable` | `MintPlayer.Spark.History.Abstractions` | both of the above (`IAuditCreated` + `IAuditModified`) | History |
| `ISoftDeletable` | `MintPlayer.Spark.SoftDelete.Abstractions` | `bool IsDeleted`, `DateTimeOffset? DeletedAt`, `string? DeletedBy`, `string? DeleteReason` | SoftDelete (`AddSoftDelete()`) |
| `IModeratable` | `MintPlayer.Spark.Moderation.Abstractions` | `string? AuthorId`, `DateTimeOffset? PostedAt` | Moderation (`AddModeration<TUser>()`) |

- **User ids, never names** (#460 D8). A name is resolved when it is read, so deleting an account
  leaves an id that resolves to "deleted user" instead of personal data in every document.
- **Timestamps are `DateTimeOffset?`, always UTC** (`TimeProvider.GetUtcNow()`, replaceable in tests).
- **Nullable on purpose.** A system, job or anonymous write has no user id.
- **Pick a half** when you only need one pair: an immutable log entry wants `IAuditCreated` only.
  Fleet's `Car` is one (`apps/Fleet/Fleet.Library/Entities/Car.cs`).

There is no separate "deleted" audit interface: `DeletedBy`/`DeletedAt` mean nothing without soft
delete, so `ISoftDeletable` is both the behaviour and its fields.

## Declaring an audited entity

```csharp
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace Acme.Entities;

public partial class Invoice : IAuditable, ISoftDeletable
{
    public string? Id { get; set; }
    public string Number { get; set; } = "";
}
```

That is the whole entity. `MintPlayer.Spark.LibraryGenerators`, which every Spark entity library
already references, emits the eight members into a partial half:

```csharp
partial class Invoice
{
    /// <summary>The creating user's id. ...</summary>
    [global::System.ComponentModel.ReadOnly(true)]
    [global::MintPlayer.Spark.Abstractions.ReferenceAttribute(typeof(global::MintPlayer.Spark.Authorization.Identity.SparkUser))]
    public string? CreatedBy { get; set; }
    // CreatedAt, ModifiedBy, ModifiedAt, IsDeleted, DeletedAt, DeletedBy, DeleteReason ...
}
```

The rules:

- **Only the missing members are generated.** Declare a member yourself (to add an attribute or a doc
  comment of your own) and the generator leaves it alone. An explicit interface implementation counts as
  declared — QnA's `QuestionTranslationsContribution` implements `IModeratable` that way, as read-only
  views over the contribution's own fields.
- **A base type's contract is not generated again.** If `Entry : IAuditable` already has the members,
  `SpecialEntry : Entry, IAuditable` gets nothing (re-emitting would hide them, CS0108).
- **The type must be `partial`**, and so must every type containing it. Otherwise **SPARK038** (Error)
  says so, with a code fix that adds the keyword. A record is reopened as a record, a generic type with
  its type parameters.
- **Every generated member is `[ReadOnly(true)]`.** Model synchronization creates such an attribute
  read-only (the server then drops a posted value), not required, and on the object page only. That is
  creation-time only: change it in the model JSON and synchronization keeps your choice.
- **User ids are references to `SparkUser`** — `CreatedBy`, `ModifiedBy`, `DeletedBy`, `AuthorId` — when
  the library can see `SparkUser`, i.e. references `MintPlayer.Spark.Authorization.Abstractions`. The page
  then shows the user's `{UserName}` breadcrumb instead of an id. Without that reference they are plain
  strings, and **SPARK040** (Info) tells you why. Referencing the generator package never brings
  `SparkUser` in: an analyzer reference contributes no compile references.

⚠️ A `SparkUser` reference renders the user's breadcrumb to anyone who can read the row: reference
labels are resolved through row security on the target, not through a type right on `SparkUser`
(`tests/MintPlayer.Spark.E2E.Tests/QnA/QnAAuditFieldsTests.cs`). The user name is a public handle by
design (#264, G-Q15/G-Q22). If your app's `SparkUser` breadcrumb shows anything more private, such as
an email address, fix the breadcrumb.

## Registering the runtime half

The interface lives in an `.Abstractions` package so an entity library can declare it without pulling in
ASP.NET Core or RavenDB.Client. The **application** must reference the package that stamps it, and
register it:

```csharp
builder.Services.AddSpark(spark =>
{
    spark.AddHistory();       // IAuditCreated / IAuditModified / IAuditable
    spark.AddSoftDelete();    // ISoftDeletable
});
```

If an entity implements one of the interfaces and the application does not reference its package,
**SPARK039** (Warning) names the entity, the package and the `Add…()` call. Without it the members
exist and are never stamped.

`AddHistory()` also refuses to start when an `IAuditCreated`/`IAuditModified` member is not a public
read/write property of the interface's type, as `AddSoftDelete()` already did for `ISoftDeletable`.

## Who stamps, and when

Stamping is done by Spark persistence interceptors on every write through `IDatabaseAccess` (the
`/spark` endpoints, custom actions that save through it, revert, restore, sync):

| Write | `CreatedBy` / `CreatedAt` | `ModifiedBy` / `ModifiedAt` |
|---|---|---|
| Create | the current user, now | the current user, now |
| Edit, revert, restore | the **stored** values, whatever was posted | the current user, now |
| Save that changes nothing | — | not stamped (no new revision, no new etag) |
| Soft delete | — | the deleter, now |
| Write-back from a replica (`Sync`) | stored values (the replica's user on an insert) | the user the replica states (`SyncAction.InitiatorId`) |
| `Sync` from a replica that states no user (older, anonymous, background) | as sent | as sent |

The current user is `ISparkCurrentUser.Id`: the `NameIdentifier` claim of the request's principal. Replace
the registration to act for someone else, such as a background job acting for a user.

### Writes that are not stamped

These do not go through the interceptors. That is deliberate, but you should know it:

- **Raw session writes:** `session.Store(...)` + `SaveChangesAsync()` in a custom action, job, message
  handler or migration, and `IDatabaseAccess.SaveDocumentUncheckedAsync`.
- **Patches, delete-by-query, BulkInsert.** RavenDB raises no client-side event for them, so no listener
  could stamp them either.
- **ETL / replication into a replica.** It carries the owner's stamps, which is correct.

A store-level `OnBeforeStore` listener was considered and rejected (PRD §4, D7). It cannot tell a module
sync from a user edit, and outside an HTTP request it has no user to stamp with.

## Model hash

A generated member is an ordinary compiled property, so it is in the model and its hash like any other.
Adding an audit interface to an entity therefore moves the hash once, and so does adding the
`Authorization.Abstractions` reference that turns the user ids into references. Run
`--spark-synchronize-model` after either.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| SPARK038 | Error | The type (or a type containing it) is not `partial`, so its members cannot be generated. Code fix: add `partial`. |
| SPARK039 | Warning | The application references the interface but not the package that stamps it. |
| SPARK040 | Info | User ids were generated as plain strings: the library cannot see `SparkUser`. |
