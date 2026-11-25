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
   - **Files in an object store** (S3 and the other `ObjectFileStore` backends) are kept under the lower-cased entity
     id. The files reconciler moves a legacy-cased key to its lower-case one once at startup and on every reconcile
     (`FilesReconciler.LowerCaseLegacyKeysAsync`), so no manual step is needed.
2. **`ISparkSession`'s untyped id members are gone** (M2, D27). `ExistsAsync`, `Delete` and `GetVersionsAsync` take an
   `Id<T>`, or a `Type` and the id. `Delete(object)` refuses a string.
3. **`Spark:DataProtection:Storage=RavenDb` is now `Storage=Store`** (M3, PRD §2.3). It keeps the key ring in the
   application's database through whichever store provider is registered. Update test hosts and any deployment that
   sets it; `KeysPath` is unchanged.
4. **`ISparkQueryProvider.Search` takes the words** (M3, S2b): `Search(source, properties, IReadOnlyList<string> words)`,
   plain text with no wildcards. A provider adds its own syntax; RavenDB still wraps each word in `*`.
5. **Spark's commands run after `Build()`, through one call** (M3, D34). `builder.SynchronizeSparkModelsIfRequested`
   (and `<TContext>`), `InitializeSparkSecurityIfRequested`, `VerifySparkSecurityIfRequested` and
   `InitializeSparkModerationIfRequested` are gone. Replace the block before `Build()` with this, after it:
   ```csharp
   using MintPlayer.Spark.Commands;

   var app = builder.Build();
   if (await app.RunSparkCommandIfRequestedAsync(args))
       return;
   ```
   The flags are unchanged and `--spark-help` lists them. Building the host opens no connection, so the model and
   security commands still run in CI without a database. An unknown `--spark-` flag now exits 2 instead of starting
   the app.
6. **`MintPlayer.Spark.Migrations` no longer references RavenDB** (M3, S15). It runs on any store provider, and the
   package no longer brings `RavenDB.Client` along: a migration that injects `IAsyncDocumentSession` needs the
   app's own RavenDB reference (`MintPlayer.Spark.RavenDB` brings it). The lock is now a renewed 2-minute lease
   instead of a 30-minute one, and a lock the previous version left behind is still respected until it expires.

## New

1. **`Id<T>` controller parameters bind through the app model** (M2, D7): `GET /people/PEOPLE/42` and
   `GET /people/42` bind the same id; text that isn't an id of the entity is a 400. Minimal-API handlers take the text
   and call `ids.Parse<T>`.
2. **Provider-neutral startup messages** (M3): a feature with no store asks for any store provider unless it names one,
   and the index-binding, live-update and expiration messages no longer assume RavenDB.
3. **`SparkExpressions.Captured(value, type)`** (M3, S2b): a request value as a captured field, which EF turns into a
   query parameter instead of a literal. Spark's own column filters and id restrictions use it.
4. **`ISparkCommand`** (M3, D34): a package registers a command-line command with
   `services.AddSparkCommand<TCommand>("--spark-…", description)`. It is constructed from DI only when its flag is
   passed, and `--spark-help` lists every registered one.
5. **`ISparkAtomicValues` and `ISparkStoreLock`** (M3, S6/S15): the neutral compare-exchange (get, compare-and-swap,
   delete, by version) that every provider implements, and a lease over it that core implements once: it renews
   itself while held, frees after its time to live when its holder dies, and cancels `Lost` when someone else took
   it.
6. **Schema migrations: `--spark-migrate`, `--spark-migration-script` and `Spark:Database:ApplyMigrations`** (M3,
   D33): a provider with a schema registers `ISparkSchemaMigrator`. By default the app applies pending migrations at
   startup. With `Spark:Database:ApplyMigrations=false` it refuses to start while any are pending, and a deploy step
   runs `--spark-migrate` (schema, indexes, data migrations) with a login that may change the schema, or a DBA runs
   the idempotent script `--spark-migration-script <file> [--from <m>] [--to <m>]` writes. RavenDB and InMemory have
   no schema, so for them nothing changes.
7. **`SparkView<TRow>`: a projection declared once, for every provider** (M3, D32). A view's `Map` reads
   `s.Set<T>()`, follows a reference with `s.Load<T>(id)` and binds the row's id with `s.IdOf(row)`. RavenDB renders
   it as a static index, InMemory as LINQ to objects, and SQL Server and PostgreSQL as a database view (created by the
   app's migrations, recreated when its `Map` or a table under it changes). A `Map` may only use constants. It is queried
   by its `Name`, like an index, and a model's `indexName` may name it. On RavenDB a native index of the same name
   shadows it. `[GenerateIndex]` now also emits `{Index}_View` beside the generated index, which the index shadows,
   so a RavenDB app sees no change. An application without RavenDB.Client gets only the row type and the view, which
   becomes the entity's default. Core's empty index catalog is replaced by one that lists the declared views, so
   an InMemory app can query a view.
8. **More of the store on `ISparkSession`** (M3, S6), so code needs no RavenDB session for them:
   - `SetExpiry`/`GetExpiry`: when the store may delete a document (RavenDB's `@expires`).
   - `StreamAsync(query)`: every row of a query as untracked `SparkStreamResult<T>` (id, document, version).
   - `Reserve`/`Release`/`GetReservationAsync`: unique keys claimed with the save. A key held by another existing
     document fails the save with `SparkReservationTakenException`; one whose holder is gone is taken over.
   - `DeleteWhereAsync`/`UpdateWhereAsync`: set-based delete and update by a predicate, with constant assignments
     (`set => set.Set(x => x.Status, Status.Archived)`).
   - `PatchAppend`: an append to a stored list that concurrent writers cannot lose.

   A custom `ISparkSession` implementation must add these members. On InMemory, `Query<T>()` now reads its rows when
   the query runs rather than when it is opened, as RavenDB does.
9. **`MintPlayer.Spark.SqlServer` and `MintPlayer.Spark.PostgreSql`** (M4, D2): `builder.UseSqlServer()` and
   `builder.UsePostgreSql()` store the app in a table per entity through EF Core 11, with the connection string from
   `ConnectionStrings:Spark` unless `options.ConnectionString` sets one. `options.ConfigureModel(mb => …)` adjusts the
   EF model last. Child collections (`[ValueObject]` lists) become child tables, references become foreign keys,
   reference lists become join tables, and the version covers the whole aggregate. *In progress: the change feed
   lands in the next slice.* A migration that drops and re-adds the column of a renamed attribute (same id) is
   refused (open question O19).
    The relational providers also keep atomic values (so leases work), integer ids (a HiLo), the Data Protection key
    ring, expiry and the expiration sweep, reservations, streams, set-based delete/update and appends. A `decimal`
    is `decimal(38,18)` on SQL Server. A `DateTime` keeps its ticks but not its `Kind` (open question O18); prefer
    `DateTimeOffset`.

    **Migrations (M4c):** at startup (or with `--spark-migrate`) the provider creates the database, applies Spark's
    own schema (schema `spark`) and then the app's EF migrations, holding a lock in the server's maintenance database
    so several instances migrate once. `--spark-synchronize-model` writes the migration for a model change into
    `Migrations/<Provider>` of the host project, once the host references **`MintPlayer.Spark.Relational.Design`** as a
    development dependency (it stays out of `dotnet publish`; publish, don't copy `bin`). A model with changes no
    migration covers stops startup with a message naming that command. `options.MigrationsAssembly` moves the
    migrations out of the entry assembly. `RelationalSchema.CreateFromModelAsync` builds a schema from the model
    without migrations, for test databases.
10. **`ISparkStoredTypes` and `AddSparkStoredType<T>()`** (M4, D36): the list of types a store keeps, from the app
    model plus explicit registrations. `SparkCollectionNames.Of(type)` is the collection (table) name every provider
    uses.
