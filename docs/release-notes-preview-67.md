# Release notes — `10.0.0-preview.67` / `@mintplayer/ng-spark@22.8.0` / `@mintplayer/ng-spark-auth@22.8.0`

A **query row is a projection, and a persistent object is a document** (#327). Separating the two
shrinks the wire, makes composed queries safe to offer, and turns a long list of silent wrong
answers into loud ones.

This is a breaking release on both sides of the wire. Everything below is intentional; preview, so
no shims and no migration window.

Full guides: `docs/guide-queries-and-sorting.md`, `docs/guide-custom-actions.md`,
`docs/guide-custom-attribute-renderers.md`.

---

## The query wire contract

`QueryResult` is now **columns once, then one lightweight row per result**:

```jsonc
{
  "columns": [ { "name": "LicensePlate", "label": { "en": "Plate" }, "dataType": "string" } ],
  "items":   [ { "id": "cars/1-A", "breadcrumb": "1-ABC-123",
                 "values": [ { "key": "LicensePlate", "value": "1-ABC-123" } ] } ],
  "totalItems": 42, "skip": 0, "take": 50
}
```

Rows used to be full `PersistentObject`s, so every row carried a complete copy of the attribute
metadata — label, dataType, rules, renderer options, and for an AsDetail attribute the whole nested
object graph — that the client already held from `GET /spark/types` and never read off the row.

The payload saving is real but secondary. The point is that a row now **cannot pretend to be a
document**: it carries no `can` block and no etag, because neither can be trusted from a projection.
Nothing treats a posted row id as verified — every mutating path re-materializes from the id through
the same load path a detail page uses and re-applies security there, which is why row ids can be
treated as hostile input with no integrity token on the wire.

Two rules are now enforced rather than tolerated: a row **must** have an id, and two rows **may
not** share one. Both used to fail silently — a null id collapsed the grid to a single row (every
null key compares equal), duplicates rendered the same row repeatedly with a matching total.

**Type hints**: `columns`, `items` and `values` may each carry a `typeHints` map, merged
column → item → value with later winning, keys lower-cased at the boundary. No registry and no
validation — that openness is how an app adds its own keys with zero framework change.

## Composed queries: rows with no documents behind them

A query whose entity type declares **no `clrType`** is now supported end to end. There is no entity
class, no collection and no document behind a row — the rows are computed by `{Name}Actions`, found
by name, the same seam the virtual-type page path already used.

```csharp
public async Task<IEnumerable<CollectionRow>> GetCollections() =>
[
    new CollectionRow("collections/people", "People", await session.Query<Person>().CountAsync()),
];

public sealed record CollectionRow(string Id, string Collection, int Records);
```

### ⚠️ Row-level security does not run over a composed query, and cannot

`FilterAsync` re-judges a **stored** document; `RedactAsync` compares against stored values. A
computed row has neither. Both are skipped, and no configuration turns them back on.

The type-level `Query` right still applies — it was never a row-shaped question. Everything else is
the actions class's job: filtering rows to what the caller may see, omitting values they may not
read, gating what the rows can be acted upon with.

**Every composed query says so at startup**, naming the type. That line exists because a composed
grid is indistinguishable from every other Spark grid once rendered. The risk is not the deliberate
landing page someone wrote on purpose — it is the next developer who reaches for a composed query
because it is easier than writing a row rule, over data that does have owners.

Two things a composed type may not do, refused at `--spark-verify-model` and at query-load time
rather than when someone opens the page: **stream** (there is no collection to watch — this used to
die at the first `MoveNext` inside a websocket as `CLR type '' not found`), and **carry a query while
showing nothing on it** (rows with no columns is a blank grid that reads as an empty result).

## `SparkQueryPage<T>` — the author's own page

For a source the framework cannot page: an external API with its own offset, an aggregate whose
total is a separate query, a log store that answers in chunks.

```csharp
public async Task<SparkQueryPage<LogRow>> GetLogs(CustomQueryArgs args)
{
    var (rows, total) = await logApi.FetchAsync(args.Skip, args.Take, args.Search);
    return new SparkQueryPage<LogRow>(rows, total);
}
```

**The authority rule is binary**: the framework owns filtering, search, sorting, counting and paging,
or the method does — never some of each. `CustomQueryArgs` gains `Skip`, `Take` and `Search` so the
method can honour them.

No partial mode, because half-delegation fails invisibly: a framework sort over a method-trimmed page
sorts *the current page* and presents it as a global ordering — every page internally sorted, the
sequence across pages wrong, nothing about the result saying so. Row security is **not** part of what
transfers.

## Silent failures became loud

Nine paths in the query executor returned an empty result, which is byte-identical to a correctly
configured query over an empty collection. All of them now name the misconfiguration and the fix: no
SparkContext registered, a context property that does not exist or is not readable or returned null
or has no element type, an entity type with no model file, a custom query with no `entityType`, a
query method that returned null or something that is not a sequence.

Also refused: a custom query returning `IEnumerable<object>`, `IEnumerable<dynamic>` or
`IEnumerable<PersistentObject>`. All three produced the same silent wrong answer — the right number
of rows, every cell blank.

**Authorization moved above all of it.** Reflecting over the context, reading a property and matching
a CLR type to a model file used to run for a caller with no `Query` right at all, because the only
check sat after the last of them — so a misconfigured query answered a denied caller with an empty
grid instead of a denial.

`ModelLoader` no longer swallows a model that contradicts itself: its per-file catch-all was
discarding the entity alias-collision error it had just raised, so the application started with one
of two types unroutable and a console line nobody reads.

## The selection load is no longer an N+1

A custom action over a selection resolved one document per selected row. It is now one batched load
through the same pipeline a single row uses, so single and batch cannot drift — 50 rows now cost
what 5 did. A subclass that overrides `OnLoadAsync` opts out automatically, so a decorated page can
never be skipped by a bulk path.

`ExecuteCustomAction` **refuses a short result** rather than acting on 498 of 500 selected rows.

## `showedOn` decides the wire; `isVisible` decides the drawing

A column is on the query wire when its attribute's `showedOn` includes `Query`. `isVisible` no longer
filters server-side — it is carried to the client and applied there.

That split lets an app **ship a value without giving it a column**:

```jsonc
{ "name": "IsPrivate", "dataType": "boolean", "showedOn": "Query", "isVisible": false }
```

Every row then carries `IsPrivate`, no column is drawn for it, and a custom renderer on a *different*
column can read it — a lock glyph beside a name, without spending a column on the fact. Previously
the only way to get a value to a renderer was to make it a visible column, which is exactly the
layout decision such an app is avoiding.

It also removes an inconsistency that was introduced rather than inherited: **the sort allow-list
checks `showedOn` alone**, so filtering on both flags made an attribute marked
`"showedOn": "Query", "isVisible": false` *sortable with no column* — the kind of thing that reads as
a bug report from a user who cannot possibly explain it.

Not a disclosure widening: rows carried every attribute regardless of both flags before this
release, so it stays strictly narrower than what shipped previously, and per-caller redaction is
untouched — that nulls values on the row, never on the definition.

## `valueFor` reads a row of any shape

A renderer's `item` arrives in three shapes — a `QueryResultItem` in a query grid, a flat record in
an AsDetail sub-table, the `PersistentObject` itself on a detail page — and they are genuinely
different objects rather than variations of one. `valueFor(item, key)` now normalises all three onto
the same cell, so a renderer reused across a grid and an AsDetail table stops needing a branch:

```ts
readonly isPrivate = computed(() => valueFor(this.item(), 'IsPrivate')?.value === true);
```

It derives what each shape expresses differently — a `PersistentObject` reference carries no
`objectId` because its *value* is the target's id; an AsDetail row keeps its resolved label in a
side channel. Type the input as `SparkRow`. `isQueryRow` and `isPersistentObject` are exported for
the cases that genuinely need to know.

Raised by the first consumer, whose own helper's branch count would otherwise have gone **up** after
this release rather than to zero.

## A sub-query's action knows which page it was on

A grid rendered on another object's detail page now passes that object to its custom actions, as
`CustomActionArgs.QueryParent`. It could not before: the grid knew its parent (it filters the rows by
it) and dropped the fact on the way out, so an action invoked from a company's Cars tab could not
tell it was on a company's page at all.

⚠️ It is **not** `Parent`, and that separation is load-bearing. `Parent` means an object of the
action's own type, and the server loads it under the route's type on purpose. A sub-query's container
is a *different* type by construction — the cars listed on a company's page are Cars, the page is a
Company — so it travels as `parentId` + `parentType` and resolves under its own type, with that
type's own `Read` gate. A container the caller may not see refuses the request.

`SparkClient.ExecuteActionAsync` and `SparkService.executeCustomAction` gain the corresponding
optional arguments. Try it in DemoApp: open a company, use the **Cars** tab, tick rows, and click
**Copy to this company** — the copies are assigned to that company.

## Cancellation

`IQueryExecutor.ExecuteQueryAsync` takes a `CancellationToken`, wired from
`httpContext.RequestAborted`. `ToListAsync` was pinned to `CancellationToken.None`, so a cancelled
request kept materializing its result after the socket was gone. `IRowSecurity.FilterAsync` /
`ComposeRowFilterAsync` / `RedactAsync` take one too.

## Client

- **Column renderers receive `column: SparkCellColumn`, not `attribute`.** There is no
  `EntityAttributeDefinition` to hand a cell any more. Detail and edit renderers keep `attribute` —
  those paths really are attribute-shaped. `SparkCellColumn` is satisfied structurally by both
  `QueryColumn` and `EntityAttributeDefinition`, which is what lets one cell component serve the
  query grid and the AsDetail sub-table.
- **A host binding `data` must also bind `columns`.** A projection cannot describe itself, and the
  old fallback inferred columns from whichever attributes the first row happened to carry.
- **`rowRoute`** on `spark-query-grid`: an optional per-row function replacing where the first
  column's link points; return `null` to suppress it for that row. It changes the destination, never
  the gate — `canRead()` still decides whether any link renders.
- **`image` and `url` data types**, rendered in the grid, on the detail page, and mapped to an input
  type for editing. Both are presentation-only overrides of a string property, hand-authored in the
  model file and preserved across `--spark-synchronize-model` (as `MultiLineString` already was).
- **`SparkQueryActionsService`** resolves a query's custom actions without rendering the grid, so a
  page-level action can live elsewhere. Paired with **`*sparkShellTopbarActions`**, which renders
  *beside* the language selector rather than replacing it (which is what `*sparkShellTopbarEnd`
  does).
- The reference picker and the AsDetail option lookup now fall back from `breadcrumb` straight to
  the id; the `name` they used to try went with the persistent-object row shape.

## Server API, smaller changes

- **`SparkDenial` is public.** An application writing endpoints alongside Spark's has to refuse the
  same way, or it reopens the oracle Spark closed — one 403 next to Spark's 404 tells a prober which
  ids exist.
- **`[SparkAuthorize(Group = …)]` naming a well-known group now throws.** `anonymous` and
  `authenticated` are decided from authentication state and excluded from claim-derived membership,
  so the requirement could never be satisfied: the endpoint denied every caller forever, with a 403
  indistinguishable from an ordinary refusal.
- **SPARK010's message was overstated.** `[SparkAuthorize]` is an endpoint filter carried by the
  action and runs whichever call mounted the route, so a bare-`MapControllers()` controller *is*
  authorized. What is actually lost is antiforgery path scoping and pipeline ordering.
- `source` and `entityType` are now structural in the model hash, so a hand-authored query cannot be
  changed without `--spark-verify-model` noticing. Entity-type alias collisions throw, symmetrically
  with query aliases.

---

## A custom action receives the rows the grid had

`CustomActionArgs.SelectedItems` is now `QueryResultItem[]` — an id, a display string and a value per
query column — instead of `PersistentObject[]`.

They are **re-materialized by re-running the query the selection came from**, narrowed to the posted
ids. Not echoed from the browser, so the values are server truth; and not re-derived from documents,
so they stay faithful to what the user was looking at. Three things made the document load wrong:

- A column computed inside an index and stored there is on no document, so it came back **null** —
  silently, because the mapper skips a property it cannot find.
- Loading by id can only use the type's **default** index, so a query naming its own silently
  produced a different row shape than the grid rendered.
- A **composed** query has no documents at all. Its selection previously fell into a per-id loop over
  the page-compose hook and handed the action *N copies of the page object wearing row ids*.

The client now posts the query id alongside the selection. It is narrowing-only: the `Query` right is
enforced on it independently, and its entity type must match the action's type or the request is
refused. Every id still resolves or the whole request is refused — an action never receives a subset.

**Two new hooks on `{Type}Actions`:**

```csharp
// Rows -> entities. One batched load, never one per row.
public virtual Task<IDictionary<string, T>> MaterializeAsync(IReadOnlyList<string> ids)

// Only when a row's Id is not a document id — a composed query minting "collections/people",
// or a composite "{a}|{b}" key. Without it such a query throws, naming this hook.
public object RestrictToIds(object source, IReadOnlyCollection<string> ids)
```

**Three shapes still materialize by id** and therefore lose index-computed values: a query owning its
own paging (`SparkQueryPage<T>`), a streaming query, and a request naming no query.

## Migration checklist

| If you… | Do this |
|---|---|
| read `result.data` / `result.totalRecords` | read `result.items` / `result.totalItems` |
| wrote a **column** renderer | rename its `attribute` input to `column`, type `SparkCellColumn` |
| wrote a detail or edit renderer | nothing — `attribute` is unchanged |
| bind `[data]` on `spark-query-grid` | also bind `[columns]` |
| read `args.SubmittedSelectedItems` | use `args.SelectedItems` (resolved, row-checked) or `args.SubmittedSelectedItemIds` |
| post `selectedItems` to `/spark/actions/…` | post `selectedItemIds` |
| implement `IQueryExecutor` or `IRowSecurity` | add the `CancellationToken` parameters |
| have a custom query returning `IEnumerable<object>` | return a concrete row type |
| have a query whose rows share an id, or have none | fix the row identity — it now throws |
| wrote `args.Parent ?? args.SelectedItems.FirstOrDefault()` | coalesce on ids: `args.Parent?.Id ?? args.SelectedItems.FirstOrDefault()?.Id` |
| read attributes off `args.SelectedItems` | read the column value, or materialize the entity via `MaterializeAsync` |
| have a query whose rows carry synthetic or composite ids | declare `RestrictToIds` on its actions class |
| have a renderer reading a sibling value off a row | mark that attribute `"showedOn": "Query", "isVisible": false` |
| hand-wrote a helper branching on the shape of `item` | delete it — `valueFor(item, key)` reads all three shapes |
| have a renderer whose `rendererOptions` *names* a sibling attribute | mark that attribute too — see the renderer guide |
