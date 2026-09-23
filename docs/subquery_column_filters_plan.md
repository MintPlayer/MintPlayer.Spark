# Per-column filters on sub-queries — implementation plan

Companion to [`subquery_column_filters_PRD.md`](subquery_column_filters_PRD.md).
Branch: `fix/subquery-column-filters`. One PR, everything in it.

## Status

| | |
|---|---|
| Root cause | **Confirmed** — `ExecuteCustomQueryAsync` has no `columnFilters` parameter (`QueryExecutor.cs:1021-1024`) |
| Reproduced | **Yes**, on production, three sub-queries measured |
| Client | **Cleared** — audited, sends `columns` + `parentId` + `parentType` correctly, no client-side narrowing |
| Spikes run | none yet |
| Milestones done | none yet |

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

### SP2 — What does the in-memory path filter on? (gates M3)

The search fallback (`:296-307`) narrows over *materialized POs*, matching `po.Name`,
`po.Breadcrumb` and each `attr.Breadcrumb ?? attr.Value?.ToString()`. Column filters carry the
`{value, label}` wire shape from #431, where `value` is the raw value and `label` is the rendered
text.

Answer: when narrowing in memory, do we compare against the raw attribute `Value` or the
`Breadcrumb`? They differ for references and custom renderers, and the queryable path compares the
raw property. **The two paths must agree** or the same filter narrows differently depending on
whether the author returned a queryable — which would be a worse bug than the one being fixed.

Kill criterion: if they cannot be made to agree, the in-memory path must refuse rather than
narrow wrongly.

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

### SP5 — Streaming queries and filters

`onFilterChanged()` returns early on `hasExternalData()`. Decide: narrow the streaming snapshot
client-side (it already narrows by search and sort at `spark-query-list.component.ts:288-310`), or
hide the filter row for streaming queries. Silently accepting a click that does nothing is not an
option.

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

### M3 — In-memory fallback for non-queryable custom results *(gated by SP2)*
Narrow via `SecuredRows.Narrow`, mirroring the search fallback. Applies when the author returned a
materialized collection.

### M4 — Filtered `TotalItems` and paging *(gated by SP3)*
Count after narrowing on every path; page over the filtered set.

### M5 — Distincts on the custom branch
`LoadSecuredRowsAsync:200` — pass `columnFilters` instead of `null`. Makes the panel's values agree
with what `execute` will return.

### M6 — Refuse filters on an author-paged query (D5)
400 when a `SparkQueryPage` short-circuit (`:252-285`) meets a non-empty `columnFilters`. Explicit,
not silent.

### M7 — Fix the search 500 *(gated by SP4)*

### M8 — Streaming queries *(gated by SP5)*

### M9 — Test the paths, not just the case
- Column filters over `Custom.*`: includes, excludes, multi-column AND, queryable and materialized.
- Distincts over `Custom.*` with another column filtered.
- RQL position assertion on the custom branch (mirrors the database-branch assertion).
- `canFilter: false` on a custom query — still silently refused, behaviour unchanged.
- A `DenyAllRowSecurity` case on the filtered custom path: zero rows reach the wire.

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
