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

### Marking a type

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

| Check | Runs in | Asks |
|---|---|---|
| SPARK016 | the entity library | is a decorated type `partial`? |
| SPARK017 | the application | is anything reachable from `SparkContext` **missing** the marker? |

⚠️ SPARK017 has to live in the application because only the context knows which types the model
reaches. Under `dotnet build` a referenced project arrives as a .dll, so it reports **without a
source location** — a bare `CSC : error` naming the fully-qualified type. That still fails the build,
which is the point; the message carries the type name because it is all you get. In an IDE the same
analyzer gets a real location and the squiggle lands on the class.

⚠️ SPARK017 deliberately does **not** check `partial`: that keyword is source-only and invisible in
metadata, so no analyzer outside the declaring compilation can see it.

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

On the parent entity's detail page, a single AsDetail attribute is shown as a formatted summary string using the nested type's `displayFormat`. If the object is null, `(not set)` is displayed.

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
