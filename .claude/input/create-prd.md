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

## Further notes
Try to make a demo as complete as possible
