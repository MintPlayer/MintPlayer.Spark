# MintPlayer.Spark.SoftDelete

Soft deletion for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark). An entity that
implements `ISoftDeletable` is **marked** deleted instead of removed, disappears from every read
path, can be restored, and can be purged for good — revisions included, for GDPR.

Two packages:

| Package | Reference it from | Contains |
|---|---|---|
| `MintPlayer.Spark.SoftDelete.Abstractions` | Domain / Library projects | `ISoftDeletable`, `ISparkSoftDelete`, `ISoftDeleteObserver`, `SoftDeleteRights` |
| `MintPlayer.Spark.SoftDelete` | the host | the row policy, the interceptor, the endpoints, `AddSoftDelete()` |

## Setup

```csharp
public class Order : ISoftDeletable
{
    public string? Id { get; set; }
    public string Number { get; set; } = "";

    // All four PUBLIC — never an explicit interface implementation (see "Startup checks").
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

builder.Services.AddSpark(spark =>
{
    spark.UseContext<AppContext>();
    spark.AddSoftDelete();                       // binds Spark:SoftDelete, code wins
    spark.AddSoftDeleteObserver<OrderAudit>();   // optional
});
```

Then grant the new rights **per type, by name** in `App_Data/security.json` (there are no wildcards):

| Right | Lets the holder |
|---|---|
| `Delete/Order` | delete — which is now a soft delete (unchanged right) |
| `Restore/Order` | bring a deleted row back |
| `Purge/Order` | remove a deleted row and all its revisions, permanently |
| `ViewDeleted/Order` | list deleted rows (`deleted: include \| only` on a query), and point a reference at one |

None implies another: `Edit` does not grant `Restore`, `Delete` does not grant `Purge`.

## What happens

**Delete** (`POST /spark/po/delete`, `IDatabaseAccess.DeletePersistentObjectAsync`) sets
`IsDeleted = true`, `DeletedAt`, `DeletedBy` (the user **id**) and, through
`ISparkSoftDelete.DeleteAsync(typeId, id, reason)`, `DeleteReason`. The document stays. Replication
forwards a save, not a delete. The Actions class's `OnDeleteAsync` is **not** called — the
replacement is decided in `IDatabaseAccess` before it (so an override cannot defeat it);
`OnBeforeDeleteAsync` still runs.

**Hidden everywhere** a row policy applies — lists, detail, custom queries, sub-queries, distinct
values, streams, breadcrumbs, reference pickers, edit/delete gates, custom-action selections. The
filter is `IsDeleted != true`, so documents stored before the type became soft-deletable (no field
at all) stay visible. A query asks for more with the request field `deleted`:

| `deleted` | holder of `ViewDeleted/T` | everyone else |
|---|---|---|
| `exclude` (default) | live rows | live rows |
| `include` | live + deleted | live rows (flag ignored) |
| `only` | deleted rows | live rows (flag ignored) |

Opening a deleted row by id (`/spark/po/load`) is a 404 for everyone.

**Restore** — `POST /spark/po/restore { objectTypeId, id }` → 200 with the restored object. Needs
`Restore/T`; the row must be deleted; it is saved through the normal pipeline with no attributes, so
nothing but the four fields changes. Refused with 403 when the Actions class's
`OnDisableActionsAsync` withholds `Restore`, `Edit` or `Save` on the row.

**Purge** — `POST /spark/po/purge { objectTypeId, id }` → 204. Needs `Purge/T`; the row must already
be deleted. The document is deleted (this time the Actions class's `OnDeleteAsync` **is** called),
then every revision of it, force-created ones included. Refused with 403 when the hook withholds
`Purge` or `Delete`. Cannot be undone.

Every refusal — no right, a live row, a missing id, an id of another collection, a type that is not
soft-deletable — is the same answer as any other Spark endpoint: 404 (401 when signing in could
help). A 403 comes only after the row gate, and names the withheld action.

**Edits own nothing here.** A save or create cannot change the four fields: the stored values (or
the defaults, on a create) replace whatever was posted. Mark them read-only in the model if they
are attributes at all.

**References.** Core does not row-check references on save, so the interceptor refuses (400) a save
that points a `[Reference]` at a deleted row unless the caller holds `ViewDeleted` on the target
type. Only references the save **changes** are checked — an existing reference to a row deleted
since does not block other edits. (Checked for `string` and string-collection properties.)

**Natural ids.** Creating an `IHasNaturalId` row whose derived id a deleted row holds would be an
edit of that row, which the row gate refuses. Holders of `ViewDeleted/T` or `Restore/T` get a 400
"a deleted T already holds this key — restore it instead"; everyone else keeps the 404.

**System context** (module sync, background jobs) does not see deleted rows either, by default. A
job that works on deleted rows sets `Spark:SoftDelete:ApplyInSystemContext = false` (or
`AddSoftDelete(o => o.ApplyInSystemContext = false)`) — then the system context sees every row.

## Observers

```csharp
public sealed class OrderAudit : ISoftDeleteObserver
{
    public ValueTask OnDeletedAsync(SoftDeleteEvent e) { /* e.EntityType, e.Id, e.UserId, e.Reason */ return default; }
    public ValueTask OnRestoredAsync(SoftDeleteEvent e) => default;
    public ValueTask OnPurgedAsync(SoftDeleteEvent e) => default;
}
```

Called in-process after the write committed, for every path through the Spark pipeline. An exception
reaches the caller but does not undo the write.

## Startup checks

`UseSpark()` refuses to start when a model type implements `ISoftDeletable` with a member that is not
a public read/write property of the interface's type — the filter is rebound by member **name**, and
RavenDB stores only public properties, so an explicit implementation would be stored nowhere and
every row would look live.

It warns when:
- a soft-deletable type's Actions class overrides `OnDeleteAsync` (it now runs only for a purge —
  move delete-time logic to `OnBeforeDeleteAsync` or an observer);
- an index over a soft-deletable collection has a projection without `IsDeleted` (the filter falls
  back to filtering after materialization), or a Map that never mentions `IsDeleted` (a pushed-down
  `IsDeleted != true` matches every index entry; rows are dropped only on the reload, and pages come
  back short). Emit `IsDeleted` in the Map and the projection.

Analyzer **SPARK022** flags `!x.IsDeleted` and `x.IsDeleted == false` on an `ISoftDeletable` inside an
expression a provider translates: both drop every document without the field. Write
`x.IsDeleted != true`.

## Limits

- Purge runs RavenDB's `DeleteRevisionsOperation`, an **admin** operation: the client certificate
  (on a secured server) needs database-admin access, or every purge fails after the document is gone.
- Revisions of *other* documents that embed the purged row's data are not touched (GDPR guidance:
  that content is the app's to handle).
- An Actions class that overrides `OnDeleteAsync` without deleting makes a purge fail (the document
  and its revisions are kept, the caller gets a 500) rather than wiping the history of a live row.
- An `OnLoadAsync` / `OnSaveAsync` override that skips the base implementation skips row policies
  and interceptors for its type — soft deletion included (D1, documented core behaviour).
