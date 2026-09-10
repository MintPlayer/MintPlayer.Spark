# AsDetail Attributes

Spark supports embedding complex objects and collections directly inside a PersistentObject using the `AsDetail` data type. This is useful for nested data like addresses, career histories, or any structured sub-object that should be stored as part of the parent document rather than as a separate entity.

## Overview

An `AsDetail` attribute stores an embedded object (or array of objects) inline within the parent entity. Unlike `Reference` attributes which link to separate documents by ID, `AsDetail` objects live inside the parent document in RavenDB.

There are two variants:

| Variant | C# Property Type | `isArray` | Example |
|---|---|---|---|
| Single object | `Address?` | `false` | A person's home address |
| Array/collection | `CarreerJob[]` | `true` | A person's employment history |

## Step 1: Define the Nested C# Class

Create a class for the embedded object.

**A single embedded object needs no id** — it is matched by its property name, which cannot go
missing or be reordered.

⚠️ **An element of an embedded *collection* does.** Mark the class `[ValueObject]` and declare it
`partial`, and a row key is generated for it. See [Row identity](#row-identity) below for why this is
not optional; a missing marker is a build error (SPARK017), not a silent omission.

**Single object example (Address):**

```csharp
// DemoApp.Library/Entities/Address.cs
namespace DemoApp.Library.Entities;

public class Address
{
    public string? Id { get; set; }
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
```

**Array element example (CarreerJob):**

```csharp
// HR.Library/Entities/CarreerJob.cs
using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

[ValueObject]
public partial class CarreerJob
{
    [Reference(typeof(Profession))]
    public string? ProfessionId { get; set; }
    public DateOnly ContractStart { get; set; }
    public DateOnly? ContractEnd { get; set; }
}
```

Note that AsDetail sub-objects can themselves contain `[Reference]` attributes pointing to other entities.

## Step 2: Add the Property to the Parent Entity

Add the nested type as a property on the parent entity. Spark's model synchronizer automatically detects complex types and arrays of complex types and assigns them the `AsDetail` data type.

**Single object:**

```csharp
// DemoApp.Library/Entities/Person.cs
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    // ...

    public Address? Address { get; set; }  // detected as AsDetail, isArray=false
}
```

**Array:**

```csharp
// HR.Library/Entities/Person.cs
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    // ...

    public Address? Address { get; set; }       // single AsDetail
    public CarreerJob[] Jobs { get; set; } = []; // array AsDetail
}
```

## Step 3: Model JSON (Auto-Generated)

After model synchronization, Spark generates the model JSON automatically. The key fields for an AsDetail attribute are:

**Single object (Address on Person):**

```json
{
  "name": "Address",
  "label": {"en": "Address", "fr": "Adresse", "nl": "Adres"},
  "dataType": "AsDetail",
  "asDetailType": "DemoApp.Library.Entities.Address",
  "isArray": false,
  "showedOn": "PersistentObject",
  "order": 7
}
```

**Array (Jobs on Person):**

```json
{
  "name": "Jobs",
  "label": {"en": "Jobs"},
  "dataType": "AsDetail",
  "asDetailType": "HR.Entities.CarreerJob",
  "isArray": true,
  "editMode": "inline",
  "showedOn": "PersistentObject",
  "order": 7
}
```

Key properties:

| Property | Description |
|---|---|
| `dataType` | Always `"AsDetail"` |
| `asDetailType` | The full CLR type name of the nested class. Spark uses this to look up the nested type's model JSON for its attributes. |
| `isArray` | `false` for single objects, `true` for arrays/collections |
| `editMode` | For arrays: `"inline"` (editable table) or `"modal"` (default, opens a modal per item) |

## Step 4: Model JSON for the Nested Type

The nested type needs its own model JSON file. Spark generates this automatically during model synchronization. The key addition for AsDetail types is the `displayFormat` property:

```json
{
  "name": "Address",
  "description": {"en": "Address", "fr": "Adresse", "nl": "Adres"},
  "clrType": "DemoApp.Library.Entities.Address",
  "displayFormat": "{Street}, {City} {State}",
  "displayAttribute": "Street",
  "attributes": [
    {
      "name": "Street",
      "label": {"en": "Street", "fr": "Rue", "nl": "Straat"},
      "dataType": "string",
      "isRequired": true,
      "order": 1,
      "rules": [
        { "type": "minLength", "value": 3 },
        { "type": "maxLength", "value": 200 }
      ]
    },
    {
      "name": "City",
      "label": {"en": "City", "fr": "Ville", "nl": "Stad"},
      "dataType": "string",
      "isRequired": true,
      "order": 2
    },
    {
      "name": "State",
      "label": {"en": "State"},
      "dataType": "string",
      "isRequired": true,
      "order": 3
    }
  ]
}
```

The `displayFormat` uses `{PropertyName}` placeholders to build a summary string. When the AsDetail object is displayed on the parent's detail or edit view, the format string is used. For example, `"{Street}, {City} {State}"` produces `"123 Main St, Springfield IL"`.

If no `displayFormat` is set, Spark falls back to the `displayAttribute` (a single property name), and then to the first non-null property value.

## Row identity

A collection element needs a stable key, and the reason is a save, not a screen.

When a parent is saved, the incoming collection has to be matched against the stored one. Without a
key that comparison is impossible, so the collection is **rebuilt from the payload** — and anything
the payload is not allowed to carry is gone. A property the model marks `isReadOnly` is refused on
the way in, which is correct, but on a freshly built row there is no stored value left to prefer, so
the field is simply lost. That is a real bug this design fixes: three server-owned fields on
`EventColumnMapping` were being wiped whenever a user edited an unrelated field on the parent.

The same comparison is what makes the row type's `New/X`, `Edit/X` and `Delete/X` rights decidable.
Before it existed those rights could be granted on an embedded type and no code read them.

### Adding a keyed collection to an app that already has documents

You do not need a backfill migration for the *new* collection. The startup gate asks

```rql
from 'Cars' where ServiceEntries[].Id == null or ServiceEntries[].Id == ''
```

and an **absent array does not match** — measured against 10,009 real documents with no such field,
`TotalResults: 0`. This is the opposite of the better-known rule for absent *scalars*, which do match
`== null` (and why an absent boolean needs `!= true` rather than `== false`).

A backfill is needed only when the collection already exists in stored documents and its rows predate
the key — `HR/Migrations/M_202609091210_BackfillValueObjectKeys.cs` is the worked example, and note
its `if (rows)` guard, which leaves an absent array absent for exactly this reason.

⚠️ **The gate can only check types it knows about.** It walks the registered value objects, so a type
whose generator never ran is not checked — it is skipped silently. See the warning under "Marking a
type".

### Marking a type

⚠️ **The attribute does nothing unless the declaring project references the generator.** Analyzer
`ProjectReference`s are not transitive, so a library that references `MintPlayer.Spark.Abstractions`
(where the attribute lives) still needs its own:

```xml
<ProjectReference Include="...\MintPlayer.Spark.LibraryGenerators\MintPlayer.Spark.LibraryGenerators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Without it `[ValueObject]` compiles cleanly and generates **nothing** — no key property, no
registration — and nothing fails: the startup gate only inspects types that registered, so the rows
simply reach the client with a null id. It cost a day on `Fleet.Library` (#386), and only a test that
asserted the key *as it arrives over the wire* caught it, because a keyless row deserialises with a
freshly minted guid and looks correct in memory.

```csharp
using MintPlayer.Spark.Abstractions;

[ValueObject]
public partial class CarreerJob
{
    public string Profession { get; set; } = string.Empty;
}
```

`partial` is required because the generator adds the key:

```csharp
[ValueKey] public string Id { get; set; } = global::System.Guid.NewGuid().ToString("N");
```

### When the type already has a key

Mark it, and nothing is generated:

```csharp
[ValueObject]
public partial class ProjectColumn
{
    /// The GitHub single-select option id — a real key, not a surrogate.
    [ValueKey] public string Id { get; set; } = string.Empty;
}
```

⚠️ **Do not simply omit `[ValueObject]` because the type has an `Id`.** "Declares an id" and "has a
row key" are different statements. A type absent from the registry is not neutral — its collections
cannot be matched at all, which is worse than having no key, because it looks fine.

The key need not be a `Guid` and need not be called `Id`; it need only be stable and unique within
its collection.

### Two checks, in two places

Each runs where its question can be answered:

| Check | Runs in | Asks | Code fix |
|---|---|---|---|
| SPARK016 | the entity library | is a decorated type `partial`? | **Declare the value object 'partial'** |
| SPARK017 | the application | is anything reachable from `SparkContext` **missing** the marker? | **Make this a value object** — adds `[ValueObject]`, `partial` and the `using` in one edit |

SPARK017 has to live in the application because only the context knows which types the model
reaches. The type it complains about, though, usually lives in the entity library — and that
mismatch decides where the error appears:

⚠️ **The diagnostic is reported on the `SparkContext` property that reaches the type, not on the
type itself**, whenever the type belongs to another project. This is not cosmetic. Roslyn's analyzer
driver *discards* any diagnostic whose location lies in a syntax tree the analyzed compilation does
not contain, so pointing at the type's own declaration made SPARK017 **vanish silently in the IDE**
— it showed up only at `dotnet build`, where a referenced project is a .dll and the location is
empty. Reporting on the context property keeps it inside the compilation, so you now see it while
editing, and `dotnet build` prints a file and line instead of a bare `CSC : error`. The message
still names the fully-qualified type, because the location no longer does.

⚠️ SPARK017 deliberately does **not** check `partial` — that is SPARK016's job, in the compilation
that owns the syntax. It is a division of responsibility, not a limitation: an analyzer reads
`partial` fine for any type declared in source. (Only a type that arrives purely as metadata is
opaque, and there is nothing to fix there anyway.) The SPARK017 **code fix** therefore adds
`partial` as well as the attribute — clearing SPARK017 only to raise SPARK016 on your next build
would be worse than no fix.

### What the code fixes can and cannot reach

Both fixes ship in `MintPlayer.Spark.LibraryGenerators`, so a project only gets the lightbulb if it
references that analyzer (in this repo, every app and every entity library does; NuGet consumers get
it through `MintPlayer.Spark.AllFeatures`).

They are an **IDE affordance and nothing else**. `dotnet build` behaviour is unchanged: the error is
the enforcement. And a type that arrives as a true metadata reference — a NuGet-packaged entity
library — has no document to edit, so it is reported but not fixable. Mark it by hand.

### Existing data

A row stored before its type gained a key needs a backfill migration, and it needs one **before the
key ships**.

A keyless row cannot be recognised at runtime. The key is minted by a field initializer that runs
during deserialization, so the row comes back carrying a fresh guid — a *different one on every
load*. In memory every row looks correctly keyed. Loading such a document also marks it dirty, so the
next unrelated `SaveChanges` in that session writes random ids into a document nobody edited.

So detection lives where the raw JSON is still visible:

- a `PatchByQueryOperation` migration per collection, filtering **inside the script**
  (`if (row.Id) return;`) rather than in a `where` clause, because patch-by-query does not wait for
  non-stale results and a stale index matches nothing — indistinguishable from "nothing to migrate";
- ⚠️ RavenDB's patch engine has **no guid helper** (`newGuid`, `raven`, `crypto`, `uuid` are all
  undefined), so derive the key from `id(this)` plus the array index: unique, and reproducible, which
  is what makes a replay a no-op;
- a startup gate that queries for surviving keyless rows and **refuses to start**.

## How AsDetail Objects Are Displayed

### Detail View

On the parent entity's detail page, a single AsDetail attribute is shown as a summary string rendered from the nested type's `breadcrumb` template in its model JSON (`"breadcrumb": "{Street}, {City}"`). A null object renders an empty cell.

### Where an embedded breadcrumb comes from

Worth understanding before changing anything near it, because the rule is not the one the shape suggests.

A **root** object's breadcrumb is resolved server-side, up front, for the whole page: `BreadcrumbResolver` walks the documents a page touches and returns a `BreadcrumbResult` — a map **keyed by document id**. `EntityMapper` then looks each object up by id.

An **embedded** object is not in that map, and the reason matters:

> An embedded row is absent from `BreadcrumbResult` because it is **embedded** — the map holds document ids, and an embedded object does not have one. It is *not* absent because it is unkeyed.

So the mapper's rule is **"the lookup missed, so render this object's own template in place"**, and nothing more. Since row identity shipped, every `[ValueObject]` carries a `[ValueKey]` — minted by a field initializer that runs during deserialization, so in memory every embedded collection row has one — and that key is *never* a key in `BreadcrumbResult`. A gate that also tested "and this object has no id" therefore skipped the renderer for every keyed row and fell through to the CLR type name. That was #384; it sat unnoticed for three months because every test fixture asserting an embedded breadcrumb used a **keyless** row type, which takes the same path either way.

Two consequences when writing code or tests here:

- **Do not reintroduce an id test.** Having a key says nothing about whether a breadcrumb was resolved for the object.
- **Test embedded breadcrumbs with a keyed type.** A keyless fixture passes with or without the mechanism working.

### How the row type's definition reaches the client

A detail table draws its columns from the **row type's** `EntityTypeDefinition`. The client used to
resolve that from the entity-type catalogue (`GET /spark/types`), which is scoped to the `Query`
right — and a row type usually has no rights of its own, because a row is edited through its parent
and nobody thinks to grant `Query/{RowType}`.

The result was silent and total: the row type was absent, `asDetailColumns` returned `[]`, and the
table rendered with **no columns at all** — headers gone, every row reduced to an action cell. No
console error, no server log.

So the server now ships each AsDetail row type's definition alongside its parent, in
`EntityTypeDefinition.DetailTypes`:

- **Gated on the parent's right**, not the row type's. That is the same gate that already ships the
  row *schema*: `EntityMapper.ScaffoldFrom` stamps every row in `attr.objects` with its type's
  attributes — labels, data types and validation rules — with no check on the row type at all.
- **Pruned.** `queryType`, `indexName`, `queries` and `alias` are cleared on the embedded copy. A
  detail table needs `attributes` and `id`; the rest is projection-and-query surface.
- **Per-caller, never persisted** — like `canRead`. A property that serialized by default would be
  round-tripped into every model file by `ModelSynchronizer`, permanently.

The catalogue is still the client's first source and is **not** widened. `Query`-scoping it is
deliberate: `spark-po-detail` depends on it for sub-query pruning.

> ⚠️ **Restoring the columns does not restore the buttons.** A row type with no grants gets an
> honest `canCreate: false` / `canEdit: false`, so the table becomes readable but not editable. If
> users are meant to manage those rows, the row type needs its own grant — a type used only as an
> `asDetailType` still needs a type-level right.

When a template renders blank, the server substitutes the **CLR type name** as a placeholder. That is deliberate but is *not* data — the client filters it out (`resolvedBreadcrumb` in `@mintplayer/ng-spark/models`) rather than printing it. Any new client code reading a breadcrumb off the wire must filter it too, or a cell with an unset template renders the literal `BuildFeedback`.

### Edit View -- Single Object (Modal)

When editing a parent entity, clicking on a single AsDetail attribute opens a **modal dialog** containing the nested type's edit form. The modal reuses the same `PoFormComponent` recursively:

```html
<app-po-form
  [entityType]="getAsDetailType(editingAsDetailAttr)"
  [(formData)]="asDetailFormData">
</app-po-form>
```

This means the nested object gets the same form rendering as any top-level entity, including validation rules, translated labels, and support for nested References.

### Edit View -- Array (Inline Table)

When `isArray` is `true` and `editMode` is `"inline"`, the array is rendered as an editable table directly in the parent form:

- Each column corresponds to an attribute of the nested type
- Each row is an item in the array
- Rows can be added (with an "Add" button) or removed (with a delete button per row)
- All standard input types are supported (text, number, date, boolean toggle, Reference selectors)

The inline table looks like:

```
| Profession  | Contract Start | Contract End | [x] |
|-------------|----------------|--------------|-----|
| Developer   | 2020-01-15     | 2022-06-30   | [x] |
| Manager     | 2022-07-01     |              | [x] |
                                         [+ Add]
```

### Asking the server before a row is added or removed

By default a detail grid is entirely client-side: **Add** pushes a blank row, the delete button
splices one out, and the parent's save is the first the server hears of either. There is nowhere to
put a server-computed default and nowhere to refuse a removal.

A row type can opt out of that, per type, by setting `serverSideRowLifecycle` on **its own** model
file — not on the parent's attribute, because the type that owns the hooks owns the decision, and one
setting then governs every grid the type appears in:

```json
{
  "persistentObject": {
    "name": "ServiceEntry",
    "clrType": "Fleet.Entities.ServiceEntry",
    "serverSideRowLifecycle": true,
    "attributes": [ ... ]
  }
}
```

With it on, the grid calls `POST /spark/po/{rowType}/new` before showing a new row and
`POST /spark/po/{rowType}/delete-row` before removing a stored one, and two hooks become reachable:

```csharp
public partial class ServiceEntryActions : DefaultPersistentObjectActions<ServiceEntry>
{
    public override Task OnNewAsync(SparkNewArgs<ServiceEntry> args)
    {
        // SetOriginalValue, NOT SetValue -- see below.
        args.PersistentObject[nameof(ServiceEntry.PerformedOn)]
            .SetOriginalValue(DateOnly.FromDateTime(DateTime.Today));
        return Task.CompletedTask;
    }

    public override Task OnDeleteRowAsync(SparkDeleteRowArgs<ServiceEntry> args)
    {
        // args.Row is the STORED row, never the caller's copy of it.
        var invoiced = args.Row.Attributes
            .FirstOrDefault(a => a.Name == nameof(ServiceEntry.IsInvoiced))?.Value;

        if (invoiced is true)
            throw new SparkValidationException("This entry has been invoiced.", "ServiceEntries");

        return Task.CompletedTask;
    }
}
```

Four things about this are worth knowing before you use it.

**Neither hook writes anything.** Construction is not persistence and removal is not deletion: the
row appears or disappears for real only when the parent is saved. A hook that touches the database is
writing outside the parent's unit of work. Record things from the *parent's* `OnBeforeSaveAsync`.

**Use `SetOriginalValue` for defaults, not `SetValue`.** `SetValue` marks the attribute changed,
which makes the object dirty before the user has typed anything — so adding a row and abandoning it
leaves the parent falsely modified, and on a replicated type widens the property list the sync action
reports.

**⚠️ A refusal is an affordance, not enforcement.** `OnDeleteRowAsync` stops a *cooperating* client.
It cannot stop one that never calls the endpoint and submits the parent with the row already gone,
because the endpoint writes nothing and the save is a separate request. What stops that caller is the
save path's per-row `Delete/{RowType}` check, which runs on every embedded collection regardless of
this flag. Put the rule in the row type's rights; use the hook to explain it.

**⚠️ The flag is not a permission and is outside the model hash.** It governs the round trip and
nothing else. Nothing that gates a write may ever read it — an unhashed model field that could switch
a rights check off would be one edit away from disabling it on a deployed model.

The endpoints check the **row type's own** right — `New/ServiceEntry`, not `New/Car` — matching the
button the client renders, and load the parent separately so a caller who cannot see a parent cannot
add rows to it. A new row arrives already carrying its row key, because the server constructs the CLR
entity rather than scaffolding from the model alone; that is also why C# property initializers show
up as the row's defaults.

### Edit View -- Array (Modal)

When `editMode` is `"modal"` (or omitted), each array item is edited via a modal dialog, similar to single-object AsDetail editing.

## Automatic Type Detection

Spark's model synchronizer automatically detects AsDetail types in two cases:

1. **Single complex type**: A property whose type is a non-primitive class (not `string`, `int`, etc.) is detected as `AsDetail`.

2. **Array/collection of complex types**: A property of type `T[]`, `List<T>`, `IList<T>`, `ICollection<T>`, or `IEnumerable<T>` where `T` is a complex type is detected as `AsDetail` with `isArray: true`.

The detection logic in `ModelSynchronizer` identifies complex types as those that are not built-in primitives, not `DateTime`/`DateOnly`/`Guid`/`Color`, and are classes with properties.

## RavenDB Storage

AsDetail objects are stored inline within the parent document in RavenDB. For example, a Person document with an Address looks like:

```json
{
  "Id": "people/1-A",
  "FirstName": "John",
  "LastName": "Doe",
  "Address": {
    "Street": "123 Main St",
    "City": "Springfield",
    "State": "IL"
  }
}
```

An array variant stores as a JSON array:

```json
{
  "Id": "people/1-A",
  "FirstName": "Jane",
  "Jobs": [
    { "ProfessionId": "professions/1-A", "ContractStart": "2020-01-15", "ContractEnd": "2022-06-30" },
    { "ProfessionId": "professions/2-A", "ContractStart": "2022-07-01" }
  ]
}
```

No separate documents or indexes are created for AsDetail objects -- they exist purely as embedded data within the parent.

## Complete Example

See the demo apps for working examples:

**Single AsDetail (Address):**
- `Demo/DemoApp/DemoApp.Library/Entities/Address.cs` -- C# class
- `Demo/DemoApp/DemoApp.Library/Entities/Person.cs` -- parent entity with `Address?` property
- `Demo/DemoApp/DemoApp/App_Data/Model/Address.json` -- nested type model with `displayFormat`
- `Demo/DemoApp/DemoApp/App_Data/Model/Person.json` -- parent model with `AsDetail` attribute

**Array AsDetail (CarreerJob):**
- `Demo/HR/HR.Library/Entities/CarreerJob.cs` -- C# class with `[Reference]` attribute
- `Demo/HR/HR.Library/Entities/Person.cs` -- parent entity with `CarreerJob[]` property
- `Demo/HR/HR/App_Data/Model/CarreerJob.json` -- nested type model
- `Demo/HR/HR/App_Data/Model/Person.json` -- parent model with `isArray: true` and `editMode: "inline"`
