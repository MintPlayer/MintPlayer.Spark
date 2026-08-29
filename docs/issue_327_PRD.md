# PRD — Query result model: rows separate from persistent objects, batched id→entity resolution, and composed queries

> Issue [#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327). Baseline: master @ `5ebfaa45`
> (`10.0.0-preview.65`, `@mintplayer/ng-spark@22.6.0`).

## Problem

PR #325 made a persistent object able to exist **without a CLR class**: `clrType` became optional, and a
JSON-only type's page is composed by `{Name}Actions.OnLoadAsync(id, parent)`, resolved by name. That is a
complete story for a **page**. It is not a story for a **grid**.

A `Custom.*` query on such a type returns an empty list with no diagnostic, because `QueryExecutor`
resolves the row `clrType` before it does anything else. Virtual types are a page feature with a
query-shaped hole in it — and the hole was never exercised: both virtual types that exist today
(`StartPage.json`, `ConfirmDeleteCar.json`) carry `"queries": []`.

The gap surfaced while adopting `preview.65` in the first out-of-tree consumer of program units, whose
home page is a JSON-only virtual PO hosting a grid of computed rows plus a page-level action.

Underneath that immediate gap sit two structural problems, both of which would force a rework later if
this shipped as a narrow patch:

1. **A grid row is currently a full `PersistentObject`.** Rows carry an entity's identity and shape, so
   every mutating path that accepts a posted row treats it as a document. On a virtual type that is
   actively wrong (F13). Spark holds an invariant today — *every row a query returns is a document the
   framework can reload and re-judge* — and composed rows invert it.
2. **The selection load is an N+1**, on entity-backed grids, today, independent of composed queries
   (F10). At `MaxSelectedItems = 200` a bulk action costs hundreds of round trips behind a deliberately
   lifted request cap. This contradicts the codebase's own stated batching principle.

## Governing direction

From the maintainer, and it governs every trade-off in this document:

- Spark is in **preview**. **No backward compatibility is required.** Breaking changes are free; shims,
  `[Obsolete]` and migration windows are not wanted.
- **Rewriting large portions of the framework is acceptable.** Stated reasoning: an enterprise framework
  with a five-year production record uses a separate query-result-item model to work around exactly these
  problems, so Spark most likely needs to as well.
- **Resilience is the acceptance criterion** — the design must not force a rework later.

## Prior art

The reference model is the mature framework's `QueryResultItem`, verified by decompiling
`6.0.20260820.6492` (XML doc comments preserved), with usage evidence from a surveyed estate of 17
production applications built on it. What it does *and what it gets wrong* are both load-bearing here;
§6 of the issue holds the detail and is not repeated.

- **Copy**: the row/entity separation; two security gates (a uniform pre-construction filter, and a
  re-check on every single-entity load); hooks split by axis and phase; type hints as an open,
  string-keyed presentation side-channel; a reason enum threaded through query hooks; and
  `DEV:`-prefixed errors that name the fix.
- **Improve on** eight things it has had five years and not fixed — chiefly: batch the id→entity seam,
  never shrink a selection silently, require explicit row identity, and give the author a real result
  envelope so "I already paged this, here is the true total" is expressible.

## Investigation findings

Verified against the working tree at `5ebfaa45` by four parallel investigations. Every line number below
is current. Findings that **correct or extend** the issue brief are marked ⚠.

### F1 — Type-level authorization runs before the CLR bail, but only on the custom path

`QueryExecutor.cs:239-246` — `EnsureAuthorizedAsync("Query", def.Name)` precedes
`SparkTypeResolver.ResolveClrType`, so making `clrType` optional in the query path costs the type-level
right nothing. Confirmed. The right is anchored on the **model type name**, never the CLR type.

⚠ **The `Database.*` path is the opposite, and this is a security finding the brief did not have.** There
the authorization call sits at `QueryExecutor.cs:138`, *after* five silent bails (`:108`, `:116`, `:122`,
`:130`, `:136`). A `Database.*` query whose entity type is not in the model therefore returns `200 []`
**having never authorized at all**. It leaks nothing today (the result is empty), but it means the
authorization gate is not where a reader would assume, and any future change that makes those bails
return partial data would be a silent hole. Authorization must move above the bails.

### F2 — The mapper already maps unregistered row classes

`EntityMapper.cs:172-189` — `ToPersistentObject(entity, objectTypeId, …)` scaffolds from the definition
looked up by `objectTypeId` (which comes from `query.EntityType`, **not** the row's CLR type), then
reflects the row **by attribute name**, at `:222-224`:

```csharp
var property = entityType.GetCachedProperty(attribute.Name);
if (property is null || !property.CanRead)
    continue; // silent skip — attribute may be projection-only
```

Anonymous types, records and ad-hoc classes all map today. Only the `ResolveClrType` guard and
`actionsResolver.ResolveForType(entityType)` stand in the way. ⚠ The **reverse** gap is also silent: the
loop iterates attributes, not properties, so a property with no matching model attribute is never even
looked at.

### F3 — Row security is document-shaped by construction

`GetRowFilterAsync` returns `Expression<Func<TEntity,bool>>`; `GetProtectedAttributesAsync` takes a `T
entity`; `FilterAsync`/`RedactAsync` reload base documents by id to judge a projection and drop rows they
cannot correlate. A computed row has no document. **Row security is a no-op for composed rows under every
design in this space** — a property of the row, not of any proposal.

### F4 — #325 already shipped this posture on the PO path

`ExecuteCustomAction.cs:205-210`: the per-row gate is guarded by `clrType is not null`, so it is **already
skipped for a virtual type today**, and `LoadVirtualObjectViaActionsAsync` compensates by squaring the
envelope (`DatabaseAccess.cs:497-499`: `Id ??=`, `Breadcrumb ??=`, `Can ??= { Edit = false, Delete = false }`).

### F5 — The client builds columns from metadata, never from rows

`spark-grid-columns.ts:21-25` filters `entityType.attributes` on `isVisible && showedOn ⊇ Query`; cells
match by attribute **name** (`spark-query-grid.component.ts:399`); `canRead()` comes from
`/spark/permissions/{typeId}`; `row.can` is **never read by the grid**; `etag` appears nowhere in the TS
client.

### F6 — `IEnumerable<PersistentObject>` is accepted and is a silent failure

`ExtractQueryableElementType` (`:490-525`) returns `typeof(PersistentObject)` from its first branch and
nothing rejects it. Rows then flow into `ToPersistentObject`, which treats each PO **as an entity**:
correct row count, every cell blank, no error, no log.

⚠ **R12 is worse than the brief states.** The `!= typeof(object)` guard exists only in the third
(interface-scan) branch at `:513-519`. A method declared **directly** as `IEnumerable<object>`,
`IQueryable<object>`, `IEnumerable<dynamic>` or `Task<IQueryable<object>>` hits branch 1 or 2 first and
returns `typeof(object)` unfiltered. The guard only catches `object` arriving via an implemented
interface on a concrete class.

### F7 — Custom actions on a virtual type already work

Both `ExecuteCustomAction` and `ListCustomActions` use `entityType.ClrType?.Split('.').Last() ?? entityType.Name`.

### F8 — Spark has batching discipline and states it as a principle

`IRowSecurity.AreAllowedAsync` is documented *"Batched rather than per-id so it cannot regress into an
N+1 if the identity map is ever cold."* `RowSecurity.LoadBaseDocumentsAsync` (`:433`) resolves
`LoadAsync(IEnumerable<string>, CancellationToken)` — one round trip for all ids. The selection load path
violates this principle (F10).

### F9 — Model-hash coverage of queries is thin

`ModelFileShape.cs` contributes only `name` and `indexName` per query to the structural hash. **`source`
and `entityType` are not hashed** — yet `entityType` gates the request (`Execute.cs:46-50`) and, under any
composed-query design, also selects which actions class and method are invoked.

### F10 — The selection load is an N+1 (⚠ file path corrected)

⚠ The loop is at **`Endpoints/Actions/ExecuteCustomAction.cs:171-184`**, not `Endpoints/PersistentObject/`:

```csharp
var selectedItems = new List<Po>();
foreach (var submitted in request?.SelectedItems ?? [])
{
    var loaded = string.IsNullOrEmpty(submitted.Id)
        ? null
        : await databaseAccess.GetPersistentObjectAsync(entityType.Id, submitted.Id);
    if (loaded is null) return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
    selectedItems.Add(loaded);
}
```

The mitigation lifts the ceiling instead of removing the N+1 (`:140-147`), and `estimatedRequests` is
**only a logging threshold** — `IgnoreMaxRequests` sets `MaxNumberOfRequestsPerSession` to `int.MaxValue`
outright (`SessionExtensions.cs:23`).

**The true per-row request cost is exactly two steps** of the pipeline `#325` moved into
`DefaultPersistentObjectActions.OnLoadAsync` (`:61-130`): the load+includes (`:69-81`) and breadcrumb
resolution (`:100-102`). The collection guard, row `Read` check, mapping, redaction, per-row `can` and
etag cost no I/O after the first row, because `PermissionService` memoizes per request
(`PermissionService.cs:33`) and `RowSecurity` memoizes the compiled filter per (type, action).

### F11 — Everything expensive is already batch-capable, and the missing overload exists

| Step | Batch form | Signature |
|---|---|---|
| breadcrumbs | **already plural** | `ResolveAsync(session, IReadOnlyList<object> roots, def, ct)` — `BreadcrumbResolver.cs:40`; cost is O(depth), *independent of row count* |
| redaction | **already plural** | `RedactAsync(session, IReadOnlyList<(Po, object Row)>, …)` — `RowSecurity.cs:128` |
| row gate | **already plural** | `AreAllowedAsync(session, type, action, IReadOnlyCollection<string> ids)` — `RowSecurity.cs:69` |
| base-document load | batched but `private static`, **no include support** | `LoadBaseDocumentsAsync` — `RowSecurity.cs:433` |
| load + includes for N ids | **exists in the pinned client, unused in this repo** | `IAsyncDocumentSession.LoadAsync<T>(IEnumerable<string>, Action<IIncludeBuilder<T>>, CancellationToken)` — verified present in RavenDB.Client **7.2.5** |

That last row settles the only genuinely open question in M2: batching the load *with* its declared
includes is a documented client API, not something to prototype. The repo uses the single-id include form
and the multi-id plain form, and never the combination.

### F12 — Nothing overrides `OnLoadAsync`, but three classes implement it without the base

Confirmed: **zero** `override` of `OnLoadAsync` and **zero** `base.OnLoadAsync(` calls in `libs/`, `Demo/`
or `tests/` (the only textual hits are two stale pre-#324 signatures in an unrelated doc). But three
classes implement the hook *without deriving from* `DefaultPersistentObjectActions<T>`:

- `Demo/DemoApp/DemoApp/Actions/StartPageActions.cs:24` — duck-typed virtual page.
- `tests/…/Endpoints/PersistentObject/VirtualObjectEndpointTests.cs:123` — likewise.
- `tests/…/Actions/HandWrittenActionsCompatibilityTests.cs:32` — `LegacyHandWrittenActions`, which
  implements *every* member of `IPersistentObjectActions<T>` and inherits nothing.

⚠ That last one is a **deliberate tripwire**: adding a member to `IPersistentObjectActions<T>` breaks it
by design, and `IPersistentObjectActions.cs:89-100` carries a standing warning saying so. This is a real
design lever for M2 — see D2.

### F13 — Row ids fed back into the by-id read path compose the page N times

⚠️ **Still live after M2. Fixed by D12 (M11), not before.**

`ExecuteCustomAction`'s loop calls `GetPersistentObjectAsync`, which for a virtual type lands in
`LoadVirtualObjectViaActionsAsync` → **the page-compose hook**, once per selected row, with `obj.Id ??= id`
stamping row ids onto the page object. Since #325 blesses ignoring the `id`, `SelectedItems` becomes N
copies of the page object wearing row ids — silent, and it feeds an action that writes data.

M2 batched the entity case and left this one untouched: a type with no `clrType` has no documents to
batch, so it still loops the compose hook per id. Only re-running the query produces real rows for a
composed type, which is why D12 materializes a selection that way rather than by id.

### F14 — The wire shape today: rows are full persistent objects, columns are not sent at all

`Abstractions/QueryResult.cs` is the whole contract, serialized directly (there is no query DTO):

```csharp
public sealed class QueryResult
{
    public required IEnumerable<PersistentObject> Data { get; set; }
    public required int TotalRecords { get; set; }
    public required int Skip { get; set; }
    public required int Take { get; set; }
}
```

Each row is a full `PersistentObject`, and **every row repeats the complete attribute metadata** —
`id`, `label` (a `TranslatedString` object), `dataType`, `isRequired`, `order`, `rules[]`, `group`,
`renderer`, `rendererOptions`, `options`, … — because `ScaffoldFrom`/`FromDefinition` rebuild it per row.
`AsDetail` attributes additionally carry fully scaffolded nested POs, recursively, so one grid row can
drag an entire child-collection object graph with per-child metadata.

⚠ **That metadata is entirely redundant payload**: the client already holds the authoritative copy from
the entity-type endpoint and derives columns from it (F5). `can` and `etag` are serialized `null` on this
path. There are **no type hints anywhere on the wire** (zero occurrences repo-wide). Shipping columns once
per result is therefore a large payload reduction, not just a refactor.

⚠ Also: `/spark/queries/{id}/execute` is **GET only** — there is no POST — and `MaxTake = 1000`
(`Execute.cs:110`) clamps only the *page*; `QueryExecutor` materializes the entire result set into a
`List` (`:71`) before `Skip/Take` (`:74`).

### F15 — The silent-bail inventory is nine, not eight

Every one produces an empty grid indistinguishable from "no rows match". `([], false)` returns at
`QueryExecutor.cs` **108, 116, 122, 130, 136, 237, 245, 288, 363**. Plus ten further silent degradations:
`:395` (entity-type inference never implemented), `:541`, `:642` (returns *unprojected* when `ProjectInto`
is not found), `:696`/`:707` (sort column dropped with a `Console.WriteLine`), `:825`, `:917`, `:923`,
`:936`.

`DistinctBy(po => po.Id)` occurs exactly twice — `:222` (Raven fan-out path, correct) and `:382`. ⚠ The
second is **not** exclusively the in-memory path: it is the single return for all three custom shapes
(Raven queryable, in-memory `IQueryable`, plain `IEnumerable`), so dropping it wholesale would also drop
legitimate fan-out dedup for a `Custom.*` query over a Raven index.

### F16 — Streaming dies opaquely on a virtual type

`StreamingQueryExecutor.cs:57-62` throws `InvalidOperationException($"CLR type '{…ClrType}' not found …")`
— which for a virtual type interpolates empty, reading `CLR type '' not found`. Because the method is an
async iterator, the throw is deferred to the first `MoveNextAsync` inside the `await foreach`
(`StreamExecuteQuery.cs:72`) and is swallowed by the generic handler at `:110-119`; the client sees a
successful handshake followed by `{"message":"Stream failed"}`.

### F17 — `IQueryExecutor` has one method, one implementation, one call site

```csharp
Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null,
                                    int skip = 0, int take = 50, string? search = null);
```

No `CancellationToken`. One implementation, one call site (`Execute.cs:138`) — where
`httpContext.RequestAborted` is *already in scope*. Threading a token through is a small, contained
change; `ExecuteQueryableAsync` currently hardcodes `CancellationToken.None` at `:920`.

### F18 — Model hashing is thinner than F9 says, and the fix is one line

⚠ `ModelFileShape.Describe:113-129` does not merely omit `source` and `entityType`: it **skips the query
entirely when `indexName` is absent**:

```csharp
if (!query.TryGetProperty("indexName", out var indexName)
    || indexName.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    continue;
```

So `"queries": []` and `"queries": [ <a whole query with no indexName> ]` hash **identically**. A
hand-authored file can gain an entire composed query — any `source`, any `entityType`, streaming or not —
without moving the file hash by one bit. That is precisely the shape #327 introduces, so this must be
fixed *before* composed queries ship, not after. (`showedOn` is likewise not structural — noted, not
changed here.)

### F19 — Alias collision handling is asymmetric, and the two indexes disagree

`ModelLoader.cs:64-71` warns via `Console.WriteLine` and keeps **first**; `byId[...] = entityType` on
`:62` keeps **last**. So on a collision the two dictionaries resolve to different types.
`SparkQueryAliases.Index:48-49` **throws**, and its doc comment records why it was upgraded from a
`Console.WriteLine`. The entity-type side is the un-upgraded twin.

### F20 — `Verify` returns before its other two checks

`SparkDevelopmentExtensions.cs:183-189` — `VerifyQueryAliasesAreUnique` and
`VerifyRefreshTriggersAreImplemented` sit *inside* the in-sync branch, followed by `return`. A change that
both drifts the hash and collides an alias reports only the drift.

⚠ Also, the remedy the drift message prints — *"Run `--spark-synchronize-model` and commit the regenerated
App_Data/Model"* — is misleading for a hand-authored virtual type: there is no CLR class to regenerate
from, and synchronize only re-stamps the hash file. It works; it just describes the wrong action.

### F21 — Test infrastructure: the idioms exist, with one gap that matters here

`SparkEndpointFactory<TContext>` boots a real `TestServer` over a per-instance temp content root, writes
`security.json` and `modelHashes.json` itself, and swaps in the test `IDocumentStore`; `SparkTestDriver`
gives a **fresh RavenDB database per test case**. `ExecuteQueryEndpointTests` is the query-endpoint
idiom; `VirtualObjectEndpointTests` is the #324 sibling and the closest precedent for a virtual type with
a query.

⚠ **There is no end-to-end HTTP test for custom actions anywhere in the repo.** `ExecuteCustomActionTests`
drives the endpoint class directly with NSubstitute doubles and a hand-built `DefaultHttpContext`; the
only thing that touches `/spark/actions/{type}/{name}` over the wire is a single deny-all table row. Five
of its tests assert `IDatabaseAccess.GetPersistentObjectAsync` **per id**, so they pin the N+1 shape by
construction and must move with it. There is also no test for `MaxSelectedItems`, none for the
`IgnoreMaxRequests` budget, and none exercising `AreAllowedAsync` through the endpoint.

## Design

### D1 — The wire contract: rows carry identity and values; columns ship once

```csharp
public sealed class QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }
    public required IReadOnlyList<QueryResultItem> Items { get; init; }
    public required int TotalItems { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}

public sealed class QueryResultItem
{
    /// Never null and unique within a result — enforced at construction (D6).
    public required string Id { get; init; }
    /// The row's display string: what a reference picker shows and what the first column links.
    public string? Breadcrumb { get; init; }
    public required IReadOnlyList<QueryResultItemValue> Values { get; init; }
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}

public sealed class QueryResultItemValue
{
    public required string Key { get; init; }
    public object? Value { get; init; }
    /// For a reference cell: the target document id, so a cell can link without a second lookup.
    public string? ObjectId { get; init; }
    /// For a reference cell: the target's display string.
    public string? Breadcrumb { get; init; }
    public IReadOnlyDictionary<string, string>? TypeHints { get; init; }
}
```

`QueryColumn` is hoisted from `EntityAttributeDefinition` — `name`, `label`, `dataType`, `order`,
`isSortable`, `renderer`, `rendererOptions`, `referenceType`, `lookupReferenceType`, `asDetailType`,
`typeHints` — and is sent **once per result**, replacing the per-row metadata the client already ignores
(F5, F14). This is a large payload reduction, not only a refactor.

Three deliberate divergences from the prior art, each recorded because a future reader will ask:

1. **`Value` stays typed JSON, not a string.** The reference framework stringifies the whole wire and
   re-types client-side from `column.type`. Spark already converts to JSON-typed values
   (`ConvertValueForWire`), and stringifying would *add* machinery, lose date/number fidelity and churn
   every renderer. Kept typed.
2. **`Id` is non-nullable.** The prior art tolerates a null key (empty disabled row), no key at all
   (**every row shares the id `""`**) and duplicates (silently colliding in a client dictionary keyed by
   id). Spark refuses all three at construction — improvement #3.
3. **`TypeHints` is a dictionary, not a `;`-separated string.** Keys are normalized to lower case once, at
   the serialization boundary, so the client never tries two spellings — improvement #6. Merge order is
   uniform at every level (column → item → value, later wins), rather than the prior art's
   replace-at-one-level/merge-at-the-others split.

`Breadcrumb` on the item is not decoration: `spark-reference-picker` displays
`selected?.breadcrumb || selected?.name || id` and filters on it, and po-form/po-detail use `executeQuery`
for reference option lists. Without a display string on the row, those three surfaces would each need a
second fetch. This is the one field that keeps `executeQuery` usable as an option-list source.

### D2 — One read pipeline, batched internally (M2)

**There is exactly one load hook, and it stays `OnLoadAsync`.** An earlier draft added a plural
`OnLoadManyAsync` to `IPersistentObjectActions<T>`; the maintainer rejected it, and the reference
framework has no such hook either. Batching is an **optimization the framework applies**, not a seam
an actions class implements:

- `DefaultPersistentObjectActions<T>` implements the batched pipeline as `LoadManyAsync`, reached
  through an internal, non-generic `IBatchedLoadActions` — non-generic so `DatabaseAccess` can use
  it without reflection, internal so it is not public surface.
- `OnLoadAsync` is `(await LoadManyAsync([id], parent)).FirstOrDefault()`, so the two cannot drift.
- `SupportsBatchedLoad` is **false as soon as a subclass overrides `OnLoadAsync`**. An override
  decorates the page; taking the batched route would skip that decoration and make a row content
  depend on how many rows were selected. The optimization applies exactly where it is invisible, and
  a decorating actions class falls back to the per-id loop — slower, and correct.
- `IPersistentObjectActions<T>` is therefore **unchanged**, and the `LegacyHandWrittenActions`
  tripwire never fires.

The batched pipeline does:

- one `session.LoadAsync<T>(ids, includes, ct)` — **the overload exists in RavenDB.Client 7.2.5** (F11)
- one `breadcrumbResolver.ResolveAsync(session, entities, definition)` — already plural, O(depth)
- one `rowSecurity.RedactAsync(session, pairs, …)` — already plural
- collection guard, row `Read` check, mapping, `Can`, etag stay per-row and cost no I/O

`OnLoadAsync(id, parent)` then becomes `(await OnLoadManyAsync([id], parent)).FirstOrDefault()` — **one
pipeline, so single and batch cannot drift.** That property is why the plural is the primitive rather than
a sibling.

Returns rows **in the order the ids were given**, omitting any id that names no document, names a foreign
collection, or is refused by the row rule — the three reasons deliberately indistinguishable, so no
existence oracle. Duplicate ids collapse to one row.

⚠ Adding a member to `IPersistentObjectActions<T>` breaks `LegacyHandWrittenActions`
(`HandWrittenActionsCompatibilityTests`) **by design** — that class exists as a tripwire and the interface
carries a standing warning saying so (F12). Firing it is the correct outcome here, not collateral damage:
the batch form must be on the interface for the "one pipeline" property to hold. The test's implementation
is updated; the tripwire's purpose is served by making the decision conscious.

`IDatabaseAccess` gains the batch sibling, keeping the `EnsureAuthorizedAsync("Read", …)` type gate (which
memoizes per request, so N ids cost one decision) and the virtual-type fork. `ExecuteCustomAction` makes
one call and **refuses when the returned count is short of the requested count** — improvement #2, and the
opposite of the prior art, which drops unresolvable rows silently so a bulk action can act on 498 of 500.
The `estimatedRequests` budget becomes a small constant and the `#239 M5` comment is rewritten, since it
currently documents lifting the ceiling *as the fix*.

### D3 — `clrType` optional in the query path

`ExecuteCustomQueryAsync` resolves the actions class by **name** (`ResolveByEntityName(def.Name)`, the
seam #325 already added) instead of `ResolveForType`, and the CLR-type bail at `:242-246` goes away.
Consequences, each made explicit rather than inferred:

- **Row security is skipped, because there is nothing to evaluate** (F3). This is not a policy choice; a
  computed row has no document to re-judge. The rule is written into the doc comment of the hook that
  enforces it — improvement #8, and the prior art has no such note anywhere.
- **Every composed query gets a loud startup diagnostic** naming the type and stating that row filtering,
  redaction and per-row permissions are the actions class's responsibility (§12 of the issue). This is the
  containment for the real risk: not this one home page, but the next developer who reaches for a composed
  query because it is easier than writing a row rule, over real sensitive data, and gets a grid that looks
  exactly like every other Spark grid.
- **The per-row envelope is squared closed** — `Can = { Edit = false, Delete = false }`, matching what
  `LoadVirtualObjectViaActionsAsync` already does on the page path (F4).
- **Streaming is refused for a `clrType`-less type at model-verify and at `QueryLoader` index-build time**,
  not at the first `MoveNext` inside a websocket where it currently dies as `CLR type '' not found` →
  `{"message":"Stream failed"}` (F16).

### D4 — The author's result envelope, and the paging authority rule

A custom query method may return `SparkQueryPage<T>` instead of a bare sequence:

```csharp
public sealed record SparkQueryPage<T>(IReadOnlyList<T> Items, int TotalItems);
```

This is improvement #5 — *the single most valuable divergence available*. The prior art makes `PageSize`,
`Skip` and `TotalItems` `internal set`, which forced its own log query to hack the `Sort` override, after
which the framework's `.Count()` reported the trimmed count.

**The authority rule is binary (R6): the framework owns filtering, search, sorting, count and paging — or
the author does. No partial delegation.** Returning `SparkQueryPage<T>` transfers all five; returning a
sequence keeps all five. A half-delegated design sorts the current page and presents it as globally
sorted, and that failure is invisible.

### D5 — Type hints

An open, untyped, string-keyed side-channel at three levels (column, item, value), merged later-wins,
keys lower-cased once at the boundary. No registry and no validation — that openness is how an app adds
its own keys with zero framework change. Server-consumed keys are documented; everything else is passed
through to the client untouched.

### D6 — Every silent bail becomes loud

The nine `([], false)` returns and ten further silent degradations (F15) become either a thrown
`DEV:`-style error naming the fix, or a startup/verify-time refusal — following the precedent already set
by `LoadVirtualObjectViaActionsAsync`, which throws on a shape mismatch rather than 404ing. Specifically:

- `DistinctBy` stays on the Raven path (`:222`, fan-out is semantically expected) and is **removed from
  the custom path** (`:382`). ⚠ Because `:382` is the single return for all three custom shapes, the Raven
  sub-case keeps dedup explicitly rather than losing it by accident (F15).
- A duplicate or null row id on a composed path is an **authoring bug → throw**, never a collapsed grid.
- `PersistentObject` and `object`/`dynamic` are rejected as custom-query element types with a message that
  names the declared return type — closing F6 and the wider R12 hole.
- In-memory **sorting** is added beside the existing in-memory search fallback, killing S3.
- Authorization moves above the `Database.*` bails (F1).

### D7 — Model hashing, alias symmetry, verify ordering

- `source` and `entityType` become structural in `ModelFileShape.Describe`, **and the `indexName`-gated
  skip is removed** so a query always contributes a line (F18). Hash rebake across all four demo apps.
- Entity-type alias collision **throws**, symmetrically with `SparkQueryAliases.Index` (F19), and `byId`'s
  last-wins is aligned so the two indexes cannot disagree.
- `Verify` runs the alias and refresh-trigger checks regardless of hash drift (F20), and the drift message
  distinguishes a hand-authored file from a generated one.

### D8 — `CancellationToken` through `IQueryExecutor`

One method, one implementation, one call site, and `httpContext.RequestAborted` already in scope (F17).
Threaded while the signature is changing anyway; `ExecuteQueryableAsync`'s hardcoded
`CancellationToken.None` goes. Composed row counts are capped loudly (R5).

### D9 — Client migration

The surface is small because the row shape has few consumers:

| Seam | File |
|---|---|
| the one fetch | `services/src/spark.service.ts` |
| row → value | `pipes/src/query-cell.pipe.ts` (new), `renderers/src/renderer-inputs.ts::cellValue` |
| renderer bag | `grid/src/spark-grid-renderers.ts` |
| row route | `grid/src/spark-query-grid.component.html` |
| column source | flips from `EntityType.attributes` to per-result `columns` |

**No compatibility shim** (S2, rejected). Renderers receive **`column`** rather than `attribute`,
typed `SparkCellColumn` — a small interface carrying only what a cell needs (`name`, `label`,
`dataType`, `isArray`, `renderer`, `rendererOptions`, `referenceType`, `lookupReferenceType`,
`asDetailType`). Both `QueryColumn` and `EntityAttributeDefinition` satisfy it structurally, which is
what lets **one** cell component serve the query grid and the AsDetail sub-table without either side
knowing the other's shape. Detail and edit renderers keep `attribute`: those paths really are
attribute-shaped, and renaming them would be churn for its own sake.

A host that binds `data` itself must also bind `columns`. That is not an oversight to paper over: a
projection cannot describe itself, and the old fallback — inferring columns from whichever attributes
the first row happened to carry — is exactly the per-row metadata this work removes.

**Two client fallbacks disappear**, and both were reading a `name` that no longer exists: the
reference picker's display value and the AsDetail cell's option lookup now go from `breadcrumb`
straight to the id. Not a regression — `breadcrumb` *is* the server-resolved display string, and an id
beats an empty cell.

R3 is fixed in the same pass: `clrType?: string` on the TS model and a guarded `?.endsWith(...)`.

### D10 — Projection, not a second mapper

The mapping pipeline still builds a `PersistentObject` per row internally; `QueryResultProjector`
projects it at the boundary. **Deliberate, not transitional.** Redaction
(`IRowSecurity.RedactAsync`), breadcrumb resolution and reference display all operate on
attribute-shaped rows, and re-expressing them over a second row type would mean two implementations
of the same security-relevant logic — the kind of duplication that stays identical right up until it
doesn't. Projecting keeps one, and the saving that actually mattered (the wire) is realised either
way. The internal allocation is a later optimization behind an unchanged contract.

One consequence worth stating: an **AsDetail** column has no flat value by construction — the mapper
nulls it and puts the children on `Object`/`Objects`, which a projection does not carry. Rather than
render an empty cell where a count used to be, the projector emits the two facts a grid can use: the
child **count** (data, so the client owns the wording and its pluralisation) and, for a single child,
the breadcrumb the server already resolved.

### D11 — `image` / `url` data types, and `rowRoute`

`GetDataType` gains `"image"` and `"url"`. On the client both need branches in
`spark-grid-cell.component.html` **before** the chips/link/text fallthrough, styled with inline
`[style.*]` — the grid renders inside `mp-datatable`'s shadow root, where neither component-scoped SCSS
nor Bootstrap utilities arrive (measured, and documented verbatim in `spark-grid-cell.component.scss:1-13`).
The detail page has its own `dataType` chain and needs the same branches or the new types render as raw
text there.

`rowRoute` is an optional `(row) => unknown[] | null` input that **replaces the anchor's target** while
leaving the `canRead()` gate exactly where it is. It exists because an app whose rows have a canonical
non-PO route must otherwise withhold `Read` and re-implement the link in a custom renderer — which cannot
work for the first column, since `cellContent` is projected *inside* the framework's anchor and nested
anchors are invalid HTML (confirmed verbatim in the template comment).

### D12 — `CustomActionArgs.SelectedItems` becomes `QueryResultItem[]`

The half of the issue's title that M2/M4 did not land. The wire became ids and the resolution became
one batched load; the **author-facing** type stayed `PersistentObject[]`, which is neither what the
issue's research described nor what the grid actually had.

**A selection is re-materialized by re-running the query it came from, restricted to the selected
ids.** Not by loading documents.

That is the whole design, and it follows from one observation: *the rows an action receives should be
the rows the caller was looking at*. A query knows its own index binding, its own projection and its
own filter; re-running it reproduces exactly what the grid showed. Loading documents by id
re-derives something adjacent, and the two disagree in ways nothing reports.

#### Why not load the documents

Three reasons, in ascending order of severity.

1. **Index-computed columns come back null.** A value computed inside an index and stored there — the
   demo's `OwnerFullName`, produced by a `LoadDocument` in the index map — is on neither the document
   nor the CLR class. The mapper scaffolds the attribute from the definition, finds no property, and
   skips it deliberately ("attribute may be projection-only"); the projector then emits an empty
   cell. Silent at every step. Re-running the query gets the value because the query reads the index.
2. **The wrong index.** Materializing by id can only use the type's *default* binding. A query that
   names its own — as `company-cars` does — has a different projection, so an id-based materialization
   silently returns a different row shape than the grid rendered.
3. **A composed query cannot be materialized by id at all.** This is F13, and it is live today. A
   type with no `clrType` has no documents; the selection path falls into a per-id loop calling the
   *page-compose* hook, so `SelectedItems` becomes **N copies of the page object wearing row ids** —
   silently, feeding an action that writes data. M2 batched the entity case and left this exactly as
   it was. Re-execution is the only thing that produces real rows here, which on its own decides the
   design.

#### The shape

The client posts the ids it already posts, plus the **query** they came from. The server:

1. Runs every existing gate first, unchanged and upstream — the type-level grant on
   `(action, type)`, the `customActions.json` listing, the 200-id ceiling, the declared selection
   rule, and the id-less refusal.
2. Resolves the query and **verifies its entity type is the action's route type**. A mismatch is a
   refusal. The query id is client-supplied, so this is the check that keeps it from naming
   something else.
3. Re-executes it restricted to the selected ids, through `IQueryExecutor`.
4. Applies the row gate on the action's own name over the returned ids.

The result is already `QueryResultItem[]` — the executor produces exactly that shape for the grid,
so there is no second projection path and no way for the two to drift.

**Cost is unchanged:** one round trip, replacing the document load rather than adding to it.

#### Why the client-supplied query id is safe

It is a *narrowing* input, not an authorizing one. The executor enforces the `Query` right on it
independently, so naming another query yields only what the caller could have fetched directly. The
route type check above stops it from yielding rows of a different type. And the ids only filter —
they cannot introduce a row the query would not otherwise have returned, which is why the collection
guard is not needed on this path: **the query defines the collection.**

#### What re-execution trades

The load path applies `Read` row-filtering plus the collection guard. Re-execution applies the
`Query` row filter and `Query` redaction instead. That is not a weakening — it is the *same*
filtering the grid applied when it showed the caller those rows, which is the correct semantic for
"the user selected these". A row the caller may no longer see simply does not come back, and the
all-or-nothing rule below turns that into a refusal.

#### Three fallbacks, and one rule for all of them

Re-execution is impossible for three shapes. Each falls back to the batched document load, projected
the same way:

| Case | Why | Consequence |
|---|---|---|
| `SparkQueryPage<T>` | The author owns filtering and paging; there is no way to ask for "the page containing these ids" | Index-computed columns null |
| A streaming query | Its method takes `(StreamingQueryArgs, CancellationToken)` and is not resolvable as a custom query | Index-computed columns null |
| No query id | A direct POST, or a caller that predates the field | Index-computed columns null |

Whether a query owns its own paging is known from the resolved method's return type before it is
invoked, so this is a decision, not a failed attempt.

**The rule that holds on every path: the returned ids must exactly equal the caller's distinct
requested set, or the whole request is refused.** This is the existing all-or-nothing property, and
it is what makes the fallback safe rather than a silent downgrade — a re-execution that quietly
ignored the id restriction returns the wrong set and is caught by the same comparison.

#### What `SelectedItems` carries, and what it does not

Rows carry the **query surface** — the columns the grid had — on every path, including the
fallbacks. Deliberately not a superset:

- On the re-execution path a superset is not available; the query projects what it projects.
- Making the fallback richer than the primary path would mean an action's data depended on which
  shape its query happened to be, which is exactly the kind of invisible variation this work removes.

An action that needs a **detail-only attribute, the entity itself, or an etag** materializes it
through the hook below. That is the trade the prior art makes too, and it is what keeps a row a row.

Genuinely lost, and recorded so it is a decision rather than a discovery: **`Etag` and the `Can`
block.** `QueryResultItem` carries neither. No action reads either today; one wanting optimistic
concurrency uses the hook.

#### The materialization hook

`DefaultPersistentObjectActions<T>` gains a `public virtual` hook marked `[NoInterfaceMember]`,
following `GetDefaultIncludes`, called from inside `LoadManyAsync` at the load itself — the only
place `[Reference]` includes can still be attached. It is what an action calls to turn selected rows
into entities, and what the fallback paths use internally.

It stays **off** `IPersistentObjectActions<T>` deliberately. The hook is typed on `T` *and* needs the
framework-attached session, so a hand-written implementer that inherits nothing could not provide one
meaningfully — a member that cannot reasonably be implemented from outside the framework is exactly
what the hand-written-actions tripwire exists to prevent. Nothing in `IBatchedLoadActions` changes,
and that tripwire does not fire.

#### Invariants that fail silently if this is built wrong

Each is already pinned by a test. Needing to edit one of those tests to compile is a signal to
re-read this section, not to edit the test.

1. **The all-or-nothing comparison must be against what the source actually yielded**, never against
   the submitted ids zipped with results or padded with id-only stubs. Built that way it degrades to
   `n == n`, and a bulk action quietly processes 498 of 500.
2. **The row gate on the action's own name must take ids from the materialized rows**, not from the
   submitted list. `IRowSecurity.AreAllowedAsync` applies neither the type right nor the collection
   guard — it is a refinement, never an entry point.
3. **The id-less refusal stays upstream of materialization.** It is a pre-check on the submitted ids
   and must not be folded into whatever produces the rows.
4. **Redaction must have run before projection on the fallback path.** `RedactAsync` mutates the
   mapped `PersistentObject` in place and the load path already runs it; projecting from raw entities
   bypasses it entirely. The re-execution path inherits the query pipeline's own redaction.

#### One hazard to decide deliberately

A composed type's rows are produced by an actions method and are not guaranteed to carry an id. The
projector throws on a null or duplicate id, which through the generic handler becomes a 500 with the
cause only in the log. It should refuse at the endpoint, naming the type — the same treatment M5 gave
every other composed-query authoring error.


## Breaking changes

All intentional; preview, so no shims, no `[Obsolete]`, no migration window (per the governing
direction). Marked ✅ where already landed on the branch.

**Wire**

1. ✅ `QueryResult` changes shape entirely: `Data: PersistentObject[]` → `Items: QueryResultItem[]`
   plus `Columns`, and `TotalRecords` → `TotalItems`.
2. ✅ The streaming protocol follows: the snapshot carries `columns` once, and a patch carries
   `values` keyed by column name rather than `attributes`.
3. ✅ A custom action's selection is **ids**: `CustomActionRequest.SelectedItems`
   (`PersistentObject[]`) → `SelectedItemIds` (`string[]`), and
   `CustomActionArgs.SubmittedSelectedItems` is **removed** — a selected row is named by an id, never
   submitted as an object. `SubmittedParent` stays; an unsaved detail form really does submit state.
4. ✅ `SparkClient.ExecuteActionAsync` takes `selectedItemIds`.

**Client API**

5. ✅ A column renderer receives `column` (typed `SparkCellColumn`) instead of `attribute`. Detail and
   edit renderers keep `attribute` — those paths really are attribute-shaped.
6. ✅ `visibleGridAttributes` is gone; columns come from the result. A host binding `data` itself must
   also bind `columns`.
7. ✅ `EntityType.clrType` is optional, matching the null the server sends for a virtual type (R3).
8. ✅ The reference picker and the AsDetail option lookup fall back from `breadcrumb` straight to the
   id — the `name` they used to try went with the persistent-object row shape.

**Server API**

9. ✅ `IDatabaseAccess` gains `GetPersistentObjectsByIdAsync`. ⚠️ M11 replaces this member: with
    selections materialized by re-running their query, it has no caller left.
10. ✅ `IQueryExecutor.ExecuteQueryAsync` gains a `CancellationToken`, and `IRowSecurity`'s
    `FilterAsync` / `ComposeRowFilterAsync` / `RedactAsync` gain one each (M7).
11. ✅ Custom queries returning `IEnumerable<PersistentObject>`, `IEnumerable<object>` or `dynamic` now
    throw instead of silently producing blank rows.
12. ✅ A query producing a row with **no id**, or two rows sharing one, now throws. This landed in M4
    rather than M5: `QueryResultItem.Id` is non-nullable, so the rule arrived with the type.
13. ✅ Entity-type alias collisions throw instead of warning.
14. ✅ `source` and `entityType` are structural in the model hash → all four demo apps rebaked.
15. ✅ Streaming on a `clrType`-less type is refused at build/verify time, and a composed type
    carrying a query must show at least one attribute on it (M5).
16. ✅ Eight silent `([], false)` bails in the query executor now throw, naming the misconfiguration
    and its fix; `ModelLoader` no longer swallows a model that contradicts itself (M6).
17. ✅ A custom query method may return `SparkQueryPage<T>`, which transfers filter/search/sort/
    count/page to the author — all five or none (M5). `CustomQueryArgs` gains `Skip`, `Take`,
    `Search`.

18. `CustomActionArgs.SelectedItems` becomes `QueryResultItem[]` (M11). Every consumer using
    `args.Parent ?? args.SelectedItems.FirstOrDefault()` stops compiling — `Parent` stays a
    `PersistentObject`, so the two no longer unify; coalesce on ids instead. Rows carry the query
    surface, not the full attribute set: an action needing more materializes through the hook.
19. `CustomActionRequest` gains the query the selection came from (M11), so it can be re-executed.
    Client-supplied and therefore narrowing-only: the `Query` right is enforced on it independently,
    and its entity type must match the action's route type.

## Out of scope (genuinely not being done)

- **Row security for composed rows.** Not deferred — impossible by construction (F3). A computed row has
  no document to re-judge. Documented in the enforcing hook and surfaced as a startup diagnostic.
- **Stringifying the wire.** Rejected in D1 with reasons.
- **A per-row `can` in the grid.** The wire field exists and is defaulted closed; the grid does not read
  it and this work does not make it do so. Not described as a feature.
- **`[SparkIgnore]`/`[SparkView]` marker classes.** Rejected in §4A of the issue and previously rejected by
  this repo in #324; the absence of `clrType` is already the better declaration.
- **A full rights lattice.** Out of scope here; `Read ⇒ Query` shipped in #325 and #326 tracks the warning.

## Spikes

- **S1 — batch load with includes.** ~~Prototype~~ **resolved during investigation**: the overload
  `LoadAsync<T>(IEnumerable<string>, Action<IIncludeBuilder<T>>, CancellationToken)` is present in
  RavenDB.Client 7.2.5 (verified in the shipped XML docs). No spike needed; M2 uses it directly.
- **S2 — renderer compatibility shim.** ~~Prototype~~ **rejected, not run.** It would have
  reconstructed the old `(value, attribute)` bag from `(itemValue, column)` so renderers migrated once
  centrally. Once "no backward compatibility" was restated as the governing rule, the shim stopped
  being worth de-risking — and it was worse than unnecessary: the bag it rebuilds hands every column
  renderer an `EntityAttributeDefinition` the grid no longer possesses, i.e. fabricated metadata a
  projection deliberately does not carry. Renderers take `column` instead (D9); the two demo column
  renderers were updated by hand, which was a two-line change each.
- **S3 — composed query end to end.** Still wanted, and now the substance of M5 rather than a spike
  ahead of it: M4 landed the contract a composed query renders through, so the prototype and the
  milestone are the same work.
