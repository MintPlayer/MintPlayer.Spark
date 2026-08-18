# Release notes — `10.0.0-preview.53`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.53`. No Angular package changes.

All from [#210](https://github.com/MintPlayer/MintPlayer.Spark/issues/210): `[GenerateIndex]` generates a
RavenDB index, its `[FromIndex]` index entity and a `SparkContext` query root from the entity, including the
sort companion fields that make text containing spaces sortable at all. Two new analyzer rules cover
hand-written indexes, which generated code cannot help.

**No breaking changes.** Hand-written indexes keep working unchanged; the new attributes are opt-in, and the
sort redirect is a no-op for any type that has no companion.

See [Queries and Sorting](./guide-queries-and-sorting.md#generating-the-index-instead-of-writing-it) for the
full surface.

---

## `[GenerateIndex]` — the index, the index entity and the context root

```csharp
[GenerateIndex]
public class Car
{
    public string? Id { get; set; }

    [Search] public string LicensePlate { get; set; } = string.Empty;
    [Search] public string Model { get; set; } = string.Empty;

    public int Year { get; set; }

    [IgnoreForIndex] public string? CreatedBy { get; set; }
}
```

emits `Cars_Overview`, a `VCar` carrying `LicensePlateSort` and `ModelSort`, `StoreAllFields(FieldStorage.Yes)`,
a `partial void OnInitialize()` extension seam, and `VCars` on the app's `SparkContext`. `Demo/Fleet` is
converted as the worked example.

**The generated types land in the application project, never in the entity's assembly.** `[GenerateIndex]` takes
no type argument and no entity-side partial is emitted, so an entity library gains no reference to any index
type and stays safe to reference for replication. The generator reads the attribute from referenced assemblies
to make that work.

The `SparkContext` must be `partial` to receive query roots (`SPARK_INDEX_008` if it is not). A hand-written
root of the same name wins silently.

## Why sort companions exist

This is the part worth reading even if you never use the generator.

A field indexed `FieldIndexing.Search` is **analyzed**: `"Volkswagen Golf GTI"` is stored as the three terms
`volkswagen`, `golf`, `gti`. Sorting orders documents by their term, and with three terms per document the order
is arbitrary. A companion field with **no `Index(...)` call at all** keeps RavenDB's default indexing — one
lower-cased, un-tokenized term — and sorts correctly, case-insensitively, and supports `==` and `StartsWith`.

Two consequences, both measured rather than assumed:

- **Do not declare `FieldIndexing.Exact` on a companion.** It is a regression on both counts: ordering becomes
  case-sensitive ordinal, and equality changes silently — `ModelExact = 'audi a4'` matches nothing where
  `ModelSort = 'Audi A4'` matches.
- **Nulls and empties sort first**, not last: RavenDB indexes the sentinel terms `NULL_VALUE` and
  `EMPTY_STRING` and orders on those literals.

`[Search]` therefore does both jobs from one attribute — analyzed indexing *and* the companion — because
analyzing the field is what destroys its sortability. `DateTimeOffset` gets `Exact` plus a companion
automatically; `DateTime` gets neither, deliberately.

## Sorting redirects automatically

`QueryExecutor` now orders by an attribute's `{Name}Sort` companion when the query type has one and it is
`[IgnoreProperty]`. Callers, query JSON `sortBy` and the `?sortBy=` override all keep naming the display
attribute.

Resolved by convention, so **no model-JSON field was added and no existing model file changes.** A missing
companion falls back to the requested property unchanged.

## `TranslatedString` fans out per language

RavenDB cannot usefully sort or search a dictionary, so a `TranslatedString` becomes one
`Description_{lang}` field per language in `App_Data/culture.json`, plus a companion per language under
`[Search]`. That file must be declared as an `AdditionalFiles` item — a generator has no DI and cannot ask
`CultureLoader`:

```xml
<AdditionalFiles Include="App_Data\culture.json" Condition="Exists('App_Data\culture.json')" />
```

The `Condition` is required, not defensive: an `AdditionalFiles` item naming a missing file fails the build.
Absent or malformed, the generator falls back to `en` alone — what `CultureLoader` does too.

⚠️ **Do not register a Newtonsoft converter for `TranslatedString`.** It persists nested as
`Description.Translations.nl`, which is the path the generated index maps; the flat `{"en":…}` form is a
System.Text.Json concern that applies only on the wire. Making persistence "consistent with the API" would
silently empty every generated per-language field — no deploy failure, no index error, healthy index, correct
row counts. A test now pins the stored shape so that change fails loudly.

## Hand-written indexes get the boilerplate generated too

A hand-written index no longer needs its companions or its `Index(...)` calls written by hand. Declare
searchability once with `[Search]` on the index entity, make both classes `partial`, and call the generated
method from your constructor:

```csharp
IndexSearchFields();      // one Index(...) call per [Search] property, Exact per DateTimeOffset
```

Previously `[Search]` and `Index(nameof(VCar.Model), FieldIndexing.Search)` said the same thing twice and drifted
independently — and drift is invisible here, because an un-analyzed field simply is not searchable.

You still call the method and still write the map assignments: a generator adds *members* to a partial class, it
cannot add *statements* to a constructor you wrote. `SPARK006` covers the map half.

`SPARK_INDEX_009` fires if the index class is not `partial`. All demos are converted to this shape; DemoApp's
index entities also moved from `DemoApp.Data` into `DemoApp.Indexes` so every index and index entity in the
solution is co-located, matching what the generator emits.

## New analyzer rules for hand-written indexes

Generated pairs are correct by construction and excluded from analysis. Hand-written ones now get:

| Rule | Fires when |
|---|---|
| `SPARK005` | an index declares a field `Search`/`Exact` and its index entity has no `{Name}Sort` companion |
| `SPARK006` | a companion exists but the index map never assigns it, so it indexes as null for every document |

Both are **warnings**, so existing indexes keep compiling while they are flagged. No suppression switch is
needed: generators run before analyzers, so wherever a companion was generated the analyzer finds it and stays
quiet.

The `[Search]`-on-an-ignored-property case is also reported now (`SPARK_INDEX_006`): `[IgnoreProperty]` keeps a
property out of the index, so a `[Search]` beside it never took effect and used to do nothing silently.

## Corrections to existing documentation

- `guide-queries-and-sorting.md` claimed `FieldIndexing.Search` indexes can produce duplicate results. Measured:
  they do not. A single-map index over an analyzed field returns exactly one row per document. Duplicates come
  from **fan-out** maps (`SelectMany` over a collection). `QueryExecutor`'s `DistinctBy` is still correct — it
  guards a different hazard than the guide described.

## Search now runs in the database

Search was already wired end-to-end — the query-list search box, the `?search=` parameter, the executor's
`search` argument — but the server implemented it by **materializing the whole collection and running
`string.Contains` over the mapped rows**. It now becomes a RavenDB `search(...)` clause.

No API change, no client change, and no configuration: every text attribute of a query type is searchable, with
or without `[Search]`. Substring matching is preserved, so existing behaviour carries over:

```
?search=olkswag
→ from index 'Cars/Overview' where (search(LicensePlate, $p0, and) or search(Model, $p1, and))
```

Two behaviour differences worth knowing:

- **Slightly more forgiving.** Each word is matched independently, so word order and adjacency no longer matter:
  `gti golf` now finds `Volkswagen Golf GTI`. Nothing that matched before stops matching.
- **Non-text attributes and reference display text are no longer searched.** Numbers, dates and the document id
  are excluded, and an attribute's resolved breadcrumb is computed after the query runs so it cannot be an index
  term. Denormalize that text into the index — which is exactly what `VCar.OwnerFullName` already does — and it
  becomes searchable again.

`TranslatedString` fields are searched across **all** languages, with no dependency on the request's culture.

Two failure modes were found by measurement and are now guarded rather than discovered in production: an empty
search term returns *zero rows* in RavenDB rather than acting as a no-op, so it is skipped entirely; and passing
`SearchOptions` explicitly makes the option leak onto the adjacent clause — which here is the row-level security
predicate, so an explicit `Or` would have silently turned a security filter into an alternative. The emitted RQL
shape is pinned by a test for exactly that reason.

## Known gaps

- **No "Add Sort property" code fix yet.** `SPARK005` names the property to add but there is no lightbulb. A
  `CodeFixProvider` cannot live in the analyzer assembly — it needs `Workspaces` at runtime, which is
  deliberately not packed — so it requires its own assembly and packaging. Follow-up.
- **Collection fan-out** (`[GenerateIndex(typeof(Country), nameof(Country.Cities))]`) is not implemented, and the
  constructor overload is withheld so there is no dead surface.
- **Map/reduce, multi-map, `LoadDocument` and other cross-document maps** stay hand-written.
- **No client editor for a `TranslatedString` value** — the generated per-language fields are queryable and
  sortable before they are editable.
- **Streaming queries do not push search down.** `StreamingQueryExecutor` takes no search term; the client
  filters streamed rows locally, as it did before.
- **No relevance ranking.** Search narrows the result set; ordering comes from the sort columns. Term-based
  organic search with relevance is a possible opt-in later, deliberately not the default because it would drop
  substring matching.
- **No fuzzy / typo-tolerant search.** Measured and rejected for now: RavenDB's `Fuzzy` is unsupported by Corax
  (the default engine, and what generated indexes use), exists only on the document-query API rather than the
  `IQueryable` surface the pipeline is built on, and is mutually exclusive with the wildcard wrapping that
  preserves substring matching. Details in `docs/issue_210_plan.md` spike S10.
