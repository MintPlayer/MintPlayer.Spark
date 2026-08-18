# PRD — Issue #210: `[GenerateIndex]` — generate RavenDB indexes, projections and sort fields from the entity

**Issue:** [#210](https://github.com/MintPlayer/MintPlayer.Spark/issues/210) ·
**Plan:** [issue_210_plan.md](issue_210_plan.md) ·
**Guides touched:** [guide-queries-and-sorting.md](guide-queries-and-sorting.md),
[guide-translated-strings.md](guide-translated-strings.md),
[guide-reference-attributes.md](guide-reference-attributes.md)

## Origin

Applying `.Where` or `.Select` to a RavenDB collection `IQueryable` at runtime makes the server
generate an **auto-index**. To avoid that, Spark's convention is to hand-write a static index per
entity and query *that* instead of the collection, with `ProjectInto<TView>()` to fill computed
fields. `docs/guide-queries-and-sorting.md` already codifies the five-step recipe.

The recipe works. The problem is that it is entirely manual, and every step is silent when skipped.

Vidyano solved the same problem with a `[GenerateIndex]` source generator. That implementation is
proprietary and cannot be reused; this PRD specifies a clean-room equivalent in Spark's own generator
house style.

---

## The current cost, measured

Five production index/projection pairs exist in this repo, and they are strikingly uniform:

| File | Index | Projection | Notes |
|---|---|---|---|
| `Demo/DemoApp/DemoApp/Indexes/People_Overview.cs` | `People_Overview` | `VPerson` (own file) | computed `FullName`, 2 x `Search` |
| `Demo/DemoApp/DemoApp/Indexes/Companies_Overview.cs` | `Companies_Overview` | `VCompany` (own file) | 1 x `Search` |
| `Demo/DemoApp/DemoApp/Indexes/Cars_Overview.cs` | `Cars_Overview` | `VCar` (own file) | the repo's only `LoadDocument` |
| `Demo/HR/HR/Indexes/People_Overview.cs` | `People_Overview` | `VPerson` (same file) | computed `FullName`, 1 x `Search` |
| `Demo/Fleet/Fleet/Indexes/Cars_Overview.cs` | `Cars_Overview` | `VCar` (same file) | pure passthrough, no `Search` |

**No index in the repo uses `Reduce`, multi-map, `AdditionalSources`, `Analyze(...)`, per-field
`Store(...)`, spatial, suggestions or term vectors.** The entire corpus is single-map
`AbstractIndexCreationTask<T>` + `StoreAllFields(FieldStorage.Yes)` + occasional
`Index(field, FieldIndexing.Search)` / `Indexes.Add(x => x.P, FieldIndexing.Exact)`.

That uniformity is the case for generating it. It is boilerplate with a narrow shape, and the manual
version has four documented silent failure modes when a step is missed — null computed fields with a
*correct* row count, lost index-side filtering, a no-op sort, and invisibility to the model-hash check.

---

## F1 — Sort fields are the real motivation, and they are undiscoverable

A string field indexed `FieldIndexing.Search` is analyzed and tokenized. Ordering on it is therefore
meaningless, and the repo's own guide already records the collateral damage: `Search` indexes can
produce duplicate results, which `QueryExecutor` papers over with `DistinctBy(po => po.Id)`.

Any string whose value **might contain spaces** needs a second, un-analyzed companion field to sort
and filter on. In Vidyano apps the developer declares this once and the framework does the rest. In
Spark today there is no mechanism at all: `[Search]` does not exist, `[GenerateIndex]` does not exist,
and no framework code calls `.Search(...)` — `QueryExecutor.ApplySorting` reflects an `OrderBy` over
the projection property and nothing more.

So a Spark developer who indexes `Model` as `Search` and then sorts by it gets wrong results with no
error. Generating the companion field removes the failure rather than documenting it.

### The mechanism, established from ~30 production indexes

The companion field is **not** explicitly indexed. Across every hand-written index in the reference
app, `*Sort` fields receive **no `Index(...)` call at all** — only the blanket
`StoreAllFields(FieldStorage.Yes)`. That is the whole trick, and it is worth stating precisely because
it is counter-intuitive:

| Field | Indexing | Analyzer behaviour | Sorts correctly? |
|---|---|---|---|
| `Model` (marked `[Search]`) | `FieldIndexing.Search` | tokenized on whitespace — `Volkswagen Golf GTI` becomes three terms | no |
| `ModelSort` (companion) | *none declared* → `FieldIndexing.Default` | single lower-cased term, not tokenized | yes |

So the companion is created simply by **mapping the value into a second field and leaving it alone**.
Adding `FieldIndexing.Exact` to it would be wrong-by-cargo-cult: it is unnecessary, and it changes
case sensitivity relative to the default analyzer.

The reference app also queries `*Sort` fields directly for equality and prefix matching
(`x.Name == name || x.NameSort == name`, `x.FullNickNameSort.StartsWith(...)`) in several importers,
which independently confirms that the analyzed field cannot serve either.

The map expression feeding the companion is a **byte-identical duplicate** of the display expression —
including interpolated strings, `??` chains, ternaries and security masks. There is no normalization:
no lower-casing, no trimming, no accent stripping, no culture handling. Spark should match this;
inventing normalization would make the sort field disagree with the value the user sees.

## F2 — `TranslatedString` cannot be indexed or sorted at all today

`TranslatedString` is a `Dictionary<string, string>` serialized flat (`{"en":..,"fr":..,"nl":..}`) and
classified as a first-class scalar `dataType: "TranslatedString"` in the model. Fleet's
`Car.Description` is a live example.

RavenDB cannot sort or search a dictionary field usefully. What is needed is one flattened property
per language — `Description_en`, `Description_fr`, `Description_nl` — plus a sort companion for each.
Writing that by hand is six properties and six index lines per translated field, per entity, and it has
to be revisited whenever a language is added.

**Constraint discovered:** the supported-language set lives in `App_Data/culture.json`, read at
runtime by the `CultureLoader` DI singleton. A source generator has no DI and cannot see it. The
language list must therefore reach the generator as an **AdditionalFile**, which is the pattern
`PersistentObjectNamesGenerator` already uses for `App_Data/Model/*.json`. This is new wiring and is
the single biggest piece of plumbing in this issue.

## F3 — The generator must run in the app project, not the entity library

Entities live in `*.Library` projects; indexes and projections live in the app project. The
`.Library` projects do not reference the generator.

Three options were considered:

1. **A new leaner generator package for libraries** — rejected. Another csproj, PackageId, version to
   keep in lockstep, and `SparkModelSymbols`/`Models` would need sharing via a third project. It also
   still emits into the library, so it inherits the defect of option 2.
2. **Reference the existing generator from each `*.Library`** — rejected. It emits the index into the
   *library* assembly, which makes `spark.AddIndexesFrom(...)` mandatory. No production module calls
   that today (only tests), and forgetting it fails in the four silent ways above. It also obliges
   every downstream consumer to add an analyzer reference to their own entity libraries.
3. **Keep the generator referenced only from the app project and read entities from referenced
   assemblies via metadata symbols** — **chosen.** `[GenerateIndex]` sits on the entity in
   `Fleet.Library`; the generated index and view land in `Fleet`, exactly where hand-written ones live
   today, so `SparkMiddleware.CreateSparkIndexes` and `IndexCreation.CreateIndexes` keep working with
   no new wiring and no consumer csproj changes.

There is house-style precedent for reading referenced-assembly attributes
(`HostTranslationsAggregatorGenerator`), and the Vidyano generator contributes context members for
entities in referenced assemblies by the same symbol-only path.

The cost is that a metadata walk cannot use `CreateSyntaxProvider`'s incremental caching. Mitigations:
filter to assemblies that reference `MintPlayer.Spark.Abstractions`, and use the `ICompilationCache`
provider that `IncrementalGenerator.Initialize` already supplies for exactly this purpose. **Spike S1
settles whether the cost is acceptable before any of the feature is built.**

## F4 — Generated code is invisible to the analyzers that currently protect the pattern

`ProjectionPropertyAnalyzer` declares `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)`.
SPARK001 (projection property type must match the entity) and SPARK002 (missing `[Reference]`) will
therefore **not** run on a generated pair.

The safety net disappears at exactly the point where we start emitting code, so the generator itself
must guarantee type fidelity and carry `[Reference]` / `[LookupReference]` onto projection properties.
This raises the bar on tests: correctness cannot be delegated to the analyzer.

## F5 — Silence is the dominant failure mode of the design being copied

Two independent swallowing layers would combine:

- Spark's `Producer.Produce` wraps `ProduceSource` in a catch-all that discards the exception. A
  throwing producer emits **nothing**, with no build error.
- The Vidyano generator's mapping returns `null` on failure, so a mis-modelled entity yields no index,
  no view and no diagnostic.

Stacked, a modelling mistake would produce a silently missing index — which then degrades into an
auto-index at runtime, i.e. precisely the problem `[GenerateIndex]` exists to prevent, with no signal
anywhere. **Every abort path in this generator must report a diagnostic instead of returning null.**

## F6 — One index per collection type is a hard ceiling

`IndexRegistry` keys registrations by collection type in a dictionary and silently skips duplicates.
At most one *registered* index per entity is therefore possible, and a duplicate index name produces no
diagnostic today. The generator must diagnose duplicates rather than emit a second index that is
silently ignored.

## F7 — Everything generated moves the model hash

A generated index/projection changes `EntityTypeDefinition.QueryType`, `IndexName` and per-attribute
`ShowedOn` in `App_Data/Model/*.json`, and therefore `App_Data/modelHashes.json`.
`VerifySparkModelHash` refuses startup on drift and CI's `--spark-verify-model` exits 3.

Ordering is not a problem — the build-time commands reflect over the *compiled* assembly, so generated
types are visible to synchronize provided the generator runs first in the build. But the model JSON and
hashes must be re-synchronized and committed as part of this work.

## F8 — The redirect is transparent, so this issue reaches into `QueryExecutor`

This was the open question in the design, and the reference app settles it with data rather than
inference. Query definitions name the **display** field, never the companion:

```
Car.json        "SortOptions": "OrderDate Desc"
CarChange.json  "SortOptions": "CreatedOn Desc, ChangedColumn"
```

Nothing in the app names a `*Sort` field for ordering. The redirect happens in the framework, driven by
per-attribute metadata persisted in the model: searchable attributes carry
`"SortExpression": "<Field>Sort"`, with 100+ occurrences across the reference app's committed model
JSON. An attribute with no `[Search]` has no `SortExpression`.

**Consequence for scope.** `[GenerateIndex]` is therefore not a self-contained generator. Three
non-generator changes come with it:

1. `EntityAttributeDefinition` gains a `SortExpression` property, emitted by `ModelSynchronizer`.
2. `QueryExecutor.ApplySorting` must honour it — sorting by `Model` orders by `ModelSort` when a
   `SortExpression` is present, transparently.
3. The property must be **mutable at runtime**, because the reference app uses exactly that as an
   escape hatch (`args.PersistentObject[...].SortExpression = null` to suppress a redirect for one
   action). Spark should expose the same.

Without item 2 the generated companions are dead weight: correctly indexed, correctly stored, and never
used, because `ApplySorting` would keep ordering by the analyzed field. That is the difference between
this issue fixing sorting and merely preparing to fix it.

## F9 — Per-language sort has no prior art; Spark would be first

The reference implementation has **no multilingual sort mechanism at all**. Its multilingual columns
are `[KeyValueList]` lookups resolved client-side and they receive no sort companion; no index anywhere
feeds a sort field from a translated value.

Issue #210 nonetheless asks for `Name_nlSort` / `Name_frSort` / `Name_enSort`, and F2 explains why
Spark needs it (`TranslatedString` is a real indexed scalar here, which is not true over there). This
is worth stating plainly: **R10 is a Spark design decision, not a port.** It carries the most risk of
the requirements and is deliberately sequenced last in the plan so the proven parts ship first.

---

## Requirements

### The attribute surface

- **R1** `GenerateIndexAttribute` in `MintPlayer.Spark.Abstractions`, `AttributeTargets.Class`,
  `AllowMultiple = false`. **One constructor, `()`**, for an entity index. Named properties:
  `IndexName`, `ViewName`, `Description`. Nothing else — the Vidyano surface offers several redundant
  ways to say the same thing and that is not carried over. The fan-out overload
  `(Type root, params string[] paths)` is deliberately **not** declared yet (N6): shipping a
  constructor that silently generates nothing is exactly the failure mode F5 exists to prevent.
- **R2** `SearchAttribute` in `MintPlayer.Spark.Abstractions`, `AttributeTargets.Property`. Marks a
  `string` / `TranslatedString` property as full-text searchable *and* requests its sort companion.
- **R3** `IgnoreForIndexAttribute`, `AttributeTargets.Property` — exclude a property from the generated
  index/view without affecting the Spark model (distinct from `[IgnoreProperty]`, which excludes it
  from the model everywhere).
- **R4** Attributes are matched by **fully-qualified string**, never symbol identity, per house style
  (`SparkModelSymbols`).

### Generated artifacts

- **R5** For `[GenerateIndex]` on entity `Car`, emit an index class
  `Cars_Overview : AbstractIndexCreationTask<Car>` in `{EntityNamespace}.Indexes`, containing `Map`,
  `StoreAllFields(FieldStorage.Yes)`, the `Index(...)` calls of R8/R9, and a call to a
  `partial void OnInitialize()` declared on the same class as the sanctioned extension point.
- **R6** Emit a `[FromIndex(typeof(Cars_Overview))] public partial class VCar` carrying one property
  per included field. `Id` is declared on the view and, for an entity index, not assigned in the `Map`
  (Raven supplies it).
- **R7** Both emitted types are `partial`, so a developer can extend either by hand.
- **R8** A property marked `[Search]` gets `Index(nameof(V.Field), FieldIndexing.Search)` on the base
  field, plus a `{Field}Sort` companion property, always decorated `[IgnoreProperty]`. **Suffix is
  `Sort` with no separator** — `NameSort`, `Name_nlSort`. The companion gets **no `Index(...)` call**
  (F1): default indexing is what makes it sortable. The companion is fed a byte-identical copy of the
  base field's map expression, with no normalization.
- **R8a** `[Search]` is valid on `string`, `string[]` / `IEnumerable<string>`, and `TranslatedString`.
  It composes with `[IgnoreProperty]` (indexed and searchable, but absent from the model).
- **R9** `DateTimeOffset` / `DateTimeOffset?` properties get `Index(field, FieldIndexing.Exact)` on the
  base field and a sort companion automatically, with no attribute. Reference-typed fields never get
  one.
- **R10** For a `TranslatedString` property `Description`, emit one flattened `string?` property per
  language from `culture.json` (`Description_en`, `Description_fr`, `Description_nl`), each mapped from
  the dictionary, and — when `[Search]` is present — a `{Field}_{lang}Sort` companion per language.
  Novel design, no prior art (F9); sequenced last.
- **R11** The generated view must **never** emit a property whose name equals an entity property but
  whose type differs. For a `TranslatedString` entity property this means no `string?` property named
  `Description`. SPARK001 would be an error, and although it does not analyze generated code per F4,
  the model merge in `ModelSynchronizer` validates `dataType` compatibility on both sides.
- **R12** `[Reference(typeof(T))]` and `[LookupReference(typeof(T))]` on an entity property are copied
  onto the corresponding view property. `[Reference]` id properties are marked `[IgnoreProperty]` on
  the view, matching the hand-written convention.
- **R13** Property discovery walks the type hierarchy (Spark's `GetAllProperties` helper), rather than
  silently dropping inherited members.
- **R14** The property filter is exactly the Roslyn twin of
  `ReflectedTypeExtensions.IsSparkModelProperty` — skip `Id`, non-readable, indexers, and
  `[IgnoreProperty]` — plus `[IgnoreForIndex]`. Divergence here makes the index and the model hash
  disagree.

### Where it runs

- **R15** The generator runs in the project that references it (the app), and discovers
  `[GenerateIndex]` entities from **both** its own syntax and referenced assembly metadata, filtered to
  assemblies referencing `MintPlayer.Spark.Abstractions`, memoized through the supplied
  `ICompilationCache`.
- **R16** Feature-gated both ways per house style: `GetTypeByMetadataName(...) != null` in the pipeline
  **and** an early `return` in the producer. No Spark reference, no emitted source.
- **R17** The language list reaches the generator as an `AdditionalFile` (`App_Data/culture.json`),
  wired through the shipped `spark.targets` so consumers get it without hand-editing a csproj. Absent
  file falls back to the single default language `en`, matching `CultureLoader`.

### Sort redirection (F8) — outside the generator

- **R21** `EntityAttributeDefinition` gains a nullable `SortExpression` string, emitted into
  `App_Data/Model/*.json` by `ModelSynchronizer` for any attribute whose generated view has a sort
  companion. Absent for attributes without one, so existing model files stay byte-identical where
  nothing is searchable.
- **R22** `QueryExecutor.ApplySorting` honours `SortExpression`: a request to sort by `Model` orders by
  `ModelSort`. Callers, query JSON (`sortBy`) and the `?sortBy=` runtime override all keep naming the
  display field. A `SortExpression` naming a property absent from the projection must fall back to the
  display field rather than throwing.
- **R23** `SortExpression` is settable at runtime on a `PersistentObjectAttribute`, so an action can
  suppress or override the redirect for one request.

### Diagnostics — no silent aborts (F5)

- **R18** Every abort path reports a diagnostic. Minimum set: non-partial existing index or view class;
  unresolvable fan-out path segment; last path segment's element type is not the decorated class;
  duplicate index name / a second index for one collection type (F6); `[Search]` on a type that is
  neither `string` nor `TranslatedString`; `[GenerateIndex]` on a type with no indexable property.
- **R19** New ids continue the `SPARK0nn` analyzer series, declared in a `.Rules.cs` partial per house
  style.
- **R20** The producer must not rely on exceptions for control flow, given that `Producer.Produce`
  discards them.

### Non-goals

- **N1** Organic/full-text search execution. `[Search]` makes a field *indexed* for search and gives it
  a sort companion; wiring RavenDB's `.Search(...)` into `QueryExecutor` is a **separate future
  ticket**. Nothing in the framework calls `.Search(...)` today and this issue does not change that.
- **N2** `Reduce` / map-reduce, multi-map, `AdditionalSources`, spatial, suggestions, term vectors.
  Nothing in the repo uses them. Noted for whenever map-reduce does arrive: sort companions have to be
  propagated through the grouping explicitly (`ModelSort = g.Max(x => x.ModelSort)`), not just through
  the map — the reference app does this by hand in its one map-reduce index.
- **N3** Stringly-typed expression escape hatches (`IndexCustomProperty` / `Variable` / `Where`).
  Rejected in favour of `OnInitialize()` and hand-written partials.
- **N4** Emitting `SparkContext` members. The demo contexts are not `partial`, so this would be a
  breaking requirement on consumers; DemoApp already omits `V*` roots entirely. Revisit separately.
- **N5** Migrating the five existing hand-written index pairs. One demo entity is converted as a
  proof; a full migration is follow-up, since renaming an index re-indexes the database.
- **N6** Collection fan-out (`[GenerateIndex(typeof(Country), nameof(Country.Cities))]`), the
  `...ObjectId` composite keys it needs, and the parented `CustomQueryArgs` query methods that go with
  it. Issue #210 does not ask for it, it is the largest and least-proven part of the reference design,
  and it interacts with `[ValueObject]` / splitted collections. The constructor overload is withheld
  along with the feature (R1) so there is no dead surface.
- **N7** A client-side editor/renderer for `dataType: "TranslatedString"`. None exists today — the type
  is used for labels and metadata, but no `po-form` or renderer keys on that data type, so a translated
  *value* has no editor. Out of scope here, but it means R10's generated per-language fields will be
  queryable and sortable before they are editable.
