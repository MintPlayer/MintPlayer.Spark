# Plan — Issue #210: `[GenerateIndex]`

**PRD:** [issue_210_PRD.md](issue_210_PRD.md) ·
**Issue:** [#210](https://github.com/MintPlayer/MintPlayer.Spark/issues/210)

Built and committed milestone by milestone. Each milestone below is one commit, compiles on its own, and
carries its own tests. Test suites are run once as a batch at the end (per repo convention), with
intermediate milestones verified by reading and type-checking.

| | Work | Requirements |
|---|---|---|
| W1 | Attributes + Roslyn symbol helpers | R1–R4, R8a |
| W2 | Generator skeleton: index + view for a plain entity | R5–R7, R13, R14, R16, R18–R20 |
| W3 | `[Search]` → search indexing + `Sort` companion, incl. hand-written index entities | R8, R8a–R8d, R30, R32 |
| W4 | `DateTimeOffset` → `Exact` + automatic companion | R9 |
| W5 | Reference / lookup carry-over, `[IgnoreForIndex]` | R3, R12 |
| W6 | Referenced-assembly entity discovery | R15 |
| W7 | `SortExpression` model field + `QueryExecutor` redirect | R21–R23 |
| W8 | `culture.json` AdditionalFile + `TranslatedString` fan-out | R10, R17 |
| W9 | Convert one demo entity; re-synchronize model + hashes | F7, N5 |
| W10 | Guides, release notes, version bump | — |
| W11 | Missing-sort-property analyzer + "Add Sort property" code fix | R24–R27, R31 |
| ~~W12~~ | ~~Lean entity-side generator for `*.Library`~~ — dropped, superseded | — |
| W13 | Generated `SparkContext` query roots | R33–R36 |
| W14 | Organic full-text search — push the existing search into RavenDB | R37–R47 |

Ordering rationale: W2–W5 are the proven parts of the reference design and ship first. W7 is what makes
the companions actually used (PRD F8) and is independent of the generator, so it can land in parallel.
W8 is the novel, prior-art-free part (PRD F9) and is deliberately last, so a problem there cannot block
everything else.

---

## Spikes

Three unknowns are load-bearing enough that a negative result changes the shape of the work. All three
are run before W1.

### S1 — is the sort companion actually sortable without an `Index(...)` call? PASSED

**The premise of the entire feature**, and it holds. Run together with S6 against the live server.

Ordering by a `Search`-indexed field is garbage; ordering by an undeclared companion is correct and
case-insensitive; and adding `FieldIndexing.Exact` to the companion is a measurable regression on *both*
sort and equality, not just redundant. Stored terms, orderings and the case-sensitivity result are in PRD
F1.

Two claims were corrected rather than confirmed: `Search` does **not** produce duplicate rows (fan-out
does), and nulls/empties sort **first** on a lower-cased companion rather than last. Both are recorded in
PRD F1 and the guide correction is part of W10.

Kept permanently as the regression guard for the mechanism, in `MintPlayer.Spark.Tests`.

### S2 — referenced-assembly discovery: visibility and cost

PRD F3/R15. Two questions: can the generator see `[GenerateIndex]` on a type in a referenced assembly
via metadata symbols, and what does walking those assemblies cost on a real build?

Method: put `[GenerateIndex]` on `Fleet.Library`'s `Car`, generate into `Fleet`, confirm the attribute
resolves through `IAssemblySymbol` and measure the incremental build. Filter to assemblies referencing
`MintPlayer.Spark.Abstractions` and memoize through the supplied `ICompilationCache`.

Negative result → fall back to referencing the generator from each `*.Library` **plus** a generated
`AddIndexes()` that calls `AddIndexesFromAssemblyContaining<TMarker>()`, so the library case is wired
automatically rather than by hand.

### S3 — can the generator read `culture.json`?

PRD R17. `AdditionalTextsProvider` filtered to `App_Data/culture.json`, parsed with the project's
hand-rolled `Json/` helpers (System.Text.Json is not available in a netstandard2.0 generator — house
style has its own mini parser). Confirm the file is visible in a real build once `spark.targets` adds the
`AdditionalFiles` item, and that an absent file degrades to `en` rather than emitting nothing.

Negative result → the language list moves to an MSBuild property, which is worse ergonomically but keeps
R10 achievable.

### S4 — are generated symbols visible to the analyzer? PASSED

Whether the "no marker, no editorconfig" suppression design actually works under
`GeneratedCodeAnalysisFlags.None`. Proven twice — in the test harness and in a real `dotnet build`:

```
=== BEFORE GENERATOR ===  Proj => Name
=== AFTER GENERATOR  ===  Proj => Name,NameSort
```

`GetMembers()` returns the merged partial including generated halves; a partial with at least one
hand-written declaration is analyzed normally; generators run before analyzers inside one `csc`
invocation. Full detail and the load-bearing caveat about diagnostic location are in PRD F11.

Had it failed, R27 would have needed a marker type and the lean generator a public marker to advertise
itself.

### S5 — can a second lean generator project coexist? PASSED, with precedent

`libs/all_features/MintPlayer.Spark.AllFeatures.SourceGenerators` already *is* this pattern — a lean
single-generator project loaded alongside the main one in every demo app. Confirmed no `ComparerRegistry`
collision, packaging fully inherited, and `spark.targets`' SPARK001 hard-error does not reach an in-repo
`*.Library`. Detail in PRD F12.

The milestone this was spiked for (W12) has since been dropped, so nothing in this issue consumes the
result. It is kept because it is the groundwork for the postponed `IAudit` generator (PRD N8), which is the
one remaining reason to put a generator in a library project.

### S6 — can an index read one language out of a `TranslatedString`? PASSED, and the premise was wrong

Run against the live RavenDB on `localhost:8080` and re-verified on embedded 7.2.5 — identical results.

**The spike's own premise turned out to be false, which is the most useful thing it produced.**
`TranslatedString` does *not* persist flat: the flat converter is System.Text.Json and applies only at the
HTTP layer, while RavenDB persists through Newtonsoft as `Description.Translations.nl`. So there is no
CLR-path-vs-JSON-path conflict, and the straightforward strongly-typed expression is correct:

```csharp
Description_nl = c.Description!.Translations["nl"],
```

RavenDB stores that map verbatim and evaluates the dictionary indexer natively via `DynamicBlittableJson`.
Populates through `ProjectInto`, sorts correctly with spaces, supports `==` and `StartsWith`, and a missing
language key or a null `Description` both index to `null` with no exception and no index error.

Rejected with evidence rather than by preference:

- **Dynamic fields (`CreateField`)** satisfy projection and sorting but `Where(==)` returns **0 rows,
  silently** — terms are stored case-preserved while the query side lower-cases, because a dynamic field has
  no index-definition entry to consult. Making them work requires declaring `Store` + `Index(..., Exact)`
  statically, which needs the language names at build time anyway, at which point a plain static field is
  strictly better.
- **A raw string map / `AdditionalSources`** works but buys nothing and loses compile-time checking.
- **Abandoning the dictionary for real per-language properties** is unnecessary; the dictionary indexes
  fine.
- **`Description.GetValue("nl")`** deploys happily, produces no index error, and returns null forever —
  exactly the trap this spike existed to find.

Four distinct silent-null modes were catalogued, all error-free; they drive R10a and PRD F13.

### S7 — can `.Search()` reproduce the current substring semantics? PASSED, and it reframed W14

Run against RavenDB.Client 7.2.5, on **both** search engines — Corax (the default here) and a twin index forced
to Lucene. **Every wildcard result agreed across engines**, so the conclusions are engine-independent.

The spike existed because the framework's current search is an in-memory
`Contains(term, OrdinalIgnoreCase)`, so a pushdown that only matched whole tokens would be a regression.

Measured: **substring parity is reachable** — wrap each word as `*word*` with `SearchOperator.And`. Leading,
trailing and both-ends wildcards all match, including token-internal (`*olf*`); RavenDB's historic leading-
wildcard restriction does not bite on 7.2.5. The term is lower-cased for you, so no pre-normalization is needed.
`?` is unsupported, mid-word `*` (`go*lf`) does not match, and a bare `*` matches everything.

Two things it found that were not being looked for, and that changed the design:

- **Wildcards match on fields that were never declared searchable.** On a plain `string` with default indexing,
  bare-word search returns **0** (the whole value is one term) but `*olkswag*` matches. So `[Search]` does not
  gate searchability, which inverts R38 — see PRD F15.
- **`FieldIndexing.Exact` is case-sensitive to the search term**, silently and direction-dependently: `*GOL*`
  matched, `*gol*` returned 0. That is what makes R39 a semantics requirement rather than an optimization.

One irreducible gap: a substring **spanning a whitespace boundary** cannot match, because the query term is split
on whitespace first. `*olf gt*` → 0 rows. So `Contains("olf GT")` is not recoverable; `Contains("olkswag")` is.

### S8 — how do multiple `Search` legs combine with a preceding `Where`? PASSED, and found a security hazard

The spike was meant to confirm that `SearchOptions.Or` is how you OR several fields. **It is not, and the plan's
original instruction to use it was wrong.**

Measured: an explicit `SearchOptions.Or` **leaks forward onto the adjacent `Where` clause**, OR-ing it instead of
AND-ing it. Spark composes row-level security as exactly such a `Where`, immediately before where search goes —
so an explicit `Or` is a **silent row-security bypass** that returns plausible-looking rows. Putting the `Where`
first breaks precedence the other way instead.

The default `SearchOptions.Guess` is both safe and exactly right: it parenthesizes the consecutive `Search` group
and ANDs it with neighbours in both directions. Mixing explicit options is additionally meaningless — `(And, Or)`
and `(Or, And)` both rendered `or`. Drives R44, and the RQL-shape test that pins it.

Also measured here: no required LINQ call order (`Search` before or after `OrderBy`/`ProjectInto`/`Skip`/`Take`
emits identical RQL), and **no duplicate rows** on a single-map index even when one document matches several legs
or several tokens — so the existing `DistinctBy(po => po.Id)` suffices.

### S9 — what does an empty or null term do? PASSED, and the answer is why R40 is load-bearing

Measured: `""` and `"   "` **return zero rows** — they do not throw and they do not no-op. `(string)null`
**throws `ArgumentException`** at query-build time, and an empty `IEnumerable<string>` throws
"Cannot search on empty searchTerms array". A bare `null` literal additionally fails to compile (CS0121 between
the two overloads).

So the "empty term is a no-op" requirement cannot be delegated to RavenDB: without an explicit guard, clearing
the search box silently empties every grid.

### S10 — can we offer fuzzy search (`EditDistance = 2`)? **FAILED — and the blocker is the engine, not the API**

Spiked because it is the obvious next question once search is pushed down. The answer is no, for a reason that
would not have been found by reading the API surface.

**Corax does not support fuzzy at all.** Every `Fuzzy` query against a Corax index, at every similarity value, on
both analyzed and plain fields, returned the same server-side error:

```
Raven.Client.Exceptions.InvalidQueryException: Method 'Fuzzy' is not supported.
Query: from index 'Cars/Fuzzy' where fuzzy(Model = $p0, 0.5)
   at ...Persistence.Corax.CoraxQueryBuilder.ToCoraxQuery(...)
```

The *client* emits valid RQL; the *server* refuses it. Corax is the 7.x default and is what generated indexes get,
so fuzzy is not a query-side feature Spark can add — it is an **index-definition** decision
(`SearchEngineType = SearchEngineType.Lucene`, measured to work per-index against a Corax-default server). A
fuzzy toggle on a query therefore cannot be honoured on an index that was not built for it, and a fuzzy leg
reaching a Corax index fails as a 500, not as a degraded result.

**Second, independent blocker: `Fuzzy` is document-query-only.** It hangs off `IDocumentQueryBase<T,TSelf>` /
`IAbstractDocumentQuery<T>`; there are **zero** `IQueryable` extension methods for it in the 7.2.5 assembly and no
`ToQueryable()` back-conversion. It is also positionally fragile — it must come *immediately* after a
`WhereEquals`, and anything else throws `InvalidOperationException: Fuzzy can only be used right after Where
clause with equals operator` (measured for `Search`, `WhereStartsWith`, and a second `Where`).

The `ToAsyncDocumentQuery()` bridge looked like a shortcut and is not: it survives a single lone `Where` plus
`Include`, but **a second `Where` — i.e. row security, which Spark always adds — or `OrderBy`/paging makes the
positional guard reject it.** Measured in all three orderings.

Lucene-only behaviour, recorded in case this is ever revisited:

- Similarity is `1 − editDistance / min(len)` with **strict** `>`, matching Lucene 3.x classic `FuzzyQuery`. So a
  fixed similarity is **not** a fixed edit distance: "distance 2" is 0.6 on a 5-char term and 0.9 on a 20-char
  one. An `EditDistance` knob must be translated per term as `1 − d/len − ε`.
- Valid range is `[0.0, 1.0)`. Outside it throws `ArgumentOutOfRangeException` client-side (no clamping), and
  exactly `1.0` passes the client then fails on the wire with `minimumSimilarity >= 1`.
- Edit distance is **not** capped at 2 — distance-3 and distance-4 misspellings both matched at low thresholds.
- **`~` is a literal, not an operator.** `"volkswagon~2"` does not match; the decisive control is that
  `"volkswagen~"` (correct spelling) *does* match, because StandardAnalyzer discards the `~` as punctuation. There
  is no `~` back door into fuzzy from a search string.
- **Fuzzy + wildcard is the dangerous combination.** `*volkswagon*` "matched" only because the two `*` count as
  literal **edits** (3 edits over min-len 10 = 0.7 > 0.5), while `volks*` silently vanished (5 edits over min-len
  6 = 0.167). So wildcards eat the edit budget and the threshold becomes meaningless. Any fuzzy path must strip
  wildcards first — which makes it mutually exclusive with W14's substring-parity wrapping.
- **Multi-word fuzzy on an analyzed field matches nothing, even spelled correctly** — fuzzy treats the query as
  one term and an analyzed field holds no multi-word terms. A fuzzy feature would have to tokenize the input
  itself and emit one leg per word.
- Unlike `SearchOptions` (S8), `Fuzzy` does **not** leak onto adjacent clauses — verified with a probe whose
  neighbouring value was one edit from a real term and correctly did not match.

**The one genuinely useful finding**, if this is ever picked up: fuzzy works **whole-value on plain
default-indexed fields**, which includes the sort companions `[GenerateIndex]` already emits — no `[Search]`
needed. A narrow feature (one field, whole-value, Lucene index, document query built entirely by Spark rather
than composed onto a caller's queryable) would avoid the expression-tree translation problem. That is a separate
issue, not W14.

---

## W1 — Attributes and symbol helpers

`libs/spark/MintPlayer.Spark.Abstractions/`:

- `GenerateIndexAttribute.cs` — `[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]`,
  parameterless ctor, `IndexName` / `IndexEntityName` / `Description`. No fan-out overload (N6).
- `SearchAttribute.cs` — `AttributeTargets.Property`.
- `IgnoreForIndexAttribute.cs` — `AttributeTargets.Property`, with a docstring stating the difference
  from `[IgnoreProperty]` (index-only exclusion vs model-wide exclusion), following the precedent set by
  `IgnorePropertyAttribute`'s docstring-as-spec.

`libs/source_generators/MintPlayer.Spark.SourceGenerators/Models/SparkModelSymbols.cs` — extend with
fully-qualified-string matchers for the three new attributes and an `IsIndexableProperty` predicate that
is the Roslyn twin of `ReflectedTypeExtensions.IsSparkModelProperty` plus `[IgnoreForIndex]` (R14).

**No direct tests for the symbol helpers.** `SparkModelSymbols` is `internal` to a generator assembly
that consumers reference with `ReferenceOutputAssembly="false"`, and the test project loads that assembly
by name at runtime — so its internals are not reachable from a test, and `InternalsVisibleTo` cannot
change that. Coverage is therefore behavioural, through the generator in W2: the
`[IgnoreProperty] + [Search]` combination (R8a) and inherited properties (R13) each get a test asserting
what does and does not appear in the emitted source. This matches how every existing generator in the
repo is tested.

## W2 — Generator skeleton

New files, mirroring the `CronJobRegistrationGenerator` shape exactly:

```
Generators/GenerateIndexGenerator.cs             pipeline
Generators/GenerateIndexGenerator.Producer.cs    emission
Models/GeneratedIndexInfo.cs                     [AutoValueComparer] partial
Diagnostics/GenerateIndexDiagnostics.Rules.cs    SPARK0nn descriptors
```

- `[Generator(LanguageNames.CSharp)] : IncrementalGenerator`, 3-arg `Initialize`.
- `CreateSyntaxProvider` over class declarations carrying an attribute list →
  `.Where(x => x != null).WithNullableComparer().Collect()`.
- Feature gate both ways (R16): `GetTypeByMetadataName("…GenerateIndexAttribute") != null` and an early
  `return` in the producer.
- `(Producer)new GenerateIndexProducer(...)` → `context.ProduceCode(sourceProvider)`.
- Emits index + `[FromIndex]` view, both `partial`, with `partial void OnInitialize()` called last in
  the ctor (R5–R7).
- **One model, two outputs** — the property list is computed once and projected into both the map and
  the view, rather than traversed twice (the reference design's "index and context disagree" bug class).
- Diagnostics for every abort path (R18–R20). No `null` returns, no reliance on exceptions.

Because `Producer.Produce` discards exceptions, every diagnostic path gets a test asserting the
diagnostic *and* that no source was emitted.

Tests: `GeneratorHarness.Run("GenerateIndexGenerator", ...)` — plain entity emits both types; no
attribute emits nothing; no Spark reference emits nothing; each diagnostic fires. Plus a Verify snapshot
of the full generated pair in `Snapshots/`.

## W3 — `[Search]`

Base field gets `Index(nameof(V.Field), FieldIndexing.Search)`; companion `{Field}Sort` property is
emitted with `[IgnoreProperty]`, fed a byte-identical copy of the base map expression, and receives **no
`Index(...)` call** (R8). Valid on `string`, `string[]`/`IEnumerable<string>`, `TranslatedString`;
diagnostic on anything else.

## W4 — `DateTimeOffset`

`Index(field, FieldIndexing.Exact)` on the base field plus an automatic companion, no attribute needed.
Reference-typed fields never get one (R9).

## Producer-pattern conformance

Corrected during W5 after review against the upstream examples in
`C:\Repos\MintPlayer.Dotnet.Tools\SourceGenerators`. Two house patterns were being reimplemented by hand:

- **Diagnostics belong on the producer**, via `IDiagnosticReporter.GetDiagnostics(Compilation)` plus
  `context.ReportDiagnostics(...)` — not a hand-rolled `RegisterSourceOutput`. Both producers now implement
  it, and the local `DiagnosticInfo` model is deleted. This also moves dedup and diagnostics onto the same
  object, so the emitted source and the reported problems are projected from **one** model rather than two
  traversals — the "index and context disagree" bug class the PRD warns about.
- **`writer.OpenPathSpec(symbol.GetPathSpec(ct))` reconstructs containing types.** Hand-rolling
  `OpenBlock($"namespace {ns}")` was a real defect, not just off-style: a *nested* index entity was emitted as
  a top-level class in its namespace, which does not compile. Now covered by a test.

## W5 — Reference and lookup fidelity

`[Reference]` / `[LookupReference]` copied onto view properties; `[Reference]` id properties marked
`[IgnoreProperty]` on the view (R12); hierarchy walked (R13); `[IgnoreForIndex]` honoured (R3).

This milestone carries the weight of PRD F4 — SPARK001/SPARK002 do not analyze generated code, so these
tests *are* the safety net. Type fidelity between entity and view property gets explicit coverage,
including the R11 case where a `TranslatedString` entity property must not produce a `string?` view
property of the same name.

## W6 — Referenced-assembly discovery

Implements whichever branch S2 selected. Tests use `GeneratorHarness.CompileToMetadataReference` to
fabricate a library assembly containing a `[GenerateIndex]` entity and assert the app compilation
generates for it.

## W7 — `SortExpression` and the redirect

Independent of the generator; delivers PRD F8.

1. `EntityAttributeDefinition.SortExpression` (nullable string), emitted by `ModelSynchronizer` only for
   attributes with a sort companion, so existing model files are byte-identical where nothing is
   searchable (R21).
2. `QueryExecutor.ApplySorting` resolves `SortExpression` before reflecting the `OrderBy`, falling back
   to the display field when the named property is absent from the projection (R22).
3. Runtime-settable on `PersistentObjectAttribute` (R23).

Tests: unit coverage of the fallback and of absent-`SortExpression` behaviour, plus a `SparkTestDriver`
integration test proving that sorting by the display field returns space-containing values in correct
order end-to-end — the user-visible payoff of the whole issue.

## W8 — `culture.json` and `TranslatedString`

`AdditionalTextsProvider` for `App_Data/culture.json`; `AdditionalFiles` item added to the shipped
`spark.targets` so consumers need no csproj edit (R17); per-language flattened properties and their
companions (R10). Novel design per PRD F9, hence last.

## W9 — Prove it on a demo, re-synchronize the model

Convert exactly one demo entity to `[GenerateIndex]`, deleting its hand-written index and view, then run
`--spark-synchronize-model` and commit the resulting `App_Data/Model/*.json` + `modelHashes.json`
(PRD F7). Fleet's `Car` is the natural candidate: it already has `TranslatedString? Description`,
`[IgnoreProperty] RegistrySyncEtag`, a `[Reference]`, two `[LookupReference]`s and a pure-passthrough
index — it exercises W3–W8 in one entity.

Verified by `--spark-verify-model` exiting 0 and the Fleet app starting (the `modelHashes.json` gate
refuses startup on drift), plus the E2E project's real-build check.

## W10 — Docs and release

Update `guide-queries-and-sorting.md` (the generated alternative to the five-step recipe, and the
`SortExpression` redirect), `guide-translated-strings.md` (indexing and sorting a translated field),
`guide-reference-attributes.md` (carry-over onto generated views). Release notes covering the new model
field and the `ApplySorting` behaviour change. Version bump; CI publishes on merge to `master` — no
manual `dotnet nuget push`.

## W11 — Missing-sort-property analyzer and code fix

`Diagnostics/MissingSortPropertyAnalyzer.cs` + `.Rules.cs`, following `ProjectionPropertyAnalyzer`'s
shape. Fires on a projection property indexed `Search` (or a `DateTimeOffset` indexed `Exact`) with no
`{Name}Sort` companion; **warning**, not error, so the five existing hand-written pairs keep compiling
while they are flagged (R24). Anchored on the hand-written property's location (R25) — anchoring it
anywhere else drops it silently.

The code fix (R26) is the repo's first, so it brings a `CodeFixProvider` and a
`Microsoft.CodeAnalysis.CSharp.Workspaces` reference. Packaged into the same analyzer assembly.

No suppression logic is written (R27): S4 proved the generated companion satisfies the analyzer by symbol
lookup alone.

Tests: `GeneratorHarness.RunAnalyzerAsync` for the diagnostic; a combined generator-then-analyzer test
asserting the diagnostic does **not** fire once the companion is generated — that test is the executable
form of R27 and guards the interaction that the whole "no marker needed" design rests on.

## W12 — DROPPED (superseded)

Was: a lean entity-side generator project referenced from the `*.Library` projects.

Dropped for two independent reasons. S6 removed its technical justification — a generated index reads one
language of a `TranslatedString` directly, so no entity-side helper is needed. And its remaining candidate
job, generating sort companions for hand-written index entities, turns out not to need a library-side
generator at all: **the index entity always lives in the application project**, so the main generator can
contribute a partial half to it (R30–R32, folded into W3).

The one real use case for a library-side generator — `IAudit` boilerplate on collection entities — is
postponed to its own issue as PRD N8. It is the only identified reason to put a generator in a library, and
it deserves justifying on its own terms rather than riding along here. PRD F12 records exactly how a second
lean generator project is wired, so that groundwork is not lost.

**For this issue the rule is unchanged: no generator in the library projects.**

<details>
<summary>Original W12 scope, kept for the record</summary>

New project `libs/source_generators/MintPlayer.Spark.Entities.SourceGenerators` (name TBD), csproj copied
from `MintPlayer.Spark.AllFeatures.SourceGenerators` with a new `PackageId` and `Description`, keeping
`GeneratePathProperty="true"` and the Roslyn `Update` pins, both of which are load-bearing (PRD F12).

One generator, one job: entity-side per-language helper properties for `TranslatedString` on `partial`
entity classes, driven by an attribute added to the **existing** `MintPlayer.Spark.Abstractions` — no new
attributes package.

`SparkModelSymbols` is duplicated into the project rather than linked, matching what AllFeatures already
does and the file's own doc comment.

**S6 removed this milestone's original justification — open question for the issue owner.** The reason for
entity-side per-language helpers was that a generated index could not otherwise reach one language of a
`TranslatedString`. S6 showed it can, directly, with `x.Description!.Translations["nl"]` in the app-side
index. So W12 is no longer needed to make W8 work.

Two honest options:

1. **Drop W12.** The app-side generator covers indexing and sorting completely. Nothing in issue #210 then
   goes unimplemented.
2. **Keep it for a different, still-real job.** Two candidates: per-language helper properties on the entity
   for *application code* convenience (unrelated to indexing), or emitting sort companions onto hand-written
   `partial` index-entity classes in projects that keep their indexes in the library — which is how the
   reference app is actually laid out, and which would make the W11 code fix unnecessary there.

Not deciding this unilaterally: option 2's value depends on whether Spark apps are expected to keep
hand-written indexes in libraries long-term, which is a product call. Everything else in the plan is
independent of it.

In-repo consumers get a `ProjectReference … OutputItemType="Analyzer" ReferenceOutputAssembly="false"`;
external consumers a `PackageReference` with `PrivateAssets="all"` and `analyzers` in `IncludeAssets`. If
it should attach automatically in-repo, copy the `spark-allfeatures.targets` injection pattern.

</details>

## W13 — Generated `SparkContext` query roots

Emit `public IRavenQueryable<VCar> VCars => Session.Query<VCar, Cars_Overview>();` onto the app's
`SparkContext`, matching what Fleet and HR write by hand today (DemoApp omits them entirely).

Requires the context class to be `partial`, which is approved — the demo contexts get the keyword. Two
consequences to handle rather than discover:

- **It moves the context-roots hash.** `ModelShapeDiscovery.QueryableRoots` walks `IRavenQueryable<>`
  properties, so adding one changes the model hash even though projection types are skipped from the model
  itself. Folded into W9's re-synchronize.
- **Name collisions.** If the developer already declared a member of that name, emit nothing for it rather
  than producing a duplicate-member compile error — and prefer that over a diagnostic, since a hand-written
  root is a legitimate override.

Naming comes from the same `IndexNaming` function as everything else, per the "compute the model once, project
both outputs" rule: the root is the pluralized index-entity name, so `VCar` → `VCars`. That rule exists
because the reference design derived names in two independent traversals, which is the classic source of
"the index and the context disagree" bugs.

## W14 — Organic full-text search

Reframed by S7–S9 and PRD F14–F16. The ticket reads "nothing calls `.Search(...)` yet", which understates it in
one direction and overstates it in another: search is **already wired from the search box to the database call**,
and the server implements it as an in-memory `Contains` over the **fully materialized collection**. So W14 is a
pushdown replacing a pathological implementation, not a new feature.

What that changes versus the original W14 sketch:

- **R42 dissolves** — the Angular search box, `searchTerm` state and `?search=` parameter all exist. Server-only
  release, as the release notes already claim.
- **R38 is resolved and inverted** — `[Search]` does not gate searchability (S7), so neither candidate mechanism
  is needed. `AttributeRenderer` and its pinning test are untouched. The companion-detection option was wrong
  anyway: `DateTimeOffset` gets a companion while indexed `Exact`.
- **R41 is resolved for free** — per-language fields are plain strings on the projection, so all languages are
  searched with no `RequestCultureResolver` coupling.
- **R44 is new and mandatory** — never pass `SearchOptions` explicitly (S8), or row-level security gets OR-ed.
- **R43, R45–R47 are new** — search-aware `TotalRecords`, an in-memory fallback for non-Raven `Custom.` queries,
  the documented `Breadcrumb` narrowing, and streaming explicitly out of scope.

### One decision still open

**Matching semantics** (PRD "The open decision"): **(a)** wildcard-wrap each word for substring parity with
today's behaviour, or **(b)** true term-based organic search with relevance ranking, which only works on
`[Search]` fields and drops infix matching. Recommendation is **(a)** — it is the only option with no
user-visible regression, and (b) can be layered on afterwards as an opt-in. This decides whether the two existing
search tests keep passing or get rewritten, so it is settled before any code is written.

### Steps

1. Thread `search` into `ExecuteDatabaseQueryAsync` and `ExecuteCustomQueryAsync` instead of post-filtering.
2. `ResolveSearchableProperties(Type sortType)` — string-typed properties, `Exact` excluded — cached through
   `ReflectionCache` on the identity-keyed tier, next to `ResolveSortProperty`.
3. `ApplySearch(queryable, elementType, term)` — reflective `LinqExtensions.Search<T>` following the
   `ApplyProjection`/`RowSecurity.Where` shape. Watch three things the existing helpers do not hit: the first
   parameter is the **generic** `IQueryable<T>` (so the `== typeof(IQueryable)` predicate style must not be
   copied); the selector is `Expression<Func<T, object>>` so a property access needs
   `Expression.Convert(..., typeof(object))`; and `MethodInfo.Invoke` does not apply optional parameter defaults,
   so all six arguments are passed explicitly.
4. Insert between `ComposeRowFilterAsync` and `ApplySorting`, gated on `IsRavenQueryable` for the custom path.
5. Guard per R40, and keep the in-memory filter as the fallback per R45.
6. Restore search-aware `TotalRecords` from RavenDB statistics (R43).
7. Guide + release-note updates for the `Breadcrumb` narrowing (R46) and the streaming asymmetry (R47).

### Tests

- The two existing tests are the canary — `QueryExecutorIntegrationTests.cs:162,176` and
  `ExecuteQueryEndpointTests.cs:83-90` pin substring semantics (`"alice"`, `"SMITH"`, `"ALICE"`). Under
  option (a) they must pass **unchanged**; that is the whole argument for (a).
- Multi-word term matches regardless of word order.
- **RQL shape test**: with row-level security active, assert `(predicate) and (search or search)` — this is the
  F16 bypass, and it fails by returning extra rows rather than by erroring, so only a shape assertion catches it.
- Empty term, whitespace term and a bare `*` each leave the result set exactly as an unsearched query returns it.
- A query type with no searchable field is unaffected.
- `TotalRecords` reflects the filtered count, not the collection count, on a paged search.
- A `DateTimeOffset`/`Exact` field does not participate.

## Test strategy

Per the harness investigation: **in-memory `CSharpGeneratorDriver` via the existing
`GeneratorHarness`, not `MSBuildWorkspace`.** Reasons in short — CI is ubuntu-only on both workflows,
`MSBuildLocator` is process-global and would collide with the existing `VerifyDefaults` module
initializer, `OpenProjectAsync` degrades silently to a partially-populated project, and an
MSBuildWorkspace test reading a sibling project's `bin/` races the Nx cache (the preview.43 failure
mode). The fidelity it would add is already covered by `InMemoryAdditionalText`,
`StubAnalyzerConfigOptionsProvider` and `CompileToMetadataReference`.

Real-build fidelity comes from MSBuild itself instead, via the pattern already in the repo: the E2E
project references generators with `OutputItemType="Analyzer"` and feeds them `AdditionalFiles`, so
generated code that does not compile fails the build. W9 extends that to this generator.

Three tiers:

1. **Generator unit tests** — `tests/MintPlayer.Spark.SourceGenerators.Tests/Generators/`, structural
   `Should().Contain(...)` assertions plus Verify snapshots of full emitted pairs.
2. **Framework unit/integration tests** — `tests/MintPlayer.Spark.Tests/` for W7, including the S1
   sortability guard against a real embedded RavenDB.
3. **Real-build check** — a demo app whose compilation and startup depend on generated output.

Note `MintPlayer.Spark.SourceGenerators.Tests` deliberately does not reference
`MintPlayer.Spark.Testing` (RavenDB dependency) and duplicates `VerifyDefaults`; the S1/W7 RavenDB tests
therefore live in `MintPlayer.Spark.Tests`, not the generator project.
