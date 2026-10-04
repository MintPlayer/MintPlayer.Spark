# MintPlayer.Spark.History

Revision history for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark): RavenDB
revisions configured from the model, audit stamping, revision reads through Spark's row security and
redaction, revert through the normal save pipeline, and the changed attributes of every write for
durable after-commit interceptors.

- `IAuditable` — `CreatedBy`, `CreatedAt`, `ModifiedBy`, `ModifiedAt`, stamped on every write with
  **user ids** (never names).
- `"revisions"` in a model file — the collection's RavenDB revisions settings, merged into the
  database at startup.
- `POST /spark/po/revisions`, `/spark/po/revision`, `/spark/po/revert`.
- `ISparkHistory` (the same three in code).

## Setup

```csharp
builder.Services.AddSpark(spark =>
{
    spark.UseContext<AppContext>();
    spark.AddHistory();
    spark.AddInterceptor<AuditTrail>();                      // optional, durable: needs spark.AddMessaging()
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

### Revision limits from configuration (#460 M16)

The limits can also come from `Spark:History` — appsettings, environment variables, user secrets — and
there **configuration beats code** (re-applied after the `AddHistory` delegate):

```json
"Spark": {
  "History": {
    "Revisions": { "MinimumRevisionsToKeep": null, "MinimumRevisionAgeToKeep": "30.00:00:00", "PurgeOnDelete": false },
    "Types": {
      "Question": { "MinimumRevisionsToKeep": 2 },
      "AuditEntry": { "Enabled": true, "MinimumRevisionAgeToKeep": "45.00:00:00" }
    }
  }
}
```

Each setting is taken from the first source that sets it: `Spark:History:Types:{type}` (the model
type's `name`, case-insensitive; `Spark__History__Types__Question__MinimumRevisionsToKeep=2`) → the
model's `revisions` block → `Spark:History:Revisions`. A type is configured when its model has a block or
`Types:{type}:Enabled` is set (which also overrides the model's `enabled`); a `Types` key naming no model
type is warned about. `0` / `00:00:00` mean "no limit". The model's `purgeOnDelete: false` cannot be told
from "unset", so it defers to the default; only `true` in the model is explicit.

**Defaults:** revisions are kept **30 days** (`MinimumRevisionAgeToKeep`), with no count limit and no
purge on delete. Chosen to fit the Community licence and to bound how long revisions keep personal data
(account deletion does not rewrite them, D8). RavenDB deletes a revision only once it is past **both**
limits, and only when the document is written again — a document nobody edits keeps its old revisions.
Set `Spark:History:Revisions:MinimumRevisionAgeToKeep=00:00:00` for unlimited history.

⚠️ **Licence limits.** RavenDB caps what a collection may ask for. Measured on a **Community**
licence (7.2): at most **2** `minimumRevisionsToKeep`, at most **45 days** `minimumRevisionAgeToKeep`,
and no *enabled* `Default` configuration (a disabled one is accepted). When `GET /license/status` reports
`Community`, History checks the merged limits **before** sending anything and refuses startup naming
every type, setting and source (`Question (Questions): MinimumRevisionsToKeep = 10 (from
Spark:History:Types:Question)`) and the way out; on any other licence, a refusal is RavenDB's own reason.
Configuring revisions is a **database-admin** operation; the read is
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
- every persistence interceptor runs with operation `Revert` — stamping, soft deletion, locks;
- a concurrent edit between load and save is a 409; an interceptor's refusal a 400.

What reverts: every **model attribute** (measured, spike H3: strings, a `TranslatedString` — made
exact, so a language added after the revision is removed — a `DateTimeOffset` with its offset,
`AsDetail` rows). What does not: the audit fields (stamped anew), the soft-delete fields (SoftDelete
keeps the stored ones, so a revert never undeletes, and a deleted row cannot be reverted — restore it
first), fields the model does not declare. Validation rules are **not** re-run: the revision was valid
when it was written. ⚠️ `AsDetail` rows are rewritten from the revision as a whole; rows have no
identity across saves beyond their position (the known AsDetail row-identity caveat).

## Reacting to writes

`ISparkRevisionObserver` is gone (#482): use the framework's durable after-commit interceptors
([guide](../../../docs/guide-interceptors.md#5a-durable-after-commit-interceptors)), which run once the write
committed, even across a crash, with Messaging's retries.

```csharp
public sealed class AuditTrail : IAfterSaveCommitted<Order>, IAfterDeleteCommitted<Order>
{
    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken ct)
    {
        // change.Operation (New, Save, Revert, Restore, Sync), change.Id, change.UserId,
        // change.PreviousChangeVector, change.Facts[SparkFacts.ChangedAttributes] ("Title,Total")
        return Task.CompletedTask;
    }

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken ct)
        => Task.CompletedTask; // change.IsReplaced: a soft delete; change.IsPurge: a purge
}
```

For a type whose model has `"revisions": { "enabled": true }`, History records the top-level
attributes each write changed as `SparkFacts.ChangedAttributes` (comma-separated). The payload has no
new change vector — the message is written in the same commit, before the database assigns it — so
an interceptor that needs the revision loads the row. Writes outside `IDatabaseAccess` (a raw session, a
patch, ETL) still create revisions, but no interceptor hears of them.

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

- History's before-save interceptor runs in `InterceptorStage.Finalize` (#482), after every interceptor that stamps or
  trims fields, so its "did this edit change anything?" judges the row as it is written. Nothing can skip it.
- `ChangedAttributes` compares the stored and the saved value of each top-level model attribute as
  JSON; `AsDetail` changes are reported as the whole attribute.
