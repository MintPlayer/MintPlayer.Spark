# Index/model agreement — implementation plan

Companion to [`spark_index_agreement_PRD.md`](spark_index_agreement_PRD.md).
**Status: all three spikes run; M1, M2, M2a, M3, M5, M6, M9, M10 implemented. M4 dropped, M7 a no-op,
M8.1 deleted, M8.2 left as a decision.** Shipped in PR #441 alongside the sub-query filter fix.

| item | state |
|---|---|
| SP1 reindex cost | ✅ measured — side-by-side, no partial results |
| SP2 descriptors / base class / packaging | ✅ measured — no new package; the hazard throws |
| SP3 analyzers vs generated code | ✅ measured — pinned by `AnalyzerSeesGeneratedCodeTests` |
| M2a guard every generated `Index(...)` | ✅ done, first |
| M6 base-type walk (four copies) | ✅ done, shared helper + real RavenDB types in tests |
| M1 `SparkIndexCreationTask<T>` (+ multi-map) | ✅ done, in `MintPlayer.Spark` |
| M2 generator emits the gated override | ✅ done |
| M3 four DemoApp indexes migrated | ✅ done |
| M4 `libs/` indexes | ⛔ dropped — a base class emits nothing for them |
| M5 SPARK018 | ✅ done |
| M7 CodeCoverage | ⚪ no-op — no projection, so nothing to emit |
| M8.1 `PullRequestFeedback` | ⛔ deleted — correct by design, not a gap |
| M8.2 `Commit` binding | ⏳ **decision needed** — the recommended option was refuted |
| M9 false comments | ✅ done |
| M10 dead `QueryEntitiesWithIncludesAsync` | ✅ deleted |

## What the spikes changed

SP1 was the gate that could have stopped this shipping. It did the opposite: it **removed** the gate.
SP2 and SP3 both came back with corrections that reorder the work.

| | outcome | effect on the plan |
|---|---|---|
| **SP1** reindex cost | ✅ side-by-side; no partial results, ever | **M7's conditional is gone.** M4 was already going away for another reason |
| **SP2** descriptors / base class | ⚠️ ordering hazard **throws**; no new package needed | **M2a inserted first**; M4 **dropped**; M2 now depends on M6 |
| **SP3** analyzers vs generated code | ✅ good world, with a precise caveat | **M5 unblocked**, but it is ~10× the estimated size and belongs in an existing analyzer |

## Order

**M2a → M6 → M1 → M2 → M3 → M5 → M8.** M2a is first because it is provably a no-op today and makes
every later step order-independent. M6 is second because M2 cannot emit a base-type-gated override
without it.

---

## Spikes — all complete

### ✅ SP1 — Reindex cost of a `Fields`-only change. **Answer: side-by-side. Not an operational event.**

Full measured table in PRD §2.1. Headline: a `ReplacementOf/{indexName}` index is built while the
live index keeps serving **complete, non-stale** results and keeps indexing new writes. Measured
across five transitions plus a Map-change control, on 50,000 documents.

Three independent reasons `apps/CodeCoverage` is an ordinary code change:

1. `Commits_ByRepository` declares no `Index(...)` or `Store(...)` at all — its `Fields` is `{}`. A
   `ConfigureSparkFields()` that emits nothing yields `Compare == None`, measured as a true
   server-side no-op (no replacement, settled in 380 ms).
2. The collection is **804 documents**. 50,000 rebuilt in ~2 s.
3. Side-by-side makes it moot regardless.

Deployment needs no manual step — `SparkMiddleware.cs:780` calls `IndexCreation.CreateIndexes` at
startup, so the migration lands on app restart.

**Replace the removed gate with something falsifiable and cheap:** assert
`CreateIndexDefinition().Compare(previousDefinition) == IndexDefinitionCompareDifferences.None` in a
unit test. `IndexDefinition` equality is value-based, so this is cheap. That — not a cost
measurement — is what makes a migration a no-op.

⚠️ Do **not** build a "just use `Compare()` to decide" shortcut: it over-predicts
(`Indexing = Default` scores rebuild-required and produces none). The new test pins that disagreement
so the shortcut fails there rather than in production.

**Test written and passing (uncommitted):**
`tests/MintPlayer.Spark.Tests/Services/IndexFieldsOnlyRedeployTests.cs` — 500 documents, holds the
rebuild window open with `StopIndexingOperation` rather than racing it, so it cannot pass vacuously on
a fast machine. Marginal cost in a full run is about two small RavenDB fixtures.

### ✅ SP2 — Unify the two index descriptors. **Answer: keep them separate; pass a shared edge alongside.**

The stated kill criterion was not what decided it — the `generatedNames` subtraction
(`GenerateIndexGenerator.cs:173-181`) was rewritten over a shared shape and built clean. The real
obstacle is that a merged record would be ~80% empty on either side (`GeneratedIndexInfo` carries
`Properties`, `CollectionVariable`, `ItemVariable` and six diagnostic buckets; `HandWrittenIndexEntityInfo`
carries `IndexPathSpec`, `IsIndexPartial`, `Companions`, `IndexedFields` and two `PathSpec`s), and the
two come from different syntax providers keyed on different attributes, from different symbols.

Build this instead, produced by both descriptors and concatenated:

```csharp
[AutoValueComparer]
public partial class IndexGraphEdge
{
    public string CollectionFullName { get; set; }   // global::-qualified
    public string IndexName { get; set; }
    public string ProjectionName { get; set; }       // empty when the index has no [FromIndex]
    public bool   IsDefault { get; set; }
    public bool   IsSparkIndex { get; set; }         // derives from SparkIndexCreationTask<T>; needed by M2
}
```

The two missing fields on `HandWrittenIndexEntityInfo` are both one line from the existing
`indexType` at `GenerateIndexGenerator.cs:617-619`.

⚠️ **`IsDefault` means opposite things on the two sides and must be documented, not glossed.** On
`GeneratedIndexInfo` it is `[GenerateIndex(IsDefault = …)]`, which **defaults to `true`**
(`:405`). On the hand-written side it is the *presence* of `[DefaultIndex]`, which defaults to
**false**. They converge only at attribute level: "the emitted/declared index class carries
`[DefaultIndex]`". Exactly one hand-written index carries it today (`Cars_Overview.cs:13`).

⚠️ **Constraint the PRD did not record:** `MintPlayer.ValueComparerGenerator` includes *every*
property in the generated comparer, with no setter check (`ValueComparerGenerator.cs:83-91`). A
computed `public IndexGraphEdge Graph => …` property is compared on every incremental pass; it works
only because `IndexGraphEdge` is itself `[AutoValueComparer]`, and if that attribute were dropped the
comparer silently falls back to reference equality and **incrementality dies with no error**. Make the
mapping a static extension method, or mark the property `[ComparerIgnore]`.

⚠️ **Ceiling:** the generator sees **11 of 17** rows, not 17. See PRD §3.

### ✅ SP3 — Does an analyzer read generator output? **Answer: yes, with a three-part caveat.**

Full table in PRD §4.1. Generated **symbols** are always resolvable under every flag; generated
**declarations** are only visited under `Analyze`; diagnostics **located in** a generated tree are
dropped without `ReportDiagnostics`. The property M5 stands on: a **partial** type keeps being visited
under `None`, because Roslyn treats a symbol as generated only when *every* declaration is.

**Not measured: the IDE's scheduling.** Adopt the rule that is safe either way — *a rule fires from
hand-written declarations only; a generated symbol may only ever turn a diagnostic off.*

**Cross-project drop confirmed, mechanism corrected** (PRD §4.2): `ReportDiagnostic` **throws**, it
does not discard. M5 is not exposed — all its inputs are application-owned.

**Test written and passing (uncommitted):**
`tests/MintPlayer.Spark.SourceGenerators.Tests/Diagnostics/AnalyzerSeesGeneratedCodeTests.cs` (11
tests), plus an additive `RunGeneratorThenAnalyzersAsync` on `GeneratorHarness`. Project run:
289/289. This retires PRD §4's "no test proves it", and `DefaultIndexAnalyzerTests.cs:190-191`'s
admission should now point at it.

---

## Milestones

### M2a — ⛳ NEW, and first: guard every generated `Index(...)` call

```csharp
if (!IndexesStrings.ContainsKey(nameof(VCar.LicensePlate)))
    Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
```

**Ship this alone, before anything else.** It is provably a no-op today (no index declares a field
twice; the only alternative outcome was an `ArgumentException`), it needs no base class, no packaging
decision and no `libs/` touch, and it makes every later step order-independent — a half-migrated index
becomes harmless rather than a startup crash.

Test churn is zero: existing tests assert on the `Index(nameof(...), …);` substring
(`HandWrittenIndexEntitySortFieldsTests.cs:246,292-294`) which the guard preserves verbatim, and no
`VerifyResults/*.verified.txt` snapshot contains `IndexSearchFields`.

See PRD §D6 for why this is a correctness requirement rather than a nicety.

### M6 — Fix the base-type walk. ⛳ **Four copies, not three** *(now a dependency of M2)*

Both hierarchy claims confirmed independently by two agents, by reflection over the shipped
`Raven.Client.dll` 7.2.6:

```
AbstractIndexCreationTask<TDocument>               -> AbstractIndexCreationTask<TDocument, TDocument>
AbstractIndexCreationTask<TDocument,TReduceResult> -> AbstractGenericIndexCreationTask<TReduceResult>   (NOT the 1-arg form)
AbstractMultiMapIndexCreationTask<TReduceResult>   -> AbstractGenericIndexCreationTask<TReduceResult>
AbstractMultiMapIndexCreationTask                  -> AbstractMultiMapIndexCreationTask<object>
```

So the 2-arg form does not derive from the 1-arg form (the derivation runs the *other* way), and the
multi-map argument is the reduce result.

| # | site | accepts | 1-arg | 2-arg | multi-map\<T\> | multi-map (non-gen) |
|---|---|---|---|---|---|---|
| 1 | `SparkMiddleware.cs:790` `IsAbstractIndexCreationTask` ⛳ **the missed one** | open-generic identity | ✔ | ✘ **never discovered** | ✔ | ✔ |
| 2 | `IndexCatalog.cs:214` `GetCollectionTypeFromIndex` | open-generic identity | ✔ TDocument | ✘ null | ✘ TReduceResult | ✘ `object` |
| 3 | `DefaultIndexAnalyzer.cs:140` `CollectionTypeOf` | name + namespace + arity 1 | ✔ TDocument | ✘ null | ✘ TReduceResult | ✘ `object` |
| 4 | `ProjectionPropertyAnalyzer.cs:140` `GetEntityTypeFromIndex` | name only, arity ≥ 1 | ✔ TDocument | ✔ **correct by accident** | ✘ null | ✘ null |

⚠️ **#1 gates #2, so fixing `IndexCatalog` alone fixes nothing.** A 2-arg index is not merely
unregistered — it is **never discovered**, so even the `Console.WriteLine` warning at
`IndexCatalog.cs:77` never fires, while `IndexCreation.CreateIndexes` (which uses RavenDB's own
correct criterion) deploys it happily. A query naming it then fails with *"no deployed index has that
name"*, which is false and points the author at the wrong thing.

A fifth, differently-shaped walk at `RavenIndexHelper.cs:80-86` uses
`typeof(AbstractIndexCreationTask).IsAssignableFrom` — permissive and **correct**. The test helper is
right and production is wrong.

⚠️ **Delete the false doc comment at `DefaultIndexAnalyzer.cs:133-139`**, which asserts in prose that
the 2-arg form derives from the 1-arg "so the walk covers it without a separate case". It is almost
certainly the origin of the bad test stub. Delete, don't soften.

**Shared helper — one contract, two mirrors.** Literal sharing is impossible: the runtime sites are
`net10.0` and speak `System.Type`; the analyzer sites are `netstandard2.0` and **must not** reference
`RavenDB.Client` (`GenerateIndexGenerator.cs:101-108` already probes by metadata name for this
reason). So: 4 copies → 2 helpers, one per type system, sharing one documented contract, each with a
`// keep in sync with <path>` note.

```csharp
internal enum RavenIndexKind
{
    None,     // not a RavenDB index type at all
    Map,      // AbstractIndexCreationTask<TDocument> | <TDocument, TReduceResult> -> exactly one collection
    MultiMap, // several collections, none derivable
    Opaque,   // an index, but no collection derivable (AbstractGenericIndexCreationTask<T>,
              // non-generic AbstractIndexCreationTask, AbstractJavaScriptIndexCreationTask)
}

// runtime: libs/spark/MintPlayer.Spark/Services/RavenIndexHierarchy.cs
internal static RavenIndexKind Classify(Type indexType, out Type? collectionType);
internal static bool IsIndex(Type type) => typeof(AbstractIndexCreationTask).IsAssignableFrom(type);

// analyzer: libs/source_generators/.../Naming/RavenIndexHierarchy.cs  (beside IndexNaming)
internal static RavenIndexKind Classify(INamedTypeSymbol? indexType, out INamedTypeSymbol? collectionType);
internal static bool IsSparkIndex(INamedTypeSymbol indexType);   // walks past SparkIndexCreationTask<T>
```

Algorithm, identical in both — walk **the type and its bases** (start at the type itself, not
`.BaseType`; callers filter `IsAbstract` as `DefaultIndexAnalyzer:76` already does), and at each level:

1. namespace must be `Raven.Client.Documents.Indexes`;
2. `AbstractMultiMapIndexCreationTask` (arity 0 or 1) → `MultiMap`, collection `null`. **Test this
   first** — a multi-map also derives from `AbstractGenericIndexCreationTask<>` and the non-generic
   `AbstractIndexCreationTask`, so a later rule would otherwise claim it;
3. `AbstractIndexCreationTask` with arity 1 **or 2** → `Map`, collection = `TypeArguments[0]`;
4. `AbstractGenericIndexCreationTask` (arity 1), or `AbstractIndexCreationTask` /
   `AbstractJavaScriptIndexCreationTask` with arity 0 → `Opaque`;
5. nothing matched → `None`.

⚠️ **The walk must stay a loop over `BaseType`.** A direct check on `indexType.BaseType` breaks the
moment `SparkIndexCreationTask<T>` is interposed. All four existing copies are already loops, so they
survive M1 unchanged (verified).

**Two decisions this forces, which must not stay implicit:**

- The collection type on the unified descriptor must be **nullable**. `IndexCatalogEntry.CollectionType`
  is `required Type` today (`IndexCatalog.cs:58`), which is exactly why a `MultiMap`/`Opaque` index
  cannot be entered and is silently dropped.
- **Register `MultiMap`/`Opaque` entries with a null collection type** rather than dropping them, and
  have `Freeze()` skip null-collection entries in the `GroupBy(e => e.CollectionType)` at `:131` (a
  null key would throw on a `Dictionary<Type,…>`). That keeps `GetByIndexName` working so an explicit
  `indexName` binding resolves, while `GetDefaultForCollectionType` — which genuinely has no answer
  for a multi-map — never sees it. A throw would break startup for anyone legitimately adding one.

#### Tests: ⛳ **two go red, not one — and the stub should be deleted, not corrected**

Measured:

| state | result |
|---|---|
| as committed | 11 passed |
| stub corrected, analyzer untouched | `A_marked_map_reduce_index_clashes_with_a_marked_plain_index` **fails** |
| stub corrected **+** walk corrected | map-reduce test green; `A_marked_multi_map_index_clashes_with_a_marked_plain_index` **fails** |

`DefaultIndexAnalyzerTests.cs:22-24` stubs `AbstractIndexCreationTask<TDocument, TReduceResult> :
AbstractIndexCreationTask<TDocument>` — a hierarchy RavenDB does not have. The map-reduce test passes
today *only* because of that fiction. The multi-map test at `:139` encodes the same misreading in its
**fixture** (`Cars_MultiMap : AbstractMultiMapIndexCreationTask<Car>` read as "multi-map over Car"),
so under correct semantics its premise is invalid and it must be rewritten — or replaced by one
asserting a multi-map is *not* a SPARK009 participant.

**Better: delete the stub.** The test project already references `MintPlayer.Spark` →
`RavenDB.Client 7.2.6`, and the sibling `ProjectionPropertyAnalyzerTests.cs:15` already passes
`typeof(AbstractIndexCreationTask<>)` and uses the real types. That removes the whole class of
"our stub disagrees with reality" bug instead of re-spelling it correctly.

⚠️ `SparkExtensionsPrivateHelpersTests.cs:181` `MultiMapProbeIndex` registers under collection
`ProbeEntity` today only because the wrong reading coincidentally names the same type. Under correct
semantics it becomes a `MultiMap` with a null collection — that fixture needs a look during M6.

**Blast radius on shipping code: zero.** All 17 indexes enumerated; all 1-arg. No multi-map, no 2-arg
anywhere outside one test fixture.

### M1 — The base class

`SparkIndexCreationTask<T>` per PRD §D1, in **`MintPlayer.Spark`** (§D7 — no new package). Abstract,
so `GetAllInstancesOfType` never deploys it as an index in its own right. **No `StoreAllFields`**
(§D4). `ConfigureSparkFields()` documented as **required-idempotent** (§D6).

⚠️ `CommitIndexShapeGuardTests.cs:51` calls `new Commits_ByRepository().CreateIndexDefinition()`
directly. If M7 ever migrates that index, that test becomes the de-facto regression gate for the base
class in production code — which is what you want, but say so in the test.

### M2 — The generator emits the override *(depends on M6 and SP2's `IsSparkIndex`)*

Keep `private void IndexSearchFields()` **and** emit `protected override void ConfigureSparkFields()`
that delegates to it — but **only when the index derives from `SparkIndexCreationTask<T>`**. Emitting
it unconditionally is a hard **CS0115** on all ten indexes today (PRD §D1).

Verified generated output from the spike, compiling inside DemoApp:

```csharp
public partial class Cars_Overview          // re-parented to SparkIndexCreationTask<Car>
{
    private void IndexSearchFields()
    {
        if (!IndexesStrings.ContainsKey(nameof(global::DemoApp.Indexes.VCar.LicensePlate)))
            Index(nameof(global::DemoApp.Indexes.VCar.LicensePlate), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);
    }

    protected override void ConfigureSparkFields() => IndexSearchFields();
}

public partial class Companies_Overview     // still AbstractIndexCreationTask<Company> -> NO override
{
    private void IndexSearchFields() { … }
}
```

**Diagnostics to update:**
- `SPARK_INDEX_009` (`GenerateIndexDiagnostics.cs:99-105`) already parameterises the method name, but
  its message ends "…and call '{1}()' from the constructor", which is wrong for a Spark-derived index.
  Needs a second message format or a conditional argument. The test at
  `HandWrittenIndexEntitySortFieldsTests.cs:325` asserts only the id, so it survives a message change.
- `SPARK006` needs no code change; only the rationale comment at `HandWrittenProducer.cs:143-146`
  needs a sentence about the base class now supplying the call site.

**Docs:** `docs/guide-queries-and-sorting.md:543-549` and `docs/guide-dates-and-sorting.md:103`
present the base class as the default and `IndexSearchFields()` as the fallback. **It is never
deleted** (PRD §7).

### M3 — Migrate the four DemoApp indexes

`Cars_Overview`, `Companies_Overview`, `Company_Cars`, `People_Overview` — all already `partial`, all
with a `[FromIndex]` projection, **no production data**. Re-parent one at a time. Because of M2a,
leaving the constructor's `IndexSearchFields()` call in place is harmless; removing it is the end
state.

`Company_Cars` is a **no-op migration** — it has a projection but zero `[Search]`/`DateTimeOffset`
fields, so the generator emits nothing for it at all.

Assert `CreateIndexDefinition().Compare(previous) == None` (SP1).

### ~~M4 — `libs/` indexes~~ ⛳ **DROPPED, not deferred**

See PRD §D7. `Messaging` and `Replication` do not reference `MintPlayer.Spark`, and none of the five
`libs/` indexes has a `[FromIndex]` projection, so a base class would emit nothing for any of them.
Migrating costs a dependency edge from below the framework core upward, the CI version-bump gate and a
republish, for zero behaviour change. Revisit only if a `libs/` index grows a projection.

### M5 — Analyzer: the uncalled `IndexSearchFields()` ⛳ *(unblocked by SP3; bigger than estimated)*

**Add it to `SortCompanionAnalyzer` as a third rule, not a new analyzer.** Its entry point is
identical to SPARK005/006 — symbol action on `NamedType`, filter `[FromIndex]`, resolve `indexType`
from the attribute's ctor arg, walk the constructor. Projection→index is free; index→projection would
need a reverse scan. Packaging needs nothing (`AnalyzerPackagingTests` is per-assembly).

⛳ **The trigger is `[Search]` text fields *or any* `DateTimeOffset`** (PRD §1), not `[Search]` alone.

**Helper reuse:**
- `Mentioned` (`:174-185`) — reusable **verbatim**; `private` → `internal` only if M5 moves out.
- `IndexConstructor` (`:107-115`) — **needs lifting and two behaviour changes**: it returns the
  *first* constructor (`FirstOrDefault`), but M5's question is whether **every** constructor calls it
  — one ctor calling and another not is exactly the hole. Widen to
  `IEnumerable<ConstructorDeclarationSyntax>` and make SortCompanion's own `.FirstOrDefault()`
  deliberate. And it returns `null` for "no constructor", which SortCompanion treats as bail-out; for
  M5 that is a **positive**. The early-return contract is inverted.

⚠️ **Trap: do not widen the identifier scan from the constructor to the class.** The generated partial
declares `IndexSearchFields` itself, so a class-wide scan always matches and **the rule never fires**.
Scanning constructors is what makes it safe — the generated half has none.

⛳ **Size: ~280–320 lines**, not ~30. The ~30 is the rule logic only; the rest is the Rules partial,
the helper lift, this repo's comment density, and six test cases (fires / partial+call clean / no ctor
/ DateTimeOffset-only / two ctors / generated pair exempt). Reference: `SortCompanionAnalyzer` is
189+33 for two rules.

**After M1/M2, re-aim rather than retire it:** *partial, has indexed projection fields, derives from
`AbstractIndexCreationTask<T>` **and not** `SparkIndexCreationTask<T>`, and no constructor mentions
`IndexSearchFields`*. That keeps it guarding the un-migrated tail. Write the base-class case as
**exempted, not flagged**.

#### The two extra rules

**Rule A — `canSort`/`canFilter` on a collection attribute: ⛳ REAL and LIVE.**
`ConvertFilterValue` (`QueryExecutor.cs:2173-2199`) cannot coerce a scalar onto `string[]`/`List<T>`;
`JsonSerializer.Deserialize` throws, the catch returns `null`, and `AnyEquals` then emits
**`x.Flags == null`**. Sorting emits `OrderBy(x => x.Flags)` on a multi-value field: no error,
arbitrary order. Distincts would list distinct *arrays*.

**16 array attributes sit on a query surface and not one states `canSort`/`canFilter`**, so
`ColumnCapabilities` resolves absent → `true`: CodeCoverage 7 (`ApiToken.RepositoryIds`,
`Build.Sessions`, `BuildSession.{Flags,RawFileNames,Reports}`, `GitHubProject.{Columns,EventMappings}`),
HR 9. `IsBackedByShape` does not save them — an array attribute *does* resolve to a property on the
row shape — so the wire tells the grid these are sortable and filterable **today**.

⚠️ **Two decisions the plan must make explicitly:**
1. A rule on the *explicit* `"canSort": true` fires **zero** times ever. A rule on the *effective*
   value fires **16** times and forces edits to production CodeCoverage model files. Pick one.
2. **Consider fixing the executor instead.** RavenDB indexes arrays as multi-valued terms, so
   `where Flags = 'x'` is meaningful RQL. The breakage is entirely Spark's scalar assumption in
   `ConvertFilterValue`/`AnyEquals`. Coercing to the *element* type and emitting `Any` would make the
   flag honourable rather than pinning a limitation with a diagnostic.

⛳ **Detectable — and the repo says otherwise, wrongly.** `spark.targets:159` already makes
`App_Data\Model\*.json` `AdditionalFiles`; `SecurityConfigurationAnalyzer.ReadModelNames` (`:224-238`)
already reads them; `Location.Create(path, TextSpan, LinePositionSpan)` already squiggles a JSON line.
Best shape is a **hybrid**: read `queryType` from the JSON, resolve it with `GetTypeByMetadataName`,
and ask the *symbol* whether the property is a collection — immune to a stale `isArray`.

**Rule B — `canSort: true` on a `TranslatedString`: ⛳ mechanism real, hole effectively CLOSED.**
Both halves verified: the generator **replaces** the field (pinned by
`TranslatedStringFanOutTests.The_whole_object_field_is_not_emitted`), and `ResolveSortProperty`
(`:2325-2332`) only ever tries `requested + "Sort"`. But three guards stand in front: the synchronizer
derives `showedOn: PersistentObject` for such an attribute (measured in `Fleet/Car.json`);
`FindQuerySurfaceAttribute` requires `ShowedOn.HasFlag(Query)`; and `IsBackedByShape` already ANDs
`canSort` with the row shape. The executor logs and skips rather than 500ing. **One
`TranslatedString` attribute exists repo-wide**, not on a query surface.

**Verdict: not worth its own rule.** Fold into Rule A as a second message (~10 lines) if Rule A is
built; otherwise drop.

### M7 — CodeCoverage ⛳ **no longer gated**

SP1 removed the condition. `Commits_ByRepository` is not `partial`, has no projection, and is guarded
by `CommitIndexShapeGuardTests` against gaining `StoreAllFields`. Migrating it buys **nothing** (no
projection → the generator emits nothing), so the honest position is: *migrate only if something else
makes it worthwhile.* Keep the DemoApp → CodeCoverage ordering as discipline, not as a gate.

### M8 — Two model gaps ⛳ **one is not a gap; the other is not cheap**

**M8.1 `PullRequestFeedback` — ⛳ NOT a gap. Recommend no change; delete this item.**
It carries `[GenerateIndex]` and has no model file, and that is **correct**: it is not a property on
`CoverageSparkContext` and is not nested, so model sync never discovers it. It is used only from C#
(`PublishFeedbackCronJob.cs:49`). Nothing breaks; `--spark-verify-model` exits 0, so the model is
already a fixed point and synchronize would do nothing.

⚠️ "Fixing" it by adding the entity to the context is a **production change to
coverage.mintplayer.com** that creates query and per-object endpoints over an outbox document, needing
a `security.json` grant and row filtering. Don't. At most, record *why* the file is absent.

**M8.2 `Commit` has no `queryType`/`indexName` — real and live, but the obvious fix is a 500.**
`GetCommits` (`"source": "Database.Commits"`, no `indexName`) is a raw collection query, creating a
production auto-index `Auto/Commits/BySha`. It is reachable as the `Build.Commit` reference picker
(`Build.json:96`) and via a direct `/query/` hit. Consequence today: **slow, not wrong.**

⚠️ **Stamping `"indexName": "Commits_ByRepository"` converts slow into broken.** That index's Map
emits `Repository, Branch, AuthoredAt, HasCoverage, PullRequestNumber, ParentSha, ParentLookupDone,
CompleteCoverage, ContributedFromFork` — **no `Sha`** (only `ParentSha`), and `GetCommits` sorts by
`Sha`. Per PRD §2, ordering by a field not in the Map is `ArgumentException`, i.e. HTTP 500 for the
whole query.

⛳ **The preferred option does not exist. Measured 2026-09-23, and it refutes the recommendation above.**
Binding the index constrains sorting to fields the Map emits. Intersecting the two sets:

| set | members |
|---|---|
| `Commit` attributes with `showedOn` containing `Query` | `Branch`, `ContributedFromFork`, `Coverage`, `CoverageDeltaVsDefaultBranch`, `CoverageDeltaVsParent`, `Date`, `PullRequestBaseRef`, `PullRequestBaseSha`, `Sha` |
| fields `Commits_ByRepository` emits | `Repository`, `Branch`, `AuthoredAt`, `HasCoverage`, `PullRequestNumber`, `ParentSha`, `ParentLookupDone`, `CompleteCoverage`, `ContributedFromFork` |
| **intersection** | **`Branch`, `ContributedFromFork`** |

`AuthoredAt` is `showedOn: PersistentObject`, so `FindQuerySurfaceAttribute` refuses it before
`ResolveSortProperty` ever runs — the sort would be silently dropped, not applied. `Date` is on the
query surface but is **not in the Map**, so sorting by it through the index is a 500. And neither
`Branch` nor `ContributedFromFork` is a defensible ordering for a commit picker.

| option | risk |
|---|---|
| **Do nothing** ← the only one with no production index change | the grids already use the index; this is a picker on an effectively read-only entity. Cost is one stray `Auto/Commits/BySha` and a scan of 804 documents |
| Bind the index and drop `sortColumns` | no 500, no reindex, but a picker in arbitrary order — worse than the current cost it removes |
| Bind the index and add `Sha` to the Map | ⛳ SP1 removed the *reindex* objection (side-by-side, 804 docs), and the `DateTimeOffset` objection does not apply to adding a `string`. `CommitIndexShapeGuardTests` still passes — it forbids `FieldStorage.Yes`, not new Map fields. This is now the only *correct* fix, and it is a deliberate production index change |

**Left undone pending a decision.** Not skipped for size: every variant either changes production index
shape or degrades the picker, and the measurement above only became available while implementing M6.

⚠️ **Synchronize will never repair this.** `ModelSynchronizer.cs:643` stamps `IndexName` only when the
index has a `[FromIndex]` projection, so `Commit` — and all six projection-less indexes — are outside
its reach permanently.

### M9 — ⛳ NEW: false statements checked into `libs/`

Three, all found while verifying the above. Each is a comment that would send the next reader the
wrong way:

1. `DefaultIndexAnalyzer.cs:133-139` — asserts the 2-arg form derives from the 1-arg. **False.**
   Delete (M6).
2. `ValueObjectCompletenessAnalyzer.cs:136-150` — says the driver "discards" a cross-project
   diagnostic. It **throws and kills the action**. Conclusion and repair are right; the wording
   understates it (PRD §4.2).
3. `SparkDevelopmentExtensions.cs:356` and `:519` — justify `--spark-verify-model` because model JSON
   "is not part of the compilation" / "no analyzer can see" it. **Both false**: `spark.targets:159`
   makes those exact files `AdditionalFiles` and `SecurityConfigurationAnalyzer` reads them. (`:262`
   carries the correct caveat "unless it is added as an `AdditionalFile`" but still reaches the wrong
   conclusion.) The *real* reason to prefer the verify gate is **staleness** — the model files are a
   synchronize output and can be one build behind, which `spark.targets:153-155` and
   `SecurityConfigurationAnalyzer.cs:25` both state correctly. Fix the wording; keep the decision.

### M10 — ⛳ NEW: delete `DatabaseAccess.QueryEntitiesWithIncludesAsync`

`libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs:348-400`, **no callers anywhere**. It is a
second, divergent interpretation of `indexName`: it calls `ProjectInto<TEntity>()` whenever an index
is named, which `QueryExecutor` does not do (it projects only when the index has a `[FromIndex]`
projection type). If revived it would project `Commit` through a non-storing index — the exact
`DateTimeOffset`-flattening hazard `CommitIndexShapeGuardTests` guards against.

---

## Carried context

- Measured RavenDB ground truth: PRD §2 and §2.1. **The Map is the gate**, and **a `Fields` change is
  side-by-side**.
- Why the capability flags are not the input: `subquery_column_filters_PRD.md` §3.10.3 — rejected with
  evidence, do not re-litigate.
- Why synchronize must not write the flags: same file, §3.9.1.
