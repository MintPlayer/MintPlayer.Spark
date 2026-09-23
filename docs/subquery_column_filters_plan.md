# Per-column filters on sub-queries — implementation plan

Companion to [`subquery_column_filters_PRD.md`](subquery_column_filters_PRD.md).
Branch: `fix/subquery-column-filters`. One PR, everything in it.

> ### ➡️ The PR outgrew its title. Start at [§ Query-page correctness campaign](#query-page-correctness-campaign).
>
> The sub-query fix (M1–M12) is done. A four-workstream audit then found that **filtering and sorting
> were broken in fourteen further ways**, several live in production and one an anonymous disclosure
> oracle. The goal this PR now serves is *filtering and sorting on query pages work correctly*, and the
> campaign section below is the authoritative list. Everything still lands here — one PR.

## Status

| | |
|---|---|
| Root cause | **Confirmed** — `ExecuteCustomQueryAsync` has no `columnFilters` parameter (`QueryExecutor.cs:1021-1024`) |
| Reproduced | **Yes**, on production, three sub-queries measured |
| Client | **Cleared** — audited, sends `columns` + `parentId` + `parentType` correctly, no client-side narrowing |
| Row security | **Audited, sound** — filters compose after the row filter, gate is unconditional, `Where` is monotone, no widening possible (PRD §3.5). One real hole: the redaction oracle (§3.6) |
| Pushdown | **Audited** — RQL on `Database.*`, dropped on every `Custom.*`. No C#-side column filtering exists today, and none may be added (D0/D2) |
| Spikes run | SP1 ✅ · SP2 ✅ (re-settled, see D2) · SP3 ✅ no work needed · SP4 ✅ confirmed + fixed · SP5 ✅ · SP6–SP8 not run (own work) |
| Milestones done | **all of M1–M12** ✅ · SP6–SP8 and P1–P7 deliberately out of scope (own work) |

---

## Spikes

Run these before the milestones they gate. Each one exists because a wrong answer changes the
design, not because it would be nice to know.

### SP1 — Does a filter compose safely onto an author's queryable? (gates M2)

`ApplyColumnFilters` builds `Expression.Equal` comparisons and reflects onto `Queryable.Where`.
On the database branch it always receives a Raven queryable the framework itself built. On the
custom branch it receives whatever the author returned.

Answer: does the composed RQL place filter clauses in the same position relative to the
row-security predicate as on the database branch? Use `RqlRecorder` — **attached before the session
is constructed**, or it records nothing and the assertion passes over an empty collection.

Kill criterion: if the clause lands adjacent to the security predicate differently, D1's position
is not sufficient on this branch and the composition order must change.

### SP2 — What happens to a shape that cannot push down? — **ANSWERED, after two reversals**

Final answer: **it is filtered in process, not refused.** A custom query returning a fixed set — a
constant list, a computed dashboard, or `session.Query<T>().ToListAsync()` because the set is small
enough to hand over whole — is a legitimate, supported shape. Refusing it punishes an author who did
nothing wrong, and "filtering a fixed set" can only mean doing it in memory.

The route there is worth keeping, because both wrong answers were plausible:

1. *Draft:* an in-memory fallback narrowing materialized `PersistentObject`s by their rendered text,
   modelled on the search fallback. **Rejected** — that is a second implementation, free to disagree
   with the queryable path about what a value equals, so the same filter would narrow differently
   depending on a shape the caller cannot see.
2. *Then:* refuse loudly. **Rejected** — see above.
3. *Shipped:* lift the sequence through `AsQueryable` and run the **same expression**. One predicate,
   one comparison semantic, every shape.

What survives from the strict version is the guarantee the shape rule does not give for free: where a
database query *exists*, the filter must be in it. Asserted on emitted RQL, because row counts cannot
tell a pushdown from in-process filtering.

### SP3 — Where does paging happen on the custom branch? (gates M4)

`ExecuteQueryAsync:313-319` computes `totalItems` and pages in memory unless `restrictToIds` is set
or the database already paged. Trace what the custom branch actually does today with `skip`/`take`
— note `CustomQueryArgs` carries `Skip` and `Take`, so the *author* may already have paged.

Answer: can a filter be applied before paging on this branch, or has the author already sliced?
If the author paged, filtering afterwards silently narrows one page and `TotalItems` is wrong —
which is D4's failure mode and may force the same refusal as D5.

### SP4 — The Repositories search 500

Diagnose `ApplySearch` over `Indexes.Repositories_Overview`. Hypothesis (mechanism identified, not
proven): `ResolveSearchableProperties` selects every string property regardless of what the index
analyzes, so `search()` is emitted against non-analyzed fields and RavenDB throws.

Capture the actual exception before fixing. Independent of #431 and a live production 500.

### SP5 — Streaming queries and filters — **ANSWERED**

**The defect is the affordance, and it is worse than "inert".** Nothing in
`spark-query-grid.component.html:58-69` consults `isStreamingQuery`, and the capability defaults are
`true`, so a streaming grid renders a filter button on every column. Opening the panel calls
`distinctsFn` → `/spark/queries/distinct-values` → `LoadSecuredRowsAsync` → `ExecuteCustomQueryAsync`
→ `ResolveCustomQueryMethod`, which accepts only zero parameters or one `CustomQueryArgs`. A
streaming method is `(StreamingQueryArgs, CancellationToken)`, so resolution returns null and the
executor throws; `DistinctValues.HandleAsync` catches only `SparkAccessDeniedException`, so **the
panel 500s**. If a value could be ticked, `onFilterChange` stores it and `onFilterChanged` returns
early at `:431` — no rows change, no error.

`ExecuteStreamingQueryAsync(SparkQuery, CancellationToken)` takes no filters, and there is nowhere to
put them: the socket handshake has no body.

**Decision: (b) — declare streaming columns non-filterable on the server**, where
`StreamingQueryExecutor.cs:58` builds its columns. The server states the truth once, the existing
`!== false` template polarity does the rest, and the sub-query card grid is fixed for free with no
client special-case.

Rejected (a) client-side narrowing: three changes, not one — a filter pass, a client-derived distincts
source to replace the 500-ing endpoint, and a semantic divergence, since a server filter covers the
whole result set while a streaming filter would cover *whatever has arrived so far* and silently
re-widen as patches land. A filter that quietly stops meaning what it said is the failure class this
PR exists to remove.

**Nobody is using this**: `isStreamingQuery: true` appears exactly twice, both demos
(`DemoApp/Stock.json:162`, `Fleet/Company.json:145`). CodeCoverage — the only production app — has it
false everywhere.

**Do alongside, whichever option:** `GetDistinctValuesAsync` must refuse a streaming query explicitly
rather than 500. Note `CanListDistincts` is checked *after* rows load (`:110`), so
`canListDistincts: false` would not have saved it.

---

## Milestones

### M1 — A failing test for the reported bug
Extend `ExecuteQueryParentGateTests.cs` (or a sibling) with a sub-query execution that passes
`columnFilters` over a `Custom.*` source and asserts narrowing. **Must fail before any fix.**
This is the test whose absence shipped the bug — the two existing suites are disjoint exactly here.

### M2 — Thread `columnFilters` into the custom branch *(gated by SP1)*
Add the parameter to `ExecuteCustomQueryAsync` (`:1021-1024`), pass it at the call site (`:229`),
and apply after `ComposeRowFilterAsync` (`:1185-1188`) and before `ApplySearch` (`:1203`), guarded
by `isQueryable` the way search is guarded by `isRavenQueryable`.

### M3 — Every shape filters, by the same predicate *(gated by SP2)* — **✅ done**
A Raven queryable composes into RQL. A non-Raven `IQueryable` filters through its own provider. An
`IEnumerable<T>`/`List<T>` is lifted through `AsQueryable` and filtered by the **same expression**,
rather than by a second hand-written implementation that could disagree about what a value equals.

The trap this leaves open, and why one test asserts RQL rather than rows: `Queryable.Where` over an
`EnumerableQuery` provider composes and executes in C# with no error and no warning, so "it returned
the right rows" is not evidence of pushdown. A Raven-backed query that had silently become an
in-memory one would look identical from the outside.

### M4 — Filtered `TotalItems` and paging *(gated by SP3)* — **✅ no work needed**
SP3 answered it: the filter composes into the queryable *before* materialization, and the custom
branch derives `totalItems` from `allResults.Count` over the materialized set, so the count is
post-filter automatically and in-memory paging slices already-filtered rows. Pinned by an assertion
in `Filter_narrows_a_custom_query` rather than left implicit.

### M5 — Distincts on the custom branch
`LoadSecuredRowsAsync:200` — pass `columnFilters` instead of `null`. Makes the panel's values agree
with what `execute` will return.

### M6 — Refuse filters on an author-paged query (D5)
400 when a `SparkQueryPage` short-circuit (`:252-285`) meets a non-empty `columnFilters`. Explicit,
not silent.

### M7 — Fix the search 500 *(gated by SP4 — confirmed, verbatim exception captured)*

```
System.ArgumentException: The field 'SecretToken' is not indexed in 'Sp4Gadgets/Overview',
cannot query/sort on fields that are not indexed in query: from index 'Sp4Gadgets/Overview'
where (Owner = $p0) and (search(Label, $p1, and) or search(Owner, $p2, and) or search(SecretToken, $p3, and))
```

Scope is wider than the production symptom: **not custom-only.**

| shape | search over a static index |
|---|---|
| `Database.*` + index **with** a `[FromIndex]` projection | safe — *by accident*: `sortType` becomes the V-type, whose properties are the index's fields |
| `Database.*` + index **without** a projection | **broken** |
| **every** `Custom.*` + any static index | **broken** — `ApplySearch` gets the author's element type, never the projection |

Fix: restrict the searchable set to fields the bound index actually emits.

1. Resolve the bound index. Already in hand on `Database.*`; on `Custom.*` cast the queryable to
   `Raven.Client.Documents.Session.IRavenQueryInspector` and read `IndexName` — verified to return
   the deployed name for a static index and **null for dynamic/auto**, which is an exact,
   non-heuristic discriminator.
2. `IndexName` null → dynamic → keep today's behaviour. An auto-index indexes whatever the query
   names, so it cannot throw (measured: `Auto/X/ByNote` and `Auto/X/BySearch(Note)` are created
   separately, per query shape).
3. Index has a `ProjectionType` → intersect the entity's string properties with the projection's
   property names. Fixes production with pushdown retained.
4. Static index with **no** `ProjectionType` → the field set is genuinely unknowable (it exists only
   as a rendered string in `IndexDefinition.Maps`) → **do not push down**; return `Applied: false`
   and let the existing in-memory fallback handle it. Slower, never wrong, and already tested.
   This is not a rare branch: **7 of the 10 production hand-written indexes have no projection.**

⚠️ Implementation trap: `IndexCatalog` keys on `indexType.Name` (`Sp4Gadgets_Overview`) while
`IRavenQueryInspector.IndexName` returns the deployed name (`Sp4Gadgets/Overview`). Normalise `/`→`_`
(unambiguous — a CLR type name cannot contain `/`) or key both forms in `RegisterIndex`.

Rejected: catch-and-retry on the RavenDB exception. It must match on the exception *message*
(the type is a generic `RavenException` wrapping `ArgumentException`), covers two statements (count
and page), pays a wasted round trip forever rather than once, and writes a real error into the
RavenDB log on an ordinary user action.

### M7b — Column metadata must reflect what the index can actually do
The enforcement sites already skip a column absent from the projection, with a warning
(`QueryExecutor.cs:1767-1775` for sort, `:1983-1991` for filter). What is missing is that the column
metadata **sent to the client** does not reflect it, so the grid draws a sort arrow and a filter cell
the server then silently ignores. Thread the resolved projection type through `QuerySourceResult`
and AND an index-derived term into `ColumnCapabilities`, exactly as the streaming refusal added
earlier in this PR already does. Per-query, no model-file churn, never touches an author's field.

### M7c — Pin the silent degradation
No test anywhere pins that `FieldIndexing.No` makes a filter return **zero rows** and a sort a
**no-op**, both with a 200 and no diagnostic. That is worse than the loud failure and is currently
asserted only by documentation. Measured during SP4; needs a test before anything derives from it.

### M8 — Streaming queries *(gated by SP5)* — **✅ done**
`ColumnCapabilities.CanFilter`/`CanListDistincts` return false for a streaming query, so the server
states once that a stream cannot be filtered and no client renders the affordance — the routed grid,
the sub-query card and any future client at once. Sorting is untouched; the client sorts the
accumulated snapshot itself. `GetDistinctValuesAsync` refuses a streaming query up front as well, so
an older client or a hand-made request gets `DistinctValuesResult.Empty` instead of a stack trace;
the existing `CanListDistincts` check could not cover it, because it runs *after* the load that threw.

### M9 — Test the paths, not just the case
- Column filters over `Custom.*`: includes, excludes, multi-column AND, queryable and materialized.
- Distincts over `Custom.*` with another column filtered.
- RQL position assertion on the custom branch (mirrors the database-branch assertion).
- `canFilter: false` on a custom query — still silently refused, behaviour unchanged.
- A `DenyAllRowSecurity` case on the filtered custom path: zero rows reach the wire.

### M9b — Pin the row-security guarantee that is currently unpinned
`ColumnFilterDisclosureTests` has **no row rule in its fixture**, so the test named
`The_filter_clause_does_not_displace_the_row_security_predicate` asserts adjacency against the search
group, not a security predicate. Add a fixture with a real `IRowSecurity` and assert (a) the RQL
clause position against an actual security `Where`, and (b) that `DenyAllRowSecurity` returns zero
rows under an active filter. Correct by construction today — but nothing would catch a regression.

### M9c — The redaction oracle (PRD §3.6) — **✅ documented, not closed**
Decided: document the obligation rather than gate on the hook. The framework *cannot* close it —
`GetProtectedAttributesAsync` takes an entity and may answer per row, so it cannot decide a
query-level operation; by the time rows exist the filtering has already happened. That is the same
reasoning the sort path already carries at `:1702-1707`, and it applies unchanged.

So the mitigation stays static (`canFilter: false`, `canListDistincts: false`) while the hazard is
dynamic, and the asymmetry is now stated where an author will meet it: a section in
`docs/guide-authorization.md` and remarks on `ApplyColumnFilters`. Both flags default to `true`, so
this is an obligation, not a default — which is exactly why leaving it undocumented was the problem.

### M9d — The smaller correctness defects (PRD §3.8)
- Filter on a column absent from the projection: report rather than silently skip.
- `Expression.Constant(null, propertyType)` 500 on a non-nullable value-type column — pass the
  underlying type, or skip the value.
- Route the warnings to the injected `ILogger` instead of `Console.WriteLine`.
- Enforce `IRavenQueryable<T>` at `:794-796` instead of only naming it in the error message.
- `mayPageInDatabase` condition 4 vs a non-projecting fan-out index (inflated `TotalItems`).

### M10 — Guard against the next disjoint-suite gap
A test that fails if a refinement is wired into one branch and not the other. The specific shape of
this bug — a parameter present on one branch's signature and absent on the other's — is mechanically
detectable by reflection over the two method signatures, and would have caught it at authoring time.

### M11 — Demo coverage
Give a sub-query in DemoApp or HR a filterable column so the feature is exercised by something a
browser check actually visits. **Fleet declares no sub-queries** — this is why #440's browser
verification passed while production was broken. Verifying against an app without the shape under
test is the process failure worth fixing, not just the code.

### M11 + M12 — Verified in a browser, against an app that has the shape — **✅ done**

Run against **DemoApp**, not Fleet. Fleet declares no sub-queries at all, which is why #440's browser
check passed while production was broken; verifying there again would have proved nothing. DemoApp's
`Company` PO has `company-people` and `company-cars`.

Measured on `companies/1-A` (4 cars — 3 Volkswagen Transporter, 1 Mercedes Sprinter):

| case | result |
|---|---|
| unfiltered | 4 |
| `Model includes [Mercedes Sprinter]` | **1** — the Sprinter |
| `Model includes [Volkswagen Transporter]` | **3** |
| both values | 4 (values OR within a column) |
| `Model excludes [Mercedes Sprinter]` | **3** |
| nonsense value | **0** |
| `search=Mercedes` | **1**, no 500 |
| distincts `LicensePlate`, unfiltered | 3 values |
| distincts `LicensePlate` under the Sprinter filter | **1** — cross-filtering works |

The positive cases are the load-bearing ones: "everything returns 0" is equally consistent with
over-filtering, so a nonsense value alone would not have distinguished a fix from a new bug.

Note `Volkswagen Transporter` — a multi-word value matched exactly on a `[Search]`-analyzed field,
which only works because equality resolves through the `{Name}Sort` companion.

**Through the UI**, not just the endpoint: opened the Model panel on the cars grid (values listed),
ticked *Mercedes Sprinter*, and the grid went from 4 rows to exactly
`2-DEF-222 Mercedes Sprinter 2022 Contoso Logistics`.

---

## Found along the way

Appended as discovered, so a later investigator fixes them in **this** PR.

- **G1** — `ResolveSearchableProperties` ignores index field options entirely (`:1813-1822`); the
  known-gap comment above it admits the `Exact` case but not the not-indexed-at-all case. SP4/M7.
- **G2** — The disjointness of the three test suites is the real defect; M10 is the guard.
- **G3** — Browser verification during #440 ran against the one app with no sub-queries. Worth a
  line in the testing docs: verify against an app that has the shape under test.
- **G4** — `Execute.cs:117-143` validates filter column names against `query.EntityType`'s
  attributes and 400s on unknown. Confirm this is reached identically for custom sources, so the
  fix does not turn a previously-400 request into a silently-unfiltered one.

## Spikes for the index/model agreement route (PRD §3.10)

### SP6 — Unify the two index descriptors *(gates P1 and the analyzer rules)*
`GeneratedIndexInfo` carries collection/index/projection/isDefault; `HandWrittenIndexEntityInfo`
carries neither the collection type nor `[DefaultIndex]`, though `DescribeHandWritten` already holds
the index `INamedTypeSymbol` two lines from both. Add the two fields, then put one shared shape over
both records so the full 17-row graph exists inside a single generator.

Kill criterion: if the two pools cannot share a shape without disturbing the `generatedNames`
subtraction at `GenerateIndexGenerator.cs:170-181`, keep them separate and pass the graph alongside.

⚠️ **Do not copy the base-type walk verbatim** — all three existing copies mishandle multi-map and
2-arg map-reduce (PRD §3.10.6). Fix it once in the shared helper.

### SP7 — Prove an analyzer reads generator output *(gates every analyzer rule)*
The repo asserts it in prose twice and **no test demonstrates it**: the harness attaches analyzers
without running generators, and `DefaultIndexAnalyzerTests.cs:190-191` says so. Before writing a rule
that depends on seeing a generated `VAccount`, write a fixture that runs the generator *and* the
analyzer and proves the symbol is visible.

Kill criterion: if generated trees are not reliably analyzed (host settings can skip them), every
rule must re-derive from `[GenerateIndex]` the way `DefaultIndexAnalyzer` deliberately does, and
degrade rather than go silent. Also confirm the cross-project diagnostic drop measured at
`ValueObjectCompletenessAnalyzer.cs:136-150` — entity library + app-owned index is exactly that
topology.

### SP8 — Measure the reindex cost of a `Fields`-only definition change
Still unmeasured, and it gates any migration. Deploy a changed definition against a scratch database
with production-shaped data and observe whether the index resets. CodeCoverage's `Commits` index
backs the commit list, history chart, sparklines and badges, which return partial results during a
rebuild. **Do not quote a number without running this.**

## ➡️ Successor: [`spark_index_agreement_PRD.md`](spark_index_agreement_PRD.md) + [plan](spark_index_agreement_plan.md)

P1–P7 and SP6–SP8 below were the raw notes; they are now written up properly as their own PRD and
plan, including the measured RavenDB ground truth and the safe migration order. **Start there**, not
here. The list below is kept only so the provenance of each item is traceable.

## Proposed as their own work (investigated here, deliberately not smuggled in)

Recorded so the investigation is not lost. These are new capability, not this bug's fix, and one
carries an unmeasured production cost — see PRD §3.9.

- **P1 — `SparkIndexCreationTask<T>` supplying the call site.** The generator already emits the
  `Index(...)` body; what it cannot do is add a statement to a hand-written constructor. A base class
  overriding `CreateIndexDefinition()` can, turning "you must call `IndexSearchFields()`" from
  documentation into structure. Needs a new package (`Abstractions` has no `RavenDB.Client` by
  design), must not carry `StoreAllFields` (`CommitIndexShapeGuardTests` forbids it on
  `Commits_ByRepository`), must not clobber a hand-written `Index(...)` from `OnInitialize()`, and
  **must have the reindex cost of a `Fields`-only definition change measured first** — CodeCoverage's
  `Commits` index backs the commit list, history chart, sparklines and badges, which return partial
  results during a rebuild.
- **P2 — An analyzer for the uncalled `IndexSearchFields()`.** ~30 lines, reusing helpers already in
  `SortCompanionAnalyzer`. Cheapest item here and closes a latent trap regardless of whether P1
  happens. Currently *latent, not live*: every index with `[Search]` fields either calls the method
  or is fully generated.
- **P3 — A test pinning that synchronize never writes the capability flags.** The invariant is
  protected only by the comment at `ModelSynchronizer.cs:830-837`; the #431 PRD listed this test and
  it was never written.
- **P4 — Fix the multi-map collection-type derivation** before any multi-map variant ships.
  `IndexCatalog.cs:223-231`, `SparkMiddleware.cs:795-803` and `DefaultIndexAnalyzer.cs:143-150` all
  read `AbstractMultiMapIndexCreationTask<T>`'s `T` as the *collection* type, but it is the
  **reduce result**. Masked today because the repo's only multi-map is a test whose reduce result
  happens to be the entity.

- **P5 — Fix the multi-map / 2-arg base-type walk** in all three copies, and the test stub that
  encodes the wrong hierarchy (`DefaultIndexAnalyzerTests.cs:22-24`). Latent today — no index in the
  repo is multi-map or 2-arg — but a real 2-arg map-reduce index silently never registers.
- **P6 — Two analyzer rules for claims that cannot be honoured:** `canSort`/`canFilter: true` on a
  collection attribute, and `canSort: true` on a `TranslatedString` (the flag resolves to a
  projection property that does not exist, because `ResolveSortProperty` never appends a language).
- **P7 — `PullRequestFeedback` has a generated index and projection but no model file**; `Commit` has
  no `queryType`/`indexName`, so a `Database.*` query over it reads the raw collection rather than
  `Commits_ByRepository`. Both are pre-existing and unrelated to this bug.

**Explicitly rejected — do not re-litigate:**

1. **Synchronize writing the flags into the model JSON.** One attribute, many queries, different
   indexes (PRD §3.9.1), no field-level visibility offline, no provenance signal, and it reverses the
   deliberate `bool?` design. Derive at runtime instead (M7b).
2. **The capability flags driving index configuration** (PRD §3.10.3). The emission list is empty —
   everything a `true` could require is already emitted unconditionally — and `false → FieldIndexing.No`
   is actively dangerous: it degrades silently, applies to every query through the index, makes
   `search()` throw, and defeats the `SortColumnsAreCallerSupplied` exemption at
   `QueryExecutor.cs:2219`. The three flags authored anywhere in this repo are presentation and
   disclosure judgements, none of which describes anything an index could be configured for.
   *The mechanism is fine; the input is wrong.*

## Carried over from #431 (not resolved here)

`docs/query_column_filter_plan.md` still lists F1–F8. **F1** — free-text search matching
off-surface columns — remains open and needs the user's decision. F7 (filterActive freeze) was never
proven either way. These are not in scope for this PR unless the user says otherwise.

---

# Query-page correctness campaign

**Goal: filtering and sorting on Spark query pages work correctly.** Four workstreams audited the
whole path (root `Database.*`, custom, sub-query) in 2026-09-23. This is the authoritative list.

## New measured ground truth (none of it was in either PRD)

| # | measurement |
|---|---|
| **G-a** | **`Exact` is not a milder `Search`.** `search()` over a non-analyzed field returns **0 rows, HTTP 200** — it does not throw; only `FieldIndexing.No` throws. `Exact` differs from no `Index()` call in exactly one respect: **case sensitivity**. Pinned by `ExactVersusSearchSemanticsTests` |
| **G-b** | **Ordering by a collection DROPS ROWS.** 4 docs, one with an empty collection → `OrderBy` returns 3, `OrderByDescending` returns **the same 3 in the same order**. Nullable-scalar controls returned 4 and reversed exactly, so it is collection-ness, not a missing term |
| **G-c** | **A projection resolves PER FIELD.** Stored fields come from the index, unstored ones from the **document**. This is the lever that removes the `DateTimeOffset` hazard instead of managing it — see M8.2 |
| **G-d** | **The lambda/string field collision throws CLIENT-SIDE**, from `CreateIndexDefinition()`, in ~47 ms with no server. The PRD said "at deploy". So the guard is a construction test, not an analyzer heuristic |
| **G-e** | **`Take(0)` bounds the page but NOT `Count()`.** Rows vanish while `TotalItems` reports the unfiltered total. Narrowing to nothing must be a predicate |
| **G-f** | **A field that is neither indexed nor stored is rejected by Corax at map time** — deploy succeeds, then `state=Error, entries=0` and every query 500s. So `FieldIndexing.No` *obliges* a `Store` |

## The defect table

| # | symptom | live where | status |
|---|---|---|---|
| **D1** | Unconvertible filter value on a **nullable** column returned the rows with **no** value — the complement of the request — and leaked enum membership to an **anonymous** caller (0-vs-3) | **LIVE DemoApp** | ✅ `22ae9732` |
| **D2** | `< none >` distinct round-trips as `Col == null`, which matches present-and-null but **not absent** — wrong rows in both directions, on a value the panel itself offered | **LIVE DemoApp**, root + sub-query | ⏳ |
| **D3** | Every AsDetail column's filter panel collapses to a single `< none >` (`EntityMapper` sets `attr.Value = null`) | **LIVE production CodeCoverage** ×6 | ✅ capability refusal |
| **D4** | Filtering a **singular complex** column returned the complement — the same construction the collection fix removed | **LIVE production CodeCoverage** ×3 | ✅ `22ae9732` |
| **D5** | Complex-collection filter emitted `Any(e => e == null)` → no rows | **LIVE production CodeCoverage** ×3 | ✅ `22ae9732` |
| **D6** | Complex columns draw a sort arrow that orders nothing (`FieldIndexing.No` degrades silently) | **LIVE production CodeCoverage** ×6 | ✅ capability refusal |
| **D7** | Opening any filter panel materializes the **whole** result set (`take: int.MaxValue`); the endpoint's `MaxTake = 1000` applies only to `/execute` | **LIVE production CodeCoverage** | ⏳ |
| **D8** | `sortType` for a `Custom.*` query is the **entity**, wider than the Map → `canSort/canFilter` true for fields the Map never assigns → **HTTP 500** | latent, one refactor away | ⏳ |
| **D9** | A streaming query hit through `/queries/execute` 500s — no `IsStreamingQuery` guard, unlike `GetDistinctValuesAsync` | measured DemoApp | ⏳ |
| **D10** | Distinct panel sorts numbers as text (`['1','120','20','40']`) and caps at an arbitrary first 100 | **LIVE DemoApp** | ⏳ |
| **D11** | A filter naming a non-query-surface attribute is accepted then ignored (200, unnarrowed) | API-only | ⏳ |
| **D12** | Distinct panel would collapse to the ticked values | latent — client omits the self column | ⚪ verified not live |
| **D13** | Page offsets drift on a non-projecting fan-out index | latent | ⏳ |
| **D14** | `DateTimeOffset` filter may match nothing (panel offers `+02:00`, index term is UTC) | **UNMEASURED** | ⏳ needs one test |
| **D15** | **Two people grids ship unsorted** — `GetPeople` sorts by `LastName`, which is `showedOn: PersistentObject` in both apps | **LIVE DemoApp + HR** | ✅ `ec258892` |
| **D16** | Author-paged queries build column metadata without `sortType`, so they skip `IsBackedByShape` on the one path that also hands filtering to the author | latent | ✅ `22ae9732` |
| **D17** | Generated `{Name}Raw` wrapper gets `Index(…, No)` but no `Store` → index **fails to build** unless the author happens to call `StoreAllFields` | latent (all current indexes call it) | ✅ generator fix |

⚠️ **D12 is the one to leave alone.** The client correctly omits the listed column from its own
`distinct-values` request; cascading narrowing works end to end, verified in the network trace.

## Remaining work, in order

### R1 — D2, the `< none >` round-trip *(wrong rows, live)*
An absent JSON field does not satisfy `== null` in RavenDB, but the distinct list builds `< none >`
from *mapped* rows where absent and null are indistinguishable. Either the predicate must express
"absent or null", or the distinct list must stop offering `< none >` on a field whose absence it
cannot round-trip. ⚠️ Same family as the repo's own `!= true` rule — see
`Commits_ByRepository.Result.ContributedFromFork`, where this exact confusion emptied every grid for
pre-existing repositories while the suite stayed green.

### R2 — D7, unbounded materialization on a production filter panel
`GetDistinctValuesAsync` → `LoadSecuredRowsAsync(take: int.MaxValue)`. Needs the same clamp
`/execute` has, or a cap with an honest "list truncated" signal.

### R3 — D8/A3, an index bound with no projection
`sortType` falls back to the entity, which is a **superset** of the Map, so Spark validates a sort
against fields the index cannot serve. General form of the `Commit.GetCommits` → `Sha` hazard.
Repair is a `[FromIndex]` projection; the verify rule below makes it impossible to reintroduce.

### R4 — the `{Name}Search` inversion
Today the base field carries `Index(Search)`, which **destroys equality on it**, and the plain value
is exiled to `{Name}Sort`. Invert: base field plain, companion `{Name}Search` carries `Search`.

⛳ **The decisive argument is that the repo already does it correctly for `DateTimeOffset`** — base
field plain, abnormality on the `{Name}Raw` companion (`GenerateIndexGenerator.cs:387`). The two
companion mechanisms are mirror images and only one is right.

Hazard it closes: anything that does **not** go through `ResolveSortProperty` and names the base field
gets broken equality silently. Latent today only because `RowSecurity.cs:309-321` refuses pushdown
whenever `elementType != entityType`, i.e. for every projection-bearing index. ⚠️ Also closes a second
one nobody had named: the companion is on the **hot path**, so a custom query that hand-builds rows
and forgets to populate `{Name}Sort` silently matches nothing on both filter and sort.

Cost: **zero** production reindex (CodeCoverage has no `[Search]` at all), zero snapshot churn, zero
model/hash churn, 3 DemoApp files. ~32–36 files total, concentrated in 3.

### R5 — M8.2 / `Commits_ByRepository` gets a real projection ⚠️ production
**GO**, with **per-field `Store`, never `StoreAllFields`** (G-c). Measured: `+02:00`, `-05:00`,
`+05:30` all exact; computed fields correct; a document predating `Commit.Date` recovered through the
stored `DateRaw`. Store only `DateRaw`, `HasCoverage`, `ParentLookupDone`, `CompleteCoverage`.
⚠️ `Coverage` must **not** be in the Map — a complex field there forces `No` + storage or Corax fails
the index (G-f); unmapped, it is read from the document anyway.
⚠️ `Commit.json` needs hand-authored `canSort/canFilter/canListDistincts: false` on `Coverage`:
`IsBackedByShape` checks the **CLR shape**, which is a superset of the Map, so it cannot catch this.
⚠️ `[Reference(typeof(Repository))]` on the projection is **mandatory** — SPARK002 is a build error.
The guard test is widened **deliberately** to "no scalar `DateTimeOffset` is stored, and each carries a
stored `{Name}Raw`", plus an allow-list — not deleted.

### R6 — the verify-model rules
Placement rule: **a check belongs in `--spark-verify-model` when it needs the index catalog, the
rendered `IndexDefinition.Maps`, or a judgement about a model file's *content*; it belongs in an
analyzer only when its whole subject is hand-written C#.** (Not "because analyzers cannot see the
model files" — they can, `spark.targets:159`; it is staleness plus the two capabilities.)

| rule | severity | note |
|---|---|---|
| **A1** `VerifyQuerySortColumnsResolve` | error | would have caught D15 in both apps. Ship first |
| **A2** `VerifyQuerySurfaceIsBackedByIndexShape` | error | 0 hits; ⚠️ scope to the **default** binding — `Company_Cars` narrows legitimately |
| **A3** `VerifyBoundIndexHasAProjection` | error | closes D8 by construction |
| **B1** explicit `canSort: true` on an array | error | 0 hits; the runtime refusal does the real work |
| **B3** `VerifyBoundIndexEmitsSortColumns` | warning | word-boundary probe over `Maps`; one-sided |
| **C1** `VerifyIndexDefinitionsCompile` | error | ~45 lines, **zero false positives by construction** — it performs the real operation |
| **C2** `SparkIndexCreationTask.IsFieldDeclared` | — | makes the emitted guard total by also walking `Indexes.Keys` |
| **SPARK019** | error | forbid the lambda form; rewrite the two OIDC indexes in this PR |

⚠️ **Every model-driven rule is silent in `libs/` structurally, not by care**: verify never runs there
(no `App_Data/Model`, `IsUsableContextType` returns early). SPARK019 is the only one that reaches
`libs/`, deliberately.

### R7 — B2 `canSort` on a `TranslatedString`: **drop the rule**
Measured: exactly one `TranslatedString` repo-wide, and **zero** on any query surface. Three guards
already stand in front. A rule with no reachable subject is maintenance with no payoff.

### R8 — docs
~35 defects found across the two PRD/plan pairs. The load-bearing ones:
- ✅ `subquery_column_filters_PRD.md:315` claimed `search()` "works" with no `Index()` call. **Fixed** —
  that single cell was the only documented statement making today's shape look safe.
- `spark_index_agreement_PRD.md:6` says "not implemented" while its plan and the code say otherwise;
  ~8 stale present-tense claims trace to that one line.
- D5/D7 in `subquery_column_filters_PRD.md` describe behaviour the PR **deliberately reversed** (no
  400 for author-paged filters; `CustomQueryArgs.Columns` now exists). A future reader could "restore"
  them.
- "6 of 10 hand-written indexes have no projection" becomes **5 of 10** after R5 — also in
  `QueryExecutor.cs:1904`.
- ⚠️ **~28 drifted `QueryExecutor.cs` citations** — the file grew ~460 lines this PR. Three are
  actively misleading, including "`ExecuteCustomQueryAsync` has no `columnFilters` parameter".
- `SearchAttribute`'s own doc references a `SortExpression` member **that does not exist**.
- `OidcCorsOrigins.cs:95-98` states a mechanism refuted by G-c.
