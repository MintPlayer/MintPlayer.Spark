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
  compilation-start action resolving `AbstractIndexCreationTask`1`/`AbstractMultiMapIndexCreationTask`1` via
  `GetTypeByMetadataName` + `[DefaultIndex]` by full-name; collect marked types keyed by collection-type
  symbol (base-type walk, `SymbolEqualityComparer.Default` on `OriginalDefinition`); compilation-end reports
  on each duplicate, anchored on hand-written declaration locations only (generated locations are suppressed
  by `GeneratedCodeAnalysisFlags.None`). Message names both indexes, the collection type, and the fix.
- Tests (`tests/MintPlayer.Spark.SourceGenerators.Tests`): `Diagnostics/DefaultIndexAnalyzerTests.cs`
  (RavenStub pattern from `SortCompanionAnalyzerTests.cs`; cases: duplicate ⇒ SPARK009, single ⇒ clean,
  different collections ⇒ clean, opt-out + hand-marked ⇒ clean — SP279.4/5); emission + opt-out `[Fact]`s in
  `Generators/GenerateIndexGeneratorTests.cs` (text-contains style).

## M2 — `IIndexCatalog` (alongside the registry, callers migrate in M3–M4)

- `libs/spark/MintPlayer.Spark/Services/IndexCatalog.cs`: entry
  `IndexCatalogEntry { IndexName, IndexType, CollectionType, ProjectionType, IsDefault }`; singleton
  `[Register]`; populated by the existing two-pass scan (`PopulateIndexTypes`/`PopulateProjectionTypes`
  retargeted or duplicated onto the catalog) then **frozen** (`Freeze()` validates and seals; mutation after
  freeze throws).
- Surface: `GetByIndexName(string)` (OrdinalIgnoreCase), `GetDefaultForCollectionType(Type)`,
  `GetAllEntries()`.
- Freeze-time validation (R279.5/R279.6, authoritative cross-assembly check): per collection type over
  projection-bearing entries — 2+ entries with zero or 2+ `[DefaultIndex]` ⇒ error naming candidates;
  `[DefaultIndex]` on a projection-less index ⇒ error. `IsDefault` computed at freeze: single
  projection-bearing entry ⇒ implicit default; else the marked one.
- Wire the catalog everywhere the registry is built: `SparkMiddleware.CreateSparkIndexes` (:485-507),
  `SparkDevelopmentExtensions.TryBuildIndexRegistry` (:214-238) and `WriteSparkModelHashes` (:123-141) —
  runtime startup + all three offline commands get the validation for free.
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
- Update the NSubstitute seams in QueryExecutor/DatabaseAccess tests (ctor arity changes: registry → catalog).

## M4 — synchronizer

- `ModelSynchronizer`: ctor takes `IIndexCatalog`; default projection via `GetDefaultForCollectionType`
  (:103-105); `IsProjectionType` call sites (:60/:91) → `IsSparkProjection()`; stale-projection cleanup
  (:230-241) enumerates catalog entries; `ModelShapeDiscovery` (:38/:41/:84) likewise.
- Stamp query `indexName` (R279.2): minting loop (:126-147) sets `IndexName` to the entity's default entry's
  name; update pass applies stored==derived provenance — stored empty ⇒ stamp; stored == derived default ⇒
  machine-owned (retarget/clear with console note when the default changes/disappears); anything else ⇒
  authored, preserved, but validated against the catalog (unknown name fails synchronize). `Custom.*`
  untouched.
- Hash coverage (R279.9): `ModelFileShape.Describe` appends a structural line per query with non-null
  `indexName` (absent ⇒ no line, preserving old hashes until stamping; same sync rewrites
  `modelHashes.json`).
- Preserve the F279.14 invariant: no projection ⇒ no entity-file binding and no stamped query name.
- Rewrite superseded synchronizer tests (`Hand_authored_indexName_on_a_query_is_never_cleared` becomes the
  provenance triple: authored-preserved / machine-retargeted / machine-cleared; registry stubs → catalog).

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
- New integration tests (patterns from `QueryExecutorAdvancedIntegrationTests` / SparkTestDriver, Corax
  pinned): the #272 motivating case — `[GenerateIndex]` + hand-written index on one entity, one query per
  index, both grids correct (per-query binding honored, non-default projection null-fills gracefully —
  SP279.1/SP279.3 pins); unknown `indexName` ⇒ error; entity-file fallback path.
- Full test suite + E2E; fix fallout. (Flaky-under-load caveat: re-run named tests in isolation before
  calling a regression.)
