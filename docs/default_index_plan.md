# Default index per entity type — plan

PRD: [default_index_PRD.md](default_index_PRD.md). One branch, one PR: `fix/seotester-site-organization-query`,
which also fixes the trigger (SeoTester's empty Organization picker). Test runs are batched at the end (M9).
Generators follow the `C:\Repos\MintPlayer.Dotnet.Tools\SourceGenerators` pattern: read its docs before M3.

## M0 — Spikes (PRD §6)

Run SP1–SP5 on scratch code, without committing it. Record each outcome in the PRD with its evidence. SP1 picks D4's
B or fallback A; SP3 settles D6's ambiguity rule.

## M1 — Attributes and options

- `DefaultIndexAttribute(params Type[] entityTypes)`, exposing `EntityTypes`, with a new doc comment.
- Remove `GenerateIndexAttribute.IsDefault`.
- `SparkOptions.ModelAttribute` plus `ModelAttributeSeed { Default, Hidden, Exclude }`, with the default
  audit-name convention.

## M2 — Catalog and executor

- Entries carry their declared default types.
- `Freeze()` elects per entity type (D3), multi-maps included (D4). Rename `GetDefaultForCollectionType` to
  `GetDefaultFor(Type)`.
- Allow a marked default without a view.
- Multi-map discriminator: injected by `SparkMultiMapIndexCreationTask` (or the convention, per SP1). The executor
  filter goes in `QueryExecutor` and `LiveQueryCollections`.
- Re-check the callers that skip null collections: `SoftDeleteStartupCheck`, `ExpirationStartup`,
  `ModelShapeDiscovery`.
- Tests: `IndexCatalogTests` covering per-type, multi-map, viewless, conflict and implicit-only-with-view cases. A
  RavenDB test that a multi-map default returns only T's rows under row security.

## M3 — Generator and analyzers

- Claims (D5): a same-project `ForAttributeWithMetadataName` provider, plus referenced claims in `DescribeReferenced`.
  Suppress the generated marker for claimed entities.
- SPARK009: per-type claims; a hand-written claim beats `[GenerateIndex]`; new message text.
- New diagnostics (next free IDs from `docs/diagnostics.md`):
  - a type argument that isn't the single-map's `T`
  - a multi-map marker without types
  - a listed type with no same-project `AddMap<X>`
  - (fallback A only) a missing discriminator
- Tests: `GenerateIndexGeneratorTests`; rewrite `A_GenerateIndex_entity_clashes_with_a_hand_marked_index`; delete
  the `IsDefault` tests.

## M4 — View roots and naming (D6, D7)

- The context step emits a view root for every public `[FromIndex]` view, from the same project and referenced
  assemblies.
  - It merges two sources: the generator's own `[GenerateIndex]` pairs (it never sees its own output) and the
    scanned hand-written views. Drop scanned names that equal generated ones, so no view gets two roots.
- If SP3 picks the generator marker: emit the plain-root marker for each entity type on the context.
- Naming happens in the provider's transform lambda:
  - collect member names including inherited ones, and treat this pass's own roots as taken
  - take the first free name
  - carry `(ViewFullName, IndexFullName, MemberName)` as equatable data
  - the producer only writes
  - this replaces `RootsFor`'s silent drop
- Info diagnostic when a name had to change.
- Tests:
  - a hand-written view and index
  - a referenced-assembly view
  - the name taken by an unrelated member
  - the name taken by an inherited member
  - two views with the same name
  - incremental caching, after adding step tracking to `GeneratorHarness` (`trackIncrementalGeneratorSteps`)
  - an updated snapshot

## M5 — Missing-root diagnostic (D8)

- An analyzer over the generated compilation: a writable `[Reference(typeof(T))]` with no query, and no entity root
  of T, raises a warning. Read-only and audit members are skipped.
- A code fix (in the CodeFix assembly) adds the entity root to the context.
- If SP3 finds no way to identify the plain root among several roots of T: a warning requiring such a reference to
  name its query.
- Tests, via `RunGeneratorThenAnalyzersAsync`:
  - hand-written root (clean)
  - only a view root (warns)
  - library type with no root (warns, fix applies)
  - read-only reference (clean)
  - several roots of T, including the plain one (clean, or warns under SP3's fallback)

## M6 — Synchronizer and MCP (D6, D8, D9)

- Per-type default stamping.
- When T has several roots, a reference resolves to the plain root (SP3's rule).
- Library-type roots derive attributes through `SparkOptions.ModelAttribute`, at creation only.
- Warning for a reference that resolves to no query.
- Remove this branch's uncommitted `AddLibraryReferenceQueries` / `ambiguousLibraryQueries` and
  `SynchronizeLibraryReferenceQueryTests`.
- Tests: plain-root precedence, hook outcomes (Exclude, Hidden, Default, `[IgnoreProperty]` intersecting), creation
  only (no delete), the warning.
- MCP `describe` states each reference's query.

## M7 — Picker label (D10)

- `spark-reference-picker` and `reference-display-value.pipe`: show the server `attribute.breadcrumb` while the value
  equals the loaded id, otherwise the chosen option row's text, otherwise the id.
- ng-spark specs, plus a browser check that the list, detail and edit pages show the same text, including a value
  outside the first 50 options.

## M8 — Apps and docs

- SeoTester: add the `Organizations` root to the context (`Site.cs` stays plain).
- Every app: run `--spark-synchronize-model`; revert unrelated `$schema` bumps; commit model JSON and
  `modelHashes.json`.
- DemoApp: `Cars_Overview` keeps `[DefaultIndex]`.
- Browser-check SeoTester's picker with the browser MCP: it lists the caller's organizations.
- Docs per PRD §5, plus release notes:
  - the `[DefaultIndex]` shape change
  - the removal of `IsDefault`
  - generated view roots
  - `SparkOptions.ModelAttribute`
  - the deploy note for CodeCoverage and SeoTester (model hash)

## M9 — Test sweep

Run `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected` once, after
M1–M8. Fix failures by re-running only the failing class.
