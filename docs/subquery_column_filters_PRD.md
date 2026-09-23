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

## 4. Design decisions

**D1 — Thread `columnFilters` into `ExecuteCustomQueryAsync`, at the same position as the database
branch.** After `ComposeRowFilterAsync`, before `ApplySearch`. Position is load-bearing on the
database branch for a documented security reason (`:1900-1917`): a filter clause must not end up
adjacent to the row-security predicate where a `SearchOptions` could leak onto it. The custom branch
composes the same clauses against the same provider and inherits the same requirement.

**D2 — Add an in-memory fallback for custom results that are not `IQueryable`.** `ApplyColumnFilters`
reflects onto `Queryable.Where`, so it cannot touch a custom query returning `List<T>` or
`IEnumerable<T>`. Search already solves this exact problem with `SecuredRows.Narrow` (`:296-307`);
filters get the same treatment. Narrowing is the only post-gate transform that cannot break the
gate's invariant, which is why `Narrow` is an instance method on `SecuredRows`.

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
3. A filter on a custom query returning a plain `IEnumerable<T>` narrows correctly.
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
