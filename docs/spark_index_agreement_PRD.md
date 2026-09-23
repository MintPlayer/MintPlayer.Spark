# Making the C# index and Spark's belief about it agree — PRD

Successor to the investigation in [`subquery_column_filters_PRD.md`](subquery_column_filters_PRD.md)
§3.9–§3.10 (PR #441). That PR fixed the symptoms; this is the class.

**Status: SHIPPED in PR #441.** `SparkIndexCreationTask<T>` (+ the multi-map variant), the guarded
`Index(...)`/`Store(...)` emission, the base-type-gated `ConfigureSparkFields()` override, SPARK018,
the consolidated base-type walk, and the DemoApp migration are all in. `Commits_ByRepository` gained a
`[FromIndex]` projection, so the corpus is now **5 of 10** hand-written indexes without one, not 6.
⚠️ Statements below written in the future or conditional tense pre-date that and should be read as
design rationale rather than as outstanding work; the live status table is in
[`subquery_column_filters_plan.md`](subquery_column_filters_plan.md).

SP1, SP2 and SP3 have all run and are reported in
[`spark_index_agreement_plan.md`](spark_index_agreement_plan.md). They changed the design in four
material ways, marked ⛳ below. Everything here is measured unless it says otherwise.

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

⛳ **The trigger is wider than "`[Search]`".** `IndexSearchFields()` is emitted for `[Search]` text
fields **or any `DateTimeOffset` field** (`GenerateIndexGenerator.cs:572-599`), because a
`DateTimeOffset` needs a wrapper companion at `FieldIndexing.No`. The generator's own comment says
that without it Corax **parks the whole index at `state=Error, entries=0` after a clean deploy** — so
the `DateTimeOffset` case is *more* severe than lost search, not less. Any guard must cover both.

## 2. ⚠️ Measured ground truth (RavenDB 7.2.6, Corax, real server)

**The Map projection is the gate. `Index(...)` and `Store(...)` are not.**

| operation | no `Index(...)` | `Exact` | `Search` | `FieldIndexing.No` | not in Map |
|---|---|---|---|---|---|
| `where f == v` | ✅ case-**insensitive** | ✅ case-**sensitive** | ⚠️ **0 rows** | ⚠️ **0 rows, no error** | ❌ `ArgumentException` |
| `order by f` | ✅ correct | ✅ correct | ⚠️ no-op | ⚠️ **silent no-op** (asc == desc) | ❌ same |
| `search(f, …)` | ⚠️ **0 rows** | ⚠️ **0 rows** | ✅ matches a term | ❌ throws (no analyzer) | ❌ same |
| projection | ✅ (`StoreAllFields` needed for index-only computed fields) | ✅ | ✅ | ✅ value intact | ❌ |

⛳ **`Exact` is not a milder `Search`; it is the opposite half of the question**, and the rows above are
measured by `ExactVersusSearchSemanticsTests`. Two consequences that keep being assumed the other way:

- **`search()` over a non-analyzed field returns 0 rows with HTTP 200** — it does not throw. Only
  `FieldIndexing.No` throws. So a field that *should* have been declared `Search` and was not loses
  full-text search with no signal whatsoever, which is the entire reason `SPARK018` exists.
- **`Exact` differs from no call at all in exactly one respect: case sensitivity.** A map-emitted field
  is already equal-matchable and sortable. That is why `Exact` is right for an OIDC client id
  (`OidcApplications_ByClientId.cs:25`) and **wrong for a generated sort companion** — it would silently
  order `"Zebra"` before `"apple"` and make a filter for `"alpha bravo"` match nothing. The repo tried
  `Exact` + companion once and dropped it; see the historical note on `SparkModelSymbols.IsDateTimeOffset`.

Consequences that constrain every design below:

- **A Map-emitted field is already queryable and sortable with no `Index(...)` call.** `Index(...)`
  only ever *changes* a default: `Search` analyzes, `Exact` is case-sensitive, `No` removes.
- **`FieldIndexing.Search` destroys equality and ordering** (tokenization) — which is why the
  `{Name}Sort` companion exists and why both the sort and filter paths resolve through it.
- **`IndexDefinition.Fields` is an override table, not an inventory.** An index with two fully
  queryable fields and nothing declared has `Fields.Count == 0`. Absent means *capable*.
- ⛳ **Stronger than that: the server prunes the table to its minimal form.** Deploying
  `Fields = { Note: { Indexing = Default } }` comes back from `GetIndexOperation` with the key
  **absent**, and provokes no rebuild. An explicitly declared `FieldIndexing.Default` never reaches
  the stored definition at all (SP1).
- **The emitted field set exists only as a rendered C# string** in `IndexDefinition.Maps`. Nothing —
  generator, analyzer or runtime — can read it reliably: `LoadDocument`, `let`, ternaries, coalesce,
  `CreateField` and anonymous projections all defeat it. The repo's own analyzer refuses to try and
  says why (`SortCompanionAnalyzer.cs:65-67`).
- **`IRavenQueryInspector.IndexName`** returns the deployed name for a static index and **null for a
  dynamic one** — an exact, non-heuristic discriminator (used by #441's search fix).

### 2.1 ⛳ Redeploying an index is side-by-side, not a service interruption (SP1)

Measured on 7.2.6/Corax with 50,000 documents, sampling every ~10 ms. For every `Fields`-only
transition, a second index named **`ReplacementOf/{indexName}`** appeared within ~40 ms while the
live index was untouched: entries stayed at exactly 50,000, `IsStale` stayed false, and an unhinted
query returned all 50,000 rows on **every sample, never once lower**. Writes during the window were
indexed by the live index (50,000 → 50,500 mid-window).

| transition | `Compare` | replacement? | window (50k docs) |
|---|---|---|---|
| identical definition (control) | `None` | **no** — true no-op | — |
| add `Indexing = Search` | `Fields` | yes | 5582 ms |
| add `Indexing = Default` (no-op override) | `Fields` | **no** — normalised away | — |
| add `Storage = Yes` | `Fields` | yes | 2506 ms |
| `Indexing` Search → Default | `Fields` | yes | 1956 ms |
| change the **Map** (calibration control) | `Maps` | yes | 1988 ms |

The Map control behaves identically to the Fields changes, which is the point: **the blast radius of
a Fields change equals that of a Map change, and that blast radius is indexing work, not partial
results.** The guarantee is unconditional — it holds even for an index that had mapped 0 documents
with indexing paused.

The real cost nobody had stated: **two copies of the index exist on disk and in memory** for the
duration.

**Predicting it client-side.** `IndexDefinition.Compare(IndexDefinition)` is `public` and returns
`IndexDefinitionCompareDifferences`. The server's own predicate is
`IndexDefinition.ReIndexRequiredMask`, which is `internal static readonly` — reachable only by
reflection or by hardcoding the flag set. It **over-predicts** (`Indexing = Default` scores
`Fields`/rebuild-required and produces no rebuild) and never under-predicted in any run. Compare
against the **deployed** definition, not the previous source version: the server stores a normalised
form.

## 3. The corpus

**10 hand-written indexes; 4 have a `[FromIndex]` projection, 6 do not.**

| index | projection | partial? | declaring assembly references |
|---|---|---|---|
| `DemoApp/Cars_Overview` | `VCar` | yes | `MintPlayer.Spark` |
| `DemoApp/Companies_Overview` | `VCompany` | yes | `MintPlayer.Spark` |
| `DemoApp/Company_Cars` | `VCompanyCar` | yes | `MintPlayer.Spark` |
| `DemoApp/People_Overview` | `VPerson` | yes | `MintPlayer.Spark` |
| `CodeCoverage/Commits_ByRepository` | **none** (nested `Result`, not `[FromIndex]`) | **no** | `MintPlayer.Spark` |
| `libs/identity_provider/Oidc*` × 3 | **none** | **no** | `MintPlayer.Spark` |
| `libs/messaging/SparkMessages_ByQueue` | **none** (anonymous type) | **no** | ⚠️ `Abstractions` + `RavenDB.Client` only |
| `libs/replication/SparkSyncActions_ByStatus` | **none** | **no** | ⚠️ `Abstractions` + `RavenDB.Client` only |

Plus **7 fully generated** via `[GenerateIndex]` (CodeCoverage ×5, Fleet `Car`, HR `Person`).
`GenerateIndexGenerator.Producer.cs:251` emits `AbstractIndexCreationTask<{Entity}>` unconditionally,
so **a generated index can never be multi-map or 2-arg.**

⛳ **The generator can only ever see 11 of these 17 rows** — 7 generated entities + 4 `[FromIndex]`
projections. The six projection-less indexes have no syntax node the generator subscribes to, so no
amount of descriptor unification brings them in (SP2). This is a ceiling on the design, not a defect.

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
- **`CreateIndexDefinition()` is `public virtual`** and is called **exactly once** per deploy on a
  freshly constructed instance, through both `AbstractIndexCreationTask.Execute` and
  `IndexCreation.CreateIndexes` (measured, SP2). It is declared on `AbstractIndexCreationTaskBase<T>`
  and overridden on the arity-2 form — *not* on arity-1, which inherits it. Overriding it from
  `SparkIndexCreationTask<T> : AbstractIndexCreationTask<T>` compiles and runs.
- ⛳ **`Index`, `Store`, `StoreAllFields` and friends are `protected` on
  `AbstractGenericIndexCreationTask<T>`**, so a base class in a *different assembly* can call them
  through derivation. No `InternalsVisibleTo` needed.
- ⛳ **`IndexDefinition` equality is value-based**, which makes the "byte-identical definition" check
  a cheap unit test rather than a string diff.

### 4.1 ⛳ Analyzers and generated code — measured, and more precise than "they see it" (SP3)

The repo asserted this in prose twice with no test. `AnalyzerSeesGeneratedCodeTests` (11 tests) now
pins it, and the answer has three parts that must not be collapsed:

| question | answer |
|---|---|
| Are generated **symbols** resolvable? | ✅ **Always**, under every `GeneratedCodeAnalysisFlags` value |
| Do analyzer **actions visit** generated declarations? | Only under `Analyze` |
| Do diagnostics **located in** a generated tree survive? | Only under `ReportDiagnostics` — otherwise dropped |

The headline proof is a real analyzer changing its answer: `SortCompanionAnalyzer` fires SPARK005 for
a missing `VCar.ModelSort`, and falls silent on the same sources once the generator has run. So
`SortCompanionAnalyzer.cs:19-24` is correct as written, and is now pinned.

✅ **The property M5 stands on:** a **partial** type keeps being visited under `None` even after the
generator contributes a second declaration — Roslyn treats a symbol as generated only when *every*
declaration is. An index class is partial and always has a hand-written half.

"Generated" is a property of the **tree**, not its origin: a file name ending `.g.cs` **or** a leading
`// <auto-generated/>` banner. This repo's generators trip both. None emits `[GeneratedCode]`.

**Not measured: the IDE.** All of the above is the Roslyn driver that `csc` hosts, plus an
`AdhocWorkspace`. What a test process cannot observe is Visual Studio's *scheduling* — whether the
generated trees behind live squiggles are current, especially in balanced mode. ⛳ **The design rule
that is safe under either answer, and which the design adopts:**

> A rule must **fire** from hand-written declarations only. A generated symbol may only ever turn a
> diagnostic **off**.

That is what `DefaultIndexAnalyzer` already does deliberately.

### 4.2 ⛳ The cross-project diagnostic drop is worse than recorded (SP3)

`ValueObjectCompletenessAnalyzer.cs:136-150` has the **observation** right and the **mechanism**
wrong. It says the driver "discards any diagnostic whose location lives in a syntax tree the analyzed
compilation does not contain". Measured: `SymbolAnalysisContext.ReportDiagnostic` **throws
`ArgumentException`**, which kills the whole symbol action and every other diagnostic it would have
reported for that symbol, leaving only `AD0001` — informational and routinely invisible. "Discards"
reads as recoverable; it is not. Its repair (report at a location the analyzed symbol owns) is
correct either way.

This and the memory note *"analyzers can't locate cross-assembly types"* are two halves of one
hazard:

| topology | referenced type looks like | diagnostic aimed at it |
|---|---|---|
| `dotnet build` (`PortableExecutableReference`) | no `DeclaringSyntaxReferences` | emitted, but bare `CSC : warning …` with no file/line; no code fix runs |
| IDE / loaded solution (`CompilationReference`) | full source location in the *other* project | **throws**; the action dies; nothing reaches the developer |

**M5 is not exposed.** Every input it needs — index class, constructor, `[FromIndex]` projection,
`[Search]` attributes — is application-owned. Only the mapped entity lives in a library, and M5 never
points at it.

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

    /// <remarks>Must be idempotent — see D6.</remarks>
    protected virtual void ConfigureSparkFields() { }
}
```

The generator emits `protected override void ConfigureSparkFields()` into the user's partial instead
of a private method they must remember to call. **Same body, two keywords** — and "never forgotten"
becomes structural rather than documented.

This is the one thing a generator genuinely cannot do: *"a generator cannot add statements to a
hand-written constructor body, only members to the class"* (`HandWrittenProducer.cs:143-145`), which
is why `SPARK006` and `SPARK_INDEX_009` exist.

⛳ **The override cannot be emitted unconditionally.** All ten hand-written indexes derive from
`AbstractIndexCreationTask<T>` today, and `protected override` against a base that has no such method
is a hard **CS0115**. Emission must be gated on the index's own base type, which is the same
corrected base-type walk M6 fixes. **M2 therefore depends on M6.**

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
`DateTimeOffset` flattens it through projection and loses the offset.

⛳ **It is load-bearing for a second reason SP1 surfaced:** `StoreAllFields` in the base would be a
real `Fields` change on *every* index in *every* app simultaneously — the one way this migration could
become a corpus-wide rebuild. Opt-in at most.

### D5 — ⛔ The capability flags are NOT the input

Rejected with evidence in `subquery_column_filters_PRD.md` §3.10.3. Summary: the emission list is
empty (everything a `true` could require is already emitted unconditionally), and `false →
FieldIndexing.No` degrades silently, applies to every query through the index, makes `search()`
throw, and defeats the `SortColumnsAreCallerSupplied` exemption. The three flags authored anywhere in
this repo are a color-swatch column, a licence plate and a video URL — presentation and disclosure
judgements, none describing anything an index could be configured for.

**Do not re-litigate.** The mechanism is fine; that input is wrong.

### D6 — ⛳ Duplicate field declaration THROWS. The guard is a correctness requirement.

`Index(string, FieldIndexing)` is a `Dictionary.Add`, **not** an indexer assignment:

```
System.ArgumentException: An item with the same key has already been added. Key: Name
   at Raven.Client.Documents.Indexes.AbstractGenericIndexCreationTask`1.Index(String, FieldIndexing)
```

So the "ordering hazard" is not silent drift — it is an exception out of `CreateIndexDefinition()`,
i.e. **a crash at deploy**, on startup, for every migrated index whose constructor still calls
`IndexSearchFields()`. Every generated `Index(...)` must therefore be wrapped:

```csharp
if (!IndexesStrings.ContainsKey(nameof(VCar.LicensePlate)))
    Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
```

`IndexesStrings` is `protected` on `AbstractGenericIndexCreationTask<TReduceResult>` and reachable
from a derived class in another assembly.

Three consequences:

- **The guard is what makes a half-migrated index safe.** Constructor call *and* override both
  present is then harmless: the constructor wins, the override no-ops.
- ⛳ **`ConfigureSparkFields()` must be idempotent**, and the guard is what buys it. Without the
  guard, calling `CreateIndexDefinition()` twice on one instance throws on the first field, because
  `IndexesStrings` persists across calls. `CommitIndexShapeGuardTests.cs:51` calls it outside the
  deploy path. With the guard, two calls produce equal definitions.
- ⚠️ **The guard is not total.** A string-keyed check cannot see a lambda-declared field: after
  `Index(x => x.Name, …)`, `IndexesStrings.ContainsKey("Name")` is `false`, and the later
  `Index("Name", …)` surfaces as `IndexCompilationException: There is a duplicate key in indexes:
  Name`. D2 already forbids the lambda overload and no index in the repo uses it, but the guard does
  not enforce D2.

### D7 — ⛳ No new package. The base class goes in `MintPlayer.Spark`.

The apparent blocker was that no single package is referenced by all five index-declaring assemblies:
`Messaging` and `Replication` reference `Abstractions` + `RavenDB.Client` only, never
`MintPlayer.Spark`.

That is the wrong question, because **those two gain nothing from migrating.** The base class exists
solely to supply the call site for the generator's `Index(...)` block, and the generator only emits
that block for an index named by a `[FromIndex]` projection. Neither has one, so a base class would
emit nothing for them. Migrating them would add a dependency edge from below the framework core
upward, fire the CI `libs/` version-bump gate, and republish two packages — for zero behaviour change.

So: `SparkIndexCreationTask<T>` in `MintPlayer.Spark`, namespace `MintPlayer.Spark`. This also
removes the `10.0.0-preview.*`-from-scratch auto-publish risk §6 used to carry. If a `libs/` index
ever grows a projection, revisit then — it is a one-line `ProjectReference`, not a design reversal.

⚠️ `MintPlayer.Spark.Attributes` is a bare SDK project with zero references and must stay that way.
`MintPlayer.Spark.Abstractions` must not gain `RavenDB.Client`.

## 6. Risks

- ⛳ **~~Reindex cost~~ — measured, and it is not a risk.** See §2.1. A `Fields`-only change rebuilds
  side by side; the live index serves complete, non-stale results throughout. The earlier claim that
  CodeCoverage's grids "return partial results during a rebuild" was **false for a definition
  change** — it generalised one step too far from the adjacent recorded fact that *renaming* an index
  rebuilds from scratch, which is a genuinely new index with no predecessor to serve from. The
  residual costs are: two copies of the index on disk/in memory for the window, and indexing work
  bounded by the mapped collection (`Commits` is **804 documents**; the 199,917 figure is
  `FileCoverages`, which this index does not map).
- ⛳ **~~Packaging~~ — resolved by D7.** No new package.
- ⛳ **Ordering is a crash, not drift.** See D6. This raised the guard from a nicety to a blocking
  requirement, and is why M2a ships first and alone.
- **Migration would touch `libs/`** — avoided entirely by dropping M4 (D7).
- **Six of ten indexes are not `partial`** and have no projection; each needs a source edit to
  participate at all, and none of them would gain anything by it.
- ⚠️ **The `knowsSpark` gate does not imply `MintPlayer.Spark` is referenced.**
  `GenerateIndexGenerator.cs:105-108` gates on `Abstractions` + the Raven index base type only, which
  `MintPlayer.Spark.Messaging` satisfies. Making *generated* indexes derive from
  `SparkIndexCreationTask<T>` would be a `CS0246` there. Gating on the index's own base type (D1)
  sidesteps this; a generated-index migration would need a third condition on the gate.

## 7. Non-goals

- Deriving capability flags from indexes, or writing them into model JSON (§D5).
- Reading the Map, statically or at runtime (§2).
- A multi-map variant **until M6 lands** — no production multi-map exists, and four call sites
  mis-derive its collection type today.
- ⛳ Migrating the `libs/` indexes (§D7) — dropped, not deferred.
- ⛳ Deleting `IndexSearchFields()`. It stays permanently as the escape hatch for indexes the base
  class cannot cover (multi-map, 2-arg map-reduce, JavaScript indexes). It is
  `public const string IndexSearchFieldsMethod` and the comment already says renaming it breaks every
  consumer's constructor. Only the *documentation* changes, to present the base class as the default.
