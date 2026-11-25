# Relational storage providers: SQL Server and PostgreSQL (#536)

> Work in progress on PR #697. Each milestone adds its changes here as it lands. Decisions and evidence:
> [relational_providers_PRD.md](relational_providers_PRD.md), plan: [relational_providers_plan.md](relational_providers_plan.md).

**Packages:** the NuGet packages get a minor with breaking changes. The versioning engine computes the numbers from the
tags, and everything ships once the merged pull request is backported to `release/dotnet-11.x`.

---

## Breaking changes

1. **Entity ids are `Id<T>`, not `string`** (M2, D23). Every Spark entity's `Id` and its reference fields are typed.
   - `Id<T>`, `[IdKind]` and `SparkIdKind` live in `MintPlayer.Spark.Attributes`. There is no public constructor:
     make a new id with `ISparkIds.NewAsync<T>()`, and turn text into one with `ISparkIds.Parse<T>` / `TryParse<T>`.
     `Parse` accepts the whole id or the key alone, in any casing, and rejects another collection's id.
   - An entity must be `partial`: the LibraryGenerators analyzer emits its private `__DocumentId` (SPARK064 otherwise).
   - **Loaded ids are lower case.** References stored before the upgrade keep their casing, so compare ids, and key
     sets and dictionaries by them, ignoring case. A hash or key derived from a loaded id changes once at deploy.
   - A store you build yourself (a test driver, a tool) must call `SparkStoreConfiguration.ApplySparkConventions`, or
     RavenDB can't set an `Id<T>` ("property type is not a string").
   - On a raw `IAsyncDocumentSession`, ids are not rewritten. A typed `Contains` fails there (use RQL
     `id() in ($ids)`), and projecting `r.Id` to text needs a row with a `string? Id`.
   - Stored document ids don't change: no data migration.
2. **`ISparkSession`'s untyped id members are gone** (M2, D27). `ExistsAsync`, `Delete` and `GetVersionsAsync` take an
   `Id<T>`, or a `Type` and the id. `Delete(object)` refuses a string.
3. **`Spark:DataProtection:Storage=RavenDb` is now `Storage=Store`** (M3, PRD §2.3). It keeps the key ring in the
   application's database through whichever store provider is registered. Update test hosts and any deployment that
   sets it; `KeysPath` is unchanged.
4. **`ISparkQueryProvider.Search` takes the words** (M3, S2b): `Search(source, properties, IReadOnlyList<string> words)`,
   plain text with no wildcards. A provider adds its own syntax; RavenDB still wraps each word in `*`.

## New

1. **`Id<T>` controller parameters bind through the app model** (M2, D7): `GET /people/PEOPLE/42` and
   `GET /people/42` bind the same id; text that isn't an id of the entity is a 400. Minimal-API handlers take the text
   and call `ids.Parse<T>`.
2. **Provider-neutral startup messages** (M3): a feature with no store asks for any store provider unless it names one,
   and the index-binding, live-update and expiration messages no longer assume RavenDB.
3. **`SparkExpressions.Captured(value, type)`** (M3, S2b): a request value as a captured field, which EF turns into a
   query parameter instead of a literal. Spark's own column filters and id restrictions use it.
