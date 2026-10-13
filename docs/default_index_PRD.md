# Default index per entity type — PRD

Status: **design settled (grilled 2026-10-13), spikes done (§7)**. Plan: [default_index_plan.md](default_index_plan.md).
Supersedes R279.5 and R279.6 of [issue_279_PRD.md](issue_279_PRD.md) and the `IsDefault` surface of
[issue_210_PRD.md](issue_210_PRD.md). Packages are preview: **no backward compatibility** (owner, 2026-10-13).

Terminology: a **view** is a class carrying `[FromIndex(typeof(SomeIndex))]` (`VCar`, `VCommit`). An index "has a
view" when such a class names it. This is what the code calls "projection" (`IndexCatalog.RegisterProjection`, fed
only by `[FromIndex]` classes, `SparkMiddleware.cs:614-626`). An index's *Map* is not a view.

## 1. Trigger

On seotester.mintplayer.com, a Site's edit page shows the Organization field as an empty picker.
`Site.OrganizationId` carries `[Reference(typeof(Organization))]` (`apps/SeoTester/SeoTester/Entities/Site.cs:35`), and
`Site.json` says `"dataType": "Reference"`. What it lacks is a `query`. The client fetches options only from
`attr.query` (`ng-spark/po-form/src/spark-po-form.component.ts:292-303`), so a null query gives an empty picker, and
the server has no fallback.

The synchronizer derives a reference's query from the app's `SparkContext` roots (`Get{Property}`,
`ModelSynchronizer.cs:69-77`). SeoTester's context has no `Organization` root, because `Organization` is shipped by
`libs/organizations`. Nothing warned about it. `VCommit.Repository` in CodeCoverage needs no query name only because
`CoverageSparkContext.cs:10` hand-writes `Repositories => Session.Query<Repository>()`.

## 2. Owner requirements (2026-10-13)

1. `[DefaultIndex]` can be applied to an index **for a specific entity type**.
2. `[GenerateIndex]` checks whether a hand-written single-map or multi-map index mapping the entity already carries
   `[DefaultIndex]` for it. If so, the generated index doesn't get the marker.
3. The default index is what Spark uses to look up the entity, unless the reference says otherwise.
4. **The query list, the detail page and the edit page show the same text for a Reference** (owner, grill Q3).

## 3. What exists today (evidence)

| Fact | Evidence |
|---|---|
| `[DefaultIndex]` takes no arguments; `AllowMultiple=false`; it "does not route queries" | `libs/attributes/MintPlayer.Spark.Attributes/DefaultIndexAttribute.cs` |
| An index's entity type comes only from its base type; a multi-map maps **null** | `Services/RavenIndexHierarchy.cs:87-115` |
| The catalog elects one default per collection among indexes **with a view**, and skips multi-maps | `Services/IndexCatalog.cs:135-221` |
| `[GenerateIndex]` emits `[DefaultIndex]` unless `IsDefault=false`; no app uses `IsDefault=false` | `GenerateIndexGenerator.cs:432`, `.Producer.cs:249-250` |
| SPARK009 treats a `[GenerateIndex]` entity next to a hand-marked index as a clash (tested) | `Diagnostics/DefaultIndexAnalyzer.cs:78-93` |
| The default stamps `indexName` on `Database.*` queries and the entity file; `Custom.*` queries are never stamped | `ModelSynchronizer.cs:126-200`, `:775` |
| The synchronizer skips view roots ("merged into collection type"); they mint no query | `ModelSynchronizer.cs:103-111` |
| The generator already emits **view** roots (`Query<VX, X_Index>()`) for `[GenerateIndex]` pairs on the app's partial context, and drops a root whose name is taken | `GenerateIndexGenerator.ContextProducer.cs:113-147` |
| A root makes the synchronizer derive attributes from the CLR type; that is why `SparkUser` deliberately has none | `apps/CodeCoverage/CodeCoverage.Library/Entities/ApiToken.cs:120-128` |
| Labels are load-by-id plus template, with type and row redaction; no query or index is involved | `Services/Breadcrumb/BreadcrumbResolver.cs:111-253`; `EntityMapper.cs:365` |
| The edit form's picker label comes **only from the option rows** (one page of 50), never from the server's `attribute.breadcrumb` | `spark-reference-picker.component.ts:78-83`; `reference-display-value.pipe.ts:13-17`; `spark.service.ts:220-229` |
| `showedOn: "None"` is Spark's "invisible" (Vidyano's Visibility), but it is layout only: values still travel | `PersistentObject.cs:244-253` |
| Reference targets without any context root today: `Organization` (the bug), `SparkUser` (intentional, read-only references only) | repo scan, 2026-10-13 |

## 4. Decisions

**D1. `[DefaultIndex(params Type[] entityTypes)]`, `AllowMultiple=false`.** (Req. 1)
- **Single-map:** the argument is optional and defaults to the mapped `T`. When given, it must be `T` (analyzer error).
- **Multi-map:** the argument is required. A referenced assembly's metadata does not contain constructor bodies, so
  `AddMap<T>` cannot be read from there (`docs/prd/PRD-AsDetail-Row-Identity.md:734-750`). Same-project `AddMap`
  inspection only *validates* the listed types.

**D2. One concept, overridable per reference.** (Grill Q1, C)
The default index both shapes T's model and serves T's lookup. `[Reference(typeof(T), "Q")]` overrides the lookup
with any `Database.*` query (another index) or `Custom.*` query (grill Q1).

**D3. Election.** (Grill Q4, A)
- Implicit default: the sole index of T **that has a view** (today's rule).
- An index without a view becomes a default only when explicitly marked. It then shapes nothing, and its queries
  return entity documents (SP4).
- Several claims for T, or zero claims among several viewed indexes: SPARK009 at compile time, and the startup error
  at runtime. The error text loses its `IsDefault` advice.

**D4. Multi-map defaults filter on a collection discriminator.** (Grill Q6, B with fallback A)
A multi-map that is a default must derive from `SparkMultiMapIndexCreationTask`.
- Spark injects a collection field into each map when it builds the definition (`SparkIndexCreationTask.cs:84-97`).
- The executor adds a filter on that field when running T's default on a multi-map.
- Without it, a multi-map returns every mapped collection, so a `Bike` would render as a `Car` and bypass Bike's row
  policy.
- **Fallback A, if SP1 shows the map rewrite is fragile:** authors emit the field themselves, and an analyzer checks
  each same-project `AddMap` (startup checks the definition of indexes from other assemblies).

**D5. `[GenerateIndex]` yields to a hand-written default; `IsDefault` is removed.** (Req. 2; grill Q5, A)
- The generator suppresses its marker when an index claims T, in the compilation (`ForAttributeWithMetadataName`) or
  in a referenced assembly (the `DescribeReferenced` walk, `GenerateIndexGenerator.cs:492-540`, reading the
  attribute's `TypedConstant`s).
- The claim model holds strings only.
- The generator excludes its own index names (`:187-194`).
- SPARK009 no longer counts the `[GenerateIndex]` entity as a competing claim.

**D6. Entity roots are hand-written; view roots are generated.** (Grill Q7–Q10; owner, "that's how Vidyano does it")
- **Entity root, hand-written, always:** `People => Session.Query<Person>()`.
  - It mints `Database.People`, which the synchronizer stamps with Person's default index.
  - `[Reference(typeof(Person))]` always resolves to that plain root's query, whatever other roots of Person exist.
  - `[Reference(typeof(Person), "Q")]` is the only way to use another definition.
  - Library types need one too. SeoTester's fix is
    `public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();`.
- **View roots, generated for every public `[FromIndex]` view** (grill Q10, A):
  `VPeople => Session.Query<VPerson, People_Overview>()`.
  - Hand-written indexes are included, not only `[GenerateIndex]`, and views from referenced assemblies too.
  - A view root has no model effect (`ModelSynchronizer.cs:103-111`). It is a typed C# shortcut and adds no
    executable surface.
- **Ambiguity:** when T has several hand-written roots, the reference uses the one whose body is the plain
  `Session.Query<T>()` (owner-confirmed, 2026-10-13).
  - The synchronizer reflects at runtime and can't read bodies, but the generator can. SP3 settled the mechanism, and
    the owner's follow-up placed it (2026-10-13). Every app context is `partial`: view roots need that anyway, and
    nothing prevents it. The generator emits `[SparkPlainRoot(typeof(T), nameof(Prop))]` (`AllowMultiple`) on the
    context's generated partial. The synchronizer reads it off the context type, walking the base types; a base
    context from a referenced assembly carries its own marker from its own generator run.
  - SP3 proves the mechanism. If no plain root can be identified, the analyzer requires the reference to name a query
    (M5).

**D7. Generated names never collide; they are resolved first in the pipeline.** (Owner, 2026-10-13)
Today a generated root whose name is taken is silently dropped, and member names miss inherited members
(`GenerateIndexGenerator.cs:475`). New rule, in the provider's transform lambda:
- Collect member names of the context and its base types.
- Take `IndexNaming`'s name, or the first free name `{Name}2`, `{Name}3`, …. Roots generated in the same pass count
  as taken.
- Carry the chosen name forward as equatable data; the producer only writes.
- Report an info diagnostic when a name had to change.
- Follow the `MintPlayer.Dotnet.Tools/SourceGenerators` pattern (`IncrementalGenerator`, `ProduceCode`,
  value-equatable models).

**D8. A writable reference without a resolvable query is a compile-time warning.** (Grill Q11, A)
- It fires on `[Reference(typeof(T))]` with no query, where T has no context entity root.
- It skips `[ReadOnly(true)]` and generated audit members.
- It runs on the compilation **after** generation (`RunGeneratorThenAnalyzersAsync`), so hand-written and generated
  members are both seen. Only an entity root silences it; a view root does not, because it mints no query.
- Code fix: add the entity root to the context.
- The synchronizer prints the same warning (for JSON-only cases).

**D9. A root over a library type derives its attributes, through one app-level hook.** (Grill Q12, B; Q13, C; Q14, B)
- A root over any type, library types included, derives attributes from the CLR type, as in Vidyano.
- What each property becomes is decided by
  `SparkOptions.ModelAttribute: Func<Type, PropertyInfo, ModelAttributeSeed>`, returning `Exclude`, `Hidden`
  (`showedOn: None` + read-only) or `Default`.
  - `[IgnoreProperty]` always excludes.
  - Spark's default implementation is the audit **name convention** (`CreatedBy`, `CreatedAt*`, `ModifiedBy`,
    `ModifiedAt*` → `Hidden`). An app replaces or wraps it.
  - The hook runs only during synchronize, and only when an attribute is first created. It never deletes (#253), and
    it must be pure.
- `SparkUser` keeps having no root: its secrets are inherited Identity members, and `Hidden` still ships values.
- So `Organization.CreatedBy` in SeoTester becomes a hidden, read-only attribute.

**D10. Labels come from one source.** (Req. 4; grill Q2, A; Q3)
- Labels stay load-by-id plus T's template on every page.
- The edit form's picker shows the server's `attribute.breadcrumb` while the value is the loaded one, and the chosen
  option row's text after a change. Today it falls back to the raw id when the value isn't in the first 50 options.
- The query only feeds the picker's options. Option rows render T's template too (`EntityMapper.cs:330`).

## 5. Scope

In scope:
- the attribute (D1), the catalog (D3, D4), the generator and analyzers (D5, D7, D8)
- `SparkOptions.ModelAttribute` (D9)
- the synchronizer: plain-root precedence, warnings, library-type derivation (D6, D8, D9)
- the picker's label (D10)
- MCP `describe` stating each reference's query
- removing this branch's uncommitted synchronizer heuristic and its test (plan M6)
- apps:
  - SeoTester gets an `Organizations` root (the trigger)
  - resync every app's model and hashes
- docs:
  - `guide-reference-attributes.md`
  - `guide-queries-and-sorting.md`, including the stale "smallest index name" text at `:622-629`
  - `diagnostics.md`
  - `issue_279_PRD.md` and `issue_210_PRD.md` (superseded markers)
  - `libs/attributes/.../README.md`
  - `libs/spark/MintPlayer.Spark/AGENTS.md`
  - release notes

Out of scope (not being done): labels resolved through queries or indexes (grill Q2 chose load-by-id).

## 6. Spikes

| Id | Question | Method | Decides |
|---|---|---|---|
| SP1 | Can `SparkMultiMapIndexCreationTask` inject a collection field into each map's definition reliably (C# and JS maps)? Does `Query<T, MultiMap>()` plus the filter give the correct rows, paging, totals, row policy and view projection? | Scratch multi-map over two DemoApp types; through `QueryExecutor` | D4: B or fallback A |
| SP2 | Does the generator see `[DefaultIndex]` / `[DefaultIndex(typeof(X))]` and public `[FromIndex]` views in same-project and referenced assemblies under `dotnet build`, not only in the IDE? | `GeneratorHarness.CompileToMetadataReference` (`:321`), plus a real two-project build | D5, D6 view roots |
| SP3 | With several hand-written roots of T, can the synchronizer identify the plain `Session.Query<T>()` root? With an `Organizations` root in SeoTester, does `Database.Organizations` list exactly what `Custom.Organizations` lists for a member, a non-member and an anonymous caller? | Prototype a generator-emitted marker for the plain root, read by the synchronizer (alternative: invoke the getter on a stub session and inspect the expression); run both queries per caller | D6 ambiguity rule; the trigger's fix |
| SP4 | A marked default without a view: model shaping, the executor's path, live queries, and the soft-delete/expiration startup checks that iterate catalog entries | Mark a viewless index in DemoApp; synchronize; query; live query | D3 |
| SP5 | Can the synchronize command see `AddSpark` options (the hook)? What does each app's model and hash diff look like once library roots derive attributes through the default hook? Does `[IgnoreProperty]` affect only the model, never Raven serialization? | Run `--spark-synchronize-model` on every app on a scratch branch; diff; round-trip a document with an `[IgnoreProperty]` member | D9; deploy note for CodeCoverage and SeoTester |

## 7. Spike outcomes (2026-10-13)

All spikes ran as scratch code in isolated worktrees; nothing from them was committed.

**SP1 — D4 option B works.**
- **The rewrite.** RavenDB normalizes C# maps before Spark sees them: query syntax becomes a method chain,
  `let`s become chained `Select`s, and typed results become `new { }`. So a string rewrite is enough: insert
  `SparkCollection = "<Coll>",` after the last top-level `.Select(`/`.SelectMany(`'s `new {`.
  - Rewritten correctly: plain objects, `let`, `where` before or after `let`, nested `new{}`, string literals
    containing `})`, SelectMany fan-out, method syntax, typed results. A map that already emits the field is left
    alone (that covers fallback A).
  - Refused with `NotSupportedException`: a conditional body `c ? new{..} : new{..}`.
  - The server compiles the rewritten maps (0 index errors).
- **JS maps** can be wrapped the same way, but cannot derive from `SparkMultiMapIndexCreationTask`. **Decision:** a
  JS index cannot be a multi-map default; a marker on one is an analyzer error. This is not deferred work: the owner's
  requirement names `AbstractMultiMapIndexCreationTask`.
- **Querying.** `Where(SparkCollection == coll).OfType<T>()` gives the right rows, paging, totals and view
  projection, with and without a row policy (`Executor_multimap_default_returns_only_T`, all four combinations).
  Unfiltered, a multi-map returns bikes as cars, which is the hazard this filter closes.
- **Where the filter goes.** There is one choke point, `QueryExecutor.cs:944-964` (`ApplyIndexWithType`), before
  re-rooting, projection, row filter, search and sort.
- **Gap found.** `SoftDeleteStartupCheck.cs:61-63` and `ExpirationStartup.cs:75-78` skip null-collection entries,
  so they must also check multi-maps for each type they claim.

**SP2 — the generator sees everything D5–D7 need (6/6 scratch tests, plus a real two-project `dotnet build`).**
- It sees claims on all four index bases, in the same project and in referenced assemblies, reading the explicit
  types from metadata. It also sees public `[FromIndex]` views; internal views are excluded.
- `AddMap<T>` resolves through the semantic model in the same project, including the inferred form. It is not
  readable from referenced assemblies, which confirms D1.
- ⚠️ A bare `[DefaultIndex]` with the `params` constructor arrives as **one argument holding an empty array**.
  Test the array's length.
- The `ForAttributeWithMetadataName` transforms re-run on every edit ("Unchanged", not "Cached"), so the models
  must be value-equal strings.
- `GetMembers()` misses inherited members. Walk the base types, skipping private members of base types.
- Incremental-step tests: use `MintPlayer.SourceGenerators.Testing`'s `RunGeneratorTwice` rather than growing
  `GeneratorHarness`.

**SP3 — the plain root is identified by a generator marker; row parity holds; a new alias collision found.**
- **Marker.** The spike proposed an assembly-level attribute, because five of the seven app contexts weren't
  `partial`. **Superseded (owner, 2026-10-13):** contexts are made `partial` (view roots require it anyway), and the
  marker sits on the context's generated partial (see D6).
  - Classifier: plain means a body that is a single `Query<T>()` call with no arguments. It handles all three body
    forms and inherited same-compilation roots.
  - A helper method counts as not plain, which is conservative: such a reference falls back to an explicit query.
  - Roots inherited from a referenced assembly's base context get their marker from that assembly's own generator
    run.
  - The runtime alternative (invoking getters) was rejected: `.Customize()` leaves no expression node, and it would
    have to instantiate the context.
- **Rows.** `Database.Organizations` and `Custom.Organizations` return identical rows for a member, an owner, a user
  with no organization and an anonymous caller (401 for both). Both paths compose the type's row filter
  (`QueryExecutor.cs:977`, `:1294`) and run the gate (`:1081`, `:1460`).
- **Blocker found: alias collision.** The minted `GetOrganizations` derives the alias `organizations`, which the
  library's `Custom.Organizations` already uses. `--spark-verify-model` then exits 3 ("Two queries resolve to the
  alias 'organizations'"), and startup throws (`SparkQueryAliasIndex.cs:38`).
  → **D11** (below).

**SP4 — a viewless default works, after three changes.**
- `ModelSynchronizer.cs:745` must keep `indexName` on the entity file when the default has no view.
- The `ModelShapeDiscovery.cs:40-50` comment and invariant change accordingly.
- The dev gate `VerifyBoundIndexHasAProjection` (`SparkDevelopmentExtensions.cs:533-583`) must accept a viewless
  default. But then sorting on a field the map doesn't emit returns a 500 ("The field 'Author' is not indexed"),
  and the synchronizer's default sort picks exactly such a field. **Decision:** for a viewless default, the index's
  query surface is the set of fields its maps emit, read with SP1's map scanner. Attributes outside that set are
  minted as not sortable and not filterable, and the default sort is chosen only from emitted fields. Search
  already falls back to in-memory (`QueryExecutor.cs:2052`).
- Live queries, the soft-delete check and the expiration check handle a viewless entry correctly.

**SP5 — the hook is reachable and has a precise insertion point.**
- **Reachability.** `SparkOptions` is a singleton instance in the service descriptors (`SparkMiddleware.cs:52-58`),
  readable by the synchronize command the way `GetRegistrationTimeModuleRegistry` already reads it
  (`SparkDevelopmentExtensions.cs:1374`). `ISparkBuilder` does not expose options, so the API is a builder
  extension, `spark.ModelAttributes(Func<Type, PropertyInfo, ModelAttributeSeed>)`.
- **Where the hook applies.** Only in the "create new attribute" branch (`ModelSynchronizer.cs:974-1010`).
  `Exclude` must not join `ignoredPropertyNames` (`:777`), because that set also deletes carried attributes
  (`:1033`, which would break #253).
- **Why it's needed.** Without the hook, SeoTester's `Organization` root writes a visible, editable `CreatedBy`
  whose `order` duplicates another attribute's. New attributes must get the next free order.
- **`[IgnoreProperty]`** affects only the model: RavenDB still persists and reads it (round-trip test passed).
- **Baseline sync with no code change:** no hash changes in any app. SeoTester has `$schema` churn and HR a key
  reorder, both unrelated and not committed.

**Implementation notes (M1/M2, commits `a77e9175`, `63f240a1`).**
- **Runtime refusals.** The catalog refuses at startup, not only in the analyzer:
  - a single-map marker naming another type
  - a bare multi-map marker, or one on a multi-map not deriving from `SparkMultiMapIndexCreationTask`
  - any marker on a JS or opaque index (`IndexCatalog.ResolveClaims`)
- **Not checked at startup:** that a multi-map's listed types are actually mapped. Only the M3 analyzer does that.
- **Viewless multi-map default:** its query surface is the union of every map's emitted fields. A field another map
  emits is indexed, so sorting on it is not an error.
- **The dev gate is strict for a viewless default:** an existing attribute outside the emitted fields fails
  `--spark-verify-model` until it is marked `canSort`/`canFilter` false.
- **Test fixtures:** every Spark test host scans the whole test assembly, so each marked fixture index claims its
  own entity types. Later fixtures must follow this.

**D11. Minted query names and aliases never collide either.** (Follows D7's principle; autonomous decision
recorded for owner review.)
When a query minted for a context root would take a name or alias another query already holds (a library's own
`Custom.*` query, as SP3 found), the synchronizer picks the first free alias (`{alias}-2`, …), records it in the
model file, and reports it. Queries that already exist keep their name, alias and program-unit links. A reference
still resolves to the minted root query (D6).

**Implementation notes (M6 and the analyzer corrections).**
- **D11 reproduced first.** `SynchronizeReferenceRootTests.A_minted_root_query_takes_the_first_free_alias` failed
  before the change with SP3's exact error ("Two queries resolve to the alias 'rrliborganizations'",
  `SparkQueryAliasIndex.cs:38`).
- **Names collide too.** The client fetches a reference's options by query *name*
  (`spark-po-form.component.ts:364`, `executeQueryByName`), and `QueryLoader.GetQueryByName` returns the first query
  of that name. So a minted `Get{Prop}` taken by another query, in a library or on another type, steps aside to
  `Get{Prop}2`, …, like the alias. Names and aliases are taken across the composed model.
- **Fixed point.** A root's query is the query of its type named `Get{Prop}`, as before, or a stepped-aside
  `Get{Prop}N` over `Database.{Prop}`. Otherwise the next run would mint again.
- **The warning** fires for a writable reference to a type with no root, as SPARK056 does. ~~When the type has roots
  but none is plain, it fires for every reference, read-only ones included.~~ **Superseded (M8):** it skips read-only
  references in that case too, as D8 says: a read-only reference opens no picker, so it loses nothing
  (`SynchronizeReferenceRootTests.A_read_only_reference_to_a_type_without_a_plain_root_does_not_warn`, red before the
  change).
- **Satellite queries go through the D11 plan (M8).** A satellite's query (a contributions query) was minted outside
  the plan, so a root whose derived alias equalled it took the alias first and the satellite then duplicated it
  ("Two queries resolve to the alias 'rrsatnotes'", red before the change). Satellites are planned first, because
  their name is a library contract (`ContributionDescriptor` hands the client the alias derived from it): a minted
  root steps around them. A satellite steps aside only from a query that already exists, with a warning that the
  library's contract then no longer holds.
- **Hook outcome `Hidden`** also creates the attribute not required: no form shows it, so nobody could supply a
  value.
- **Order:** a new attribute keeps its place in the property walk unless an existing attribute holds that order.
  Then it takes `max + 1`. A fresh type is numbered exactly as before.
- **SPARK056** now counts only plain roots: the generator's classifier (`Naming/PlainRootClassifier.cs`, shared rather
  than copied) plus `[SparkPlainRoot]` markers.
- **SPARK009, zero claims (D3):** several same-project single-map indexes of T with a `[FromIndex]` view and no claim.
  A `[GenerateIndex]` T counts as claimed.

