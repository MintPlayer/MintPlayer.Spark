# Plan — issue #279: query-declared index bindings replace IndexRegistry's ambient resolution

PRD: `docs/issue_279_PRD.md`. Branch: `feat/issue-279-query-declared-index-bindings`. One commit per
milestone; each milestone builds clean; the full test suite + E2E runs once in M6 (intermediate milestones
are verified by build + targeted reasoning, per working convention).

## M1 — `[DefaultIndex]` attribute, generator emission, SPARK009 analyzer

- `libs/spark/MintPlayer.Spark.Abstractions/DefaultIndexAttribute.cs` — `[AttributeUsage(AttributeTargets.Class,
  AllowMultiple = false)] public sealed class DefaultIndexAttribute : Attribute`, doc comment stating the
  contract (marks the index whose `[FromIndex]` projection shapes the entity's model file; required when an
  entity has 2+ projection-bearing indexes).
- `GenerateIndexAttribute`: add `public bool IsDefault { get; set; } = true;` (doc-comment style matching
  `IndexName`/`Description`); rewrite the stale paragraph at :25-29 (R279.11 lands here, release-notes half in M6).
- Generator (`MintPlayer.Spark.SourceGenerators`, conventions per `MintPlayer.SourceGenerators.Tools` —
  `IncrementalGenerator` + `Producer` + `[AutoValueComparer]` info models):
  - bool sibling of `GetNamedArgument` (`GenerateIndexGenerator.cs:723-733`), absent ⇒ `true`;
  - `IsDefault` on `GeneratedIndexInfo` (comparer regenerates), populated in `Describe` (~:390-407) and
    `DescribeReferenced`;
  - emit `[global::MintPlayer.Spark.Abstractions.DefaultIndexAttribute]` in `WriteIndex`
    (`GenerateIndexGenerator.Producer.cs:243-248`, next to the Description line) when `IsDefault`.
- Analyzer `Diagnostics/DefaultIndexAnalyzer.cs` — **SPARK009**, category `Correctness`, Error:
  compilation-start action collecting **default claims** keyed by collection-type symbol (base-type walk,
  `SymbolEqualityComparer.Default` on `OriginalDefinition`, matched by name + `Raven.Client.Documents.Indexes`
  namespace). A claim is a `[DefaultIndex]`-marked non-abstract index class **or** — per owner direction —
  a `[GenerateIndex]` entity that has not opted out (`IsDefault = false`), claiming under its generated
  index's name so the clash with a hand-written marker never depends on the generated tree being analyzed;
  when the generated tree IS analyzed, its symbol deduplicates against the entity's claim by index name.
  Compilation-end reports each duplicate, anchored on hand-written (non-`.g.cs`, in-source) locations only —
  the generated-code filter drops other locations, and the analyzer uses `GeneratedCodeAnalysisFlags.Analyze`
  (not `None`) so generated symbols still enter the walk. Descriptor carries the `CompilationEnd` custom tag.
  Message names both indexes, the collection type, and the fix.
- Tests (`tests/MintPlayer.Spark.SourceGenerators.Tests`): `Diagnostics/DefaultIndexAnalyzerTests.cs`
  (RavenStub pattern from `SortCompanionAnalyzerTests.cs`; cases: duplicate ⇒ SPARK009 on both, single ⇒
  clean, different collections ⇒ clean, map-reduce + multi-map clashes, abstract ignored, `[GenerateIndex]`
  entity vs hand-marked index (clash / opt-out clean / alone clean / `IndexName` override in the message) —
  SP279.4/5); emission + opt-out `[Fact]`s in `Generators/GenerateIndexGeneratorTests.cs` (text-contains
  style) plus the re-verified `SourceGeneratorSnapshots` snapshot.

## M2 — `IIndexCatalog` (alongside the registry, callers migrate in M3–M4)

- `libs/spark/MintPlayer.Spark/Services/IndexCatalog.cs`: entry
  `IndexCatalogEntry { IndexName, IndexType, CollectionType, ProjectionType, IsDefault }`; singleton
  `[Register]`; populated by the existing two-pass scan (`PopulateIndexTypes`/`PopulateProjectionTypes`
  retargeted or duplicated onto the catalog) then **frozen** (`Freeze()` validates and seals; mutation after
  freeze throws).
- Surface: `RegisterIndex(Type)` / `RegisterProjection(Type, Type)` / `Freeze()` (population lifecycle),
  `GetByIndexName(string)` (OrdinalIgnoreCase), `GetDefaultForCollectionType(Type)` (throws before
  freeze), `GetAllEntries()`. Registering a second CLR type under an already-taken index name **throws**
  (the two would deploy over the same RavenDB index); re-registering the same type is idempotent.
- Freeze-time validation (R279.5/R279.6, authoritative cross-assembly check): per collection type over
  projection-bearing entries — 2+ entries with zero or 2+ `[DefaultIndex]` ⇒ error naming candidates;
  `[DefaultIndex]` on a projection-less index ⇒ error. `IsDefault` computed at freeze: single
  projection-bearing entry ⇒ implicit default; else the marked one.
- Wire the catalog into `SparkMiddleware.CreateSparkIndexes` (runtime startup; populate + freeze before
  the model-hash check). The offline commands (`TryBuildIndexCatalog`, `WriteSparkModelHashes`) move to
  the catalog in M4, when their consumers (synchronizer, verifier) switch — all paths then share the
  freeze validation.
- `IsSparkProjection()` extension (`[FromIndex]` cached-attribute check) added for M3/M4 call-site swaps.
- Unit tests for catalog semantics (default rules, ambiguity errors, case-insensitive lookup, freeze).

## M3 — runtime rewrite (kills F279.1)

- `QueryExecutor.ExecuteDatabaseQueryAsync` (:139-165): resolution chain per R279.4 —
  `query.IndexName` → `entityTypeDefinition.IndexName` (already loaded at :131) → none. Non-empty name ⇒
  `catalog.GetByIndexName`; unknown ⇒ throw naming query + index (R279.1). `resultType =
  entry.ProjectionType ?? entityType`; apply via entry's `IndexType` (existing `ApplyIndexWithType`);
  delete the registration-default branch and the `ApplyIndexByName` fallback path's registry coupling.
- `DatabaseAccess.GetPersistentObjectsAsync` (:150-158): replace the registry lookup with
  `entityTypeDefinition.QueryType`/`IndexName` + `ResolveType` (R279.4); by-name application unchanged.
- `IsProjectionType` → `IsSparkProjection()` at `QueryExecutor.cs:287`.
- Delete `SparkQuery.UseProjection` (`SparkQuery.cs:48`), the clone line (`Execute.cs:121`), and
  `useProjection` from `ng-spark/models/src/spark-query.ts` (R279.3). Fix the `indexName` TS comment
  (references a nonexistent `[QueryType]`).
- Sort-drop warning (R279.10): `ApplySorting` logs when a sort column resolves to no property.
- `ApplyIndexByName` deleted outright (nothing reaches it once resolution goes through the catalog's
  `IndexType`), taking the `_`→`/` name normalization in QueryExecutor with it.
- `SparkEndpointFactory` gains an optional `configureIndexCatalog` hook, invoked before `UseSpark()`
  freezes the catalog — fixture indexes are nested test classes the assembly scan must not discover
  wholesale (fixtures for the catalog's own error cases would fail every host), so arming is explicit
  and per fixture.
- Update the NSubstitute seams in QueryExecutor/DatabaseAccess tests (ctor arity changes: registry →
  catalog); rewrite the ambient-default integration pins to declared bindings, and add new pins:
  unknown `indexName` ⇒ throws; entity-file fallback path works.

## M4 — synchronizer

- `ModelSynchronizer`: ctor takes `IIndexCatalog`; default projection via `GetDefaultForCollectionType`
  (:103-105); `IsProjectionType` call sites (:60/:91) → `IsSparkProjection()`; stale-projection cleanup
  (:230-241) enumerates catalog entries; `ModelShapeDiscovery` (:38/:41/:84) likewise.
- Stamp query `indexName` (R279.2): minting loop (:126-147) sets `IndexName` to the entity's default entry's
  name; update pass applies stored==derived provenance — stored empty ⇒ stamp (behavior-preserving: empty
  already fell back to the entity-file binding); stored == derived default ⇒ machine-owned, already correct;
  stored names a **known** index ⇒ authored, preserved verbatim; stored names a **dead** index (renamed or
  removed) ⇒ retarget to the default (or clear when there is none) **with a console note** — failing there
  would make synchronize unable to repair the very drift it exists to repair. `Custom.*` untouched.
- Offline commands move to the catalog here: `TryBuildIndexRegistry` → `TryBuildIndexCatalog` (populate +
  freeze; a freeze error prints and exits misconfigured), `WriteSparkModelHashes` builds and freezes a
  catalog, `ModelHashVerifier.Verify` and `BuildModelHashes` take `IIndexCatalog`,
  `ModelShapeDiscovery.RootEntityNames` drops its registry parameter (pure `[FromIndex]` check).
- Hash coverage (R279.9): `ModelFileShape.Describe` appends a structural line per query with non-null
  `indexName` (absent ⇒ no line, preserving old hashes until stamping; same sync rewrites
  `modelHashes.json`).
- Preserve the F279.14 invariant: no projection ⇒ no entity-file binding and no stamped query name.
- Rewrite superseded synchronizer tests (`Hand_authored_indexName_on_a_query_is_never_cleared` becomes the
  provenance suite: stamp-at-mint / stamp-on-resync / authored-known-preserved / dead-retargeted-with-note /
  dead-cleared-without-default; registry stubs → catalog; `MS_TestVehicle` gains a real `[FromIndex]`
  since projection-ness is now the attribute, not a mock).

## M5 — delete `IndexRegistry`

- Remove `Services/IndexRegistry.cs` (interface, impl, `IndexRegistration`), its `[Register]`, the two-pass
  populate helpers' registry overloads, `ModelHashVerifier`'s registry parameter, and every remaining
  reference (`SparkMiddleware`, `SparkDevelopmentExtensions`, OIDC test constructions).
- `IndexRegistryTests.cs` → deleted; ordinal-min pins deliberately superseded (already re-covered by M2
  catalog tests). Sweep remaining mocks (`ModelHashVerifierTests`, `ModelHashWriteTests`,
  `SynchronizeIdempotencyTests`, `SparkExtensionsPrivateHelpersTests`, row-security tests).

## M6 — demos, docs, release notes, sweep

- Re-sync all four demo apps: expect stamped `indexName` on minted `Database.*` queries, `useProjection`
  gone, `modelHashes.json` rewritten — and nothing else (minimal-diff check).
- Release notes (consumer migration per PRD §Migration); `docs/adopt-spark-preview-55.md`-style upstream
  notes if applicable.
- New integration tests (`QueryDeclaredIndexBindingTests`, SparkTestDriver pattern): the #272 motivating
  case — two coexisting indexes on one entity ([DefaultIndex]-elected overview + specialized index), one
  query per index, both correct through the real freeze validation; projection-missing columns null-fill;
  sorting on a projection-missing column degrades with a warning (SP279.1/SP279.3 pins). Unknown-name and
  entity-file-fallback pins landed in M3. `[GenerateIndex]` emission itself is pinned by the M1 generator
  tests + snapshot; the runtime case uses hand-written twins of the generated shape.
- Version bumps: all `MintPlayer.Spark.*` → `10.0.0-preview.56`; `@mintplayer/ng-spark` → `22.1.0`
  (TS model lost `useProjection`). Release notes: `docs/release-notes-preview-56.md`.
- Full test suite; fix fallout. (Flaky-under-load caveat: re-run named tests in isolation before calling a
  regression. E2E runs in CI — known teardown flake is unrelated.) Two defects found and fixed by the
  sweep, now part of the design:
  - `WriteSparkModelHashes` gains a `configureIndexCatalog` overload and `SparkEndpointFactory` threads
    its hook through — the hash writer and the startup verifier must compute through the **same** catalog,
    or every fixture-armed test host fails `SparkModelOutOfSyncException` at startup.
  - Map-less `AbstractIndexCreationTask<T>` fixture classes (catalog unit tests) must be `abstract`, or
    the smoke tests' assembly-wide `IndexCreation.CreateIndexes` faults on them ("Map is required").
