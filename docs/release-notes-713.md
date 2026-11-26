# Typed ids: `Id<T>` on RavenDB and InMemory (#536, part 1)

> The first part of #536, split out of #697: every entity id becomes an `Id<T>`, on the stores Spark has today. The
> SQL Server and PostgreSQL providers follow in #697. The decisions this part implements (D7, D23, D27, O15) are in
> `docs/typed_ids_PRD.md`, and how it was built is in `docs/typed_ids_plan.md`.

**Packages:** the NuGet packages get a minor with breaking changes. The versioning engine computes the numbers from the
tags, and everything ships once the merged pull request is backported to `release/dotnet-11.x`.

---

## Breaking changes

1. **Entity ids are `Id<T>`, not `string`** (D23). Every Spark entity's `Id` and its reference fields are typed.
   - `Id<T>`, `[IdKind]` and `SparkIdKind` live in `MintPlayer.Spark.Attributes`. There is no public constructor:
     make a new id with `ISparkIds.NewAsync<T>()`, and turn text into one with `ISparkIds.Parse<T>` / `TryParse<T>`.
     `Parse` accepts the whole id or the key alone, in any casing, and rejects another collection's id.
   - An entity must be `partial`: the LibraryGenerators analyzer emits its internal `__DocumentId` (SPARK064 otherwise).
     Internal, not private: RavenDB looks for the identity on the stored type, and a base type's private member is
     invisible there.
   - **Loaded ids are lower case.** References stored before the upgrade keep their casing, so compare ids, and key
     sets and dictionaries by them, ignoring case. A hash or key derived from a loaded id changes once at deploy.
   - A store you build yourself (a test driver, a tool) must call `SparkStoreConfiguration.ApplySparkConventions`, or
     RavenDB can't set an `Id<T>` ("property type is not a string").
   - On a raw `IAsyncDocumentSession`, ids are not rewritten. A typed `Contains` fails there (use RQL
     `id() in ($ids)`), and projecting `r.Id` to text needs a row with a `string? Id`.
   - Stored document ids don't change: no data migration.
   - **Files in an object store** (S3 and the other `ObjectFileStore` backends) are kept under the lower-cased entity
     id. The files reconciler moves a legacy-cased key to its lower-case one once at startup and on every reconcile
     (`FilesReconciler.LowerCaseLegacyKeysAsync`), so no manual step is needed.
2. **`ISparkSession`'s untyped id members are gone** (D27). `ExistsAsync`, `Delete` and `GetVersionsAsync` take an
   `Id<T>`, or a `Type` and the id. `Delete(object)` refuses a string.

## New

1. **`Id<T>` controller parameters bind through the app model** (D7): `GET /people/PEOPLE/42` and `GET /people/42`
   bind the same id; text that isn't an id of the entity is a 400. Minimal-API handlers take the text and call
   `ids.Parse<T>`.
2. **SPARK065**: an `Id<T>` passed to a session's `entity` parameter (RavenDB's `Delete<T>(T entity)`,
   `StoreAsync(object entity)`, `Evict`) is a compile error. The call compiled and failed at runtime, because the
   session does not track the id. See `docs/diagnostics.md`.
