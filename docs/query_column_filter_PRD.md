# PRD — Per-column sort and filter capabilities on query grids (#431)

Issue: [#431 — Per-column filter on query page](https://github.com/MintPlayer/MintPlayer.Spark/issues/431).

## 1. Verification summary — what the request got right, and what it got wrong

**Issue #431 has no body.** The requirement comes from the requesting conversation: *"On queries and
subqueries, Vidyano displays a per-column filter under the grid headers, containing all values
(breadcrumb texts) for that column … I want to support the same in the Spark framework directly. The
Spark queries in the json files should contain a `CanSort`, `CanFilter` property that determines if
the sort arrows/functionality and filter row is shown in the respective query grid."* The issue body
should be filled from this PRD.

Four investigations ran against the real system before this was written: a live survey of the Vidyano
Fleet app (`https://localhost:5001`, client `4.0.0-pre.65`) including its wire protocol, a map of the
Spark server model, a map of the `ng-spark` client, and a RavenDB feasibility spike.

**Right:**

- Vidyano really does render a per-column filter row under the headers, on top-level queries **and**
  on sub-queries inside a detail page, with the same component and the same protocol.
- The values really are display/breadcrumb text, not raw values.
- `canSort` and `canFilter` really are the names Vidyano uses — they ship per column on the query
  definition, positive-form, alongside `canListDistincts` and `canGroupBy`.

**Wrong, or incomplete:**

1. **There is no per-column node in Spark's model JSON to add these to.** A query has no column list
   at all. Columns are derived at request time from `persistentObject.attributes[]` filtered by
   `showedOn` containing `Query` (`libs/spark/MintPlayer.Spark/Services/QueryResultProjector.cs:28-63`).
   Grep confirms no model JSON anywhere contains a `columns` node. The flags therefore cannot simply
   be "added to the queries in the json files" as stated — see §5.2.

2. **`isSortable` already exists and its meaning is already taken.**
   `EntityAttributeDefinition.IsSortable` is the **AsDetail drag-reorder** flag, derived by the
   synchronizer as `[Sortable]` + `dataType == "AsDetail"` + `isArray`
   (`ModelSynchronizer.cs:755,828`). It is **not** available to repurpose as `CanSort`.

   Worse, the repo already disagrees with itself about it. Of the three occurrences across 110 model
   files:
   - `apps/HR/HR/App_Data/Model/Person.json:250` — AsDetail array, `editMode: inline`. Drag-reorder.
     Correct.
   - `apps/DemoApp/DemoApp/App_Data/Model/StartPage.json:77,91` — `"dataType": "string"`,
     `"isArray": false`, `"showedOn": "Query"`. A plain scalar query column, hand-authored as though
     `isSortable` meant "this column can be sorted".

   `QueryColumn.IsSortable` on the wire is fed this flag (`QueryResultProjector.cs:56`), so it is
   `false` for every ordinary scalar column, and the client ignores it entirely — the grid hard-codes
   `sortable: true` for every column (`spark-query-grid.component.html:42`). **Untangle, do not layer.**

3. **Vidyano has no per-query override.** `Company.json:381-383` puts `CanSort`/`CanFilter`/
   `CanListDistincts` on the *attribute*; `Queries[].Columns[]` is only an ordered selection of
   attributes by `$ref` (`{ "Id": …, "Attribute": { "$ref": … } }`), carrying no flags, with
   `"Columns": []` meaning "all". The per-query override in §5.2 is ours, not inherited.

4. **The protocol groundwork is already done, deliberately, for this feature.** `?sortColumns=` as a
   query-string parameter no longer exists; reads are `POST /spark/queries/execute` with a typed body.
   Both `spark.service.ts:116-138` and `QueryRequests.cs:242-254` state the reason in as many words:
   *"column filtering — multiple columns, multiple selected values per column — cannot be expressed as
   a flat string without inventing an encoding, and inventing one is what `sortColumns` already did."*
   `SparkClient.cs:387-405` carries the same note.

**Backward compatibility is explicitly not required** (packages are in preview). This permits the
`IsSortable` untangling, wire renames, and a re-synchronize of all four apps in the same PR. It does
**not** relax the version rule: npm major = Angular major, NuGet major = .NET major, so these are
**minor** bumps only.

## 2. Problem

A Spark query grid today offers exactly two ways to narrow what it shows: a single free-text `search`
box that matches every readable string property of the row type, and nothing else. Every column is
sortable whether or not that makes sense, because `sortable: true` is a hard-coded literal in the
grid template. There is no way for a model author to say "this column is noise, do not offer to sort
it" or "let the user pick from the values that are actually present here".

The result is that any non-trivial grid — a fleet of 950 cars, 200k file-coverage rows — is navigated
by typing guesses into a free-text box.

## 3. What Vidyano actually does — the measured contract

All of this is measured from the running app, not read from documentation.

**Placement.** A second header row under the column headers. Each `vi-query-grid-column-header`
renders a `vi-query-grid-column-filter` inside its own shadow root, so cells align by construction.
A single global "clear all filters" cell sits in the left gutter and highlights when any filter is
active.

**Collapsed cell.** A pale funnel glyph when empty. When filtered, the funnel is replaced by the
operator and the comma-joined **display texts** — `= Nee`, `= Apcoa, Cityparking`, or when inversed
`≠ Apcoa, Cityparking`. No badge, no count.

**Popup.** A "Clear filter for &lt;Column&gt;" menu item (disabled when empty), an auto-focused search
box, an inverse (`≠`) toggle in a left gutter, and a virtualised checkbox list. **No select-all/none,
no per-value counts, no OK/Apply** — checking a box applies immediately.

**Two buckets.** `MatchingDistincts` are the values satisfying the current context (other columns'
filters + the popup's search term); `RemainingDistincts` are the column's other values, rendered
greyed below and still selectable.

**Capped at 100 per bucket**, measured. There is no paging and no scroll-to-load; the search box
round-trips to the server instead. The cap is not a stable alphabetical prefix — on a 491-row query
the first alphabetical value was absent from `MatchingDistincts` but present in `RemainingDistincts`
under search. There is no ordering contract to copy.

**Null.** A real distinct rendered `< Niets >`; a literal JSON `null` element on the wire.

**Semantics.** Multiple column filters AND together. The inverse toggle is not a flag — it moves the
selected strings from `includes` to `excludes`. Filters are **transient**: they survive neither
reload nor SPA navigation. Persistence is a separate named-preset feature (`QueryFilters`).

**Types.** Every column type gets the same control — a distinct-value checkbox list. Verified across
`String`, `MultiLineString`, `KeyValueList`, `Boolean`, `Date`, `NullableDate`, `DateTimeOffset`,
`NullableDecimal`, `NullableInt32` and app enums. **No range picker, no calendar, no min/max box
anywhere.** Booleans list only the values actually present in the data — the list is data-driven,
never a type domain.

**Per-column flags on the definition**, the exact set:

```json
{"name":"Name","type":"String","id":"6e18b89a-…","offset":10,
 "isHidden":false,"typeHints":{},"label":"Naam",
 "canSort":true,"canFilter":true,"canListDistincts":true,"canGroupBy":false}
```

**The distinct encoding — and why we are not copying it.** Vidyano encodes each option as
`"<rawLen>|<raw><display>"`: `"9|AlfaRomeoAlfa Romeo"`, `"7|CitroenCitroën"`, `"|Audi"` (empty prefix
= raw equals display), `"5|FalseNee"`, `null` for the null distinct. This is a length-prefixed string
packed into JSON. Our reads are already a typed POST body precisely so we do not invent encodings;
we send an object (§5.6).

**Sub-queries** are identical except that `QueryFilter.RefreshColumn` carries an extra top-level
`parent` persistent object beside `query`. The distinct computation needs the owning object's
identity.

## 4. The honest ceiling — what RavenDB will and will not give us

Three findings bound the design. All are from the feasibility spike.

**4a. Facets leak, on the default query shape.** `RowSecurity.ComposeRowFilterAsync` refuses to push
the row filter down when the element type is a projection (`RowSecurity.cs:305-318`), and a projection
is the default for every `[GenerateIndex]` query (`QueryExecutor.cs:669-696`). The filter is applied
*after* materialization by `IRowSecurityGate.ApplyAsync`. An `AggregateBy` attached to that
`IQueryable` therefore aggregates over the **unfiltered** set and would publish values from rows the
caller cannot see — a strictly worse disclosure oracle than the `?sortColumns=` one closed by
#294-#296, because it returns the values rather than leaking their order.

`docs/actions_and_coverage_plan.md:444-447` predicted exactly this: *"Facets, distinct values and
search suggestions deserve treating as first-class row-security surfaces covered by the same
expression. Not done here."*

**4b. Breadcrumb text is not an index term.** `QueryExecutor.cs:133-136` says it outright. Reference
display text is computed after materialization by `BreadcrumbResolver`, which loads the referenced
documents and applies row security to them, substituting `RedactedPlaceholder` for denied targets
(`BreadcrumbResolver.cs:150-153`). There is nothing to facet. And it is not only references: a plain
default-indexed string stores the whole value as **one lower-cased term**, so a facet-derived list
would read `volkswagen golf gti`; a `[Search]` field is tokenized into three separate terms.

**4c. Paging is not pushed down anyway.** `QueryExecutor.cs:741` materializes the whole secured result
set and pages it in memory at `:157-162`; `TotalItems` is `allResults.Count`. Every grid already pays
O(result set).

**The conclusion that follows.** Computing the distinct list **in memory from the already-materialized,
already-secured, already-breadcrumb-resolved rows** costs one extra pass and zero extra queries. It
honours `GetRowFilterAsync`, `IsAllowedAsync` **and** `RedactAsync` by construction, and it is the
only mechanism that can produce breadcrumb text at all, because `attribute.Breadcrumb` is already
sitting on the mapped row. Its ceiling is the ceiling the grid already has.

**Facets are the right answer only once server-side paging exists.** Until then they are both less
safe and less capable. This is recorded as a non-goal (§9), not an oversight.

## 5. Design

### 5.1 Three flags, not two

| Flag | Controls | Default when absent |
|---|---|---|
| `canSort` | The sort affordance, and whether the server accepts a caller-supplied sort on this column | capable (`true`) |
| `canFilter` | Whether a filter cell is drawn, and whether the server accepts `includes`/`excludes` for this column | capable (`true`) |
| `canListDistincts` | Whether the distinct-value list may be enumerated for this column | capable (`true`) |

The requester asked for two. The third is added deliberately, and it is the one the security analysis
needs: **enumerating a column's values is a far stronger disclosure than filtering by a value the
caller already knows.** A caller who knows an account number may legitimately filter by it while
having no business listing every account number in the system. Vidyano separates these for the same
reason. `canListDistincts: false` with `canFilter: true` yields a free-text filter cell rather than a
checkbox list.

**How the degraded mode is rendered — and why every column renders the same way.** `mp-datatable`'s
built-in `FilterMode` is `'values' | 'comparison'` only; there is no free-text mode, and
`FilterOperator` has no `contains`, so comparison mode cannot express a substring search. A
`canListDistincts: false` column therefore has no built-in panel that fits.

The answer is not to ask for a third mode. **Spark always supplies its own panel**, via
`*bsDatatableFilterPanel` nested inside `*bsDatatableColumn`, for every filterable column — value
list and free text alike. See §5.8.

`canGroupBy` exists in Vidyano and is `false` everywhere we sampled. It is out of scope (§9).

**Defaults are absent → capable**, matching Vidyano (whose flags appear only where `false` — three
occurrences in 1375 lines of `Company.json`) and matching the `isVisible !== false` convention already
documented at `spark-query-grid.component.ts:244-248`. This is safe because `showedOn: Query` remains
the real authorization boundary: a column that must never be sorted, filtered or enumerated is kept
off the query surface, not merely flagged.

All three are `bool?`, never `bool`. The synchronizer writes with
`DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` (`ModelSynchronizer.cs:32-38`), so
`bool?` + absent = no byte change in any existing model file, while a non-nullable `bool` would add
`"canSort": false` to every attribute of every file in all four apps — and would break the
synchronize fixed point, which is exactly the bug recorded at `ModelSynchronizer.cs:172-176`.

### 5.2 Where the flags live — per-attribute default, per-query override

Decided by the requester against two alternatives.

**Attribute level** (`persistentObject.attributes[]`) carries the default:

```json
{ "name": "Salary", "dataType": "number", "showedOn": "Query",
  "canSort": true, "canFilter": true, "canListDistincts": false }
```

**Query level** may override, per column, for that grid only. This introduces the `queries[].columns[]`
node that does not exist today — but only as a *sparse override list*, never as a column enumeration:

```json
{ "name": "PublicPeople", "source": "Database.People",
  "sortColumns": [ { "property": "LastName", "direction": "asc" } ],
  "columns": [ { "name": "Salary", "canFilter": false, "canListDistincts": false } ] }
```

**Sparse is load-bearing.** An omitted column inherits the attribute-level answer; an omitted
`columns` node means the query overrides nothing. This is what keeps the node from becoming a second
place that decides which columns exist and in what order — that remains `showedOn` + `order`, exactly
as today. A `columns` entry naming an attribute that is not on the query surface is a `--spark-verify-model`
error, not a silent no-op.

**Resolution order** is: query override → attribute value → `true`. Resolved once per request in
`QueryResultProjector.BuildColumns`, written onto the freshly-constructed `QueryColumn`.

**Never onto the definition.** `EntityAttributeDefinition` instances are handed out by reference from
a singleton `ModelLoader`, and `ShallowCopy()` does not help because `Attributes` is shared. Writing a
resolved per-request answer onto an attribute is the live bug already fixed once in
`Endpoints/EntityTypes/List.cs:140-150`, where per-caller `CanRead` on a shared reference let two
concurrent callers each serialize the other's answer. `QueryColumn` is constructed per request and is
the correct seam; it already exists.

### 5.3 Interaction with `queries[].sortColumns`

`sortColumns` is the query's **server-authored default order**, not a capability. The two combine by
one rule:

> A column named in the query's own `sortColumns` is exempt from the `canSort` gate. The server chose
> that ordering; the caller did not.

So `canSort: false` on a column that the query sorts by is **legal and useful** — the grid arrives
ordered by that column, and the user cannot reorder by it. `Execute.cs:71-110` already unions the
query's declared `SortColumns` into its allow-list, so the endpoint layer needs no change here; only
the executor's gate does.

`--spark-verify-model` does not flag this combination. It is a deliberate shape, not drift.

### 5.4 Enforcement, not decoration

Decided by the requester: `canSort: false` / `canFilter: false` / `canListDistincts: false` **mean
something to a non-browser caller**.

- `QueryExecutor.IsSortableAttribute` (`:1670-1676`) gains `&& resolved.CanSort != false`. It keeps
  refusing **silently** — console warning, rows keep index order — because a distinguishable refusal
  is itself an oracle. This is the existing behaviour and the reason for it is recorded at
  `QueryExecutor.cs:1468-1489`.
- A new `IsFilterableAttribute` gates `includes`/`excludes` the same way, same silence.
- The distinct-values endpoint refuses a column with `canListDistincts == false` by returning empty
  buckets, not an error.

**Consequently all three are structural, not presentational**, and go in
`ModelFileShape.StructuralAttributeFields` (`:168-173`) under the rule stated there: *"every field
that gates a write is structural."* `isVisible` is the cautionary precedent at `:42-47` — it reads as
presentation but gates a write, and hashing one and not the other left a mass-assignment hole.
`AppendInline` only appends a field when the JSON property is present, so adding these names does not
churn the hashes of files that omit them.

### 5.5 Distinct values — in memory over secured rows

New endpoint `POST /spark/queries/distinct-values`, literal route, consistent with the deliberately
fully-literal route table (`StreamExecuteQuery.cs:16-19`).

Request body: `queryId`, `column`, optional `search`, the **other columns' current filters** (so the
matching/remaining split is computed in context), and optional `parentId`/`parentType` for a
sub-query — the last is required, per the measured Vidyano protocol, because a detail grid's distincts
depend on the owning object.

Implementation: run the query through the **existing pipeline** up to and including the row-security
gate, then project distincts from the secured, mapped, breadcrumb-resolved rows. This is one extra
pass over data the request already materialized.

**Order of operations is fixed and load-bearing** — authorize (404 on denial, never 403, per
`SparkDenial.cs:23-61`) → resolve the column and check `canListDistincts` → run the secured pipeline →
distinct. Authorization first, because `Execute.cs:43-55` records that an unresolvable query used to
404 while an existing-but-denied one fell to 403, letting an unauthorized caller enumerate attribute
names by watching 400-vs-403.

**Response shape.** Two buckets and a truncation flag, matching what `mp-datatable` consumes:

```json
{ "matching":  [ { "value": "AlfaRomeo", "label": "Alfa Romeo" } ],
  "remaining": [ { "value": "Audi",      "label": "Audi" } ],
  "hasMore": true }
```

`matching` satisfies the current context (other columns' filters + the search term); `remaining` is
the column's other values, rendered dimmed but still selectable. **The server must compute and return
`remaining`** — the component's local fallback cannot, because our grid is `[fetch]`-bound, so without
it the greyed-but-selectable behaviour silently never appears.

**Caps.** Hard cap of 100 per bucket, matching Vidyano, with a `hasMore` flag. Narrowing is by the
`search` parameter, which round-trips. No paging. `hasMore` must be **honest at the cap**: the
component re-queries only when `hasMore` is set or the term is widened, so a dishonest `false` leaves
a user typing past a truncated list with stale results.

**No counts.** The response carries values, never per-value counts. `QueryExecutor.cs:97-119` records
that an author-supplied total combined with row security *"became a cardinality oracle for rows the
caller may not see … It cannot be repaired by counting."* The same reasoning applies here and is why
this PRD does not offer the count Vidyano also declines to show.

### 5.6 The wire shape for a distinct value

A distinct is an object, not a length-prefixed string:

```json
{ "value": "AlfaRomeo", "label": "Alfa Romeo" }
{ "value": "Audi",      "label": "Audi" }
{ "value": null,        "label": "< none >" }
```

`value` is the raw stored value and is **the only thing that travels back** in `includes`/`excludes`.
`label` is presentation only. Keeping them distinct is what lets two values relabel to the same string
without making the predicate ambiguous, and it is what makes §5.7 possible.

Applying a filter reuses these objects' `value` fields:

```json
{ "queryId": "…", "columns": [
    { "name": "Manufacturer", "includes": ["AlfaRomeo", "Citroen"], "excludes": [] },
    { "name": "IsActive",     "includes": [], "excludes": [false] } ] }
```

Columns AND together. Within a column, `includes` OR together. `excludes` is the inverse form and,
as in Vidyano, is not a separate flag — the client moves values between the two arrays.

### 5.7 Labels when the column has a custom cell renderer

A renderer is an Angular component registered by name (`renderer?: string` on `SparkCellColumn`),
handed a bag filtered to its declared inputs by `withDeclaredInputs`, and — since #245 — given row
context via `item`. It paints arbitrary DOM. There is no text function to call, and a distinct value
has no row, so a renderer that branches on `item` cannot be instantiated for one.

**The label is therefore the server-computed cell text** — the string the grid would render with no
renderer attached; for a reference column, the breadcrumb from `BreadcrumbResolver`. A status column
rendered as a coloured pill lists `Active` / `Suspended`; a rating rendered as stars lists `3`.

**Escape hatch:** an optional `filterLabel(value): string` on the *renderer registration* — a pure
function, no DOM, no row context, no component instantiation. Absent → server text.

Explicitly rejected: instantiating the renderer per distinct value (row-context breakage, N
instantiations per popup, and an icon-only renderer yields an unlabelled checkbox); deriving labels
from rendered grid cells (only covers the current page); and letting the label become the filter
identity.

**Accepted consequence:** the popup can visibly disagree with the cell. That is the cost of the only
design that stays correct, and it is documented rather than hidden.

### 5.8 How the filter row is consumed

The filter row itself is **not** Spark's to build. It shipped in
[mintplayer-ng-bootstrap#415](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/415)
(`22.19.0` / web-components `2.16.0`): a second `<tr>` in `<thead>`, a per-column trigger, and a panel
portalled to a document-root `<mp-overlay-container>`.

**Spark always nests its own panel**, for every filterable column, rather than using the built-in
one. The decision is deliberate:

- A `canListDistincts: false` column has no built-in mode that fits (§5.1), so *some* columns must
  nest regardless. Mixing the two would put two visually different panels in one grid.
- Panel markup is where the value/label split, the null distinct and `filterLabel` (§5.6, §5.7) are
  rendered. Owning it keeps that logic in one place.
- Nesting costs almost nothing, because `FilterContext` hands a consumer panel the same machinery the
  built-in one uses.

```html
<div *bsDatatableColumn="col.name;
      sortable: col.canSort !== false;
      filterable: col.canFilter !== false;
      filterActive: isFiltered(col.name);
      filterSummary: filterSummary(col.name)">
  {{ col.label | resolveTranslation }}
  <ng-container *bsDatatableFilterPanel="let values; ctx as ctx">
    <spark-column-filter-panel [column]="col" [values]="values()" [ctx]="ctx" />
  </ng-container>
</div>
```

**What the component keeps owning**, so Spark does not reimplement it: the async `[distincts]` source,
its 250 ms debounce and `AbortSignal` cancellation, the `matching`/`remaining` rebucketing, the
`filterChange` event, the overlay portal, the focus trap and Escape-restores-focus. `FilterContext`
exposes `values()`, `loading()`, `search(term)`, `apply(values, inverse)`, `clear()` and `onChange()`.

**What Spark owns:** the panel's markup and its styles. Bootstrap CSS does **not** reach the
document-root overlay — `.form-control` is only styled inside `bs-*` components — so the panel is
styled explicitly and held visually close to the library's own.

**A simplification that follows.** `apply(values, inverse)` always emits a `ValuesFilterChangeDetail`,
and Spark never uses `'comparison'` mode. So there is exactly one event shape, no discriminated-union
switch, and a free-text column simply calls `apply([{ value: typed, label: typed }], false)`.

The old worry about needing `<bs-form>` around the grid dissolves — the panel is not in the grid's
subtree at all.

`bs-query-builder` already exists in ng-bootstrap with a full `Expression` / operator / per-type editor
registry. This feature must not invent a second, incompatible filter expression shape; where the two
meet, reuse its vocabulary.

### 5.9 Scope across grid kinds

| Surface | Component | In scope |
|---|---|---|
| Top-level query | `spark-query-grid` in `spark-query-list` | **yes** |
| Sub-query on a detail page | the **same** `spark-query-grid`, wrapped in `spark-query-card` | **yes — free** |
| AsDetail (embedded), read-only and inline-edit | hand-rolled `<bs-table>` / `<table>` over `attr.value` | **no** (§9) |
| Streaming queries | `[data]` bound, never fetches | **no** (§9) |

The requester asked for "queries and subqueries", and sub-queries come free because they share the
component. AsDetail rows live inside the parent document with no server query at all, and their
columns come from a different pipe yielding `EntityAttributeDefinition`, not `QueryColumn` — a flag on
`QueryColumn` would not even reach them.

## 6. Decisions

Settled before implementation:

- **D1.** Flags live per-attribute with a sparse per-query override. *(Requester, against
  attribute-only and query-only.)*
- **D2.** The flags are **enforcement**, not decoration; therefore structural for hashing. *(Requester.)*
- **D3.** Three flags — `canListDistincts` added to the requested two. *(This PRD, §5.1.)*
- **D4.** Defaults are absent → capable, matching Vidyano and the `isVisible` convention.
- **D5.** Distinct values are computed **in memory over secured rows**, not by RavenDB facets. *(§4.)*
- **D6.** A distinct is `{ value, label }`; Vidyano's length-prefixed string is not copied. *(§5.6.)*
- **D7.** `queries[].sortColumns` is exempt from the `canSort` gate. *(§5.3.)*
- **D8.** `QueryColumn.IsSortable` is removed, not repurposed; backward compatibility is not required.
- **D9.** No per-value counts, ever. *(Cardinality oracle, §5.5.)*
- **D10.** Filters are transient client state. Named presets are out of scope.

Open, to be settled by a spike before M1:

- **O1.** Does an in-memory distinct pass over a realistically large secured result set stay within
  the latency the grid already pays? *(SP1.)*
- ~~**O2.** Does a floating filter popup escape the datatable's scroll container and sticky header?~~
  **Struck.** ng-bootstrap#415 portals the panel to a document-root `<mp-overlay-container>`, and a
  nested consumer panel mounts *inside* that pane, so it is covered too. **SP2 is moot — do not run
  it.** Caveat: the three-engine measurement backing the fix lives in a spike harness that was deleted
  after the write-up, and all of that repo's unit tests run under jsdom, which has no layout. No
  regression test guards it on either side, which is why PRD §10 keeps one Spark-side browser check.
- **O3.** For a reference column, is the breadcrumb always present on the mapped row at the point the
  distinct pass runs, including when `BreadcrumbProjectionSatisfiable` is false? *(SP3.)*

## 7. Blast radius

- **All four apps re-synchronize** in the same PR. Model files gain nothing where the flags are absent
  (`bool?` + `WhenWritingNull`), so the diff should be empty unless a flag is authored.
- **Two CI-only gates fire:** the model/description sync check (`pull-request.yml:124-138`) and the
  `libs/` version-bump check (`:162-207`). The latter requires **both** a `.csproj` `<Version>` bump
  and an `ng-spark` `package.json` bump, minor only.
- **Two repositories**, one unit of work, ng-bootstrap publishing first.
- **`QueryColumn.IsSortable` removal** is a wire break for `ng-spark`. Permitted (preview), minor bump.
- **No data migration.** Nothing is stored.
- **CodeCoverage is production.** It grants `anonymous` on some surfaces, where — per
  `docs/guide-row-security.md` — the row filter is the only thing between the public internet and the
  collection. A distinct-value list over an anonymous query is the highest-risk surface in the repo and
  must be covered by an explicit test (§10).

## 8. Acceptance criteria

1. A model author can set `canSort`, `canFilter`, `canListDistincts` on an attribute, and override any
   of them for one query via a sparse `queries[].columns[]` entry.
2. Running `--spark-synchronize-model` twice produces no diff on the second run, with the flags both
   present and absent, **including** on attributes the synchronizer rewrites (a changed `dataType`, a
   reassigned `order`) — the non-discriminating case is worthless.
3. `--spark-verify-model` errors when a `queries[].columns[]` entry names an attribute that is not on
   that query's surface.
4. A grid draws sort arrows only on columns resolving to `canSort: true`, and a filter cell only on
   columns resolving to `canFilter: true`.
5. `POST /spark/queries/execute` with a sort on a `canSort: false` column returns rows in index order
   and does not error — the refusal is silent and indistinguishable from an unsorted query.
6. `POST /spark/queries/execute` with `includes` on a `canFilter: false` column ignores them, silently.
7. `POST /spark/queries/distinct-values` on a `canListDistincts: false` column returns empty buckets.
8. Distinct values for a row-scoped type contain **no value that appears only in rows the caller
   cannot read**, under `GetRowFilterAsync` pushdown, under the post-materialization filter, under
   `IsAllowedAsync`-only types, and under a redacted column.
9. A reference column's distincts carry breadcrumb text as `label` and the raw id as `value`; a
   reference the caller may not read is **dropped**, not shown as the redacted placeholder.
10. Multiple column filters AND together; multiple values within one column OR together; `excludes`
    inverts.
11. A query's declared `sortColumns` still orders the grid when that column is `canSort: false`.
12. The filter row and its popup are keyboard-navigable and screen-reader-labelled, and the popup is
    not clipped by the datatable's scroll container in either paged or virtual mode.

## 9. Non-goals

- **RavenDB facets.** Revisit only after `Skip`/`Take` are pushed down; until then facets are less safe
  and less capable (§4). Recorded so it is not re-litigated.
- **Per-value counts.** Cardinality oracle (D9).
- **Named/saved filter presets.** Vidyano has them as a separate server-stored feature; not this work.
- **Filter persistence across reload or navigation.** Transient, matching Vidyano.
- **`canGroupBy`** and grouping.
- **AsDetail embedded grids** — a different component over in-document rows; would need a purely
  client-side array filter, designed separately.
- **Streaming queries** — they bind `[data]` and never fetch; a server-side filter is meaningless.
- **Type-specific controls** (date range pickers, numeric min/max). Vidyano ships none; every type gets
  the value list. Revisit on evidence, not on principle.
- **A property-level `[SparkAuthorize]`.** It does not exist (`AttributeTargets.Class | Method` only);
  this feature does not build one.

## 10. Tests that encode the intent

- **Preservation**, modelled on `TriggersRefreshPreservationTests.cs:10-22` and heeding its warning:
  assert the flags survive on attributes the synchronizer **does rewrite** (changed `dataType`,
  reassigned `order`), because an untouched attribute keeps the flag whether or not the update branch
  would have clobbered it.
- **Fixed point**, extending `SynchronizeIdempotencyTests.cs`.
- **Disclosure**, modelled on `SortColumnDisclosureTests.cs`, asserting on **emitted RQL**: a distinct
  request on a row-scoped type must not emit an unfiltered aggregate.
- **Row-security matrix** for distincts: pushdown path, post-materialization path,
  `IsAllowedAsync`-only type, redacted column, `ISparkOwnsRowSecurity` delegated mode, and an
  **anonymous** grant.
- **Denial parity**: the new endpoint added to `DenyAllEndpointMirrorTests.cs:113-122` and
  `EndpointCoverageTests.cs`.
- **A route-table completeness test.** None exists today, so a new endpoint silently stays absent from
  both clients, the README, the API spec and the deny-all mirror. Add it here.
- **Client**: `spark.service.spec.ts:52-85` pins the execute body shape and must gain `columns`;
  grid specs for arrow and filter-cell suppression.
- **E2E**: one filter application, not per-keystroke. E2E shares **one** rate-limit bucket
  (150/10s on 127.0.0.1) and a per-keystroke refetch would flake the whole suite.

## 11. Docs to rewrite in the same PR

`docs/guide-queries-and-sorting.md`, `docs/guide-search.md` (the clause-order section at :128-149 gains
the filter clause), `docs/guide-row-security.md` (distincts as a security surface),
`docs/Spark-API-Specification.md` (the new endpoint and, if it is a read endpoint, the antiforgery
exemption list at :11), and `libs/client/MintPlayer.Spark.Client/README.md:90-107`.
