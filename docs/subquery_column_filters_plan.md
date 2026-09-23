# Per-column filters on sub-queries — implementation plan

Companion to [`subquery_column_filters_PRD.md`](subquery_column_filters_PRD.md).
Branch: `fix/subquery-column-filters`. One PR, everything in it.

## Status

| | |
|---|---|
| Root cause | **Confirmed** — `ExecuteCustomQueryAsync` has no `columnFilters` parameter (`QueryExecutor.cs:1021-1024`) |
| Reproduced | **Yes**, on production, three sub-queries measured |
| Client | **Cleared** — audited, sends `columns` + `parentId` + `parentType` correctly, no client-side narrowing |
| Row security | **Audited, sound** — filters compose after the row filter, gate is unconditional, `Where` is monotone, no widening possible (PRD §3.5). One real hole: the redaction oracle (§3.6) |
| Pushdown | **Audited** — RQL on `Database.*`, dropped on every `Custom.*`. No C#-side column filtering exists today, and none may be added (D0/D2) |
| Spikes run | SP1 ✅ safe · SP2 ✅ settled (then re-settled, see D2) · SP3 ✅ no work needed · SP5 ✅ answered · SP4 in progress |
| Milestones done | M1 ✅ · M2 ✅ · M3 ✅ · M4 ✅ (free) · M5 ✅ · M8 ✅ · M9b ✅ · M9d partial |

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

### SP2 — How does a refusal reach the caller? *(replaces the in-memory-fallback spike)*

D2 settled that a non-pushdownable shape **refuses** rather than filtering in C#. This spike is about
the refusal's shape, which is a disclosure decision, not a plumbing one.

`ApplyColumnFilters` today refuses silently for an unauthorized column, deliberately: a
distinguishable refusal is an enumeration oracle. But "this shape cannot push down" is a property of
the *author's code*, not of the caller's permissions, and leaks nothing about data.

Answer: does the shape refusal 400 (like D5's author-paged case), or warn-and-return-unfiltered
(like the capability refusal)? They must not share a code path, or the security-motivated silence
will keep hiding plumbing bugs — which is the whole reason this PR exists.

Kill criterion: if the two cannot be separated cleanly, the shape refusal must be the loud one, and
the capability refusal keeps the silence.

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

### M3 — Refuse non-pushdownable shapes *(gated by SP2)*
Branch on the `isRavenQueryable` flag already computed at `:1158`. A non-Raven `IQueryable`, an
`IEnumerable<T>` or a `List<T>` refuses the filter — **no `SecuredRows.Narrow`, no in-process
filtering**. The trap this closes: `Queryable.Where` over an `EnumerableQuery` provider composes and
executes in C# with no error and no warning, so "it returned the right rows" is not evidence of
pushdown. Assert on emitted RQL, not on row counts.

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

### M9c — The redaction oracle (PRD §3.6)
`canFilter` defaults to `true`, so an app using `GetProtectedAttributesAsync` gets an equality oracle
on protected values by default. Either gate filtering the way sorting is gated (`ShowedOn`, static,
per `:1702-1707`) or document the `canFilter: false` obligation where an app author will actually
see it — `docs/guide-authorization.md` and the `ColumnCapabilities` XML docs. Decide which; do not
leave it as it is.

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

### M12 — Verify on production repro
Re-run the measured table in PRD §1 and confirm all ten acceptance criteria.

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
