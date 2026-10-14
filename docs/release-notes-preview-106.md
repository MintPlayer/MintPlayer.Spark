# Spark 11.0.0-preview.106 / ng-spark 22.37.0 — a default index per entity type

**Packages:** `MintPlayer.Spark.Attributes`, `MintPlayer.Spark`, `MintPlayer.Spark.SourceGenerators`,
`MintPlayer.Spark.Mcp`, `MintPlayer.Spark.SoftDelete` and the `MintPlayer.Spark.AllFeatures` bundle → the next
`11.0.0-preview` of each. npm: `@mintplayer/ng-spark` → `22.37.0`. The majors do not move: the packages still target
.NET 11 and Angular 22, so the API breaks below ship as a preview/minor bump.

`[DefaultIndex]` now claims an index for **specific entity types**, multi-map indexes included, and a hand-written
claim beats the index `[GenerateIndex]` emits. The default index both shapes a type's model and serves its lookups,
including the query a `[Reference(typeof(T))]` without a query name resolves to: the query of T's **plain** context
root. A reference to a package's type (`Organization`) resolves once the application's context has a root for it. That
is the fix for SeoTester's empty Organization picker.

Decisions and evidence: [default_index_PRD.md](default_index_PRD.md). Guides:
[the default index](guide-queries-and-sorting.md#the-default-index-of-an-entity-type) and
[auto-resolved query names](guide-reference-attributes.md#auto-resolved-query-names).

---

## ⚠️ Breaking changes

### 1. `[DefaultIndex]` takes the entity types; `[GenerateIndex(IsDefault = …)]` is removed

`DefaultIndexAttribute(params Type[] entityTypes)` replaces the argument-less attribute.

- **Single-map:** nothing to change. A bare `[DefaultIndex]` still claims the mapped `T`; if you name a type, it must
  be `T` (`SPARK051`).
- **Multi-map:** a multi-map can now be a default, of the types it lists: `[DefaultIndex(typeof(Bicycle),
  typeof(Motorcycle))]`. A bare marker on a multi-map is `SPARK052`, a listed type without an `AddMap<T>` is
  `SPARK053`, and the index must derive from `SparkMultiMapIndexCreationTask<TReduceResult>` (`SPARK055`). That base
  writes a collection field into every map, and the executor narrows each type's queries on it, so another mapped
  collection never comes back as T, outside T's row policy. A JavaScript index cannot be a default (`SPARK054`).
- **`GenerateIndexAttribute.IsDefault` is gone.** It was the opt-out for a generated index beside a hand-written
  default. Now the generated index yields by itself: when a hand-written index claims the entity (same project or a
  referenced assembly), `[GenerateIndex]` emits its index without `[DefaultIndex]`. Delete `IsDefault = false`.
- **`IIndexCatalog.GetDefaultForCollectionType(Type)` is renamed `GetDefaultFor(Type)`.** The election is per entity
  type: the claim, else the sole index with a `[FromIndex]` view. An index without a view may be claimed; its queries
  then return entity documents, and only the fields its maps emit are sortable and filterable.
- `SPARK009` now also reports, at build time, several viewed single-map indexes of one type with no claim, which
  startup already refused. A `[GenerateIndex]` entity counts as claimed by its generated index.

### 2. A query-less reference resolves to the plain root only

`[Reference(typeof(T))]` without a query name gets the query of the context root whose body is exactly
`Session.Query<T>()`. Before, it took the **last** root of T in declaration order, so `ActiveCars` could win over
`Cars`. A filtered, customized or index-bound root is never the default lookup; name its query to use it. The
generator identifies the plain root and writes `[SparkPlainRoot]` on the context's generated partial, so **every
`SparkContext` must be `partial`** (`SPARK_INDEX_008` otherwise). Re-run `--spark-synchronize-model`: a reference that
resolved to a filtered root moves to the plain root's query, or loses its query (with a warning) when there is none.

## New

- **Generated view roots and partial contexts.** The generator writes a typed root for every public `[FromIndex]` view
  on the application's partial context, hand-written indexes and referenced assemblies included
  (`VCars => Session.Query<VCar, Cars_Overview>()`). A view root mints no query. A root whose name is taken by a member
  of the context or a base type takes the first free one (`VCars2`), reported as `SPARK_INDEX_013` (Info).
- **`SPARK056`** (warning, with a code fix): a writable `[Reference(typeof(T))]` without a query whose T has no plain
  entity root on any context in the project. The fix adds `public IRavenQueryable<T> {Plural} => Session.Query<T>();`.
  Synchronize prints the same warning, also for references the analyzer cannot see. Read-only references are skipped,
  whether the CLR property or the model file makes them read-only.
- **Roots over a package's types, and `spark.ModelAttributes(...)`.** A root over any type derives its attributes from
  the CLR type. What each property becomes when synchronize **first creates** its attribute is
  `SparkOptions.ModelAttribute`, set with the builder extension `spark.ModelAttributes((type, property) => …)`:
  `Default`, `Hidden` (`showedOn: None`, read-only, not required) or `Exclude`. The default,
  `ModelAttributeConventions.AuditMembersHidden`, hides `CreatedBy`, `CreatedAt*`, `ModifiedBy` and `ModifiedAt*`.
  The hook never changes or deletes an existing attribute, and `[IgnoreProperty]` still always excludes. `Hidden` is
  layout only: use `Exclude` for anything secret. A new attribute keeps its place in the property walk unless that
  `order` is taken; then it gets the next free one.
- **Minted query names and aliases never collide (D11).** When the `Get{Property}` query minted for a root would take a
  name or alias another query already has, in the application or a package, it takes the first free one
  (`GetOrganizations2`, `organizations-2`) and the model file records it. Existing queries are never renamed. A
  satellite query (a contributions query) is planned in the same pass, before the roots, because its library addresses
  it by name.
- **The reference picker labels from the server (D10).** The edit form shows the server's breadcrumb for the loaded
  value, then the chosen option's text, so a value outside the first 50 options no longer shows as a raw id. The list,
  the detail page and the edit page show the same text.
- **The reference picker pages and searches on the server.** The "…" popup used to page and search only the 50
  rows the form pre-loaded, so a candidate past the 50th could be neither found nor paged to. Every page, page size,
  sort and (debounced) search is now one request to the reference query with `skip`/`take`/`search`, and the pager
  follows `totalItems`; row security applies as before. A multi-reference's `<bs-tree-select>` does the same through
  the new `ReferenceQueryTreeSelectProvider` ("load more" is the next server page). A picked row from a later page
  joins the form's options, so the textbox, the AsDetail table and the conflict dialog label it. A refresh hook's
  replacement option list is still searched in memory. `spark-reference-picker` gains `query`, `queryParent` and
  `rowPicked`; `spark-po-form` gains `referencePicked`. An inline AsDetail `<select>` (non-modal reference column)
  still lists one page: use `referenceDisplayType: "Modal"` for long lists.
- MCP `describe` states each reference's query.

## Deploy notes

- **SeoTester (production): the model hash changes**, `44a430cc…` → `54cd91e5…`. `SeoContext` gains the
  `Organizations` root; `Organization.json` gets an application layer with `GetOrganizations` (alias
  `organizations-2`) and a hidden `CreatedBy`; `Site.OrganizationId` gets its query. Deploy the model files with the
  build, as for any hash change; the startup gate refuses a mismatched model.
- **CodeCoverage (production): no model change**, the hash stays `cfaa6759…`. Its `ApiToken.CreatedByUserId`, a
  `SparkUser` reference the model marks read-only, no longer prints the unresolved-reference warning on every
  synchronize.
- Every application: make the `SparkContext` `partial`, rebuild, and run `--spark-synchronize-model` once, then
  `--spark-verify-model`.
