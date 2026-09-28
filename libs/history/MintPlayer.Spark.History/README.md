# MintPlayer.Spark.History

Revision history for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark): RavenDB
revisions configured from the model, audit stamping, revision reads through Spark's row security and
redaction, revert through the normal save pipeline, and in-process revision observers.

- `IAuditable` — `CreatedBy`, `CreatedAt`, `ModifiedBy`, `ModifiedAt`, stamped on every write with
  **user ids** (never names).
- `"revisions"` in a model file — the collection's RavenDB revisions settings, merged into the
  database at startup.
- `POST /spark/po/revisions`, `/spark/po/revision`, `/spark/po/revert`.
- `ISparkHistory` (the same three in code), `ISparkRevisionObserver`.

## Setup

```csharp
builder.Services.AddSpark(spark =>
{
    spark.UseContext<AppContext>();
    spark.AddHistory();
    spark.AddRevisionObserver<AuditTrail>();          // optional
    spark.AddHistoryUserNameResolver<UserNames>();    // optional: names in revision lists
});
```

```csharp
public class Article : IAuditable
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}
```

`App_Data/Model/Article.json`, hand-written (model synchronization keeps it):

```json
{
  "persistentObject": {
    "name": "Article",
    "revisions": { "enabled": true, "minimumRevisionsToKeep": 2, "minimumRevisionAgeToKeep": "30.00:00:00", "purgeOnDelete": false },
    ...
  }
}
```

`security.json`, per type, by name (no wildcards):

| Right | Allows |
|---|---|
| `History/Article` | list and read the row's revisions |
| `Revert/Article` | revert (also needs `Edit/Article`) |

## Revisions from the model

At startup every model type with a `revisions` block has its collection configured: `enabled` →
`Disabled = !enabled`, `minimumRevisionsToKeep`, `minimumRevisionAgeToKeep` (`"d.hh:mm:ss"`),
`purgeOnDelete`. RavenDB's `ConfigureRevisionsOperation` replaces the **whole** configuration, so the
package reads the current one first and changes only those collections: the `Default`, every other
collection and every setting the model does not express are kept, and nothing is sent when the
collections already match (a restart writes nothing). Removing the block does not disable revisions —
it leaves the collection to whoever owns the database.

⚠️ **Licence limits.** RavenDB caps what a collection may ask for. Measured on a **Community**
licence (7.2): at most **2** `minimumRevisionsToKeep`, at most **45 days** `minimumRevisionAgeToKeep`,
and no *enabled* `Default` configuration (a disabled one is accepted). A model asking for more refuses
startup with RavenDB's reason. Configuring revisions is a **database-admin** operation; the read is
`GET /databases/{db}/revisions/config`. When an operator manages revisions by hand, or the app's
certificate may not, set `Spark:History:ConfigureRevisions = false`.

## Stamping

On every write through `IDatabaseAccess` to an `IAuditable` type: a create sets `CreatedBy`/`CreatedAt`
and `ModifiedBy`/`ModifiedAt` from `ISparkCurrentUser` (the id; `null` for the system); every later
save — edit, revert, restore — keeps the **stored** `CreatedBy`/`CreatedAt` whatever was posted (so
authorship cannot be claimed by editing) and sets `ModifiedBy`/`ModifiedAt`. A soft delete stamps
`Modified*` too. A module `Sync` keeps what the owner module stamped. Mark the four read-only in the
model if they are attributes at all.

Ids only (GDPR, D8): a name is resolved when a revision list is read, through an
`IHistoryUserNameResolver` if one is registered; after an account is deleted its id resolves to
nothing ("deleted user"). **Revisions are not rewritten** when an account is deleted — personal data
inside content is the app's to handle (an account-deletion handler).

## Reading history

`POST /spark/po/revisions { objectTypeId, id, skip?, take? }` → `200 { result: [ { changeVector,
lastModified, userId, userName, isDeleteRevision, isCurrent } ], operations }`, newest first (`take`
at most 200).

`POST /spark/po/revision { objectTypeId, id, changeVector }` → `200 { result: PersistentObject }`: the
revision as a read-only object (`etag` = the revision's change vector, `can` = no edit, no delete).

Both are gated on the **current** row: the caller must be able to load it now (`Read/T` + the row's
read gate, like `/spark/po/load`) and hold `History/T`. A row they cannot see has no history for them,
and every refusal — no right, hidden row, missing row, a change vector of **another** row — is the
standard 404 (401 when signing in could help). The change-vector check matters: RavenDB returns a
revision for any change vector, whatever document it belongs to.

A **soft-deleted** row's history: both reads take `deleted: exclude|include|only`, exactly as
`/spark/po/load` does (`exclude` by default). Row policies see it through `RowPolicyContext.Deleted`;
the SoftDelete package honours it only for holders of `ViewDeleted/T`, so for everyone else a deleted
row keeps answering 404. `revert` ignores it — restore the row first.

Revision content is **redacted** like a load: an attribute the Actions class's
`GetProtectedAttributesAsync` hides is hidden when it is protected on the revision **or** on the
current row. Both endpoints are reads and explicitly exempt from antiforgery.

## Revert

`POST /spark/po/revert { objectTypeId, id, changeVector }` → `200 { result: <the reverted row> }`.

The revision is mapped to a persistent object and saved through
`IDatabaseAccess.SavePersistentObjectAsync(po, Revert)` with the row's **current** change vector — the
same pipeline as an edit:

- `History/T`, `Revert/T` and `Edit/T`; the row gate (asked about `"Revert"`; an Actions class's own
  rules see `"Edit"`); `WITH CHECK`; protected attributes keep their stored values;
- the disabled-action hook refuses it (403 naming the action) when it withholds `Revert`, `Edit` or
  `Save`;
- every interceptor runs with operation `Revert` — stamping, soft deletion, locks;
- a concurrent edit between load and save is a 409; an interceptor's refusal a 400.

What reverts: every **model attribute** (measured, spike H3: strings, a `TranslatedString` — made
exact, so a language added after the revision is removed — a `DateTimeOffset` with its offset,
`AsDetail` rows). What does not: the audit fields (stamped anew), the soft-delete fields (SoftDelete
keeps the stored ones, so a revert never undeletes, and a deleted row cannot be reverted — restore it
first), fields the model does not declare. Validation rules are **not** re-run: the revision was valid
when it was written. ⚠️ `AsDetail` rows are rewritten from the revision as a whole; rows have no
identity across saves beyond their position (the known AsDetail row-identity caveat).

## Observers

```csharp
public sealed class AuditTrail : ISparkRevisionObserver
{
    public ValueTask OnRevisionCreatedAsync(SparkRevisionEvent e)
    {
        // e.EntityType, e.Id, e.ChangeVector, e.PreviousChangeVector, e.UserId, e.Kind, e.ChangedAttributes
        return default;
    }
}
```

Told after every write through the Spark pipeline to a type whose model has `"revisions": { "enabled":
true }` — create, edit, revert, restore, soft or hard delete, module sync (a purge removes the
revisions, so it is not reported). In-process and multi-registered; an exception reaches the caller
but does not undo the write. Writes outside `IDatabaseAccess` (a raw session, a patch, ETL) are not
seen. History does not depend on Messaging — publish a message from an observer to fan out durably.

## Permissions endpoint

`GET /spark/permissions/{type}` reports `canViewHistory` (`History/T`) and `canRevert` (`Revert/T` and
`Edit/T`), type-level only.

## Angular UI (`@mintplayer/ng-spark/history`)

```ts
import { provideSparkHistory } from '@mintplayer/ng-spark/history';

providers: [provideSpark(...), provideSparkHistory()]
```

This adds a History card to every detail page that `sparkRoutes()` routes to, through the
`SPARK_DETAIL_PANELS` token from `@mintplayer/ng-spark/panels`. The card lists the revisions newest
first, with the date, who made the change (the name from `IHistoryUserNameResolver`, else the id),
and markers for the current revision and delete revisions. Selecting a revision shows two views:

- **Changes**: the shown attributes whose value differs from the current object.
- **Revision**: a read-only view of every shown attribute.

The card renders only with `canViewHistory`. **Revert** is offered only with `canRevert`, and never
for the current revision, a delete revision, or a row opened from the recycle bin (`?deleted=only`),
because a revert saves through the live pipeline. Revert asks for confirmation. After it succeeds,
the page reloads the object. The page reports a 409 (the row changed meanwhile) and a 404 inline.

Outside the routed pages, use `<spark-po-history [type] [id] [entityType] [current] [permissions]
[deleted] (reverted)>` directly, or call `SparkHistoryService` (`list`, `get`, `revert`).

## Limits

- An `OnSaveAsync` override that skips the base implementation skips before-save interceptors for its
  type — stamping and the exact-`TranslatedString` revert included (D1, documented core behaviour).
- `ChangedAttributes` compares the stored and the saved value of each top-level model attribute as
  JSON; `AsDetail` changes are reported as the whole attribute.
