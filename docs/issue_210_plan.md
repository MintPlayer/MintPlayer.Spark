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
| W3 | `[Search]` → search indexing + `Sort` companion | R8, R8a |
| W4 | `DateTimeOffset` → `Exact` + automatic companion | R9 |
| W5 | Reference / lookup carry-over, `[IgnoreForIndex]` | R3, R12 |
| W6 | Referenced-assembly entity discovery | R15 |
| W7 | `SortExpression` model field + `QueryExecutor` redirect | R21–R23 |
| W8 | `culture.json` AdditionalFile + `TranslatedString` fan-out | R10, R17 |
| W9 | Convert one demo entity; re-synchronize model + hashes | F7, N5 |
| W10 | Guides, release notes, version bump | — |
| W11 | Missing-sort-property analyzer + "Add Sort property" code fix | R24–R27 |
| W12 | Lean entity-side generator project for `*.Library` | F12 |

Ordering rationale: W2–W5 are the proven parts of the reference design and ship first. W7 is what makes
the companions actually used (PRD F8) and is independent of the generator, so it can land in parallel.
W8 is the novel, prior-art-free part (PRD F9) and is deliberately last, so a problem there cannot block
everything else.

---

## Spikes

Three unknowns are load-bearing enough that a negative result changes the shape of the work. All three
are run before W1.

### S1 — is the sort companion actually sortable without an `Index(...)` call?

**The premise of the entire feature.** PRD F1 claims a field with no explicit indexing gets
`FieldIndexing.Default`, stays a single un-tokenized term, and therefore sorts correctly, while its
`FieldIndexing.Search` sibling does not.

If that is wrong — if the default analyzer also tokenizes, or if `StoreAllFields` alone does not make
the field sortable — then the companion needs explicit `FieldIndexing.Exact` and R8 changes, along with
the case-sensitivity behaviour that follows.

Method: a `SparkTestDriver`-based integration test with one hand-written index over documents whose
values contain spaces (`Volkswagen Golf GTI`, `Audi A4`, `alfa romeo`). Assert that ordering by the
`Search` field is wrong and ordering by the companion is correct, and record the case-sensitivity
observed. This test is kept permanently as the regression guard for the mechanism.

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
single-generator project loaded alongside the main one in every demo app. W12 copies its csproj.
Confirmed no `ComparerRegistry` collision, packaging fully inherited, and `spark.targets`' SPARK001
hard-error does not reach an in-repo `*.Library`. Detail in PRD F12.

### S6 — can an index read one language out of a flat-serialized `TranslatedString`?

**The gating unknown for R10 / W8, and the only spike whose failure changes a requirement rather than an
implementation detail.** Run against the live RavenDB on `localhost:8080` in a throwaway database.

`TranslatedString` serializes flat (`{"en":..,"nl":..}`) with no `Translations` wrapper, so the CLR path
and the JSON path disagree. Candidates tested: the dictionary indexer (does RavenDB translate it to
`Description.nl` or `Description.Translations.nl`?), dynamic fields via `CreateField`, a raw index
definition / `AdditionalSources`, and abandoning the dictionary shape for real persisted properties. For
each: does the field populate through `ProjectInto`, can it be sorted, does a space-containing value sort
correctly, and are there index errors.

Index *errors* are the key signal — a Map that cannot be translated often fails there rather than at
deploy time, which is the silent-null failure mode this whole issue is trying to eliminate.

The reference implementation offers no guidance here: it maps a `TranslatedString` whole and opaque and
picks the language at read time, has no dictionary or dynamic-field handling anywhere, and derives map
expressions from the CLR path only — so pointing a generated index at a converter-flattened field would
silently yield nulls. Their sanctioned workaround for shape mismatches is a raw expression string. This is
free design space for Spark, which is why it gets a spike rather than a port.

---

## W1 — Attributes and symbol helpers

`libs/spark/MintPlayer.Spark.Abstractions/`:

- `GenerateIndexAttribute.cs` — `[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]`,
  parameterless ctor, `IndexName` / `ViewName` / `Description`. No fan-out overload (N6).
- `SearchAttribute.cs` — `AttributeTargets.Property`.
- `IgnoreForIndexAttribute.cs` — `AttributeTargets.Property`, with a docstring stating the difference
  from `[IgnoreProperty]` (index-only exclusion vs model-wide exclusion), following the precedent set by
  `IgnorePropertyAttribute`'s docstring-as-spec.

`libs/source_generators/MintPlayer.Spark.SourceGenerators/Models/SparkModelSymbols.cs` — extend with
fully-qualified-string matchers for the three new attributes and an `IsIndexableProperty` predicate that
is the Roslyn twin of `ReflectedTypeExtensions.IsSparkModelProperty` plus `[IgnoreForIndex]` (R14).

Tests: symbol-level facts for each matcher, including the `[IgnoreProperty] + [Search]` combination
(R8a) and inherited properties (R13).

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

## W12 — Lean entity-side generator for `*.Library`

New project `libs/source_generators/MintPlayer.Spark.Entities.SourceGenerators` (name TBD), csproj copied
from `MintPlayer.Spark.AllFeatures.SourceGenerators` with a new `PackageId` and `Description`, keeping
`GeneratePathProperty="true"` and the Roslyn `Update` pins, both of which are load-bearing (PRD F12).

One generator, one job: entity-side per-language helper properties for `TranslatedString` on `partial`
entity classes, driven by an attribute added to the **existing** `MintPlayer.Spark.Abstractions` — no new
attributes package.

`SparkModelSymbols` is duplicated into the project rather than linked, matching what AllFeatures already
does and the file's own doc comment.

**Shape deferred to S6.** What these helpers can usefully be — computed properties, real persisted
properties, or something that feeds a dynamic field — depends entirely on what a RavenDB Map can actually
read out of a flat-serialized `TranslatedString`. A computed `Description_nl => Description?.GetValue("nl")`
is the obvious design and is very likely *not* indexable, since the server compiles the Map against raw
JSON and has no `TranslatedString` type. Writing this milestone before S6 reports would mean guessing.

In-repo consumers get a `ProjectReference … OutputItemType="Analyzer" ReferenceOutputAssembly="false"`;
external consumers a `PackageReference` with `PrivateAssets="all"` and `analyzers` in `IncludeAssets`. If
it should attach automatically in-repo, copy the `spark-allfeatures.targets` injection pattern.

---

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
