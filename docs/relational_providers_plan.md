# Relational storage providers: plan

Issue [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). The what and why are in
[`relational_providers_PRD.md`](relational_providers_PRD.md). This file holds the how and when: spikes, grilling
rounds, milestones and status. Branch `feat/536-sql-server-provider`, draft PR #697, **one pull request** (O1).

## 1. Status (2026-11-24)

- **M1 (the gate spikes) is done; there is no product code yet.** Everything so far is docs and throwaway spikes
  under `docs/relational_spike/`, each with its evidence in PRD §8.
  - All gate spikes are done (2026-11-24): S2b, S3, S4, S5, S19, S20, S-ID1, S-ID2, S-ID4, and S-ID5's RavenDB
    half. PRD §8.3–8.12 has the evidence.
  - S1, S2, S12 and S13 are done too. Since round 3 they are history for the storage model.
  - The latest spike prototype is `docs/relational_spike/s2b/`: an EF implementation of the storage seam with views,
    versions and D30. It has 64/64 tests green on both databases, and its README says how to rerun it.
- **Every decision is made; nothing is open** (§3):
  - grilling rounds 1–5
  - the commercial-framework comparison and the LLM rounds 6–9
  - O1–O17 (O8 and O9 on 2026-11-24)
- **Scope:** SQL Server (2022, 2025) and PostgreSQL (17 and later; CI on 18, O8). MariaDB is deferred (O6). Tier 4
  (identity_provider, dashboards) is in. The demo is a new `apps/SqlDemo`.
- **Storage model (round 3):**
  - a table per entity, mapped by EF Core
  - the model synchronize generates EF migrations
  - AsDetail collections are child tables, and references are real FKs
  - the contract is typed, and ids are `Id<T>` (D23)
  - views come from neutral `SparkView` declarations (D32, S5)

  Breaking changes are allowed, with no backward compatibility.
- **Upstream:** S3 and S19 found two EF bugs. Their pull requests, dotnet/efcore#39784 and npgsql/efcore.pg#4163,
  are open with the CLA signed. The workarounds are in D24 (`FreshSnapshotMigrationsAssembly`) and D33
  (`AdvisoryLockHistoryRepository`), and they go once the fixes ship (§5).
- **Done: M2,** `Id<T>` and the typed contract on RavenDB and InMemory (§4 item 2). **In progress: M3** (§4 item 3).
  - **Release note:** `docs/release-notes-697.md` gets each milestone's breaking changes as they land.
  - **M3a, core clean-ups (PRD §2.3):** `SparkDataProtectionStorage.RavenDb` → `Store` (no shim; production
    sets only `KeysPath`, so only test hosts and docs changed; the "set Storage = File" hint named a value that
    never existed and now says `KeysPath`). `SparkStoreRequirement` with no provider asks for any store provider;
    a named provider still names its `Use` method (tests `No_store_and_no_provider_named_…`,
    `No_store_with_a_provider_named_…`). The LiveHub, index-binding and composed-query messages are neutral. The
    expiration floor stays on every provider, so one setting runs on all of them, and its message says the limit is
    RavenDB's.
  - **M3b, the query seam (S2b, PRD §8.10 findings 2–3):** `ISparkQueryProvider.Search` takes the words
    (`QueryExecutor.BuildSearchWords`: split, `*`/`?` stripped, null when empty); RavenDB wraps each word itself
    (`RavenSparkQueryProvider.WildcardTerm`, the same `*a* *b*` term as before). The executor's filter values
    (column filters, scalar and collection, and the restrict-to-ids lists) are captured fields through the new
    public `SparkExpressions.Captured(value, type)`, a field read off a constant holder as a closure compiles, so EF
    parameterizes them. RavenDB reads them as closures (`RavenIdQueryRewriter` evaluates any expression); the
    provider's own `In` keeps its constant, since the relational provider builds its own. Tests:
    `CapturedFilterValueTests` (red first: 3 value constants found), `SearchPushdownTests`, and 14 column-filter
    classes on RavenDB, 130 green. Still open in M3: the neutral view declarations (S5/D32), IoC (D34).
  - **M3c-1, the command runner (D34):** `ISparkCommand` (`RunAsync(args, ct)` → exit code) and
    `services.AddSparkCommand<T>(flag, description)` in Abstractions (`Commands/`), keyed scoped by the flag;
    `app.RunSparkCommandIfRequestedAsync(args)` in core (`Commands/SparkCommandRunner.cs`) resolves only the
    requested command in a scope. **Deviations from D34's text, same intent:** (1) the help line is a
    `SparkCommandDescription` singleton registered beside the command, so `--spark-help` constructs nothing without
    reading `ServiceDescriptor`s; (2) an unknown `--spark-` flag is refused with exit 2 instead of starting the
    host (every `--spark-` flag in the repo is a command, so a typo must not boot the web server). Last registration
    of a flag wins for both the command and its help line. Test: `SparkCommandRunnerTests` (7).
  - **M3c-2, the commands on `ISparkCommand` (D34):** the five commands are commands now. `AddSpark` registers
    `--spark-synchronize-model`, `--spark-verify-model`, `--spark-describe` (`SparkModelCommand`),
    `--spark-init-security` and `--spark-synchronize-security`/`--spark-verify-security`
    (`Commands/SparkCoreCommands.cs`); `AddModeration` registers `--spark-init-moderation`, `AddFiles`
    `--spark-regenerate-variants`. The public builder-phase methods (`SynchronizeSparkModelsIfRequested` and its
    `<TContext>` overload, `InitializeSparkSecurityIfRequested`, `VerifySparkSecurityIfRequested`,
    `InitializeSparkModerationIfRequested`, the image-variant one) are gone; their bodies are internal and take a
    `SparkCommandHost(Services, Environment, Provider?)`. Every app and the template call
    `if (await app.RunSparkCommandIfRequestedAsync(args)) return;` once, after `Build()`; with no command it runs
    `SynchronizeIfMissing`. **Deviations:** (1) the commands still report through `Environment.ExitCode`, read back
    by `ExitCodeOf`, so the ~60 test call sites stay as they were through test-only extensions of the old names
    (`tests/MintPlayer.Spark.Tests/_Infrastructure/SparkCommandTestExtensions.cs`); (2) the command reads the
    service registrations from a `SparkServiceRegistrations` singleton (the collection itself), since the model and
    posture code read descriptors; (3) the posture command runs on the app's provider, and the throwaway
    `BuildServiceProvider()` remains only for that test path. The null-guard tests went with the public methods.
    Test: `AddSpark_registers_the_core_commands`.
  - **M3d, atomic values and the lease (S6/S15, the first slice):** `ISparkAtomicValues` (get, `TryPutAsync` with
    an expected version where 0 means absent, `TryDeleteAsync`; **string values**, since every user so far stores a
    timestamp and a relational column then needs no serializer) in Abstractions, implemented by RavenDB
    (compare-exchange, index = version) and InMemory (a dictionary). `ISparkStoreLock`/`ISparkLease` are implemented
    once in core (`Services/SparkStoreLock.cs`) over the values: value `{holder}|{expires}`, renewed by
    compare-and-swap every third of the TTL, `Lost` cancelled when a renewal finds another holder or the store stays
    unreachable past expiry. A bare timestamp reads as a lease of an unknown holder (the runner's pre-#536 lock), and
    an unreadable value is taken over. The data-migration runner moved onto `ISparkStore` + the lease: TTL 30 → 2
    minutes now that it renews (a killed runner frees in 2 minutes, a long run is no longer taken over at 30), the
    35-minute wait budget kept so a lock the old version left is outlived, and the migration token is linked to
    `Lost`. `MintPlayer.Spark.Migrations` dropped its RavenDB requirement and its `RavenDB.Client` reference. Tests:
    `SparkAtomicValuesConformanceTests` (Raven + InMemory, plus a DateTime-written value read as text),
    `SparkStoreLockTests` (9), `SparkMigrationRunnerTests` unchanged in substance (their pre-seeded locks are old-format
    DateTimes): 56 green. Not yet: cron, messaging and dashboards on the values/lease (the feature-library port), and
    the rest of S6 (`SetExpiry`, `StreamAsync`, `Reserve/Release`, `UpdateWhere/DeleteWhere`, `PatchAppend`).
  - **M3e, the schema migrator and the migration commands (D33):** `ISparkSchemaMigrator` (pending, apply, write
    script) and `ISparkDataMigrations` in Abstractions. Only a provider with a schema registers a migrator; absent
    means nothing to do (RavenDB, InMemory). `UseSpark` runs `SparkSchemaStartup` before the indexes:
    `Spark:Database:ApplyMigrations` (default true) applies, false refuses to start while migrations are pending,
    naming them and both flags. New core commands: `--spark-migrate` (schema, then the indexes, then the data
    migrations; exits 0) and `--spark-migration-script <file> [--from] [--to]` (usage error 2; a store without schema
    says so and writes nothing). `MintPlayer.Spark.Migrations` registers `ISparkDataMigrations` over its runner.
    **Deviations from D33:** (1) the concurrency guard lives inside the provider's `ApplyAsync` (EF takes its own
    database lock), not a core lease around it; (2) the data migrations keep their own lease and their AfterSpark slot
    at startup, so RavenDB's startup order is unchanged; (3) with `ApplyMigrations=false` the data migrations still run
    at startup, since they need only data rights; (4) on RavenDB `--spark-migrate` deploys the indexes (a data
    migration may query a new one) but installs no expiration stamper, which only a running app needs. Index deploy
    is now `SparkExtensions.DeploySparkIndexes(IServiceProvider, …)`. Tests: `SparkMigrationCommandsTests` (9),
    `ProviderNeutralCoreTests` (Abstractions, core and Migrations reference no Raven/EF/Npgsql/SqlClient assembly).
  - **M3f-1, neutral views: the declaration, core's catalog, InMemory (D32/S5):** `ISparkViewSource`
    (`Set<T>()`, `Load<T>(string?)` and `Load<T>(Id<T>?)`), `ISparkView`, `SparkView<TRow>` (declaration read once,
    `CollectionType` = the first `Set`, `ReadTypes`) and the public `SparkViewRewriter` base in
    `Abstractions/Views/SparkView.cs`. Core's `EmptyIndexCatalog` became `SparkViewCatalog`: it enters every concrete
    view with a public parameterless constructor (name, collection, row type as projection) and elects defaults like
    RavenDB's catalog (a bare `[DefaultIndex]` claim wins, else the sole view of a type). InMemory's `OpenIndex` renders
    a view as LINQ to objects over the session (`InMemoryViewRenderer`: a per-render id lookup, case-insensitive, and
    null-safe member access through `Load`), materialized once. `SparkIndexBindingStartupCheck` accepts a binding that
    names a view declared in the index assemblies. Tests: `SparkViewTests` (7).
  - **M3f-2, RavenDB renders a view:** `RavenViewIndex<TEntity>` (a `SparkIndexCreationTask`, so the S-ID6 id rewrite
    applies) turns `Set<T>().Select(…)` into `docs.Select(…)`, `Load` into `LoadDocument` (a typed id as its text),
    drops the row's `Id` binding and stores all fields; any other shape throws, naming the view. The catalog enters a
    view after every native index, and **a native index of the same name shadows it** (for a RavenDB app the generator
    will emit both, and the native one carries analyzers and companions); an explicit `RegisterIndex` of a clashing
    view throws. `RavenIndexDeployment` deploys the unshadowed views after the assemblies' native indexes (the
    deployment filter applies to view types). `OpenIndex` on a view is `Query<TEntity>(viewName)`, which the executor's
    `Project` turns into `ProjectInto<TRow>`. The view-entry helpers moved to Abstractions (`SparkViewEntries`, internal)
    so both catalogs share them. Tests: `RavenSparkViewTests` (2): the same declaration and rows as InMemory, and the
    rendered map. The test entities hand-write the generated `__DocumentId` member, as `SparkIdConformanceTests` do.
  - **M3f-3, the generator emits a view beside each generated index (2026-11-25):** `GenerateIndexGenerator` writes
    `{Index}_View : SparkView<{IndexEntity}>` with `Name => "{Index}"` and the index's own map, read from
    `__spark.Set<T>()` (the generated maps are plain member reads, so every provider can render them). The row's id is
    `__spark.IdOf(item)`, new on `ISparkViewSource`: the stored id without the generator knowing the id's type.
    RavenDB drops the binding, InMemory reads `session.GetId`, EF (M4) will read the key. InMemory now propagates
    absent values as a database does: any member read through something that can be null, and a dictionary indexer
    (a translation), yields the default. Decisions:
    (1) The view is emitted only inside the existing gate (an application that references RavenDB) and only when
    `SparkView<TRow>` resolves; the native index shadows it there, so no app's behaviour or model files change.
    (2) It carries no `[DefaultIndex]` while the native index beside it holds the claim. **Splitting the gate** (views
    without RavenDB, no `[FromIndex]`/native index, the view claiming the default) **waits for M4**, when an EF app
    exists to verify it.
    Found on the way: since S-ID6 (`dee4c6778`) `SparkMultiMapIndexCreationTask` hides Raven's `AddMap` with a
    rewriting one, and `DefaultIndexAnalyzer` only recognised Raven's, so a Spark multi-map's claims all raised
    SPARK053. It now accepts both (`DefaultIndexAnalyzerTests` 23/23). Tests: generator suite 740/740 (two snapshots
    gained the view), `SparkViewTests` (10).
  - **M3g, the rest of S6 on `ISparkSession` (2026-11-25):** five contracts, each shaped by its callers and tested in
    `SparkStoreConformanceTests` on RavenDB and InMemory (47/47):
    - `SetExpiry(entity, DateTimeOffset?)`/`GetExpiry`: the store's own expiry of a tracked entity (`@expires` on
      RavenDB), kept by later writes until set again, and an expiry-only change is a write. Callers (mail, MCP plans,
      moderation, OIDC, agent grants, messaging) only write it; `GetExpiry` serves the suite and the SQL column.
    - `StreamAsync(query)` → `SparkStreamResult<T>(Id, Document, Version)`, untracked. InMemory's `Query<T>()` now
      reads its rows when the query runs (as RavenDB's does), so a stream can read them detached; a row the session
      already tracks comes back as that instance.
    - `Reserve(key, owner)`/`Release(key, owner)`/`GetReservationAsync(key)` + `SparkReservationTakenException`. A key
      whose holder document no longer exists is taken over (the rule `UserStore` and `OidcClientIdReservation` have
      today). InMemory applies them in the commit; RavenDB claims by compare exchange before the documents, frees
      its claims when the write fails, and releases after it succeeds (a crash in between leaves an ownerless claim,
      which the takeover rule frees). Raven keys and values are the callers' existing ones, so the stored reservations
      keep working. ⚠️ On RavenDB a claim and the owner's first write are still two steps: a second claimant who looks
      in between sees no owner and takes the key over. That is the race the callers have today; SQL closes it.
    - `DeleteWhereAsync(predicate)`/`UpdateWhereAsync(predicate, set => set.Set(x => x.M, value))`: immediate,
      set-based, outside the unit of work (no hooks, tracked copies go stale), returning the count. RavenDB renders
      the LINQ query's RQL and parameters and appends `update { this.<path> = $__spark_setN; }` with
      `RqlIdentifier.FieldPath`-validated paths; values are parameters. Constants only (`SparkSetters<T>`).
    - `PatchAppend<T, TItem>(id, x => x.List, item)`: applied by `SaveChanges` to the stored document, so concurrent
      appends all land (RavenDB's typed `Advanced.Patch`; InMemory a patch write under the commit lock, dry-run first
      so a failing patch writes nothing). Messaging's metadata patches (`@expires` on progress documents) remain
      for the messaging port (M7), which writes expiry through `SetExpiry`.
    The feature libraries still call RavenDB directly; they move onto these contracts in their tier ports (M6–M8).
    That closes M3 except the generator gate split, which is M4's.
- **Done: M4** (§4 item 4), in five slices (M4e closed it, 2026-11-25):
  - M4a: the packages, the EF model, the store, the session and the query provider, with store tests on both
    databases
  - M4b: the S6 members (expiry, stream, reservations, `DeleteWhere`/`UpdateWhere`, `PatchAppend`), atomic values,
    leases, the data-protection key store, the expiration sweeper and the integer HiLo
  - M4c: migrations (D24, D33): the generator in `MintPlayer.Spark.Relational.Design` (O17), `ISparkSchemaMigrator`,
    the startup guard and the script; tests then build their schema from migrations, not `EnsureCreated`
  - M4d: views (D32: `ToView`, the `Spark:ViewSql` annotations, the differ subclass), `Query<T, TIndex>`, and the
    generator gate split
  - M4e: the change feed (D6 stamps) ✅
  - **M4a (2026-11-25):**
    - `MintPlayer.Spark.Relational` (EF Core 11 Relational), `MintPlayer.Spark.SqlServer` (`UseSqlServer()`, binary
      `Latin1_General_100_BIN2` keys, SqlException numbers) and `MintPlayer.Spark.PostgreSql` (`UsePostgreSql()`,
      `"C"` keys, `jsonb`, SQLSTATEs). `SparkStoreProviders.SqlServer`/`PostgreSql`.
    - **Which types are tables (PRD D36):** core's `ISparkStoredTypes` lists the model's entity types that resolve to
      a non-abstract class with a writable `Id`, minus `[ValueObject]`s, plus `AddSparkStoredType<T>()` for stored
      types outside the model (framework documents, in later slices). Table names come from the new shared
      `SparkCollectionNames.Of(type)`, which InMemory now uses too, so ids are the same on every provider.
    - `SparkRelationalModel`: a table per root type, TPH for derived types, a case-insensitive name collision check,
      join tables for reference lists. `SparkDbContext` maps it: the key column holds the key only (`Id<T>` →
      `Guid`/`long`/text by kind, D23) with converters, a shadow `Version` concurrency token, `[ValueObject]`
      collections as child tables (D25), references as FK columns with `Restrict` FKs (D26), complex values and
      `TranslatedString` as JSON columns, `List<primitive>` as EF primitive collections, get-only members only with a
      `__stored_` field (O13). The `ConfigureModel` hooks run last (O9).
    - ⚠️ A `[Reference]` **string** pointing at an `Id<T>`-keyed table gets the key converter but no FK: EF needs the
      same CLR type on both sides. The FK comes when the property becomes `Id<T>` (HR's `Person.Company` and others
      still are strings).
    - `RelationalSparkSession`: the unit of work over one `DbContext` in one transaction. The version moves over
      the aggregate (a row, child row or join row change moves the root's version, D3); `""` is insert-only; a
      store of a new instance over an existing row is a document PUT (the row is loaded, its values, children and
      joins replaced, and the instance aliased to it); deletes by id are unversioned or versioned; loads are
      case-insensitive (ids are lowered, D7); `LoadStartingWith` is an escaped `LIKE` with the model's identifiers
      and the pattern as a parameter; reference-list predicates (`Contains`, `Any`, `Count`, `!= null`) are
      rewritten onto the join table (`JoinRewriter`, as S2b proved); constraint errors become
      `SparkValidationException` naming the attribute and a duplicate key becomes concurrency (D30).
    - `RelationalSparkQueryProvider`: `In` and `Search` (a lower-cased `Contains` per word, until S10).
    - `MintPlayer.Spark.Relational.Tests`: `SqlServerStoreTests` and `PostgreSqlStoreTests`, 18/18 against SQL
      Server 2022 and PostgreSQL 18. Until S17 (M5) the server comes from `SPARK_TEST_SQLSERVER` /
      `SPARK_TEST_POSTGRESQL`; without them the tests skip.
    - Known gaps, for later slices: `DateTime.Kind` round trip, Npgsql's `DateTimeOffset` offset 0, PostgreSQL's
      NULLS FIRST (S18), `[Sortable]` child order (an ordinal column), arrays as child collections, querying inside
      JSON columns, enums stored as int, and the Integer kind (needs `ISparkIntegerIdSource`, M4b).
  - **M4b (2026-11-25):**
    - **The conformance suites run on SQL now** (M5's first part, brought forward): `SparkStoreConformanceTests` and
      `SparkAtomicValuesConformanceTests` are split from their RavenDB/InMemory subclasses
      (`*ConformanceProviders.cs`) and linked into `MintPlayer.Spark.Relational.Tests`. Their cases are
      `[SkippableFact]`, and a provider without a server returns no store, so every case skips instead of failing.
      Ids are compared case-insensitively (`Canon`, D7: SQL hands them back lowercase).
    - The S6 members on `RelationalSparkSession`: `SetExpiry`/`GetExpiry` (a `SparkExpiresAt` column, indexed, UTC;
      a PUT replaces it as on RavenDB), `StreamAsync` (untracked rows with versions; read whole before yielding,
      since one connection cannot read the join tables meanwhile), `Reserve`/`Release` (table `SparkReservations`,
      resolved in the save's transaction; a holder that is gone or deleted by the same save is taken over; a
      concurrent claim fails the insert or the row's version and becomes `SparkReservationTakenException`),
      `DeleteWhereAsync`/`UpdateWhereAsync` (`ExecuteDelete`/`ExecuteUpdate`, the version `+ 1`, top-level members
      only), and `PatchAppend` (the save locks the row, `UPDLOCK`/`FOR UPDATE` from the dialect, reads it again and
      appends; refused on a row the session changed itself).
    - The request budget (30) is enforced as on InMemory; a save, and the join-table reads of a load, count as one.
    - `ISparkAtomicValues` (table `SparkAtomicValues`), so core's lease works; `ISparkIntegerIdSource` as a HiLo of
      32 over an atomic value per collection; `ISparkDataProtectionKeyStore` (`SparkDataProtectionKeys`);
      `ISparkStoreExpiration` (a store save hook mirrors `IExpiring.ExpiresAtUtc` into `SparkExpiresAt`; the sweep
      deletes past rows table by table and logs a row another row still references).
    - Found by the suite: SQL Server's default `decimal(18,2)` rounded `1234.5678` (now `decimal(38,18)`); a string
      `x.Id == "ConfDocs/a"` did not lower its parameter (a lowering converter on whole-text keys); Npgsql refuses a
      non-UTC `DateTimeOffset` (written as UTC) and either kind of `DateTime` in the "wrong" column (ticks to
      `timestamp`). **`DateTime.Kind` does not survive a SQL column: open question O18.** The absent-field case (D16)
      skips on SQL until M4c's migrations.
  - **M4c (2026-11-25):**
    - **Framework schema (D24):** reservations, atomic values and the key ring move to schema `spark` (`spark.Reservations`,
      `spark.AtomicValues`, `spark.DataProtectionKeys`) with their own history `spark.__SparkFrameworkMigrations`.
      `SparkFrameworkDbContext` migrates them with hand-written migrations in the Relational package
      (`Migrations/Framework`, the first `20261125000001_SparkFramework0001`); `SparkDbContext` maps the same tables
      with `ExcludeFromMigrations`, so an app's migrations never contain them. A framework migration finds its key
      collation through the dialect registered for `MigrationBuilder.ActiveProvider`. With no snapshot, the framework
      context ignores EF's pending-changes warning; a test compares the migrated columns with the ones the framework
      model's own operations create instead.
    - **`RelationalSchemaMigrator`** (`ISparkSchemaMigrator`): `ApplyAsync` takes the dialect's guard
      (`sp_getapplock` in `master`, `pg_advisory_lock(hashtextextended(…))` in `postgres`, the key a parameter),
      creates the database, migrates the framework schema, refuses a model with changes no migration covers (naming
      `--spark-synchronize-model`), then applies the app's migrations (none: nothing to do). Pending migrations of a
      database that does not exist are all of them; the script is the framework's whole idempotent script plus the
      app's `from`/`to` range. PostgreSQL registers `AdvisoryLockHistoryRepository` (S19, until npgsql/efcore.pg#4163).
    - The app's migrations are in `SparkRelationalOptions.MigrationsAssembly`, by default the entry assembly, in
      EF's `__EFMigrationsHistory`.
    - **`RelationalSchema`**: `CreateFromModelAsync` (framework migrations, then the model's own operations: for test
      databases and prototypes; tests no longer call `EnsureCreated`) and `ApplyDifferencesAsync(target, from)`.
    - **`SparkMigrationsModelDiffer`** replaces EF's differ in `SparkDbContext` (pubternal; a test resolves it, so an EF
      constructor change fails the build: 11.0.0's has five parameters, EF `main` six). Found by the D16 case: EF adds
      a required collection column with no default, which PostgreSQL refuses for existing rows. The differ gives it
      the empty collection, as SQL (`'[]'` for JSON, `'{}'` for Npgsql's native `text[]`): a default value goes through
      the array converter and fails. The view handling joins it in M4d.
    - **D16 on SQL passes** on both databases: a V1 row, the V1 → V2 operations, the V2 read gives the CLR defaults.
    - **`MintPlayer.Spark.Relational.Design`** (O17): `RelationalSchemaScaffolder`, named by
      `[assembly: SparkSchemaScaffolder]`, which core finds by the `SparkSchemaDesignAssembly` name the provider
      registers. `--spark-synchronize-model` writes the model JSON, then the migration into
      `Migrations/<Provider>` of the content root (`--name` names it), and prints the operations and EF's warnings;
      without the package it says to reference it and exits 1. EF's design-time services come from public API plus
      the provider's attribute; `FreshSnapshotMigrationsAssembly` works around dotnet/efcore#36016, and a migration
      that creates a table the last snapshot has is refused. The package has O17's three settings
      (`DevelopmentDependency`, EF Design with `PrivateAssets="none"`, `buildTransitive` targets with
      `Publish="false"` and warning `SPARKDESIGN001` when it arrives through another project).
    - New contracts in Abstractions: `ISparkSchemaScaffolder`, `SparkScaffoldRequest`/`Result`,
      `SparkSchemaDesignAssembly`, `SparkSchemaScaffolderAttribute`.
    - Tests: `RelationalSchemaMigratorTests` (pending on a missing database, three concurrent instances migrate once,
      the refusal, the script applied twice, the framework migrations against the framework model) and
      `RelationalSchemaScaffolderTests` (no server: a dummy connection string). Relational suite 100/100 with both
      servers.
    - **Left for later slices:** D24's refusal of an attribute id split into drop + add (it needs the attribute ids
      as column annotations, so the snapshot keeps them: M4d, with the differ's view work), and the per-app
      pending-model-changes test (M9, with the first SQL app).
  - **M4e (2026-11-25): the change feed (D6).**
    - **The stamp:** every entity table has a `SparkChangeStamp` column the database moves on every insert and
      update. SQL Server: `rowversion`, read as a big-endian `ulong` (`NumberToBytesConverter<ulong>`). PostgreSQL:
      `xid8` with default `pg_current_xact_id()`, plus a `BEFORE INSERT OR UPDATE` trigger calling
      `spark.set_change_stamp()`. Synchronize's migrations get the trigger from `SparkMigrationsModelDiffer`, after a
      table with the column is created or the column is added (`SparkRelationalDialect.StampTriggerSql`). EF never
      writes the stamp, and it is no concurrency token: `Version` stays the only one (D3). A child or join row change
      moves the root's `Version`, so the root row is written and its stamp moves too (tested).
    - **Framework migration `SparkFramework0002`:** `spark.Tombstones` (Guid id, collection, document id, stamp,
      `DeletedAt`) and, on PostgreSQL, the trigger function. A session's delete, `DeleteWhereAsync` (now in a context
      of its own: ids first, then `filter AND key IN (…)` per 500, a tombstone each, one transaction) all write
      tombstones in the deleting transaction. Tombstones are kept a day (`TombstoneRetention`), trimmed hourly by the
      feed and by the expiration sweep.
    - **`RelationalSparkChangeFeed`:** `OnCommitted` from the store's post-commit event (each stored root with its new
      version, each deleted one with none). The watches poll (`SparkRelationalOptions.ChangeFeedPollInterval`, 1 s):
      read the dialect's horizon (`MIN_ACTIVE_ROWVERSION()` / `pg_snapshot_xmin(pg_current_snapshot())`); when it has
      not moved, nothing committed and the poll ends; otherwise read the watched tables' rows and the tombstones in
      `[cursor, horizon)` (id and version only) and advance. The horizon doubles as the probe, so no separate
      `@@DBTS`/`pg_current_xact_id()` read. A watch is confirmed once the feed has a cursor. A failed poll retries from
      the same cursor; the first success after it raises `Reconnected`. Registered with
      `SparkStoreCapabilities.ChangeNotifications` on both providers.
    - **Finding: PostgreSQL's horizon is the whole server's.** Transaction ids are cluster-wide, so an open
      transaction in *another database* on the same server holds every watch back. The relational tests run their
      classes in parallel, each in its own database, and a single poll after a write was not enough on PostgreSQL.
      Nothing is skipped; it is reported later, and a write from just before a watch can be reported once after it.
      The tests poll until the change arrives (bounded at 10 s). PRD D6's trap now names this.
    - Tests: `RelationalChangeFeedTests` on both servers: the stamp moves on update and on a child-row change;
      `OnCommitted` reports a store and a delete; a collection watch sees another store's write and delete; a document
      watch reports that document only; **a transaction that commits late is read once it has committed** (A
      inserts and stays open, B commits after it: not reported until A commits, then both); a set-based delete leaves
      one tombstone per row, and trimming removes it. Relational suite 134/134 with both servers.
  - **CI fix after M4d (2026-11-25):** CI had been red since M2 because the lower-case ids (D7) were verified only on
    the classes M2c touched. Real defects it found, each now compares ids ignoring case: CodeCoverage's forge
    disconnect (`ForgeEventsRecipient` ignored it), the commit-graph sidecar id regex (answered "unavailable"),
    `QueryExecutor.RestrictToIds` in memory (custom-query row actions answered 404), the breadcrumb resolver's id maps
    (a legacy-cased reference lost its label: MintPlayer's song page showed no artists), and the object file store
    (keys are now lower-cased; the reconciler moves legacy keys). Test expectations of legacy-cased ids became the
    loaded lower-case id. The E2E host's raw store now applies `ApplySparkConventions` (an `Id<T>` can't be stored
    otherwise). `SparkFrameworkSchema.Map` is now `AddTables`: the route guard reads any `Map(` call as a hand
    mapping. **`__DocumentId` is now `internal`, not `private`:** RavenDB looks for the identity member on the
    stored type, and reflection hides a base type's private member from a derived one, so MintPlayer's songs and
    artists (`Song : Subject`, the id declared on `Subject`) were stored under a generated id and loaded with an
    empty one: no 301, no breadcrumbs, "a row with no id". Pinned by
    `SparkIdConformanceTests.An_id_declared_on_a_base_type_round_trips` (red on RavenDB before the change).
    **Lesson:** a change of id casing needs the whole suite on CI, not the touched classes.
  - **M4d (2026-11-25):**
    - **Views (D32):** `RelationalViewRenderer` renders each `SparkView<TRow>`'s `Map` through EF's `ToQueryString`
      against the store's own `SparkDbContext`: `Set<T>()` is the table, `Load<T>(id)` a correlated
      `FirstOrDefault`, `IdOf(row)` the key. EF can't cast `Id<T>` to string (`EF.Property<string>` fails with "No
      coercion operator"), so the key is projected beside the row (`SparkViewRow<TRow, TKey>`), EF flattens it, and
      the row's string `Id` column gets the collection's key converter: it reads as the whole id. A `Map` that
      captures a variable becomes a parameter, which a view can't hold: the renderer refuses it. The row type is
      mapped `ToView(name)` with the SQL and the tables it reads as annotations (`Spark:ViewSql`, `Spark:ViewTables`)
      on the row entity, so the snapshot keeps them. The model is built twice (once to render, once with the SQL);
      the model cache key tells them apart.
    - **`SparkMigrationsModelDiffer`** drops a view first and creates it last when its SQL changed, when it appears or
      goes, or when any operation touches a table it reads (an altered column under a view fails on SQL Server).
      `SparkRelationalDialect.CreateViewSql`: SQL Server wraps it in `EXEC(N'…')`, because `CREATE VIEW` must start
      a batch and EF's idempotent script puts every statement in `IF … BEGIN … END`.
    - `SparkRelationalModel` refuses a row type that is a stored type, two views with one row type, duplicate view
      names and a view named like a table; a view that reads a type the app does not store is skipped and querying
      it names the reason. `Query<T, TIndex>()` and the query provider's `OpenIndex` read the view.
    - **D24's attribute-id guard:** every column carries its attribute's id (`Spark:AttributeId`, from the new
      `ISparkStoredTypes.AttributeId`), and the scaffolder refuses a migration that drops and re-adds a column of
      the same id. **Finding:** EF's differ already turns a drop + add of the same shape into a `RenameColumn` by
      itself, even for unrelated attributes; the guard catches a rename with a type change. A rename that keeps the
      data is open question O19.
    - **Generator gate split:** an application (`OutputKind` Console/Windows) with `[GenerateIndex]` and `SparkView`
      resolvable gets the row class and the view; the native index and `[FromIndex]` only when RavenDB.Client is
      referenced. Without RavenDB the view carries `[DefaultIndex]` (unless another type claims it) and
      `[Description]`. A class library still gets nothing.
    - Tests: `RelationalViewTests` (rows, `Load`, a join count, a null reference, find by whole id, `OpenIndex`, a
      changed view recreated, a view surviving a column alter, the idempotent block, the captured-variable refusal,
      the unmapped view), scaffolder tests for the view's place and snapshot and for the guard, and two generator
      gate tests. Relational 122/122 on both servers, SourceGenerators 742/742.
  - Order: the core pieces in `libs/` first (the struct, the model registry, the generator for `__DocumentId`, JSON,
    the route binder), then the feature libraries, then the apps.
  - It touches the three production apps (CodeCoverage, SeoTester, MintPlayer). RavenDB stays green at the end of the
    milestone, and D23's pass-through `__DocumentId` keeps the stored document ids unchanged (S-ID4: no re-key).
  - The S2b query-seam items (`Search` takes words; captured values) and the S5 view declarations are M3's
    (§4 item 3), not M2's.
- **In progress: M5** (§4 item 5).
  - **M5a (2026-11-25): the split test harness (S17, PRD D35).**
    - `MintPlayer.Spark.Testing` is neutral now: it references `MintPlayer.Spark` and `MintPlayer.Spark.InMemory`,
      no database client (the layering guard `ProviderNeutralCoreTests` covers it, and now also refuses
      `Raven.TestDriver`, `MintPlayer.Spark.RavenDB` and `MintPlayer.Spark.Relational`).
      - `ISparkTestDatabaseProvider` (`Name`, `CreateDatabaseAsync`) and `ISparkTestDatabase` (`UseProvider` inside
        `AddSpark`, `ConfigureServices` after it, `WaitForConsistencyAsync`, `DisposeAsync` drops the database).
      - `SparkTestDriver` (per case) and `SparkSharedDatabase` + `SparkSharedTestDriver` (per class) expose `Database`.
      - `SparkEndpointFactory` takes an `ISparkTestDatabase` and gained `SeedAsync(ISparkSession)` and
        `WaitForConsistencyAsync`. The InMemory provider is in the neutral package.
    - **Choosing the database (O16):** each provider package's build targets add
      `[assembly: SparkTestDatabase(typeof(…Provider))]`. One attribute decides; with several, `SPARK_TEST_DATABASE`
      picks one ignoring case. No attribute, several without the variable, an unknown name or a type that is no
      provider all fail, naming the candidates. CI sets `SPARK_TEST_DATABASE: RavenDB` on every test step (it is an
      input of the Spark.Tests task), and `npm run test:affected` defaults it the same way.
    - **`MintPlayer.Spark.Testing.RavenDB`** got the RavenDB code by `git mv`: the drivers became
      `RavenSparkTestDriver`, `RavenSparkSharedDatabase`, `RavenSparkSharedTestDriver` (same namespace, so no using
      changed), plus the embedded server, the licence, index deployment, the JSON fixtures and `RqlRecorder`.
      `store.AsSparkTestDatabase()` hands a raw store to a factory. A script renamed the 313 files that used them;
      the compiler's CS1503 positions placed the remaining 32 `.AsSparkTestDatabase()` calls.
    - **`MintPlayer.Spark.Testing.SqlServer`:** `SPARK_TEST_SQLSERVER`, then LocalDB on Windows, then Testcontainers
      (`mssql/server:2022-CU21`). **`.PostgreSql`:** `SPARK_TEST_POSTGRESQL`, then MysticMind.PostgresEmbed (MIT)
      as the named instance `spark-tests` (AttachIfRunning, KeepRunningAfterExit, in `%LOCALAPPDATA%\pgembed`).
      - **Finding:** the embedded server's default, an ephemeral instance per test process, left its nine
        `postgres.exe` running after the test host exited and wrote its instance into the test's bin folder. The
        named instance is started once per machine and attached to afterwards. Cold start 26 s, warm attach 13 s for
        the class.
      - Each test database is `spark_t_<12 hex>`, checked against that pattern before it is spliced into `DROP`. Its
        schema comes from the model through a replaced `ISparkSchemaMigrator`: the framework migrations, then the
        model's operations; a later host on the same database applies only its differences.
      - Per case (create, framework migrations, model schema, host boot, drop): SQL Server 0.77–1 s, PostgreSQL
        0.65–1 s; the first case pays the warm-up (SQL Server 17 s, PostgreSQL 4–5 s).
    - Tests: `NeutralHarnessTests` (CRUD through the endpoints, seeded rows listed by a query, a later host reads an
      earlier one's write) 3/3 on each of RavenDB, InMemory, SQL Server and PostgreSQL; `SparkTestDatabasesTests` 7/7.
    - The template's test project references `MintPlayer.Spark.Testing.RavenDB`; `features.json` lists it in core
      and the four relational packages as unreleased. Templates.Tests 23/23.
  - **M5b (2026-11-25): new store conformance cases, on all four providers.**
    - Cases: ids in any casing from a fresh session; `LoadStartingWith` with `_`, `%` and `[` taken literally; paging
      after an order; nulls first ascending and last descending (D4); `DateTimeOffset` compared and sorted by instant
      (D5, +02:00 against UTC); a wrong-rows guard over 14 query shapes; an absent nullable field read as null by
      `==`, `!=`, negation, sorting and `DeleteWhere` (O11).
    - Red first: PostgreSQL sorted nulls last. Fixed in its dialect (PRD D4, as built). RavenDB failed the absent
      case. Fixed by `RavenAbsentNullRewriter` (PRD "Unknown fields", as built), without a backfill.
    - **Finding, open as O20:** string equality and `StartsWith` ignore case on RavenDB and SQL Server, and respect
      it on PostgreSQL and InMemory (measured). The guard's data avoids case-only differences until it is decided.
    - Spark.Tests storage, absent-field and query classes 229/229 (RavenDB, InMemory); Relational.Tests 148/148.
  - **M5c (2026-11-25): the database behaviour tests (PRD D31, as built).** `SqlServerBehaviourTests` (13) and
    `PostgreSqlBehaviourTests` (12) on a shared `BehaviourDatabase` base, raw SQL against a throwaway database per
    class. 25/25 locally. Every row of the D31 table except O13 (moves to M6). CI runs them once M8 adds the service
    containers and the `postgres:17` leg.
- **In progress: M6, the Tier 1 and Tier 2 ports.**
  - **M6a (2026-11-25): `ISparkSession.OptimisticConcurrency` (PRD D37) and Settings.** Tier 1 sets RavenDB's
    `OptimisticConcurrencyMode.Writes` in about 15 places, so the neutral session got the switch (conformance 4/4 on
    each of the four providers). `MintPlayer.Spark.Settings` (settings, seeder, quotas) now runs on `ISparkStore` and
    drops `RavenDB.Client`; it declares `Setting` and `SparkQuotaUsage` as stored types (D36). Settings tests 25/25
    on RavenDB.
  - **M6b (2026-11-25): Boards, and Notifications' own reads and writes.** Boards drops `RavenDB.Client` (19/19).
    Notifications' actions, inbox, account hooks, visitor claim and interceptors run on `ISparkSession`; interceptors
    get the neutral session through `SparkInterceptorContext.GetSparkSession()`. `SparkNotifier` and `WebPushDelivery`
    still enqueue through messaging's `IMessageOutbox` (RavenDB session), so the package keeps its pin until M7
    (29/29).
  - **M6c (2026-11-25): `Query<T>(TimeSpan)` (PRD D38) and Organizations.** The per-query index wait, green on all
    four providers. Organizations (service, reader, row policy, interceptors, hooks, actions, current organization)
    runs on `ISparkStore`; the row policy uses `ids.Contains(x.OrganizationId)`, which RavenDB's session rewrites
    into `In()`. The package drops its RavenDB references. Organizations + Notifications 67/67 on RavenDB.
  - **Next in M6:** Apps (Patch → load-modify-save; webhooks wait for messaging, M7), templates and AllFeatures'
    generator, then Tier 2 (mail, mcp, cron, contributions, authorization user store, search, files) and O13.
  - **M7a (2026-11-25): `ISparkSubscriptions` (PRD D39, spikes S7 + S14).** Messaging comes before Apps and the rest
    of Notifications, which enqueue through it. The neutral subscription: RavenDB data subscriptions on RavenDB, a
    commit-stamp cursor plus a lease (`SparkCursorSubscriptions`) on InMemory, SQL Server and PostgreSQL. The
    conformance suite is green on all four (25 cases). Found on the way: RavenDB's create-or-update races between two
    runners; the create is now retried.
  - **M7b (2026-11-25): messaging and the subscription worker on any store (PRD D40, D41).**
    - **Messaging drops `RavenDB.Client`.** It needs only the Subscriptions capability. Each RavenDB mechanism has a
      neutral replacement:
      - the feeder's `@last-modified` sequence becomes `AssignSequences`
      - its two static indexes become plain queries
      - the sweeper's patch scripts become versioned stores
      - the lease's compare-exchange value becomes an atomic value under a new key
      - the outbox takes `ISparkSession`
    - **Relational stores check a loaded row's version on every UPDATE.** So the handler session, `MessageSession`,
      retries a save that conflicted only with its own claim renewal.
    - **`ISparkStoreReadiness` (D41).** On a fresh database, hosted services started before `UseSpark` had migrated.
      Messaging now waits for the schema.
    - **The worker and its callers.** `SparkSubscriptionWorker<T>` runs on `ISparkSubscriptions`; `RetryNumerator` moves
      to Replication. Callers still on a raw RavenDB session bridge with `AsSparkSession()`: Apps, Notifications,
      MintPlayer, CodeCoverage and SeoTester. The bridge is behaviour-neutral on RavenDB.
    - **Evidence.**
      - `NeutralMessagingTests` 3/3 on all four databases
      - RavenDB messaging, subscription worker and replication 265/265
      - subscription conformance 15/15 on SQL Server + PostgreSQL
      - CodeCoverage 286/286
    - **CI fix.** `SparkStoreRequirementTests` assumed InMemory lacks Subscriptions, which M7a changed; it now uses
      Revisions.
  - **Next:** Notifications' `SparkNotifier`/`WebPushDelivery` and Apps off the `AsSparkSession()` bridge and their
    RavenDB pins; then the rest of M6 (templates, Tier 2); cron, the subscription worker's remaining hosted services
    and others adopt `ISparkStoreReadiness` as they are ported.
- **Local spike servers left running:**
  - SQL Server: `SparkSpike536` on `localhost` and `localhost\MSSQLSERVER01`
  - Docker `spark536-pg` (`postgres:18`, 127.0.0.1:55432). M4 needs it again.
  - The embedded PostgreSQL instance `spark-tests` (M5a), started by the PostgreSQL test package when
    `SPARK_TEST_POSTGRESQL` is unset. It keeps running on purpose.
  - Docker `spark536-maria` (`mariadb:11.8`, 127.0.0.1:53306) is no longer needed: `docker rm -f spark536-maria`.

  The PostgreSQL password is in `docs/relational_spike/Program.cs`. The spikes read it from `SPARK536_PG_PASSWORD`.

## 2. Spikes

Each spike ends with an entry in PRD §8, with evidence (numbers, file:line, a red→green test).

| # | Question | Done when | Status |
|---|---|---|---|
| S1 | Mapping: does the hybrid document table hold? (SQL Server) | DemoApp's and HR's models stored on 2022 and 2025 | ✅ PRD §8.1 (history: D1 reversed) |
| S2 | Query translation on SQL Server: the filters, sort, `In`, `AnyContains`, D16, `DateTimeOffset`, with no client evaluation | the shapes produce SQL | ✅ PRD §8.1 |
| S12 | S1+S2 on **PostgreSQL 18** (Npgsql 11) | the same 18 shapes | ✅ PRD §8.2: 18/18 |
| S13 | S1+S2 on **MariaDB 11.8** (Microting 11) | the same 18 shapes | ✅ PRD §8.2: 12/18, 5 silently wrong → **MariaDB deferred** |
| **S-ID1** | **`Id<T>` on RavenDB** (D23 rule 6–7): <ul><li>the hidden pass-through `__DocumentId` (can it be non-public?) on load, query, streaming, subscriptions and lazy</li><li>query option (a): is a body field named `Id` treated specially by RQL or auto-indexes; is `RegisterQueryValueConverter` applied to `==`, each `Contains`/`In` element, `OrderBy` and projections; does the identity win over the body field on load; documents written outside the framework lack the field</li><li>then (b) the rewrite decorator, and (c) only if both fail</li><li>a re-save byte-diff on a production copy</li></ul> | DemoApp's `Person` round-trips with `Id<Person>`; author-written `Where(x => x.Id == v)` and `ids.Contains(x.Id)` return the right rows | ✅ PRD §8.3: (b) the rewrite decorator wins, (a) rejected; production-copy byte-diff moves to S-ID4 |
| **S-ID2** | **`Id<T>` on EF Core 11**, SQL Server + PostgreSQL + InMemory. Unverified so far (research from memory, 2026-11-24): <ul><li>converters on `Id<T>` as PK and FK</li><li>`UseHiLo` on a converted key: two inserts in one `SaveChanges`</li><li>`Contains` over converted values with 0/1/50/2000 ids (OPENJSON / `= ANY`; read the SQL)</li><li>a join entity and a child-table FK</li><li>the Guid key column: `uniqueidentifier`/`uuid` against the lowercase string, and v4 against v7 insert cost over 100k rows (PRD D23 rule 2: v4 stays unless the difference is large)</li></ul> | all shapes run as SQL on both databases | ✅ PRD §8.4: 25/25 on SQL Server 2025, 2022, PostgreSQL 18 and InMemory; v4 stays; `Id<T>` gets `IComparable` |
| **S-ID5** | **Id conventions under the lowercase rule** (PRD D7), RavenDB 7: <ul><li>`ApplySparkConventions` with lowercased generated, natural and HiLo ids (node tag `-a`)</li><li>`TransformTypeCollectionNameToDocumentIdPrefix` lowered, read by `RoleStore` and HiLo</li><li>the session identity map when the same document is loaded as `Accounts/X` and `accounts/x`</li><li>the debug guard refusing a non-lowercase id on store</li><li>the collection-collision build failure (two types, names equal ignoring case)</li></ul> | every generated id is lowercase; one entity per document in the session; the collision fails the model build | ✅ RavenDB half, PRD §8.3 (generated, natural, HiLo lowered; one instance per document; the guard checks new documents only). The collision failure is tested in M3 |
| **S-ID4** | **Mixed-case legacy ids under the lowercase rule** (O15), on a copy of the production `Coverage` database (never production itself): <ul><li>`LoadAsync`/batch load, `LoadStartingWithAsync` and an index equality query with lowercase ids against `Accounts/X`-style documents</li><li>the session identity map with a lowered `__DocumentId`, then `SaveChanges` (no duplicate, no casing change written)</li><li>Lucene and Corax reference-field equality ignoring case</li><li>only if (a) fails: a case-only re-key (delete + put in one batch) with attachments, and what the subscriptions see</li></ul> | (a) works without writing to any document, or (b) re-keys with every attachment kept | ✅ PRD §8.5: 15/15 on a copy of the development `Coverage` database; no re-key. A rerun on a production export is optional (owner) |
| **S2b** | **The real `QueryExecutor` end to end** on an EF model generated from arbitrary Spark entities: TPH, JSON columns for `TranslatedString` (EF 10's `ToJson()` complex types; a `Dictionary` as a JSON column is unverified), child tables, join tables | the row filter (:1055), `In` (:664) and search pushdown (:2155) run as SQL on both databases | ✅ PRD §8.10: 14/14 on both, `QueryExecutor` unmodified; `TranslatedString` is a converter JSON column; O13's stored get-only member works. For M2: `Search` takes words, not RavenDB's `*word*`; the executor composes captured values, not constants (literals today). For M3: `AsSplitQuery` for types with several AsDetail collections |
| **S3** | **Schema:** the model synchronize generates EF migration files (D24) from a model built from metadata, through the public design-time services (`IMigrationsScaffolder`, `IMigrationsModelDiffer`, via `DesignTimeServicesBuilder`; never construct `MigrationsScaffolderDependencies`) | add a field, add a queryable field, add an AsDetail collection, add a reference, on both | ✅ PRD §8.8: all six versions (plus a rename and a no-change run) scaffold the expected operations and migrate on both, from public API only. the EF 11 snapshot trap is worked around by `FreshSnapshotMigrationsAssembly` (dotnet/efcore#39784 upstream); queryable strings need a length; Design's 17 MB → O17, decided (§8.9: a development-dependency package, out of every publish) |
| **S4** | **Concurrency and keys:** the `bigint` version per table (D3); `""` = insert-only; the D14 etag on the wire; the typed contract (D27); lowercase ids and an escaped `LoadStartingWith` (D7); constraint errors become validation messages (D30) | the conformance tests for absent and stale versions pass, plus a natural-key case with `_` and `%`, plus a unique and an FK violation | ✅ PRD §8.11: 15 cases green on both, through the session and Spark's own `IDatabaseAccess`. The version covers the aggregate (child and join rows move it: D3 refined); a duplicate PK is concurrency, a unique index validation; SQL Server's errors are localized (names matched against the model, tested in German); 2628 carries the value |
| **S5** | Neutral views: one declaration, rendered by RavenDB as an index, by relational databases as an SQL view or indexes, and by InMemory as LINQ | DemoApp's `VPerson` (computed `FullName`) and one view with a join, from one declaration on InMemory + both databases; synchronize writes the EF-translated `CREATE VIEW` into the migration (PRD D32) | ✅ PRD §8.12: 10 cases green on both (64/64 with S2b and S4). <ul><li>One declaration renders four ways: SQL Server, PostgreSQL, RavenDB (`LoadDocument`) and InMemory.</li><li>The join form is `Load<T>(id)`.</li><li>`QueryExecutor` queries the view by name, unmodified.</li><li>Spark's differ (subclassing the pubternal one) writes `CREATE VIEW`/`DROP VIEW`, and recreates a view around a change to a table it reads (PostgreSQL refuses the `ALTER` otherwise).</li></ul> |
| S6 | Neutral contracts: <ul><li>`SetExpiry`</li><li>`ISparkStoreLock`</li><li>`ISparkAtomicValues`</li><li>`StreamAsync`</li><li>`Reserve/Release` (in the `SaveChanges` transaction)</li><li>`UpdateWhereAsync`/`DeleteWhereAsync`</li><li>`PatchAppend`</li></ul>(design-principles: deep modules, one contract per concern) | settings, mail (expiry) and cron (atomic value) green on all providers | after the gates |
| S7 + S14 | **Subscriptions:** <ul><li>a per-subscription cursor over the `ChangeStamp`, below the safe horizon (D6; where the stamp lives is O10)</li><li>tombstones for deletes</li><li>a neutral predicate instead of RQL</li><li>a lease replacing `WaitForFree`</li></ul> | <ul><li>two interleaved transactions never skip a row (the D6 horizon)</li><li>a new subscription sees the backlog</li><li>a message survives a host restart</li><li>two hosts never process one message</li><li>subscription_worker's tests on both</li></ul> | after S6 |
| S8 | Revisions (O12): written by **every** write path, including `UpdateWhere`/`PatchAppend` and sweepers; revision id = `Version`; per-collection retention and `PurgeOnDelete` | history's tests on both; a soft-delete restore; a set-based update leaves a revision | after S6 and O12 |
| S9 | Dashboards: a samples table, a rollup job and retention; RQL aggregation as LINQ (in scope, O2) | dashboards' tests on both | after S6 |
| S10 | Full-text: **`LIKE` first** (the commercial-framework comparison); an FTS dialect (SQL Server FTS, PostgreSQL `tsvector`) is an opt-in later | search's tests on both with `LIKE` | later, off the critical path |
| S11 | Attachments (low priority) | in or out | later |
| S15 | `ISparkAtomicValues` and leases: a table with compare-and-swap, against advisory locks under connection pooling and loss | <ul><li>cron: one occurrence fires once across two hosts, and poisoned values are reclaimed</li><li>migrations: one runner, and takeover after a killed host</li></ul> | with S6 |
| S16 | Aggregate tables for moderation's 3 map-reduce indexes: updated in the same transaction from the loaded old row; set-based writes refused on aggregated collections | moderation's reputation tests on both | with Tier 3 |
| S17 | **The split test harness** (PRD D35): <ul><li>`ISparkTestDatabaseProvider` in a neutral `MintPlayer.Spark.Testing`; `.RavenDB` keeps `RavenTestDriver`; `.SqlServer` finds a server from a connection string, then LocalDB on Windows, then Testcontainers</li><li>`.PostgreSql` defaults to `MysticMind.PostgresEmbed` (no Docker, like `RavenTestDriver`): PostgreSQL 18 binaries available from zonky, the cold start including the first download, cleanup of processes and data folders on Windows, and its licence (permissive only)</li><li>the cost of a database per class: create + migrate on SQL Server; a `TEMPLATE` clone on PostgreSQL; against a Respawn reset of a shared database</li><li>the provider choice (O16, decided): each provider testing package's `build/*.targets` generates `[assembly: SparkTestDatabase(…)]`; with several, `SPARK_TEST_DATABASE` picks one; several without the variable, an unreferenced provider in it, or none at all fails at startup</li></ul> | one Spark.Tests class runs on RavenDB, InMemory, SQL Server and PostgreSQL without a line of provider code; the per-class cost is measured on both SQL databases | with M4 |
| S18 | Null ordering (D4), with RavenDB's own order measured | a conformance case green on all four providers | with M4 |
| **S19** | **Applying migrations** (PRD D33): <ul><li>EF's migration lock with two instances starting together, on SQL Server and PostgreSQL</li><li>the order framework schema → app migrations → data migrations, and a failure halfway (EF 10: a transaction per migration)</li><li>`--spark-migration-script` writing the framework and app migrations as one idempotent script, run twice</li><li>`ApplyMigrations=false` refusing to start on a pending migration</li></ul> | one instance migrates and the other waits; the script applies once and is a no-op the second time | ✅ PRD §8.7: EF's lock is not enough (Npgsql releases it per migration; neither covers `CREATE DATABASE`), so D33 adds Spark's session guard in `master`/`postgres`; it passes on both. The Npgsql race is worked around by `AdvisoryLockHistoryRepository` (1d, npgsql/efcore.pg#4163 upstream). Failure halfway, the script and the pending check pass |
| **S20** | **Commands as keyed DI registrations** (PRD D34): `GetRequiredKeyedService<ISparkCommand>(flag)` after `Build()`; `--spark-synchronize-model` moved after `Build()` constructs no document store or `DbContext` and opens no connection (no database reachable); `--spark-help` lists the keys | synchronize runs with no database; only the requested command is constructed | ✅ PRD §8.6: 12/12; help reads the descriptors; last registration wins; core commands take only contracts with a core default |

**Gates:** S-ID1, S-ID2, S-ID4, S-ID5, S2b, S3, S4, S5, S19 and S20 gate the provider work (M3). S6–S9 and S14–S16 can run in parallel
after them. ~~S13b~~ was dropped with MariaDB (O6), and S-ID3 (a compare-exchange HiLo) was dropped by the round 4
consensus.

## 3. Grilling rounds

### Round 1: an adversarial review of the design (2026-11-24)

A reviewer agent read the PRD and the code cold and tried to break D1–D6 and the equivalents table. **The
architecture (D1, D1a, D2, D3) held**, as did D4 and D5, the S13 findings, the `SKIP LOCKED`/`READPAST` claims and
PostgreSQL 18 virtual columns. The findings, each checked against the code before being accepted:

| # | Sev | Finding | Change |
|---|---|---|---|
| 1 | blocker | A cursor on a database sequence skips rows: a value is allocated before commit, and commits land out of order. | D6 rewritten: a commit-ordered `ChangeStamp` with a safe read horizon (`MIN_ACTIVE_ROWVERSION()`, `pg_snapshot_xmin`). S14 tests interleaved transactions. |
| 2 | blocker | Cron is not a lease. `TryClaimOccurrenceAsync` is a never-expiring compare-and-swap of "latest claimed occurrence" with poison handling (`SparkCronScheduler.cs:236-274`, **verified**). A lease would let a second host fire the same occurrence. | A neutral `ISparkAtomicValues` (the compare-exchange equivalent) with leases built on it; cron uses it directly. S15 extended. |
| 3 | important | Ids are case-insensitive on InMemory and RavenDB (`InMemoryDatabase.cs:12/:43-44`, **verified**); PostgreSQL `text` isn't. `LIKE 'prefix%'` mis-handles `_` and `%`. | D7: a lower-cased `IdKey` primary key, `LIKE … ESCAPE`. S4 extended. (Revised by D23.) |
| 4 | important | Revisions written "by the session at commit" miss set-based writes and sweepers. | Every write path writes revisions; revision id = `Version`. S8 extended. |
| 5 | important | Messaging's `Advanced.Patch` list appends are concurrency-safe; load-modify-save would lose updates or 409. | A neutral `PatchAppend`. |
| 6 | important | A change-only outbox gives a new subscription no backlog; RavenDB subscriptions deliver current state. | The cursor runs over the `ChangeStamp`, plus tombstones for deletes. |
| 7 | important | Map-reduce aggregate tables need the old row; unloaded `Delete`/`UpdateWhere` break `Sum + @d`. | The hook loads the old row in the transaction; set-based writes are refused on aggregated collections. |
| 8 | important | "Tier 0 works already" ignored views (`Query<T,TIndex>`), which need S5, yet S5 sat in M6. | S5 is now a gate; Tier 0 reworded. |
| 9–11 | minor | `Reserve` must join `SaveChanges` (already covered); D3's `xmin` reason was half a reason; the `Sequence` wording was ambiguous for deletes. | Reworded. |

### Round 2: the owner (2026-11-24)

| Question | Answer |
|---|---|
| O1: one pull request, or split? | **One PR** |
| O6: MariaDB (no first-party EF 11 provider; the fork gave 5/18 wrong rows) | **Drop MariaDB for now** |
| O2: Tier 4 (identity_provider, dashboards) | **Port both** |
| O3: demo app | **A new `apps/SqlDemo`** |

### Round 3: the owner (2026-11-24)

| Question | Answer |
|---|---|
| Storage model | **A table per entity with EF Core** (D1 reversed); the collection is the TPH root table; core value types are JSON columns |
| Schema changes | **The model synchronize generates EF migration files** (D24) |
| AsDetail | **Relational child tables** (D25) |
| References | **Real FKs** (D26). Reference integrity on every provider is a separate issue with its own investigation |
| Untyped contract members | **Typed `<T>`** (D27, about 89 call sites) |
| Compatibility | **Breaking changes allowed, no backward compatibility yet** |
| Ids | Typed; the full string on RavenDB; int or Guid per entity on relational databases; **one compiled library per feature** (so a generated `global using` alias per provider was rejected). The design went to round 4. |

### Round 4: the id model, moderated across six LLMs (2026-11-24)

The owner had the design debated in a browser with ChatGPT, Claude and Gemini (rounds 1–3), then Grok, Copilot and
Mistral (challenge, rounds 4–5). DeepSeek answered only round 1, because its account was suspended. All six ended
with "AGREE WITH CONSENSUS", and the owner accepted it ("probably good"): PRD D23.

Facts checked locally during the rounds:
- Raven.Client 7.2.7 has **no** `RegisterIdConvention`, `IdentityTypeConvertors` or `DelegateTypeConverter`.
  DeepSeek's and Mistral's first designs relied on them.
- It does have `FindIdentityProperty`, `RegisterQueryValueConverter`, `OnAfterConversionToEntity` and
  `GenerateNextIdForAsync`.

What changed between rounds:

| Topic | After round 3 | Final (rounds 4–5) | Why |
|---|---|---|---|
| Payload | a kind tag + long/Guid/string | **the canonical string only**; kind is model metadata | simpler; the wire format is the domain format (Grok, Copilot; all accepted) |
| RavenDB int ids | a framework HiLo over compare-exchange | **RavenDB's native HiLo**, `"people/1-A"` opaque | the owner's requirement said "RavenDB's HiLo"; one allocator less |
| RavenDB sync | store events | **a pass-through property** | follows from the string payload (Claude) |
| `x.Id` queries | an execution-time rewrite | **spike order: body field → rewrite → helpers** | the rewrite is "a LINQ provider shim forever" (Copilot) |
| ValueComparer | always | **only when a test needs one** | an immutable struct over a string compares correctly |
| Migration between providers | a framework ETL that re-keys | **not framework work**: the developer writes a one-shot migrator | owner, 2026-11-24 |

### Comparison with a commercial framework (2026-11-24)

Three read-only investigators compared a commercial .NET framework that ships RavenDB, SQL Server and PostgreSQL
providers. Its code is proprietary and is not in this repository. Where it and Spark differ:

| Topic | Commercial framework | Spark |
|---|---|---|
| Ids | the native CLR key per provider; an untyped `ObjectId` string on the wire, split on `;` for composite keys | `Id<T>`, provider-neutral, so one compiled library runs everywhere |
| SQL int ids | IDENTITY, known after save | HiLo, known before save |
| SQL storage | row per table, but the app writes the `DbContext` | row per table, model generated by the framework (D1, D24) |
| Schema | the app owns it; the framework's own tables use versioned scripts | migrations generated; framework tables in their own schema |
| Concurrency on SQL | none | `Version bigint` (D3) |
| Live updates | caches only; SQL polls a cache table | a commit-ordered change stamp (D6, O10) |
| Revisions | none (the request log only) | open (O12) |

The owner accepted four changes from it:
1. framework id lookups through a provider hook (D23 rule 7)
2. constraint errors become validation messages (D30); FK scalars never surface twice (D26)
3. `LIKE`-first search, with FTS later (S10)
4. no composite keys and no id obfuscation

### Rounds 6–9: the consensus checked against the comparison (2026-11-24)

The six conversations saw the comparison, the accepted changes and the O14 inventory. DeepSeek was still suspended;
Mistral hit its message limit after round 7.
- **Round 6.** Everyone accepted the comparison's changes. Untyped native keys solve a different problem: they give up
  one compiled library per feature. O14 split three ways:
  - a string primary key only (Grok, Copilot, ChatGPT, Gemini)
  - plus an explicit parent FK column (Claude)
  - a hidden surrogate key, only where an embedded key is an int/Guid entity key (Mistral)
- **Round 7, a merged proposal (M1–M6):**
  - a string primary key
  - an explicit `Id<Parent>` property wherever a segment is an entity id, checked against the id template
  - binary key columns
  - a batch id-lookup hook
  - the migrator's duties written down
  - the parsers replaced by the property

  Four agreed. Mistral and Gemini objected that a binary column contradicts D7's case-insensitive ids.
- **Round 8.** Ids became case-exact in canonical form, with FK columns copying the key's collation. Claude found the
  last gap: two natural ids that differ only in case are one document on RavenDB but two rows on SQL.
- **Round 9.** Natural ids are unique ignoring case on every provider: a lower-cased unique index on SQL, and a
  "must not exist" insert on RavenDB. ChatGPT, Copilot, Grok, Claude and Gemini agreed. Their notes are in PRD D7: the
  RavenDB check is atomic, and the conformance test needs non-ASCII case variants.

Result: PRD D7 and D23 rules 7, 11 and 12. O14 waits for the owner.

### Round 10: every id lower case (2026-11-24)

The owner proposed that Spark lowercases every id when it creates, parses or reads one, so `GetById("ABC")` finds
`abc` on every provider. Round 10 put it to the six conversations, together with a collection-collision guard (L1)
and the choice for existing production ids (L2). ChatGPT, Claude, Gemini, Grok and Copilot agreed with the lowercase
rule and L1. Mistral hit its message limit again; DeepSeek is still suspended.

- The lowercase rule replaces round 9's lower-cased unique index and the per-entity normalisers (PRD D7).
- L1: building the model fails when two entity types map to collection names that are equal ignoring case.
- L2: all five chose (a), no re-key. Claude added that a reference-field patch re-triggers the subscriptions just as
  a re-key would, and that `LoadStartingWithAsync` must be shown to ignore case on mixed-case ids. The owner had
  proposed (b), then followed the recommendation: O15 is decided as (a), gated by spike S-ID4. If S-ID4 fails, O15
  reopens with a case-only re-key spiked on the same database copy.

The owner then asked why the parent id can't keep being extracted from the nested id. It can, as long as the parse is
not written by hand: `Commit.TryParseRepository` compares `"Commits"` case-sensitively and would fail closed under the
lowercase rule, as it did once when the forge segment was added. O14 is decided as the LLMs' consensus with the code
source-generated from one id template per entity (PRD D23 rule 12).

### Grilling round 5 (owner, 2026-11-24)

- **O10:** a stamp column on every entity table (`rowversion`; `xid8` by trigger on PostgreSQL) plus a framework
  `Tombstones` table. A new subscription's backlog is the table itself (PRD D6).
- **O11:** D16 everywhere, extended to nullable members: RavenDB rewrites `== null` to include absent fields; SQL
  adds columns with the CLR default. No backfill. Sorting and distinct values are conformance cases.
- **O12:** a framework `Revisions` table with whole-aggregate JSON snapshots, opt-in per type as today. The owner
  left its presence to me: always present in the provider's framework schema.
- **O13:** stored get-only properties, recomputed on save, through a generated backing field and
  `PreferFieldDuringConstruction` (the owner suggested source generation). S2b proves it.

### Still open

None. O8 and O9 were decided on 2026-11-24 (owner, PRD §10):
- **O8:** PostgreSQL 17 and later. CI runs on 18, and the D31 behaviour tests run on 17 too.
- **O9:** a public `ConfigureModel(ModelBuilder)` hook on `UseSqlServer`/`UsePostgreSql`, in M4.

## 4. Milestones

1. ✅ **Gate spikes S-ID1, S-ID2, S-ID4, S-ID5, S2b, S3, S4, S5, S19, S20.** The PRD updated with their evidence.
   Done 2026-11-24 (PRD §8.3–8.12).
2. **`Id<T>` and the typed contract on the existing providers** (D23, D27): RavenDB and InMemory.
   - the struct, the model registry (`ids.NewAsync/Parse`), the generator for `__DocumentId`, the JSON setup and the
     route binder
   - every entity's `Id` and reference fields in core, the feature libraries and the apps
   - breaking, with no shims
   - RavenDB stays green at the end of the milestone
   - **Size (inventory, 2026-11-24, grep counts, tests excluded):**
     - **Large:**
       - about 105 entity classes with a `string Id` (42 libs, 63 apps)
       - about 235 `*Id`/`*Ids` string properties (151 libs, 84 apps), some of them external ids that stay strings
         (GitHub, OIDC client ids), and 49 `[Reference]` attributes
       - about 75 hierarchical id builders plus 31 `LoadStartingWithAsync` uses (O14)
       - the session's untyped members across 53 files
     - **Medium:** generators and analyzers:
       - `ValueObjectKeyGenerator.Producer.cs:69`
       - `GenerateIndexGenerator.Producer.cs:238`
       - `MissingReferenceRootAnalyzer.cs:145`
       - `SparkModelSymbols.cs:96-105`
       - `SearchIndexGenerator.cs:124/166`
     - **Small:** Angular. ng-spark passes ids through `encodeURIComponent` and never parses them.
3. ✅ **Neutral contract additions** (S6, S15), implemented on RavenDB and InMemory first, with no behaviour change on
   RavenDB. Also the core clean-ups from PRD §2.3:
   - the `SparkDataProtectionStorage` rename
   - the `SparkStoreRequirement` default hint
   - the RavenDB wording in messages
   - the query seam (S2b, PRD §8.10): `ISparkQueryProvider.Search` takes the words, and RavenDB adds its own `*`;
     `QueryExecutor` composes filter values as captured fields, not `Expression.Constant` (EF writes constants as
     literals, one query plan per value)
   - the generators' neutral declarations, as S5 proved them (PRD D32, §8.12): one `MintPlayer.Spark.SourceGenerators`
     package that emits `SparkView<TRow>` declarations, with `Set<T>()` and `Load<T>(id)` and no `join`. It renders
     the RavenDB index or the EF view mapping depending on the referenced provider. Also in core:
     - the InMemory renderer (LINQ to objects, null-safe through `Load`), so `InMemorySparkQueryProvider.OpenIndex`
       stops throwing
     - the generator emits literals only; synchronize refuses view SQL that contains a parameter
   - IoC (PRD D34): `ISparkCommand` as keyed DI registrations (no registry class), with the five existing commands
     moved onto it and every app's `Program.cs` (and the template's) reduced to one command line after `Build()`; `ISparkSchemaMigrator` with the RavenDB
     implementation; `--spark-migrate` and `--spark-migration-script` in core; the data-migration runner on the
     neutral lease; a layering test against provider references in core
4. ✅ **`MintPlayer.Spark.Relational`, `MintPlayer.Spark.SqlServer`, `MintPlayer.Spark.PostgreSql`** (D2). Done
   2026-11-25 (§1 M4a–M4e):
   - `UseSqlServer()`, `UsePostgreSql()` and their `SparkStoreProviders` constants, with the public
     `ConfigureModel(ModelBuilder)` hook (O9), which runs last in `OnModelCreating`
   - the EF model built from the Spark model, the migration generator (D24), child and join tables (D25, D26): the
     join-list rewrite and load step proven in S2b, and `AsSplitQuery` for types with several AsDetail collections
   - the aggregate version (S4): child and join row changes move the root's version; `GetVersionsAsync` answers
     tracked rows from the tracker; unversioned deletes by id through `ExecuteDeleteAsync`
   - applying migrations (D33): at startup by default (framework schema, app migrations, then the data migrations),
     `--spark-migrate`, `--spark-migration-script`, the `ApplyMigrations=false` startup check, and the per-app
     pending-model-changes test
   - the store, session, query provider, change feed, expiration, the data-protection key store, atomic values,
     leases and reservations
   - views (D32, S5):
     - `ToView` mapping plus EF's translated SQL in the `Spark:ViewSql`/`Spark:ViewTables` annotations, normalized
       to `\n`
     - Spark's differ, a subclass of the pubternal `MigrationsModelDiffer` that overrides both `GetDifferences` and
       `HasDifferences`. It drops a view first and creates it last whenever its SQL or a table it reads changes. A
       test constructs the differ, so an EF minor that changes its constructor fails the build, not production
     - the schema in tests comes from the migration operations, never `EnsureCreated` (which skips views)
   - constraint-error translation (D30), as S4 proved it: the constraint name matched against EF's model, never the
     message's wording (SQL Server localizes it); a duplicate PK is concurrency; a length error located by value
5. **Conformance:** the two relational subclasses, plus new cases on all four providers:
   - row security, `DateTimeOffset`, paging and null ordering
   - `Id<T>` round trips, lowercase ids (an upper-case `GetById` is found everywhere) and escaped prefixes
   - expiry, atomic values, leases, reservations and streaming
   - **a wrong-rows guard for every query shape**
   - the absent-field rewrite for nullable members, and absent-field sorting and distinct values (O11)
   - **the database behaviour tests (PRD D31):** `SqlServerBehaviourTests` and `PostgreSqlBehaviourTests`, one test per
     assumption, named after the decision it protects. Each one is written when its milestone first relies on it,
     not all at once. ✅ M5c (§1), O13 in M6.

   This comes with the split test harness (S17, PRD D35): `MintPlayer.Spark.Testing` becomes neutral, today's
   RavenDB code moves to `MintPlayer.Spark.Testing.RavenDB`, and `.SqlServer`/`.PostgreSql` join. ✅ M5a (§1).
6. **Tier 1 and Tier 2 ports** (PRD §5), plus the lookup lost-update fix.
7. **Tier 3:** S7/S14 subscriptions and messaging, S8 revisions, S16 moderation. Then search with `LIKE` (S10).
8. **Tier 4:** identity_provider, and dashboards (S9).
9. **Demo, CI, docs:**
   - **`apps/SqlDemo`**, using the ported libraries, with E2E coverage
   - CI: pinned `mcr.microsoft.com/mssql/server:2025-*` and `postgres:18` service containers, plus `postgres:17` for the
     D31 behaviour tests only (O8). They join existing job
     groups (the 20-concurrent-job limit, `.github/workflows/test.yml`). The relational conformance classes are
     sharded like the rest (`tools/test-shard-weights.json`).
   - guides
   - mark the PR ready

   The new packages target `net11.0`, so their first versions are 11.x. The engine assigns them; never write a
   version by hand.

RavenDB stays green at every commit (one PR, so no milestone may break the existing provider). Tests: single classes
during development (TDD red→green). The full suite runs once at the end, via push → PR → CI.

## 5. Log

- 2026-11-09: first draft (SQL Server only).
- 2026-11-19: investigation rewrite, spikes S1/S2, D1a. Paused by the owner.
- 2026-11-24: resumed with a wider scope (SQL Server, PostgreSQL, MariaDB). Rebased onto master.
  - Three investigators: the core and light libraries; the semantics the heavy libraries rely on; the relational
    equivalents and the EF provider landscape.
  - Spikes S12/S13.
  - The PRD was renamed from `sql_server_provider_PRD.md` and rewritten, and this plan was split out of it.
  - Grilling round 1 (adversarial review): D6 rewritten, D7 added, five equivalents corrected.
  - Grilling round 2 (owner): one PR, MariaDB deferred, Tier 4 in, `apps/SqlDemo`.
  - Grilling round 3 (owner): D1 reversed to a table per entity on EF Core; D24–D27; breaking changes allowed.
  - Round 4: the id model `Id<T>` (D23), a consensus of six LLMs moderated in the browser.
  - The comparison with a commercial framework: D30, `LIKE`-first search, id lookups through a provider hook.
  - Spike S-ID1 + S-ID5 (RavenDB half), PRD §8.3: 46 of 51 checks pass, and the 5 failures are expected negatives.
    D23 rule 7 is (b), the rewrite decorator: `x.Id` becomes `id()`. Option (a)'s body field misses every
    document written today. The public `Id<T>` is `[JsonIgnore]`, and a re-save is byte-identical. HiLo comes from
    Spark's own generator, and the debug guard checks new documents only.
  - Spike S-ID2, PRD §8.4: `Id<T>` on EF Core 11, 25/25 on SQL Server 2025, 2022, PostgreSQL 18 and InMemory.
    `Id<T>` needs `IComparable`. 100k inserts show no large v4/v7 difference, so v4 stays.
  - Spike S-ID4, PRD §8.5: 15/15 on a copy of the development `Coverage` database. Mixed-case legacy ids need no
    re-key on Lucene or Corax.
  - Spike S20, PRD §8.6: 12/12. Commands as keyed DI registrations construct only the requested command;
    synchronize opens no connection. D34 gets three rules.
  - Spike S19, PRD §8.7: EF's migration lock is not enough. Npgsql 11 holds it per migration (two instances
    both applied a later migration, 3/3 runs), and neither provider guards `CREATE DATABASE`. D33 now takes Spark's
    own session lock in `master`/`postgres` around creation and all three steps, and that passes on both. Failure
    halfway, the idempotent script and the pending check pass.
  - The owner asked for a provider-neutral test harness: D35 proposes `MintPlayer.Spark.Testing` plus
    `.RavenDB/.SqlServer/.PostgreSql` packages; S17 widened; O16 (how a project picks its database) is open.
  - The owner asked whether SQL Server, PostgreSQL and MariaDB have a `RavenTestDriver` equivalent. Only
    PostgreSQL does (`MysticMind.PostgresEmbed`); SQL Server can't ship its engine (LocalDB is Windows-only) and
    MariaDB has no maintained .NET package. The owner followed the recommendation: PostgresEmbed as
    `.Testing.PostgreSql`'s default; connection string → LocalDB → Testcontainers for SQL Server. Checks added to
    S17.
  - Spike S3, PRD §8.8: synchronize can scaffold EF migrations from a metadata-built model through public
    design-time API only. Add field, queryable field, AsDetail, reference and rename all scaffold and migrate on
    both databases. Trap found: scaffolding on the context that ran `HasPendingModelChanges()` silently recreates
    every table (EF 11 `SnapshotModelProcessor` returns null for a finalized model). O17 opened for
    `Microsoft.EntityFrameworkCore.Design`'s 17 MB.
  - O17 decided by the owner, PRD §8.9: `MintPlayer.Spark.Relational.Design`, packed with
    `DevelopmentDependency=true`, its EF Design reference `PrivateAssets="none"`, and a
    `buildTransitive/*.targets` that marks the app's reference `Publish="false"` (and warns on a transitive
    arrival). Measured: a Release publish drops from 46 DLLs / 42 MB to 26 / 17 MB with no Roslyn, synchronize
    still loads the package from `bin`, and the app's own Humanizer survives. Rejected on evidence: stripping a
    hand-kept list from the relational package (it removed the app's own Humanizer, and the published app crashed
    at startup), a package `.props` file (its `Update` runs before the reference exists) and a Debug-only
    `Condition` (the line belongs at publish; in-repo apps still use one, since a `ProjectReference` never sees a
    package's targets).
  - O16 decided by the owner: the provider testing package the test project references decides, through an
    `[assembly: SparkTestDatabase(…)]` its `build/*.targets` generates; `SPARK_TEST_DATABASE` picks one when a
    project references several (Spark's neutral suites, the CI matrix), and every ambiguity fails at startup.
    The runtime provider packages ship no build files for it.
  - Upstream reports (owner's go): the S3 snapshot trap is the open dotnet/efcore#36016, now commented with the root
    cause, a SQLite repro and a fix; the S19 lock race is filed as npgsql/efcore.pg#4162. Repros with a workaround
    and an upstream patch on a `fix` branch: PieterjanDeClippel/efcore-scaffold-readonly-snapshot and
    PieterjanDeClippel/efcore-npgsql-migration-lock-race. Spark already avoids both (fresh context, D33 guard).
  - Upstream pull requests (owner's go), each red on `main` with a new test and green with the change:
    npgsql/efcore.pg#4163 (session advisory lock) and dotnet/efcore#39784 (the scaffolder reads a new snapshot
    instance).
  - The workarounds are applied (owner, 2026-11-24): synchronize scaffolds on the context that ran the check,
    through `FreshSnapshotMigrationsAssembly` in the design-time services (D24). The PostgreSQL provider registers
    `AdvisoryLockHistoryRepository` (D33). Reruns: S3 is green on every version on both databases, and the trap
    still reproduces without the decorator. S19's new scenario 1d gives each migration once on PostgreSQL with
    EF's lock alone. D33's guard stays for `CREATE DATABASE` and the data migrations. Both workarounds go once
    their fixes ship.
  - Spike S2b, PRD §8.10: the real `QueryExecutor` runs on an EF prototype of the storage seam inside the real test
    host, 14/14 on both databases. Row filter, `In`, search, the reference-list column filter, TPH, the
    `TranslatedString` JSON column, the AsDetail child table and O13's stored get-only member all run as SQL. Found
    on the way: `Search` receives RavenDB's `*word*` syntax (0 rows on the first run), and `Expression.Constant`
    becomes a SQL literal, so a query plan per value. Both are fixed in M2. M3 needs `AsSplitQuery` for types with
    several AsDetail collections.
  - Spike S4, PRD §8.11: 15 cases green on both databases, with the D14 etag through Spark's own `IDatabaseAccess`
    (44/44 with S2b's). D3 refined: the version covers the aggregate, because EF leaves the owner unchanged when
    only its child or join rows change. The D30 translator matches names against the model, because SQL Server
    localizes its messages and German drops the quotes (tested with `Current Language=Deutsch`).
  - Spike S5, PRD §8.12, the last gate: 10 cases green on both databases (64/64 with S2b and S4).
    - One `SparkView` declaration renders as the SQL Server and PostgreSQL view SQL (written by EF), as a RavenDB
      map (with `LoadDocument`) and as LINQ to objects, with the same rows from each.
    - `QueryExecutor` queries the view by name, unmodified.
    - Spark's differ writes `CREATE VIEW`/`DROP VIEW` into the migration. PostgreSQL refuses `ALTER COLUMN TYPE`
      under a view, so the differ recreates a view around any change to a table it reads.
    - The pubternal differ's constructor already changed after EF `v11.0.0`.
    - The view SQL carries `\r\n` on Windows, so synchronize normalizes it.
  - O8 and O9 decided (owner): PostgreSQL 17 and later, with CI on 18 and the D31 behaviour tests on 17 too; a
    public `ConfigureModel(ModelBuilder)` hook in M4, and no app-supplied `DbContext`. No decision is open.
