# Making the C# index and Spark's belief about it agree — PRD

Successor to the investigation in [`subquery_column_filters_PRD.md`](subquery_column_filters_PRD.md)
§3.9–§3.10 (PR #441). That PR fixed the symptoms; this is the class.

**Status: not started.** Everything below is investigated and measured; nothing is implemented.

## 1. The problem

An index declares a field set and field options in C#. Spark forms a belief about what a query over
that index can do. When the two drift apart, the failure is **silent in one direction and a 500 in
the other**, and both have reached production:

| direction | symptom | status |
|---|---|---|
| A `[Search]` field never gets its `Index(...)` call | full-text search quietly returns nothing | **latent** — every `[Search]`-bearing index today either calls `IndexSearchFields()` or is generated |
| Spark names a field the index does not emit | RavenDB rejects the **whole query** → HTTP 500 | **fixed** in #441 by restricting the searchable set |
| A field is `FieldIndexing.No` | filter returns 0 rows, sort is a no-op, HTTP 200 | **unfixed**, pinned by `UnindexedFieldDegradationTests` |

The first is the one this PRD is really about, because it is the one with no guard at all.

**The mechanism.** The generator already emits the right body into the user's partial index class
(`GenerateIndexGenerator.HandWrittenProducer.cs:148-168`):

```csharp
private void IndexSearchFields()
{
    Index(nameof(VPerson.FullName), FieldIndexing.Search);
}
```

It is `private`, **and nothing verifies the constructor calls it**. An uncalled private *method*
raises no compiler warning. `docs/guide-queries-and-sorting.md:549` states "**You must call it**" in
bold — documentation standing in for a check. Declare the index `partial`, add `[Search]`, forget one
line, and the index deploys, reports healthy, returns correct row counts, and search does nothing.

## 2. ⚠️ Measured ground truth (RavenDB 7.2.6, Corax, real server)

**The Map projection is the gate. `Index(...)` and `Store(...)` are not.**

| operation | in Map, no `Index(...)` | in Map, `FieldIndexing.No` | not in Map |
|---|---|---|---|
| `where f == v` | ✅ correct | ⚠️ **0 rows, no error** | ❌ `ArgumentException` |
| `order by f` | ✅ correct | ⚠️ **silent no-op** (asc == desc) | ❌ same |
| `search(f, …)` | ✅ | ❌ throws (no analyzer) | ❌ same |
| projection | ✅ (`StoreAllFields` needed for index-only computed fields) | ✅ value intact | ❌ |

Consequences that constrain every design below:

- **A Map-emitted field is already queryable and sortable with no `Index(...)` call.** `Index(...)`
  only ever *changes* a default: `Search` analyzes, `Exact` is case-sensitive, `No` removes.
- **`FieldIndexing.Search` destroys equality and ordering** (tokenization) — which is why the
  `{Name}Sort` companion exists and why both the sort and filter paths resolve through it.
- **`IndexDefinition.Fields` is an override table, not an inventory.** An index with two fully
  queryable fields and nothing declared has `Fields.Count == 0`. Absent means *capable*.
- **The emitted field set exists only as a rendered C# string** in `IndexDefinition.Maps`. Nothing —
  generator, analyzer or runtime — can read it reliably: `LoadDocument`, `let`, ternaries, coalesce,
  `CreateField` and anonymous projections all defeat it. The repo's own analyzer refuses to try and
  says why (`SortCompanionAnalyzer.cs:65-67`).
- **`IRavenQueryInspector.IndexName`** returns the deployed name for a static index and **null for a
  dynamic one** — an exact, non-heuristic discriminator (used by #441's search fix).

## 3. The corpus

**10 hand-written indexes; 4 have a `[FromIndex]` projection, 6 do not.**

| index | projection | partial? |
|---|---|---|
| `DemoApp/Cars_Overview` | `VCar` | yes |
| `DemoApp/Companies_Overview` | `VCompany` | yes |
| `DemoApp/Company_Cars` | `VCompanyCar` | yes |
| `DemoApp/People_Overview` | `VPerson` | yes |
| `CodeCoverage/Commits_ByRepository` | **none** (nested `Result`, not `[FromIndex]`) | **no** |
| `libs/messaging/SparkMessages_ByQueue` | **none** (anonymous type) | **no** |
| `libs/replication/SparkSyncActions_ByStatus` | **none** | **no** |
| `libs/identity_provider/Oidc*` × 3 | **none** | **no** |

Plus **7 fully generated** via `[GenerateIndex]` (CodeCoverage ×5, Fleet `Car`, HR `Person`).

The `libs/` indexes have **no `App_Data/Model` at all** and never will — model sync is app-only by
design. Any design must degrade to "capable" for them rather than disabling their columns.

## 4. What already exists (~80% of the graph)

- **`GeneratedIndexInfo`** already carries all four edges per entity: `EntityFullName` (collection),
  `IndexName`, `IndexEntityName` (projection), `IsDefault`. The generator *is* the authority for a
  generated pair and derives them once through `IndexNaming` rather than reading them back.
- **The pool is already joined** across source and referenced metadata (`allEntitiesProvider`,
  `GenerateIndexGenerator.cs:110-117`) and already fanned out to three producers — a fourth is an
  established pattern, not new plumbing.
- **The hand-written side already resolves `[FromIndex] → index symbol`** (`:615-626`) and holds the
  `INamedTypeSymbol` **two lines from** both missing pieces: the collection type
  (`baseType.TypeArguments[0]`) and `[DefaultIndex]`. Neither is taken.
- `HandWrittenIndexEntityInfo` lacks both fields. The two pools are joined today only by a
  *subtraction* on projection class name (`:170-181`).
- **`CreateIndexDefinition()` is `public virtual`** on both `AbstractIndexCreationTask<,>` and
  `AbstractMultiMapIndexCreationTask<>` — the seam the base class needs.
- **Analyzers see generated types.** `SortCompanionAnalyzer.cs:19-24` rests its whole correctness
  argument on it, and `DefaultIndexAnalyzer` opts in with `GeneratedCodeAnalysisFlags.Analyze`.
  ⚠️ But **no test proves it** — the harness attaches analyzers without running generators, and
  `DefaultIndexAnalyzerTests.cs:190-191` says so outright.

## 5. Design

### D1 — A base class supplies the call site a generator cannot

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

The generator emits `protected override void ConfigureSparkFields()` into the user's partial instead
of a private method they must remember to call. **Same body, two keywords** — and "never forgotten"
becomes structural rather than documented.

This is the one thing a generator genuinely cannot do: *"a generator cannot add statements to a
hand-written constructor body, only members to the class"* (`HandWrittenProducer.cs:143-145`), which
is why `SPARK006` and `SPARK_INDEX_009` exist.

### D2 — Use the string overload, never the lambda

`AbstractIndexCreationTask<T>` is `AbstractIndexCreationTask<T,T>`, so `Index(x => x.Field, …)` binds
against the **entity**, not the projection. It cannot name a computed field, a `{Name}Sort` companion
or a `{Name}Raw` wrapper — exactly the fields that need configuring. The existing generator already
uses `Index(nameof(V.X), …)` for this reason.

Declaring `AbstractIndexCreationTask<Car, VCar>` would fix the typing and make the index **invisible
to Spark's catalog**, which matches arity-1 only (`IndexCatalog.cs:214-236`).

### D3 — The field list keeps coming from declared attributes, never from the Map

`[Search]`, `[Breadcrumb]`, `DateTimeOffset` classification on the projection type, resolved from
symbols at compile time where diagnostics have a real source location. See §2: the Map is unreadable.

### D4 — `StoreAllFields` is NOT part of the base class

`CommitIndexShapeGuardTests` fails the build if it reaches `Commits_ByRepository`, because storing a
`DateTimeOffset` flattens it through projection and loses the offset. Opt-in at most.

### D5 — ⛔ The capability flags are NOT the input

Rejected with evidence in `subquery_column_filters_PRD.md` §3.10.3. Summary: the emission list is
empty (everything a `true` could require is already emitted unconditionally), and `false →
FieldIndexing.No` degrades silently, applies to every query through the index, makes `search()`
throw, and defeats the `SortColumnsAreCallerSupplied` exemption. The three flags authored anywhere in
this repo are a color-swatch column, a licence plate and a video URL — presentation and disclosure
judgements, none describing anything an index could be configured for.

**Do not re-litigate.** The mechanism is fine; that input is wrong.

## 6. Risks

- **⚠️ Reindex cost is unmeasured.** Whether a `Fields`-only `IndexDefinition` change forces a full
  rebuild on 7.2.6 is not known, and it gates every migration. CodeCoverage's `Commits` index backs
  the commit list, history chart, sparklines and branch badges, which return **partial results**
  during a rebuild — the same blast radius as an incident already documented in that index's own
  comments. SP1.
- **Packaging.** `Abstractions` has exactly one reference (`Attributes`) and **no `RavenDB.Client`**,
  deliberately: an entity library sees the attributes without dragging RavenDB in. The base class
  needs a new small package, which under this repo's rule starts at `10.0.0-preview.*` and
  auto-publishes on push to master.
- **Ordering hazard.** Generated indexes call `OnInitialize()` last. A base-class
  `ConfigureSparkFields()` invoked from `CreateIndexDefinition()` runs *after* it, so a hand-written
  `Index(...)` would be **silently overwritten** (last-write-wins on a dictionary). Must skip a field
  already present in `IndexesStrings`.
- **Migration touches `libs/`**, which fires the CI-only version-bump gate and republishes packages.
- **Six of ten indexes are not `partial`** and have no projection; each needs a source edit to
  participate at all.

## 7. Non-goals

- Deriving capability flags from indexes, or writing them into model JSON (§D5).
- Reading the Map, statically or at runtime (§2).
- A multi-map variant **until P4 lands** — no production multi-map exists, and three call sites
  mis-derive its collection type today.
