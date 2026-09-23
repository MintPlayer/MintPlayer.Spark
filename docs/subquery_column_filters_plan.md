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

### M7 — Fix the search 500 *(gated by SP4)*

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

## Carried over from #431 (not resolved here)

`docs/query_column_filter_plan.md` still lists F1–F8. **F1** — free-text search matching
off-surface columns — remains open and needs the user's decision. F7 (filterActive freeze) was never
proven either way. These are not in scope for this PR unless the user says otherwise.
