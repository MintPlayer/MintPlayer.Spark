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

## 3.9 Making the index and the model agree (investigated; design settled)

The SP4 500 and the `IndexSearchFields()` trap are the same defect from opposite directions: **what
Spark believes about an index and what the index actually declares have drifted apart.**

- SP4 — a field that is *not in the index* gets a `search()` clause anyway → RavenDB rejects the
  whole query, loudly, in production.
- `IndexSearchFields()` — a field that *should* be analyzed never gets configured → search silently
  returns nothing. Generated `private` (`HandWrittenProducer.cs:158`) with nothing verifying the
  constructor calls it, and an unused private *method* raises no warning. Currently a latent trap
  rather than a live bug: every index with `[Search]` fields either calls it or is fully generated.

A proposal was investigated to close the class: a `SparkIndexCreationTask<T>` base with generated
`Index(...)` calls, and `synchronize` writing the capability flags to match. Findings below.

### 3.9.1 ⛔ Synchronize must NOT write the flags — a structural blocker, not a cost

**The capability is per-query, not per-attribute, and the repo already has a counter-example.**
`apps/DemoApp/DemoApp/App_Data/Model/Car.json` declares two queries over one attribute set bound to
two different indexes — `Company_Cars` (`:265`) and `Cars_Overview` (`:285`) — which differ at field
level: `VCar` carries `[Search]` and generated `{Name}Sort` companions, `VCompanyCar` carries
neither. One `canSort` field in `attributes[]` cannot be correct for both.

Three further blockers, any one of which would be sufficient:

- **Synchronize cannot see field-level configuration.** `IndexCatalogEntry` holds four names and two
  `Type` handles — no field data. Field indexing is set in the index *constructor body*, which is IL
  at synchronize time. Reading it means instantiating index types offline, which breaks the
  "pure reflection — no database, no host, no DI" contract (`SparkMiddleware.cs:476`) that lets
  `--spark-verify-model` run in a CI merge queue with no RavenDB.
- **No provenance signal.** The repo has twice made re-derivation safe by keeping a way to tell a
  machine-written value from an authored one — `#279` uses stored == derived, `#275` uses "did it
  carry `[Reference]`?". Capability flags have no analogue, so a machine `true` and an author's
  `true` are byte-identical. `ModelSynchronizer.cs:830-837` already records this decision and it is
  still correct.
- **It reverses a deliberate #431 choice.** The flags are `bool?` specifically so absent = no byte
  change; writing explicit values adds ~880 lines across 41 model files, moves every file hash and
  every `modelHashes.json`, and forces a lockstep `App_Data` redeploy for production CodeCoverage.

If a JSON-writing variant is ever revisited, the only defensible policy is `#274`'s
**intersect-never-widen** (`derived && authored`): a derived `false` may close a column, an authored
`false` is never re-opened. The restricting direction is the only one that matters anyway — the sole
reason to author one of these flags is to restrict.

### 3.9.2 ✅ Derive at runtime instead — the pattern is already in this PR

`ColumnCapabilities.CanFilter` already ANDs a runtime, query-shape-derived term with the model's
answer, added earlier in this PR for streaming. An index-derived term is the same idea with a
different predicate:

```csharp
=> query is not { IsStreamingQuery: true }
&& (FindOverride(query, attribute.Name)?.CanFilter ?? attribute.CanFilter ?? true);
```

This is correct per query (the executor already resolves `sortType` per query), changes no bytes in
any model file, never touches an author's field, and needs no offline index instantiation — the
projection `Type` is already in hand. The enforcement sites already skip a column absent from the
projection, with a warning; what is missing is that the **column metadata sent to the client does
not reflect it**, so the grid draws a sort arrow and a filter cell the server then silently ignores.
That is the actual defect behind the proposal.

### 3.9.0 Measured ground truth (RavenDB 7.2.6, Corax, against a real server)

**The Map projection is the gate. `Index(...)` and `Store(...)` are not.**

| operation | emitted by Map, no `Index(...)` | emitted by Map, `FieldIndexing.No` | **not in the Map** |
|---|---|---|---|
| `where f == v` | ✅ correct rows | ⚠️ **0 rows, no error** | ❌ `ArgumentException` |
| `order by f` | ✅ correct | ⚠️ **silent no-op** (ASC == DESC) | ❌ same |
| `search(f, …)` | ✅ works | ❌ throws (no analyzer) | ❌ same |
| projection | ✅ (needs `StoreAllFields` for index-only computed fields) | ✅ value intact | ❌ |

Consequences that change the design:

- **A field emitted by the Map is queryable and sortable with no `Index(...)` call at all.** `Index(...)`
  only ever *changes* a default (`Search` analyzes, `Exact` is case-sensitive, `No` drops from the
  index); it never enables. So **generating `Index(...)` calls would not have prevented the SP4 500** —
  that error depends solely on whether the Map emits the field.
- **`FieldIndexing.No` degrades silently rather than erroring**: a filter returns zero rows and a sort
  does nothing, with a 200 and no diagnostic. Worse than the loud failure, and **no test pins it**.
- **`IndexDefinition.Fields` is an override table, not an inventory** — measured: an index with two
  fully queryable fields and nothing declared has `Fields.Count == 0`. "Absent" means *capable*, so
  capabilities cannot be enumerated from it.
- **The emitted field set exists only as a rendered C# string** in `IndexDefinition.Maps`. The only
  structured surrogate is the `[FromIndex]` projection type's property list — and **6 of the 10
  production hand-written indexes have no `[FromIndex]` projection at all** (`Commits_ByRepository`
  plus all five `libs/` indexes). Counted: the four that do are all in DemoApp.
- **Capability is per-(index, field), not per-attribute** — proven in-repo: `Car.LicensePlate` is
  analyzed and needs a `{Name}Sort` companion under `Cars_Overview`, and is a plain default field
  under `Company_Cars`. Same model attribute, opposite capabilities. This is the same conclusion
  §3.9.1 reaches from the model side, arrived at independently.
- **A dynamic query has no index to derive from and is always capable** — RavenDB builds the
  auto-index from the query shape, creating `Auto/X/ByNote` and `Auto/X/BySearch(Note)` separately.
  Any derivation must be conditional on a bound index or it will falsely disable columns on every
  collection query.

### 3.9.3 The base class is worth building — for the call site, and NOT for SP4

The generator **already emits** the `Index(nameof(V*.X), FieldIndexing.Y)` body
(`HandWrittenProducer.cs:148-168`). What a generator cannot do is add a statement to a hand-written
constructor — stated at `:143-145` — which is exactly why the author must remember the call.
`CreateIndexDefinition()` is `public virtual`, so a base class can supply the call site:

```csharp
public abstract class SparkIndexCreationTask<T> : AbstractIndexCreationTask<T>
{
    public override IndexDefinition CreateIndexDefinition()
    {
        ConfigureSparkFields();          // Map is assigned by now: the derived ctor has run
        return base.CreateIndexDefinition();
    }
    protected virtual void ConfigureSparkFields() { }
}
```

with the generator emitting `protected override void ConfigureSparkFields()` instead of a private
method. Same body, two keywords, and "never forgotten" becomes structural.

**What this does and does not buy, now that §3.9.0 is measured.** It fixes the *silent* half — a
`[Search]` field that never gets analyzed, so search quietly returns nothing. It does **nothing** for
SP4, because that failure is about a field the Map never emitted, and no `Index(...)` call can add a
field to a Map. Those are separate defects that happen to share a cause ("the index and Spark's
belief about it disagree"), and only one of them is fixed by configuring fields.

**Constraints that shape it, all measured:**

- **`Index(x => x.Field, …)` is type-wrong.** `AbstractIndexCreationTask<T>` is
  `AbstractIndexCreationTask<T,T>`, so the lambda binds against the *entity*, never the projection —
  it cannot name a computed field, a `{Name}Sort` companion or a `{Name}Raw` wrapper. The `nameof`
  string overload is required, which is what the generator already uses. Declaring
  `AbstractIndexCreationTask<Car, VCar>` fixes the typing and makes the index **invisible to Spark's
  catalog**, which matches arity-1 only.
- **Nothing can derive the emitted field set from the `Map`** — statically or at runtime.
  `LoadDocument`, `let`, coalesce, `CreateField` and anonymous projections defeat it, and the repo's
  own analyzer refuses to try and says why (`SortCompanionAnalyzer.cs:65-67`). The field list keeps
  coming from declared attributes on the projection type.
- **`StoreAllFields` must not be in the base class.** `CommitIndexShapeGuardTests` exists to fail the
  build if it reaches `Commits_ByRepository`, because it flattens `DateTimeOffset`s through
  projection silently.
- **It needs a new package.** `Abstractions` deliberately has no `RavenDB.Client`; adding one pushes
  RavenDB onto every entity library.
- **Ordering hazard.** Generated indexes call `OnInitialize()` last; a base-class
  `ConfigureSparkFields()` invoked from `CreateIndexDefinition()` runs *after* it, so a hand-written
  `Index(...)` would be silently overwritten (last-write-wins on a dictionary). Must skip a field
  already present in `IndexesStrings`.
- **Unmeasured:** whether a `Fields`-only `IndexDefinition` change forces a full reindex on RavenDB
  7.2.6. That decides the migration cost, and CodeCoverage's `Commits` index backs the commit list,
  history chart, sparklines and badges, which return partial results during a rebuild. Measure on a
  scratch database with production-shaped data before touching production.

**Scope call for this PR:** 3.9.2 (runtime derivation) is in scope — it is what fixes SP4 properly
and it is a small change to a pattern already added here. The base class and the analyzer are
separable and larger; they are recorded here and proposed as their own milestones rather than
smuggled in under a bug fix. The one-PR rule says everything that *needs fixing* lands together; a
new generated base class across `libs/` with an unmeasured reindex cost on production is new
capability, not the fix.

## 3.10 The JSON-driven generator route — investigated in full

Proposal: two base classes (`AbstractIndexCreationTask` / `AbstractMultiMapIndexCreationTask`), a
generator that reads the model JSON's capability flags and emits the matching `Index(...)` /
`StoreAllFields(...)` calls into one method, and that method called automatically from the base
classes. Generators partitioned by `[GenerateIndex]`: the existing one keeps everything it owns, the
new one covers only classes without it.

**The mechanism is sound and mostly already built. The proposed input is the wrong one.**

### 3.10.1 ✅ The risks I flagged are not real

- **No build-order cycle.** Proven three ways: the flags are hand-authored input and
  `ModelSynchronizer.cs:830-837` forbids writing them; the generator's output changes the Raven index
  definition but no property, name or type synchronize reads; and `ModelShapeDiscovery` hashes only
  the projection type's name and the index name, so **the index definition is not hashed at all**.
  A fresh clone bootstraps (all 41 model files are tracked) and `--spark-verify-model` cannot
  oscillate, because nothing is written and nothing is hashed.
- **No MSBuild work.** `spark.targets:157-159` already ships
  `<AdditionalFiles Include="$(SparkAppDataDir)\Model\*.json" />`, flowed transitively, with a
  wildcard so an absent folder is a safe no-op rather than a build break.
- **Same compilation.** Entities live in class libraries but indexes are generated **into the app**,
  which is where the JSON is.
- **Analyzers can see generated types** — `SortCompanionAnalyzer.cs:20-23` rests its whole
  correctness argument on it, and `DefaultIndexAnalyzer` is the one analyzer that opts in with
  `GeneratedCodeAnalysisFlags.Analyze`.

### 3.10.2 ✅ The relationship walk is ~80% already implemented

`GeneratedIndexInfo` already carries all four edges per entity — `EntityFullName` (collection),
`IndexName`, `IndexEntityName` (projection), `IsDefault` — because for a generated pair the generator
*is* the authority and derives them once through `IndexNaming` rather than reading them back. The
pool is already joined across source and referenced metadata (`allEntitiesProvider`,
`GenerateIndexGenerator.cs:110-117`) and already fanned out to three producers, so a fourth is an
established pattern rather than new plumbing.

The hand-written side already resolves `[FromIndex] → index symbol` (`:615-626`) and **holds the
`INamedTypeSymbol` two lines away from** both the collection type (`baseType.TypeArguments[0]`) and
`[DefaultIndex]` — neither is taken today. `HandWrittenIndexEntityInfo` lacks both fields; adding
them reaches parity, after which one shared shape over both records gives the whole 17-row graph
inside a single generator.

The two pools are currently joined only by a *subtraction* on projection class name (`:170-181`).

### 3.10.3 ⛔ But the capability flags cannot drive index configuration

Working every (capability × data type) pair against §3.9.0: **there is no case where `canSort: true`
/ `canFilter: true` / `canListDistincts: true` requires emitting anything the generator does not
already emit unconditionally, and no case where a `false` can be safely translated at all.**

- A Map-emitted field is already filterable and sortable with no `Index(...)` call.
- The `{Name}Sort` companion is already unconditional for every `[Search]` text field — *"analyzing
  the field is what destroys its sortability, so the two are one decision"*.
- `StoreAllFields` is already unconditional on the generated path — and **build-forbidden** on
  `Commits_ByRepository`.
- **`canListDistincts` touches the index in no way whatsoever.** Distincts are computed over
  materialized `PersistentObject` attributes, never an index term.

**And `canSort: false` → `FieldIndexing.No` would be actively dangerous.** Four independent reasons:
it degrades silently (0 rows / no-op / 200); it applies to every query through that index while the
flag is per-(attribute, query); `No` drops the field entirely so `search()` on it then throws — a
flag meaning "don't show a sort arrow" would start 500ing free-text search; and it defeats the
exemption at `QueryExecutor.cs:2219`, `if (query is { SortColumnsAreCallerSupplied: false }) return
true;` — a sort the query itself declares is deliberately exempt. Fleet's `Car.json:182` sets
`canSort: false` on `color` while that file declares `sortColumns` on four queries.

The decisive evidence is the repo's own authored flags — the only three that exist anywhere:
`canSort: false` on a `color` column with a `color-swatch` renderer; `canListDistincts: false` on a
high-cardinality `LicensePlate`; `canFilter: false` on a video URL with a `video-player` renderer.
**Not one describes something an index could be configured differently for.** They are presentation
and disclosure judgements. Capability flags ask *may this caller do this*; index configuration asks
*can the engine do this*. Two vocabularies sharing three verbs.

### 3.10.4 Corpus reach

**10 hand-written indexes; 4 have a `[FromIndex]` projection** (all DemoApp), 6 do not
(`Commits_ByRepository`, which is not even `partial`, plus all five `libs/` indexes, which have no
`App_Data` and never will — one maps an *anonymous type*, so there is no named symbol at all).
For those 6 a capability→config translation achieves nothing: no emission, no diagnostic, no runtime
derivation. They must fall back to "capable", as must every dynamic-index query.

### 3.10.5 What survives, and what it should be driven by

The generator's job here is **diagnostics, not emission** — a materially smaller feature:

- **Runtime AND term** (M7b) — the enforcement that actually fixes the affordance.
- **Analyzer rules** cross-checking a JSON claim against the index, feasible where a projection
  exists. Two unsatisfiable cases nothing detects today: `canSort`/`canFilter: true` on a
  **collection** attribute, and `canSort: true` on a **`TranslatedString`** — the generator replaces
  the field with `{Name}_{lang}` per language while `ResolveSortProperty` only ever tries
  `requested + "Sort"`, so the flag resolves to a property that does not exist.
- **The base class for the call site** (P1), which remains the one genuine structural improvement.

### 3.10.6 Defects found during this investigation

- **The multi-map / 2-arg base-type walk is wrong in three places.**
  `AbstractMultiMapIndexCreationTask<T>`'s argument is the **reduce result**, not a collection type,
  and `AbstractIndexCreationTask<TDocument, TReduceResult>` does **not** derive from
  `AbstractIndexCreationTask<TDocument>` — it derives from `AbstractGenericIndexCreationTask<TReduceResult>`.
  So a real 2-arg map-reduce index resolves to a null collection type and is never registered.
  Three near-duplicate copies of the walk exist with *different* acceptance rules
  (`IndexCatalog.cs:214`, `DefaultIndexAnalyzer.cs:140`, `ProjectionPropertyAnalyzer.cs:140`).
- **A test stub encodes the wrong hierarchy.** `DefaultIndexAnalyzerTests.cs:22-24` declares
  `AbstractIndexCreationTask<TDocument, TReduceResult> : AbstractIndexCreationTask<TDocument>`,
  which is not the real shape, so its map-reduce test proves nothing.
- **No test anywhere proves an analyzer reads generator output** — the harness attaches analyzers
  without running generators, and `DefaultIndexAnalyzerTests.cs:190-191` says so outright.
- **`PullRequestFeedback` has a generated index and projection but no model file at all.**
- **`Commit` has no `queryType`/`indexName`**, so a `Database.*` query over it reads the raw
  collection rather than `Commits_ByRepository`.
- **Filtering a collection column silently becomes `== null`** — `ConvertFilterValue` returns null on
  conversion failure and `AnyEquals` compares the collection member to it. 200, no warning.

## 4. Design decisions

**D0 — A filter is never silently dropped, and never silently relocated.** Where a database query
exists the filter belongs in it; where one does not, the filter still applies. What is forbidden is
the invisible outcome: a filter that vanishes (this bug), or a filter that quietly moves from the
database into C# while looking identical from the outside. See D2 for the shape rule and for the RQL
assertion that enforces the second half.

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

**D2 — The filter follows the shape: RQL where there is a database, in process where there is not.
It is never dropped.** *(Settled after two reversals; both are recorded because the reasoning matters
more than the conclusion.)*

- **Raven-backed queryable** → composed into RQL. Asserted on emitted RQL, never on row counts.
- **Non-Raven `IQueryable`** (`EnumerableQuery`) → the predicate runs in process.
- **`IEnumerable<T>` / `List<T>`**, including a set materialized from the database with
  `ToListAsync()` → lifted through `AsQueryable` and narrowed by the **same expression**.

A custom query that returns a fixed set — a computed dashboard, a constant list, an API response, or
a developer deciding the set is small enough to hand over whole — is a legitimate, supported shape.
Filtering it in memory is not a degraded pushdown; it is the only thing filtering a fixed set can
mean. Refusing it would punish an author who did nothing wrong.

**The first draft proposed an in-memory fallback modelled on search's `SecuredRows.Narrow`.** That
was rejected, then reinstated in this form. The distinction: the rejected version was a *second
implementation* that narrowed materialized `PersistentObject`s by their rendered attribute text,
while the queryable path compares the raw CLR property. Two implementations can disagree about what
a value equals, and then the same filter narrows differently depending on a shape the caller cannot
see. Lifting through `AsQueryable` keeps **one predicate and one comparison semantic for every
shape**, which is what makes the in-memory path safe rather than merely convenient.

**The guarantee that still needs enforcing** is the one the shape rule does not give for free: where
a database query *exists*, the filter must be in it. `Queryable.Where` dispatches on the provider and
`ApplyColumnFilters` inspects it nowhere, so a Raven queryable that had silently become an
`EnumerableQuery` would filter in process, return identical rows and report nothing. Row counts
cannot see that; only the emitted RQL can. Hence a test asserting every statement issued for a
Raven-backed query carries the filter — including the count, since a count that skipped it would
report more rows than the grid can show.

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
