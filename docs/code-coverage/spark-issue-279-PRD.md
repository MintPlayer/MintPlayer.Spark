# PRD — Spark issue #279: query-declared index bindings replace IndexRegistry's ambient resolution

**Status: ✅ SHIPPED 2026-08-20 as `10.0.0-preview.56` — [Spark PR #280](https://github.com/MintPlayer/MintPlayer.Spark/pull/280),
squash-merged the same day this was filed. Issue [#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) closed.**

Successor to #272 (preview.55 made the ambient default *deterministic*; this makes the binding *declared*
and deletes the ambient mechanism). Breaking changes allowed — no backward compatibility required (owner's
direction, 2026-08-20).

> This file is the **proposal record** as filed, kept for the reasoning chain. The authoritative design
> record now lives upstream in `docs/issue_279_{PRD,plan}.md` + `docs/release-notes-preview-56.md` in
> MintPlayer/MintPlayer.Spark. Findings/requirements below matched what shipped; the **as-built deltas and
> spike outcomes are recorded in the next section**. Coverage's own upgrade plan is
> `docs/adopt-spark-preview-57.md`. File:line references are to the Spark repo (preview.55 for Findings,
> preview.56 for As-built) unless marked otherwise.

## As-built (verified against the shipped code, `MintPlayer.Spark` @ `2244af3`)

**All five spikes were resolved by the implementation** — no open questions remain:

- **SP279.1 (sort companion on a non-default index)** → *degrade, don't throw*: a sort column with no
  matching property on the resolved sort type logs `Warning: sort column '{col}' has no matching property
  on {type}; the column is skipped and rows keep their index order.` and skips (`QueryExecutor.cs:607-613`).
  This is the floor the spike demanded, though it is a `Console.WriteLine`, not `ILogger`.
- **SP279.2 (hash the per-query `indexName`?)** → **yes, it is structural now** — and the migration cost the
  spike worried about was designed away. `ModelFileShape.cs:113-129` emits a hash line for a query **only
  when it carries an `indexName`**, so a model whose queries were never stamped hashes exactly as before.
  Consumers' committed hashes stay valid, the startup gate and `--spark-verify-model` pass *before* the
  first synchronize, and the one synchronize that stamps also rewrites `modelHashes.json` atomically.
  `SparkModelShape` (the per-entity CLR hash) was not touched, so the `entities` block never moves.
- **SP279.3 (projection mismatch degrades gracefully)** → pinned by the new `QueryDeclaredIndexBindingTests`:
  two coexisting indexes over one entity, one query per index, both correct; projection-missing columns
  null-fill rather than throw.
- **SP279.4/5 (analyzer scope, generated marker)** → `SPARK009` (Error, `CompilationEnd`,
  `DefaultIndexAnalyzer.cs:36-43`) fires only when ≥2 *distinct index names* claim one collection type,
  where a claim is `[DefaultIndex]` on a non-abstract index task **or** `[GenerateIndex]` without
  `IsDefault = false` — so the entity counts as a claim for its generated index without the analyzer
  needing to see the generated tree. An unmarked hand-written index is not a claim. The catalog's
  `Freeze()` remains the authoritative cross-assembly check.

**Deltas from the proposal, worth knowing:**

1. **`IIndexCatalog` lives in `MintPlayer.Spark.Services`, not Abstractions** (`IndexCatalog.cs:19-51`).
   A test host using the new `configureIndexCatalog` hook must reference `MintPlayer.Spark` itself.
2. **`[DefaultIndex]` is fully typeless** — `sealed class DefaultIndexAttribute : Attribute { }` with no
   properties (`Abstractions/DefaultIndexAttribute.cs:20-23`). The opt-out knob went on the *entity* side
   instead: `GenerateIndexAttribute.IsDefault { get; set; } = true`.
3. **`GetDefaultForCollectionType` is not synchronizer-only** as the proposal assumed: `ModelShapeDiscovery`
   calls it too, so hash computation — and therefore the startup gate — also fails on an ambiguous default,
   not just synchronize.
4. **Election rule refined**: candidates are *projection-bearing* entries only. Zero candidates → no default
   and **no error**; exactly one → implicit default, marker unnecessary. A `[DefaultIndex]` on a
   projection-less index **throws** ("has no effect: the index has no `[FromIndex]` projection"). This is
   what makes a projection-less hand-written index (Coverage's `Commits_ByRepository`) invisible to the
   election — the #272 motivating case needs no marker and no opt-out at all.
5. **`ApplyIndexByName` was deleted along with the registry**, taking its `_`→`/` normalization: a model
   `indexName` must now be the CLR class name as the catalog keys it.
6. **Duplicate index class names now throw** at registration (`IndexCatalog.cs:92-95`) — previously
   first-wins plus a console line.
7. **Two test-infrastructure requirements emerged during the sweep** and are not in the proposal: the
   `WriteSparkModelHashes` / `SparkEndpointFactory` `configureIndexCatalog` overload (the hash writer and
   the startup verifier must compute through *one* catalog, or an armed fixture projection reads as model
   drift and the host refuses to start), and Map-less fixture index classes must be `abstract` or
   assembly-wide `IndexCreation.CreateIndexes` faults with "Map is required".
8. **Deployment note (unchanged but newly consequential):** `SparkMiddleware.cs:518-529` catches index
   creation failures *per assembly and only logs them* — so one throwing index ctor silently leaves every
   index in that assembly undeployed.

**Release mechanics:** both packages shipped — `10.0.0-preview.56` on NuGet and
`@mintplayer/ng-spark@22.1.0` on npm (tagged `latest`, verified 2026-08-20). One of the three release runs
(`32350010621`) failed its `Push ng-spark to NPM` step with `npm error 404 … PUT @mintplayer%2fng-spark`
while a sibling run published successfully — a duplicate/racing publish path, not a failed release. The
client change is only the removal of `useProjection?: boolean` from the `SparkQuery` TS model, so consumers
that never read the field — Coverage among them — take it as a no-op, but should still bump for lockstep.

## Problem

A collection entity can back dozens of RavenDB indexes (measured prior art: Vidyano/Fleet has 17 indexes on
the Car entity alone). `IndexRegistry` was born (commit `18380a3`, PR #7) keying one registration per
collection type; #210/PR #269 documented that as a "hard ceiling" (F6, `issue_210_PRD.md:249-255`); #272
restructured it to retain-all + ordinal-min default. The default is now deterministic but still **ambient**:
the runtime resolves index and projection from the collection type, ignoring the bindings the model already
declares. The intended model is: **one Spark query = one RavenDB index** — every consumer that queries
through an index names its Spark query; every query declares its index; `[Reference]` attributes already
bind via `"query"`. Nothing should resolve by collection type at runtime.

## Findings

- **F279.1 — a declared `query.IndexName` is silently overridden.** `QueryExecutor.cs:139-165` reads
  `query.IndexName` (:140) but fetches the registration by collection type (:142); when the default
  registration has an `IndexType`, `ApplyIndexWithType` uses the **default's** type (:152-155) and
  `resultType` comes from the **default's** projection (:143-145). `ApplyIndexByName` (:552-570, honors the
  string, `_`→`/`) only runs when no default is registered — and even then no projection engages.
- **F279.2 — the correct lookup exists and is unused.** `GetRegistrationByIndexName`
  (`IndexRegistry.cs:160`) has zero production callers; likewise `GetRegistrationsForCollectionType`
  (added in preview.55) is called only by its own tests.
- **F279.3 — the model already declares the entity-level binding, and nothing reads it.**
  `EntityTypeDefinition.QueryType`/`IndexName` (`EntityTypeDefinition.cs:20,26`) are written by the
  synchronizer (`ModelSynchronizer.cs:429-430`), covered by the file hash (`ModelFileShape.cs:88-92`) and
  the shape hash (`SparkModelShape.cs:58-62`), and read back by no runtime code. The PO-list path
  (`DatabaseAccess.cs:153`) re-resolves ambiently while holding the `EntityTypeDefinition` it loaded at :140.
- **F279.4 — `SparkQuery.UseProjection` is dead server-side.** `SparkQuery.cs:48` is only echoed to the
  client (`Endpoints/Queries/Execute.cs:121`); the ng-spark model comment for it references a `[QueryType]`
  attribute Spark doesn't have (`ng-spark/models/src/spark-query.ts:19-21`).
- **F279.5 — every other registry use is a disguised `[FromIndex]` scan.** `IsProjectionType`
  (QueryExecutor.cs:287, ModelSynchronizer.cs:60/:91, ModelShapeDiscovery.cs:38/:84) is answerable by
  `type.GetCachedCustomAttribute<FromIndexAttribute>() != null` — the registry is populated *from* that
  attribute (`SparkMiddleware.cs:417-430`). `GetAllRegistrations` (ModelSynchronizer.cs:230, stale
  projection-file cleanup) enumerates what a `[FromIndex]` scan enumerates. The collection type is
  derivable per index from `AbstractIndexCreationTask<T>`'s generic argument (`IndexRegistry.cs:186-208`).
- **F279.6 — deployment never depended on the registry.** `SparkMiddleware.cs:515` deploys via
  `IndexCreation.CreateIndexes(assembly, store)` straight off the assembly list; the test driver does the
  same (`RavenIndexHelper.cs:61`).
- **F279.7 — per-query `indexName` is unhashed.** `ModelFileShape.Describe` reads only `persistentObject`;
  the `queries` array is invisible to `--spark-verify-model`. Stamping `indexName` on queries therefore
  moves no hash; but once it is load-bearing, its absence from the hash is itself a gap (see SP279.2).
- **F279.8 — the founding rationale constrains where the declaration lives, not whether it exists.** The
  registry exists so the entity/library never names its indexes ("the arrows all point one way — app →
  library", `issue_210_PRD.md:620-637`). Vidyano's `[QueryType]`-on-the-entity chain is unavailable to
  Spark (Fleet keeps indexes in its library project; Spark deliberately doesn't). But the app-side reverse
  arrows already exist: index → entity (generic argument), view → index (`[FromIndex]`), model JSON → both.
- **F279.9 — Vidyano prior art (Fleet).** No registry. Query → `Source` (context member) → code picks the
  index per query (`Query<VCar, Cars_Overview>()` vs `Query<VCar, Cars_Archived>()` — same view, different
  index). References bind via `LookupId` → a full query. Grid columns are per query over the PO's attribute
  pool; genuinely different projections get their own PO. The `[QueryType]`/`[FromIndex]` default chain is
  codegen convenience, not a resolution mechanism.
- **F279.10 — one grid shape per entity is baked in downstream.** `EntityTypeDefinition` merges exactly one
  projection (attribute union, `inCollectionType`/`inQueryType`, `showedOn` derivation,
  `ModelSynchronizer.cs:448-473,549-565,617-632`); ng-spark computes columns per entity type
  (`spark-query-list.component.ts:306-310`). So a per-entity **default projection** remains a real modeling
  concept at sync time — the defect is only that the runtime resolves through it ambiently.
- **F279.11 — stale doc surface.** `GenerateIndexAttribute.cs:26-28` still describes pre-preview.55
  behavior ("at most one index per entity… reported as a diagnostic") — wrong on both counts.

## Requirements

- **R279.1** `QueryExecutor` resolves the index and projection **by the query's declared `indexName`**
  (name-keyed lookup); the declared name is authoritative. A `query.IndexName` naming an unknown index is an
  error, not a fallback.
- **R279.2** A minted `Database.*` query is **stamped** with the entity's default `indexName` at mint time,
  provenance-gated per the #275 pattern: a stored value equal to the derived one is machine-owned (may be
  retargeted when the default changes or cleared when the index disappears, with a console note); any other
  value is authored and preserved verbatim. `Custom.*` queries are never stamped or touched.
- **R279.3** `SparkQuery.UseProjection` is deleted (server model, wire echo, ng-spark model). Whether a
  query projects is derived from whether its named index has a `[FromIndex]` companion.
- **R279.4** The query-less PO-list path (`DatabaseAccess.GetPersistentObjectsAsync`) resolves through the
  entity model file's `queryType`/`indexName` (F279.3) — the fields become load-bearing instead of
  write-and-hash-only. No collection-type lookup remains anywhere at runtime.
- **R279.5** The per-entity default is authored via a new **`[DefaultIndex]` attribute on the index class**
  (app-side; arrow rule holds). Enforcement is two-layered: a Roslyn analyzer errors when two indexes over
  the same collection type both carry it (compile-time DX, single compilation only), and the
  synchronizer/startup validation is the authoritative cross-assembly check (`AddIndexesFrom(...)` — the
  SPARK_INDEX_004 lesson: analyzer-only guards don't cover cross-assembly and can be disabled).
- **R279.6** Default rules: exactly one index over an entity → implicitly default; `[GenerateIndex]` emits
  `[DefaultIndex]` on its generated index by default (it is the generic-surface index) with an
  `IsDefault = false` opt-out; multiple indexes with zero or two-plus markers → synchronize/startup **error**
  naming the candidates. No ordinal-min, no guessing, ever.
- **R279.7** `IndexRegistry` (service, interface, `IndexRegistration`, retain-all + ordinal-min machinery,
  the preview.55 plural API) is **deleted**. Replacement: an immutable name→(`IndexType`, `ProjectionType`,
  `CollectionType`, `IsDefault`) catalog built once from the assembly scan, used by both runtime and
  synchronizer so their answers cannot diverge. `IsProjectionType` becomes a `[FromIndex]` attribute check.
- **R279.8** Constraints preserved: program units reference queries by `Id`
  (`Endpoints/ProgramUnits/Get.cs:128-131`); idempotent synchronize; #275/#276 provenance; row security
  filters on the base entity type (`QueryExecutor.cs:175-204`, `DatabaseAccess.cs:166-170`); detail/edit
  loads the full entity; model-hash gate honored (see Migration).
- **R279.9** `GenerateIndexAttribute.cs:26-28` doc comment corrected; release notes carry the consumer
  migration steps.

## Design

Resolution becomes a two-step, fully declared chain:

```
grid/lookup/PO-list  →  Spark query (by name or Id)  →  query.indexName  →  catalog[name]  →  (IndexType, ProjectionType)
```

- The **catalog** replaces the registry: built in `CreateSparkIndexes`/the offline dev paths from the same
  scan that deploys indexes; flat dictionary by index name; no collection-type keying. Its only
  entity-keyed consumer is the **synchronizer**, which asks "which index over entity E carries
  `[DefaultIndex]`" — a filtered enumeration, not a keyed lookup, and an error when ambiguous (R279.6).
- **QueryExecutor**: `indexName = query.IndexName` (stamped at mint, so normally present); resolve via
  catalog; `resultType = catalog entry's ProjectionType ?? entityType`; apply via `Query<T,TIndex>` from the
  entry's `IndexType`. The empty-`indexName` case (a hand-authored query deliberately hitting the raw
  collection) keeps the current no-index path.
- **DatabaseAccess**: read `entityTypeDefinition.IndexName`/`QueryType`, resolve the CLR projection type via
  the existing `ResolveType`, apply the index by name (this path never needed the CLR index type —
  `DatabaseAccess.cs:413-414`).
- **Synchronizer**: unchanged merging semantics (one default projection shapes the entity file), but the
  default comes from `[DefaultIndex]` (R279.6) instead of `registrations[0]`; stamps `indexName` on minted
  queries (R279.2); `ModelShapeDiscovery` hashes the same `querytype`/`index` lines it does today, now fed
  by the catalog — same value for every current consumer, so no hash movement (see Migration).

## Spikes

> **All resolved by the implementation — see "As-built" above.** Kept as filed, for the reasoning.

- **SP279.1 — breadcrumb/sort-companion resolution against a non-default index.**
  `ResolveSortProperty` (`QueryExecutor.cs:796-803`) redirects to `{Name}Sort` on the *projection*. When a
  query names a non-default index whose projection lacks the companion (or has a different shape), what is
  the correct behavior — no redirect (sort the raw field) or error? Method: unit test against two
  projections with divergent companions. Decision rule: silent wrong ordering is unacceptable; no-redirect
  with a debug log is the floor.
- **SP279.2 — should per-query `indexName` enter the model hash?** Today the `queries` array is unhashed
  (F279.7). Once load-bearing, a hand-edit to `indexName` changes runtime behavior with no
  `--spark-verify-model` signal. Method: enumerate what hashing queries would newly cover and what churn it
  causes (every consumer's file hash moves once). Decision rule: if the one-time migration is acceptable
  under the existing refuse-to-start gate mechanics, hash it; otherwise document the gap explicitly.
- **SP279.3 — EntityMapper behavior when a query's projection ≠ the entity's default projection.**
  Columns fill by name and non-matching attributes stay null (`EntityMapper.cs:42-53`) — pin that this
  degrades gracefully (no throw, AsDetail renderers tolerate null) so per-query projections are *usable*
  before per-query grid shapes exist. Method: integration test, two projections, generic grid on each.
- **SP279.4 — analyzer scope for `[DefaultIndex]`.** Confirm the analyzer can see both indexes when they
  live in the same compilation (the common case) and document that cross-assembly duplicates are caught by
  the synchronizer/startup check only. Method: analyzer test with two `[DefaultIndex]` classes over one
  entity; E2E-style test with `AddIndexesFrom` across two assemblies.
- **SP279.5 — `[GenerateIndex]` emission of `[DefaultIndex]`.** The generated index is a generated type; the
  marker must be emitted into the generated source (not required from the consumer). Verify the analyzer
  doesn't double-report when a consumer opts out (`IsDefault = false`) and hand-marks their own index.

## Plan (milestones)

> Shipped as proposed — PR #280's own milestones ran M1…M6 (`92abae6` … `7d7e8e3`) in this order, with
> tests green: SourceGenerators 197/197, client 38/38, main suite 1557/1557.

- **M1 — catalog + attribute + analyzer.** `IndexCatalog` (immutable, name-keyed, built from the existing
  scan); `[DefaultIndex]` in Abstractions; Roslyn analyzer (one marker per collection type per compilation);
  `[GenerateIndex]` emits the marker with `IsDefault` opt-out (SP279.4/5 land here).
- **M2 — runtime rewrite.** QueryExecutor by-name resolution (kills F279.1); DatabaseAccess reads the model
  binding (R279.4); `IsProjectionType` → attribute check at :287; delete `UseProjection` server-side + wire +
  ng-spark model (R279.3).
- **M3 — synchronizer.** Default from `[DefaultIndex]` with the R279.6 error; stamp `indexName` at mint with
  provenance gating (R279.2); stale-projection cleanup via `[FromIndex]` enumeration; ambiguity errors in the
  offline verify/hash paths too (`SparkDevelopmentExtensions`).
- **M4 — delete IndexRegistry.** Remove service/interface/registration/plural API; rewrite
  `IndexRegistryTests` as catalog + default-designation tests (the preview.55 ordinal-min pins are
  deliberately superseded); sweep ctor plumbing (QueryExecutor/DatabaseAccess DI, row-security test mocks).
- **M5 — demos + docs + release notes.** Re-sync all four demo apps (expect stamped `indexName` on minted
  queries as the only model diff — zero hash movement per F279.7/F279.3); fix `GenerateIndexAttribute`
  docs (R279.9); release notes with consumer migration; SP279.2's hash decision recorded.
- **M6 — sweep.** Full test suite + E2E; the #272 motivating case as an integration test: `[GenerateIndex]`
  + hand-written index on one entity, one query per index, both grids correct.

## Migration (consumers)

- Minted `Database.*` queries gain a stamped `indexName` on first synchronize — a bytes-only, git-visible
  diff; **no hash movement** (queries unhashed today; PO-level fields keep their current values since the
  catalog feeds the same `querytype`/`index` lines). If SP279.2 decides to hash queries, that is a separate,
  explicit one-time hash migration.
- `useProjection` disappears from model JSON and the wire model — synchronize removes it; ng-spark type drops
  the field (breaking, allowed).
- Entities with multiple indexes and no `[DefaultIndex]` fail synchronize/startup with a named-candidates
  error — consumers add one marker (or the `[GenerateIndex]` default covers it).
- MintPlayer/CodeCoverage: unblocks `[GenerateIndex]` on `Commit` alongside `Commits_ByRepository` with
  explicit per-query bindings — the #272 motivating case, finally without a tiebreaker. Supersedes the
  upstream ask in `docs/adopt-spark-preview-57.md` §6.
  **As-built follow-up:** the unblock is confirmed (projection-less index → invisible to the election →
  no marker, `SPARK009` silent), but Coverage decided *against* coexistence on its own merits — the nine
  hand-written call sites still need the coalesce and null test, and the production commit grid is a
  `Custom.*` source that no index binding helps. The chosen route is one index, not two: see
  `docs/adopt-spark-preview-57.md` D5 and §5.

## Out of scope / follow-ups

- **Per-query grid shapes** (per-query column sets / separate PO per specialized projection, as Vidyano
  does): a client-contract change over `EntityTypeDefinition` + ng-spark; file after this lands. SP279.3
  pins the graceful-degradation floor until then.
- Hashing the `queries` array beyond `indexName` (SP279.2 decides `indexName` only).
