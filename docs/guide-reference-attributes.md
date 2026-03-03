# Reference Attributes

Spark supports two kinds of references between entities: **Reference** attributes (links to another entity stored in RavenDB) and **LookupReference** attributes (links to a predefined set of values). Both are defined with C# attributes on entity properties and auto-detected during model synchronization.

## Overview

| Reference Type | C# Attribute | Stored Value | UI Control | Use Case |
|---|---|---|---|---|
| Reference | `[Reference]` | RavenDB document ID (e.g. `"Companies/abc-123"`) | Modal picker with search | Link to another entity (Car -> Company) |
| Transient LookupReference | `[LookupReference]` | Enum key | Dropdown or modal | Fixed set of values defined in code |
| Dynamic LookupReference | `[LookupReference]` | String key | Modal picker | User-managed values stored in RavenDB |

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
  "label": { "en": "Owner" },
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

If you omit the `query` parameter from `[Reference]`, Spark resolves it automatically by matching the target type to a SparkContext property:

```csharp
// SparkContext has: IRavenQueryable<Company> Companies
// Auto-resolves to query name: "GetCompanies"

[Reference(typeof(Company))]  // query auto-resolved to "GetCompanies"
public string? Owner { get; set; }
```

This works because the synchronizer maps each SparkContext property to a query name using the pattern `Get{PropertyName}`.

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

#### Step 1: Define the Enum and Lookup Class

```csharp
using MintPlayer.Spark.Abstractions;

namespace MyApp.Library.LookupReferences;

public enum ECarStatus
{
    InUse,
    OnParking,
    InMaintenance,
    Stolen
}

public sealed class CarStatus : TransientLookupReference<ECarStatus>
{
    private CarStatus() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<CarStatus> Items { get; } =
    [
        new CarStatus()
        {
            Key = ECarStatus.InUse,
            Description = "Car is in use",
            Values = _TS("In use", "En usage", "In gebruik"),
        },
        new CarStatus()
        {
            Key = ECarStatus.OnParking,
            Description = "Car is parked",
            Values = _TS("In parking lot", "Dans le parking", "Op parking"),
        },
        new CarStatus()
        {
            Key = ECarStatus.InMaintenance,
            Description = "Car is being maintained",
            Values = _TS("In maintenance", "En maintenance", "In onderhoud"),
        },
        new CarStatus()
        {
            Key = ECarStatus.Stolen,
            Description = "Car is stolen",
            Values = _TS("Stolen", "Vole", "Gestolen"),
        },
    ];
}
```

Key points:
- Extend `TransientLookupReference<TEnum>` where `TEnum` is your enum
- Override `DisplayType` to control the UI: `Dropdown` renders a `<select>`, `Modal` renders a search modal
- `_TS(en, fr, nl)` is a helper that creates a `TranslatedString` with values for each language
- The `Items` collection must be `static` and `IReadOnlyCollection<T>`

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
  "label": { "en": "Status" },
  "dataType": "string",
  "lookupReferenceType": "CarStatus",
  "showedOn": "Query, PersistentObject"
}
```

Note that `dataType` remains `"string"` (not `"Reference"`). The `lookupReferenceType` field tells the frontend to render a dropdown or modal picker using the lookup values.

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
