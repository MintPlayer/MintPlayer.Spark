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
7. **The RavenDB test harness moved to `MintPlayer.Spark.Testing.RavenDB`** (M5, D35). `MintPlayer.Spark.Testing` is
   provider-neutral now and references no database client. Replace the package reference with
   `MintPlayer.Spark.Testing.RavenDB`, and rename the base classes:
   - `SparkTestDriver` → `RavenSparkTestDriver`
   - `SparkSharedDatabase` → `RavenSparkSharedDatabase`
   - `SparkSharedTestDriver` → `RavenSparkSharedTestDriver`

   The namespace is unchanged. `SparkEndpointFactory` takes an `ISparkTestDatabase`: pass `Store.AsSparkTestDatabase()`
   where you passed `Store`. The old names now belong to the neutral drivers (below).
8. **On RavenDB, `== null` through Spark's session finds a field the document never wrote** (M5, O11). A document
   saved before a nullable property existed now matches `x.Prop == null` and no longer matches `x.Prop != null`, as a
   `NULL` column does on SQL. It applies to `ISparkSession.Query`, the query pages and `DeleteWhere`/`UpdateWhere`;
   a raw `IAsyncDocumentSession` query is unchanged.
9. **PostgreSQL sorts nulls first ascending and last descending** (M5, D4), like the other providers.
10. **`ISparkSession` has two new members** (M6, D37/D38): `OptimisticConcurrency` (the neutral
    `OptimisticConcurrencyMode.Writes`) and `Query<T>(TimeSpan waitForIndexes)` (RavenDB's `WaitForNonStaleResults`
    on one query). A custom `ISparkSession` implementation must add them.
11. **Settings, Boards and Organizations no longer reference RavenDB** (M6). They run on `ISparkStore`/`ISparkSession`,
    so any store provider keeps them, and their packages no longer bring `RavenDB.Client` along. Internal helpers
    that took an `IAsyncDocumentSession` (for example `OrganizationMembershipReader.LoadAsync`) take an
    `ISparkSession`: wrap a raw session with `session.AsSparkSession()`. Notifications' own reads and writes are
    neutral too; it keeps its RavenDB reference until messaging is ported (M7).
    Apps follow (M6d): `IWebhookPublisher.PublishAsync` takes an `ISparkSession`, the package no longer references
    RavenDB, and `OidcMachineClients` takes `ISparkSession`/`ISparkStore`.
    `ISparkSession.LoadStartingWithAsync` gained a `startAfter` parameter before `cancellationToken`: a caller that
    passed the token by position names it (`cancellationToken: ct`).
    MailManager, Cron and MCP no longer reference RavenDB either (M6e–g):
    - Mail delivery records and MCP plans are `IExpiring` (`ExpiresAtUtc`) instead of carrying hand-written `@expires`
      metadata. An MCP plan's store expiry is one day after it stops being appliable, so an expired plan still answers
      "expired".
    - Cron claims occurrences through `ISparkAtomicValues`, with the same `cron/{job}` key and value as before.
    - Apps, MailManager and MCP register their document types (`AddSparkStoredType`), so SQL Server and PostgreSQL get
      their tables.
    Spark.Files runs on any store (M6h). RavenDB attachments stay the default file store on RavenDB. On SQL Server,
    PostgreSQL and InMemory, `AddFiles` needs a file store (`files.UseFileSystem(path)`, `UseAzureBlob` or `UseS3`);
    without one the app refuses to start.
    Spark.Authorization runs on any store (M6j) and no longer requires RavenDB. The user store finds users by name and
    by external login through reservations (`usernames/…`, `logins/…`; compare exchange on RavenDB), as it already
    did by email, so user names and logins are now unique. Migration `M_202611251200_UserLookupReservations` claims
    them for existing RavenDB users at startup; a duplicate name or login from before stays with the first user and is
    logged. A user document written around the user store (a seeder, an import) holds no reservation and is not found by
    name, email or login: call `SparkUserReservations.ClaimAsync(store, userId, user)` after writing it.
    `SparkUserBackfill` and the older user migrations run on RavenDB only.
    Spark.Contributions runs on any store (M6i) and no longer references MintPlayer.Spark.RavenDB. **Its document ids
    change:** every contribution and current id now starts with its own collection
    (`songlyricscontributions/songs/1/lyricscontributions/…`, `songlyricscurrents/songs/1/lyrics/…`), because a
    relational table accepts only ids of its own collection. Contributions stored under the old ids
    (`songs/1/lyricscontributions/…`) are not found any more; an app with contribution data must re-key them (load each,
    store it under the generated `GetId`, delete the old one) before upgrading. The startup shape marker moves to
    `contributionshapemarkers/{queryname}`, so the first start rebuilds each declaration's current documents once.
    A model's seeded `lineDiff` options are not rewritten by the synchronizer: delete the `renderer`/`rendererOptions`
    of the contribution's value attributes and synchronize to re-seed a pattern that takes the new ids.
12. **Messaging runs on any store with subscriptions** (M7b, D40), and its package no longer references RavenDB:
    - `IMessageOutbox.EnqueueAsync` and `ISparkAfterCommitOutbox.EnqueueAsync` take an `ISparkSession`, not a RavenDB
      session. Wrap a raw session with `session.AsSparkSession()`.
    - The indexes `SparkMessages_ByPriority` and `SparkMessages_ByQueue` and `CreateSparkMessagingIndexes` are gone.
      The feeder and the retry sweeper query one view, `SparkMessages_ByState`, which `UseSpark` deploys with the
      app's indexes: a static index on RavenDB, an SQL view on SQL Server and PostgreSQL. On an existing RavenDB
      database the two old indexes stay until you delete them. A test that builds messaging without `UseSpark` must
      deploy the view itself.
    - `AddNotifications()` no longer requires RavenDB: notifications run on any store.
    - The messaging lease moved from the compare-exchange key `spark/messaging/leader` to `spark/messaging/lease`.
      While old and new hosts run side by side during a deploy, each version holds its own lease. Stop the old
      hosts before starting the new ones.
    - `SparkSubscriptionWorker<T>` runs on `ISparkSubscriptions`. `Filter` is a LINQ expression, `ProcessBatchAsync`
      takes a `SparkSubscriptionBatch<T>`, and the RavenDB-specific members (`DocumentStore`, `Database`,
      `ConfigureSubscription`) are gone. The constructor now takes `ISparkSubscriptions` and an `ILoggerFactory`.
    - On a relational store, messaging's background work waits for `UseSpark` to apply the schema
      (`ISparkStoreReadiness`, below).
    - `RetryNumerator`, a RavenDB counter, moved from `MintPlayer.Spark.SubscriptionWorker.Abstractions` to
      `MintPlayer.Spark.Replication.Services`, the namespace of its only user.
13. **History and SoftDelete no longer reference RavenDB's client** (M7d, S8). They work through `ISparkRevisions` and
    `ISparkStore`:
    - `ISoftDeleteRevisions.DeleteAsync` takes the entity type and the id (was the RavenDB session's document).
    - `HistoryInterceptor` reads versions through `ISparkSession`.
    - The licence probe and the revisions-configuration operations moved to `MintPlayer.Spark.Storage.Raven.RavenSparkRevisions`.
    - The licence check now covers every collection History configures, not only the changed ones.
14. **Moderation runs on any store** (M7e) and its package no longer references RavenDB's client:
    - Its four RavenDB indexes are `SparkView`s now, plus six row-per-entity views that replace its plain collection
      queries (which made RavenDB build auto indexes). The names are `Moderation_ReputationCells`,
      `Moderation_PendingReputation`, `Moderation_VotePairs`, `Moderation_Votes`, `Moderation_Flags`, `Moderation_Ledger`,
      `Moderation_IpObservations`, `Moderation_Cases` and `Moderation_Audit` (`ModerationIndexNames.All`).
    - `Moderation_EntriesToCredit` is gone: the crediting job reads `Moderation_Ledger`. On an existing RavenDB
      database the old indexes (`Moderation/…`) stay until you delete them.
    - `IModerationAccounts`' default implementation reads users through `ISparkStore`.
    - Two id prefixes change, because a relational store requires an id to start with its collection: summaries are
      `reputationsummaries/{userId}` (were `moderationreputation/…`) and review cases `reviewcases/…` (were
      `moderationcases/…`, now `ModerationIds.CasePrefix`). The crediting job rewrites every summary, so old summaries
      are only orphaned; open cases under the old prefix are refused by the review endpoints and need re-flagging.
15. **`ISparkSession.Query<TRow, TView>()` on RavenDB reads the view's columns** (M7e). A map view used to hand back the
    source documents, so a computed column came back empty or as the document's field of the same name. A query the
    caller does not project itself is now read with `ProjectInto<TRow>()`.
16. **`UseOpenSearch` declares that it needs RavenDB** (M7f, D44). The engine syncs from RavenDB's `Spark_Search`
    index, so on another store the host refuses to start, naming "Spark.Search.OpenSearch". Before, the whole of
    `AddSearch` was refused off RavenDB; now it runs on any store, as does a custom `UseProvider<T>` engine.

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
   reference lists become join tables, and the version covers the whole aggregate. **Live updates work on both**
   (M4e): every table has a `SparkChangeStamp` column the database moves on each write (`rowversion`; on PostgreSQL
   an `xid8` set by a trigger the migrations create), deletes leave a row in `spark.Tombstones` for a day, and the
   change feed polls every `options.ChangeFeedPollInterval` (1 s). An open transaction delays the feed until it ends,
   on PostgreSQL even one in another database on the same server. A migration that drops and re-adds the column of a renamed attribute (same id) is
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
11. **Provider-neutral tests** (M5, D35): a class deriving from `SparkTestDriver` (per case) or `SparkSharedDatabase`
    (per class) gets a `Database`, passes it to `SparkEndpointFactory` and seeds with `factory.SeedAsync(session => …)`.
    It runs on whichever database the project's test package provides: `MintPlayer.Spark.Testing.RavenDB`,
    `MintPlayer.Spark.Testing.SqlServer` (`SPARK_TEST_SQLSERVER`, else LocalDB, else a Testcontainers container),
    `MintPlayer.Spark.Testing.PostgreSql` (`SPARK_TEST_POSTGRESQL`, else an embedded server), or InMemory from the
    neutral package. A project with several picks one with `SPARK_TEST_DATABASE`.
12. **`ISparkSubscriptions`** (M7a, D39): durable, exclusive, at-least-once subscriptions over a collection with a LINQ
    filter, on every store. RavenDB runs them as data subscriptions; InMemory, SQL Server and PostgreSQL as a cursor
    over the commit stamp with a lease. Stores that have it report `SparkStoreCapabilities.Subscriptions`.
13. **`ISparkStoreReadiness`** (M7b, D41): a background service awaits `WaitAsync()` before its first store access.
    On a relational store it returns once `UseSpark` has applied the schema; elsewhere it returns at once. A feature
    library registers it with `services.AddSparkStoreReadiness()`.
14. **`ISparkSession.Query<TRow, TView>()` takes a `SparkView` on every store** (D42). RavenDB queries the view's static
    index and InMemory renders it; SQL Server and PostgreSQL already read the SQL view.
15. **`ISparkRevisions`** (M7d, S8): configure, list, read and delete document revisions on every store, through one
    API. RavenDB keeps its own revisions; SQL Server and PostgreSQL write `spark.Revisions` in the writing transaction
    on every write path, set-based updates and deletes and the expiry sweep included; InMemory keeps them beside its
    documents. Stores that have it report `SparkStoreCapabilities.Revisions`. History and SoftDelete now run on any
    store with revisions.
16. **Map-reduce and multi-map views** (M7e, D43): `SparkView<TRow>.Reduce` (`group r by key into g select new TRow
    { …, g.Sum(…) }`) and several maps joined by `Concat`. RavenDB deploys a map-reduce or multi-map index; SQL Server
    and PostgreSQL a view with `UNION ALL` under `GROUP BY`, always current; InMemory computes it on read. Such a view
    is never an entity type's default index.
17. **`ISparkSession.Query<TRow, TView>(TimeSpan waitForIndexes)`** (M7e): a read that must include every committed
    write. RavenDB waits for the index and throws `TimeoutException` when it is still stale; the other stores have
    nothing to wait for.
18. **Site search on every store** (M7f, D44). RavenDB keeps its generated `Spark_Search` index (Lucene, fuzzy).
    SQL Server, PostgreSQL and InMemory search each searchable type's collection directly:
    - **Fields:** the label's attributes and `search.fields`.
    - **Matching:** every word must be a whole word of them. The last word is completed while suggesting, and
      soft-deleted rows are left out.
    - **Order:** best first.
    - **Limits:** no typo tolerance, and SQL does not fold accents in stored text.
19. **Fan-out views** (M7g): a `SparkView` map may read a list,
    `from x in s.Set<T>() from item in x.List select new TRow { … }`, yielding one row per item. RavenDB fans out in
    the index; SQL Server reads the list column with `OPENJSON` and PostgreSQL with `unnest`. It works with or without
    a reduce, and a missing list yields no rows.
