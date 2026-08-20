# PRD — issue #279: query-declared index bindings replace IndexRegistry's ambient resolution

Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/279 · Successor to #272 (preview.55 made the
ambient default *deterministic*; this makes the binding *declared* and deletes the ambient mechanism).
Breaking changes allowed — no backward compatibility required (owner's direction, 2026-08-20).
All findings re-verified against this tree (preview.55) by a four-agent census, 2026-08-20.

## Problem

A collection entity can back dozens of RavenDB indexes (measured prior art: Vidyano/Fleet has 17 indexes on
the Car entity alone). `IndexRegistry` was born keying one registration per collection type; #210/PR #269
documented that as a "hard ceiling" (F6, `issue_210_PRD.md:249-255`); #272 restructured it to retain-all +
ordinal-min default. The default is now deterministic but still **ambient**: the runtime resolves index and
projection from the collection type, ignoring the bindings the model already declares. The intended model
is: **one Spark query = one RavenDB index** — every consumer that queries through an index names its Spark
query; every query declares its index; `[Reference]` attributes already bind via `"query"`. Nothing should
resolve by collection type at runtime.

## Findings

- **F279.1 — a declared `query.IndexName` is silently overridden.** `QueryExecutor.cs:139-165` reads
  `query.IndexName` (:140) but fetches the registration by collection type (:142). `IndexRegistration.IndexType`
  is `required` (IndexRegistry.cs:62), so any registered collection has one — and :152-155 takes
  `indexType = registration?.IndexType` unconditionally: when both are non-null, `ApplyIndexWithType` runs on
  the **default's** type and `resultType` comes from the **default's** projection (:143-145). The declared
  string only serves as a non-empty trigger. `ApplyIndexByName` (:552-571, honors the string, `_`→`/` at
  :569) is reached only when the collection has **zero** registrations — and then no projection engages.
- **F279.2 — the correct lookup exists and is unused.** `GetRegistrationByIndexName` (`IndexRegistry.cs:160`,
  case-insensitive) has zero production callers; `GetRegistrationsForCollectionType` (:150, added in
  preview.55) is called only by its own tests.
- **F279.3 — the model already declares the entity-level binding, and nothing reads it back.**
  `EntityTypeDefinition.QueryType`/`IndexName` (`EntityTypeDefinition.cs:20,26`) are written by the
  synchronizer (`ModelSynchronizer.cs:429-430`), hashed twice (`ModelFileShape.cs:91-92`,
  `SparkModelShape.cs:58-62`), serialized to the wire (`Endpoints/EntityTypes/List.cs:25`) — and read by no
  runtime code. The PO-list path (`DatabaseAccess.cs:150-158`) re-resolves ambiently while holding the
  `EntityTypeDefinition` it loaded at :140.
- **F279.4 — `SparkQuery.UseProjection` is dead on both sides.** Server: declaration (`SparkQuery.cs:48`) +
  one clone/echo (`Endpoints/Queries/Execute.cs:121`); never branched on, never written by the synchronizer.
  Client: one optional field declaration (`ng-spark/models/src/spark-query.ts:21`, whose comment references a
  `[QueryType]` attribute Spark doesn't have); no component or service reads it. Every demo model JSON
  carries `"useProjection": false` as dead weight (17 occurrences). Deletion is zero-behavioral-change.
- **F279.5 — every other registry use is a disguised `[FromIndex]` scan.** `IsProjectionType`
  (QueryExecutor.cs:287, ModelSynchronizer.cs:60/:91, ModelShapeDiscovery.cs:38/:84) is answerable by a
  `[FromIndex]` attribute check — the registry is populated *from* that attribute
  (`SparkMiddleware.cs:417-430`). `GetAllRegistrations` (ModelSynchronizer.cs:230, stale-projection-file
  cleanup) enumerates what a `[FromIndex]` scan enumerates. The collection type is derivable per index from
  the base-type walk in `IndexRegistry.GetCollectionTypeFromIndex` (:186-208, covers
  `AbstractIndexCreationTask<T>` and `AbstractMultiMapIndexCreationTask<T>`).
- **F279.6 — deployment never depended on the registry.** `SparkMiddleware.cs:511-522` deploys via
  `IndexCreation.CreateIndexes(assembly, store)` straight off the assembly list; SparkTestDriver uses
  `RavenIndexHelper.DeclaredIndexNames`, not the registry.
- **F279.7 — per-query `indexName` is unhashed.** `ModelFileShape.Describe` reads only `persistentObject`
  (`ModelFileShape.cs:85`); the `queries` array is invisible to `--spark-verify-model` (explicitly listed as
  ignored, `SparkModelShape.cs:28-31`). Stamping `indexName` on queries therefore moves no hash today — but
  once the field is load-bearing, a hand-edit changes runtime behavior with no gate signal (see SP279.2,
  **decided: hash it**).
- **F279.8 — the founding rationale constrains where the declaration lives, not whether it exists.** The
  registry exists so the entity/library never names its indexes ("the arrows all point one way — app →
  library", `issue_210_PRD.md:620-637`). The app-side reverse arrows already exist: index → entity (generic
  argument), view → index (`[FromIndex]`), model JSON → both. `[DefaultIndex]` is typeless and lives in
  Abstractions, so it drags nothing anywhere.
- **F279.9 — Vidyano prior art (Fleet).** No registry. Query → `Source` (context member) → code picks the
  index per query (`Query<VCar, Cars_Overview>()` vs `Query<VCar, Cars_Archived>()` — same view, different
  index). References bind via `LookupId` → a full query. Grid columns are per query; genuinely different
  projections get their own PO. The `[QueryType]`/`[FromIndex]` default chain is codegen convenience, not a
  resolution mechanism.
- **F279.10 — one grid shape per entity is baked in downstream, and it degrades gracefully.**
  `EntityTypeDefinition` merges exactly one projection (`ModelSynchronizer.cs:448-499,552-565,617-632`);
  ng-spark computes columns from the entity type definition (`spark-query-list.component.ts:306-310`).
  `EntityMapper.PopulateAttributeValues` fills by exact name match and **silently null-fills** attributes
  with no matching property (`EntityMapper.cs:222-224`, documented contract at :49-51 "Vidyano parity");
  the grid renders those as empty cells. So per-query projections are *usable* before per-query grid shapes
  exist — SP279.3 is answered by existing behavior; an integration test pins it.
- **F279.11 — stale doc surface.** `GenerateIndexAttribute.cs:25-29` still describes pre-preview.55 behavior
  ("at most one index per entity… reported as a diagnostic") — wrong on both counts.
- **F279.12 — QueryExecutor and DatabaseAccess apply indexes through different mechanics.** QueryExecutor:
  `Query<TEntity, TIndexCreator>()` via reflection then `ProjectInto` (:527-542, :579-600). DatabaseAccess:
  by-name `Query<TProjection>(ravenIndexName)` with the **projection** as the generic argument then
  `ProjectInto` (`DatabaseAccess.cs:413-440`). By-name catalog resolution converges both onto one shape.
- **F279.13 — a sort column missing from the sort type is silently dropped.** `ApplySorting`
  (`QueryExecutor.cs:626-652`): unresolvable property → `continue` (:631-632), no warning, RavenDB default
  order. The `?sortColumns=` endpoint allow-list (`Execute.cs:47-78`) checks the *model attribute set*, not
  the projection's CLR properties, so a model attribute absent from the projection passes the gate and then
  no-ops. `ResolveSortProperty` (:796-803) redirects to `{Name}Sort` only when the companion exists on the
  sort type and carries `[IgnoreProperty]` — convention-derived, never read from the model (persisting the
  name was deliberately rejected, doc comment :777-795).
- **F279.14 — "no projection ⇒ no indexName" is a hash-relevant invariant.** `ModelSynchronizer.cs:430`
  writes `IndexName` only when a projection exists; `ModelShapeDiscovery.cs:42-48` mirrors it in the shape
  hash. An index without a `[FromIndex]` projection contributes no binding to the model — default selection
  must preserve this (or consciously migrate hashes).
- **F279.15 — provenance patterns available to replicate.** #275 query gating decides "machine domain" from
  stored structural companions (`ModelSynchronizer.cs:567-606`); #274 showedOn intersects and self-heals
  (:620-626); #276 retarget treats "stored == derived" as machine-owned (alias rule :783). Once the
  synchronizer starts writing query `indexName` there is no companion field — so the applicable template is
  the literal **stored == derived ⇒ machine-owned** comparison. The existing test
  `Hand_authored_indexName_on_a_query_is_never_cleared` (ModelSynchronizerTests.cs:1003, reason string "the
  synchronizer never wrote indexName — every value is authored") is **directly superseded** by R279.2.

## Requirements

- **R279.1** `QueryExecutor` resolves the index and projection **by the query's declared `indexName`**
  (name-keyed catalog lookup); the declared name is authoritative. A `query.IndexName` naming an unknown
  index is an **error**, not a fallback.
- **R279.2** A minted `Database.*` query is **stamped** with the entity's default `indexName` at mint time,
  and existing values are maintained on every synchronize, provenance-gated per F279.15: an empty value is
  machine domain and gains the default; a value equal to the derived default is machine-owned and already
  correct; a value naming a **known** index is authored and preserved verbatim; a value naming a **dead**
  index (renamed or removed) is retargeted to the default — or cleared when there is none — with a console
  note, because failing there would make synchronize unable to repair the very drift it exists to repair.
  `Custom.*` queries are never stamped or touched.
- **R279.3** `SparkQuery.UseProjection` is deleted (server model, wire echo, ng-spark model, demo JSON via
  re-sync). Whether a query projects is derived from whether its resolved index has a `[FromIndex]`
  companion.
- **R279.4** Runtime resolution is a declared-only chain, identical in both paths:
  `query.indexName` (when set) → **entity model file's `queryType`/`indexName`** (the model-declared default,
  F279.3 — becomes load-bearing) → no index (raw collection). The query-less PO-list path
  (`DatabaseAccess.GetPersistentObjectsAsync`) starts at step 2. **No collection-type lookup remains anywhere
  at runtime.** (The entity-file fallback also keeps an un-restamped model behaving correctly instead of
  silently null-filling computed fields.)
- **R279.5** The per-entity default is authored via a new **`[DefaultIndex]` attribute on the index class**
  (in `MintPlayer.Spark.Abstractions` — typeless, arrow rule holds). Enforcement is two-layered: Roslyn
  analyzer **SPARK009** errors when two claims over the same collection type collide — two `[DefaultIndex]`
  markers, or a `[DefaultIndex]` index beside a `[GenerateIndex]` entity that has not opted out (the entity
  counts as a claim for its generated index, so the clash never depends on generated-tree analysis)
  (compile-time DX, single compilation), and catalog-build validation is the authoritative cross-assembly check, reached by
  runtime startup, `--spark-synchronize-model`, `--spark-verify-model`, and the test-host hash writer alike
  (the SPARK_INDEX_004 lesson: analyzer-only guards don't cover `AddIndexesFrom(...)` and can be disabled).
- **R279.6** Default rules operate over **projection-bearing** entries (F279.14): zero → no default, entity
  file carries no binding (unchanged invariant); exactly one → implicitly default; two-plus → exactly one
  `[DefaultIndex]` required, zero or two-plus markers → **error naming the candidates**. `[DefaultIndex]` on
  a projection-less index is an error (it cannot shape the entity file — misconfiguration, not a no-op).
  `[GenerateIndex]` emits `[DefaultIndex]` on its generated index by default (it is the generic-surface
  index) with an `IsDefault = false` opt-out. No ordinal-min, no guessing, ever.
- **R279.7** `IndexRegistry` (interface + service in `Services/IndexRegistry.cs`, `IndexRegistration`,
  retain-all + ordinal-min machinery, the preview.55 plural API) is **deleted**. Replacement: `IIndexCatalog`
  — name-keyed (OrdinalIgnoreCase) entries `(IndexName, IndexType, CollectionType, ProjectionType,
  IsDefault)`, populated once from the existing two-pass assembly scan and frozen, used by runtime,
  synchronizer, and offline commands so their answers cannot diverge. `IsProjectionType` becomes a
  `[FromIndex]` attribute check (extension method), removed from the service surface.
- **R279.8** Constraints preserved: program units reference queries by `Id` (`Endpoints/ProgramUnits/Get.cs`
  — verified index-free); idempotent synchronize (fixed-point); #274/#275/#276 provenance; row security
  filters on the base entity type (`QueryExecutor.cs:178,203-204,213`, `DatabaseAccess.cs:166-172`);
  detail/edit loads the full entity; model-hash gate honored (see Migration).
- **R279.9** Per-query `indexName` **enters the file hash** (SP279.2 decision): `ModelFileShape` appends a
  structural line per query carrying a non-null `indexName`. Absent field ⇒ no line, so un-stamped files
  hash as before; the same synchronize that stamps also rewrites `modelHashes.json`, making the migration
  atomic. A later hand-edit to a stamped value then trips `--spark-verify-model`/the startup gate.
- **R279.10** A sort column that resolves to no property on the sort type is logged (console warning naming
  the query, column, and sort type) instead of today's silent drop (F279.13, SP279.1 floor: no silent wrong
  ordering).
- **R279.11** `GenerateIndexAttribute.cs:25-29` doc comment corrected; release notes carry the consumer
  migration steps.

## Design

Resolution becomes a fully declared chain, converged across both runtime paths (F279.12):

```
grid/lookup/PO-list → Spark query (by name or Id) → query.indexName ┐
                                    entity file queryType/indexName ┴→ catalog[name] → (IndexType, ProjectionType)
```

- **`IIndexCatalog`** replaces the registry: same DI lifecycle (singleton, populated in
  `CreateSparkIndexes`; offline paths build a fresh instance via the existing
  `PopulateIndexTypes`/`PopulateProjectionTypes` two-pass over `ResolveIndexAssemblies()`), then **frozen**.
  Surface: `GetByIndexName(string)`, `GetDefaultForCollectionType(Type)` (synchronizer-only consumer,
  enforces R279.6 with a candidates-naming error), `GetAllEntries()`. Validation (R279.5/R279.6 errors) runs
  at freeze, so runtime startup and all three offline commands get it for free.
- **QueryExecutor**: `indexName = query.IndexName`, falling back to
  `entityTypeDefinition.IndexName` (already loaded at :131). Non-empty ⇒ catalog lookup (unknown ⇒ throw
  naming the query and the name); `resultType = entry.ProjectionType ?? entityType`; apply via the entry's
  `IndexType`. Empty ⇒ raw collection. The registration-default branch (:142-150) is deleted.
- **DatabaseAccess**: read `entityTypeDefinition.QueryType`/`IndexName` (loaded at :140), resolve the
  projection CLR type via the existing `ResolveType`; index application unchanged (by name, F279.12 —
  this path never needed the CLR index type).
- **Synchronizer**: unchanged merging semantics (one default projection shapes the entity file), but the
  default comes from `catalog.GetDefaultForCollectionType` (R279.6) instead of `registrations[0]`; stamps
  and maintains query `indexName` with stored==derived provenance (R279.2); stale-projection cleanup
  enumerates catalog entries (same data, no behavioral change); `ModelShapeDiscovery` emits the same
  `querytype`/`index` lines, fed by the catalog — same values, no shape-hash movement.
- **`[DefaultIndex]`** (`MintPlayer.Spark.Abstractions`, `AttributeUsage(Class)`, no members) + SPARK009
  analyzer in `MintPlayer.Spark.SourceGenerators/Diagnostics/` (compilation-start collect by collection-type
  symbol via base-type walk to `AbstractIndexCreationTask<T>`/`AbstractMultiMapIndexCreationTask<T>`,
  compilation-end report duplicates; anchor diagnostics on hand-written declarations — generated-code
  locations are suppressed by `GeneratedCodeAnalysisFlags.None`). `[GenerateIndex]` gains
  `public bool IsDefault { get; set; } = true`; the producer emits the marker next to the `Description`
  attribute line (`GenerateIndexGenerator.Producer.cs:245`).

## Spikes (resolved during investigation where evidence sufficed)

- **SP279.1 — sort/companion behavior on a non-default index: DECIDED.** `ResolveSortProperty` already
  falls back to the raw name when the companion is missing from the projection; the remaining gap is the
  silent drop of fully-unresolvable columns (F279.13) — closed by R279.10 (log, don't throw: the endpoint
  allow-list already 400s unknown *model* names; projection-missing model names degrade to unordered with a
  warning). Pinned by a two-projection unit test in M6.
- **SP279.2 — hash per-query `indexName`: DECIDED, hash it** (R279.9). Rationale: the entity-file fallback
  (R279.4) removes the un-resynced-model hazard, but a hand-edited stamped value would otherwise change
  runtime behavior with zero gate signal — the exact gap the gate exists to close. Migration is atomic
  within one synchronize.
- **SP279.3 — EntityMapper degradation on non-default projection: RESOLVED by evidence** (F279.10). Null-fill
  is documented contract; grid renders empty cells. Integration test in M6 pins it end-to-end.
- **SP279.4 — analyzer scope: CONFIRMED feasible.** Same-compilation duplicates only; cross-assembly is the
  catalog's job (R279.5). Generated trees are visible to the analyzer (analyzers run after generators), but
  diagnostics must anchor on hand-written locations.
- **SP279.5 — generator emission: CONFIRMED feasible.** Marker emitted into `SparkGeneratedIndexes.g.cs`;
  opt-out via `IsDefault = false` named argument (needs a bool sibling of the string-only
  `GetNamedArgument`, `GenerateIndexGenerator.cs:723-733`). Verify no double-report when a consumer opts out
  and hand-marks their own index.

## Migration (consumers)

- Run `--spark-synchronize-model` once after upgrading. Effects: minted `Database.*` queries gain a stamped
  `indexName`; `useProjection` disappears from model JSON; `modelHashes.json` is rewritten (the stamped
  queries now hash, R279.9). Until re-sync, the runtime behaves identically via the entity-file fallback
  (R279.4); the startup gate accepts the old files (their hashes still match) — re-sync is required only to
  adopt the new stamped shape, not to boot.
- ng-spark: `useProjection` leaves the TS model (breaking, allowed; nothing reads it — F279.4).
- Entities with multiple projection-bearing indexes and no `[DefaultIndex]` fail catalog build with a
  named-candidates error — add one marker (the `[GenerateIndex]` default covers generated indexes).
- MintPlayer/CodeCoverage: unblocks `[GenerateIndex]` on `Commit` alongside `Commits_ByRepository` with
  explicit per-query bindings — the #272 motivating case, finally without a tiebreaker. Supersedes the §4
  upstream ask in CodeCoverage's `docs/adopt-spark-preview-55.md`.

## Out of scope / follow-ups

- **Per-query grid shapes** (per-query column sets / separate PO per specialized projection, as Vidyano
  does): a client-contract change over `EntityTypeDefinition` + ng-spark; file after this lands. F279.10
  pins the graceful-degradation floor until then.
- Hashing the `queries` array beyond `indexName` (R279.9 covers `indexName` only).
- Whether the wire `EntityTypeDefinition.queryType`/`indexName` should be trimmed from the client payload
  (F279.3 wire nuance) — harmless today, revisit with per-query grid shapes.
