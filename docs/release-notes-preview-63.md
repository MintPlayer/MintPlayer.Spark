# Release notes — `10.0.0-preview.63` / `@mintplayer/ng-spark@22.4.0`

Two components replace the two grids, a third de-duplicates the cell, a documented validation
gate that was never wired up now runs, and an AsDetail label that no client could ever resolve
now comes from the server.

`@mintplayer/ng-spark-auth` is unchanged and stays at `22.3.0`.

---

## `spark-query-grid` — one grid

`spark-query-list` and `spark-sub-query` were the same grid written twice, and — counting the
page's streaming and paged branches — the `<bs-datatable>` element written three times. That
duplication had already produced four user-visible bugs, each fixed on one side and not the
other: `[indeterminate]` on null booleans, permission state surviving a failed reload, a
swallowed fetch failure, and virtual-scroll sizing.

`@mintplayer/ng-spark/grid` now exports `SparkQueryGridComponent`, which owns query and
entity-type resolution, permissions, lookups, paging, sorting, search, the row-link gate,
selection and custom-action execution.

```html
<spark-query-grid queryId="cars" />
```

**Rows can come from either side.** With `data` unbound the grid fetches and pages server-side;
bound, it renders what it is given and never fetches. This is how streaming stays in
`spark-query-list` and out of every PO detail page's bundle, and it mirrors the line
`bs-datatable` already draws between `[data]` and `[fetch]`.

A **streaming query suppresses the fetch on its own**, without waiting to be handed `data` —
otherwise every streaming grid fired one pointless `/execute` on mount, because the grid resolves
the query before its host can see it.

### One fixed bug rides along

A sub-query resolved its entity type from the query's declared `entityType` **only**, while the
page also fell back to the source name. So a `Database.*` query that declared no `entityType`
rendered correctly as a page and as an **empty card** as a sub-query — no columns, no rows, no
error. There is one resolver now, and it is the page's.

## `spark-query-card` — chrome, with slots

A `<bs-card>` around the grid: icon, caption, actions.

```html
<spark-query-card queryId="cars">
  <spark-icon *sparkQueryIcon name="car-front" />
  <ng-container *sparkQueryActions="let actions">
    <button (click)="export()">Export</button>
  </ng-container>
</spark-query-card>
```

Three structural directives, following ng-bootstrap's `*bsDatatableColumn` convention with the
`spark` prefix — these are the first attribute directives in ng-spark:

| Directive | Slot | Default when absent |
|---|---|---|
| `*sparkQueryIcon` | header, left | nothing — nothing in the model describes an icon |
| `*sparkQueryCaption` | header, centre | the query description, falling back to its name |
| `*sparkQueryActions` | header, right | the server-declared custom actions |

**An omitted slot renders the default, and that is the design.** A sub-query is auto-rendered
once per entry in `EntityTypeDefinition.Queries` with no host projecting into it, so it must look
right with no host cooperation at all. Slots override defaults; they do not replace them.

Each slot takes an **optional query alias** — `*sparkQueryIcon="'cars'"` — because a detail page
renders one card per query and a bare slot would decorate all of them identically. A targeted
slot wins over the catch-all.

The actions slot's context carries the server's actions and the current selection, so a host
adding one button does not silently drop every action the type declares — and those are the ones
carrying `selectionRule` and the permission filter.

### Slotting into an auto-rendered sub-query

A structural directive cannot cross a component boundary, and `spark-po-detail` is created by the
router, so a default app has no tag to project into. It therefore accepts three forwarded
templates — `queryIconTemplate`, `queryCaptionTemplate`, `queryActionsTemplate` — which it passes
to every card. Substitute your own route component via `SparkRouteConfig.poDetail` to supply them.

## Breaking changes

1. **`SparkSubQueryComponent` is removed** from `@mintplayer/ng-spark/po-detail`. Use
   `SparkQueryCardComponent` from `@mintplayer/ng-spark/grid`; `queryId`, `parentId`,
   `parentType` and `reloadToken` carry over unchanged.
2. **`showCard` is gone.** `showCard="false"` meant "the grid without the card" — that is now
   `<spark-query-grid>`, a component choice rather than a flag.
3. **`headerTemplate` is gone**, replaced by the three slots. It replaced the entire header, so
   changing the icon meant re-implementing the caption and the action bar too.

`spark-query-list` keeps its selector, both routes, its inputs and its outputs.

The major stays at `22`: it tracks the Angular version, not our API.

## A malformed `selectionRule` is now refused at load

`SelectionRuleParser.IsValid` has existed since the parser was written, documented as *"call at
configuration load so a typo fails loudly at startup"*, and `guide-custom-actions.md` promised the
same. **Nothing called it.** A rule of `"1-5"` survived to the moment a user pressed the button,
where `Parse` threw `FormatException` out of the execute endpoint — a 500 on a user action rather
than a refused configuration.

`customActions.json` is now validated when it loads, naming every offender at once rather than one
per fix-and-retry cycle.

The guide's wording is corrected with it. Loading is lazy and hot-reloadable, so this surfaces the
first time custom actions are read, not at process start — the old text promised a gate the code
could not keep, which is how the gap went unnoticed.

## `spark-grid-cell` — the cell was written three times

The two grids were the visible duplication. The third copy — the AsDetail table on a PO detail
page — had already drifted, so the same column said different things depending on where it
appeared:

| Column type | Query grid | AsDetail table |
|---|---|---|
| `boolean` | checkbox, indeterminate when null | the text `"true"` / `"false"` |
| `color` | swatch | the hex string |
| custom renderer | registry lookup | a second, hand-copied lookup |

`@mintplayer/ng-spark/grid` now exports `SparkGridCellComponent`, which owns **presentation**:
which control a `dataType` becomes, and dispatching a declared renderer. Callers keep **value
resolution**, because the row models differ — a query row is a `PersistentObject` with an
attribute list and an id, an AsDetail row is a plain dictionary with neither.

Custom renderers are unaffected: `SparkGridRenderers.columnInputsFor` still exists and now
delegates to a new `cellInputsFor`, so the renderer contract is stated once instead of twice.

## An AsDetail label the client could not resolve

Opening a record for edit showed `(click to edit)` where the detail page showed the value — for
example an address that read correctly at
`/po/person/{id}` and not at `/po/person/{id}/edit`.

This affected any type whose breadcrumb template names a property excluded from the model with
`[IgnoreProperty]` — the **sanctioned** shape for a `[Breadcrumb]`-marked computed property
(`ModelSynchronizer` whitelists exactly this pairing). Server-side it resolves fine, by reflecting
over the CLR property. Client-side it can never resolve, by construction, for every row: the
attribute is deliberately absent from the model.

The server was already sending the resolved string on the nested object; the form's flattening
step discarded it. Both flatteners now carry it, and `AsDetailDisplayValuePipe` prefers it.

Nothing to change in an app. Two details worth knowing:

- **Client-side substitution still works** and remains the fallback. It is the only strategy on
  the create path, where no server object exists yet, so a template naming real attributes
  (`"{Street}, {City}"`) behaves exactly as before.
- **A blank breadcrumb is not shown.** The mapper never emits an empty one — a template that
  renders blank becomes the bare CLR type name — so an unset `Address` would have displayed the
  literal word `Address`. That placeholder is filtered back out; the field falls through to
  `(click to edit)` as it should.

The same fix landed one level down on the detail page, where a **nested** AsDetail column
stringified its inner dict to `[object Object]`.
