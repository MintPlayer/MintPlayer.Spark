# Live updates (X4): one mechanism for queries and persistent objects

Investigation for `docs/seo_analyzer_PRD.md` §12 D4/D5 (X4). Read-only; repo state at 761a9f2b (master, 2026-10-10).
Libraries are preview: no backward compatibility required.

---

## 1. What exists today: streaming queries, end to end

### 1.1 Model and metadata
- `SparkQuery.IsStreamingQuery` — `libs/spark/MintPlayer.Spark.Abstractions/SparkQuery.cs:86-91` (doc comment names `/spark/queries/{id}/stream`).
- Allowed model-file field — `libs/spark/MintPlayer.Spark.Abstractions/Model/ModelFileShape.cs:224-229`; JSON schema `schemas/model.schema.json:700-703`.
- Sent to the client on `SparkQueryInfo` — `libs/spark/MintPlayer.Spark/Queries/SparkQueryInfo.cs:42,56`.
- Streaming switches off other features: `ColumnCapabilities.cs:87,94` (no filter / distincts), `QueryExecutor.cs:146` (distincts empty), `QueryExecutor.cs:310-320` (`/execute` returns an empty result for a streaming query), `SparkSelectionResolver.cs:88`, and composed types are refused (`SparkComposedQueries.cs:67`, `StreamingQueryExecutor.cs:77-84`).
- In use: `apps/Fleet/Fleet/App_Data/Model/Company.json:84-94` (`StreamCompanies`, `Custom.StreamItems`) and `apps/DemoApp/DemoApp/App_Data/Model/Stock.json:87-98` (`StreamStocks`). 22 model files in 6 apps also carry `"isStreamingQuery": false` (37 occurrences).

### 1.2 Server endpoint — `libs/spark/MintPlayer.Spark/Endpoints/Queries/StreamExecuteQuery.cs`
- Route `/{id}/stream` under the queries group (:21). It is the last route variable in Spark (:15-20), because a WebSocket handshake has no body.
- Methods `GET` and `CONNECT` (:37). CONNECT is needed because a browser on HTTP/2 uses RFC 8441 extended CONNECT. Any new socket endpoint has to keep this.
- Checks at the handshake (:60-79): not a WS request → 400. An unknown query, or no type-level `Query` right → **404, the same answer for both**, so the endpoint does not reveal whether a query exists. Not a streaming query → 400.
- Accept (:81). There is one `StreamingDiffEngine` per connection (:83). Each batch is serialized and sent as one text frame (:87-102).
- Endings: when the enumerable finishes the server closes with 1000 "Stream completed" (:105-111). `SparkAccessDeniedException` sends `{type:error,"Access denied"}` and closes with **1011** (:113-116, :137-162). Cancellation and `WebSocketException` are swallowed. Any other exception is logged, and the client gets only the generic "Stream failed" (:125-132, R2-M1).

### 1.3 Executor — `libs/spark/MintPlayer.Spark/Streaming/StreamingQueryExecutor.cs`
- The source must be `Custom.<Method>` (:36-42), and the query must have an `EntityType` (:45-56).
- `EnsureAuthorizedAsync("Query", type)` runs once at open (:59).
- Columns are resolved once on the caller's **attribute-rights query surface** (:68-71). They are fixed for the life of the stream.
- The method is resolved by reflection on `{Entity}Actions`. Signature: `(StreamingQueryArgs, CancellationToken)` returning `IAsyncEnumerable<IReadOnlyList<T>>` or `IAsyncEnumerable<T>` (:92-101, :187-233).
- One connection session with `MaxNumberOfRequestsPerSession = int.MaxValue` is handed to the app (:109-116).
- `StreamingQueryArgs` (`Queries/StreamingQueryArgs.cs`) contains only `Query`, `Session` and `CancellationToken`. It has **no parent, no parameters, no search, no paging**.
- Re-authorization runs every 10 batches (:29-30, :132-144): it re-checks `IsAllowedAsync("Query")` and calls `rowSecurity.ResetRequestFilterCache()`. The comment there says the frozen handshake `ClaimsPrincipal` is a known residual: removing a user from a group is not noticed.
- Per batch, the framework opens a **fresh session** (:152) and runs `IRowSecurityGate.ApplyAsync` (:161-174): row filter → breadcrumb → map → redact. `DedupeById=false`. Then `QueryResultProjector.ToItemsAsync` (:181-183), so a streamed row has the same shape as a fetched row.
- ⚠️ The error text at :81-83 says "Streaming watches a RavenDB collection for changes". **That is false.** Nothing watches RavenDB. All data comes from the app's iterator.

### 1.4 Diff protocol — `Streaming/StreamingMessage.cs`, `Streaming/StreamingDiffEngine.cs`
- Messages: `snapshot{columns,data}`, `patch{updated:[{id,values}]}`, `error{message}`.
- The engine keys rows on `QueryResultItem.Id`. The first batch is a snapshot. After that, a row it has not seen goes out as a patch carrying all of its values (:389-396), and a known row goes out as changed values only. Equality is `object.Equals` (:418-423).
- **Defects, independent of any redesign:**
  1. **Removed rows are never signalled.** `_previousState` is replaced (:399), but no "removed" message exists, so a row that leaves the set stays on the client forever.
  2. **The client drops added rows.** `spark-query-list.component.ts:419-430` maps only over the items it already has, so a patch for an unknown id is ignored. The server sends the row and the grid never shows it.
  3. `object.Equals` on boxed values that came through JSON-ish projections (arrays, references, `TranslatedString`) compares references. Those values re-patch on every batch, or fail to (unverified, but likely for array-valued cells).
  4. There is no total count, no ordering and no paging. The whole set lives on the client, which filters and sorts locally (`spark-query-list.component.ts:244-251, 433-460`; `docs/guide-search.md:176-178`).

### 1.5 Origin and auth on the socket
- `SparkMiddleware.cs:377-404`: `UseWebSockets()` runs before the guard, because the guard needs `IsWebSocketRequest`. An upgrade whose `Origin` **host** differs from the `Host` → 403. A missing Origin is allowed (non-browser clients).
  - It compares the host only, not scheme or port. `https://app:443` and `http://app:8080` count as the same origin. Low risk; mention it in the PRD.
  - The guard applies to every WS upgrade, so a new endpoint is covered automatically.
- E2E: `tests/MintPlayer.Spark.E2E.Tests/Security/WebSocketOriginTests.cs` (Fleet). A foreign Origin is rejected (:24-45). No Origin is accepted against `StreamCompanies`, which is granted to anonymous callers (:47-72).
- Auth is the cookie on the handshake (browsers cannot set `Authorization` on a WebSocket). The principal is frozen for the life of the socket. Cookie expiry and security-stamp changes are not re-checked.

### 1.6 Client
- `libs/node_packages/ng-spark/services/src/spark-streaming.service.ts`:
  - `connectToStreamingQuery(queryId)` returns a cold `Observable`, with **one socket per subscription**.
  - URL (:79-94): ws/wss is derived from `baseUrl` or `window.location`.
  - Reconnect (:44-61): close code 1000 completes the stream. Any other code reconnects with exponential backoff, 1 s·2^n capped at 30 s, up to 10 tries, then errors.
  - ⚠️ "Access denied" closes with 1011, so the client retries 10 times for nothing. A re-authorization failure (`yield break`) closes with 1000, so the grid **silently completes** with stale rows and no message.
  - A reconnect gets a fresh snapshot, because the server builds a new diff engine.
- Types: `libs/node_packages/ng-spark/models/src/streaming-message.ts`.
- Consumer: `libs/node_packages/ng-spark/query-list/src/spark-query-list.component.ts`:
  - an effect connects when `query().isStreamingQuery` (:233-242);
  - `gridData` feeds `streamItems` to the grid (:211-212);
  - the handlers are at :382-431;
  - client-side search and sort are at :433-460.
  - The grid knows not to fetch: `spark-query-grid.component.ts:688,1129`.
  - The socket lives in the page component, not the grid (`guide-queries-and-sorting.md:901`). An embedded `<spark-query-grid>`, a sub-query or a card therefore never streams.
- Specs: `spark-streaming.service.spec.ts` (7 tests), plus `spark-query-list.component.spec.ts`, which covers patches (:218, :291, :331).

### 1.7 Related existing mechanisms the design should reuse
- **Client-operation refresh buses** (they ride the HTTP response and are not pushed; `docs/prd/PRD-ClientOperations.md:53` lists "WebSocket / real-time push" as a non-goal):
  - `SparkQueryRefreshService` (`client-operations/src/query-refresh.service.ts`): a per-query-key token. Grids compare it with the last token they saw (`spark-query-grid.component.ts:724-750`, #319).
  - `SparkAttributeRefreshService`: patches for the object on screen (`spark-po-detail.component.ts:189-200`).
- **PO read pipeline**: `Endpoints/PersistentObject/Get.cs` (`/spark/po/load`). It calls `IDatabaseAccess.GetPersistentObjectAsync` (row security, `GetProtectedAttributesAsync` redaction, attribute rights) and then `IDisabledActionsEvaluator.ApplyOnLoadAsync`. Denial → `SparkDenial.RefuseJson`, which gives the same answer for missing and forbidden (#453).
- **Query read pipeline**: `Endpoints/Queries/Execute.cs` with `ExecuteQueryRequest` (`QueryRequests.cs:27-78`: QueryId, SortColumns, Skip, Take, Search, Columns, ParentId/ParentType, Deleted, ParentDeleted, RetryResults) → `IQueryExecutor.ExecuteQueryAsync(query, parent, skip, take, search, …)` (`QueryExecutor.cs:307`).
- **Concurrency** (`docs/guide-concurrency.md` §1-§4): every PO carries an `etag` (change vector). The edit page merges a 409 with the pure function `mergeThreeWay(base, mine, theirs, schema)` and `SparkPoConflictDialogComponent` (`po-edit/src/conflict-merge.ts`, `spark-po-edit.component.ts:412-507`). A live push into an edit page can reuse both unchanged.
- **RavenDB Changes API: no usage anywhere in the repo.** Messaging uses data subscriptions, and production's Community licence caps those at **3 per database** (memory `reference_raven_subscription_cap.md`). Subscriptions are therefore NOT a viable change source for live UI.

---

## 2. What should drive updates

| Source | Sees | Multi-instance | Cost | Fits |
|---|---|---|---|---|
| A. App `IAsyncEnumerable` (today) | whatever the app yields | ❌ only the instance that holds the socket; a background job on instance B cannot reach a socket on A unless it persists its state | one iterator per socket | non-persisted data (Stock ticker), composed types |
| B. RavenDB Changes API (`store.Changes(db).ForDocument(id)`, `ForDocumentsInCollection(name)`, `ForIndex(name)`) | every committed write, from any process | ✅ every instance gets every notification over its own Changes connection | one multiplexed Changes WebSocket per store/database, shared by all subscriptions; notifications carry id, collection, change vector and type (Put/Delete), **not content** | POs, index/collection queries, job progress written to a document |
| C. In-process notifier (`ISparkLive.Notify(type,id)`) | only explicit calls in this process | ❌ | trivial | fast path; never the only path |

**Recommendation: B by default, with A kept as an explicit app source.** C is not needed once the progress is persisted.
- The SEO audit (and any job) writes its progress to the run document, throttled (for example every 1 s or every N pages). B then delivers it with no live-specific app code, from whichever instance runs the job. This removes the multi-instance problem rather than solving it.
- **Licence:** the Changes API is a core client feature and is believed **not** to be licence-gated. That differs from data subscriptions, which are capped at 3 on Community. **This is unverified. Spike L1 must confirm it against the Community licence file** (`.secrets/raven-community-license.log`; CI and local are Developer, which caps nothing, so they prove nothing — memory `reference_raven_licence_tiers.md`).
- **Changes notifications are not durable.** If the Changes connection drops, notifications are lost. On `ConnectionStatusChanged` → reconnected, mark every subscription dirty and re-evaluate it. When the client reconnects, it resubscribes and gets a fresh snapshot. The current resync-on-reconnect behaviour stays correct.
- **Trigger choice per resource:**
  - **PO** → `ForDocument(id)`, plus optional extra ids from an Actions hook (for example, the documents a computed attribute reads).
  - **Index query** → `ForIndex(indexName)`. Re-run on a `BatchCompleted` change, so that the re-query sees non-stale results, and it covers `LoadDocument` references the index tracks.
  - **Collection / auto-index query** → `ForDocumentsInCollection(collection)`.
  - **Composed / `Custom.*` queries** have no collection, so they need source A, or a hook that declares the collections to watch.

### Security on every push (non-negotiable)
- **Never push raw Changes data.** A notification is only a *dirty bit*. Each push re-runs the **same read pipeline as HTTP** under the socket's principal:
  - PO: `GetPersistentObjectAsync` + `ApplyOnLoadAsync`.
  - Query: the `Execute.cs` body.
  - So row filter, `GetProtectedAttributesAsync` redaction, attribute rights (`IAttributeRightsEnforcement`), breadcrumbs and `disabledActions` are all re-evaluated per push. A grant revoked in security.json applies on the next push, not 10 batches later.
- **Fresh DI scope per evaluation** (`IServiceScopeFactory.CreateAsyncScope`). This replaces the `ResetRequestFilterCache` workaround (`StreamingQueryExecutor.cs:143`) and the uncapped session. Each evaluation gets the normal 30-request cap, a bounded identity map, and per-subscription `IRowPolicyRequestState` (Deleted mode). `IHttpContextAccessor` still resolves the socket's HttpContext, so the principal is right.
- **Missing ≡ no access (#453):** a PO that becomes unreadable or deleted is sent as one `gone`, and the subscription ends. The message does not say whether the PO was deleted or access was revoked.
- **Change-timing oracle:** a notification for a row the user cannot see must cause no visible effect. With re-evaluate-then-diff, an unchanged visible result produces **no message**, so no timing leaks. (A pure "invalidate, client refetches" protocol would leak the existence and timing of writes to hidden rows in the same collection. That is a reason to reject it.)
- Known read-path leaks (memory `reference_spark_read_path_leaks.md`: search substring oracle, breadcrumbs unredacted, Update echo) are inherited, not widened, as long as the live path reuses the HTTP pipeline instead of a fourth copy.
- **Frozen principal:** cap the socket lifetime at the auth ticket's expiry and re-validate the security stamp periodically. When that fails, close with an app code (for example 4401) that tells the client to re-authenticate, rather than 1011 followed by blind retries.

### Throttling, coalescing, back-pressure
- Each subscription has a **dirty flag**, never a queue:
  - at most one evaluation in flight;
  - leading edge, then a trailing edge after a minimum interval (proposed 250–500 ms per subscription, configurable);
  - the latest state wins.
  - A burst of 1,000 writes to the run document costs at most about 2–4 evaluations per second per viewer.
- **Per-socket send loop** fed by a bounded channel, with the latest message per subscription (coalescing replace). If one `SendAsync` blocks longer than N s, close the socket as a slow consumer (spike L4 sets N).
- **Limits:**
  - max subscriptions per socket (for example 50);
  - max sockets per user;
  - subscribing goes through the rate limiter, because a subscribe triggers a full read (#265 options).
  - Spike L3 measures whether WS upgrades currently pass the rate limiter.
- **Fan-out cost:** N viewers of one PO means N pipeline runs per change, because security is per user. Possible optimisation: one shared raw document load per change per instance, then a per-user projection. Spike L3 decides whether that is worth it.

---

## 3. Proposed unified API

### 3.1 Transport: one multiplexed socket per client
**`GET|CONNECT /spark/live`.** It removes the last route variable (`StreamExecuteQuery.cs:15-20`). It is opened lazily on the first subscription and closed when the last one goes away.
- **Why one socket and not one per resource:**
  - a detail page with sub-query grids plus a run list would otherwise open 3–5 sockets;
  - HTTP/1.1 browsers allow about 6 connections per host;
  - each socket also freezes its own principal;
  - with one socket there is one place for auth expiry, limits and heartbeat.
- **Cost:** a small subscribe protocol and per-subscription error handling instead of close codes.
- **Client → server:**
  ```json
  {"op":"subscribe","sub":"s1","po":{"type":"AuditRun","id":"auditruns/1-A","deleted":null}}
  {"op":"subscribe","sub":"s2","query":{ /* exactly ExecuteQueryRequest: queryId, skip, take, sortColumns, search, columns, parentId, parentType, deleted */ }}
  {"op":"update","sub":"s2","query":{...new page/sort/search...}}
  {"op":"unsubscribe","sub":"s1"}
  ```
- **Server → client** (every frame carries `sub`):
  - `po` `{persistentObject}`: the full PO in the exact `/spark/po/load` shape, including `etag`, `can` and `disabledActions`.
  - `gone`: the PO is no longer readable. It does not distinguish deleted from forbidden.
  - `result` `{queryResult}`: the full page in the exact `/execute` shape (columns, items, totalItems, disabledActions).
  - `patch` `{added:[{item,index}], updated:[{id,values}], removed:[id], totalItems}`: optional, and only if spike L2 shows full pages are too big.
  - `error` `{message}`: a generic message, the subscription ends, and the socket stays open.
  - The handshake-time 404 for an unknown or forbidden query becomes a per-subscribe `error` "not found". It is the same answer for both, so the existence oracle stays closed.
  - Heartbeat: a server `ping` every 30 s, below nginx's default `proxy_read_timeout` of 60 s. The docker guide has no WS upgrade/timeout nginx snippet (`docs/guide-docker-deployment.md` ~:143-150), so add one.
- **Mutations never travel over the socket.** Subscriptions are reads, so antiforgery stays HTTP-only and the CSWSH protection (the origin guard) is sufficient.
- `RetryResults`: a query hook that raises a retry action cannot prompt from a push, so it becomes a subscription `error`.

### 3.2 Server-side contract for apps
1. **Nothing, for the common case.** Every PO the caller can read, and every collection- or index-backed query the caller can query, is subscribable. Changes notifications drive it. The SEO audit run detail page and the "running audits" list need **zero live-specific server code**, provided the job persists its progress.
   - *Grill decision G-L1:* default-on, or opt-in per type/query (`"live": true` in the model)? Default-on matches "lean API". Opt-in bounds cost on hot collections.
2. **Extra dependencies (optional hook)** on `DefaultPersistentObjectActions<T>`:
   `public virtual IEnumerable<string> GetLiveDependencies(T entity)`, which returns extra document ids to watch.
   - Use it when a PO's computed attributes read other documents.
   - The query equivalent is `GetLiveCollections(SparkQuery)` for composed/custom queries that should be re-run when a collection changes.
3. **App-produced sources**, replacing the streaming method:
   - keep `Custom.<Method>` returning `IAsyncEnumerable<IReadOnlyList<T>>`, for data that is not in RavenDB (Stock ticker, Fleet demo);
   - rename `StreamingQueryArgs` → `LiveQueryArgs`;
   - it gains `Parent` (a resolved `PersistentObject?`, through the same parent resolution as `/execute`), `Search`, `Skip`/`Take`/`Sort` (the app may ignore them, and the framework applies them in memory as the client does today), `Session`, and `CancellationToken`.
   - Each yielded batch goes through the same gate as today (`IRowSecurityGate` + projector), then the new diff, which handles added and removed rows.
   - Alternative to consider: the iterator yields **triggers** (`IAsyncEnumerable<Unit>`), and the framework re-runs the normal `Custom.` query method. That makes "custom data" and "custom trigger" one concept.
4. `isStreamingQuery` → rename to `"live"` / `IsLiveQuery`, or drop it entirely. Every query becomes subscribable, and only `Custom.*` sources that return `IAsyncEnumerable` are app-driven. The method signature can mark them, so no model flag is needed. Dropping the flag also removes the `/execute`-returns-empty special case (`QueryExecutor.cs:319`).

### 3.3 ng-spark client API
- **`SparkLiveService`** (root, in `@mintplayer/ng-spark/services`, replacing `SparkStreamingService`):
  - one lazy socket, ref-counted subscriptions keyed by a canonical request;
  - two components watching the same PO share one server subscription;
  - reconnect with backoff plus jitter, then **resubscribe everything**, which yields fresh snapshots;
  - it does not reconnect on 4401 (auth) or 4403/limit codes, and surfaces those instead;
  - it runs outside NgZone and enters the zone only to emit.
  - `watchPersistentObject(type, id, opts?) : Observable<LivePoEvent>` (`po` | `gone` | `error`)
  - `watchQuery(request: ExecuteQueryRequest) : Observable<LiveQueryEvent>`
  - Signal helpers for custom pages:
    `livePersistentObject(type: Signal<string>, id: Signal<string>) : Signal<PersistentObject | null | 'gone'>`,
    `liveQuery(request: Signal<ExecuteQueryRequest>) : Signal<QueryResult | null>`.
    An effect re-sends `update` when page, sort or search change.
- **Progress bar on a custom page:**
  `run = livePersistentObject(signal('AuditRun'), runId); progress = computed(() => attr(run(), 'PagesDone') / attr(run(), 'PagesTotal'))`.
- **`spark-po-detail`:**
  - subscribes after `load()` and replaces `item` on `po` (a new etag);
  - re-derives Edit/Delete from `item.can` / `disabledActions`, which arrive with the PO, so the 1.7 note about publishing rights before the object still holds;
  - on `gone`, shows the "deleted or no longer available" state;
  - `context.reload()` stays for add-ons.
  - It can replace or co-exist with the `refreshAttribute` path (`spark-po-detail.component.ts:189-200`).
- **`spark-po-edit` (conflicts):**
  - Keep `base` as loaded. On a `po` push with a different etag:
    - **form not dirty** → silently replace base and form; the next save posts the new etag;
    - **form dirty** → do NOT touch the form. Store `theirs`, run `mergeThreeWay(base, mine, theirs, schema)` and show a non-blocking banner: "Changed by someone else while you edit — Review". If nothing conflicts, *Apply* merges and adopts their etag. If something conflicts, open the existing `SparkPoConflictDialogComponent`.
  - The 409 path stays as the server-side guarantee. Live updates only bring it forward.
  - Read-only attributes (for example, progress) always take theirs, as the merge table already does.
  - `gone` while editing → the same message as a 409 `deleted`.
- **Grids** (`spark-query-grid`, so embedded grids, sub-queries and cards all benefit, not only `spark-query-list`):
  - add an input `live` (or default it on when the query is live);
  - the grid sends its current `ExecuteQueryRequest` (page, sort, search, column filters, parent) as the subscription and replaces its page on `result`;
  - **server-side paging, sort and search keep working**, and client-side filtering of a full set is deleted.
  - `SparkQueryRefreshService` is untouched: a client-operation `refreshQuery` still forces an HTTP reload.

---

## 4. Breaking changes and every place to update

**Server (libs/spark/MintPlayer.Spark):**
- Remove `Endpoints/Queries/StreamExecuteQuery.cs` and add `Endpoints/Live/LiveSocket.cs` (`/spark/live`, GET+CONNECT).
- Rework `Streaming/` (`IStreamingQueryExecutor`, `StreamingQueryExecutor`, `StreamingDiffEngine`, `StreamingMessage`) into `Live/`: subscription registry, Changes watcher (one per store/db), coalescer, send loop, protocol records.
- Rename `Queries/StreamingQueryArgs.cs` → `LiveQueryArgs`.
- Extract the bodies of `Endpoints/Queries/Execute.cs` and `Endpoints/PersistentObject/Get.cs` into reusable services, so HTTP and live share one read path.
- Update or remove the `IsStreamingQuery` uses: `SparkQueryInfo.cs:42,56`, `QueryExecutor.cs:146,319`, `SparkSelectionResolver.cs:88`.
- Remove the comment at `DefaultPersistentObjectActions.cs:518`.
- Fix the false text at `StreamingQueryExecutor.cs:81-83`.
- `SparkMiddleware.cs:384-390`: the comment names `/spark/queries/{id}/stream`.
- Abstractions: `SparkQuery.cs:86-91`, `ModelFileShape.cs:224-229`, `ColumnCapabilities.cs:87,94`, `SparkComposedQueries.cs:67`, `SparkQueryAliasIndex.cs:25` (comment).
- `schemas/model.schema.json:700-703`. A schema change needs a new `schemas/v*` tag (memory: the schema revision is the nearest tag).
- `libs/spark/MintPlayer.Spark/README.md:301` (endpoint table).

**Client (libs/node_packages/ng-spark):**
- `services/src/spark-streaming.service.ts` (+ spec) → `spark-live.service.ts`.
- `models/src/streaming-message.ts`, `models/src/index.ts`, `models/src/spark-query.ts:25`.
- `query-list/src/spark-query-list.component.ts`: :13, :67, :200-251, :382-460 — delete the stream branch and client-side filtering.
- `grid/src/spark-query-grid.component.ts:688,1129`.
- `po-detail/src/spark-po-detail.component.ts`, `po-edit/src/spark-po-edit.component.ts`.
- `src/public-api.ts:4`.
- Specs: `spark-query-list.component.spec.ts`, `spark-query-grid.component.spec.ts`, `spark-query-card.component.spec.ts`, `spark-query-toolbar.spec.ts`.
- Version: a minor bump (stays 22.x), with the break described in the release notes.

**Apps:**
- `apps/Fleet/Fleet/Actions/CompanyActions.cs:15-44` + `App_Data/Model/Company.json:80-94`.
- `apps/DemoApp/DemoApp/Actions/StockActions.cs:15` + `App_Data/Model/Stock.json:83-98` + `App_Data/translations.json`.
- 22 model files: remove or rename `"isStreamingQuery"` (CodeCoverage ×8, DemoApp ×4, Fleet ×3, HR ×4, QnA ×3). `--spark-synchronize-model` may rewrite them. Back up before the bulk edit.

**Tests:**
- `tests/MintPlayer.Spark.Tests/Endpoints/Queries/StreamExecuteQueryTests.cs` (8)
- `Streaming/StreamingDiffEngineTests.cs` (9, plus added/removed cases)
- `Streaming/StreamingQueryExecutorUnitTests.cs` (12)
- `Streaming/StreamingRowSecurityTests.cs` (3, plus per-push revocation and redaction cases)
- `Builder/SparkModelVerifyChecksTests.cs:407-421`
- `Endpoints/Queries/ComposedQueryTests.cs:46,70,98`
- `Services/QueryLoaderTests.cs:141,159`
- `ExecuteCustomActionTests.cs`, `Reflection/ReflectionCacheTests.cs`, `Model/ModelFileShapeQueryFieldsTests.cs` (they mention streaming)
- E2E `Security/WebSocketOriginTests.cs`: re-point to `/spark/live`, plus subscribe-level 404 parity.
- New tests:
  - Changes-driven PO push;
  - a revoked grant gives `gone`;
  - redaction is applied per push;
  - a hidden-row write produces no frame (the oracle);
  - coalescing bound;
  - slow consumer;
  - reconnect resync;
  - an E2E po-edit dirty-form banner.

**Docs:**
- `docs/guide-queries-and-sorting.md:17,814-816,854,901`
- `docs/guide-search.md:176-178`
- `docs/guide-aliases.md:99-122`
- `docs/guide-row-security.md:106,234`
- `docs/guide-authorization.md:127`
- `docs/guide-custom-actions.md:763`
- `docs/guide-concurrency.md` (a new section on live push while editing)
- `docs/prd/PRD-ClientOperations.md:53` (the push non-goal)
- `docs/guide-docker-deployment.md` (nginx WS upgrade and timeouts)
- `docs/seo_analyzer_PRD.md` X4/S8 rows (:179, :197, :263)
- A new `docs/guide-live-updates.md`.

---

## 5. Spike questions (need measurement)

- **L1 — Changes API on Community.** Does `store.Changes().ForDocument/ForDocumentsInCollection/ForIndex` work against the **Community** licence (production)? Is there any connection cap? What is the latency from `SaveChanges` to the notification on the local server? What happens on server restart (`ConnectionStatusChanged`, re-subscription)?
- **L2 — Payload: full page vs patch.** Measure bytes per push for a 50-row page of a realistic type (a CodeCoverage repository list, an SEO run list), full `QueryResult` against a diff. If the full page stays under about 10–20 KB, ship full pages and delete the diff engine.
- **L3 — Evaluation cost.** Measure the cost per push of a full pipeline re-run (PO load + redaction + disabledActions; query page + breadcrumbs) at 1, 10 and 50 concurrent viewers of one run document written at 10 Hz, with a 250 ms vs 500 ms coalescing window. Also measure CPU and Raven requests per second, whether a shared raw load helps, and whether WS upgrades and subscribe frames currently pass the rate limiter.
- **L4 — Back-pressure.** With a throttled client (DevTools offline/slow 3G), check that the server's memory stays bounded and the slow-consumer close fires. Choose N.
- **L5 — Through the real proxy.** Does nginx in production pass `Upgrade` for `/spark/live` (the `/spark/github/dev-ws` precedent suggests yes) and its idle timeout? Do a 30 s ping keep a 10-minute idle socket alive? Is HTTP/2 extended CONNECT reached end to end?
- **L6 — Index-query freshness.** After `ForIndex` `BatchCompleted`, is the re-query non-stale every time? How many evaluations does one write cause when several indexes and collections are watched?
- **L7 — Auth expiry.** Cookie ticket expiry and sliding renewal on a long-lived socket: does closing with 4401 at expiry plus a client reconnect re-authenticate cleanly? Measure the security-stamp re-validation interval.
- **L8 — Edit page under live push.** Run against the existing `mergeThreeWay` matrix: base/etag adoption when the banner is ignored and the user saves (must still 409, then merge), and AsDetail rows. Is a push during an in-flight save resolved by the etag order?
- **S8 (seo PRD :263)** is answered by L1 + L3: push driven by Changes vs app iterator vs polling, with numbers.

## 6. Open decisions for grilling
- **G-L1:** live default-on for every readable PO/query, or opt-in per type/query?
- **G-L2:** full page vs patch (decided by L2).
- **G-L3:** keep app `IAsyncEnumerable` data sources, or turn them into triggers that re-run the normal query?
- **G-L4:** drop `isStreamingQuery` entirely, or rename it to `live`?
- **G-L5:** does po-detail auto-refresh always, or only when the type opts in?
- **G-L6:** subscription and socket limits, and the coalescing window defaults.
