# Queries and Sorting

Spark queries drive the list views in the Angular frontend. Each query maps to a SparkContext property and optionally uses a RavenDB index with a projection type for computed columns. Sorting is configurable both in the query definition (default sort) and at runtime via query string parameters.

## Overview

There are two types of queries:

| Type | Index | Projection | Use Case |
|---|---|---|---|
| Collection query | None | No | Simple list of all documents in a collection |
| Index-based query | RavenDB index | Yes | Computed columns, full-text search, cross-document data |

Collection queries return the full entity. Index-based queries return a projection type with only the columns needed for the list view.

## Collection Queries

A collection query is the simplest form. It queries all documents of a type directly from the RavenDB collection.

### Step 1: Define the Entity and SparkContext

```csharp
public class Company
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
```

```csharp
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
```

### Step 2: Synchronize

Run `dotnet run --spark-synchronize-model`. This generates `App_Data/Queries/GetCompanies.json`:

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440001",
  "name": "GetCompanies",
  "description": { "en": "Companies", "fr": "Entreprises", "nl": "Bedrijven" },
  "contextProperty": "Companies",
  "sortBy": "Name",
  "sortDirection": "asc"
}
```

The query name follows the pattern `Get{PropertyName}`. The `contextProperty` maps back to the SparkContext property. The synchronizer picks a default `sortBy` based on the entity's attributes (preferring `Name`, `LastName`, or the first string attribute).

### Step 3: Customize the Query JSON

After generation, you can edit the query JSON to change the default sort order, add translated descriptions, or set an alias:

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440001",
  "name": "GetCompanies",
  "description": { "en": "Companies", "fr": "Entreprises", "nl": "Bedrijven" },
  "contextProperty": "Companies",
  "alias": "companies",
  "sortBy": "EmployeeCount",
  "sortDirection": "desc"
}
```

## Index-Based Queries

For list views that need computed columns, full-text search, or cross-document data (e.g. showing a referenced entity's name), you need a RavenDB index and a projection type.

### Step 1: Create the Projection Type

The projection type defines the columns shown in the list view. Annotate it with `[FromIndex]` to link it to the index:

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Data;

[FromIndex(typeof(People_Overview))]
public class VPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
```

The `[FromIndex]` attribute tells Spark:
1. This is a projection type (not a standalone entity)
2. Which RavenDB index produces it
3. Which collection type it maps to (derived from the index's generic parameter)

### Step 2: Create the RavenDB Index

The index maps entity documents to the projection type. Use `AbstractIndexCreationTask<TEntity>`:

```csharp
using Raven.Client.Documents.Indexes;

namespace MyApp.Indexes;

public class People_Overview : AbstractIndexCreationTask<Person>
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            Id = person.Id,
                            FullName = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                            IsActive = person.IsActive
                        };

        // Enable full-text search on specific fields
        Index(nameof(VPerson.FullName), FieldIndexing.Search);
        Index(nameof(VPerson.Email), FieldIndexing.Search);

        // Store all fields so they can be projected
        StoreAllFields(FieldStorage.Yes);
    }
}
```

Key points:
- The `Map` expression computes values like `FullName = person.FirstName + " " + person.LastName`
- `Index(field, FieldIndexing.Search)` enables full-text search on that field
- `StoreAllFields(FieldStorage.Yes)` is required so RavenDB stores the computed values for projection
- Use `LoadDocument<T>(id)` to pull data from related documents (see [Reference Attributes](guide-reference-attributes.md))

### Step 3: Deploy and Synchronize

In `Program.cs`, call `CreateSparkIndexes()` to deploy indexes on startup:

```csharp
app.CreateSparkIndexes();
```

Then run model synchronization (`dotnet run --spark-synchronize-model`). The synchronizer detects the `[FromIndex]` link and:
1. Merges properties from both `Person` (entity) and `VPerson` (projection) into `Person.json`
2. Sets `queryType` and `indexName` on the entity type definition
3. Marks properties that exist only in the entity as `"showedOn": "PersistentObject"` (detail/edit pages only)
4. Marks properties that exist only in the projection as `"showedOn": "Query"` (list view only)
5. Properties in both types get `"showedOn": "Query, PersistentObject"` (shown everywhere)

### Example: Merged Model JSON

For Person, where `FirstName` and `LastName` exist only on the entity, `FullName` exists only on the projection, and `Email` exists on both:

```json
{
  "name": "Person",
  "clrType": "DemoApp.Library.Entities.Person",
  "queryType": "DemoApp.Data.VPerson",
  "indexName": "People_Overview",
  "displayAttribute": "FullName",
  "attributes": [
    {
      "name": "FirstName",
      "dataType": "string",
      "inQueryType": false,
      "showedOn": "PersistentObject"
    },
    {
      "name": "LastName",
      "dataType": "string",
      "inQueryType": false,
      "showedOn": "PersistentObject"
    },
    {
      "name": "Email",
      "dataType": "string",
      "showedOn": "Query, PersistentObject"
    },
    {
      "name": "FullName",
      "dataType": "string",
      "inCollectionType": false,
      "showedOn": "Query"
    }
  ]
}
```

### Including Cross-Document Data

To show data from a related document in the list view, use `LoadDocument` in the index map:

```csharp
public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      let owner = LoadDocument<Company>(car.Owner)
                      select new VCar
                      {
                          Id = car.Id,
                          LicensePlate = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          OwnerFullName = owner != null ? owner.Name : null,
                          Status = car.Status
                      };

        Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
        Index(nameof(VCar.OwnerFullName), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
```

The projection type must include the computed property:

```csharp
[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }
}
```

## Sorting

### Default Sort Order

Each query defines a default sort in its JSON file:

```json
{
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "LastName",
  "sortDirection": "asc"
}
```

The `sortBy` value must match a property name on the type that the query returns. For index-based queries, this is the projection type (e.g. `VPerson`). For collection queries, this is the entity type.

### Runtime Sort Override

The frontend can override the sort order by passing query string parameters to the query execution endpoint:

```
GET /spark/queries/{queryId}/execute?sortBy=Email&sortDirection=desc
```

The backend reads these parameters and applies them instead of the defaults:

```csharp
var sortBy = httpContext.Request.Query["sortBy"].FirstOrDefault();
var sortDirection = httpContext.Request.Query["sortDirection"].FirstOrDefault();
```

If `sortBy` or `sortDirection` are not provided, the query falls back to the values defined in the query JSON file.

### Sortable Columns in the Frontend

The Angular frontend renders clickable column headers in query list views. Clicking a column header toggles the sort direction and re-fetches the query with the new `sortBy` and `sortDirection` parameters.

Only attributes with `"showedOn"` including `"Query"` appear as sortable columns. The current sort column and direction are reflected in the column header UI.

### Sorting on Projected Fields

For index-based queries, you can sort on computed fields that exist only in the projection type. For example, sorting by `FullName` works because it is a stored field in the index:

```json
{
  "name": "GetPeople",
  "contextProperty": "People",
  "sortBy": "FullName",
  "sortDirection": "asc"
}
```

The query executor applies sorting after projection, so projected fields like `FullName` are available for sorting even though they do not exist on the entity type.

### Sorting on searchable text — why `*Sort` fields exist

A field indexed `FieldIndexing.Search` is **analyzed**: RavenDB tokenizes it, so `"Volkswagen Golf GTI"` is
stored as the three terms `volkswagen`, `golf`, `gti`. Sorting orders documents by their term in the field, and
with three terms per document that order is arbitrary. Measured, it comes back genuinely scrambled:

```
"Trailing spaces  ", "ZZ Top", "", null, "alfa romeo", "Audi A4", "Volkswagen Golf GTI", …
```

The repair is a **sort companion**: a second field carrying the same value, with **no `Index(...)` call at
all**. Left undeclared it keeps RavenDB's default indexing — a single lower-cased, un-tokenized term — so it
orders correctly, case-insensitively, and can serve `==` and `StartsWith` as well.

| Field | Declared as | Terms for `"Volkswagen Golf GTI"` | Sorts correctly? |
|---|---|---|---|
| `Model` | `FieldIndexing.Search` | `volkswagen`, `golf`, `gti` | no |
| `ModelSort` | *nothing* → `Default` | `volkswagen golf gti` | yes |

**Do not "improve" the companion by declaring `FieldIndexing.Exact` on it.** That is a measured regression on
both counts: ordering becomes case-sensitive ordinal, so every capitalised value sorts before every lowercase
one, and equality changes silently — `ModelExact = 'audi a4'` matches nothing where `ModelSort = 'Audi A4'`
matches. Leaving it undeclared is correct, not an oversight.

**A plain string field needs none of this.** Only a field declared `Search` is tokenized, so an ordinary
`string` property sorts correctly with no companion at all — measured, and pinned by a test. Tokenization is a
per-field indexing mode, not something strings do:

| `FieldIndexing` | Terms for `"Volkswagen Golf GTI"` | Sortable | `==` |
|---|---|---|---|
| *undeclared* → `Default` | one: `volkswagen golf gti` | yes, case-insensitive | yes |
| `Search` | three: `volkswagen`, `golf`, `gti` | no | no — full-text match |
| `Exact` | one: `Volkswagen Golf GTI` | yes, case-sensitive ordinal | case-sensitively only |

Adding `[Search]` later emits the companion *and* activates the redirect, so there is no window where a field is
analyzed but unsortable.

Two things worth knowing:

- **This is only about values that can contain spaces.** A space is the tokenization boundary, so a
  single-word value yields one term either way and an analyzed field *accidentally* sorts fine. The bug stays
  invisible until someone stores a value with a space in it.
- **Nulls and empties sort first**, not last. RavenDB indexes the sentinel terms `NULL_VALUE` and
  `EMPTY_STRING` and orders on those literals, which on a lower-cased companion land before every real value.
  If a UI wants them last, that has to be arranged explicitly.

You never name the companion when sorting. `sortBy`, the `?sortBy=` override and any caller all keep naming the
display attribute; the query executor redirects to `{Name}Sort` when the projection has one and it is
`[IgnoreProperty]`. That `[IgnoreProperty]` is required — it is what distinguishes a real companion from an
ordinary property that happens to be named `FooSort`.

### A sort column must be on the query surface

A caller-supplied `sortColumns` entry must name an attribute that exists in the type's model **and**
whose `showedOn` includes `Query`. Anything else is refused, logged, and the rows keep their index
order.

This is a security boundary, not tidiness. Ordering is a comparison oracle: redaction blanks a value
in the response but leaves `ORDER BY` intact, so an attribute a caller may never read could otherwise
be recovered one comparison at a time — sort ascending, sort descending, observe where the row lands,
bisect. Silently, and indistinguishable from ordinary paging.

So narrowing an attribute's `showedOn` to `PersistentObject` now does what a reader would assume:
removes it from the grid **and** from the set of things the grid can be ordered by.

```json
{ "name": "InstallationId", "dataType": "string", "showedOn": "PersistentObject" }
```

Two things worth knowing:

- **The check runs on the declared name, before sort-companion redirection.** A companion
  (`{Name}Sort`) is only ever used when it is ignored for the Spark model, so it is never an attribute
  itself and would fail the check. Sorting by the complex field it stands in for keeps working.
- **The gate is `showedOn`, not the redaction hook.** `GetProtectedAttributesAsync` takes an entity
  and may answer differently per row, so it cannot decide a query-level operation — and by the time
  rows exist, the ordering has already happened.

## Generating the index instead of writing it

Everything above can be generated. Put `[GenerateIndex]` on the entity and the index, the index entity and the
`SparkContext` query root are all emitted for you:

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

That yields `Cars_Overview`, a `[FromIndex]`-annotated `VCar` with `LicensePlateSort` and `ModelSort`
companions, `StoreAllFields(FieldStorage.Yes)`, and a `VCars` root on the context. `Demo/Fleet` is the worked
example.

Points that matter in practice:

- **The generated types land in the application project, never in the entity's assembly.** Entities usually
  live in a lean class library; the generator reads `[GenerateIndex]` from referenced assemblies so the library
  gains no reference to any index type and stays safe to reference for replication.
- **The context must be `partial`** to receive its query roots. A hand-written root of the same name wins.
- **`[Search]` does two things with one attribute** — analyzed indexing *and* the sort companion — because
  analyzing the field is what destroys its sortability.
- **`DateTimeOffset` gets `Exact` indexing and a companion automatically.** `DateTime` gets neither; the
  asymmetry is deliberate.
- **`[IgnoreForIndex]` versus `[IgnoreProperty]`**: the first keeps a property in the model but out of the
  index; the second removes it from the model everywhere, and therefore from the index too.
- **`TranslatedString` fans out** into one `Description_{lang}` field per language in `App_Data/culture.json`.
  That file must be an `AdditionalFiles` item for the generator to see it — a generator has no DI and cannot
  ask `CultureLoader`:

  ```xml
  <AdditionalFiles Include="App_Data\culture.json" Condition="Exists('App_Data\culture.json')" />
  ```

  The `Condition` is not optional: an `AdditionalFiles` item naming a file that does not exist fails the build.
- **Extending a generated index**: implement `partial void OnInitialize()` on a hand-written partial half. It
  is called at the end of the generated constructor.

### Keeping a hand-written index, without hand-writing the boilerplate

An index you write yourself can still have its companions and its `Index(...)` calls generated. Declare
searchability once, with `[Search]` on the index entity, and make both classes `partial`:

```csharp
[FromIndex(typeof(People_Overview))]
public partial class VPerson
{
    [Search] public string FullName { get; set; } = string.Empty;
    // FullNameSort is generated.
}

public partial class People_Overview : AbstractIndexCreationTask<Person>
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            FullName = person.FirstName + " " + person.LastName,
                            FullNameSort = person.FirstName + " " + person.LastName,
                        };

        IndexSearchFields();                 // generated from the [Search] attributes
        StoreAllFields(FieldStorage.Yes);
    }
}
```

`IndexSearchFields()` carries one `Index(...)` call per `[Search]` property and `Exact` per `DateTimeOffset`
property. **You must call it** — a generator can add members to a partial class but cannot add statements to a
constructor you wrote. For the same reason the **map assignments stay yours**; `SPARK006` flags a companion the
map never assigns.

Both classes must be `partial`: `SPARK_INDEX_001` if the index entity is not, `SPARK_INDEX_009` if the index is
not.

Hand-written indexes keep working and are still the answer for anything the generator does not cover — map/reduce,
multi-map, `LoadDocument` and other cross-document maps. For those, `SPARK005` and `SPARK006` flag a missing or
unmapped sort companion so the convention does not have to be remembered.

### Several indexes over one entity

Multiple indexes mapping the same collection type — a `[GenerateIndex]` beside a hand-written index with
computed fields, say — coexist. The registry retains every registration; the **generic query path uses one
deterministic default** (the smallest index name under ordinal comparison — name order, not registration
order, so reordering declarations can never silently move the model hash), and warns at startup when a
collection has more than one. The others stay fully usable by hand via `session.Query<TProjection, TIndex>()`,
which never consults the registry.

### Complex fields and the breadcrumb sort companion

A complex-typed property (a nested object, a collection of non-scalars, a dictionary) cannot be indexed with
default options — Corax faults on every document, leaving the index silently empty — so a generated index maps
it **stored but not indexed** (`FieldIndexing.No`): the AsDetail column keeps rendering, but cannot be
filtered or sorted (`SPARK_INDEX_010`). To make a singular complex column sortable, mark a property of its
type with `[Breadcrumb]` (typically a null-safe computed property carrying `[IgnoreProperty]`); the generator
emits a `{Name}Sort` companion reading the persisted value, and the runtime sort redirect does the rest.

## Scoping a query on the context

A context property is not limited to a bare root. It may compose onto it, and it may depend on
services — the context is registered with `AddScoped<SparkContext, TContext>()`, so constructor
injection works like any other scoped service:

```csharp
public class MyAppContext(ICurrentUser currentUser) : SparkContext
{
    public IRavenQueryable<Account> Accounts => Session.Query<Account>();

    public IRavenQueryable<Account> MyAccounts =>
        Session.Query<Account>().Where(a => a.OwnerId == currentUser.Id);
}
```

Point a query's `source` at `Database.MyAccounts` and the grid is scoped to the signed-in user.

The composed predicate is preserved when the query runs against an index: the property's expression is
replayed onto the index-backed query rather than replaced by it. A property that composes nothing
produces exactly the query it always did.

> **Before `preview.58` this silently failed open.** The index query was built from scratch and the
> property's `Where` was discarded, so a scoped grid returned every row with no error and no log. If
> you are upgrading and have such a property, it was not filtering.

### A scoped property is not an authorization boundary

It scopes the **grid**. A by-id read, save or delete never consults the context property — those go
through `session.LoadAsync` — so a user who knows an id can still fetch and modify a row a scoped
property would have hidden.

Use a **row rule** when the requirement is access control, and treat the context property as the way to
express *which rows this screen is about*. The two are complementary: a row rule cannot express a
predicate on an index-only field (for a projected query it is evaluated against the reloaded document),
which is exactly the case the context property covers.

### Why a context can have constructor dependencies

Before `preview.58` the offline model commands (`--spark-synchronize-model`, `--spark-verify-model`)
instantiated the context, which required a public parameterless constructor and so ruled the pattern
out. They now work from the context **type** — they only ever read property types — so nothing is
constructed and no session or service provider is needed. They stay runnable in CI.

Two rejections come with that: passing an abstract type (or `SparkContext` itself) is refused, as is
writing a model hash for a context with no query roots when the model directory is not empty. Both
describe an empty model, and the resulting hash file would otherwise certify emptiness over a populated
directory — which `--spark-verify-model` cannot detect and which surfaces as a startup failure in
Production.


## Async custom queries

A custom query may be `async`, and since preview.59 it is treated exactly like its sync twin —
declared `sortColumns`, header-click sorting, row-filter pushdown, search pushdown, index projection
and `.Include()` all apply.

```csharp
public async Task<IRavenQueryable<VCar>> Recent_Cars()
    => await Task.FromResult(session.Query<VCar, Cars_Overview>().Where(c => c.Year >= 2020));
```

Capabilities are inferred from the **object the method returns**, not from its declared type. That
matters because the declared type is routinely weaker than the object: `session.Query<T>()` assigned
to `IQueryable<T>` is the common idiom, and it still gets the RavenDB path.

| Return type | Treated as |
|---|---|
| `IRavenQueryable<T>` / `Task<IRavenQueryable<T>>` | full RavenDB query |
| `IQueryable<T>` / `Task<IQueryable<T>>` backed by `session.Query<T>()` | full RavenDB query |
| `IQueryable<T>` over an in-memory source | in-memory queryable — sorting and row filtering only |
| `IEnumerable<T>` / `Task<IEnumerable<T>>` | already materialized; no pushdown, no declared sorting |
| `ValueTask<...>` | **not supported** — use `Task` |

Note the last two rows. An already-materialized result cannot be ordered by the database, so a
declared `sortColumns` on a `Task<IEnumerable<T>>` query does nothing. If you need declared sorting,
return the queryable and let the framework enumerate it.

## Searching

A query list's search box sends its term as `?search=`, and the server pushes it into RavenDB as a
`search(...)` clause across the query type's text fields. Nothing needs declaring — **every text attribute is
searchable, whether or not it carries `[Search]`** — and each word of the term is matched as a substring.

```
GET /spark/queries/{id}/execute?search=olkswag
→ from index 'Cars/Overview' where (search(LicensePlate, $p0, and) or search(Model, $p1, and))
```

Search and sorting are independent: search narrows, the sort columns order, and the `*Sort` redirect below
applies unchanged. A search never ranks by relevance.

Two things a database search cannot match, both of which used to work when filtering happened in memory:
**non-text attributes**, and **reference display text** (a breadcrumb is computed after the query runs, so it is
not an index term — denormalize it into the index, as `OwnerFullName` does above).

> Full details — wildcard handling, multi-language fields, the composition order with row-level security, the
> in-memory fallbacks, and why fuzzy matching is not offered — are in **[Full-Text Search](guide-search.md)**.

## Query Execution Flow

When the frontend requests a query:

1. Backend loads the `SparkQuery` definition from `App_Data/Queries/`
2. Resolves the SparkContext property (e.g. `People`)
3. Checks IndexRegistry for a projection type linked via `[FromIndex]`
4. If an index exists, queries using the index and applies `ProjectInto` for computed fields
5. Composes the row-level security filter, then the search term, then sorting — in that order, so the security
   predicate is ANDed with the search group rather than OR-ed into it
6. Executes the query against RavenDB
7. Maps results to `PersistentObject` format using the merged entity type definition
8. Deduplicates results by ID (fan-out maps — `SelectMany` over a collection — emit one index entry per element, so one document can match several times)

> An earlier version of this guide attributed the duplicates to `FieldIndexing.Search`. That was measured and
> is not the case: a single-map index over an analyzed field returns exactly one row per document. The
> deduplication is still correct, it just guards a different hazard.
9. Returns the results as JSON

## Complete Example

From the DemoApp:

```
Demo/DemoApp/
  DemoApp/
    App_Data/
      Model/Person.json          <-- merged entity + projection attributes
      Queries/GetPeople.json     <-- query definition with sort config
    Data/VPerson.cs              <-- projection type with [FromIndex]
    Indexes/People_Overview.cs   <-- RavenDB index with computed FullName
  DemoApp.Library/
    Entities/Person.cs           <-- entity with FirstName, LastName, etc.
```

The entity lives in a class library here while the index and projection sit in the application. That
is not incidental — see below.

## Indexes and projections in a class library

Spark discovers indexes and `[FromIndex]` projections by scanning the **entry assembly**. An index or
projection shipped in a class library is invisible to that scan, so declare its assembly:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();
    spark.AddIndexesFrom(typeof(People_Overview).Assembly);
    // or: spark.AddIndexesFromAssemblyContaining<People_Overview>();
});
```

A **module** declares its own assembly from inside its `AddXxx(...)`, so applications using it write
nothing. Declarations are additive — the entry assembly is always scanned, and declaring the same
assembly twice costs nothing.

> **Declare during `AddSpark`, not from middleware.** A declaration made inside a
> `Registry.AddMiddleware(...)` callback runs after index creation *and* after the build-time model
> commands have read the list, so it is silently missed by both.

### Why it matters more than it looks

Without a registration Spark queries the **collection** rather than the index, and skips
`ProjectInto`. RavenDB then materialises results from the source documents, so:

- fields the index **computes** (`LoadDocument` joins, concatenations, projections) come back **null**
  — with the correct row count, which reads like a broken index rather than a missing registration;
- index-side filtering is lost, so a filtering index returns **more** rows than intended;
- sorting on a projection-only column silently does nothing.

None of that raises an error, and the model-hash check cannot catch it either: synchronization and the
running application read the same registry, so both agree. If computed columns are empty and nothing
is logged, check that the declaring assembly is registered.

See also:
- `Demo/DemoApp/DemoApp/Indexes/Cars_Overview.cs` -- index with cross-document `LoadDocument`
- `Demo/DemoApp/DemoApp/Data/VCar.cs` -- projection with `[LookupReference]`
- `MintPlayer.Spark/Services/QueryExecutor.cs` -- query execution with sorting and projection
