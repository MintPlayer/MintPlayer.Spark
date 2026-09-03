# PRD — Sortable means sortable: honest sort affordances, and a coverage percentage you can order by

**Status:** investigated, not implemented
**Issue:** not yet filed
**Plan:** `docs/sort_column_honesty_plan.md`
**Reported against:** `https://coverage.mintplayer.com/po/account/Accounts%2F48772716`
**Investigated:** 2026-09-03, against `master` @ `c1c12b9b`

---

## Problem

On the Coverage app's Account detail page, the Repositories grid shows a **Coverage** column whose
header is a clickable sort button. Clicking it does nothing: no reorder, no error, no console
message the user could see, no visible difference between "sorted ascending" and "sorted
descending". The grid re-fetches on every click and comes back in the same order.

The reported symptom is one instance of a general defect. The framework has **no honest notion of
which columns can be ordered by**:

- the client draws a sort affordance on *every* column, unconditionally;
- the server accepts a sort request for a column it cannot order by, and composes an `ORDER BY`
  that RavenDB silently ignores;
- the one place that actually knows the answer is a **source-generator diagnostic at build time**,
  and that knowledge never reaches either runtime or the client.

So the failure is not "one column is broken in one app". It is that a sort affordance in Spark
carries no information, and there is currently no way for an app to make a complex or computed
column sortable at all.

## Prior art

Ordering-as-a-security-boundary is already settled in this repository and must not be relitigated:
`docs/guide-queries-and-sorting.md:420-446` establishes that a caller-supplied `sortColumns` entry
must name a model attribute whose `showedOn` includes `Query`, because ordering is a comparison
oracle over values the caller may not read. That gate stays exactly as it is. This PRD **adds** a
second, orthogonal gate — *can this field be ordered by at all* — and does not weaken the first.

`SPARK_INDEX_010` already tells an author the truth at build time
(`GenerateIndexDiagnostics.cs:115-121`):

> "Property '{0}' has complex type '{1}'; it is **stored for projection but not indexed, so it
> cannot be filtered or sorted**. Add `[Breadcrumb]` to a property of '{1}' to make the column
> sortable, or `[IgnoreForIndex]` to drop it from the index."

The gap this PRD closes is that nothing carries that classification forward.

## Investigation findings

Three parallel investigations (app model, server pipeline, client wiring) produced the following.
Every claim below is cited; nothing here is inferred.

### F1 — The "coverage percentage" column is not a percentage, and no percentage is stored anywhere

`apps/CodeCoverage/CodeCoverage/App_Data/Model/Repository.json:162-181`:

```json
{
  "name": "LatestCoverage",
  "label": { "en": "Coverage" },
  "dataType": "AsDetail",
  "asDetailType": "CodeCoverage.Entities.CoverageSummary",
  "isArray": false,
  "showedOn": "Query, PersistentObject",
  "renderer": "coverage-bar"
}
```

The column is a nested object, not a number. `CoverageSummary`
(`apps/CodeCoverage/CodeCoverage.Library/Entities/CoverageSummary.cs:7-19`) holds five `int`
counters — `LinesCovered`, `LinesCoverable`, `BranchesCovered`, `BranchesTotal`, `FilesCount` — and
**no percentage property**, deliberately (`CoverageSummary.cs:3-6`):

> "Percentages are derived by consumers from the covered/coverable pairs — never stored, so they
> can't drift."

The percentage the user sees is computed in the browser:
`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/spark/coverage-summary.ts:9-28`, rendered by the
`coverage-bar` renderer (`coverage-bar-renderer.component.ts:50-55`).

Note also that the string "Coverage percentage" does not exist anywhere in the repository. The
header reads "Coverage".

### F2 — The server composes an `ORDER BY` on a field the index declared unindexed, and Raven silently no-ops

This is the actual root cause, and it is the one drop path that produces no warning at all.

`Repository` carries `[GenerateIndex]` (`Repository.cs:9`), so `Repositories_Overview` is
source-generated. `LatestCoverage` is a complex type, so the generator maps it **stored but not
indexed** — `GenerateIndexGenerator.cs:380`:

```csharp
FieldIndexing = isComplex ? "No" : isSearchableText ? "Search" : isDateTimeOffset ? "Exact" : null
```

with the reason recorded at `:290-293` (indexing it "faults Corax per document … the whole index
silently ends up empty").

The full chain for one header click:

| Step | Location | Outcome |
|---|---|---|
| Client sends `sortColumns=LatestCoverage:asc` | `spark.service.ts:60-62` | issued |
| Endpoint allow-list | `Execute.cs:77-102` | **passes** — it is a model attribute |
| `IsSortableAttribute` | `QueryExecutor.cs:1412-1418` | **passes** — `showedOn` includes `Query` |
| `ResolveSortProperty` | `QueryExecutor.cs:1420-1427` | no `LatestCoverageSort` companion → returns `LatestCoverage` |
| `GetCachedProperty` | `QueryExecutor.cs:1236-1244` | **finds it** (type `CoverageSummary`) → no `continue` |
| `OrderBy(x => x.LatestCoverage)` | `QueryExecutor.cs:1247-1261` | composed onto the Raven query |
| RavenDB | — | `order by` a `FieldIndexing.No` field → **no ordering, no error** |

Neither of `QueryExecutor`'s two `continue` guards fires, because both are satisfied. There is no
`try`/`catch` around sorting. The response is a 200 with unchanged row order.

### F3 — The execution gate asks about visibility, never about orderability

`QueryExecutor.cs:1412-1418` is the whole of the server's sortability test:

```csharp
private static bool IsSortableAttribute(EntityTypeDefinition definition, string requested) {
    var attribute = definition.Attributes.FirstOrDefault(a => string.Equals(a.Name, requested, StringComparison.OrdinalIgnoreCase));
    return attribute is not null && attribute.ShowedOn.HasFlag(EShowedOn.Query); }
```

Sortability is defined as *"is on the query surface"*. That is the correct **security** gate and a
non-answer to the **capability** question. Nothing anywhere asks whether the underlying field is
orderable.

### F4 — A complex column gets a sort companion only via `[Breadcrumb]`, and this one has none

`ResolveSortProperty` (`QueryExecutor.cs:1420-1427`) redirects to `{Name}Sort` when the projection
has one *and* it is `[IgnoreProperty]`. The generator emits a companion for `isSearchableText ||
isDateTimeOffset` (`GenerateIndexGenerator.cs:389-390`), and for a complex property only when a
`[Breadcrumb]` path resolves inside the complex type (`:296-333`, `ResolveBreadcrumbPath`);
otherwise `BreadcrumbResolution.None()` and no companion.

`CoverageSummary` has no `[Breadcrumb]` on any property (grep for `Sortable|Breadcrumb` across
`CodeCoverage.Library\Entities\*.cs`: zero hits). So no `LatestCoverageSort` exists.

### F5 — `[Breadcrumb]`, the route `SPARK_INDEX_010` recommends, would produce a *wrong* order here

The diagnostic's advice is sound in general and wrong for this column. A `[Breadcrumb]` on a
`CoverageSummary` property means ordering by that single counter, and no counter is the percentage:
a 90/100 repository (90%) would sort *below* a 200/1000 one (20%). Following the diagnostic would
replace a dead affordance with a live and silently incorrect one — strictly worse, because the
current failure is at least visible as a failure.

### F6 — The client hardcodes the sort affordance and reads no flag

`libs/node_packages/ng-spark/grid/src/spark-query-grid.component.html:43`:

```html
<div *bsDatatableColumn="col.name; sortable: true">
```

Identically in `spark-reference-picker.component.html:66`. The only column-level gate applied
anywhere on the client is visibility — `spark-query-grid.component.ts:228`:
`allColumns().filter(c => c.isVisible !== false)`.

So "advertised sortable" is, in practice, "every visible column".

### F7 — `QueryColumn.isSortable` exists on the wire, is read by nothing, and means something else

`Abstractions/QueryResult.cs:63` declares it; `Services/QueryResultProjector.cs:56` fills it
(`IsSortable = a.IsSortable ?? false`); the TS mirror is `models/src/query-result.ts:48`. A grep of
`libs/node_packages` for `isSortable` finds only its two declarations plus
`spark-po-form.component.html`, where it gates **drag-to-reorder handles on an AsDetail array** — an
unrelated concept.

The name is already taken, and by the other meaning. `Abstractions/SortableAttribute.cs`:

> "Marks an `AsDetail` array property as drag-to-reorderable in the PO-edit UI"

And `ModelSynchronizer.cs:720` can only ever write it for that case:

```csharp
bool? isSortable = sortableAttr != null && dataType == "AsDetail" && isArray ? true : null;
```

A *non-array* `AsDetail` — exactly `LatestCoverage` — can therefore never receive the flag at all.
Consequence: every ordinary column ships `isSortable: false` while being fully sortable at
execution, and the flag cannot be repurposed without breaking drag-reorder. A new, differently
named field is required.

### F8 — The endpoint's 400-gate and the executor's gate disagree

`Execute.cs:77-102` builds `allowedProperties` from **all** `entityType.Attributes` with no
`ShowedOn` filter, plus the query's declared sort columns. So an attribute with
`showedOn: "PersistentObject"` passes the endpoint's 400 check and is then silently dropped by
`IsSortableAttribute` downstream. A 400 is returned only for a name absent from the model entirely.

Two gates, two answers, one of them silent. This is a latent inconsistency independent of the
reported bug, and it belongs in the same fix.

### F9 — Refusals go to `Console.WriteLine`, not to a logger

`QueryExecutor.cs:1227-1233` and `:854-860` both warn via `Console.WriteLine` and `continue`. There
is no `ILogger` on this path, so in a container these lines land in stdout unstructured and
unfilterable, and there is no way to alert on "sorts are being refused in production".

### F10 — The web component's in-memory sort is a no-op for every Spark grid in `[data]` mode

`bs-datatable` sets `autoSort = !fetching`
(`mintplayer-ng-bootstrap-datatable.mjs:311-319`), and the WC's in-memory key extractor is flat
property access (`mp-datatable-UJI8E73X.mjs:321-324`):

```js
function w(l,t){ if(!(l==null||typeof l!="object")) return l[t]; }
```

Spark rows are `QueryResultItem` — `{ id, values: [{ key, value }] }`
(`models/src/query-result.ts:65-73`) — so `row['LatestCoverage']` is `undefined` for every row and
the sort is a stable no-op.

This affects `spark-reference-picker` (binds `[data]`, no `[fetch]`,
`spark-reference-picker.component.html:63`). It does **not** affect the reported grid, which is in
`[fetch]` mode. The only working client-side sort in the codebase is
`spark-query-list.component.ts:274-300`, which reads the `values` array correctly and runs solely
for `isStreamingQuery` (`:118-119`). A second dead sort affordance, same shape, different cause.

### F11 — The detail path is not the culprit; routing is identical to a top-level query

Worth recording because it was the initial hypothesis and it is wrong.

There is no separate server endpoint for a PO sub-query: `GET /spark/queries/{id}/execute` is the
only surface (`Execute.cs:11`), with parent scoping riding the same request as `?parentId=` +
`?parentType=` (`Execute.cs:118-133`). On the client, both hosts render the same
`spark-query-grid`: the routed page via `spark-query-list.component.html:85-92`, the detail via
`spark-po-detail.component.html:196-201` → `spark-query-card` → grid. Neither host binds
`settings`, `data()` is left `null` in the detail card, so `hasExternalData()` is false and
`fetchFn` is set (`spark-query-grid.component.ts:409-411`) — the detail grid is in `[fetch]` mode
and **does** re-fetch server-side on each header click.

The WC confirms the click is live (`mp-datatable-UJI8E73X.mjs:1231-1248`): `onHeaderClick` toggles
`_sortColumns`, resets `_page`, calls `scheduleFetchReload()`, and dispatches
`mp-datatable-sort-change`. Its dedupe key is `JSON.stringify({s: _sortColumns, pp, p})`
(`:1183-1193`), so a changed sort passes the dedupe. The request is issued and accepted; the
ordering is what evaporates.

Commit `c1c12b9b` (the ng-bootstrap light-DOM datatable adoption) is also exonerated: `git show
--stat` touches no sort binding and not `spark-query-grid.component.html` at all.

One genuine light-DOM consequence, already handled: the `[i]` description glyph renders a nested
`<button>` inside the WC's `header-sort` button, and its click is deliberately swallowed
(`spark-attribute-description.component.ts:70-73`). So clicking precisely on the `[i]` never sorts —
by design, documented at `:17-18`. This is a plausible secondary source of "clicking does nothing"
reports and is *not* a defect.

### F12 — There is no way for an app to declare a computed sortable column

Sorting is only ever possible over an indexed field of the entity or projection type. A value that
is deliberately not persisted — a ratio, a percentage, a derived score — has no expression in the
model, the generated index, or the sort pipeline. `[Breadcrumb]` reaches an existing nested
*property*; it cannot compute. This is the missing capability behind F1/F5, and it is the reason the
Coverage app cannot fix its own column without framework support.

### F13 — Nothing is tested here

- `SortColumnDisclosureTests.cs:124-172` covers the four `showedOn` surface-gate cases.
- `SortInjectionTests.cs:22-56` covers unknown property, metadata property, malformed direction.
- `spark.service.spec.ts:52-66` asserts the wire format `'Name:asc,Age:desc'`.
- `spark-query-grid.component.spec.ts` has `sortColumns: []` in a fixture and no header-click test.

**No test covers sorting a complex or `AsDetail` column, and none asserts that a sort request
against a `FieldIndexing.No` field is reported rather than silently ignored.** The defect was
reachable precisely because the gap in coverage matches the gap in the gate.

### F14 — `isSortable: true` appears in app JSON that nothing reads

`apps/DemoApp/DemoApp/App_Data/Model/StartPage.json:77,91` and
`apps/HR/HR/App_Data/Model/Person.json:236` carry `isSortable: true`. No server code reads the
field for column sorting, and no client code reads it at all (F7). These are inert and will need
reconciling with whatever the new field means, or they become a trap for the next reader.

## Options

### Where sortability truth comes from

| # | Option | Assessment |
|---|---|---|
| O1 | Runtime reflection over the live index definition | Most accurate, worst coupling: needs a Raven round-trip per query resolution, and hand-written indexes give no reliable classification. Rejected. |
| O2 | **Model JSON field written at sync time** | The synchronizer already runs offline with the entity assemblies loaded and already reflects per property. Same classification the generator computes. **Recommended.** |
| O3 | Client-side inference from `dataType` | Zero cost, and catches `AsDetail` — but silently wrong for a hand-written index that *does* index a complex field, and says nothing about analyzed text. Useful only as a fallback. |
| O4 | Leave the affordance, surface an error toast on refusal | Turns a silent failure into a loud one without making anything sortable. Strictly worse UX than not offering the affordance. Rejected as the primary fix; the *reporting* half is kept (D3). |

O2 with O3 as a defensive default for un-classifiable cases.

### How a computed value becomes sortable

| # | Option | Assessment |
|---|---|---|
| O5 | Persist `LatestCoveragePercent` on the document | Directly contradicts `CoverageSummary.cs:3-6`; introduces the drift the app's design explicitly avoids. Rejected. |
| O6 | `[Breadcrumb]` on a `CoverageSummary` counter | Wrong ordering (F5). Rejected. |
| O7 | **Computed field in the generated index** — `LinesCoverable == 0 ? -1 : LinesCovered * 100.0 / LinesCoverable`, indexed and stored, never persisted | Preserves "never stored, so it can't drift" — the value lives only in the index. Needs a generator affordance. **Recommended.** |
| O8 | Hand-written index + `indexName` on the query (#279 binding) | Works today with no framework change, but abandons `[GenerateIndex]` for `Repository`, which nine call sites depend on. Viable fallback if S3 says O7 is expensive. |
| O9 | Projection type `VRepository` with a computed property | The documented pattern for computed columns (`guide-queries-and-sorting.md:153-215`), but the query returns `IRavenQueryable<Repository>` and `ApplyProjection` only runs for `IsSparkProjection()`; converting it changes the row shape for the renderer. Fallback. |

O7 primary, O8 fallback; the choice is S3's to make.

### Naming

`isSortable` is unavailable (F7). Candidates: `isQuerySortable`, `canOrderBy`, `isOrderable`.
**`isOrderable`** is recommended — it borrows the SQL/Raven verb, does not read as a near-synonym of
the existing flag, and keeps "sortable" reserved for drag-reorder.

## Design

### D1 — `isOrderable : bool?` on `EntityAttributeDefinition`, written by the synchronizer

A third state matters, so the field is nullable:

- `true` — the field is indexed and can be ordered by;
- `false` — classified as not orderable (complex with no companion, analyzed text with no companion);
- `null` — unknown (hand-written index, no classification available) → treated as `true`, preserving
  today's behaviour rather than silently removing a working sort.

Written at sync from the same complex/searchable classification the index generator uses. S4 decides
whether that classifier is shared or currently duplicated; if duplicated, it is extracted so there is
one answer.

This is a presentational/capability field, not a structural one: like `description` (#348) it must
stay outside `StructuralAttributeFields` so it does not churn `modelHashes.json`.

### D2 — Sortability becomes two independent gates, both explicit

```
may this caller order by it?   → showedOn contains Query   (security — unchanged, F3)
can anything order by it?      → isOrderable != false      (capability — new)
```

Both must pass. The security gate keeps running first and keeps its existing message, so no
disclosure behaviour changes. Ordering by a complex field with a resolved `{Name}Sort` companion
keeps working: the companion is what makes it orderable, so the classifier reports `true`.

### D3 — A refused sort is reported, through `ILogger`, and told to the caller

Two changes on the server path:

1. Replace `Console.WriteLine` at `QueryExecutor.cs:1227-1233` and `:854-860` with `ILogger`
   warnings (F9), and add the new capability refusal alongside them.
2. Return the refused columns to the client in the query result, so a grid can render *why* nothing
   happened rather than appearing broken. A refusal stays a 200 with unchanged order — never a 400,
   which would break every existing caller that sorts by a legitimately-refused column.

The endpoint's 400-gate is aligned with the executor's (F8): one allow-list, one answer.

### D4 — The client binds the affordance to the column

`spark-query-grid.component.html:43` becomes `sortable: col.isOrderable !== false`, and
`QueryColumn` carries `isOrderable` (`QueryResultProjector.cs`, the TS mirror, and
`spark-reference-picker.component.html:66`). The hardcoded `true` is deleted in both templates.

`QueryColumn.isSortable` is left alone — it is load-bearing for drag-reorder (F7) — but gains a
docblock saying which of the two things it means and pointing at the other.

### D5 — A computed, indexed, unpersisted sort field

The enabling capability behind F12. The generator gains a way to declare a field that exists only in
the index: an expression over the entity, mapped and indexed, never written to the document. The
Coverage app then declares the coverage percentage that way, and `isOrderable` reports `true` for it.

Exact surface is S3's to fix — the constraint is that it must not require abandoning
`[GenerateIndex]` on `Repository` (nine call sites, F-agent §4) and must not persist anything.

### D6 — The Coverage column orders by percentage, with defined placement for the undefined cases

A repository with `LinesCoverable == 0` has no percentage, and `LatestCoverage` may be `null`
entirely (a repository with no finalized build). Both must have a defined, documented position
rather than whatever the index happens to do. Sentinel `-1` for "no coverable lines" and Raven's
`NULL_VALUE` ordering for absent summaries are the starting proposal; S5 confirms against what the
UI should show.

### D7 — The reference picker's dead sort is closed too

`spark-reference-picker` inherits a no-op in-memory sort (F10). It is in `[data]` mode, so the honest
answer is `sortable: false` on its headers unless S2 shows the WC can be given a key extractor that
understands `QueryResultItem`. Either way, the dead affordance goes.

## Acceptance criteria

1. Clicking the **Coverage** header on `/po/account/{id}` reorders the Repositories grid by coverage
   percentage, ascending then descending, and the order is correct for a repository with a high
   ratio and a low absolute count.
2. Repositories with no coverable lines and repositories with no coverage summary appear in a
   defined, documented position in both directions.
3. No column anywhere in the five apps offers a sort affordance the server will refuse.
4. A column that is genuinely orderable still offers one — no working sort is lost. Enumerated by S6.
5. A caller-supplied sort against a non-orderable field is logged through `ILogger` at warning level
   and reported in the query result; the response is still a 200 with unchanged order.
6. The `showedOn` security gate is unchanged: `SortColumnDisclosureTests` and `SortInjectionTests`
   pass untouched.
7. `--spark-synchronize-model` is a fixed point: two consecutive runs leave `git diff` empty.
8. `isOrderable` does not appear in `StructuralAttributeFields`; `modelHashes.json` does not churn.
9. Drag-to-reorder on `[Sortable]` AsDetail arrays still works — `isSortable` semantics untouched.
10. A test sorts a complex/`AsDetail` column and asserts the refusal is reported, not silent.
11. The reference picker offers no sort affordance it cannot honour.

## Breaking changes

- **Sort affordances disappear from columns that never worked.** Visible UI change in every app.
  Intended, and the point of the PRD, but it will read as "sorting was removed" unless the release
  notes say otherwise. S6 must enumerate the affected columns per app before this ships.
- **`isOrderable` is a new model-JSON field.** Additive; absent means `null` means "assume
  orderable", so an un-synchronized app behaves exactly as today.
- **The endpoint 400-gate narrows** to match the executor (F8). A caller sorting by a
  `showedOn: "PersistentObject"` attribute currently gets a silent 200; it will get a reported
  refusal. Same order either way.
- Whatever D5 adds to the generator is additive; existing `[GenerateIndex]` output is unchanged for
  entities that do not use it.

Per repository policy this is one PR, spanning framework, generator, and the Coverage app.

## Out of scope (genuinely not being done)

- **Making every complex column sortable.** `FieldIndexing.No` on complex fields exists because
  indexing them faults Corax (`GenerateIndexGenerator.cs:290-293`). This PRD makes the limitation
  honest and gives one escape hatch; it does not lift it.
- **Per-query column sets.** Issue #284 ("Grid columns are per-entity, not per-query") overlaps: a
  per-query `isOrderable` would be more precise than a per-entity one. Deliberately not attempted —
  the per-entity field is correct as far as it goes and #284 can refine it later without a rewrite.
- **#319** (custom action on a PO detail page fires `/execute` twice). Adjacent, same components,
  independent cause. Not folded in.
- **Sorting streaming queries.** `StreamExecuteQuery.cs` has no `sortColumns` handling at all. That
  is a separate, deliberate omission; `spark-query-list` sorts those rows client-side and correctly.
- **Multi-column sort UX.** The WC already supports shift-click; nothing here changes it.

## Spikes

Seven, detailed in `docs/sort_column_honesty_plan.md` §M0. In brief:

- **S1** — Confirm RavenDB silently ignores `order by` on a `FieldIndexing.No` field rather than
  erroring or partially ordering. *The entire diagnosis rests on this.*
- **S2** — Confirm `sortable: false` makes the WC header non-clickable and dispatch-free.
- **S3** — Find the generator's affordance for a computed indexed field; decide O7 vs O8.
- **S4** — Establish whether the synchronizer can reach the index classification, or whether it must
  be extracted from the generator.
- **S5** — Decide percentage-sort semantics for zero-coverable and null-summary repositories.
- **S6** — Enumerate, per app, every column that would lose its affordance and every sort that
  currently works, before the gate changes.
- **S7** — Confirm a `FieldIndexing.No` sort is not a partial disclosure oracle.
