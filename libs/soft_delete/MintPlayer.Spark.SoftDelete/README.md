# MintPlayer.Spark.SoftDelete

Soft deletion for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark). An entity that
implements `ISoftDeletable` is **marked** deleted instead of removed, disappears from every read
path, can be restored, and can be purged for good — revisions included, for GDPR.

Two packages:

| Package | Reference it from | Contains |
|---|---|---|
| `MintPlayer.Spark.SoftDelete.Abstractions` | Domain / Library projects | `ISoftDeletable`, `ISparkSoftDelete`, `SoftDeleteRights` |
| `MintPlayer.Spark.SoftDelete` | the host | the row policy, the hooks (formerly "the interceptor"), the endpoints, `AddSoftDelete()` |

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
    spark.AddHook<OrderAudit>();                 // optional, durable: needs spark.AddMessaging()
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
forwards a save, not a delete. The replacement is an `IDeleteReplacement` (#482): the framework
decides it before any before-delete hook runs, so nothing can turn it back into a hard delete, and
every `IBeforeDelete` hook still runs (and sees `context.IsReplaced`).

**Hidden everywhere** a row policy applies — lists, detail, custom queries, sub-queries, distinct
values, streams, breadcrumbs, reference pickers, edit/delete gates, custom-action selections. The
filter is `IsDeleted != true`, so documents stored before the type became soft-deletable (no field
at all) stay visible. A query asks for more with the request field `deleted`:

| `deleted` | holder of `ViewDeleted/T` | everyone else |
|---|---|---|
| `exclude` (default) | live rows | live rows |
| `include` | live + deleted | live rows (flag ignored) |
| `only` | deleted rows | live rows (flag ignored) |

Opening a row by id (`POST /spark/po/load`) takes the same field: `{ objectTypeId, id, deleted:
"include" }` opens a deleted row from the recycle bin for a `ViewDeleted/T` holder (ng-spark
`spark.get(type, id, { deleted })`, `SparkClient.GetPersistentObjectAsync(…, deleted: …)`); for
everyone else, and without the field, a deleted row is a 404. The loaded row's `can.edit` is false
(an edit still hides deleted rows — restore first).

**Sub-queries on a deleted row's page.** A sub-query names its parent (`parentId`, `parentType`), and
the parent is resolved as a row too — so on a deleted row's page it would be a 404 ("Parent not
found"). `POST /spark/queries/execute` and `/spark/queries/distinct-values` take a second field for
that, `parentDeleted`, with the same values as `deleted` and the same gate, on the **parent's** type:

| request | holder of `ViewDeleted/{ParentType}` | everyone else |
|---|---|---|
| deleted parent, no `parentDeleted` | 404 | 404 |
| deleted parent, `parentDeleted: "include"` | the parent's rows | 404, the same body as a missing parent |

The two fields are independent: `parentDeleted` says how the parent is found, `deleted` still says
which of its rows are listed (a deleted parent's live children by default). ng-spark sends
`parentDeleted: "include"` from `<spark-query-card [parentDeleted]>`, which the detail page sets for a
row opened with `?deleted=only`; `SparkClient.ExecuteQueryAsync(…, parentDeleted: …)` and
`GetDistinctValuesAsync(…, parentDeleted: …)` take it too. `/spark/actions/execute` and
`/spark/po/delete-many` do **not**: their sub-query parent is always a live row, so an action or bulk
delete under a deleted parent is a 404 whatever the body says (the recycle bin offers neither).

`GET /spark/permissions/{type}` reports `canRestore`, `canPurge` and `canViewDeleted` alongside the
built-in rights, so a UI knows whether to offer the recycle bin.

**Restore** — `POST /spark/po/restore { objectTypeId, id }` → 200 with the restored object. Needs
`Restore/T`; the row must be deleted; it is saved through the normal pipeline with no attributes, so
nothing but the four fields changes. Refused with 403 when the Actions class's
`OnDisableActionsAsync` withholds `Restore`, `Edit` or `Save` on the row.

**Purge** — `POST /spark/po/purge { objectTypeId, id }` → 204. Needs `Purge/T`; the row must already
be deleted. The document is deleted (the before-delete hooks run, with `IsPurge` set),
then every revision of it, force-created ones included. Refused with 403 when the hook withholds
`Purge` or `Delete`. Cannot be undone. See [Purge needs database-admin](#purge-needs-database-admin).

**A refused delete leaves nothing behind.** The mark is set on the request session's copy of the
row; if a before-delete hook (a lock, say) refuses the delete, `IDatabaseAccess` evicts that copy, so
no later save in the same request writes the half-made delete.

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

## Angular UI (`@mintplayer/ng-spark/soft-delete`)

```ts
import { provideSparkSoftDelete } from '@mintplayer/ng-spark/soft-delete';

providers: [provideSpark(...), provideSparkSoftDelete()]
```

This adds two things to the pages that `sparkRoutes()` routes to, through the `SPARK_QUERY_LIST_ACTIONS`
and `SPARK_DETAIL_ACTIONS` tokens from `@mintplayer/ng-spark/panels`:

- **A Deleted toggle** on the query page, shown to holders of `ViewDeleted/T` (`canViewDeleted`). It
  switches the list to the recycle bin by writing `?deleted=only` to the route, so a reload or the
  back button keeps the mode. The grid sends `deleted: "only"` with every page and distinct-values
  request. Row links carry the same parameter, so the detail page loads the row with
  `/spark/po/load { deleted: "only" }`.
- **Restore and Purge** on the detail page of a row opened from the recycle bin. Restore needs
  `canRestore` and Purge needs `canPurge`; Purge asks for confirmation first. On that page, Edit,
  Delete and custom actions are hidden, since they judge live rows only. After a Restore the page
  drops `?deleted` and re-reads the row as a live one. After a Purge it goes back.

**The recycle bin offers nothing else.** The query-list toolbar, a sub-query card and the row `⋮`
menu show no New, no default Delete and no custom action while the grid lists `deleted: "only"`, or
while it sits on the detail page of a row opened with `?deleted=only` (the card's `parentDeleted`).
The rule lives once in the grid (`recycleBin()`), so the list page and the card cannot disagree. The
server refuses the same requests anyway: `/spark/po/delete-many` and `/spark/actions/execute` judge
live rows only, so a hand-made request that aims either at a deleted row is the same 404 as a hidden
row, and a `deleted` field in its body widens nothing.

Core ng-spark understands `?deleted=` on both pages even without this entry point: the entry point
only adds the controls. The server stays the gate, and a widening from a non-holder is ignored.

## Reacting to deletes, restores and purges

`ISoftDeleteObserver` is gone (#482): use the framework's durable after-commit hooks, which run once
the write committed, even across a crash, with Messaging's retries
([guide](../../../docs/guide-hooks.md#5a-durable-after-commit-hooks)).

```csharp
public sealed class OrderAudit : IAfterDeleteCommitted<Order>, IAfterSaveCommitted<Order>
{
    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken ct)
    {
        // change.IsReplaced: soft-deleted; change.IsPurge: purged. change.Id, change.UserId, change.Reason
        return Task.CompletedTask;
    }

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken ct)
    {
        // change.Operation == PersistentObjectOperation.Restore: restored
        return Task.CompletedTask;
    }
}
```

SoftDelete records the stored delete reason as `SparkFacts.Reason`, so `change.Reason` is set for a
delete through `ISparkSoftDelete.DeleteAsync` as well as a bulk delete with a reason.

## Startup checks

`UseSpark()` refuses to start when a model type implements `ISoftDeletable` with a member that is not
a public read/write property of the interface's type — the filter is rebound by member **name**, and
RavenDB stores only public properties, so an explicit implementation would be stored nowhere and
every row would look live.

It installs the raw-delete guard (see [Limits](#limits)), and warns when an index over a soft-deletable collection has a projection without `IsDeleted` (the filter falls
  back to filtering after materialization), or a Map that never mentions `IsDeleted` (a pushed-down
  `IsDeleted != true` matches every index entry; rows are dropped only on the reload, and pages come
  back short). Emit `IsDeleted` in the Map and the projection.

Analyzer **SPARK022** flags `!x.IsDeleted` and `x.IsDeleted == false` on an `ISoftDeletable` inside an
expression a provider translates: both drop every document without the field. Write
`x.IsDeleted != true`.

## Purge needs database-admin

Purge deletes the document, then runs RavenDB's `DeleteRevisionsOperation` — a **database-admin**
operation (deleting a document is not). On a secured server the client certificate the app connects
with must therefore have **Admin** access to that database (the certificate's per-database access
level), or no purge can finish.

A purge **fails safe** without it: before the document is deleted, the before-delete hook runs the same
operation on an id that names nothing (no effect, same authorization). If that is refused, the purge
is refused — the caller gets a 500, the log says why, and the document and its revisions are
untouched. A success is remembered for the process, so the probe costs one request per process.
Unsecured servers (development, the embedded test server) need nothing.

## Limits

- Revisions of *other* documents that embed the purged row's data are not touched (GDPR guidance:
  that content is the app's to handle).
- A raw session delete of a soft-deletable document (`session.Delete(id)` or `session.Delete(entity)`)
  is refused when the session commits, unless the framework issued it (a purge) or the code runs inside
  `using (SparkRawWrites.Allow()) { … await session.SaveChangesAsync(); }` (a migration, a test
  fixture). A delete by id is recognised by the collection prefix of the id; patches and delete-by-query
  raise no session event and are not covered (#467, D32).
- An `OnLoadAsync` override that skips the base implementation skips row policies for its type —
  soft deletion included (D1, documented core behaviour). Writes have no such gap since #482.
