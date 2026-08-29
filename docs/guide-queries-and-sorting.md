# Queries and Sorting

Spark queries drive the list views in the Angular frontend. Each query maps to a SparkContext property and optionally uses a RavenDB index with a projection type for computed columns. Sorting is configurable both in the query definition (default sort) and at runtime via query string parameters.

## Overview

There are two types of queries:

| Type | Index | Projection | Use Case |
|---|---|---|---|
| Collection query | None | No | Simple list of all documents in a collection |
| Index-based query | RavenDB index | Yes | Computed columns, full-text search, cross-document data |

Collection queries return the full entity. Index-based queries return a projection type with only the columns needed for the list view.

**Every query owns its URL.** An alias resolves to exactly one query, and a collision is a startup
failure — including between a streaming and a non-streaming query, which are not allowed to share
one. See [one query per URL](guide-aliases.md#one-query-per-url) for why, and for the
transport-negotiation design that was rejected.

## What a query returns

A query result is **columns once, then one lightweight row per result**:

```jsonc
{
  "columns": [                       // sent ONCE, not per row
    { "name": "LicensePlate", "label": { "en": "Plate" }, "dataType": "string", "isSortable": true },
    { "name": "Owner", "dataType": "Reference", "referenceType": "Person" }
  ],
  "items": [
    {
      "id": "cars/1-A",              // required, and unique within the result
      "breadcrumb": "1-ABC-123",     // the row's display string, resolved server-side
      "values": [
        { "key": "LicensePlate", "value": "1-ABC-123" },
        { "key": "Owner", "value": "people/7-A", "objectId": "people/7-A", "breadcrumb": "Ada Lovelace" }
      ]
    }
  ],
  "totalItems": 42,
  "skip": 0,
  "take": 50
}
```

**Changed in #327.** Rows used to be full `PersistentObject`s, so every row carried a complete copy
of the attribute metadata — label, dataType, rules, renderer options, and for an AsDetail attribute
the whole nested object graph — that the client already held from `GET /spark/types` and never read
off the row. `data` became `items`, and `totalRecords` became `totalItems`.

The saving is real but secondary. The point is that **a row is a projection and a persistent object
is a document**, and conflating them made a row look like something it never was:

- A row carries **no `can` block and no etag**, because neither can be trusted from a projection —
  a computed row has no document behind it to re-judge.
- Nothing treats a posted row id as verified. Every mutating path re-materializes from the id
  through the same load path a detail page uses and re-applies security there, which is why row ids
  can be treated as hostile input with no integrity token on the wire.

Two rules the server now enforces rather than tolerating: a row **must** have an id, and two rows
**may not** share one. Both used to fail silently — a null id collapsed the grid to a single row
(every null key compares equal), and duplicates rendered the same row repeatedly with a matching
total.

### Type hints

`columns`, `items` and `values` may each carry a `typeHints` map — an open, string-keyed
presentation side-channel merged column → item → value, later winning. Keys are lower-cased at the
boundary, so a client never has to try two spellings. There is no registry and no validation, which
is the point: an application adds its own keys with no framework change.

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
| `IEnumerable<T>` / `Task<IEnumerable<T>>` | already materialized; sorted in memory by the framework |
| `SparkQueryPage<T>` / `Task<SparkQueryPage<T>>` | the method's own page — see [taking over paging](#taking-over-paging-sparkquerypaget) |
| `ValueTask<...>` | **not supported** — use `Task` |
| `IEnumerable<object>`, `IEnumerable<dynamic>`, `IEnumerable<PersistentObject>` | **refused, loudly** |

An already-materialized result cannot be ordered by the database, so the framework orders it in
memory instead. That ordering is deliberately **not** RavenDB's: ordinal case-insensitive for
strings, nulls last, pinned rather than inherited from the machine's culture. An in-memory result
has no index terms to order by, and a culture-sensitive default would sort differently per machine.

The last row is a refusal rather than a shape. A row type of `object`/`dynamic` has nothing to
reflect over, and a `PersistentObject` row is mapped *as* an entity — every declared attribute is
looked up as a CLR property and none is found. Both used to produce the same silent wrong answer:
the right number of rows, every cell blank, no error and no log. Return a concrete row type whose
property names match the attributes on the query's entity type — an anonymous type, a record or an
ad-hoc class all work.

## Composed queries: rows with no documents behind them

A query whose entity type declares **no `clrType`** is a *composed* query. There is no entity class,
no collection and no document behind a row — the rows are computed by the type's `{Name}Actions`
class, found by name, exactly as the virtual-type page path finds it.

The model file is hand-authored (there is nothing for `--spark-synchronize-model` to generate from),
and the attributes marked `"showedOn": "Query"` are what become the grid's columns:

```jsonc
// App_Data/Model/StartPage.json  -- note: no "clrType"
{
  "persistentObject": {
    "id": "7f3a5b21-9c4e-4d6a-b8f2-1e5d7a9c3b60",
    "name": "StartPage",
    "breadcrumb": "{Collection}",
    "attributes": [
      { "name": "Collection", "dataType": "string", "showedOn": "Query", "isSortable": true, /* … */ },
      { "name": "Records",    "dataType": "number", "showedOn": "Query", "isSortable": true, /* … */ }
    ]
  },
  "queries": [
    { "name": "StartPageCollections", "alias": "collections",
      "source": "Custom.GetCollections", "entityType": "StartPage",
      "sortColumns": [{ "property": "Records", "direction": "desc" }] }
  ]
}
```

```csharp
public partial class StartPageActions
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IEnumerable<CollectionRow>> GetCollections() =>
    [
        new CollectionRow("collections/people",    "People",    await session.Query<Person>().CountAsync()),
        new CollectionRow("collections/companies", "Companies", await session.Query<Company>().CountAsync()),
    ];
}

public sealed record CollectionRow(string Id, string Collection, int Records);
```

The row type is an ordinary record. The mapper reads its properties by the names the model declares,
and `Id` is the row identity — required, and unique.

### ⚠️ Row-level security does not run, and cannot

This is the one thing to understand before writing a composed query.

`IRowSecurity.FilterAsync` re-reads each row's collection type and evaluates the type's row rule
against the **stored** entity; `RedactAsync` compares each mapped attribute against the value on
that document. A composed row is computed, not stored. There is no document to re-judge, no
collection to resolve a rule from, and no stored value to compare against — so both steps are
skipped, and no amount of configuration turns them back on.

What still applies:

- the **type-level** `Query` right, which was never a row-shaped question;
- everything the actions class does itself.

What does not, and is therefore the actions class's job:

- filtering rows to what this caller may see;
- omitting values this caller may not read;
- gating anything the rows can be acted upon with.

**Every composed query announces this at startup**, naming the type:

```
Spark: query 'StartPageCollections' (Custom.GetCollections) is COMPOSED — its rows come from
StartPageActions, not from a collection. Row filtering, value redaction and per-row permissions do
not apply and cannot: there is no document behind a row. …
```

The line exists because a composed grid is **indistinguishable from every other Spark grid** once
rendered. The risk is not the deliberate landing page someone wrote on purpose; it is the next
developer who reaches for a composed query because it is easier than writing a row rule, over data
that does have owners, and gets a grid that looks exactly right.

### What a composed type may not do

Two things are refused at `--spark-verify-model` and again when queries load, rather than when
someone opens the page:

- **Streaming.** Streaming watches a RavenDB collection for changes, and a composed type has none.
  This used to die at the first `MoveNext` inside an open websocket as `CLR type '' not found`,
  wrapped in `{"message":"Stream failed"}`.
- **A query over a type that shows nothing on it.** If every attribute is `"showedOn":
  "PersistentObject"` the grid gets rows and no columns — a blank table that reads as an empty
  result. Both virtual types that existed before this feature were `PersistentObject`-only, and
  copying one is exactly what an author adding a query will do.

## Taking over paging: `SparkQueryPage<T>`

Some sources cannot be paged by the framework — an external API that takes its own offset, an
aggregate whose total is a separate query, a log store that only answers in chunks. Such a method
returns its own page:

```csharp
public async Task<SparkQueryPage<LogRow>> GetLogs(CustomQueryArgs args)
{
    var (rows, total) = await logApi.FetchAsync(args.Skip, args.Take, args.Search);
    return new SparkQueryPage<LogRow>(rows, total);   // rows = this page, total = the whole result
}
```

**The authority rule is binary.** Either the framework owns filtering, search, sorting, counting and
paging, or the method does — never some of each. Returning a bare sequence keeps all five with the
framework; returning a `SparkQueryPage<T>` transfers all five, and the request's `Skip`, `Take`,
`Search` and `Query.SortColumns` reach the method through `CustomQueryArgs` for it to honour.

There is no partial mode because half-delegation fails invisibly. If the method pages and the
framework sorts, the framework sorts **the current page** and presents it as a global ordering: the
grid looks sorted, every page is internally ordered, and the sequence across pages is wrong, with
nothing about the result saying so. The same applies to a framework `.Count()` over an
already-trimmed sequence — the pager then reports the page size as the total and offers one page.

Row-level security is **not** part of what transfers. Whether this caller may see a row is a
different question from how rows are presented, and not one a method opts out of by choosing a
return type.

⚠️ **One consequence for custom actions.** A selection is normally re-materialized by re-running its
query narrowed to the selected ids, which is what keeps index-computed columns populated. A
`SparkQueryPage<T>` cannot be asked for "the page containing these ids", so selections from such a
query fall back to a document load and **lose index-computed values**. Same for a streaming query.
See [custom actions](guide-custom-actions.md).

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

## Rendering a query in the frontend

Everything above describes what the server sends. `@mintplayer/ng-spark/grid` renders it, and there
is exactly one grid — `spark-query-grid` — behind both the `/query/:alias` page and every sub-query
card on a detail page.

```html
<spark-query-grid queryId="cars" />
```

It resolves the query, its entity type, the caller's rights, lookup references, paging, sorting,
search and custom actions. Give it a `queryId` and it does the rest.

### Rows from outside

`data` is optional. Leave it unbound and the grid fetches and pages server-side; bind it and the
grid renders what it is given and never fetches.

**A host that binds `data` must also bind `columns`** (#327). A projection cannot describe itself,
and the old fallback — inferring columns from whichever attributes the first row happened to carry —
is exactly the per-row metadata this design removes. That is the seam streaming uses — the WebSocket
lives in the page component, not the grid, so a detail page's bundle does not carry it. A streaming
query also suppresses its own fetch, so binding `data` asynchronously does not cost one wasted
`/execute` on mount.

The split mirrors the one `bs-datatable` already draws between `[data]` and `[fetch]`. Binding both
is not a supported combination.

### The first-column link is the rights model

The grid links the first column to the row's own detail page, gated on `Read`. A query granting
`Query` without `Read` therefore lists rows and withholds the link — the intended shape for a
`Custom.*` query that fabricates rows no detail page could load.

A custom `renderer` does **not** suppress that link: the cell renders inside the anchor, so a
renderer emitting its own `<a>` produces nested anchors.

`rowRoute` changes **where the link points**, per row, and nothing else:

```html
<spark-query-grid queryId="collections" [rowRoute]="routeFor" />
```

```typescript
routeFor = (row: QueryResultItem) => row.id.startsWith('collections/') ? ['/query', row.id.split('/')[1]] : null;
```

Returning `null` suppresses the link for that row. It is **not** a permission: `canRead()` still
decides whether any link renders at all, and `rowRoute` is never consulted when that gate is closed,
so it cannot reach a row the rights model withheld. It exists for rows whose natural destination is
not `/po/{type}/{id}` — a composed row that maps to a page of its own, say.

### Chrome, and replacing parts of it

`spark-query-card` wraps the grid in a `<bs-card>` with an icon, a caption and an action bar. With
no template supplied it renders all three itself, so an auto-rendered sub-query needs no host
markup at all.

Three structural directives override one region each, leaving the others at their defaults:

```html
<spark-query-card queryId="cars">
  <ng-template sparkQueryIcon>
    <spark-icon icon="car" />
  </ng-template>

  <ng-template sparkQueryCaption>
    Fleet <bs-badge [type]="colors.secondary">{{ count }}</bs-badge>
  </ng-template>

  <ng-template sparkQueryActions let-actions>
    <button (click)="exportAll()">Export</button>
    <!-- `actions` is the server's list — render it too, to add rather than replace -->
  </ng-template>
</spark-query-card>
```

Each directive takes an optional query alias, so one host can target a specific card among several:
`<ng-template sparkQueryIcon="employees">`. An untargeted template is the catch-all, and a targeted
one wins over it regardless of declaration order.

A sub-query rendered automatically by `spark-po-detail` has no tag to project into, and a structural
directive cannot cross a component boundary. Pass a `TemplateRef` instead — `spark-po-detail` accepts
`queryIconTemplate`, `queryCaptionTemplate` and `queryActionsTemplate` and forwards them to every
card it renders.

### Cells

`spark-grid-cell` decides what a `dataType` looks like — a checkbox for `boolean` (indeterminate
when null), a swatch for `color`, chips for a reference array — and dispatches a declared custom
renderer. The same component renders the AsDetail table on a detail page, so a column looks the same
wherever it appears.

Callers resolve the *value*; the cell only presents it. A label a client cannot compute — a
breadcrumb template naming a property `[IgnoreProperty]` keeps out of the model — is resolved by the
server and passed through, never recomputed here.

## Query Execution Flow

When the frontend requests a query:

1. The `SparkQuery` definition is loaded — queries live in the `"queries"` array of the entity's
   `App_Data/Model/*.json` file, not in a directory of their own.
2. **The caller is authorized**, against the query's declared `entityType`. This happens *before*
   any resolution work: reflecting over the context, reading a property and matching a CLR type to a
   model file all used to run for a caller with no `Query` right at all, because the only check sat
   after the last of them — so a misconfigured query answered a denied caller with an empty grid
   instead of a denial.
3. The source resolves: a `Database.*` query to a SparkContext property, a `Custom.*` query to a
   method on the entity's actions class (found by type, or **by name** for a composed type).
   Every failure here throws and names the fix — the nine silent "return an empty result" paths are
   gone, because an empty grid is indistinguishable from a correctly configured query over an empty
   collection.
4. The query's `indexName` (or the entity's declared default) resolves through the index catalog;
   when the index has a `[FromIndex]` projection, `ProjectInto` is applied so computed and stored
   fields survive.
5. `.Include()` is chained for `[Reference]` properties and `GetDefaultIncludes()` paths, so
   referenced documents arrive in the same round trip.
6. The row-level security filter is composed, then the search term, then sorting — **in that order**,
   so the security predicate is ANDed with the search group rather than OR-ed into it.
7. RavenDB executes the query, under the request's `CancellationToken`.
8. Rows the caller may not see are dropped (`FilterAsync`), breadcrumbs are resolved in batches, the
   rows are mapped, and values the caller may not read are nulled (`RedactAsync`). **All three are
   skipped for a composed query** — there is no document to judge, redact or resolve against.
9. Results are deduplicated by id — **only on the RavenDB path**. A fan-out map (`SelectMany` over a
   collection) emits one index entry per element, so one document matches several times. Off the
   index there is no fan-out, and deduplicating there is destructive: every null key compares equal,
   so a computed row type with no readable id collapses the whole grid to one row.

   > An earlier version of this guide attributed the duplicates to `FieldIndexing.Search`. That was
   > measured and is not the case: a single-map index over an analyzed field returns exactly one row
   > per document. The deduplication is still correct, it just guards a different hazard.
10. The rows are **projected** into `columns` + `items` (see [what a query returns](#what-a-query-returns))
    and counted, filtered and paged — unless the method returned a `SparkQueryPage<T>`, in which case
    it already did all of that.

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
