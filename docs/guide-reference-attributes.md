# Reference Attributes

Spark supports two kinds of references between entities: **Reference** attributes (links to another entity stored in RavenDB) and **LookupReference** attributes (links to a predefined set of values). Both are defined with C# attributes on entity properties and auto-detected during model synchronization.

## Overview

| Reference Type | C# Attribute | Stored Value | UI Control | Use Case |
|---|---|---|---|---|
| Reference | `[Reference]` | The target row's `Id` — a document id (e.g. `"Companies/abc-123"`) for a document type | Modal picker with search, or an inline dropdown on an AsDetail row | Link to another entity (Car -> Company) |
| Transient LookupReference | `[LookupReference]` | Enum key | Dropdown or modal | Fixed set of values defined in code |
| Dynamic LookupReference | `[LookupReference]` | String key | Modal picker | User-managed values stored in RavenDB, **one global set per lookup name** |

Two properties of that table are worth reading before you pick one, because both are easy to
discover the hard way:

- **Only `[Reference]` can vary its options per object.** Both lookup shapes produce one option set
  for the whole application — a transient one at compile time, a dynamic one per lookup *name*. If
  the valid choices depend on which object is being edited, see
  [Scoping a reference's options to the parent object](#scoping-a-references-options-to-the-parent-object).
- **A reference target need not be a document type.** An embedded value object works, and stores
  whatever its `Id` property holds. See
  [The target does not have to be a document type](#the-target-does-not-have-to-be-a-document-type).

## Reference Attributes

A Reference attribute stores the RavenDB document ID of another entity. On the detail page, it renders as a clickable link. On the edit form, it shows a modal picker that queries the referenced entity type.

### Step 1: Add the Reference Property

Use the `[Reference]` attribute on a `string?` property. The property stores the target entity's document ID.

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Library.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }

    [Reference(typeof(Company), "GetCompanies")]
    public string? Owner { get; set; }
}
```

The `[Reference]` attribute takes two parameters:

| Parameter | Required | Description |
|---|---|---|
| `targetType` | Yes | The C# type of the referenced entity |
| `query` | No | The query name used to populate the selection modal. If omitted, Spark auto-resolves it from the SparkContext property name (e.g. `Companies` -> `GetCompanies`) |

The `Owner` property stores a value like `"Companies/a1b2c3d4-..."`. The property name becomes the attribute name in the model JSON.

### Step 2: Synchronize the Model

Run `dotnet run --spark-synchronize-model` to update the model JSON. The synchronizer detects the `[Reference]` attribute and generates:

```json
{
  "name": "Owner",
  "dataType": "Reference",
  "isRequired": false,
  "query": "GetCompanies",
  "referenceType": "MyApp.Library.Entities.Company",
  "showedOn": "PersistentObject"
}
```

Key fields:
- `dataType` is set to `"Reference"` (not `"string"`)
- `referenceType` identifies the target entity's CLR type
- `query` specifies which query to use for the selection modal

### Step 3: How the UI Works

On the **detail page**, a Reference attribute renders as a clickable link showing the referenced entity's display name (as defined by `displayAttribute` in the target entity's model JSON). Clicking the link navigates to the referenced entity's detail page.

On the **create/edit form**, a Reference attribute renders as an input with a search button. Clicking the button opens a modal that shows the referenced query's list view. The user selects an entity from the list, and the reference is set.

### Auto-Resolved Query Names

If you omit the `query` parameter from `[Reference]`, the synchronizer gives the attribute the query of the target
type's **plain entity root**: the SparkContext property whose body is exactly `Session.Query<T>()`
([default_index_PRD.md](default_index_PRD.md), D6).

```csharp
// SparkContext has: public IRavenQueryable<Company> Companies => Session.Query<Company>();
[Reference(typeof(Company))]  // query auto-resolved to "GetCompanies"
public string? Owner { get; set; }
```

Each context root mints a `Database.{Property}` query named `Get{Property}`, and that query runs on the target type's
default index (see [the default index](guide-queries-and-sorting.md#the-default-index-of-an-entity-type)). What decides
which root a reference uses:

- **Several roots of one type.** Only the plain one counts. A filtered or customized root (`ActiveCompanies =>
  Session.Query<Company>().Where(c => c.IsActive)`), one bound to an index, or one returned by a helper method is never
  a reference's lookup by default, whatever its declaration order. To use one of those, name its query:
  `[Reference(typeof(Company), "GetActiveCompanies")]`. The Spark generator finds the plain root (it can read the
  getter's body, which reflection cannot) and records it as `[SparkPlainRoot(typeof(T), nameof(Prop))]` on the
  context's generated partial, so **the context must be `partial`**.
- **A view root is not a lookup.** `VCompanies => Session.Query<VCompany, Companies_Overview>()` mints no query. The
  generator writes such a root for every public `[FromIndex]` view, as a typed C# shortcut.
- **A type from a package needs a root too.** A reference to a package's type, such as `Organization` from
  `MintPlayer.Spark.Organizations`, resolves only when your context has
  `public IRavenQueryable<Organization> Organizations => Session.Query<Organization>();`. The root's attributes are
  derived from the CLR type, through the `spark.ModelAttributes(...)` hook (see
  [Hand-editing the model](#hand-editing-the-model-what-survives-synchronization)).
- **No root at all** leaves the attribute without a query, and the edit page's picker without options. A writable
  reference then gets build warning **SPARK056**, with a code fix that adds the root, and synchronize prints the same
  warning. Read-only references open no picker and are not reported: no setter or `[ReadOnly(true)]` for both, and
  for synchronize also `isReadOnly` or `showedOn: None` in the model file, which the analyzer cannot see. `SparkUser` has no root on purpose: a root would derive its password hash and
  security stamp as attributes.
- **A name or alias that is already taken.** When `Get{Property}` or its derived alias already belongs to another query
  (a package's `Custom.Organizations` uses the alias `organizations`), the minted query takes the first free one
  (`GetOrganizations2`, `organizations-2`). The model file records it, and synchronize prints it (D11). Existing queries
  are never renamed.

Whatever query serves the picker, the label of the current value is the target's breadcrumb, loaded by id, on the
list, the detail page and the edit form alike (D10).

### Scoping a reference's options to the parent object

The `query` parameter is not limited to a plain listing of the target type. **Point it at a
`Custom.*` query and the option list can be scoped to the specific object the reference lives on.**
This is the only mechanism in Spark that can do that — see
[Choosing between the three option sources](#choosing-between-the-three-option-sources) for why
neither lookup shape can.

The query method receives `CustomQueryArgs.Parent`, which Spark resolves and authorizes in the
query endpoint before the method runs:

```csharp
public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IEnumerable<ProjectColumn>> Project_Columns(CustomQueryArgs args)
    {
        // Return empty rather than EnsureParent — see the create-form caveat below.
        if (args.Parent is null || !string.Equals(args.ParentType, nameof(GitHubProject), StringComparison.OrdinalIgnoreCase))
            return [];

        var board = await session.LoadAsync<GitHubProject>(args.Parent.Id);
        return board?.Columns ?? [];
    }
}
```

```csharp
[Reference(typeof(ProjectColumn), "Project_Columns")]
public string TargetColumnOptionId { get; set; } = string.Empty;
```

Plus the query entry in the target type's model JSON, and a right (see the traps below):

```jsonc
{ "id": "<guid>", "name": "Project_Columns", "entityType": "ProjectColumn",
  "source": "Custom.Project_Columns",
  "sortColumns": [ { "property": "Name", "direction": "asc" } ] }
```

#### It works from an AsDetail row, and the parent is the ROOT

A reference on an embedded `AsDetail` + `isArray` row gets the **root document** as its parent — not
the row. So a rule inside a sub-table can be scoped by the object that owns the sub-table, which is
usually what you want.

#### The target does not have to be a document type

A reference target needs only:

1. a model file with a `clrType`,
2. at least one attribute with `showedOn: Query` (a zero-column query is refused at startup),
3. a `{TypeName}Actions` class to host the method, and
4. a readable, non-empty, unique `Id` per row.

An **embedded value object** satisfies all four, so you do not have to promote a child collection
to documents just to make it pickable. Note what supplies the stored value: the mapper reads the
CLR `Id` property directly rather than asking RavenDB for a document id, so whatever `Id` holds is
what the picker writes.

A `Custom.*` method may return `IQueryable<T>`, `IRavenQueryable<T>`, `IEnumerable<T>`, or a `Task<>`
of any of those. Returning a plain in-memory sequence is fully supported: every Raven-specific step
(projection, `.Include()`, row-filter push-down, search push-down, provider sorting) is guarded on
the returned object actually being a Raven queryable, and sorting, search and paging fall back to
in-memory equivalents.

> **Entity-backed is not the same as composed.** Because the target's model declares a `clrType`,
> such a query is *not* a `clrType`-less composed query, and none of the composed-query costs apply
> — no `ISparkOwnsRowSecurity` to implement, no row-filtering or redaction responsibility moved onto
> your actions class.

#### Traps

- **Grant the right, or the dropdown is silently empty.** `/spark/queries` only lists a query whose
  `entityType` the caller holds `Query` on, and the client treats an unknown query name as an empty
  result — no error, no log. Add `QueryRead/{TargetType}` (or `Query/{TargetType}`) to
  `security.json`. This is the single easiest way to ship a picker that looks fine and does nothing.
- **Prefer a null-guard over `args.EnsureParent(...)`.** The **create form sends no `parentId`**, so
  `EnsureParent` throws there and surfaces as a 500 from the picker. Returning empty is the honest
  degradation. Check the parent *type* as well as its presence — answering with a different
  parent's children is worse than answering with none.
- **Option lists are capped at 50 and truncate silently.** The client passes no `take`, so the
  server default applies. Fine for a handful of child rows; wrong for a large collection. The
  picker's search box filters the already-fetched array in memory — it does not re-query.
- **A stored value that cannot be loaded degrades, it does not break.** The breadcrumb resolver
  attempts one batched load and skips misses silently; the client then falls back to matching the
  value against the loaded option list, and finally to showing the raw value. For an embedded target
  there is no document to load, so the option list is what makes the label readable.
- **`GetRowFilterAsync` cannot do parent scoping.** Its signature is `(string action)` and nothing
  else; the parent is never forwarded into row security. It is the right tool for "which rows may
  *this caller* see" and structurally the wrong tool for "which rows belong to *this parent*".
  Parent scoping belongs in the query method.

### Choosing between the three option sources

The opening table lists three attribute shapes; this is the decision that actually matters between
them, because only one can vary its options per object:

| Need | Use | Why |
|---|---|---|
| A fixed set, known at compile time | `[LookupReference]` + a `[SparkLookup]` enum | Values are C#, labels in `translations.json`; no storage, no query |
| A user-managed set, shared app-wide | `[LookupReference]` + `DynamicLookupReference<TValue>` | Persisted and runtime-editable, but **one global set per lookup name** — a single `LookupReferences/{Name}` document |
| Options that depend on **which object** you are editing | `[Reference(typeof(X), "CustomQuery")]` | The only shape whose option source receives the parent |

The middle row is the trap: a dynamic lookup is editable at runtime, which makes it *look* like the
answer for "columns of this board" — but its values are global to the lookup name, so it would offer
every board's columns on every board.

### Hand-editing the model: what survives synchronization

Not every model field can be hand-added. `--spark-synchronize-model` regenerates attributes from the
C# types, so anything it derives is overwritten:

| Field | Hand-edit survives? | Where it comes from |
|---|---|---|
| `editMode` (e.g. `"inline"`) | **Yes** | Hand-authored presentation |
| `isReadOnly`, `showedOn` (including `"None"`) | **Yes** | Hand-authored presentation (`isVisible` no longer exists, #264) |
| `referenceTree` | **Yes** | Hand-authored presentation (see [Hierarchical references](#hierarchical-references-a-lazy-tree-picker)) |
| `lookupReferenceType` | **No — stripped** | Derived from `[LookupReference(typeof(...))]` |
| `query`, `referenceType` | **No — stripped** | Derived from `[Reference(typeof(...), "...")]` |
| An inline `queries` entry on an embedded type | **Yes** | Explicitly preserved by the synchronizer |
| A minted query's `name`, `alias`, `sortColumns` | **Yes** | Minted once; the file owns them afterwards. A stepped-aside `GetX2` stays the root's query (D11) |
| The seed of a new attribute (`Hidden`, `Exclude`) | **Yes** | `spark.ModelAttributes(...)` runs only when an attribute is first created (D9) |

What a context root's properties become **when synchronize first creates them** is decided by
`spark.ModelAttributes((type, property) => …)`, returning `Default`, `Hidden` (`showedOn: None`, read-only, not
required) or `Exclude` (no attribute). Spark's default hides the audit names `CreatedBy`, `CreatedAt*`, `ModifiedBy`
and `ModifiedAt*`; wrap `ModelAttributeConventions.AuditMembersHidden` to keep that. It matters most for a root over
a package's type, whose class you cannot annotate. Fleet treats the mail package's `UpdatedAt*` as an audit stamp too:

```csharp
spark.ModelAttributes((type, property) =>
    property.Name.StartsWith("UpdatedAt", StringComparison.Ordinal)
        ? ModelAttributeSeed.Hidden
        : ModelAttributeConventions.AuditMembersHidden(type, property));
```

The hook never changes or deletes an existing attribute (#253), and `[IgnoreProperty]` always excludes. `Hidden` is
layout only: the value still reaches the client, so a secret needs `Exclude`.

So a lookup or reference must be declared in C#. Adding `lookupReferenceType` to the JSON by hand
appears to work until the next synchronization silently reverts the attribute to a free-text box.

### Hierarchical references: a lazy tree picker

When the candidates form a tree (warehouse locations site → zone → aisle → rack → level → bin, a
venue's sections and blocks), declare `referenceTree` on the referencing attribute in the model JSON.
The multi-reference picker (`<bs-tree-select>`) then lists the roots first and loads a node's children
when it is expanded, instead of one flat list:

```json
{
  "name": "Locations",
  "dataType": "Reference",
  "isArray": true,
  "query": "GetLocations",
  "referenceType": "Warehouse.Location",
  "referenceTree": { "parentAttribute": "Parent", "childCountAttribute": "ChildCount" }
}
```

- **`parentAttribute`** names the attribute of the **referenced** type that holds a row's parent id: a
  Reference from that type to itself, empty on a root.
- **`childCountAttribute`** (optional) names a column holding the number of children, or a boolean. A
  row with `0` or `false` there is drawn as a leaf. Without it every node shows an expand chevron, and
  one without children expands to nothing.

**How the levels are read.** The picker runs the attribute's own query with the existing column filter
(#431): `columns: [{ name: "Parent", includes: [null] }]` for the roots, `includes: [id]` for one node's
children, paged like the flat list. Nothing new is exposed: the rows come back under the query's row
security, and search stays flat (a hit is listed wherever it sits in the tree). The provider is
`ReferenceQueryTreeSelectProvider` in `@mintplayer/ng-spark/po-form`; its third argument is the
`referenceTree`.

**Traps.**
- The parent attribute must be **filterable** on the query: shown on it (`showedOn` includes `Query`)
  and not `canFilter: false`. A refused filter does not fail, it returns the rows unnarrowed, so every
  node would list the whole set as its children. `--spark-verify-model` refuses such a declaration.
- `includes: [null]` matches a stored `null`. A document saved through the RavenDB client stores the
  null; a document written without the field at all is not a root to this filter.

**Materialized paths.** For subtree reads ("all stock under aisle A"), store the parent chain as a
path string next to the parent reference. `MaterializedPath` (`MintPlayer.Spark.Abstractions.Hierarchy`)
builds and takes apart paths (`Child`, `Parent`, `Ancestors`, `Depth`, `DescendantPrefix`, `Reparent`),
and `MaterializedPathQueries` (`MintPlayer.Spark.Hierarchy`) does the RavenDB side:

```csharp
// Everything under BRU/A, never BRU/A10: the prefix is "BRU/A/", passed as a query parameter.
var below = await session.Query<Location>().WhereDescendantOf(l => l.Path, "BRU/A").ToListAsync();

// Move BRU/A (and its subtree) under BRU/B: one set-based patch, every path a parameter.
await MaterializedPathQueries.MoveSubtreeAsync<Location>(store, l => l.Path, "BRU/A", "BRU/B");
```

The move rewrites only the paths. Setting the moved node's own parent reference is the caller's job,
in the unit of work that decided the move; a move into the node's own subtree is refused.

### Including Reference Data in Index Projections

Reference properties store only the document ID. To display related data in list views (e.g. the owner's name in a car list), you need to include it in a RavenDB index using `LoadDocument`:

```csharp
using Raven.Client.Documents.Indexes;

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
                      };

        StoreAllFields(FieldStorage.Yes);
    }
}
```

The projection type then has a computed property:

```csharp
[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? OwnerFullName { get; set; }
}
```

After synchronization, the model JSON will include `OwnerFullName` as a query-only attribute (`"showedOn": "Query"`), while `Owner` remains a PersistentObject-only Reference attribute (`"showedOn": "PersistentObject"`). This means the list view shows the owner's name as text, and the detail/edit pages show the full reference picker.

## Lookup References

LookupReferences are used for attributes that should be selected from a predefined set of values, like statuses, categories, or brands.

### Transient Lookup References (Code-Defined)

A transient lookup reference defines its values in C# code. Values are not stored in RavenDB -- they are compiled into the application.

#### Step 1: Declare the Enum

Mark the enum `[SparkLookup]`. The source generator (`SparkLookupGenerator`, in
`MintPlayer.Spark.SourceGenerators`) emits the lookup class: one item per member, keyed by the member,
labelled by the `translations.json` key `lookup.{Enum}.{Member}`.

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Library.LookupReferences;

[SparkLookup]
public enum ECarStatus
{
    /// <summary>Car is in use.</summary>
    InUse,
    /// <summary>Car is parked.</summary>
    OnParking,
    InMaintenance,
    Stolen
}
// generated: public sealed partial class CarStatus : TransientLookupReference<ECarStatus>
```

The labels live with the app's other texts, in the declaring project's `App_Data/translations.json`
(for a library, the library's own layer; an app may override any of them in its layer):

```json
"lookup": {
  "ECarStatus": {
    "InUse":         { "en": "In use",         "fr": "En usage",        "nl": "In gebruik" },
    "OnParking":     { "en": "In parking lot", "fr": "Dans le parking", "nl": "Op parking" },
    "InMaintenance": { "en": "In maintenance", "fr": "En maintenance",  "nl": "In onderhoud" },
    "Stolen":        { "en": "Stolen",         "fr": "Volé",            "nl": "Gestolen" }
  }
}
```

Key points:
- The lookup's name (the generated class, and what model files put in `lookupReferenceType`) is the
  enum's name without its `E` prefix; pass one when it has none: `[SparkLookup("OrganizationRoles")] enum OrganizationRole`
- `[SparkLookup(DisplayType = ELookupDisplayType.Modal)]` renders a search modal instead of a `<select>`
- Items follow the declaration order; `[SparkLookupOrder(n)]` on members reorders them when the zero
  value (the stored default) is not the first to show
- The endpoint (`GET /spark/lookupref/{name}`) resolves each key through the composed translation layers;
  a key no layer defines shows the humanized member name (`InMaintenance` → `In Maintenance`)
- `tools/verify-translation-keys.test.mjs` (`npm run test:tools`, run by CI) fails when a member's label is
  missing, or when a `lookup.*` key labels no member any more
- An enum the generator cannot emit a class for (nested in a type, no `E` prefix and no name, aliased
  values, ...) is error **SPARK064**

**Keys stored as strings.** For a property that stores the key as a `string`, set `StringKeys = true`:
the class derives from `TransientLookupReference<string>` and carries a `const string` per member. A
member whose stored key differs from its name says so with `[SparkLookupKey]`:

```csharp
[SparkLookup(StringKeys = true)]
public enum EProjectBasis
{
    [SparkLookupKey("scoped")] Scoped,
    [SparkLookupKey("projection")] Projection,
}
// generated: ProjectBasis.Scoped == "scoped"; the label key stays lookup.EProjectBasis.Scoped
```

**Extra data per item.** Declare the extra properties in a partial of the generated class, with a private
setter, and fill them in `Initialize`, which runs once per item. The lookup endpoint sends every extra
public property in the value's `extra` dictionary:

```csharp
public sealed partial class CarStatus
{
    public bool AllowOnCarNotes { get; private set; }

    static partial void Initialize(CarStatus item) => item.AllowOnCarNotes = item.Key != ECarStatus.OnParking;
}
```

A lookup that one enum cannot express (a subset of another lookup's members) stays a hand-written
`TransientLookupReference<TKey>` subclass; give its items `TranslatedString.FromKey("lookup.{Enum}.{Member}")`
labels, never inline text.

#### Step 2: Apply to Entity Property

```csharp
[LookupReference(typeof(CarStatus))]
public ECarStatus? Status { get; set; }
```

The property type should be the enum type (nullable if optional).

### Dynamic Lookup References (Database-Stored)

A dynamic lookup reference stores its values in RavenDB. Users can add, edit, and remove values through the application UI.

#### Step 1: Define the Lookup Class

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Library.LookupReferences;

public sealed class CarBrand : DynamicLookupReference
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Modal;
}
```

No static `Items` collection is needed -- values come from the database.

#### Step 2: Apply to Entity Property

```csharp
[LookupReference(typeof(CarBrand))]
public string? Brand { get; set; }
```

For dynamic lookup references, the property type is `string?` (stores the lookup value's key).

### Model JSON for Lookup References

After synchronization, a lookup reference attribute in the model JSON looks like this:

```json
{
  "name": "Status",
  "dataType": "string",
  "lookupReferenceType": "CarStatus",
  "showedOn": "Query, PersistentObject"
}
```

Note that `dataType` remains `"string"` (not `"Reference"`). The `lookupReferenceType` field tells the frontend to render a dropdown or modal picker using the lookup values.

### Carry-over onto a generated index entity

With `[GenerateIndex]` you do not repeat these attributes on the projection — they are copied for you, verbatim
and including optional arguments. `[Reference(typeof(Company), "GetCompanies")]` on the entity arrives intact on
the index entity, which is what `SPARK002` requires.

Carry-over is a **deny-list**: everything travels except the generator's own directives (`[Search]`,
`[IgnoreForIndex]`, `[GenerateIndex]`, `[IgnoreProperty]`), so an attribute the generator has never heard of is
still copied. An attribute whose arguments cannot be rendered as source is skipped **and reported** as
`SPARK_INDEX_007`, never dropped silently.

A sort companion inherits the copied attributes **except** the reference-shaped ones: a companion is a plain
sort key, and declaring it a reference would make the model resolve a second reference to the same target.

Note that a `[Reference]` property is **not** given `[IgnoreProperty]` on the index entity. Doing so would strip
the reference from the model and break breadcrumb and `.Include()` resolution.

### Lookup References in Index Projections

If you include a lookup reference property in a projection type, apply the `[LookupReference]` attribute on the projection property too:

```csharp
[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }
}
```

This ensures the list view renders the lookup value's translated label instead of the raw enum value.

## Complete Example

From the DemoApp -- a Car entity with both reference types:

```csharp
// Entity (DemoApp.Library/Entities/Car.cs)
using DemoApp.Library.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Library.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }

    [LookupReference(typeof(CarStatus))]
    public ECarStatus? Status { get; set; }

    [LookupReference(typeof(CarBrand))]
    public string? Brand { get; set; }

    [Reference(typeof(Company), "GetCompanies")]
    public string? Owner { get; set; }
}
```

See also:
- `Demo/DemoApp/DemoApp.Library/Entities/Car.cs` -- entity with references
- `Demo/DemoApp/DemoApp.Library/LookupReferences/` -- lookup reference definitions
- `Demo/DemoApp/DemoApp/App_Data/Model/Car.json` -- generated model JSON
- `Demo/DemoApp/DemoApp/Indexes/Cars_Overview.cs` -- index with `LoadDocument` for reference data
