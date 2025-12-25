Help me create a Product Requirements Document.
Here's what we're building:

# What is Spark
Spark is supposed to be a framework like Vidyano, that allows you to write web-apps with a minimum of code.

## No DTO's
Instead of using DTO's for each database type, we're using a concept of PersistentObject.
A PersistentObject has Attributes (List<PersistentObjectAttribute>).
A PersistentObjectAttribute has Rules (Required, …), a data-type (string, number, decimal, "Reference" to another object of a specific type - Always Reference, ...).
The PersistentObjects are saved in the codebase as json files under App_Data/Model.

## No Repositories/Services
We don't need the Repository pattern because we can just build a Spark Middleware and endpoints (UseSpark/MapSpark)
which parses the request body as PersistentObject (containing the user-entered values), maps the attribute values to a corresponding database entity (Raven DocumentSession.Query)
and depending on the invoked endpoint we can just use the corresponding DocumentSession method
- GET /spark/po/[entitytypeguid] => session.Query<Data.Person>() => map to List<PersistentObject>
- GET /spark/po/[entitytypeguid]/{guidid} => session.Find<Data.Person>(guidid) => map to PersistentObject
- POST /spark/po/[entitytypeguid] => session.Store(person) => map back to PersistentObject and return with auto-generated values
- PUT /spark/po/[entitytypeguid]/{guidid} => either session.Store(person) or session.Advanced.Patch<,>
- DELETE /spark/po/[entitytypeguid]/{guidid}

[entitytypeguid] is the ID found in the json-file of the persistent-object

Because of this, we can just manipulate any type of object through the API, without adding more code.

## The "DbContext"
We don't need an actual DbContext, but using our own concept of a "DbContext" makes it easier to know what collections exist on the RavenDB database.

## Extension methods
We can write extension methods to make some things easier:
- PersistentObject.PopulateAttributeValues<T>(T entity, PersistentObject po) => reads the corresponding model-json file, for all Attributes (PersistentObjectAttribute) in the json file, the property value of the entity should be read, and filled into the corresponding po.Attributes value
- PersistentObject.PopulateObjectValues<T>(T entity, PersistentObject po) => opposite way

## Angular frontend
I want to see an angular app, that uses @mintplayer/ng-bootstrap and the BsShellComponent (examples are available in the demo app inside the library repo).
The app should ask the API for the available database queries, which you can find on the DbContext.
For each entity-type in the database, an accordion item should be shown in the sidebar, with a link to the angular page that lists the items of that type.
I want to see 4 pages to manipulate any type of object from the database.

## LookupReferences

LookupReferences allow you to have a property of any type (`int`, `string`, `Enum`, ...) on your database object class, while in the web-app it's displayed as a dropdown.

### Structure

A LookupReference has:
- **Key**: The value that gets stored on the object-property in the RavenDB database (can be `string`, `int`, enum value, etc.)
- **Values**: A `TranslatedString` that provides a description for each available application language

### Usage

To make a property use a LookupReference instead of a plain `int`/`string`/`Enum`, simply add the `[LookupReference(typeof(...))]` attribute on the property:

```csharp
public class Car
{
    public string Id { get; set; }

    [LookupReference(typeof(CarStatus))]
    public string Status { get; set; }  // Stored as string in DB, shown as dropdown in UI
}
```

### Types of LookupReferences

#### 1. Static (Transient) LookupReferences
These are defined in code and their available values never change after deploying the app. Example:

```csharp
public sealed class CarStatus : TransientLookupReference
{
    public const string InUse = nameof(InUse);
    public const string OnParking = nameof(OnParking);
    public const string Stolen = nameof(Stolen);

    private CarStatus() { }

    // Extra properties beyond Key/Values
    public bool AllowOnCarNotes { get; init; }

    public static IReadOnlyCollection<CarStatus> Items { get; } =
    [
        new CarStatus()
        {
            Key = InUse,
            Description = "Car is in use",
            Values = _TS("In use", "En usage", "In gebruik"),  // EN, FR, NL
            AllowOnCarNotes = true,
        },
        new CarStatus()
        {
            Key = OnParking,
            Description = "Car is parked",
            Values = _TS("In parking lot", "Dans le parking", "Op parking"),
            AllowOnCarNotes = false,
        },
        new CarStatus()
        {
            Key = Stolen,
            Description = "Car is stolen",
            Values = _TS("Stolen", "Volé", "Gestolen"),
            AllowOnCarNotes = true,
        },
    ];
}
```

Key characteristics:
- Inherits from `TransientLookupValue`
- Has a static `Items` collection with all possible values
- Uses `const string` fields for type-safe key references
- `_TS()` helper creates `TranslatedString` with translations for each supported language
- Can have extra properties (like `AllowOnCarNotes`) since it's just a runtime collection

#### 2. Dynamic (Persistent) LookupReferences
These are stored in the RavenDB database in a `LookupReferences` collection, allowing users to change the available values after deployment without code changes. Example:

```csharp
public class Car
{
    public string Id { get; set; }

    [LookupReference(typeof(CarStatus))]
    public ECarStatus Status { get; set; }  // Static lookup - enum stored in DB

    [LookupReference(typeof(CarBrand))]
    public string Brand { get; set; }   // Dynamic lookup - user can add new brands
}
```

```csharp
// Custom value class with extra properties for this lookup
public class CarBrandValue
{
    [Reference(typeof(Country))]
    public string CountryOfOrigin { get; set; }  // References another entity

    public int FoundedYear { get; set; }
}

// The lookup reference uses the generic base class
public sealed class CarBrand : DynamicLookupReference<CarBrandValue>
{
    // No static Items collection - values are loaded from the database
    // Users can add/edit/remove brands through the UI after deployment
    // Extra properties (CountryOfOrigin, FoundedYear) are stored alongside each value
}
```

Key characteristics:
- Inherits from `DynamicLookupReference<TValue>` with a custom value type
- No static `Items` collection - values come from the database
- Users can manage values through the application UI
- Values are stored in the `LookupReferences` collection in RavenDB
- Extra properties can reference other entities using `[Reference(typeof(...))]`

The base class and value structure:

```csharp
// Base class for dynamic lookup references without extra properties
public abstract class DynamicLookupReference : DynamicLookupReference<EmptyValue>
{
}

// Generic base class for dynamic lookup references with custom value properties
public abstract class DynamicLookupReference<TValue> where TValue : new()
{
    public string Id { get; set; }  // e.g., "LookupReferences/CarBrand"
    public string Name { get; set; }
    public List<LookupReferenceValue<TValue>> Values { get; set; }
}

// Base value class with standard properties
public class LookupReferenceValue<TValue> where TValue : new()
{
    public string Key { get; set; }
    public TranslatedString Values { get; set; }
    public bool IsActive { get; set; } = true;

    // Extra properties defined by TValue
    public TValue Extra { get; set; } = new();
}

public class EmptyValue { }
```

### TranslatedString

The `TranslatedString` type holds translations for multiple languages:

```csharp
public class TranslatedString
{
    public Dictionary<string, string> Translations { get; set; }

    // Helper to get translation for current culture
    public string GetValue(string culture) => Translations.GetValueOrDefault(culture);
}
```

### API Endpoints

The Spark middleware should expose endpoints for LookupReferences:
- `GET /spark/lookupref/{name}` - Get all values for a LookupReference (both static and dynamic)
- `GET /spark/lookupref` - List all available LookupReferences
- For dynamic LookupReferences only:
  - `POST /spark/lookupref/{name}` - Add a new value
  - `PUT /spark/lookupref/{name}/{key}` - Update a value
  - `DELETE /spark/lookupref/{name}/{key}` - Remove a value

These endpoints can only create/modify Dynamic LookupReferences.

### Frontend Integration

In the Angular frontend:
- When rendering a PersistentObjectAttribute that has a LookupReference, display a dropdown (`<select>` or `bs-select` from @mintplayer/ng-bootstrap)
- Fetch available options from the API
- Display the translated value based on the current application language
- Store the Key value when saving

## Further notes
Try to make a demo as complete as possible
