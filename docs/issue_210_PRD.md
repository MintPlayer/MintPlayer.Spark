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
meaningless.

> **Correction to an existing repo claim.** `docs/guide-queries-and-sorting.md` states that
> `FieldIndexing.Search` indexes can produce duplicate results, which is why `QueryExecutor` applies
> `DistinctBy(po => po.Id)`. Spike S6 could not reproduce that: every query over a `Search`-indexed field,
> including `order by` on it, returned exactly one row per document. Duplicates come from **fan-out** maps
> (`SelectMany` over a collection), not from the analyzer. The `DistinctBy` is still correct — it just
> guards a different hazard than the docs say. W10 corrects the guide.

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

**Measured in S6, not assumed.** Indexing the same value three ways and dumping the stored terms:

| Field | Declared as | Terms stored |
|---|---|---|
| `Model` | `FieldIndexing.Search` | 17 terms — `<volkswagen> <golf> <gti> <audi> <a4> …` |
| `ModelSort` | *undeclared* | 10 terms, lower-cased, whitespace preserved — `<volkswagen golf gti>` |
| `ModelExact` | `FieldIndexing.Exact` | 10 terms, case preserved — `<Volkswagen Golf GTI>` |

Ordering by `Model` is garbage exactly as predicted. Ordering by the undeclared companion is correct and
**case-insensitive** (`alfa romeo` < `Audi A4` < `audi a4 lowercase`, and `Zeta One` < `ZZ Top`).

And adding `FieldIndexing.Exact` to the companion is not merely redundant — it is a **behaviour
regression on both sort and filter**. Ordering becomes case-sensitive ordinal, so every capitalised value
sorts before every lowercase one and `ZZ Top` overtakes `Zeta One`; and equality changes silently, with
`ModelExact = 'audi a4'` returning **0 rows** where `ModelSort = 'Audi A4'` returns 1. Leaving the
companion undeclared is genuinely correct, not cargo-cult.

One consequence worth knowing: RavenDB indexes sentinel terms `NULL_VALUE` and `EMPTY_STRING` and sorts on
those literals, so on a lower-cased companion **nulls and empties sort before every real value**. If a UI
wants them last, that must be arranged explicitly; it is not free.

### Why companions are scoped to `[Search]` and not given to every string

This is the first question the design attracts, so it is answered here rather than left to be re-derived:
**a plain `string` field already sorts correctly.** Measured — same fixture, same space-containing values, a field
with no `[Search]`, no `Index(...)` call and no companion:

```
"alfa romeo spider", "Audi A4", "Volkswagen Golf GTI", "Zeta One", "ZZ Top"
```

Byte-identical to ordering the analyzed field through its companion. Pinned by
`SortCompanionRedirectTests.A_plain_string_field_with_no_companion_sorts_correctly`.

Tokenization is a per-field RavenDB indexing mode, not a property of strings:

| `FieldIndexing` | Analyzer | Terms for `"Volkswagen Golf GTI"` | Sortable | `==` |
|---|---|---|---|---|
| *undeclared* → `Default` | `LowerCaseKeywordAnalyzer` | one: `volkswagen golf gti` | yes, case-insensitive | yes |
| `Search` | `StandardAnalyzer` | **three**: `volkswagen`, `golf`, `gti` | **no** | no — full-text match |
| `Exact` | `KeywordAnalyzer` | one: `Volkswagen Golf GTI` | yes, case-sensitive ordinal | case-sensitively only |
| `No` | — | none | no | no |

(The first three rows' term counts are measured; the analyzer names are RavenDB's documented mapping.)

So nothing is broken until a field is declared `Search`, and the companion repairs *that specific damage*. Giving
every string one would double the indexed field count — larger indexes, more re-indexing work — to duplicate
ordering that is already correct. It is also what the reference corpus does: across 398 generated string
properties, `has *Sort` ⟺ `Index(field, Search)`, with zero exceptions.

**The follow-on worry is also handled:** adding `[Search]` later emits the companion *and* the sort redirect picks
it up, because one attribute drives both. There is no window in which a field is analyzed but unsortable.

Two ways a field can still be tokenized without `[Search]`, both outside what the generator emits:

- a hand-written `Index(nameof(V.X), FieldIndexing.Search)` in the developer's own constructor — covered by
  `SPARK005`, which flags the missing companion whoever wrote the call;
- `Analyze(field, "SomeAnalyzer")` or a custom analyzer through `AdditionalSources`, e.g. added in
  `OnInitialize()`. **Not covered by SPARK005**, which only recognises the `Search`/`Exact` forms. A known gap,
  and acceptable: naming an analyzer by hand is a deliberate act by someone who already knows what analysis does.

Note `DateTimeOffset` gets `Exact`, never `Search`, so it is never tokenized. Its companion exists for a different
reason: `Exact` is chosen for precise range and equality matching, and the companion keeps a stable sort key
independent of that choice.

The reference app also queries `*Sort` fields directly for equality and prefix matching
(`x.Name == name || x.NameSort == name`, `x.FullNickNameSort.StartsWith(...)`) in several importers,
which independently confirms that the analyzed field cannot serve either.

The map expression feeding the companion is a **byte-identical duplicate** of the display expression —
including interpolated strings, `??` chains, ternaries and security masks. There is no normalization:
no lower-casing, no trimming, no accent stripping, no culture handling. Spark should match this;
inventing normalization would make the sort field disagree with the value the user sees.

## F2 — `TranslatedString` is indexable, and the obvious worry about it was unfounded

`TranslatedString` is a `Dictionary<string, string>` classified as a first-class scalar
`dataType: "TranslatedString"` in the model. Fleet's `Car.Description` is a live example.

**Corrected by spike S6.** The initial reading of this PRD was that `TranslatedString` persists *flat*
(`{"en":..,"nl":..}`) because `TranslatedStringJsonConverter` emits that shape, which would put the CLR
path and the stored JSON path in conflict and make a generated index field silently null. That is wrong:
the converter is a **System.Text.Json** converter and therefore applies only at the HTTP /
`PersistentObject` layer. RavenDB persists through **Newtonsoft**, where only
`ColorNewtonsoftJsonConverter` is registered. Measured raw JSON from the server:

```json
{"Id":"SpikeCars/1-A","LicensePlate":"1-AAA-111",
 "Description":{"Translations":{"en":"Alpha with spaces","nl":"Zebra met spaties"}}}
```

The stored path is `Description.Translations.nl`, so the CLR expression `x.Description.Translations["nl"]`
is exactly correct. RavenDB's server-side `DynamicBlittableJson` supports the dictionary indexer natively
and stores the map verbatim without rewriting it, so a strongly-typed map expression is all that is
needed — no raw index definition, no `AdditionalSources`, no dynamic fields.

What is still needed is one property per language — `Description_en`, `Description_fr`, `Description_nl` —
plus a sort companion for each. By hand that is six properties and six map lines per translated field, per
entity, revisited whenever a language is added.

Measured robustness: a document whose `Translations` lacks the key, and one whose `Description` is null,
both index to `null` — no `KeyNotFoundException`, no index error, index state Normal. Space-containing
values sort correctly, and both `==` and `StartsWith` work against the generated field.

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

The cost is that a metadata walk cannot use `CreateSyntaxProvider`'s incremental caching — there is no
syntax to key on. The scan re-runs whenever the compilation changes; what protects downstream work is value
comparison on the **result**, which the generated comparers already provide.

**Correction:** an earlier draft said to memoize this through the `ICompilationCache` that
`IncrementalGenerator.Initialize` supplies, "for exactly this purpose". That is wrong — every `GetOrCreate`
overload on it is constrained to `T : class, IEqualityComparer`, so it caches *comparers*, not data. The
usable mitigation is the filter: only walk assemblies that reference `MintPlayer.Spark.Abstractions` at all,
which excludes the BCL and every unrelated package.

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

The reference implementation has **no multilingual sort mechanism at all**, and this is now measured
rather than inferred: it has 76 `TranslatedString` model attributes and **every one has
`SortExpression: null`** and no sort term. No `TranslatedString`-typed property is ever indexed there.
The generator binary contains no per-language suffix logic for indexes whatsoever. That app collapses a
translated value to one language at *render* time instead.

Issue #210 nonetheless asks for `Name_nlSort` / `Name_frSort` / `Name_enSort`, and F2 explains why Spark
has the problem the reference app does not: `TranslatedString` is a first-class indexed scalar here, so
"sort a translated column" is a question Spark can be asked and that app cannot.

Two honest consequences:

- **R10 is a Spark design decision, not a port.** It carries the most risk of any requirement, has no
  implementation to check against, and is sequenced last so the proven parts ship first.
- The investigation's own recommendation was *don't invent per-language sort fields*. That advice is
  sound for a port and wrong for this issue, since the issue asks for the feature by name — but it is
  recorded here because it is the strongest argument for splitting R10 into a follow-up if the rest
  proves large enough on its own.

## F10 — Hand-written indexes need an analyzer, or they drift

The reference implementation ships Roslyn analyzers *and a code fix* specifically for hand-written
indexes — a missing-sort-property analyzer with an "Add Sort property" lightbulb. The investigation's
judgement was blunt: that code fix is the only thing keeping ~40 hand-written indexes consistent.

Spark has five hand-written pairs today and will keep them (N5), so the same drift applies. Combined
with F4 — generated code is excluded from analysis, so the analyzer only ever sees hand-written code —
an analyzer and the generator are complements, not alternatives: the generator guarantees correctness
where it writes, the analyzer guards everywhere it does not. There is repo precedent for exactly this
reasoning in SPARK004.

## F11 — Analyzer suppression falls out for free, but the diagnostic location is load-bearing

Spiked and proven, both in the test harness and in a real `dotnet build`.

`GeneratedCodeAnalysisFlags.None` filters **which declarations the driver invokes actions on**, and
suppresses diagnostics whose *location* is in a generated file. It does **not** filter the symbol model:
`INamedTypeSymbol.GetMembers()` returns the complete merged partial, generated halves included. Roslyn
treats a symbol as generated only when *all* its declarations are in generated code, so a partial type
with at least one hand-written declaration is analyzed normally.

Measured, with the generated file carrying both an `<auto-generated/>` header and a `.g.cs` hint name:

```
=== BEFORE GENERATOR ===  Proj => Name
=== AFTER GENERATOR  ===  Proj => Name,NameSort
```

And in a real build, generators run before analyzers *inside a single `csc` invocation* — not an MSBuild
ordering knob, so there is no race.

Two consequences:

- **No marker type and no `.editorconfig` switch are needed.** A generated companion silently satisfies
  the analyzer, which is exactly the requested behaviour.
- **The diagnostic must be anchored on the hand-written property's location.** Falling back to
  `Location.None` or a generated location would cause it to be *silently dropped*.
  `ProjectionPropertyAnalyzer` already does this correctly and is the pattern to copy.

IDE caveat, flagged honestly and not empirically tested: generator re-runs are debounced, so the squiggle
can lag a beat behind adding a searchable property, and a stale companion can briefly keep it suppressed.
Transient, clears on the next generator pass, and does not affect build correctness. The mitigation is
generator hygiene — a cheap incremental pipeline with properly compared models — not analyzer hygiene.

## F12 — A second lean generator project has a working precedent in this repo

`libs/all_features/MintPlayer.Spark.AllFeatures.SourceGenerators` is already a second, lean,
single-generator project (`SparkFullGenerator` plus two models), loaded side by side with the main
generator as an analyzer in every demo app and in the test host. Its csproj is byte-identical to the main
one except `PackageId` and `Description`.

So the lean library generator needs nothing invented. Confirmed details that are easy to get wrong:

- **`GeneratePathProperty="true"` is load-bearing**, not decoration — the Tools targets hard-error
  without it.
- **The Roslyn `PackageReference Update` pins are load-bearing** — the Tools props *Include* Roslyn
  4.14.0, and the `Update` lines raise it to 5.3.0. Omit them and the lean generator silently compiles
  against a different Roslyn than the test harness hosts.
- **Analyzer packaging is 100% inherited** from the Tools/ValueComparerGenerator shipped props; neither
  existing generator csproj has a single `PackagePath="analyzers/…"` item.
- **No `ComparerRegistry` collision.** It is keyed on the runtime `Type` object, so same-named models in
  two assemblies are distinct keys; registration is `TryAdd`, so a true duplicate is a silent no-op; and
  `ValueComparerGenerator` emits no `[ModuleInitializer]` per model. Two generator assemblies already
  coexist today.
- **`spark.targets`' SPARK001 hard-error does not reach an in-repo `*.Library`** — it is packed only as
  `buildTransitive`, and `ProjectReference` does not import a referenced project's build assets. All four
  `*.Library` projects build clean today with no generator reference. Externally it applies only to
  projects with `MintPlayer.Spark` in their dependency closure, which an entity library referencing just
  `Abstractions` does not have.
- Shared code: **duplicate the 15-line `SparkModelSymbols`** rather than linking it. That is what the
  repo already does (AllFeatures shares nothing), and the file's own doc comment already describes itself
  as a hand-maintained restatement of the runtime rules. There is zero precedent for `Compile Include`
  links, `.projitems` or shared projects anywhere in the tree.
- If the lean generator should attach automatically for in-repo consumers, copy
  `libs/all_features/MintPlayer.Spark.AllFeatures/Targets/spark-allfeatures.targets`, which injects
  analyzer `ProjectReference`s itself precisely because they do not flow transitively.

## F13 — Generated per-language fields are coupled to the absence of a Newtonsoft converter

This follows directly from F2 and is the most dangerous thing S6 turned up, because it is a trap for a
future maintainer rather than a problem today.

`TranslatedString` persists nested (`Description.Translations.nl`) purely because no Newtonsoft converter
is registered for it. If someone later adds one — and there is an entirely reasonable-sounding motive,
"make persistence consistent with the API shape" — then **every generated per-language index field
silently becomes null.** Measured: no deploy failure, no index error, index state Normal, correct row
counts, empty values.

That is the same silent-null class as R10a and the four failure modes in the Origin section, and it cannot
be caught by any gate that exists: the model hash does not change, `--spark-verify-model` passes, and the
index reports healthy.

Mitigations, both cheap:

- **R28** A comment on `TranslatedStringJsonConverter` recording that it is System.Text.Json only, that
  RavenDB persistence deliberately uses the nested Newtonsoft shape, and that generated index fields depend
  on that.
- **R29** A test that asserts the **stored** RavenDB JSON for a `TranslatedString` is nested, so adding a
  Newtonsoft converter fails a test instead of silently emptying indexes.

One more measured detail, found while writing R29's behavioural half: **RavenDB drops a map entry that produces
no terms at all.** An index whose only field is a null translation loses the document from its results
entirely — not a null value, an absent row. It does not affect a generated index, which always maps the entity's
other properties too, but it makes a single-field index over an optional translation a trap worth knowing about.

---

## F14 — Search is already wired end-to-end; the server side is the only broken half

The ticket was written as "nothing in the framework calls `.Search(...)` yet", which is true but describes a much
smaller gap than the one that exists. Search is **already plumbed from the search box to the database call**:

| layer | file | state |
|---|---|---|
| search input, clear button, result count | `ng-spark/query-list/src/spark-query-list.component.html:41-53` | **exists** |
| `searchTerm`, `onSearchChange()`, `clearSearch()` | `spark-query-list.component.ts:71,278-302` | **exists** |
| sends `?search=` | `ng-spark/services/src/spark.service.ts:57,69` | **exists** |
| typed .NET client | `libs/client/MintPlayer.Spark.Client/SparkClient.cs:243-276` | **exists** |
| reads `?search=` | `Endpoints/Queries/Execute.cs:89` | **exists** |
| `IQueryExecutor.ExecuteQueryAsync(..., string? search)` | `Services/QueryExecutor.cs:15` | **exists** |
| the actual filtering | `QueryExecutor.cs:46-59` | **in-memory `string.Contains` after materializing the whole collection** |

So W14 does not add a feature. It **replaces a working but pathological implementation**, and two consequences
follow that the original framing missed.

**First, R42 dissolves.** No Angular change is needed; this stays a server-only release.

**Second, there is existing behaviour to preserve, and it is *wider* than `.Search()` naturally reaches.**
The in-memory filter matches:

```csharp
po.Name.Contains(term, OrdinalIgnoreCase)
|| po.Breadcrumb.Contains(term, OrdinalIgnoreCase)
|| po.Attributes.Any(attr => (attr.Breadcrumb ?? attr.Value?.ToString()).Contains(term, OrdinalIgnoreCase))
```

Two properties of that are not free to reproduce:

- **It is an infix substring match.** `olkswag` finds `Volkswagen Golf GTI` today. Term-based search does not do
  that by default — see F15, which measures how close it can get.
- **It matches `Breadcrumb` — resolved reference display text**, computed after materialization by
  `BreadcrumbResolver`. That text is **not an index term** and a pushdown cannot see it. Searching a car by its
  owner's name works today via the breadcrumb and would stop working, unless the app denormalizes that text into
  the index. The demos already do exactly that (`VCar.OwnerFullName`), which is why the gap is survivable — but
  it is a real narrowing and must be documented, not discovered.

Two existing tests pin the current semantics and will flag any change:
`tests/MintPlayer.Spark.Tests/Services/QueryExecutorIntegrationTests.cs:162,176` and
`tests/.../Endpoints/Queries/ExecuteQueryEndpointTests.cs:83-90` (`"alice"`, `"SMITH"`, `"ALICE"`).

Also worth knowing before touching it: **paging happens after the search filter** (`QueryExecutor.cs:61-64`), so
`TotalRecords` is search-aware today. A pushdown has to keep it that way (R43), or every paged grid silently
reports the unfiltered count.

---

## F15 — Substring parity is achievable, and `[Search]` turns out not to be required for it

This is the finding that reshaped W14. Measured on RavenDB.Client **7.2.5**, against a real server, on **both**
search engines (Corax — the default here — and a twin index forced to Lucene). **They agreed on every result.**

Wildcards, on an analyzed (`FieldIndexing.Search`) field containing `"Volkswagen Golf GTI"`:

| term | measured |
|---|---|
| `volks*` trailing | **match** |
| `*swagen` leading | **match** — RavenDB's historic leading-wildcard restriction does not bite on 7.2.5, on either engine |
| `*olkswag*` both ends | **match** |
| `*olf*` token-internal | **match** |
| `volks* gti` + `SearchOperator.And` | **match**, precisely one document |
| `VOLKS*`, `*OLKSWAG*` | **match** — the term is lower-cased for you; the framework need not pre-lower-case |
| `gol?` | **no match** — `?` is unsupported |
| `go*lf` mid-word `*` | **no match** — only leading/trailing positions work |
| bare `*` | **matches everything** (all 5 docs) — needs guarding |

So substring parity with today's `Contains` **is** reachable: wrap each whitespace-separated word as `*word*` and
pass `SearchOperator.And`.

**A gap that looked irreducible and is not.** The spike measured `*olf gt*` — one wrapped token containing a
space — against `"Volkswagen Golf GTI"` → **0 rows**, and concluded that a substring spanning a whitespace
boundary could not match. That conclusion does not survive the implemented term shape: because each word is
wrapped *separately*, `*olf* *gt*` matches, since the two wildcard words are matched independently rather than as
one adjacent run. Pinned by `SearchPushdownTests.A_substring_spanning_a_space_still_matches`.

The real difference is the opposite of a gap: the pushdown is **more permissive** than the `Contains` it replaces,
because the words need not be adjacent or in order. `Contains("gti golf")` did not match `"Volkswagen Golf GTI"`;
the search does. That is a widening, so no caller loses a result — but it is a genuine behaviour change and is
pinned deliberately (`Words_need_not_be_adjacent_or_in_order`) rather than left as an accident.

### The surprise: wildcards work on fields that were never declared searchable

`.Search()` against a plain string field — default `LowerCaseKeywordAnalyzer`, no `[Search]`, no `Index(...)` call:

- **bare-word search is dead:** `search(Trim, "volkswagen")` → **0**. The whole value is one term, so no query
  token equals it. Even searching the complete value verbatim returns 0, because the *query* is tokenized while
  the *stored term* is not.
- **wildcards match the whole value:** `volkswagen*` → 3, `*golf*` → 1, `*olkswag*` → 3.

**Therefore substring search does not require an analyzed field at all**, and the premise that `[Search]` gates
searchability is wrong. `[Search]` still earns its place — it enables token-level matching, relevance ranking and
analyzer behaviour, and it forces the sort companion (F1) — but W14 does not depend on it. That inverts R38: the
question is no longer "how does the runtime find `[Search]` fields" but "should search be scoped to them at all".

`FieldIndexing.Exact` is the one shape to keep out. Measured: it is **case-sensitive to the search term** —
`*GOL*` matched, `*gol*` returned 0, and plain `"vw"` returned 0 while `VW*` returned 3. Silent, direction-
dependent mismatching. In this codebase `Exact` is only ever applied to `DateTimeOffset` (W4), which is excluded
by the type test anyway, but a hand-written index can apply it to a string.

Nothing throws in any of these cases and nothing errors on a field absent from the index (`search(id(), ...)` →
0 rows). So excluding non-analyzed fields is **not** a correctness requirement — it is purely a semantics choice.

Two more measured details that matter for the implementation:

- **No duplicate rows** on a single-map index, even when one document matches several tokens or several OR-ed
  legs. The existing `DistinctBy(po => po.Id)` is enough; nothing extra is needed. (Multi-map/fan-out untested.)
- **There is no required LINQ call order.** `Search` before or after `OrderBy`, `ProjectInto`, `Skip`/`Take` all
  emit identical RQL and identical results — the provider collects clauses rather than composing sequentially.
  The one position that matters is `Where`, for the reason in F16.

---

## F16 — `SearchOptions` leaks forward onto the next `Where`, and that is a filter-bypass hazard

Measured, and the most dangerous thing found in this investigation. Passing an explicit `SearchOptions.Or` does
not just join the search legs — **it leaks onto whatever clause follows**:

```
.Search(Model, term, Or).Search(Desc, term, Or).Where(v => v.Code == "VW UP")
  → where search(Model,$p0) or search(Description,$p1) or Code = $p2        4 rows
```

The trailing `Where` was **OR-ed in, not AND-ed**. Put the `Where` first and precedence breaks the other way:

```
.Where(Code == "VW UP").Search(Model, Or).Search(Desc, Or)
  → where (Code = $p0) and search(Model,$p1) or search(Description,$p2)     2 rows — leaked a non-matching row
```

Spark composes row-level security as exactly such a `Where`, immediately before where search would go
(`RowSecurity.ComposeRowFilterAsync`, called at `QueryExecutor.cs:168`). An explicit `Or` there would turn a
security filter into an alternative — **a row-security bypass, silent, with plausible-looking results.**

**The default `SearchOptions.Guess` is the safe one, and it is also exactly what a multi-field OR wants.**
Measured: it parenthesizes the consecutive `Search` group and ANDs it with neighbours in *both* directions:

```
.Where(Code == "VW UP").Search(Model).Search(Desc)
  → where (Code = $p0) and (search(Model,$p1) or search(Description,$p2))   1 row  ✔
.Search(Model).Search(Desc).Search(Trim).Where(Code == "VW UP")
  → where (search(...) or search(...) or search(...)) and (Code = $p3)      1 row  ✔
```

Mixing explicit options is additionally *non-deterministic in intent*: `(And, Or)` and `(Or, And)` both rendered
`or`. The per-call option does not map to a determinable clause boundary.

So R44 is a hard requirement, not a style preference: **never pass `SearchOptions` explicitly.** The original
plan's instruction to OR the legs with `SearchOptions.Or` was wrong and would have introduced the bypass.

Empty and null terms, measured:

| input | behaviour |
|---|---|
| `""` or `"   "` | **returns nothing** — 0 of 5 rows. Does not throw, does not no-op |
| `(string)null` | **throws `ArgumentException`** at query-build time (and a bare `null` literal is a CS0121 overload ambiguity) |
| `Array.Empty<string>()` | **throws `ArgumentException`**: "Cannot search on empty searchTerms array" |

An empty term silently narrowing every grid to zero rows is the worst of those, so the guard in R40 is load-
bearing rather than defensive.

---

## Requirements

### The attribute surface

- **R1** `GenerateIndexAttribute` in `MintPlayer.Spark.Abstractions`, `AttributeTargets.Class`,
  `AllowMultiple = false`. **One constructor, `()`**, for an entity index. Named properties:
  `IndexName`, `IndexEntityName`, `Description`. Nothing else — the Vidyano surface offers several redundant
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
- **R7a** **No entity-side partial is emitted, and `[GenerateIndex]` takes no type argument.** This is the
  load-bearing constraint of the whole feature, not a tidiness preference.

  The reference implementation puts `[QueryType(typeof(VCar))]` on the entity. That is a *type reference*
  from the entity's assembly to the index entity, so the index entity must live in the library. The index
  entity in turn carries `[FromIndex(typeof(Cars_Overview))]`, which is a second type reference that drags
  the index into the library as well. One attribute on the entity therefore pulls the entire index stack
  into what should be a lean library — and these libraries get referenced for other purposes, replication
  among them, so everything they drag in travels with them.

  Spark was built from the start to avoid that: it needs no `[QueryType]` on the collection entity, because
  the link is *derived* rather than declared. `[FromIndex]` on the index entity names the index, and
  `IndexRegistry` recovers the collection type from the index's generic argument. The arrows all point one
  way — app → library — and nothing generated is ever referenced by the library.

  Two rules follow, and both must hold for every future extension of this attribute:
  1. Never emit a partial for the entity.
  2. Never give `[GenerateIndex]` a parameter that names a generated type. A `typeof(...)` argument
     pointing at an index or index entity would reintroduce exactly the coupling this avoids.
- **R8** A property marked `[Search]` gets `Index(nameof(V.Field), FieldIndexing.Search)` on the base
  field, plus a `{Field}Sort` companion property, always decorated `[IgnoreProperty]`. **Suffix is
  `Sort` with no separator** — `NameSort`, `Name_nlSort`. The companion gets **no `Index(...)` call**
  (F1): default indexing is what makes it sortable. The companion is fed a byte-identical copy of the
  base field's map expression, with no normalization.
- **R8a** `[Search]` is valid on `string`, `string[]` / `IEnumerable<string>`, and `TranslatedString`.
  Anything else is `SPARK_INDEX_005`.
- **R8e** **`[IgnoreProperty]` excludes a property from the generated index as well as from the model, so
  `[Search]` on such a property has no effect and is reported as `SPARK_INDEX_006` (warning).**

  This is a deliberate divergence from the reference implementation, which indexes an
  `[IgnoreProperty, Search]` field and merely hides it from its model — a combination its own app uses for
  searchable-but-not-displayed values.

  Two coherent designs exist and the choice is a real one:

  | | `[IgnoreProperty]` | Consequence |
  |---|---|---|
  | **chosen** | excludes from model *and* index | matches the attribute's own docstring — "Spark treats it as if it did not exist" — and keeps infrastructure fields like Fleet's `RegistrySyncEtag` out of the index. Loses the ability to index without modelling. |
  | alternative | excludes from model only; `[IgnoreForIndex]` is the sole index control | fully orthogonal and strictly more capable, but indexes every infrastructure field by default, growing index size and re-indexing cost for values nobody queries. |

  The chosen option is the conservative one and is cheap to reverse; the alternative is not, because it
  silently enlarges every existing index. Either way the combination must not be silent, which is what
  `SPARK_INDEX_006` guarantees. Revisit if a real case for index-without-model appears.
- **R8b** For strings the relationship is a strict **biconditional**, verified across 398 generated
  properties in the reference app with zero exceptions: a field is `FieldIndexing.Search` **if and only
  if** it has a sort companion. They are one decision, not two, because analyzing the field is what
  destroys its sortability and the companion is the repair.
- **R8c** Attributes on the base property are **copied onto the companion** — the reference
  implementation carries `MaxLength` and `IgnoreProperty` through — in addition to the companion's own
  mandatory `[IgnoreProperty]`.
- **R8d** The companion must stay a real property on the view class, reachable from LINQ. It is hidden
  from the *model* by `[IgnoreProperty]`, not from code: the reference app relies heavily on
  `x.Name == v || x.NameSort == v` and `x.NameSort.StartsWith(...)` for exact and prefix matching,
  precisely because the analyzed base field cannot serve either.
- **R9** `DateTimeOffset` / `DateTimeOffset?` properties get `Index(field, FieldIndexing.Exact)` on the
  base field and a sort companion automatically, with no attribute (15/15 in the reference app).
  **`DateTime` / `DateTime?` get neither** (22/22) — the distinction is deliberate, not an oversight, and
  must be reproduced exactly. Reference-typed fields never get a companion, and searchability is **not
  inherited through a reference**: a referenced entity's own `[Search]` does not make the flattened
  field searchable.
- **R10** For a `TranslatedString` property `Description`, emit one `string?` property per language from
  `culture.json` (`Description_en`, `Description_fr`, `Description_nl`), each mapped as
  `x.Description!.Translations["<lang>"]` — the expression RavenDB stores verbatim and evaluates natively
  against `Description.Translations.<lang>` (F2). When `[Search]` is present, add a `{Field}_{lang}Sort`
  companion per language. Novel design, no prior art (F9); sequenced last.
- **R10a** `StoreAllFields(FieldStorage.Yes)` is **mandatory** on every generated index, not merely
  conventional. S6 measured the failure: without it, a projection-only field such as `Description_nl` comes
  back `null` while fields that also exist on the document materialize fine from the document, and
  `OrderBy`/`Where` still work — so the index is provably correct and the projection is silently empty.
  This is the single most likely way a generated index could appear broken.
- **R11** The generated view must **never** emit a property whose name equals an entity property but
  whose type differs. For a `TranslatedString` entity property this means no `string?` property named
  `Description`. SPARK001 would be an error, and although it does not analyze generated code per F4,
  the model merge in `ModelSynchronizer` validates `dataType` compatibility on both sides.
- **R12** `[Reference(typeof(T))]` and `[LookupReference(typeof(T))]` on an entity property are copied
  onto the corresponding index-entity property, **verbatim and without adding `[IgnoreProperty]`**.

  An earlier draft of this requirement said `[Reference]` id properties should be marked
  `[IgnoreProperty]` on the index entity "matching the hand-written convention". That was wrong — it is the
  *reference implementation's* convention, where an id field is index-only and hidden from its model.
  Spark's hand-written views do the opposite: `Demo/DemoApp/DemoApp/Data/VPerson.cs` carries
  `[Reference(typeof(Company))]` with no `[IgnoreProperty]`, and **SPARK002 is an error precisely when the
  index-entity property lacks a `[Reference]` its entity has**. Adding `[IgnoreProperty]` would strip the
  reference from the model and break breadcrumbs and `.Include()` resolution.
- **R12a** Attribute carry-over uses a **deny-list, not a whitelist**: every attribute on the entity
  property is copied except the generator's own directives (`[Search]`, `[IgnoreForIndex]`,
  `[GenerateIndex]`). The reference implementation whitelists, so anything a developer puts on a property
  outside that list is dropped with no indication. An attribute whose arguments cannot be rendered
  faithfully is skipped **and reported** (`SPARK_INDEX_007`), never dropped silently.
- **R12b** A sort companion inherits the base property's copied attributes **except** `[Reference]` and
  `[LookupReference]`. A companion is a plain sort key, not a second reference to the same target; copying
  the reference would declare a reference the model then has to resolve.
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

### Hand-written index entities also get their companions generated

The index entity is **always** in the application project, so the generator can always contribute a partial
half to it — whether the pair was generated by `[GenerateIndex]` or hand-written. That covers the
established layout where a developer keeps a hand-written index and its `[FromIndex]` index entity
side by side.

- **R30** For a `partial` index entity carrying `[FromIndex]`, emit `{Name}Sort` companions for its
  `[Search]`-marked properties, decorated `[IgnoreProperty]`, exactly as for a generated pair. The
  developer writes the class and its `[Search]` intent; the boilerplate is generated.
- **R30a** Also emit a `private void IndexSearchFields()` method onto the **index** class, containing the
  `Index(nameof(V.Field), FieldIndexing.Search)` call per `[Search]` property and `Exact` per `DateTimeOffset`
  property. The hand-written constructor calls it.

  Without this, `[Search]` on the index entity and `Index(nameof(...), FieldIndexing.Search)` in the constructor
  state the same fact twice and drift independently — and drift here is invisible, because an un-analyzed field
  simply is not searchable. The attribute becomes the single declaration.

  A generator can add *members* to a partial class but cannot add *statements* to a hand-written constructor, so
  the call itself stays the developer's. That is the same limit that keeps the map assignments hand-written
  (R31), and `IndexSearchFieldsMethod` is therefore effectively public API — renaming it breaks every consumer's
  constructor.
- **R30b** The index class must also be `partial`; `SPARK_INDEX_009` otherwise. Without it the method cannot be
  contributed and the fields would be indexed with default options — searchable text that is silently not
  searchable.
- **R30c** The generated `nameof` is **fully qualified** (`nameof(global::App.Data.VCar.Model)`). The method
  lands on the index class, which may sit in a different namespace than the index entity; co-locating them is
  the convention but the generator cannot rely on a consumer following it, and an unqualified name there is a
  `CS0103` inside generated code.
- **R31** **The generator cannot supply the map assignment for a hand-written index.** A generator adds
  members to a partial class; it cannot reach inside a hand-written `Map = ... select new VCar { ... }`
  initializer to add `ModelSort = car.Model`. So R30 generates a property that is declared, stored and
  sortable but **fed by nothing** unless the developer also adds that line — a silent null of exactly the
  kind F13 and R10a are about.

  This is the one place where generating *half* the boilerplate creates a hazard the fully-hand-written
  version did not have, so it is not left to documentation: the W11 analyzer gains a rule for a sort
  companion that is declared but never assigned in the index's map. Generated pairs are immune by
  construction, since the generator owns both halves.
- **R32** A non-`partial` hand-written index entity gets `SPARK_INDEX_001` rather than silence, since
  nothing can be contributed to it.

### Generated context query roots

- **R33** Emit a query root onto the app's `SparkContext` for each generated pair:
  `public IRavenQueryable<VCar> VCars => Session.Query<VCar, Cars_Overview>();` — the member Fleet and HR
  write by hand today. Requires the context to be `partial`; the demo contexts gain the keyword.
- **R34** If the context already declares a member of that name, emit nothing for it. A hand-written root is
  a legitimate override, so this is silent by design rather than a diagnostic — and emitting anyway would be
  a duplicate-member compile error.
- **R35** Root names come from the same `IndexNaming` function as index and companion names, not from a
  second derivation. The reference design computed names in two independent traversals, which is where
  "the index and the context disagree" bugs come from.
- **R36** Adding a root moves the model hash: `ModelShapeDiscovery.QueryableRoots` walks
  `IRavenQueryable<>` properties, so W9's re-synchronize must cover it even though projection types are
  themselves skipped from the model.

### Sort redirection (F8) — outside the generator

- **R21** `QueryExecutor.ApplySorting` redirects a requested sort to the attribute's sort companion. Sorting
  by `Model` orders by `ModelSort`. Callers, query JSON `sortBy` and the `?sortBy=` runtime override all keep
  naming the display attribute; nothing outside the query pipeline learns that companions exist.

  Without this the generated companions are dead weight — correctly indexed, correctly stored, never used —
  which is the difference between this issue fixing sorting and merely preparing to fix it.
- **R22** The companion is resolved **by convention at query time, not persisted per attribute.**

  The reference implementation persists a `SortExpression` on every searchable attribute (100+ occurrences in
  its committed model JSON). Spark does not need to: the name is *always* `{Name}Sort`, measured across every
  hand-written index in that corpus with no exceptions. Persisting it would add a model field and matching
  model-hash churn to **every existing model file**, to restate something already derivable — and would add
  one more thing able to go stale, since a persisted name can outlive the property it points at.

  Their reasons for persisting it do not transfer: their query pipeline resolves the indirection from model
  metadata rather than by convention, and they expose it as runtime-mutable so an action can suppress the
  redirect for one request.
- **R22a** A candidate companion qualifies only if it is **`[IgnoreProperty]`**. That is the signal
  distinguishing a real companion from a coincidence, so an ordinary domain property named `FooSort` cannot
  silently hijack ordering on `Foo`. Every companion, generated or hand-written, carries it.
- **R22b** A missing companion falls back to the requested property unchanged. No diagnostic, no throw:
  ordering by the display field is merely imperfect, whereas throwing would take the whole query down.
- **R23** ~~Runtime-settable `SortExpression` on `PersistentObjectAttribute`.~~ **Dropped.** It does not fit
  Spark's pipeline: sorting is resolved once per request from the sort type, while `PersistentObjectAttribute`
  instances are per **row**, produced *after* ordering has already been applied. A property there would be
  dead surface — the same objection that keeps the fan-out constructor out of R1.

  If per-attribute override or suppression is ever wanted, an optional model field can be added later without
  breaking anything: absent would continue to mean "use the convention".

### Organic full-text search (W14)

Reframed after F14–F16. The premise "`[Search]` produces an analyzed field that nothing queries" is true, but the
gap is not a missing feature — search is wired end-to-end and the server implements it by **materializing the
whole collection and filtering in memory** (F14). W14 replaces that with a database pushdown.

**R37** `QueryExecutor` pushes the search term into RavenDB via `LinqExtensions.Search(...)`, invoked
reflectively and cached through `ReflectionCache` exactly as `ProjectInto` and `OrderBy` already are. It is
applied on the same untyped `object` queryable, between `ComposeRowFilterAsync` (`QueryExecutor.cs:168`) and
`ApplySorting` (`:170`). Position within the LINQ chain is otherwise free (F15) — this position is chosen to keep
the row-security `Where` ahead of the search group, which is what makes F16's `Guess` grouping correct.

**R38 — RESOLVED, and inverted.** The question was "how does the runtime find `[Search]` fields". F15 makes it
moot: wildcards match on *plain* string fields, so searchability is not gated on `[Search]` at all. Searchable
fields are therefore **every string-typed property of the sort type** (`resultType` when a projection is in play,
else `entityType` — the same type `ResolveSortProperty` already reflects over), including the per-language
`{Prop}_{lang}` fields a `[Search]`ed `TranslatedString` fans out to. `[Search]` keeps its existing meaning
(token matching, relevance, analyzer behaviour, and forcing the sort companion) but no longer decides what is
searched.

This also disposes of the two candidate mechanisms that were on the table, both now unnecessary:

- carrying `[Search]` through `AttributeRenderer` — not needed, so the deny-list and its pinning test
  (`GenerateIndexGeneratorTests.cs:813-829`) stay as they are;
- inferring searchability from the `[IgnoreProperty]` companion — which was **wrong anyway**: `DateTimeOffset`
  gets a companion while indexed `Exact` (`GenerateIndexGenerator.cs:305,314-315`), so it would have searched
  date fields in violation of R39.

A third option, persisting an `IsSearchable` flag on `EntityAttributeDefinition`, is rejected on cost (model-hash
churn plus a `--spark-synchronize-model` rerun) and was **broken as written**: `ModelSynchronizer.cs:471` resolves
`collectionProp ?? projectionProp`, so the collection property wins and `[Search]` on a projection property whose
name also exists on the entity would be silently invisible — measured against `VPerson.Email` and
`VCar.LicensePlate`. Recorded so nobody takes that route later.

**R39** Excluded from search: non-string properties, and any string field indexed `FieldIndexing.Exact`. Not for
correctness — nothing throws (F15) — but because `Exact` fields mismatch **case-sensitively and silently** in a
direction-dependent way. The type test covers `DateTimeOffset` for free, since it is not a string.

**R40** The search is skipped **entirely** — not applied with an empty argument — when the term is null,
empty or whitespace, when it reduces to nothing after wildcard wrapping, or when the type has no searchable
field. This is load-bearing: measured, an empty term returns **zero rows** rather than no-opping, and `null`
throws (F16). A bare `*` must not reach RavenDB either; it matches everything.

**R41 — RESOLVED for free.** Under R38 the per-language `{Prop}_{lang}` fields are plain strings on the
projection, so **all languages are searched**, with no coupling to `RequestCultureResolver` and no fallback rule
to specify. This is also the more forgiving behaviour: a Dutch-language user still finds a record whose only
match is its French label. Scoping to the request's language remains available later as a narrowing, which is a
safer direction of change than widening.

**R42 — DISSOLVED.** No client work: the search box, the `searchTerm` state and the `?search=` parameter all
already exist (F14). This stays a server-only release.

**R43** `TotalRecords` must remain search-aware. Today it is, because filtering precedes paging
(`QueryExecutor.cs:61-64`); once the filter moves into the database the count has to come from RavenDB's query
statistics rather than from the materialized list.

**R44** `SearchOptions` is **never passed explicitly** — every leg uses the default `Guess`. This is a safety
requirement, not a style one: an explicit `Or` leaks onto the adjacent `Where`, and the adjacent `Where` is
row-level security (F16). A test must pin the emitted RQL shape
`(security predicate) and (search or search or ...)`, because this failure mode returns plausible rows rather
than erroring.

**R45** Paths that cannot push down keep the in-memory filter as a documented fallback: `Custom.` queries whose
method is not `IRavenQueryable` (`CustomQueryMethodInfo.IsRavenQueryable` already distinguishes them), and any
query type with no searchable field. The fallback is also what preserves `Breadcrumb` matching (F14) where it
still runs.

**R46** The narrowing in F14 — reference display text (`Breadcrumb`) is not an index term and stops being
searchable on pushdown paths — is documented with the denormalization remedy the demos already use
(`VCar.OwnerFullName`). Search gets its own guide, `docs/guide-search.md`, linked from the README's guide table
and from the queries guide: the behaviour has enough surface (wildcard handling, per-language fields, the
composition order with row security, the fallbacks, four known limits) that burying it in a section of the
queries guide would hide most of it.

**R47** The streaming query path (`Streaming/StreamingQueryExecutor.cs`, `Endpoints/Queries/StreamExecuteQuery.cs`)
takes no search term at all; the client filters those in memory (`spark-query-list.component.ts:410-421`).
**Out of scope**, explicitly, and noted in the release notes so the asymmetry is known rather than surprising.

#### Matching semantics — decided

F15 leaves exactly one genuine choice, and it changes user-visible behaviour either way:

- **(a) Substring parity.** Wrap each word of the term as `*word*` and pass `SearchOperator.And`. Today's
  `Contains` behaviour survives, the two existing tests keep passing, and
  search works across every text field whether or not it is analyzed. Cost: leading wildcards force term-
  dictionary scans, so this is the slower query shape — still far cheaper than fetching the whole collection,
  but **unmeasured at scale**, and it gives up relevance ranking.
- **(b) True organic term search.** Pass the words as-is, letting the analyzer match tokens, with relevance
  ordering when no explicit sort is requested. Cheaper and it is what "organic search" normally means. Cost: it
  **only works on `[Search]` fields** (bare-word search on a plain field returns nothing — measured), so it
  narrows what is searchable to whatever has been declared, and infix matching disappears: `olkswag` stops
  finding `Volkswagen`. Both existing tests change.

**DECIDED: (a).** It is the only option that is a strict improvement — same semantics as today, executed in the
database instead of in memory, with no regression to explain to anyone. (b) remains available afterwards as an
explicit opt-in without disturbing (a); doing it the other way round would mean shipping a regression first.

So the term is normalized as: split on whitespace, drop `*` and `?` from each word (they are not wildcards the
caller gets to supply — a bare `*` matches everything, and F15 measured that `?` and mid-word `*` do not work
anyway), wrap each surviving word as `*word*`, and pass `SearchOperator.And` so every word must be present
somewhere in the field. No pre-lower-casing: F15 measured that the term is lower-cased for us.

Two things this shape actually delivered, both verified against a real server and neither predicted by the spike:
a substring **does** span a whitespace boundary (per-word wrapping, so the words match independently), and the
result is therefore slightly **wider** than `Contains` rather than narrower — word order and adjacency stop
mattering. The measured narrowings are only the ones inherent to a pushdown: non-text attributes (R39) and
`Breadcrumb` (R46).

### Sort-companion analyzer for hand-written indexes (F10)

Restored: this block was accidentally dropped by the W7 edit and lived only as dangling `R24`–`R27`
references from the plan. Statuses below are as shipped.

- **R24** **Done** as `SPARK005` (`SortCompanionAnalyzer`): a projection property indexed
  `FieldIndexing.Search` — or a `DateTimeOffset` indexed `Exact` — with no `{Name}Sort` companion on the same
  type. Severity **warning**, not error: unlike SPARK001/002 this is a correctness *risk* rather than a broken
  contract, and existing hand-written indexes must keep compiling.

  It earned its keep immediately: the first run found **six real pre-existing bugs** — DemoApp's
  `VPerson.FullName`/`Email`, `VCompany.Name`, `VCar.LicensePlate`/`OwnerFullName` and HR's `VPerson.FullName`.
  `FullName` is `FirstName + " " + LastName`, so it always contains a space, which means sorting a people grid
  by name was broken in both demos before this issue.

  A companion rule shipped alongside it: **`SPARK006`** flags a declared companion that the map never assigns,
  which is the other half of the same defect (R31 — a generator cannot write into a hand-written map, so the
  property can exist while staying permanently null).
- **R25** **Done.** The diagnostic is reported on the **hand-written property's location**, never
  `Location.None` and never a generated location, or it is silently dropped (F11).
- **R26** **Not done — deferred to its own issue.** The "Add Sort property" code fix was attempted and
  reverted. A `CodeFixProvider` cannot live in the analyzer assembly: it needs
  `Microsoft.CodeAnalysis.CSharp.Workspaces` at runtime, which is deliberately not packed, and its presence made
  `GetTypes()` throw `ReflectionTypeLoadException` — **breaking the entire generator test suite**, not just the
  code fix. It needs its own `*.CodeFixes` assembly plus packaging, which is a self-contained piece of work
  with no bearing on the rest of this issue. **Tracked as #270.**

  The analyzer already names the property to add, so the gap is a missing lightbulb rather than missing
  information.
- **R27** **Done, and it cost nothing.** No suppression mechanism was built. When the generator emits the
  companion the analyzer sees the generated symbol and does not fire (F11, proved by spike S4), so referencing
  the generator stops the suggestions automatically — no marker, no configuration. A combined
  generator-then-analyzer test pins this, because it is the interaction the whole "no marker needed" design
  rests on.

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

- **N9** **Fuzzy / typo-tolerant search.** Asked for during the W14 discussion and spiked (plan S10) rather than
  guessed at. Rejected on measurement, not preference: **Corax — the 7.x default, and what generated indexes
  get — does not support `Fuzzy` at all**, failing server-side with `InvalidQueryException: Method 'Fuzzy' is not
  supported`. It is an index-definition decision (`SearchEngineType.Lucene`), not a query feature, so a per-query
  fuzzy toggle cannot be honoured on an index not built for it. Independently, `Fuzzy` exists only on the
  document-query API — zero `IQueryable` extensions, no back-conversion — and must sit immediately after a
  `WhereEquals`, so it cannot splice into Spark's reflective `IQueryable` pipeline; the `ToAsyncDocumentQuery()`
  bridge breaks as soon as row security adds a second `Where`. It is also **mutually exclusive with W14's
  substring wrapping**, because wildcards are counted as edits and silently consume the distance budget.

  Two findings worth keeping for a future issue: fuzzy works **whole-value on plain default-indexed fields**
  (including the sort companions this issue already emits, so no `[Search]` is needed), and an `EditDistance`
  knob would have to be translated per term as `1 − d/len − ε`, since similarity is `1 − d/min(len)` and a fixed
  threshold means a different distance on every field length.

- ~~**N1** Organic/full-text search execution.~~ **Promoted into scope** — see R37–R47 and plan W14. `[Search]`
  already makes a field indexed for search; W14 makes something actually query it.
- **N2** `Reduce` / map-reduce, multi-map, `AdditionalSources`, spatial, suggestions, term vectors.
  Nothing in the repo uses them. Noted for whenever map-reduce does arrive: sort companions have to be
  propagated through the grouping explicitly (`ModelSort = g.Max(x => x.ModelSort)`), not just through
  the map — the reference app does this by hand in its one map-reduce index.
- **N3** Stringly-typed expression escape hatches (`IndexCustomProperty` / `Variable` / `Where`).
  Rejected in favour of `OnInitialize()` and hand-written partials.
- ~~**N4** Emitting `SparkContext` members.~~ **Promoted into scope** — see R33. The blocker was that the
  demo contexts are not `partial`; adding the keyword is accepted.
- ~~**N5** Migrating the five existing hand-written index pairs.~~ **Done after all.** Fleet's `Car` is fully
  generated; DemoApp's and HR's pairs stay hand-written but now declare searchability once via `[Search]` on the
  index entity and call the generated `IndexSearchFields()`. Index names did not change, so no re-indexing is
  triggered. DemoApp's index entities also moved from `DemoApp.Data` into `DemoApp.Indexes`, so every index and
  index entity in the solution is co-located the way the generator emits them.
- **N6** Collection fan-out (`[GenerateIndex(typeof(Country), nameof(Country.Cities))]`), the
  `...ObjectId` composite keys it needs, and the parented `CustomQueryArgs` query methods that go with
  it. Issue #210 does not ask for it, it is the largest and least-proven part of the reference design,
  and it interacts with `[ValueObject]` / splitted collections. The constructor overload is withheld
  along with the feature (R1) so there is no dead surface.
- **N7** A client-side editor/renderer for `dataType: "TranslatedString"`. None exists today — the type
  is used for labels and metadata, but no `po-form` or renderer keys on that data type, so a translated
  *value* has no editor. Out of scope here, but it means R10's generated per-language fields will be
  queryable and sortable before they are editable.
- **N8** **Postponed: an `IAudit` boilerplate generator.** Enterprise applications need
  `CreatedBy` / `CreatedOn` / `ModifiedBy` / `ModifiedOn` on their entities, which is boilerplate that
  wants generating rather than retyping. That generator would have to run **in the library project**,
  because those properties belong to the collection entity itself — the one place this issue deliberately
  keeps generators out of.

  It is therefore a separate generator project and a separate decision, not an extension of this one:
  - it is the *only* identified reason to put a generator in a library, so it should be justified on its
    own terms rather than smuggled in here;
  - a library-side generator brings the packaging, versioning and reference-flow questions catalogued in
    F12, none of which this issue needs to answer;
  - and unlike an index, an audit property is genuinely part of the entity, so it does not conflict with
    R7a's rule about never generating into the entity's assembly for *index* purposes.

  For this issue the rule stands unchanged: **no generator in the library projects.** Deferred to its own
  issue — **tracked as #271** — with F12 as the ready-made reference for how a second lean generator project
  is wired.
