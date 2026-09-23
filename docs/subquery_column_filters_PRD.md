# Per-column filters on sub-queries — PRD

Follow-up to [#431](https://github.com/MintPlayer/MintPlayer.Spark/issues/431) (shipped in PR #440,
squashed to master as `30dff691`). Found on production the same day.

## 1. What is broken

Ticking a value in a column-filter panel on a **sub-query** — a query rendered on a
PersistentObject detail page — sends the filter to the server, which returns every row.

Reproduced on production at
<https://coverage.mintplayer.com/po/account/Accounts%2Fgithub%2F48772716>:

```json
POST /spark/queries/execute
{"queryId":"7a4c9b30-…","sortColumns":[{"property":"Name","direction":"asc"}],
 "parentId":"Accounts/github/48772716","parentType":"Account","skip":0,"take":10,
 "columns":[{"name":"Name","includes":["Dagucar","mintplayer-ng-bootstrap"]}]}

→ 200, totalItems: 15   (unfiltered; 2 expected)
```

Measured across all three sub-queries on that page:

| sub-query | rows | column filter | free-text search |
|---|---|---|---|
| Repositories | 15 | **inert** — a nonsense filter still returns 15 | **HTTP 500** |
| Upload tokens | 2 | **inert** — a nonsense filter still returns 2 | works (narrows to 0) |
| Project boards | 1 | inconclusive (single row) | works |

A nonsense filter returning the full set is the sharp form of the finding: the filter is not
merely mis-applied, it is not applied at all.

## 2. Root cause

`QueryExecutor.ExecuteQueryAsync` (`QueryExecutor.cs:208`) has exactly two branches, chosen purely
by `query.Source`:

```csharp
229: source = await ExecuteCustomQueryAsync(query, name, parent, searchTerm, skip, take, search, restrictToIds, cancellationToken);
242: source = await ExecuteDatabaseQueryAsync(query, name, parent, searchTerm, restrictToIds, columnFilters, skip, take, cancellationToken);
```

`ExecuteCustomQueryAsync` (`QueryExecutor.cs:1021-1024`) **has no `columnFilters` parameter**.
`ApplyColumnFilters` (`:1918`) has exactly one call site, `:893`, inside the database branch.

There is no early return, no null check and no refusal to find. The argument does not exist.

### Why this maps exactly onto "sub-queries"

A sub-query is scoped by its parent, and a `Database.<property>` source just reads a SparkContext
property — it has no way to consume the parent. The framework does not merely discourage this, it
**refuses** it: `QueryExecutor.cs:756-767` throws for any declared sub-query with a `Database.*`
source.

So **sub-queries ⊂ custom queries, strictly**, and the containment is enforced. Every sub-query in
all four apps is `Custom.*`:

| App | alias | source |
|---|---|---|
| CodeCoverage | account-repositories | `Custom.Account_Repositories` |
| CodeCoverage | account-projects | `Custom.Account_Projects` |
| CodeCoverage | account-upload-tokens | `Custom.Account_UploadTokens` |
| CodeCoverage | repository-commits | `Custom.Repository_Commits` |
| CodeCoverage | commit-builds | `Custom.Commit_Builds` |
| CodeCoverage | my-accounts | `Custom.MyAccounts` |
| DemoApp | company-cars, company-people | `Custom.*` |
| HR | company-people | `Custom.Company_People` |

**#431 therefore works on no sub-query in any app.** The feature shipped covering exactly the
complement of where it was asked for.

### Why it was not caught

The two test suites are disjoint, and neither covers the intersection:

- `ColumnFilterDisclosureTests.cs` — filters, `Source = "Database.Crates"`, no parent.
- `DistinctValuesDisclosureTests.cs` — distincts, `Source = "Database.Vaults"`, no parent.
- `ExecuteQueryParentGateTests.cs` — sub-queries, `Source = "Custom.ChildrenOf"`, **no filters**.

Filters were only ever exercised over `Database.*`; sub-queries only ever over `Custom.*` without
filters. Nothing tested the one combination that ships.

Browser verification during #440 did not catch it either, because it was done against Fleet — and
**Fleet declares no sub-queries at all**. CodeCoverage, conversely, has no top-level query: both of
its program units are PersistentObjects, so every grid in production is a sub-query. The app used
to verify and the app in production had disjoint query shapes.

### The contrast that points at the fix

Search was given three things on the custom branch; column filters were given none of them:

| refinement | database branch | custom branch |
|---|---|---|
| row-security pushdown | `:882` | `:1185` |
| search pushdown | `:901` | `:1203` (guarded by `isRavenQueryable`) |
| in-memory search fallback | shared, `:296-307` | shared, `:296-307` |
| sorting | `:919` | `:1209` |
| `restrictToIds` | `:907` | `:1192` |
| **column filters** | **`:893`** | **absent** |

The custom branch is not structurally unable to filter. `Account_Repositories` returns
`IRavenQueryable<Repository>` (`RepositoryActions.cs:198-203`), so a database-side `Where` composes
there exactly as it does on the database branch. Nothing wired it.

## 3. Secondary defects found while investigating

All in scope for this PR (one-PR rule).

### 3.1 Distinct lists are unfiltered on custom queries

`LoadSecuredRowsAsync` (`:193`) passes `columnFilters` to the database branch (`:201`) and literal
`null`s to the custom branch (`:200`). So on a sub-query, the distinct list for column B ignores an
active filter on column A.

This is why the panel populated with all 15 repository names during the repro. It is a correctness
bug in its own right: after `execute` is fixed, cross-column filtering would still offer values
that select zero rows.

### 3.2 Free-text search 500s on an indexed custom query

`ResolveSearchableProperties` (`:1813-1822`) selects **every** `string` property of the CLR type,
with no regard for whether the query's index indexes or analyzes it:

```csharp
.Where(static p => p.PropertyType == typeof(string) && p.CanRead
    && p.GetIndexParameters().Length == 0
    && !string.Equals(p.Name, "Id", StringComparison.Ordinal)
    && !p.IsIgnoredForSparkModel())
```

`Account_Repositories` queries through `Indexes.Repositories_Overview`, and `Repository` declares no
`[Search]` fields — so `ApplySearch` emits a `search()` clause per string property against a static
index that does not analyze them. `Account_UploadTokens` uses no index and survives.

**Predates #431** and is a live production 500. The exact exception has not been captured; that is
SP4.

### 3.3 Streaming queries silently ignore column filters

`spark-query-grid.component.ts:433-438` — `onFilterChanged()` returns early when
`hasExternalData()`, which is true for a streaming query. Ticking a filter on a streaming grid does
nothing and reports nothing. Client-side, unrelated to the server bug, same feature.

### 3.4 The author-page short-circuit bypasses filtering entirely

`QueryExecutor.cs:252-285` — when a custom query returns a `SparkQueryPage`, the executor returns
the author's rows and count immediately, before any framework narrowing. An author-paged query can
therefore never be filtered, on either branch. Needs a decision, not just a fix (§4, D5).

## 3.5 Audit: does filtering compose on top of row security?

Audited adversarially against `GetRowFilter` / `IRowSecurity`. **Verdict: structurally sound. No
bypass of `ComposeRowFilterAsync`, `FilterAsync`, `IsAllowedAsync` or `RedactAsync` exists.**

Composition order in `ExecuteDatabaseQueryAsync` is linear, with no branch around any step:

```
882-883  rowSecurity.ComposeRowFilterAsync(...) → queryable = rowFilter.Queryable
893      ApplyColumnFilters(queryable, sortType, columnFilters, ...)
903      ApplySearch      914  RestrictToIds      921  ApplySorting
939-942  mayPageInDatabase     1005-1008  CountQueryableAsync + ApplyPaging
1010     materialize           972/~1013  gate.ApplyAsync  ← UNCONDITIONAL
```

**The row rule is enforced even when it does not push down.** Three of the five `RowFilterMode`s
(`ConstantPredicate`, `ProjectionFallback`, and any mode with `HasPerRowRefinement`) enforce in
memory — and `ProjectionFallback` "is the DEFAULT shape for an indexed query, not a corner case".
`gate.ApplyAsync` is called with no reference to `rowFilter.Mode`, and `FilterAsync` re-evaluates
the rule per row against the stored document, reloading base documents when
`resultType != entityType`.

**A column filter cannot widen.** `ApplyColumnFilters` only chains `Queryable.Where` (`:1963`), which
is monotone-decreasing: the set reaching the gate is a subset of what it would have been, and the
gate filters that subset again. Surfacing a row the rule removes is structurally impossible.

**No RQL interference.** The filter path constructs only `Equal`, `OrElse` (inside a single lambda),
`Not`, `AndAlso` and `Lambda`. It never constructs `LinqExtensions.Search` or `SearchOptions` — the
`OrElse` never escapes the lambda body, so it becomes `(A = $p0 or A = $p1)` *within* one conjunct.

**`TotalItems` is not a cardinality oracle.** A database count runs only under `mayPageInDatabase`,
which requires `CanPageInDatabase` — true only when nothing removes rows after the database answers.
Otherwise the count is `allResults.Count` over `SecuredRows`, whose private constructor and
`Narrow`-only API make counting an ungated set impossible. An author-supplied `SparkQueryPage` total
is refused outright on a row-ruled type (`:262-274`).

**Distincts are the strongest part.** In-memory over gated *and redacted* rows, never a facet —
because a facet "refuses to compose into a projection query, which is the default shape for an
indexed query". Surface and capability are checked before any value is read, both refusing to
`DistinctValuesResult.Empty`, indistinguishable from "no values".

### 3.6 ⚠️ Real hole: a column filter is a redaction oracle

`RedactAttribute` nulls a protected attribute's value in the response. `ApplyColumnFilters` compares
against the **raw stored value in the database**. So a caller filters `Salary Includes [100000]`,
the row passes the row rule and is returned with `Salary: null` — and its presence, plus
`TotalItems`, confirms `Salary == 100000`.

A direct equality oracle on a value the caller may never read. **`canFilter` defaults to `true`**
(`ColumnCapabilities.cs:46`), so an app using `GetProtectedAttributesAsync` gets this by default.

This is the sharper sibling of the `?sortColumns=` oracle closed by #294-#296, and the codebase
already takes a reasoned position on it — but only on the *sort* path (`:1702-1707`): gate on
`ShowedOn` rather than on the redaction hook, because the hook takes an entity and may answer
per-row, so it cannot decide a query-level operation. That reasoning transfers to filtering
verbatim. `ApplyColumnFilters` carries no equivalent comment and nothing tells an app author that
`canFilter: false` is mandatory on any attribute their redaction hook may protect. The mitigation is
static; the hazard is dynamic. That asymmetry is the residual risk and it is currently undocumented.

Not a row-security bypass — no forbidden row is returned — but a genuine attribute-level disclosure
that #431 newly enabled.

### 3.7 The test named for this guarantee does not test it

`ColumnFilterDisclosureTests.cs:190-215`, `The_filter_clause_does_not_displace_the_row_security_predicate`,
runs against a fixture with **no actions class and no row rule** — verified: the file contains zero
references to row security. There is no security predicate in the emitted RQL, so the assertion is
really about the *search* group. **No test in the repo exercises a real row rule together with a
column filter on the grid path.** The behaviour is correct by construction, but unpinned.

### 3.8 Further defects found during the audits

- **A filter on a column absent from the projection is silently skipped** (`:1936-1944`) — warning,
  rows unnarrowed, unfiltered total. On a projecting index the client shows a filter chip over
  unfiltered rows. Same invisible-failure shape as the headline bug.
- **A malformed value on a non-nullable value-type column 500s.** `ConvertFilterValue` returns `null`
  on any conversion failure by design (`:2019-2022`, "should narrow to nothing rather than 500"),
  but computes `target` as the *underlying* type while `AnyEquals` passes the **declared**
  `propertyType` to `Expression.Constant` (`:1975`). `Expression.Constant(null, typeof(int))` throws.
  Verified by reading; reachable by any caller, since the endpoint validates column names, not
  values. It also makes the documented "refusal is indistinguishable from a value matching no row"
  property false for those columns — a 500 vs a 200-with-zero-rows is a type oracle.
- **The silent skips log to `Console.WriteLine`, not the injected `ILogger`**, so they reach no log
  sink in production.
- **`IRavenQueryable<T>` is required only in an error message, never enforced** (`:794-796` checks
  merely that the type has a generic argument). A `SparkContext` property typed `IQueryable<T>` over
  a list would today get C#-side filtering and fail only later, at `ExecuteQueryableAsync`.
- **`mayPageInDatabase` condition 4 is wrong for a non-projecting fan-out index.** An index without a
  `ProjectionType` leaves `resultType == entityType`, so the condition passes, but a fan-out index
  still emits several entries per document: `CountQueryableAsync` counts entries while the gate's
  `DistinctBy(po => po.Id)` collapses them. Inflated `TotalItems`, short pages, drifting offsets.
  Not a disclosure — the duplicates are rows the caller may already see — but the invariant stated
  at `:934-938` is false.

## 4. Design decisions

**D0 — Column filtering reaches the database or it does not happen.** No C#-side narrowing is added
on any path. Today this holds by accident rather than by design: filters are pushed into RQL on
`Database.*` and *dropped entirely* on every `Custom.*` shape, so there is no in-memory column
filtering anywhere — and the fix must not introduce any. See D2.

Measured position today:

| shape | column filter |
|---|---|
| `Database.*` plain collection | pushed into RQL (`:891-893`) |
| `Database.*` via index, no projection | pushed into RQL |
| `Database.*` + projection | pushed, against the projection type; silently skipped if absent from it |
| `Custom.*` → `IRavenQueryable<T>` | **dropped** |
| `Custom.*` → non-Raven `IQueryable<T>` | **dropped** |
| `Custom.*` → `IEnumerable`/`List` | **dropped** |
| `Custom.*` → `SparkQueryPage` | **dropped**; author cannot see the filter either |
| streaming | no filter concept at all |


**D1 — Thread `columnFilters` into `ExecuteCustomQueryAsync`, at the same position as the database
branch.** After `ComposeRowFilterAsync`, before `ApplySearch`. Position is load-bearing on the
database branch for a documented security reason (`:1900-1917`): a filter clause must not end up
adjacent to the row-security predicate where a `SearchOptions` could leak onto it. The custom branch
composes the same clauses against the same provider and inherits the same requirement.

**D2 — No in-memory column filtering. A shape that cannot push down REFUSES the filter.**
*(Revised — the original D2 proposed an in-memory fallback mirroring search's `SecuredRows.Narrow`.
Rejected: filtering must reach the database, per §4.1.)*

`ApplyColumnFilters` must be applied **only when the queryable is Raven-backed**, branching on the
`isRavenQueryable` flag `ExecuteCustomQueryAsync` already computes at `:1158` for the search path.
A `Custom.*` query returning a non-Raven `IQueryable`, an `IEnumerable<T>` or a `List<T>` refuses
the filter rather than narrowing it in process.

This is not a nicety. `Queryable.Where` dispatches on `queryable.Provider`, and `ApplyColumnFilters`
inspects the provider nowhere. Handed an `IQueryable` backed by `EnumerableQuery`, it composes
without error and enumeration then compiles the lambda and filters **in process — no exception, no
warning, no log line**. Wiring the filter onto the custom branch without this guard would silently
manufacture exactly the C#-side filtering this decision forbids, and nothing would report it.

Precedent for refusing rather than degrading: `:756-767` throws outright for a `Database.*`
sub-query rather than quietly serving it unscoped.

**D3 — Fix `LoadSecuredRowsAsync` to pass `columnFilters` on both branches.** Distincts and execute
must agree about what the filtered set is, or the panel offers dead values.

**D4 — `TotalItems` is counted after narrowing, on every path.** The database branch already gets
this right by composing before `CountQueryableAsync`. The custom branch must not report the
pre-filter count, or the pager claims 15 while showing 2.

**D5 — An author-paged (`SparkQueryPage`) query rejects column filters with a 400.** The author owns
paging and counting there, and the framework cannot narrow a page it did not compute without making
`TotalItems` a lie. A silent no-op is what this whole PRD is about, so the refusal is explicit.
This is a deliberate reversal of the #431 convention that a refused filter is silent — see D6.

**D6 — Keep the silent refusal for an unauthorized column; make "nothing applied it" loud.** #431
made `ApplyColumnFilters` warn to the console and return rows unnarrowed
(`:1924-1943`), because a distinguishable refusal is an enumeration oracle — a caller could probe
which columns exist by watching which filters bite. That reasoning is sound and stays.

It is the wrong behaviour for "this code path never called me", which is not a disclosure decision
at all and is precisely why this bug was invisible for a full release. The two cases must be
distinguishable **in tests**, even though they stay indistinguishable **to a caller**. Hence D7.

**D7 — Do not expose filters on `CustomQueryArgs`.** Authors should not have to apply framework
filtering by hand; an author who forgot would reproduce this bug per-query instead of once. The
framework applies filters on top of whatever the author returns, exactly as it already does for row
security, search and sorting.

## 5. Acceptance criteria

1. On the production repro, `columns: [{name:"Name", includes:["Dagucar","mintplayer-ng-bootstrap"]}]`
   returns `totalItems: 2` and those two rows.
2. A nonsense filter value on any sub-query returns `totalItems: 0`.
3. A filter on a custom query returning a non-Raven `IQueryable` or a materialized collection is
   **refused, not narrowed in C#** — and a test proves no in-process filtering occurred (assert on
   emitted RQL or on the provider, not on the row count, which looks identical either way).
4. A distinct list on a sub-query reflects other columns' active filters.
5. `TotalItems` equals the filtered count on every path, and paging is over the filtered set.
6. RQL on the custom branch places filter clauses where the database branch places them — asserted,
   not assumed (the row-security adjacency requirement).
7. A column with `canFilter: false` is still refused silently, with no change in observable
   behaviour to a caller.
8. An author-paged query with column filters returns 400.
9. Free-text search on the Repositories sub-query returns 200.
10. A filter on a streaming query either applies or reports that it cannot — it does not silently
    no-op.
11. A test exercises a **real row rule together with a column filter** on the grid path, asserting
    both the RQL clause position against an actual security predicate and that a `DenyAllRowSecurity`
    run returns zero rows under an active filter (§3.7).
12. A filter on a column the projection does not carry is reported, not silently dropped (§3.8).
13. A malformed filter value on a non-nullable value-type column returns rows-that-match-nothing,
    not a 500 (§3.8).
14. The redaction oracle (§3.6) is either closed or explicitly documented as an app-author
    responsibility, with the `canFilter: false` obligation stated where an author will see it.

## 6. Non-goals

- Changing where the capability flags live (settled in #431: per-attribute with a sparse per-query
  override).
- Facets. Still excluded for the #431 reason: a facet leaks on a projection query, which is the
  default indexed shape.
- Making `Database.*` sources parent-scopable. The refusal at `:756-767` is correct and stays.
- The open items F1–F8 in `docs/query_column_filter_plan.md`. They remain open there; F1 (free-text
  search matching off-surface columns) still needs a decision from the user and is not resolved by
  this work.

## 7. Risk

The change composes a `Where` into queryables authored by application code, which the framework did
not previously touch on this branch. Two things could go wrong and both are testable: a custom query
whose returned element type differs from the model's entity type (projection) would fail property
resolution — already handled by the `:1935-1943` skip-with-warning — and a custom query that has
already materialized would need the in-memory path rather than the queryable one (D2).
