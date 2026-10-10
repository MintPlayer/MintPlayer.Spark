# Live updates: pages and grids that follow the database

A detail page or a grid can follow its data: when a document changes, everyone looking at it gets the new
state within half a second, through the same read path, row security and attribute rights as a page load.
One WebSocket per browser tab carries every subscription (`/spark/live`). It replaces streaming queries
(`isStreamingQuery`, `StreamingQueryArgs`, `/spark/queries/{id}/stream`); there is no compatibility shim.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X4, D4, D24, D33–D36 and §12 "Spike S8".
Background: [seo_analyzer_research_live_updates.md](seo_analyzer_research_live_updates.md).

## The common case: one flag

```jsonc
// App_Data/Model/AuditRun.json
{
  "persistentObject": { "name": "AuditRun", "clrType": "…", "live": true, … },
  "queries": [
    { "name": "RunningAudits", "source": "Database.AuditRuns", "entityType": "AuditRun", "live": true }
  ]
}
```

That is all (D34). `spark-po-detail` refreshes an object of a live type by itself; every `spark-query-grid`
showing a live query (a query page, a sub-query on a detail page, a card) follows it. There are no notifier
calls, no socket code and no hooks to write:

- a save, delete or custom action through Spark is seen at once;
- a write from anywhere else — a message handler, a cron job, another instance of the app, RavenDB Studio —
  is seen through the RavenDB Changes API (`Spark:Live:Source = Both`, the default).

Live is **opt-in** per type and per query (D24), because every change to a watched collection costs one
re-evaluation per viewer.

## What a viewer receives, and what they never do

Every push re-runs the HTTP read (`/spark/po/load` or `/spark/queries/execute`) in a fresh DI scope under the
viewer's own principal. So for each push:

- row security applies, including the organizations row policy and the expiration filter;
- `GetProtectedAttributesAsync` redaction, attribute rights and `OnDisableActionsAsync` apply;
- a right revoked in `security.json`, or a membership removed, applies to the next evaluation, and every
  subscription is re-evaluated at least once a minute (`Revalidate`) even without a write.

The change itself is only a dirty bit; nothing from it reaches the client. The new state is diffed against
what the client already holds, and **nothing is sent when nothing visible changed**: a write to a row the
viewer cannot see has no observable effect, not even its timing. An object that is deleted, or that the viewer
may no longer read, ends its subscription with `gone` — the same answer for both (#453).

## Configuration

`Spark:Live` (or `spark.AddLive(o => …)`, code wins). Every value has a default.

| Key | Default | Meaning |
|---|---|---|
| `Source` | `Both` | `Actions`: writes through RavenDB sessions of this process, from the store's own events. `Changes`: every committed write from any process, through the RavenDB Changes API. `Both`: both, de-duplicated by document id + change vector (D33). |
| `Throttle` | `00:00:00.500` | At most one evaluation (and push) per subscription per interval; the latest state wins. The first change after a quiet period is evaluated at once. |
| `MaxSubscriptionsPerSocket` | `50` | The next `subscribe` gets `error` `limit`. |
| `MaxSocketsPerUser` | `5` | Per signed-in user, else per IP address; the next socket is closed with `4429`. |
| `Heartbeat` | `00:00:30` | A `ping` frame after this much silence (below nginx's default 60 s `proxy_read_timeout`). |
| `SendTimeout` | `00:00:10` | A frame the client has not taken by then aborts the socket (slow consumer). |
| `Revalidate` | `00:01:00` | Every subscription is re-evaluated at least this often. |
| `MaxSocketLifetime` | `00:30:00` | The socket closes with `4401` at this age or at the login ticket's expiry, whichever is first; the client reconnects with its current cookie. |
| `IndexWait` | `00:00:05` | How long a query re-run waits for the indexes over a changed collection to catch up first (a failure bound). |
| `MaxMessageBytes` | `65536` | The largest client frame accepted. |

The Changes API is **not** a data subscription: it does not count against the Community licence's three
subscriptions. It is one multiplexed connection per database per process, and it watches only the documents
and collections someone is subscribed to (measured on the production Community server, PRD §12).

### What each source does not see

- `Actions` misses writes by other processes, and patches, bulk inserts and deletes by id of documents the
  session never loaded (they raise no client event). Under `Both`, the Changes API sees all of these.
- A query is re-run when its own collection (or its index's mapped collection) changes. A change it only
  reaches through `LoadDocument`, a reference's breadcrumb or a multi-map index is picked up by `Revalidate`,
  or at once if the writing code calls the notifier:

```csharp
public sealed class Recalculate(ISparkLiveNotifier live) // MintPlayer.Spark.Abstractions.Live
{
    public async Task RunAsync(…)
    {
        // a patch, or a collection a query reads only indirectly
        live.Changed("auditruns/1-A", "AuditRuns");
        live.CollectionChanged("Sites");
    }
}
```

## Rows that are not in RavenDB: app streams

A live query whose `Custom.*` method has this signature gets its rows from the method, not from RavenDB:

```csharp
public partial class StockActions : DefaultPersistentObjectActions<Stock>
{
    // "source": "Custom.StreamItems", "live": true
    public async IAsyncEnumerable<IReadOnlyList<Stock>> StreamItems(LiveQueryArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        var stocks = Seed();
        while (!ct.IsCancellationRequested)
        {
            yield return stocks;               // the WHOLE current set: rows missing from it are removed
            await Task.Delay(1000, ct);
            Drift(stocks);
        }
    }
}
```

- `IAsyncEnumerable<IReadOnlyList<T>>`: each yield is the whole set. `IAsyncEnumerable<T>`: each yield adds or
  replaces one row by `Id`.
- Each yield passes row security, redaction and attribute rights, and is then searched, sorted and paged in
  memory with the grid's request; the total count is real. Column filters and distinct values are not
  offered for an app stream.
- `LiveQueryArgs` carries `Query`, `Parent` (a sub-query's parent, resolved and authorized like `/execute`'s),
  `Search`, `Skip`, `Take`, `SortColumns`, an uncapped `Session` and the `CancellationToken`. A grid that
  changes page, sort or search restarts the method with new arguments.
- The method does not start until the viewer holds the type's `Query` right.
- `/spark/queries/execute` answers an app stream with an empty page; its rows only arrive over the socket.
- Startup refuses a method with this signature on a query that is not `"live": true`.

## The protocol

`GET|CONNECT /spark/live`, one JSON object per text frame. The client opens it lazily on the first
subscription. Subscriptions are reads: mutations stay on HTTP (antiforgery), and the same-origin guard of
`UseSpark()` protects the upgrade (CSWSH). Behind nginx, pass the upgrade (see
[guide-docker-deployment.md](guide-docker-deployment.md)).

### Client → server

Every operation names its subscription in `sub` (1–64 characters, chosen by the client).

```jsonc
{ "op": "subscribe",   "sub": "s1", "po":    { "objectTypeId": "AuditRun", "id": "auditruns/1-A", "deleted": null } }
{ "op": "subscribe",   "sub": "s2", "query": { "queryId": "runningaudits", "skip": 0, "take": 20, "sortColumns": [{ "property": "StartedAt", "direction": "desc" }], "search": null, "columns": null, "parentId": null, "parentType": null, "deleted": null, "parentDeleted": null } }
{ "op": "update",      "sub": "s2", "query": { …the same query with a new page, sort, search or filters… } }
{ "op": "unsubscribe", "sub": "s1" }
```

`po` is exactly the body of `POST /spark/po/load`; `query` is exactly the body of `POST /spark/queries/execute`
(`retryResults` is ignored: a push cannot prompt).

### Server → client

Every frame has a `type`; every frame about a subscription carries its `sub`.

| `type` | Payload | Meaning |
|---|---|---|
| `po` | `po`: the object, exactly as `/spark/po/load` returns it (`etag`, `attributes`, `disabledActions`, …) | The first state, or a state whose set or order of attributes changed. Replace. |
| `poPatch` | `patch`: `{ "set"?: { field: value }, "attributes"?: [attribute…] }` | Top-level fields that changed (`etag`, `breadcrumb`, …; `null` = removed), and the attributes that changed, whole, matched by `name`. |
| `gone` | — | Deleted or no longer readable (one answer). The subscription has ended. |
| `result` | `result`: the page, exactly as `/spark/queries/execute` returns it | The first page, a page whose columns changed, or a change for which a patch would not be smaller. Replace. |
| `patch` | `patch`: `{ "upsert"?: [item…], "remove"?: [id…], "order"?: [id…], "set"?: { field: value } }` | Rows new or changed (whole, by `id`), ids removed, the page's new id order when it differs from "the old order minus the removed rows", and other fields that changed (`totalItems`, `skip`, `take`, `disabledActions`, `typeHints`). |
| `error` | `code`, `message` | The subscription has ended (the socket stays open). `unavailable`: unknown, forbidden, not live, or a missing parent — one answer for all. `invalid`: a malformed request (said what). `limit`: too many subscriptions. `failed`: a server error (logged; generic message). An `error` without `sub` answers an unparseable frame. |
| `ping` | — | Heartbeat. Nothing to answer. |

Applying a `patch`: index the held rows by `id`; drop `remove`; replace or add each `upsert`; if `order` is
present the rows are `order` mapped through the index, otherwise the held order minus the removed rows; then
assign `set`.

### Close codes

| Code | Meaning | Client |
|---|---|---|
| `4401` | Re-authenticate: the login ticket expired or the socket reached `MaxSocketLifetime`. | Reconnect at once and resubscribe (the cookie is validated afresh). |
| `4429` | Too many sockets for this user. | Do not reconnect until asked; surface it. |
| `1009` | A client frame was too large. | A bug: do not retry blindly. |
| `1000`, `1001`, `1006`, other | Normal close, server going away, network loss, slow consumer (abort). | Reconnect with backoff and jitter, then resubscribe everything (each subscription gets a fresh `po`/`result`). |

## Angular (`@mintplayer/ng-spark`)

Nothing to write for the generic pages: `spark-po-detail` follows a live type, and `spark-query-grid` follows a
live query (`[live]` overrides the query's flag, e.g. `[live]="false"` on a dashboard tile). While an edit form
is open on a live type, `spark-po-edit` adopts a pushed version when the form is untouched; when the user has
edited, it keeps their edits, shows a banner, and offers the existing three-way merge
(`mergeThreeWay`, `SparkPoConflictDialogComponent`). The `409` on save stays the server-side guarantee.

For a custom page, `SparkLiveService` (`@mintplayer/ng-spark/services`) shares one socket:

```ts
const live = inject(SparkLiveService);
readonly run = live.livePersistentObject(signal('AuditRun'), this.runId);   // Signal<PersistentObject | null | 'gone'>
readonly progress = computed(() => { const r = this.run(); return r && r !== 'gone' ? valueOf(r, 'PagesDone') : 0; });
readonly running = live.liveQuery(computed(() => ({ queryId: 'runningaudits', skip: 0, take: 20 })));  // Signal<QueryResult | null>
// or the observables: live.watchPersistentObject(type, id), live.watchQuery(request)
```

Two components watching the same object or the same request share one server subscription.
