# Plan — Per-column sort and filter capabilities on query grids (#431)

PRD: [`query_column_filter_PRD.md`](query_column_filter_PRD.md). Issue:
[#431](https://github.com/MintPlayer/MintPlayer.Spark/issues/431) — body filled from §1 of the
PRD.

Status: **not started.** M1 shipped upstream; SP1, SP3, SP4 and SP5 run before the remaining milestones.

## Shape of the work

Originally two repositories. The ng-bootstrap half is **done**: ng-bootstrap#415 shipped the filter
row, the document-root overlay portal and `FilterContext` in `22.19.0` / web-components `2.16.0`.

What remains is `C:\Repos\MintPlayer.Spark` alone — model, server, endpoint, both clients, the panel
component, docs. Minor bumps on both a `libs/**` `.csproj` `<Version>` and `ng-spark`'s
`package.json`, or the CI version-bump gate at `.github/workflows/pull-request.yml:162-207` fails.
Dependencies already bumped: ng-bootstrap `^22.19.0`, web-components `^2.16.0` (one deduped copy).

Backward compatibility is not required (preview), which is what makes M2's `IsSortable` removal
possible.

---

## Spikes — run before the remaining milestones

Each is the smallest experiment that settles one open question. SP1 gates the design; SP3, SP4 and SP5
gate a milestone each. All live in `tests/MintPlayer.Spark.Tests` against `RavenTestDriver`
(known CPU-starvation flakes — never clean `RavenDBServer`); SP2 is moot.

### SP1 — Cost of the in-memory distinct pass *(gates the whole design; O1)*

The PRD chose in-memory distinct over facets for correctness (PRD §4). This measures whether it is
affordable.

Seed a collection at a realistic size (use CodeCoverage's ~200k-document `FileCoverages` shape as the
upper bound), run a query through the full pipeline to the point where `QueryExecutor.cs:741` has
materialized `allResults`, and time one extra distinct pass over a high-cardinality string column and
a low-cardinality boolean one.

**Decides:** whether the 100-item cap is enough of a guardrail on its own, or whether the distinct
pass needs an early-exit once the cap is hit (it can: the buckets are capped, so the pass may stop
collecting new values while still counting `hasMore`).

**Fails the design if:** the pass is not small relative to the materialization the request already
paid for. It should be, because the rows are already in memory — if it is not, the finding is that
M13/M14 must land first and the distinct pass must pay its own bounded cost (PRD §4d).

### ~~SP2~~ — Does a filter popup escape the datatable's scroll container? **MOOT — do not run**

Answered by ng-bootstrap#415: the panel is portalled to a document-root `<mp-overlay-container>`, and
a nested consumer panel mounts inside that pane, so Spark's own panel is covered too. O2 struck.

The evidence is a deleted spike harness rather than a regression test, and that repo's unit tests run
under jsdom (no layout), so the one browser check in M15 stays.

### SP3 — Is the breadcrumb present when the distinct pass runs? *(gates M8; O3)*

`EntityTypeDefinition.BreadcrumbProjectionSatisfiable` exists precisely because a projection often
cannot render its own breadcrumb. Assert, for a reference column: at the point after the row-security
gate where the distinct pass would run, is `attribute.Breadcrumb` populated — including when
`BreadcrumbProjectionSatisfiable` is false, and when the referenced document is denied (expect
`RedactedPlaceholder`, which M8 must **drop** rather than display).

**Decides:** whether M8 can read labels straight off the mapped row or must trigger resolution itself.

### SP4 — Equality against an analyzed field *(gates M6)*

A per-column equality filter against a `[Search]`-analyzed field behaves as a full-text match, not
equality: `Volkswagen Golf GTI` is indexed as three terms (`QueryExecutor.cs:1444-1452`), and a plain
default-indexed string is stored as one **lower-cased** term.

Store a multi-word value, then filter for it by exact value against (a) the `[Search]` field, (b) a
plain string field, (c) the `{Name}Sort` companion. Print what matches.

**Decides:** whether M6's filter predicate must redirect to `{Name}Sort` the way `ResolveSortProperty`
does for sorting, and whether a companion must be generated for every filterable column — which would
make M6 an index-shape change rather than a query change.

### SP5 — How often can the row filter actually push down? *(gates M13/M14)*

M14 is only worth building if the pushdown path is reached in practice. `ComposeRowFilterAsync` bails
on a projection element type, a `ConstantExpression` body and a system context — and the projection
case is the default for every `[GenerateIndex]` query, which is most grids.

Instrument `ComposeRowFilterAsync` and run the existing test suites plus the four apps' own queries,
counting which branch each query takes. No behaviour change; counting only.

**Decides:** whether M14 is a broad win or a narrow one, and therefore how much of the two-mode
complexity is justified. If almost everything bails at the projection branch, the honest answer may be
that the real fix is making the row filter composable into projections — a different and larger piece
of work, which this spike would then be the evidence for.

### Not spiked, deliberately

RavenDB facet behaviour (licence gating, auto-index support, Corax parity, `PageSize = int.MaxValue`
blow-up, term tokenization) is **not** spiked, because facets are ruled out on **security** grounds,
not performance ones (PRD §4a, §4b, §9). Paging pushdown landing in this same work does **not** revive
them: a facet still cannot honour a row filter that did not push down, and still cannot produce
breadcrumb text. The questions are recorded in the feasibility findings should that ever change —
starting with the one that matters: attach `AggregateBy` to a projection query with a row filter and
assert the terms against a user who may see 2 of 10 rows. Expect it to leak.

---

## Milestones

Ordered so that each is independently reviewable. The cross-repo dependency (M1) already landed.

### ~~M1~~ — ng-bootstrap: filter row in `mp-datatable` — **DONE upstream**

Shipped by [mintplayer-ng-bootstrap#415](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/415)
(closes [#414](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/414)), `22.19.0` /
web-components `2.16.0`, minor bumps with majors pinned. It delivered more than was asked: the row and
the document-root overlay portal, plus a built-in distinct-value panel and a comparison mode.

Spark uses the row, the portal and `FilterContext`, but **not** the built-in panel — see PRD §5.8.
Consume the published package; nothing here is Spark's to build.

<details>
<summary>Original scope, kept for the record</summary>

- `column-def.ts` — `filterRenderer?` / `filterable?` on `DatatableColumnDef`.
- `mp-datatable.ts` — a second `<tr>` in `<thead>`, `renderFilterRow()` beside `renderHeader()`.
- `datatable.styles.ts` — filter-row styling.
- A `*bsDatatableFilter` directive bridging an Angular template as an `EmbeddedView`.

Shipped as `*bsDatatableFilterPanel` nested inside the column, rather than a sibling keyed by name.
</details>


### M2 — Untangle `IsSortable`

`libs/spark/MintPlayer.Spark.Abstractions/QueryResult.cs` — remove `QueryColumn.IsSortable`. It is fed
the AsDetail drag-reorder flag (`QueryResultProjector.cs:56`), is `false` for every scalar column, and
the client ignores it. `EntityAttributeDefinition.IsSortable` **stays** — it is the drag-reorder flag
and is correct.

Fix `apps/DemoApp/DemoApp/App_Data/Model/StartPage.json:77,91`, which sets `isSortable: true` on
non-AsDetail scalar query columns as though it meant column sorting. That is the misuse the new
`canSort` replaces.

Wire break, permitted (preview). Verify by reading; no test run yet.

### M3 — The three flags on the model

- `EntityAttributeDefinition` — `bool? CanSort`, `CanFilter`, `CanListDistincts`. **`bool?`, never
  `bool`.**
- `SparkQuery` — a sparse `Columns` list of `{ name, canSort?, canFilter?, canListDistincts? }`.
- `ModelSynchronizer` — **no assignment.** These are hand-authored, not CLR-derived, so the update
  branch (`:783-853`) must not touch them; update the carried-over comment at `:910-912` to say so.
- `ModelFileShape.StructuralAttributeFields:168-173` — add all three (they gate a write; PRD §5.4).

### M4 — Resolution and projection

`QueryResultProjector.BuildColumns` (`:28-63`) resolves query override → attribute → `true` and writes
the result onto the per-request `QueryColumn`.

**Never onto `EntityAttributeDefinition`** — it is handed out by reference from a singleton
`ModelLoader` and `ShallowCopy()` shares `Attributes`. This is the live bug already fixed at
`Endpoints/EntityTypes/List.cs:140-150`.

### M5 — Enforce `canSort`

`QueryExecutor.IsSortableAttribute` (`:1670-1676`) gains `&& resolved.CanSort != false`, keeping the
**silent** refusal (console warning, index order) — a distinguishable refusal is an oracle.

A column named in the query's own `sortColumns` is exempt (PRD §5.3). `Execute.cs:71-110` already
unions those into its allow-list, so only the executor changes.

### M6 — The filter predicate — *needs SP4*

- `QueryRequests.cs` — `columns[] { name, includes[], excludes[] }` on `ExecuteQueryRequest`.
- `Execute.cs` — validate **after** authorization (`Execute.cs:43-55` records why the order is
  load-bearing).
- `QueryExecutor` — `IsFilterableAttribute` gating on `CanFilter != false`, silent refusal; build the
  predicate; redirect to `{Name}Sort` if SP4 says so.

**Clause order is fixed and load-bearing:** row-security filter → **column filters** → search →
`restrictToIds` → sort → page. The column filters go *after* the row-security predicate
(`QueryExecutor.cs:709`) and **must not pass `SearchOptions`** — measured on RavenDB 7.2.5, an
explicit `SearchOptions.Or` leaks onto the adjacent clause, and the adjacent clause is the row-security
predicate, silently turning a security filter into an alternative (`QueryExecutor.cs:1599-1608`).

Columns AND; values within a column OR; `excludes` inverts.

### M7 — `POST /spark/queries/distinct-values` — *needs SP1*

New `Endpoints/Queries/DistinctValues.cs`, literal route, `IPostEndpoint, IMemberOf<QueriesGroup>`.

Body: `queryId`, `column`, optional `search`, the other columns' filters, optional
`parentId`/`parentType` (required for sub-queries — the measured Vidyano protocol passes the owning
object, and the distinct computation needs it).

Order: authorize (404, never 403) → resolve column, check `canListDistincts` → run the secured
pipeline → distinct over the secured, mapped rows. Cap 100 per bucket + `hasMore`. **No counts**
(cardinality oracle, PRD D9).

**The response shape is `{ matching, remaining, hasMore }`** (PRD §5.5), but `remaining` is returned
empty: the component snapshots the list client-side on first selection and derives both buckets
itself. The server answers "what matches now" and nothing more.

`hasMore` must be **honest at the cap** — the component re-queries only when `hasMore` is set or the
search term is widened, so a dishonest `false` strands a user typing past a truncated list.

Also: add the right to `RowPolicyDeclarationValidator.RowReturningActions:33` if a new right name is
introduced, or the startup gate silently stops covering it.

### M8 — Labels, breadcrumbs and renderers — *needs SP3*

- Emit `{ value, label }` per distinct; `null` value for the null distinct.
- Reference columns: `value` = raw id, `label` = breadcrumb. A denied target is **dropped**, never
  shown as `RedactedPlaceholder`.
- Client: optional `filterLabel(value): string` on the renderer registration, pure, no row context.
  Absent → server text (PRD §5.7).

### M9 — Client: flags, filter cell, panel *(M1 shipped upstream; SP2 moot)*

Much smaller than originally scoped — the row, the portal and the distinct machinery are the library's.

- `models/src/query-result.ts` — `canSort?`, `canFilter?`, `canListDistincts?` on `QueryColumn`;
  a `QueryColumnFilter` shape. Wire casing needs no mapper: ASP.NET's web defaults emit camelCase and
  the client parses straight into the interface.
- `spark-query-grid.component.html:42` — replace the hard-coded `sortable: true` with
  `col.canSort !== false`; add `filterable`, `filterActive`, `filterSummary` to the microsyntax; nest
  `*bsDatatableFilterPanel` (PRD §5.8).
- **A new `spark-column-filter-panel`**, likely its own secondary entry point
  `ng-spark/column-filter/` (13+ exist; routine). Renders `values()` from `FilterContext`, the
  `matching`/`remaining` split, the null distinct as `< none >`, a search box wired to `ctx.search()`,
  the inverse toggle, and clear via `ctx.clear()`. Selection goes back through
  `ctx.apply(values, inverse)`. **Carries its own styles** — Bootstrap does not reach the document-root
  overlay. Icons via `<spark-icon>`, never a global `bootstrap-icons.css`.
- `spark-query-grid.component.ts` — a `filters` signal; `[distincts]` as a closure over the other
  columns' filters plus `parentId`/`parentType`, **re-created whenever another column's filter
  changes** or an in-flight request carries stale context (`DistinctsRequest` itself is only
  `{ column, search, signal }`); `onFilterChanged()` modelled exactly on `onSearchChanged():320-330` —
  **reset to page 1 and construct a fresh fetch identity**, or the datatable dedupes by
  `(page, perPage, sort)` and silently does not refetch (`:296-298`).
- **One event shape.** `ctx.apply()` always emits `ValuesFilterChangeDetail`, and Spark never uses
  `'comparison'` mode, so no discriminated-union switch is needed. A free-text column
  (`canListDistincts: false`) calls `apply([{ value: typed, label: typed }], false)`.
- `services/src/spark.service.ts:116-138` — pass `columns` in the execute body; add the distinct call.
- `[labels]` — bind the panel strings for i18n. New surface the original plan did not budget for; the
  Angular input is `Partial<DatatableLabels>`, so it is additive.
- Pin the new `set fetch(null)` reset behaviour with a spec: `spark-query-grid.component.ts:388` calls
  it on every query load, and the library now clears `_totalRecords`, page caches and in-flight
  responses. Strictly better for Spark, and plausibly fixes a latent stale-`totalRecords` bug when
  switching queries — so it should be locked down.
- `spark-query-list` — decide how the filter row relates to the existing search box. The old
  `<bs-form>` concern is gone: the panel is portalled out of the grid's subtree entirely.

### M10 — `MintPlayer.Spark.Client` + the route table

- `SparkClient.cs:363` — `columns` on execute; a `GetDistinctValuesAsync`; retire the
  `ParseSortColumns` string shim (`:387-405`), whose own comment says the typed overload *"belongs with
  the column-filtering work that motivated the move"*.
- Update all the hand-maintained enumerations: `README.md:90-107`, `docs/Spark-API-Specification.md`
  (+ the antiforgery exemption list at `:11`), `DenyAllEndpointMirrorTests.cs:113-122`,
  `EndpointCoverageTests.cs`.
- **Add the missing route-table completeness test** — none exists, which is why a new endpoint can
  silently stay absent from all of the above.

### M11 — `--spark-verify-model` check

A `queries[].columns[]` entry naming an attribute not on that query's surface is an error. It rides
`--spark-verify-model` beside the existing checks (`SparkDevelopmentExtensions.cs:184-193`, modelled on
`VerifyRefreshTriggersAreImplemented`), **not** a Roslyn analyzer — the flag lives in
`App_Data/Model/*.json`, which is not part of the compilation (`:254-268`).

Does **not** flag `canSort: false` on a column in that query's `sortColumns`; that is a deliberate
shape (PRD §5.3).

### M13 — Decide the paging mode, and expose it — *needs SP5*

`RowSecurity.ComposeRowFilterAsync` already knows whether it composed a real `Where` or bailed
(projection element type, `ConstantExpression` body, system context). Surface that decision as an
explicit result rather than leaving it implicit in the returned `IQueryable`, and thread it to
`QueryExecutor`.

Add the chosen mode to the query diagnostics (`docs/diagnostics.md`). A grid that silently picks
between two very different cost profiles is unexplainable in production otherwise.

Types in `RowSecurityMode.DelegatedToActions` and `IsAllowedAsync`-only types report "cannot push
down" unconditionally.

### M14 — Push `Skip` / `Take` down where it is safe

When M13 reports a genuine pushdown **and** no post-materialization gate can remove rows, issue
`Skip`/`Take` to RavenDB and take `TotalItems` from the database count. Otherwise keep today's
materialize-then-page path unchanged.

Two things not to get wrong:

- **`TotalItems` is the cardinality oracle** `QueryExecutor.cs:97-119` already refused once. It may
  come from the database **only** on the pushdown path, where the database count and the caller's
  visible count are the same number by construction. On the in-memory path it stays `allResults.Count`.
- **Redaction never removes rows**, only nulls attributes, so it does not block pushdown. `IsAllowedAsync`
  does. Check the distinction rather than assuming.

Tests: same query, same data, same user, asserted identical results and `TotalItems` under both modes
— a row-scoped type that pushes down, and one that cannot. The two paths must be indistinguishable to
a caller, or this becomes a correctness bug rather than a performance fix.

### M15 — Demo, tests, docs, version bumps (runs last)

- Author the flags in a demo app so the feature is visible: Fleet is the natural home.
- Full test sweep per PRD §10 — this is the **single batched run**, not per milestone.
- **One browser check of our own panel**, via the `playwright_node` MCP (never the `dcg:playwright`
  skill). Narrower than first scoped: ng-bootstrap#415 now ships
  `apps/ng-bootstrap-demo-e2e/e2e/datatable-filter.spec.ts` covering panel placement, non-clipping,
  light-tier styling across the portal, Tab-trapping with Escape restoring focus, and that a click in
  the filter row never sorts — on chromium and firefox.

  Two reasons ours does not disappear entirely:
  - **Their spec exercises the built-in panel; we nest our own** (PRD §5.8). The portal and focus-trap
    machinery is shared, so the risk is low — but our markup is not theirs.
  - **Occlusion is untested, as distinct from clipping.** Their spec does run in virtual mode (the
    filter table is `[virtualScroll]="true"` at `datatables.component.html:248-249`) and asserts the
    panel is not cut off by `.datatable-scroll { overflow: auto }`. It does not assert that the sticky
    `thead th { z-index: 1 }` fails to paint *over* it — a different failure, which a bounding-box
    check passes straight through. The portal should win at `z-index: 2000`, but `position: fixed`
    "should" have been there too and shipped missing. Assert it by hit test —
    `document.elementFromPoint` at the overlap resolving inside the panel, not to a `th`. Raised
    upstream; until it is covered there, cover it here.

  Mind the shared E2E rate-limit bucket: one pass, not per-keystroke.
- Docs per PRD §11.
- Re-synchronize all four apps; the diff should be empty where no flag is authored.
- Minor bumps: a `libs/**` `.csproj` `<Version>` **and** `ng-spark`'s `package.json`.

---

## Sequencing notes

- **SP1 before anything.** It can invalidate the in-memory design and send the feature behind
  server-side paging. SP2 is moot; SP3 and SP4 gate M8 and M6 respectively.
- **M1 is done upstream**, so the cross-repo sequencing constraint is gone. M2–M8 (server) and M9
  (client) can now proceed in parallel.
- **M2 before M3.** Removing the misused `IsSortable` first keeps the new flags from landing beside a
  field that means something else.
- **M5 and M6 can land before M7.** Sorting and filtering are useful with a free-text filter cell even
  before the distinct list exists — and that is exactly the degraded mode `canListDistincts: false`
  produces, so it must work anyway.
- **Test runs batch to M15.** Verify intermediate milestones by reading and type-checking.

## Risks

1. **SP1 invalidates the approach.** Mitigation: it is the first thing run, and the fallback (land
   server-side paging first) is a fix worth having regardless.
2. **The cross-repo publish stalls the work.** ng-bootstrap must publish before M9 completes. Mitigate
   by doing M1 first and the server half in parallel.
3. **A new disclosure oracle.** This is the same class of bug as `?sortColumns=` (#294-#296) and the
   distinct list is a *stronger* leak than ordering. Mitigated by computing distincts over
   already-secured rows (PRD §4), by `canListDistincts`, and by the row-security matrix test. **The
   anonymous-grant case on CodeCoverage is the one to get right.**
4. **`ISparkOwnsRowSecurity` types** run in `DelegatedToActions` mode with no framework filtering or
   redaction at all. The distinct pass must not assume the gate filtered anything. Cover it in the
   matrix test.
5. **Silent refusals are hard to debug.** `canSort: false` producing index order with only a console
   warning is right for security and confusing for authors. Mitigate in the docs, and make
   `--spark-verify-model` the place authors find mistakes.
6. **The two CI-only gates** (model sync, version bump) are not reproducible locally and will fail the
   PR if forgotten. Both are in M15's checklist.
7. **E2E rate limit** — one shared bucket, 150/10s on 127.0.0.1. Assert a fixed set of filter
   applications, never per-keystroke.
8. **`DateTimeOffset` columns.** The offset is stripped in index projections and there is a live bug
   where writes are silently dropped. Vidyano lists dates as formatted values with no range control,
   so the value list is the shipped behaviour — but do not let a filter round-trip a `DateTimeOffset`
   through a write path in this work.

## Out of scope

Genuinely not being done — not deferred work parked to keep the diff small (PRD §9 has the reasoning):

- RavenDB facets, and per-value counts.
- Named/saved filter presets; filter persistence across reload or navigation.
- `canGroupBy` and grouping.
- AsDetail embedded grids; streaming queries.
- Type-specific filter controls (date range pickers, numeric min/max).
- A property-level `[SparkAuthorize]`.

Server-side `Skip`/`Take` pushdown **was** listed here and has been moved **into** scope as M13/M14
(PRD §5.9). It is the real fix behind several findings, this feature makes the underlying problem
worse, and parking it as a follow-up would have been deferring work to keep a diff small rather than
genuinely declining it.
