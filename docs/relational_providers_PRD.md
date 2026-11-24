# Relational storage providers (SQL Server, PostgreSQL, MariaDB): PRD

Issue: [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). Builds on the storage-agnostic core of
[#551](https://github.com/MintPlayer/MintPlayer.Spark/issues/551) (PR #620, `docs/spark_editor_PRD.md` §3
"Storage-agnostic core", §8). The spikes, milestones, grilling rounds and status are in
[`relational_providers_plan.md`](relational_providers_plan.md).

Status: **plan, after a second investigation and three grilling rounds.** In scope: **SQL Server and PostgreSQL.**
MariaDB was investigated and spiked, then **dropped for now** (owner, 2026-11-24, §10 O6). The relational core keeps
a dialect seam, so it can return once a maintained EF Core 11 provider exists.
- 2026-11-19: first rewrite, SQL Server only, spikes S1/S2 (§8.1). The file was `docs/sql_server_provider_PRD.md`.
- 2026-11-24: widened to **SQL Server, PostgreSQL and MariaDB** (owner). Changes:
  - a fresh inventory of master `0b9915c50` (§2.3, §2.5, §2.6)
  - the EF Core 11 provider landscape (§2.7)
  - a RavenDB-to-relational equivalents table (§4)
  - spikes S12/S13, the D1 hybrid on PostgreSQL 18 and MariaDB 11.8 (§8.2)
  - a grilling round (plan §3)
- 2026-11-24, grilling round 3 (owner): **D1 reversed to a table per entity mapped by EF Core.** The model
  synchronize generates EF migrations, AsDetail becomes child tables, references become real FKs, and the contract
  becomes typed. Breaking changes are allowed, with no backward compatibility. The id model `Id<T>` (D23) came from a
  moderated consensus of six LLMs. A comparison with a commercial framework's relational providers added D30, `LIKE`-first search,
  and framework id lookups through a provider hook (§3).

## 1. Goal

A web-app developer chooses the database at registration: `spark.UseSqlServer(...)` or `spark.UsePostgreSql(...)`
instead of `spark.UseRavenDb(...)`. `spark.UseMariaDb(...)` is deferred (§7, §10 O6). The model, the actions classes and the policy pipeline
stay the same (D15 portability goal, `docs/spark_editor_PRD.md` §4a).

- **Core and the feature libraries are storage-agnostic, as far as possible** (owner, 2026-11-19 and 2026-11-24).
  A library talks to `ISparkSession`/`ISparkStore` and to neutral contracts. It names a *capability* it needs, not
  a provider.
- **A plugin built around a RavenDB feature stays RavenDB-only, and says so.** It is refused at startup and by
  SPARK060, with a stated reason. Today that is replication (RavenDB ETL), plus whatever §5 and the owner's answers
  add.
- One relational core holds what the databases share. Thin dialect packages hold what differs (D2, §3).
- **One compiled feature library runs on every provider.** Ids are typed and provider-neutral (D23).
- Every provider passes the provider conformance suite, which this work extends (§2.4).

## 2. Findings

### 2.1 The provider contract today

All of it lives in `libs/spark/MintPlayer.Spark.Abstractions/Storage/`. **It has not changed since 2026-11-19**
(`git diff 5433a99dc -- Abstractions/Storage Abstractions/Indexes` is empty). There is still no lock, session
expiry, streaming or uniqueness contract.

| Contract | Members | What it means for a relational database |
|---|---|---|
| `ISparkSession` (`ISparkSession.cs:22-127`) | `Query<T>`, `Query<T,TIndex>`; loads, batch loads with `includePaths`, untyped `LoadAsync`, `PreloadAsync`; `LoadStartingWithAsync`; `ExistsAsync`; `StoreAsync` with `expectedVersion`; `Delete(id, expectedVersion)`; `GetVersion`/`GetVersionsAsync`; `GetCollectionName`/`GetStoredCollectionName`; tracking; `BeforeStore`/`BeforeDelete`; request budget; `Native` | a unit of work plus an identity map. An expected version of `""` means insert-only. `LoadStartingWith` is `LIKE 'prefix%'` on the key. |
| `ISparkStore` | `ProviderName`, `Capabilities`, `OpenSession`, `WaitForConsistencyAsync` | `WaitForConsistencyAsync` returns at once: a relational database is consistent on commit |
| `SparkStoreCapabilities` | Subscriptions, Revisions, FullTextSearch, Attachments, Expiration, ChangeNotifications, Indexes | per database, see §4.2 |
| `ISparkQueryProvider` | `IsStoreQuery`, `OpenIndex`, `Project`, `Include`, `In`, `IndexedFields`, `Search`, `CountAsync`, `ToListAsync` | `IsStoreQuery => true` only if the LINQ really becomes SQL (S2, S12, S13) |
| `ISparkChangeFeed` | `OnCommitted`, `WatchDocument`, `WatchCollection`, `Reconnected` | in process at commit; across processes through the outbox (S7) |
| Optional: `ISparkIndexDeployment`, `ISparkStoreExpiration`, `ISparkStoreRowKeyScan`, `ISparkDataProtectionKeyStore` | only RavenDB registers them today | all four are doable: generated columns and views, a sweeper, a JSON scan, a keys table |
| `IIndexCatalog` (`Abstractions/Indexes/IIndexCatalog.cs:17`) | core falls back to `EmptyIndexCatalog` | S5 |

The in-memory provider (`libs/spark/MintPlayer.Spark.InMemory/Storage/InMemory/`) is the reference implementation:
- Newtonsoft JSON snapshots (`InMemoryJson.cs:13`), with a fresh copy deserialised on every load.
- A dirty check by snapshot diff (`InMemorySparkSession.cs:245`).
- Atomic version commits (`InMemoryDatabase.cs:47-53`).
- Ids `{Collection}/{Guid}` or `IHasNaturalId` (`InMemoryConventions.cs:7-25`).

**The relational providers reuse its JSON shape and its id conventions**, so all five providers store the same
document.

### 2.2 Registration and refusal

- `UseRavenDb` is in `SparkRavenDbExtensions.cs:22` and `UseInMemory` in `SparkInMemoryExtensions.cs`. Each carries
  `[SparkStoreProvider(name, capabilities)]`.
- A feature library declares what it needs with `[SparkRequiresStore]` and `RequireStore()` (`SparkStoreRequirement.cs`).
- `UseSpark` refuses unmet requirements at startup (`SparkMiddleware.cs:275-283`). Analyzer SPARK060 refuses them at
  compile time (`StoreRequirementAnalyzer.cs:29`).
- **Every library that stores data declares `[SparkRequiresStore(Provider = RavenDb)]`.** Logging is the only
  exception. Each port replaces that pin with a capability requirement.
- `SparkStoreProviders` has no relational constants yet (`:28-32`).

### 2.3 Coupling left in core

No core project references a RavenDB package: `MintPlayer.Spark`, Abstractions, Controllers, SourceGenerators and
LibraryGenerators were checked. One leftover is `InternalsVisibleTo MintPlayer.Spark.RavenDB` in
`Abstractions.csproj`. The couplings that need a change:

- **Data protection:** `SparkDataProtectionStorage.RavenDb` (`SparkDataProtectionOptions.cs:53`,
  `SparkDataProtection.cs:60/:101/:105-106`) is already gated on `ISparkDataProtectionKeyStore`. Rename it to a
  neutral `Store`.
- **Defaults and messages:**
  - `SparkStoreRequirement.cs:50` defaults its hint to `UseRavenDb`. *New in 2026-11-24.*
  - RavenDB wording in messages: `LiveHub.cs:190`, `SparkIndexBindingStartupCheck.cs:32`, `SparkExpiration.cs:44`,
    `SparkComposedQueries.cs:71`.
- `ModelSynchronizer.cs:29` needs only `IIndexCatalog`.
- **Generators** emit RavenDB index code:
  - `GenerateIndexGenerator.cs:16/:36/:155` and its `HandWrittenProducer.cs:25/:176/:200`
  - **`SearchIndexGenerator.Producer.cs:72-79`** (`FieldIndexing.Search`, `FieldStorage`). *Missed in 2026-11-19.*

  With S5 they emit a neutral declaration, plus the RavenDB rendering only when RavenDB is referenced.
- **Analyzers** key on RavenDB type names: `RavenIndexHierarchy.cs:51`, `DefaultIndexAnalyzer.cs:216-288`,
  `SortCompanionAnalyzer.cs:248`, `ProjectionPropertyAnalyzer.cs:149`, `RavenHasValueAnalyzer.cs`,
  `McpToolDataAccessAnalyzer.cs:40-42`. They stay as RavenDB-specific checks. A neutral view declaration needs its own
  analyzer for the same mistakes.
- **`QueryExecutor`** asks the store for:
  - `IsStoreQuery` and `In` (:664)
  - `OpenIndex`/`Project` (:959-967) and `Include` (:1049, :1365)
  - the row filter's server-side translation (:1055), and `AnyContains` (:2368)
  - `IndexedFields` (:2062), `Search` (:2155), and `Skip`/`Take` paging (:438)

  The row-security gate runs on materialised rows (:1162), so it works on any provider.
- The new file-attribute types in core (`ISparkFileValueHandler`, `SparkFileUrls`, the `EntityMapper` changes,
  a2704bf02) contain no RavenDB code.

### 2.4 Conformance suite

`tests/MintPlayer.Spark.Tests/Storage/SparkStoreConformanceTests.cs:15` is abstract. A provider overrides
`CreateStoreAsync()` (:56) and optionally `Settled<T>` (:58). RavenDB (:347) and InMemory (:399) subclass it, and
each relational provider joins as one more subclass.

It has 13 facts: round-trip, absent field, collections, identity map, tracking, `BeforeStore`, delete, absent and
stale versions, `LoadStartingWith`, query filters and the request budget. It does **not** cover:
- row security, DateTimeOffset or paging (0 hits)
- null ordering, which differs between databases (§8.2 finding 4)
- the silent wrong results S13 found

This work adds those cases (plan M4), and they run on every provider. **A query the provider cannot translate must
throw; it must never return wrong rows.** S13 shows that a translating ORM does not guarantee this.

### 2.5 Feature libraries: the inventory (master `0b9915c50`)

`git ls-files` returns 0 for `approvals`, `challenges`, `commerce`, `fulfilment`, `printing`, `refdata`, `sms`,
`waiting_room` and `warehouse`. They are untracked (commerce is paused on draft PR #529) and out of this plan.
`gs1`, `sitemap`, `webpush`, `webhooks`, `model`, `attributes` and `cli` don't touch the store.

In the "Raven ref" column, "pkg" is a direct `RavenDB.Client` reference and "via" goes through
`MintPlayer.Spark.RavenDB`. The files column counts RavenDB-client files against neutral-contract files.

| Library | Raven ref | Files (Raven / neutral) | RavenDB features used |
|---|---|---|---|
| logging | — | 0 / 3 | none: already neutral |
| settings | pkg | 4 / 0 | none special |
| boards | pkg | 1 / 0 | none |
| notifications | pkg + via | 6 / 0 | `WaitForNonStaleResults`; `NotificationVisitorClaim.cs:51` is new (#710) |
| apps | pkg + via | 10 / 0 | `Advanced.Patch` (`WebhookDeliveryHandler.cs:237`) |
| organizations | pkg + via | 7 / 0 | `WaitForNonStaleResults` (`SparkOrganizations.cs:364/:384/:399`). The Patch the 2026-11-19 inventory listed is now only a comment. |
| mail | pkg | 1 / 0 | `@expires` (`SparkMailStore.cs:148`) |
| mcp | via | 1 / 0 | `@expires` (`McpPlanner.cs:152/:557/:564/:589`) |
| cron | pkg | 2 / 0 | compare-exchange lease (`SparkCronScheduler.cs:242-270`) |
| migrations | pkg | 2 / 0 | compare-exchange lock (`SparkMigrationRunner.cs:200-238`) |
| contributions | via | 5 / 0 | insert-only store, change-vector compare, `Lazily.LoadStartingWith`, `StreamAsync`, metadata (`ContributionHandler.cs:158/:180/:283-300/:477`) |
| search | via | 2 / 0 | `Search()` (`RavenSearchProvider.cs:38/:63`) |
| **files** *(new, a2704bf02)* | pkg + via | 5 / 1 | attachments as the default store, revisions (read + `DeleteRevisionsOperation`), `StreamAsync`, maintenance operations (`RavenAttachmentFileStore.cs:33-189`, `ImageVariantRegenerator.cs:77`, `DeferredFileScanner.cs:97`). Blob stores are already pluggable (FileSystem, S3, AzureBlob). It is the first library that uses `ISparkSession` (`IFileStore.cs:114`). |
| **templates** *(missed)* | via | 1 / 0 | the `SparkIndex` template emits `SparkIndexCreationTask` |
| **testing** *(missed)* | pkg (TestDriver) + via | 9 / 0 | `RavenTestDriver`, index deployment, maintenance, `WaitForNonStaleResults`. It needs a provider-neutral test driver. |
| all_features | via | 0 / 0 | nothing; it only references the RavenDB package |
| subscription_worker | | 2 | §2.6 |
| messaging | | 18 | §2.6 |
| history, soft_delete | | 4, 4 | §2.6 |
| moderation | | 15 | §2.6 |
| authorization | | 10 | §2.6 |
| identity_provider | | 71 | §2.6 |
| dashboards | | 4 | §2.6 |
| replication | | 8 | §2.6 |

### 2.6 What the heavy libraries actually rely on

The 2026-11-19 plan listed RavenDB APIs. This round asked which **guarantee** each one gives the library. The
answers are less demanding than the API list suggests.

- **Subscriptions are a wake-up signal, not the source of truth.**
  - Messaging and replication keep due-state in plain fields (`WakeUp`, `NextAttemptAtUtc`). A sweeper flips
    `WakeUp` because `now()` never matches in a subscription query.
  - The real claim is an optimistic-concurrency store plus a lease field (`ClaimExpiresAtUtc`) (`MessageClaims.cs:204`,
    `MessageRetrySweeper.cs:144`).
  - Messaging does not rely on delivery order: it reads a priority/sequence index page and merges it with the batch
    (`SparkMessages_ByPriority.cs:56-69`, with an `@last-modified` fallback).
  - `@refresh` is no longer used: `RetryNumerator.cs:18-30` documents its removal as inert.
  - The one guarantee to replace is **exclusivity**. Today it comes from the subscription's `WaitForFree`
    single-worker rule (`MessagingLease.cs:18-30`). The compare-exchange lease only checks liveness. A relational
    port needs a real claim (`UPDATE … WHERE Version = @v`, or `SKIP LOCKED`).
- **subscription_worker** is the generic one: an RQL subscription query, at-least-once delivery, the batch acked
  when the handler returns (`MaxDocsPerBatch` 256), and counters as a per-document retry count
  (`SparkSubscriptionWorker.cs:105/:134/:179/:201`, `RetryNumerator.cs:68/:110`). Its subscription *query* is RQL,
  so on a relational database it becomes a neutral predicate (an expression over `T`) evaluated over the outbox.
- **`@expires` is cleanup only.** Read paths check `ExpiresAt` themselves (IdP `Token.cs:189/:529`,
  `Introspection.cs:92`). A sweeper is enough.
- **Compare-exchange is used two ways:**
  1. **Uniqueness reservations** (email, passkey id, OIDC client id). The reservation is taken over when its owner
     no longer exists (`OidcClientIdReservation.cs:59`). In one place it shares a cluster-wide transaction with the
     document delete (`UserStore.cs:206`). A relational database does this better: a reservation row in the same
     transaction as the document.
  2. **Leases with expiry** (cron, migrations, messaging, dashboards).
- **Revisions:**
  - history reads, pages and reverts to any old version, with per-collection retention and `PurgeOnDelete`
    (`SparkHistory.cs:45/:94`, `RevisionsConfigurator.cs:78-99`)
  - soft_delete only wipes revisions on purge (`SoftDeleteRevisions.cs:67`)
  - authorization reads revision metadata in a backfill (`SparkUserBackfill.cs:147`)
  - files reads them and deletes them
- **Indexes:**
  - No index anywhere uses `LoadDocument`, multi-map or fan-out.
  - **Only moderation has map-reduce:** 3 `group by` + `Sum` indexes (`ModerationIndexes.cs:44/:72/:126`), read with
    `WaitForNonStaleResults` (×10) for read-your-writes. This is the hardest part to port.
  - The rest are plain map indexes: messaging (by priority), identity_provider (2), replication (by status), and
    moderation (2).
- **Time series:**
  - Dashboards append samples with raw and rollup retention policies, and aggregate them in RQL `group by`/`avg()`
    (`DashboardStorage.cs:62/:78-106/:130/:145`).
  - IdP's `INC:Tokens` is write-only: no reader was found.
- **Set-based writes:**
  - IdP's revoke cascade is a `PatchByQuery` with `AllowStale = false` (`OidcDisableCascade.cs:56`). In SQL that is
    one `UPDATE`.
  - authorization's migrations use `PatchByQuery` and `AsyncRawQuery`.
- **Replication** is RavenDB ETL with JavaScript transforms (`EtlTaskManager.cs:72/:92`). It has no relational
  equivalent and is RavenDB-only by design.

### 2.7 Databases and EF Core 11 providers (checked 2026-11-24)

| Database | Versions targeted | EF Core 11 provider | JSON storage |
|---|---|---|---|
| SQL Server | 2022 (compat 160), 2025 (compat 170) | `Microsoft.EntityFrameworkCore.SqlServer` **11.0.0** (Microsoft) | `json` type on 2025; `nvarchar(max)` + `ISJSON` on 2022 |
| PostgreSQL | 18 (17 with a stored `Raw` column, see §8.2) | `Npgsql.EntityFrameworkCore.PostgreSQL` **11.0.0**, stable on NuGet. It defaults to PostgreSQL 16 semantics; `SetPostgresVersion(18, 0)` enables virtual generated columns | `jsonb` |
| MariaDB | 11.8 LTS (JSON_TABLE needs 10.6+) | **No first-party option.** Pomelo stops at **9.0.0** (EF Core 9). Oracle's `MySql.EntityFrameworkCore` stops at EF 10 and does not support MariaDB. The only EF 11 provider is **`Microting.EntityFrameworkCore.MySql` 11.0.0** (2026-11-17), a community fork of Pomelo. | `JSON` = `LONGTEXT` with a validity check |

SQL Server notes from 2026-11-19 still hold: full-text is not installed on the owner's instances, and Express has
no SQL Agent (so no CDC). The `json` type and `CREATE JSON INDEX` were still marked preview for 2025 in the docs
found; the spike ran on them anyway. The SQL pin is `Microsoft.Data.SqlClient` 7.1.2 (GHSA-846f-f764-76mx,
GHSA-2fmv-rv2c-59x7). The repository has no `Directory.Packages.props`.

CI images: `mcr.microsoft.com/mssql/server:2025-*`, `postgres:18` and `mariadb:11.8` all run as GitHub Actions
service containers. The spikes pulled and ran `postgres:18` and `mariadb:11.8` on Docker Desktop.

## 3. Design direction

**Grilling round 3 (owner, 2026-11-24) reversed D1:** a table per entity, mapped by EF Core. The hybrid document
table that S1, S2, S12 and S13 proved is history (§8, §9). **Breaking changes are allowed, and no backward
compatibility is needed yet** (owner, 2026-11-24, said twice): the session contract, the entity shape and the stored
ids change outright, with no shims.

Decisions D1–D7 are local to this PRD. D14–D16 are `docs/spark_editor_PRD.md`'s, which ends at D22, so the decisions
added here start at D23.

**D1 (round 3): a table per entity, mapped by EF Core 11.**
- The framework builds the EF model from the Spark model: one table per collection. A collection with derived types
  is one TPH table (the root's) with a discriminator column.
- Scalar properties are columns. Core value types without a relational shape (`TranslatedString` and its kind) are
  JSON columns: `json` on SQL Server 2025, `nvarchar(max)` on 2022, `jsonb` on PostgreSQL.
- EF Core reads, writes and translates queries. `QueryExecutor`'s `IQueryable<T>` runs on EF's own translator, with
  no rewrite.
- **A commercial framework does the same** (compared 2026-11-24): its SQL Server and PostgreSQL packages map one
  row per table through EF Core. The difference: its apps write their own `DbContext`, while Spark generates the
  model (D24), so a feature library ships its entities with no EF code.

**D1a: superseded.** The global `Documents` table and its global id space existed because several contract members
carried no type (`ExistsAsync(id)`, `Delete(id, expectedVersion)`, `GetVersionsAsync(ids)`). D27 types them, and D23
puts the type in the id.

**D2: one relational core, two dialects.**

```
MintPlayer.Spark.Abstractions
  └─ MintPlayer.Spark.Relational   EF model built from the Spark model, migration generator (D24), unit of work over
     │                             a DbContext, outbox, leases, reservations, revisions, expiry sweeper, aggregate
     │                             tables, constraint-error translation (D30), conformance hooks
     ├─ MintPlayer.Spark.SqlServer  EF provider setup, HiLo sequences, JSON column type, claim hints (READPAST),
     │                             error numbers
     ├─ MintPlayer.Spark.PostgreSql EF provider setup, HiLo sequences, jsonb, NULLS FIRST, SKIP LOCKED, SQLSTATEs,
     │                             optional LISTEN/NOTIFY wake-up
     └─ (MintPlayer.Spark.MariaDb   deferred: needs a maintained EF 11 provider; §7 risk 1)
```

- The neutral contracts (§4.1) are implemented once, in `Relational`. Database-specific features stay optional:
  advisory locks, `LISTEN/NOTIFY`, full-text, temporal tables.
- Why not one package per database with everything in it? Two copies of the unit of work, outbox and revisions
  would drift, and the conformance suite would find the same bug twice.

**D34 (owner, 2026-11-24): inversion of control everywhere. Core owns every entry point; providers only plug in.**
- **`MintPlayer.Spark.Abstractions` and `MintPlayer.Spark`** hold the contracts and the generic logic: command-line
  switches, startup steps and their order, options, canonicalisation (D7), the neutral view declaration (D32), and
  the conformance suite. **The provider packages** (RavenDB, SqlServer, PostgreSql, later MariaDb) only register
  implementations of those contracts from their `UseRavenDb()` / `UseSqlServer()` / `UsePostgreSql()` call.
- An app's `Program.cs` names its provider once, in that call, and nowhere else. Core never asks which provider is
  installed (no `is RavenSparkStore`, no provider name strings); it resolves a contract, or reads a capability
  (`SparkStoreCapabilities`) when the feature is optional.
- A provider with nothing to do for a contract registers a no-op, or nothing where core treats absence as "nothing
  to do". It never needs its own switch or extension method.
- **Commands become one registry.** Today every command is its own extension that each app must call by hand:
  `SynchronizeSparkModelsIfRequested`, `InitializeSparkSecurityIfRequested`, `VerifySparkSecurityIfRequested`
  (`apps/HR/HR/Program.cs:106-116`), `RegenerateSparkImageVariantsIfRequested`
  (`SparkImageVariantCommand.cs:23`) and `InitializeSparkModerationIfRequested` (`ModerationInitCommand.cs:44`).
  Core gets an `ISparkCommand` contract (description, `RunAsync` returning the exit code). **There is no registry
  class: the DI container is the registry** (the owner's multi-registration pattern). Each package registers its
  commands keyed by their flag, `services.AddKeyedScoped<ISparkCommand, DatabaseMigrateCommand>("--spark-migrate")`,
  and core resolves only the requested one in a scope (`GetRequiredKeyedService<ISparkCommand>(flag)`). The key
  instead of `GetServices<ISparkCommand>().Where(c => c.Name == flag)` means only the requested command and its
  dependencies are constructed: `--spark-synchronize-model` runs in CI without a database, and must not build the
  migrate command's `DbContext` or document store on the way. `--spark-help` lists every key with its description.
  `Program.cs` keeps one line after `Build()` (`if (await app.RunSparkCommandIfRequestedAsync(args)) return;`), and
  adding a package's command edits no app. The commands that run on the builder today (synchronize, verify, describe,
  the security and moderation initialisers) move after `Build()`, which constructs no service and opens no
  connection; one that turns out to need the builder becomes a builder-phase hook of the same command. The existing
  commands move onto it in this PR (no backward compatibility).
- **Proven by S20 (§8.6), with three rules:**
  - The runner resolves inside a scope. Resolving a scoped command from the root provider throws under
    `ValidateScopes`.
  - `--spark-help` reads the keys from the service descriptors (`ServiceDescriptor.ServiceKey`), captured by a
    singleton that `AddSpark` registers. `GetKeyedServices(AnyKey)` would construct every command instead.
  - A command takes only contracts that core gives a default. In Development, `ValidateOnBuild` refuses `Build()`
    when a core command's dependency is registered only by a provider. When core and a provider register the same
    key, the **last registration wins**, so a provider registers after `AddSpark`.
  - Moving after `Build()` also retires `SparkSecurityVerificationExtensions.cs:75`'s throwaway
    `BuildServiceProvider()` before `Build()`. It existed only so that nothing needing a database was constructed.
- **Migrations (D33) are the first user.** Core owns `--spark-migrate`, `--spark-migration-script`,
  `Spark:Database:ApplyMigrations`, the startup order and the refusal message. Providers implement one contract,
  `ISparkSchemaMigrator` (the startup guard, pending migrations, apply, write a script; S19 showed the guard must be
  Spark's own, D33):
  - **RavenDB** applies what it already deploys at startup (indexes, the revisions configuration), reports nothing
    pending, and writes no script (it says RavenDB has no schema script and exits 0).
  - **SqlServer / PostgreSql**, through `Relational`, apply the framework schema and the app's EF migrations, and
    write the idempotent script.
  - The data migrations (`ISparkMigration`) run after the schema on every provider. Their runner moves off RavenDB's
    compare-exchange lock (`SparkMigrationRunner.cs:15`) onto the neutral lease contract (§4.1, Tier 2).
- The same rule holds for every other decision: the absent-field rewrite (O11) lives in the RavenDB provider, the
  id conventions (D7) in each provider's registration, and the view rendering (D32) in each provider.

**D35 (the owner's direction, 2026-11-24; the provider choice is O16, decided: the referenced provider package
decides): the test harness splits the way the runtime does.** `MintPlayer.Spark.Testing` is RavenDB-only today: it references `RavenDB.TestDriver` and
`MintPlayer.Spark.RavenDB`, `SparkTestDriver` and `SparkSharedDatabase` derive from `RavenTestDriver`, and
`SparkEndpointFactory` takes an `IDocumentStore` (`SparkEndpointFactory.cs:116-117`). 255 test files derive from
those drivers. The relational providers have no equivalent of `RavenTestDriver`; EF Core ships none.
- **`MintPlayer.Spark.Testing` (neutral, no database reference)** owns the entry points:
  - the `SparkTestDriver` (per case, database created on first use) and `SparkSharedDatabase` (per class) lifecycles
  - `SparkEndpointFactory`, which boots the host and lets the provider plug itself in instead of swapping an
    `IDocumentStore`
  - `SeedAsync` over `ISparkSession`, the JSON fixtures, and `WaitForConsistencyAsync` instead of
    `WaitForIndexesAsync`
  - `SparkTestSecurity`, `AsyncWait`, `RouteTableSnapshot`, `VerifyDefaults`, `Wire`, and the `AGENTS.md` and
    targets
  - a neutral query recorder (RavenDB: the request event `RqlRecorder` uses; EF: a `DbCommandInterceptor`)
- **One contract, `ISparkTestDatabaseProvider`**: locate or provision the server once per process, then create a
  database, configure the host for it, wait for consistency, and drop it. This is the conformance suite's
  `CreateStoreAsync()` override (§2.4) grown into a package.
- **`MintPlayer.Spark.Testing.RavenDB`** gets today's code: `RavenTestDriver`, the embedded server and
  `RavenServerLocator`, the licence, `RavenDatabaseDeletion`, index deployment and `RqlRecorder`. Its
  `RavenSparkTestDriver` still exposes the raw `IDocumentStore` for tests that pin RavenDB behaviour.
- **Locating the server is the provider's job, and only RavenDB and PostgreSQL can ship one** (researched
  2026-11-24, the owner followed the recommendation). `RavenTestDriver` is not in-process either:
  `RavenDB.Embedded` ships the server binaries and starts `Raven.Server` as a child process. The other databases:
  - **PostgreSQL has the same model:** `MysticMind.PostgresEmbed` (NuGet 5.2.1) downloads zonky's minimal real
    PostgreSQL binaries (Windows, Linux, macOS), runs `initdb` and starts a `postgres` process. It is
    `.Testing.PostgreSql`'s default, so a developer needs no Docker, as with RavenDB. A connection-string variable
    overrides it.
  - **SQL Server cannot:** Microsoft does not redistribute the engine. LocalDB is the real engine, started on
    demand, but it is Windows-only, installed by an MSI, and its 2025 package was reported to miss native binaries.
    `.Testing.SqlServer` takes a connection-string variable (a local instance, or the CI service container D31
    already needs), then LocalDB on Windows, then Testcontainers (MIT).
  - **MariaDB has no maintained .NET equivalent** of Java's MariaDB4j: Testcontainers, when its provider returns (O6).
- **`MintPlayer.Spark.Testing.SqlServer` / `.PostgreSql`** (`.MariaDb` with its provider, O6) create a database per
  class, migrated through the same guarded startup as production (D33, S19).
  PostgreSQL can clone a migrated template (`CREATE DATABASE … TEMPLATE`). SQL Server has no template, so S17 measures
  create-and-migrate per class against resetting a shared database between classes (Respawn, Apache-2.0).
- **Moving the 255 files:** no backward compatibility. Tests that touch the raw store move to `RavenSparkTestDriver`
  in one mechanical rename. They move to the neutral driver as their library is ported to `ISparkSession`
  (§5 tiers), and only then run on every provider.
- Why not one testing package with every provider in it: every test project would pull in RavenDB, the EF
  providers and Testcontainers, against D34's rule that an app names its provider once.
- **Guard:** the layering tests (`tests/MintPlayer.Spark.Tests/Layering/`) fail when `Abstractions` or `MintPlayer.Spark`
  references a provider assembly or a database client.

**D3: the version is a `bigint` column on every table**, a concurrency token the provider increments. *(S4, §8.11:
it is on every root table and covers the aggregate. A change to a child row (D25) or a join row (D26) moves the
root's version, which EF alone does not do.)*
- A write is `UPDATE … SET Version = @v + 1 WHERE <key> = @id AND Version = @v`. EF's 0-rows-affected
  `DbUpdateConcurrencyException` becomes `SparkStoreConcurrencyException`. S12 and S13 measured the statement shape
  (1 row, then 0 for the stale write).
- An expected version of `""` is an `INSERT` that fails on a duplicate key.
- `rowversion` and `xmin` would also work, but neither is portable. One mechanism keeps the dialects thin.
- The version string on the wire stays opaque (D14 etag, #467).
- The commercial framework has no optimistic concurrency on SQL at all: only its RavenDB package registers a token handler (change
  vector). Spark keeps the stricter rule.

**D4: sort order is normalised to nulls first.** InMemory (LINQ-to-objects), SQL Server and MariaDB sort nulls first
ascending, and PostgreSQL sorts them last (S12). The PostgreSQL dialect emits `NULLS FIRST` (and `NULLS LAST`
descending), and a conformance case pins it. RavenDB's order is measured in the same case.

**D5: `DateTimeOffset` compares instants on every provider.** SQL Server (`datetimeoffset`) and PostgreSQL
(`timestamptz`) do this. (MariaDB has no offset-aware type, S13; it is deferred.)

**D6: a commit-ordered `ChangeStamp`** replaces `@last-modified` as the change order for the subscription cursor, the
change feed's reconnect and messaging's fallback order. **Round 3 made it per table; grilling round 5 (O10,
2026-11-24) put it on the entity table:**
- **A stamp column on every entity table**, with a cursor per (subscription, table). SQL Server: a `rowversion`
  column. PostgreSQL: an `xid8` column set by a trigger that synchronize generates.
- A subscription reads `WHERE stamp > @cursor AND stamp < @horizon AND <its filter> ORDER BY stamp` on the table
  itself. A new subscription starts at stamp 0, so the table is its backlog: the current state, once per row, as
  RavenDB delivers it. There is no change log to trim.
- **Deletes** write a row to a framework `Tombstones` table (stamp, table, key) in the same transaction (D24's own
  schema), read with the same horizon.
- **Live updates** first probe whether the database stamp moved since the last poll (`@@DBTS` on SQL Server,
  `pg_current_xact_id()` on PostgreSQL), and only then query the watched tables.
- Rejected: one change-log table (an extra write per save, a hot insert point, and trimming that leaves a new
  subscription without its backlog), and both together (the cost of each).
- The trap: one long transaction holds the horizon back, so every subscription waits for it. S14 measures that.

A plain database sequence is wrong either way. A sequence value is allocated before commit:
1. Transaction A takes 10.
2. Transaction B takes 11 and commits.
3. A cursor reads 11 and advances.
4. A commits 10, which the cursor never sees.

The dialect supplies the stamp and a *safe read horizon*:
- **SQL Server:** a `rowversion` column, read only below `MIN_ACTIVE_ROWVERSION()`.
- **PostgreSQL:** `pg_current_xact_id()` stored per row, read only below `pg_snapshot_xmin(pg_current_snapshot())`,
  ordered by (xid, key).

S14 proves the horizon with two interleaved transactions. The commercial framework gives no guidance here: on SQL it polls a cache
table for its own caches and has no change feed for business data.

**D7 (revised by D23, by the round 6–9 consensus, then by the owner's lowercase rule in round 10): every id is
lower case, on every provider.** The owner proposed it (2026-11-24): `GetById("abc")` and `GetById("ABC")` must
behave the same whatever database backs the app. ChatGPT, Claude, Gemini, Grok and Copilot agreed in round 10;
Mistral was rate-limited.
- **Spark lowercases (`ToLowerInvariant`) every id at the one canonicalisation point** (D23 rule 3): `ids.NewAsync`,
  `ids.Parse`, the JSON converter and the route binder, and when a provider reads an id back (D23 rule 6). The whole
  string is lowered, collection prefix included, so `Accounts/X` becomes `accounts/x`.
- Int and Guid keys were canonical already (digits; Guid `N`). A natural key from a template or `IHasNaturalId` is
  lowered with the rest. There are no per-entity normalisers.
- **Casing that carries meaning never lives in an id.** A case-sensitive external key (a GitHub GraphQL node id, a
  commit message slug shown to users) is its own property, which is already the rule: never parse values out of an
  id. Ids display in lower case.
- Two keys that differ only in case are already **one** RavenDB document today, so lowercasing makes no new
  collision. It moves SQL to the same behaviour, so round 9's "natural ids unique ignoring case" needs no extra
  index: a binary key over lowercase values is unique ignoring case by construction.
- Every id the framework renders (URLs, JSON, client) is canonical, so the round trip is exact. A hand-typed
  `ABC` in a URL is lowered by the route binder and found on every provider.
- **SQL:** key columns and every FK column that points to one use a binary collation (PostgreSQL `"C"`, SQL Server
  `*_BIN2`); synchronize copies the key's collation onto the FK columns (SQL Server refuses an FK across collations).
  Equality and prefix scans then get an index seek, with no `text_pattern_ops`, no `citext` and no `LOWER()` index.
  Natural keys are `nvarchar(450)` (900 bytes, the clustered-key limit), and synchronize fails the build if a
  template can exceed it.
- **RavenDB conventions** (`SparkStoreConfiguration.ApplySparkConventions`, the one place, set before
  `Initialize()`): generated ids lowercase the prefix; `UseNaturalIds` lowers `GetId()`;
  `TransformTypeCollectionNameToDocumentIdPrefix` lowers the name (RoleStore and HiLo read it); the HiLo id from
  `ids.NewAsync` is lowered with its node tag (`people/1-a`). `FindCollectionName` is unchanged: it names the
  `@collection`, not the id. Debug builds and a conformance test reject storing an id that is not lower case.
- Code that builds an id prefix itself (`GetCollectionName(type) + "/"`) goes through `Id<T>` or the convention
  above. Prefix checks that already compare `OrdinalIgnoreCase` (`SoftDeleteRawDeleteGuard.cs:36`) keep working.
- **Collection collisions fail the model build (L1, round 10, unanimous).** Two entity classes whose collection
  names are equal ignoring case, or one class name in two namespaces, share one RavenDB collection today (RavenDB
  compares collection names ignoring case, and `FindCollectionName` ignores the namespace), and would share one
  SQL Server table name under its default collation. Building the model fails on such a pair, on every provider.
  Nothing checks this today.
- `LoadStartingWith` over natural keys is `LIKE @prefix ESCAPE '\'`, with `%`, `_` and `\` escaped; the prefix is
  lowered first.
- Conformance cases pin the canonical round trip, `GetById` with an upper-case variant (found on every provider), the
  rejection of a non-lowercase id when a **new** document is stored (a legacy RavenDB document reports its stored
  casing on save, S-ID1 G1), the prefix escaping, FK equality under the copied collation, and
  non-ASCII keys (`ToLowerInvariant` runs in .NET, so the providers never fold case themselves).
- Existing RavenDB data with mixed-case ids is **not re-keyed** (O15): RavenDB lookups ignore case and Spark lowers
  every id it reads. Spike S-ID4 proved it on a copy of the development `Coverage` database (§8.5).

**D23: the id model, `Id<T>`.** ChatGPT, Claude, Gemini, Grok, Copilot and Mistral reached this consensus over five
moderated rounds (2026-11-24). The owner accepted it ("probably good"). Rounds 6–9 checked it against the
commercial-framework comparison. They added the batch form of rule 7, the migrator duties of rule 11, rule 12
(hierarchical ids) and the case rules of D7.

A feature author writes only:

```csharp
public Id<Person> Id { get; set; }
public Id<Company>? CompanyId { get; set; }
public List<Id<Profession>> ProfessionIds { get; set; } = [];
```

1. **One generic readonly struct `Id<T>` on every provider**, compiled once per feature library. There are no
   per-entity id structs and no per-provider aliases.
2. **It wraps only the canonical string `"{collection}/{key}"`.** Equality, hash and `ToString` are ordinal string
   operations. The key is everything after the *first* `/`, so a hierarchical natural key may contain `/` (O14).
   - **Without an attribute the kind is Guid** (owner, 2026-11-24), as every generated id is today. Integer (HiLo) is
     an opt-in. A Guid id can't be counted or guessed, which the existence-hiding rules rely on, and needs no
     server coordination (`SparkDocumentStoreConventions.cs`: HiLo "serializes writes behind a cluster-wide
     operation and makes ids guessable in sequence"). New Guids stay random (v4, `Guid.NewGuid()`), not UUIDv7: a v7
     id carries its creation time into every URL, and creation order is what the audit fields are for (`IAuditable`,
     `CreatedAt`, in History). **S-ID2 measured no large difference (§8.4):** 100k EF inserts take the same time
     within noise on both databases. v7 only shrinks PostgreSQL's key index by 21%, so v4 stays.
   - The kind (Integer, Guid, Natural) is model metadata: an attribute gives the default, and the **app model** may
     override it, also for entities shipped in feature packages.
   - There is no process-global state, because several test hosts share one process.
3. **An id is canonicalised once, at creation, through the app model:** `ids.NewAsync<T>()`, `ids.Parse<T>(s)`, the
   model-scoped JSON converter and the route binder.
   - The model normalises: `"042"` becomes `"42"`, Guid `D` becomes `N`, and the whole id is lowered (D7).
   - There is no public constructor and no implicit conversion. `FromTrusted` is internal, for the generator and
     the providers.
   - `default` is empty, and a store rejects it.
4. **The collection prefix is fixed at compile time.** The canonical string is the format on the wire, in RLS, in
   audit and in live updates, on every provider. URLs may carry only the key.
5. **The id is known before `StoreAsync`.** `ids.NewAsync<T>()` draws from RavenDB's native HiLo
   (`GenerateNextIdForAsync`) or from EF's HiLo sequence, so references staged in the same session already carry the
   final id.
   - HiLo burns ranges on abandoned saves.
   - Natural-kind entities skip `NewAsync`: their key comes from the business key.
6. **RavenDB:**
   - A generated hidden `string? __DocumentId { get => Id.Value; set => Id = Id<T>.FromTrusted(value.ToLowerInvariant()); }`
     is the identity property, selected by `FindIdentityProperty`. RavenDB refuses a non-string identity member.
     There are no store events. The setter lowers what RavenDB hands back, so a legacy mixed-case id reads as its
     lowercase form (D7, O15). The member may be private (S-ID1 C2, R7).
   - The public `Id<T>` is `[JsonIgnore]` (generator-emitted), so the body never carries an `Id` field. A re-save of
     today's string-Id document is byte-identical (S-ID1 P2), and nothing needs a backfill patch.
   - Int ids are RavenDB's **native HiLo**, lowered (D7): `"people/1-a"` stays opaque, and the node tag is never
     stripped. On RavenDB, "Integer" just means HiLo-generated. Sorting by id is alphabetical there (`people/10-a`
     sorts before `people/2-a`), and that is documented.
   - The HiLo comes from **Spark's own** `AsyncMultiDatabaseHiLoIdGenerator`. `ApplySparkConventions` overrides
     `AsyncDocumentIdGenerator` (Guid ids), and from then on `store.HiLoIdGenerator` throws (S-ID1 C13).
   - The stored JSON is unchanged: references stay strings.
7. **Id lookups made by the framework itself** (detail load, reference resolution, row security, lookups) go through
   a provider hook: `Load` on RavenDB, key equality on EF. The commercial framework does the same: `Session.Load` on RavenDB, a
   key-equality expression on EF.
   - The hook has a batch form (`LoadMany`: one `IN` query on EF), so reference resolution is never N+1. D30 uses the
     same hook to name the referenced row in an FK-violation message.
   - **Only LINQ that an app author writes against `x.Id`** needs translation on RavenDB. **(b) won (S-ID1, §8.3):**
     an execution-time expression rewrite through a provider decorator.
     - It turns the entity's own `x.Id` into the identity member, so the client emits `id()`. This covers `==`,
       `!=`, `Contains`/`In` and `OrderBy`, and a projection selects `id() as …`.
     - `id()` matches the real document id, case-insensitively. It finds documents with no body `Id` (every
       document today), and a lowercase value finds a mixed-case legacy id (O15).
     - The same decorator lowers `coll.Contains(x.Member)` into `x.Member.In(coll)` for any element type. The RavenDB
       client rejects `Contains` even over `List<string>` (S-ID1 C4 control), which is why the apps write `.In()` by
       hand today (`AccountActions.cs:118`).
     - (a) was measured and rejected: RQL treats a body field `Id` as an ordinary field. Documents without it
       (all of production) are invisible to `where Id = …` (C6), and keeping it would need a backfill patch and a
       byte change on every re-save.
     - (c) is not needed.
8. **EF Core:**
   - Per-model value converters parse `"people/42"` into `int`/`bigint`/`Guid` columns, range-checked.
   - Int keys use **HiLo** by default; IDENTITY is a documented opt-in that breaks rule 5. EF's `UseHiLo` works on
     the converted `Id<T>` key on both databases and assigns the key at `Add`, before `SaveChanges` (S-ID2).
   - Guids are generated client-side, v4 on every provider (rule 2), in a `uniqueidentifier`/`uuid` column. A string
     key column costs 1.5× the pages on SQL Server and 1.9× the index on PostgreSQL (S-ID2).
   - No ValueComparer was needed (S-ID2: PK, FK, nullable FK, join entity, owned child table, `FindAsync`).
   - `Id<T>` implements `IComparable<Id<T>>` (ordinal). InMemory sorts by the CLR value and throws without it.
     **Order by id differs per provider:** numeric for HiLo, `uniqueidentifier` byte order on SQL Server, alphabetical
     on RavenDB. It is documented as unspecified for Guid ids.
   - `ids.Contains(x.Id)` translates on every provider. EF 11 on SQL Server sends one parameter per id (2000 ids =
     2000 parameters) and switches to `OPENJSON` past the 2100 limit; PostgreSQL binds one array, `= ANY(@ids)`.
9. **One framework-owned JSON setup** covers every options instance: Newtonsoft for RavenDB documents, and
   System.Text.Json for MVC and SignalR. Writing an `Id<T>` needs no model; reading on the web validates through the
   model.
10. **Not supported:**
    - composite keys
    - id obfuscation (the commercial framework offers it as an option)
11. **No cross-provider migration tooling** (owner, 2026-11-24). Moving between database systems is a one-shot job:
    the developer writes their own migrator, which maps the first database's ids to the second's after inserting into
    it. The same entity has different ids per provider (`"people/1-A"` on RavenDB, `"people/1"` on SQL).
    - The docs list that migrator's duties: seed the EF HiLo sequences above the migrated maximum, map RavenDB's
      `"1-A"` ids to the new keys, and rewrite parent keys embedded in natural ids (rule 12).
12. **Hierarchical natural ids** (O14, rounds 6–9; accepted by the owner 2026-11-24, with the code
    source-generated). About 75 id builders nest other ids, for example `"commits/{provider}/{repositoryId}/{sha}"`.
    - They stay one string primary key (Natural kind). There is no composite key and no hidden surrogate key. Ids are
      immutable, so an embedded parent key can never go stale.
    - **The entity declares its id template once**, as an attribute, for example
      `[IdTemplate("commits/{Provider}/{RepositoryId}/{Sha}")]` plus a variant such as
      `"commits/{Provider}/{RepositoryId}/pr/{PullRequestNumber}/{Sha}"`. The source generator emits both directions
      from it, so the builder and the parser can't drift apart:
      - the id builder (`ids.New<Commit>(provider, repositoryId, sha)`), replacing hand-written `DocumentId(...)`
        methods
      - **a get-only property per segment that is another entity's id** (`Id<Repository> RepositoryId`), read back
        from the id. D26 gives it a real FK and a delete rule, so the relational model sees the link and no FK hides
        inside a string. On SQL it is stored as an ordinary column written on insert (how it is persisted follows
        O13); on RavenDB it needs no field, because a prefix scan finds the children.
      - get-only properties for the other segments the app reads (`Sha`, `PullRequestNumber`)
    - **The generator refuses a template it can't parse back unambiguously:** at most one segment may contain `/`, and
      only the last one; variants must differ in a literal segment (`pr`). The parse works on the lowercase id (D7), so
      it never compares case.
    - `LoadStartingWithAsync` stays an escaped `LIKE` prefix on the key (D7). The generator also emits the prefix for
      "all children of this parent", so no prefix is concatenated by hand.
    - **Hand-written id parsers are replaced by the generated properties.** The inventory:
      - the `LastIndexOf('/')` parsers `ContributionHandler.cs:707`, `WebhookEndpointManager.cs:263` and
        `MergeQueue.RepositorySuffix`
      - `Commit.TryParseRepository` (`Commit.cs:194`). It compares `parts[0] != "Commits"` case-sensitively, so under
        the lowercase rule it would return false for every commit and fail closed. It has failed closed before, when
        the forge segment was added (its remarks, `Commit.cs:183-186`).
      - any other `Split('/')` over an id, found with an analyzer that flags string operations on `Id<T>.Value`

The full consensus and the spike details are in plan §3, rounds 4 and 6–9.

**D24 (round 3): model synchronize generates the EF migration files** (option C).
- The app's schema follows its model through ordinary EF migrations that the developer reviews and commits.
- **The framework's own tables** (outbox, leases, atomic values, reservations, revisions, change log) live in their
  own schema and are versioned separately from the app's. The commercial framework keeps its own tables the same way, with
  hand-written versioned scripts.
- **How (S3, §8.8):** synchronize builds the `DbContext` from the model, compares it with the compiled snapshot
  (`HasPendingModelChanges()`), and on a change scaffolds through EF's public design-time services on the same
  context. The design-time services replace `IMigrationsAssembly` with `FreshSnapshotMigrationsAssembly`, which hands
  the scaffolder its own instance of the snapshot class. Without it, the check has already finalized the shared
  snapshot and EF 11 scaffolds a migration that recreates every table (dotnet/efcore#36016). The decorator goes
  once EF ships the fix (dotnet/efcore#39784), as does the refusal below, which stays as a guard until then. It writes the migration, designer
  and snapshot files, prints the operations and EF's warnings, and refuses a migration that recreates a table the
  snapshot already has or splits an attribute id into a drop and an add. A queryable string attribute has a maximum
  length. The design-time code lives in `MintPlayer.Spark.Relational.Design`, a development dependency that stays
  in `bin` and out of every publish (O17, §8.9).

**D33 (owner, 2026-11-24): when migrations are generated and applied.** RavenDB apps need no schema step today; SQL
apps do. The switches and the startup order are core's; each provider implements `ISparkSchemaMigrator` (D34).
Spark keeps the RavenDB experience by default and offers two stricter modes from the same app binary, with
no `dotnet ef` tool and no design-time `DbContext` factory (Spark builds the `DbContext` from its model, which the
EF tools would have to rebuild).
- **Generated in development only.** `--spark-synchronize-model` writes the EF migration for a model change (D24),
  and the developer reviews and commits it with the change, as the model JSON is today. Nothing generates a
  migration at run time. A test per app (with the D31 tests) fails when the model has changes no migration covers
  (EF's `HasPendingModelChanges()`), so a forgotten migration fails CI rather than startup.
- **Default: applied at startup** (the owner's lean, and what Spark already does). `SparkMigrationRunner` already
  applies the data migrations (`ISparkMigration`) once at startup and makes the other instances wait
  (`SparkMigrationRunner.cs:9-11`); RavenDB index deployment runs at startup too. The SQL providers apply, in order:
  1. the framework schema (D24's own tables, its own history table)
  2. the app's EF migrations
  3. the data migrations, as today

  **EF's own migration lock is not enough (S19, §8.7).** On SQL Server it serializes instances on an existing
  database, but on PostgreSQL (Npgsql 11.0.0) it is held only per migration, so two instances both apply the later
  migrations (npgsql/efcore.pg#4162). The PostgreSQL provider therefore registers `AdvisoryLockHistoryRepository`
  (`ReplaceService<IHistoryRepository, …>`), which holds a session advisory lock for the whole `Migrate()` run as
  `sp_getapplock` does. With it, EF's lock alone applies each migration once on both databases (S19 1d). It goes once
  Npgsql ships the fix (npgsql/efcore.pg#4163). On both databases instances still race on `CREATE DATABASE`, and EF's
  lock does not cover the data migrations. So the core startup sequence takes **Spark's
  own session-level guard** around all three steps *and* the database creation: the migrator contract's
  `AcquireStartupGuardAsync`, which the SQL providers implement with `sp_getapplock @LockOwner='Session'` in `master`
  and `pg_advisory_lock` in the `postgres` maintenance database, keyed `spark:migrations:{database}`; RavenDB keeps
  its compare-exchange lease. Each migration still runs in its own transaction (EF 10+): a failure halfway leaves
  the earlier migrations applied, rolls the failing one back as a whole, and the next start resumes from there. EF
  refuses to apply while the model has changes no migration covers (`PendingModelChangesWarning`), which backs up
  the CI test.
- **Mode 2, a separate step:** `<app> --spark-migrate` applies all three and exits. It suits a pipeline step or an
  init container run with a login that may change the schema, while the app itself runs with
  `Spark:Database:ApplyMigrations=false` and a login without DDL rights. This is what an EF migration bundle does,
  and the app binary already is one.
- **Mode 3, a script for a DBA:** `<app> --spark-migration-script <file> [--from <migration>] [--to <migration>]` writes one
  idempotent SQL script (`--idempotent` semantics: it checks the history tables) covering the framework schema and the
  app's migrations in the right order. Data migrations are C# and can't be scripted: they run at the next startup or
  through `--spark-migrate`, and need only data rights.
- **With `ApplyMigrations=false`, startup checks and refuses:** pending migrations stop the app with a message that
  names both commands, instead of running against an older schema.
- Why not scripts as the default: the review a script offers already happens on the generated migration file in the
  pull request; scripts add a manual step to every deploy; and the three production apps here deploy as containers that
  migrate themselves at startup (the RavenDB precedent `M_202609210900_ForgeQualifiedDocumentIds` and its
  healthcheck `start_period`). Microsoft recommends applying migrations at deploy time rather than at startup, mainly
  because of concurrent instances (which the lock now covers) and DDL rights at run time (which mode 2 covers).

**D25 (round 3): AsDetail collections are relational child tables.**
- Child rows already have ids (`ValueObjectKeyGenerator` emits `string Id`).
- A child table is keyed by (owner id, child id), with an FK to the owner and cascade delete.
- On RavenDB they stay embedded in the parent document.

**D26 (round 3): references are real foreign keys, with no navigation properties.**
- `Id<T>` and `Id<T>?` are FK columns.
- `List<Id<T>>` maps to a framework-owned join table, which EF sees as a join entity, never as an EF collection.
- The `Id<T>` property is itself the reference attribute, so no FK column surfaces twice in the UI model. That is
  the commercial framework's "hide the FK scalars" rule, met by construction. The child tables' owner keys and the join tables'
  columns are framework-only.
- **Reference integrity** (a delete guard, and what happens on rename) on every provider is a **separate issue** with
  its own investigation.

**D27 (round 3): a typed contract.** `ExistsAsync`, `Delete` and `GetVersionsAsync` take `Id<T>` (about 89 call
sites). With D23 the type is inferred: `Delete(person.CompanyId)` can only delete a `Company`.

**D30 (the commercial-framework comparison): database constraint errors become validation messages.**
- The dialects translate unique, foreign-key, not-null and length violations into Spark's validation error, naming
  the attribute:
  - SQL Server error numbers 2601, 2627, 547, 515, 2628 and 8152
  - PostgreSQL SQLSTATEs 23505, 23503, 23502 and 22001
- A raw database exception never reaches the client.
- The commercial framework's SQL Server package does this.

**Full-text search: `LIKE` first, an FTS dialect opt-in later** (the commercial-framework comparison, owner 2026-11-24).
- The commercial framework runs production apps on SQL with `LIKE` only. It has no `CONTAINS`, `FREETEXT` or `tsvector` anywhere.
- So `Search()` on relational databases translates to `LIKE` (escaped). S10 moves off the critical path.
- **Search ignores case on both databases** (found 2026-11-24). SQL Server's `LIKE` follows the column's collation,
  which is case-insensitive for ordinary columns (only key and FK columns are binary, D7). PostgreSQL's `LIKE` is
  case-sensitive under every collation, so its dialect emits `ILIKE`. A D31 test pins both.

**D31 (owner, 2026-11-24): tests pin the database behaviour each decision relies on.** Every decision in this PRD
assumes something about SQL Server or PostgreSQL. A test per assumption, named after the decision it protects, runs
against the real database, so a new server version or a changed setting fails a named test instead of corrupting
data quietly. They live in the relational test project, one class per dialect (`SqlServerBehaviourTests`,
`PostgreSqlBehaviourTests`), and run in CI on the service containers that S17 already needs (§9 CI budget). They
test the databases, not Spark; the conformance suite tests Spark on top of them. The first set:

| Decision | Pinned behaviour |
|---|---|
| D6, O10 | A sequence value read by a cursor skips a row that commits later (the failure that ruled sequences out); reading below `MIN_ACTIVE_ROWVERSION()` / `pg_snapshot_xmin` does not skip it with two interleaved transactions (S14) |
| D6, O10 | `rowversion` changes on every update; the PostgreSQL trigger's `xid8` does too; `@@DBTS` and `pg_current_xact_id()` move on any commit |
| D7 | Under the binary collation `'abc' <> 'ABC'` and the key index serves an equality and a `LIKE 'prefix%'` seek; SQL Server refuses an FK between columns of different collations; a 451-character `nvarchar` key is refused (900-byte limit) |
| D7 | `LIKE ... ESCAPE '\'` treats escaped `%`, `_` and `\` literally |
| Search | `LIKE` on an ordinary SQL Server column ignores case; PostgreSQL's `LIKE` does not and `ILIKE` does |
| D5 | `datetimeoffset` and `timestamptz` compare instants: 12:00+02:00 equals 10:00Z |
| D3 | `UPDATE ... WHERE Version = @v` reports 0 rows on a stale version |
| D30 | The error numbers and SQLSTATEs D30 maps are the ones the servers raise for unique, FK, not-null and length violations |
| O11 | `ADD COLUMN ... NOT NULL DEFAULT` fills existing rows with the default; a nullable column added later is `NULL` |
| O12 | `OUTPUT` / `RETURNING` hands back the updated rows inside the same transaction, and a rollback discards both |
| O13 | EF with `PreferFieldDuringConstruction` reads the getter on save, so a change to `FirstName` rewrites `FullName` |
| S12 | `jsonb` reorders keys and collapses duplicates (harmless because the dirty check diffs the load snapshot; the test keeps that assumption visible) |
| D32 | A plain view returns a committed write at once (no staleness); a view over an AsDetail child table returns a row per child |

**D32 (owner, 2026-11-24): `[GenerateIndex]` renders an SQL view and indexes from the same source generator.**
- **How a view compares to a RavenDB index.** Both are a named projection defined in code and queried like a
  collection (`Query<T, TIndex>`), with computed fields. The differences:
  - A RavenDB index is computed ahead and stored, and updated **asynchronously**, so results can be stale
    (`WaitForNonStaleResults`). A plain SQL view is a stored query, computed when read, so it is always consistent and
    `WaitForConsistencyAsync` is a no-op.
  - Speed comes from indexes on the underlying table columns, not from the view. Materialised forms are not chosen:
    SQL Server indexed views have heavy restrictions (`SCHEMABINDING`, no outer joins, deterministic expressions
    only), and PostgreSQL's materialised views need a manual `REFRESH`.
  - `[Search]` analyzers don't exist; search is `LIKE`/`ILIKE` (above).
  - A RavenDB index can index inside a collection. A view over an AsDetail child table (D25) returns a row per child,
    so a generated view never joins a child table; an index over child values becomes an `EXISTS` filter on the query.
  - Many generated indexes need no view at all on SQL. Person's `FullName` is already a stored column (O13), so its
    `[GenerateIndex]` becomes indexes on the filtered and sorted columns. A view is generated only for what the table
    lacks, such as a value read from a referenced entity (a join).
- **One package, which checks the references** (the owner's preference, already the S5 plan above). The generator
  emits the neutral view declaration always, the RavenDB index only when RavenDB's types are referenced (the
  existing check, `GenerateIndexGenerator.cs:155`), and the EF mapping (`ToView`, column indexes) only when the
  relational provider's types are referenced. A RavenDB-only app compiles no EF code.
- **The SQL text is not written by the generator.** A compiler plugin can't run EF's translator, and rewriting C# into
  two SQL dialects by hand would duplicate it. The generator emits the declaration as a LINQ expression; model
  synchronize has EF translate it per dialect and writes `CREATE VIEW` and `CREATE INDEX` into the generated EF
  migration (D24), where the developer reviews the SQL.
- **S5 (§8.12) proved it** with one declaration: a computed column, a value read through a reference, and a count
  over a join table. It rendered on SQL Server, PostgreSQL, InMemory and RavenDB. The join form is `Load<T>(id)`, not
  LINQ `join`. Spark's differ writes `CREATE VIEW`/`DROP VIEW` into the migration, and recreates a view whenever a
  table it reads changes.

**Unknown fields.** A row has only the mapped columns, so undeclared fields are lost, as on InMemory. RavenDB keeps
them (§8.1 finding 6). The conformance suite pins each provider's behaviour. **Absent field versus `NULL`** (D16): a
column always exists, so "absent" can only be `NULL`. **Decided in grilling round 5 (O11, 2026-11-24): D16 is
Spark's meaning on every provider, extended to nullable members.**
- An absent field means the CLR default: `default` for a non-nullable member (the existing D16 rewrite), `null` for
  a nullable one. The RavenDB provider rewrites `x.N == null` into "null or not present" (RQL `not exists(...)`) and
  `!= null` into its negation. Today `== null` misses the absent rows and `!= null` includes them
  (`AbsentVersusNullFieldTests`).
- SQL gets there by construction: the generated migration adds a non-nullable column with the CLR default, and a
  nullable one as `NULL`. No data is written on any provider.
- If S12 shows that "not present" can't be expressed through the RavenDB LINQ provider, those members alone fall
  back to a synchronize-generated backfill patch.
- Sorting and distinct values (the `< none >` entry) are conformance cases too: RavenDB sorts an absent field
  apart from the default, and the result must match SQL's.
- The generator warns on an initializer on a persisted value-type property (`int Priority { get; set; } = 5`): a
  document without the field shows 5 but is queried as 0.

**JSON columns on PostgreSQL** use `jsonb`, which normalises the text (keys reordered, whitespace changed, duplicate
keys collapsed; S12). That is harmless: the dirty check diffs the snapshot taken at load, not the stored text.

**Reservations** (`ISparkSession.Reserve/Release`) join the session's `SaveChanges` transaction. That replaces
`UserStore.cs:206`'s cluster-wide transaction.

**Lookup references:** a dynamic lookup gets its own table. Entities store the key, and labels are resolved after
load. **There is a gap on every provider:** `LookupReferenceService.AddValueAsync`/`UpdateValueAsync`/`DeleteValueAsync`
load, modify and store with no `expectedVersion` (`:136-154`), so two concurrent edits lose one of them. The fix
(store with `GetVersion`, plus a conformance case) belongs in this work.

## 4. RavenDB features and their relational equivalents

### 4.1 The equivalents table

**Recommended** is what Spark builds. **Not chosen** says why the obvious alternatives lose. Every row gets a neutral
contract (or reuses one), implemented by RavenDB, InMemory and `Relational`.

| RavenDB feature | Spark contract | Recommended relational mechanism (all three) | Database specifics | Not chosen, and why |
|---|---|---|---|---|
| Documents, collections, `@metadata` | `ISparkSession` | **a table per collection, mapped by EF Core** (D1, round 3); TPH for derived types; JSON columns for core value types | `json`/`nvarchar(max)` vs `jsonb` | the hybrid `Documents` table (D1 before round 3: proven by S1/S2/S12, reversed by the owner) |
| Static map index | neutral view declaration (S5) | indexes on the entity's columns; an SQL view for projections and computed fields | SQL Server: filtered index. PG: partial or expression index. | materialised views (manual refresh on PostgreSQL) |
| Map-reduce index (moderation only) | neutral aggregate declaration (S16) | an **aggregate table updated in the same transaction** as the source document (`UPDATE … SET Sum = Sum + @d`, upsert). The hook needs the old row to compute `@d`, so it loads the old row inside the transaction. Set-based `DeleteWhere`/`UpdateWhere` on an aggregated collection is refused (*round 1*). | upsert: `MERGE` / `INSERT … ON CONFLICT` / `INSERT … ON DUPLICATE KEY UPDATE` | SQL Server indexed views (SQL Server-only, many restrictions); recomputing on read (slow) |
| `WaitForNonStaleResults` | `WaitForConsistencyAsync` | a no-op: commit is consistent, and so are the aggregate tables | — | — |
| `Search()` full-text | FullTextSearch capability | **`LIKE` (escaped) first**, as the commercial framework does on SQL; a full-text dialect is an opt-in later (S10) | SQL Server: `CONTAINS`/`FREETEXT` when installed. PG: `tsvector` + GIN, `pg_trgm` for fuzzy. MariaDB: InnoDB `FULLTEXT` on a generated column. | an external engine (OpenSearch) is the search library's own provider, not the store's |
| Subscriptions | Subscriptions capability (S7/S14) | **a durable per-subscription cursor over the `ChangeStamp`** below the safe horizon (D6; per table or one change log, open O10). That delivers each document's *current state*, backlog included, as a RavenDB subscription does. A **`Tombstones` table** (Id, Collection, ChangeStamp) carries deletes. Matching uses the subscription's neutral predicate (an expression over `T`, replacing RQL). Delivery is at-least-once, and the batch is acked when the handler returns. | PG: `LISTEN/NOTIFY` as an optional wake-up hint | a change-only outbox (*corrected in round 1*: a new subscription would miss the backlog); SQL Server Query Notifications/`SqlDependency` (not durable); CDC (needs SQL Agent, so not on Express); PG logical decoding (operations-heavy) |
| `WaitForFree` single worker | `ISparkStoreLock` | a lease (below) held by the active worker; messaging's claim stays a versioned `UPDATE` | — | — |
| Compare-exchange | **`ISparkAtomicValues`** (S15): get, compare-and-swap, delete; the neutral compare-exchange | an **`AtomicValues` table** (Key PK, Value, Version, ExpiresAt NULL), written with a versioned `UPDATE` / insert-only `INSERT` | optional fast path for short locks: `sp_getapplock`, `pg_advisory_lock` | — |
| ↳ lease / lock (migrations, messaging, dashboards) | `ISparkStoreLock` on `ISparkAtomicValues` | a value with `ExpiresAt`, renewed by compare-and-swap | — | session-scoped database locks alone: they are released on connection loss and fight connection pooling |
| ↳ never-expiring claim (**cron**) | `ISparkAtomicValues` directly | *corrected in round 1:* cron's `TryClaimOccurrenceAsync` (`SparkCronScheduler.cs:236-274`) is a compare-and-swap of "latest claimed occurrence", with poison handling. **An expiring lease would let a second host fire the same occurrence.** | — | a lease |
| Compare-exchange uniqueness | `ISparkSession.Reserve/Release` (S6) | a **`Reservations` table** (Key PK, OwnerId) written **in the same transaction** as the document. This covers `UserStore.cs:206`'s cluster-wide transaction for free. | — | a unique index on a generated JSON column (works, but per field, and the takeover-when-owner-missing rule needs code anyway) |
| Cluster-wide transaction | — | a plain database transaction | — | — |
| Revisions | Revisions capability (S8) | **open since round 3 (O12):** a framework `Revisions` table holding the row as JSON, or SQL Server temporal tables (none on PostgreSQL; the commercial framework has no revisions at all). The pre-round-3 choice: a **`Revisions` table** (Id, Version, Collection, Data, ChangedAt, Deleted). **Every write path writes it** through one internal code path: session commit, `UpdateWhere`/`PatchAppend`, sweepers (*round 1*: RavenDB writes a revision on every write, set-based ones included). The revision id is the opaque `Version` (history's `RevertAsync` takes it as a string, `ISparkHistory.cs:38`). Per-collection retention is enforced by a sweeper. | — | temporal tables: SQL Server and MariaDB have them, PostgreSQL doesn't, and they can't do per-collection retention or `PurgeOnDelete` |
| `@expires` | `ISparkSession.SetExpiry` + `ISparkStoreExpiration` | the `ExpiresAt` column + an in-process sweeper | — | `pg_cron`, the MariaDB event scheduler (they depend on extensions or server configuration) |
| `@refresh` | — | not needed: it is no longer used (§2.6) | — | — |
| Counters | a field, or `ISparkSession.Increment` | an atomic `UPDATE … SET n = n + 1` on a `Counters` table | — | — |
| Time series | dashboards' own samples store (S9) | a `Samples` table, a rollup job and retention | TimescaleDB is optional on PostgreSQL | — |
| Attachments | Attachments capability (S11) | off on relational at first: the files library uses its pluggable blob stores (FileSystem, S3, AzureBlob) | later: `varbinary(max)` / `bytea` / `LONGBLOB` side table | blobs in the database by default (backup size, memory) |
| `Patch`, `PatchByQuery`, `DeleteByQuery` | `UpdateWhereAsync`/`DeleteWhereAsync` and **`PatchAppend`** (S6), or load-modify-save | load-modify-save in batches where a document's own rules must run; set-based where they don't. **Concurrent appends** (messaging's `Advanced.Patch` list add and `Defer(PatchCommandData)`: `MessageProgress.cs:50`, `MessageProcessor.cs:607`, `MessageRetrySweeper.cs:142-160`) use the atomic `PatchAppend`, because load-modify-save would lose updates or 409 under contention (*round 1*). | `JSON_MODIFY(…, 'append $.x', …)` / `jsonb_set(… \|\| …)` | — |
| `StreamAsync` | `ISparkSession.StreamAsync` | keyset paging over the table's key | — | server cursors (they hold a connection) |
| `Lazily` | — | plain batched calls | — | — |
| Includes | `includePaths` (exists) | a second batched load by ids (`IN`) | — | — |
| Change vector, optimistic concurrency | `expectedVersion` (exists) | `Version bigint` per table (D3) | — | `rowversion`/`xmin` (D3) |
| `@last-modified` order | — | `ChangeStamp` below the safe horizon (D6) | — | timestamps (ties and clock skew) |
| Data-protection keys | `ISparkDataProtectionKeyStore` | a `DataProtectionKeys` table | — | — |
| ETL (replication) | — | none: **RavenDB-only**, refused by SPARK060 | — | — |
| Index deployment, `RavenTestDriver` | a neutral test driver (S17) | schema created by the provider; tests run against a service container or a local instance | — | — |

### 4.2 Capabilities per database

| Capability | SQL Server | PostgreSQL | Planned |
|---|---|---|---|
| ChangeNotifications | in process + `ChangeStamp` cursor | in process + `ChangeStamp` cursor (`NOTIFY` wake-up) | yes |
| Expiration | sweeper | sweeper | yes |
| Indexes (neutral views) | yes | yes | S5, which gates M3 (round 1) |
| Subscriptions | `ChangeStamp` cursor + tombstones | `ChangeStamp` cursor + tombstones | if S7/S14 hold |
| Revisions | revisions table or temporal tables (O12) | revisions table | if S8 holds |
| FullTextSearch | `LIKE`; FTS opt-in later | `LIKE`; `tsvector` opt-in later | S10, off the critical path |
| Attachments | off at first | off at first | S11 |

## 5. Feature tiers

Each port moves a library from `IAsyncDocumentSession` to `ISparkSession` and the neutral contracts, and swaps its
`RequireStore(provider: RavenDb)` for a capability requirement. Every port keeps working on RavenDB, and on InMemory
where the capability exists.

| Tier | Libraries | Needs |
|---|---|---|
| 0: works once the provider exists | core (CRUD, queries, row security, `security.json`, translations, live updates in process), logging. **Not** `Query<T,TIndex>` views: they need S5 (round 1; InMemory throws on them too, `InMemorySparkSession.cs:64`). | the provider; S5 for views |
| 1: port to `ISparkSession` | settings, boards, notifications, apps, organizations, all_features, templates | replace Patch with load-modify-save; `WaitForConsistencyAsync` |
| 2: port + one new neutral contract | mail, mcp (expiry); cron (atomic value), migrations (lease); contributions (streaming); authorization user store (reservations); search (FullTextSearch); files (attachments off; revisions) | §4.1, S6 |
| 3: port + capability | history, soft_delete (Revisions); subscription_worker, messaging (Subscriptions, lease, claim); moderation (aggregate tables + expiry + streaming) | S7/S14, S8, S16 |
| 4: large, **in scope** (O2) | identity_provider (71 files: reservations, expiry, set-based cascade, `RawQuery`, write-only time series); dashboards (time series, RQL aggregation, lease) | S9 for dashboards |
| test infrastructure | testing (`SparkTestDriver` and the RavenDB-specific helpers) | S17 |
| RavenDB only | replication (RavenDB ETL, RavenDB-to-RavenDB by design) | refused by SPARK060 |

**Reality check on Tier 4.** §2.6 makes identity_provider look mostly mechanical. Every RavenDB feature it uses has a
row in §4.1, and none of them is hard (reservations, expiry, one `UPDATE` cascade, two map indexes). The cost is
volume: 71 files. Dashboards' rollups are the only real design work there.

## 6. Spikes, milestones, grilling rounds

They are in [`relational_providers_plan.md`](relational_providers_plan.md).

## 7. Risks

1. **MariaDB has no first-party EF Core 11 provider** (§2.7). The only EF 11 option is a young community fork, and
   it translates five of S13's shapes into wrong rows. **Resolved by deferring MariaDB** (O6). It can return when
   either:
   - a maintained EF Core provider translates S13's shapes correctly, or
   - the owner accepts Spark's own translation fixes (formerly spike S13b).

   The dialect seam (D2), D5 and the wrong-rows conformance guard are kept so that return is cheap.
2. **Silent wrong results** are the failure mode to design against, on every provider. A translation layer that
   doesn't know a shape must throw.
3. **Pull-request size:** one PR (O1), so the milestones must keep RavenDB green at every commit.
4. **CI budget:** two database service containers in a run already capped at 20 concurrent jobs (CLAUDE.md). They
   join existing job groups (plan M8).

## 8. Spike results and decision log

**Since round 3, these spikes are history for the storage model:** they proved the hybrid document table that the
owner then reversed (D1). What still holds for EF Core table-per-entity:
- PostgreSQL's null order (D4)
- the `DateTimeOffset` findings (D5)
- the version-statement shape (D3)
- the D16 rewrite of absent members
- MariaDB's wrong rows (§7 risk 1)

### 8.1 S1 + S2: mapping and query translation (2026-11-19)

**Setup.**
- Code: `docs/sql_server_spike/` (its README says how to rerun it). EF Core 11.0.0 and Microsoft.Data.SqlClient
  7.1.2.
- Servers: the owner's SQL Server 2025 (`localhost\MSSQLSERVER01`, compat 170) and 2022 (`localhost`, compat 160).
- The entity copies HR's `Person` (`apps/HR/HR.Library/Entities/Person.cs`):
  - the get-only persisted `FullName`
  - the nested `Address`
  - the `CarreerJob` collection
  - the `List<string>` multi-reference
  - `DateOnly?`
  - plus a `DateTimeOffset?` and a string-stored enum
- 15 query shapes, built the way `QueryExecutor` builds them:
  - equality, null checks, `ids.Contains(Id)` (`In`), id prefix
  - nested member, enum, `DateOnly` and `DateTimeOffset` comparisons
  - `Contains` search
  - `AnyContains` (:2368) over the primitive list, `Any` over the complex collection, captured-array `Any`
  - the get-only property
  - nested `OrderBy` with `Skip`/`Take` (:438, :1925)
- Round 3 adds three D16 cases against a legacy document `{"FirstName":"Old","Legacy":"keep me"}`, which lacks
  `LastName`, `Status` and `Jobs`.

**Three mappings tried:**

| | C1: relational (scalars as columns, nested values `ToJson`) | C2: `Doc { Id, Collection, Data }`, `Data` = the entity as one complex `ToJson` column, EF serialises | C3 / round 3: C2's query model, but **Newtonsoft serialises**, `Raw` = computed alias of `Data` |
|---|---|---|---|
| Query shapes translated server-side | 14/15 | 14/15 | **15/15 + 3 D16 cases**, on 2025 and 2022 |
| Get-only `FullName` (persisted by RavenDB, `[Search]` in HR) | ✗ not mappable | ✗ `No backing field could be found … does not have a setter` | ✓ `JSON_VALUE([d].[Data], N'$.FullName') = N'Ann Smith'` |
| `TranslatedString` (`Dictionary`, `[JsonConverter]`) | ✗ | ✗ `Complex type … has no properties defined` | ✓ written by its converter; querying it is a JSON path (S2b) |
| Same JSON as InMemory/RavenDB | ✗ | ✗ (EF's own serialiser ignores `[JsonConverter]`s) | ✓ `InMemoryJson.Settings` |
| `rowversion` concurrency | ✓ stale write → `DbUpdateConcurrencyException` | ✓ same | own SQL (`WHERE Version = @v`), S4 |
| Schema evolution | migrations per model change | none (one JSON column) | none |

**Generated SQL (round 3).** Every shape compiles to one statement, with no client evaluation. On 2025:

- nested member: `JSON_QUERY([d].[Data], '$.Address') IS NOT NULL AND JSON_VALUE([d].[Data], '$.Address.City' RETURNING nvarchar(max)) = N'Ghent'`
- `DateTimeOffset`: `JSON_VALUE([d].[Data], '$.Hired' RETURNING datetimeoffset) > @hiredAfter`. This compares
  instants, so `09:00+02:00` correctly falls before `08:00Z`.
- `AnyContains`: `JSON_CONTAINS(JSON_QUERY([d].[Data], '$.Professions'), N'professions/2') = 1`
- `Any` over the complex collection: `EXISTS (SELECT 1 FROM OPENJSON([d].[Data], '$.Jobs') WITH ([ProfessionId] nvarchar(max) '$.ProfessionId') AS [j] WHERE …)`
- paging: `ORDER BY JSON_VALUE(…'$.Address.City'…) OFFSET @p ROWS FETCH NEXT @p1 ROWS ONLY`

On 2022, EF emits `CAST(JSON_VALUE(…) AS datetimeoffset)` and `… IN (SELECT [value] FROM OPENJSON(…))` instead, with
the same rows.

**Findings that shape the provider:**

1. **EF cannot be the serialiser.** It can't map get-only persisted properties, dictionaries or `[JsonConverter]`
   types, and it would give SQL Server a different JSON shape from the other two providers. Shadow properties on
   complex types are unsupported (`Shadow properties are not supported on complex types`, efcore #35613), so a
   "shadow twin" for a get-only property is impossible too.
2. **Do not map a raw string property onto the same column.** EF accepts the model (round 2, e), but then
   type-maps the column as `nvarchar(max)`:
   - on 2022, `Address != null` becomes `JSON_VALUE(…'$.Address') IS NOT NULL`, which is always false (0 rows)
   - on 2025, it is invalid SQL (`Incorrect syntax near 'json'`)
   - `JSON_CONTAINS` fails (`Argument data type nvarchar(max) is invalid`)

   A **non-persisted computed alias** (`[Raw] AS CAST([Data] AS nvarchar(max))` on 2025, `[Raw] AS [Data]` on 2022)
   keeps both mappings correct, costs no storage, and is what `SELECT [d].[Raw]` reads.
3. **Members outside EF's query model are read by path.** The provider's rewriter, which moves `x => …` onto
   `d => …d.Data…`, turns them into a built-in `JSON_VALUE` DbFunction over `Raw`. This covers get-only properties
   and, by the same route, any member EF can't map. String members are measured; typed members (`RETURNING`, or
   `CAST` on 2022) are S2b.
4. **The query model must mirror the serialiser settings.** With Newtonsoft's default (enums as numbers) and the
   query model's string conversion, `Status == Retired` silently returned 0 rows. With `StringEnumConverter` (as
   `InMemoryJson.Settings:17`), it returned 1. The provider derives both from one place.
5. **D16 on SQL Server is a rewrite, as on RavenDB.** EF drops `(Status?)p.Status == null` because the query model
   says non-nullable: the SQL is unchanged, and the absent `Status` is not matched. The SQL rewrite of
   `m == default` is `m == default || JSON_VALUE(Raw, '$.m') IS NULL`. It matched the legacy document
   (`[Ann, Cleo, Old]`), and `!Jobs.Any()` matches an absent `Jobs` as well (`NOT EXISTS (… OPENJSON …)`).
6. **Unknown fields.** Newtonsoft drops `"Legacy"` on a load → save. That matches InMemory, and RavenDB keeps such
   fields (memory note "Raven KEEPS undeclared fields"), so it is a provider difference the conformance suite should
   pin, decided in S3.

**Decisions:**
- **D1 confirmed and refined** (§3). Owner sign-off pending.
- **O4 answered by evidence:** support 2022 and 2025. EF picks the SQL per compat level (`UseCompatibilityLevel`),
  and the only provider-side difference is the `Raw` alias expression, so supporting 2022 is close to free. 2019
  (compat 150) is untested.

**Still open after S1/S2, carried as S2b:**
- Run the real `QueryExecutor` end to end on the prototype session, instead of hand-built shapes: the row filter
  (:1055), `In` (:664), search pushdown (:2155).
- Generate the EF query model from arbitrary Spark entity types: which member types to leave to `JSON_VALUE`
  (`Color`, `TranslatedString`, `object`, polymorphic members).
- Typed path reads (`RETURNING int/datetimeoffset` on 2025, `CAST` on 2022).
- Promoted computed columns plus indexes for `IndexedFields`, measured on a few thousand rows.
- *(added 2026-11-24, from S12/S13's SQL)* The rewriter must map `x.Id` onto the key column, not onto
  `Data->>'Id'`. All three spikes filtered `In` and the id prefix on the JSON copy of the id, which no primary key
  index serves.

### 8.2 S12 + S13: the hybrid on PostgreSQL and MariaDB (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/` (its README says how to rerun it). This is round 3 of §8.1 ported to two databases,
  with the same `Person` model, seed, 15 query shapes and 3 D16 cases.
- Throwaway containers on Docker Desktop: `postgres:18` and `mariadb:11.8`.
- PostgreSQL ran on Npgsql 11.0.0 with `SetPostgresVersion(18, 0)`. MariaDB ran on Microting 11.0.0 with
  `MariaDbServerVersion(11.8)` and `EnablePrimitiveCollectionsSupport()`.
- `Version` is a `bigint` the spike increments itself (D3).

**Results** (✓ = right rows, translated to one SQL statement; ✗ = **wrong rows, no error**; ⚠ = throws):

| Shape | PostgreSQL 18 | MariaDB 11.8 |
|---|---|---|
| equals + `OrderBy` + paging | ✓ `(d."Data" ->> 'LastName') = 'Smith' … LIMIT/OFFSET` | ✓ `CAST(JSON_VALUE(d.Data,'$.LastName') AS char) = 'Smith'` |
| null check, `In`, id prefix, enum, `DateOnly`, string `Contains` | ✓ | ✓ |
| nested member `Address != null && Address.City == …` | ✓ `#>> '{Address,City}'` | ✗ **0 rows**: `JSON_VALUE(Data,'$.Address') IS NOT NULL` |
| `DateTimeOffset` compare | ✓ `CAST(… AS timestamp with time zone) > @p`: instants | ✗ **2 rows instead of 1**: `CAST(… AS datetime(6))` drops the offset |
| `AnyContains` / `Contains` over a primitive list | ✓ `(d."Data" -> 'Professions') @> to_jsonb(…)` | without the option: ⚠ "Primitive collections support has not been enabled". With it: ✗ **0 rows**, `JSON_TABLE(CAST(JSON_VALUE(Data,'$.Professions') AS char), …)` |
| captured array `Any` | ✓ `jsonb_array_elements_text … = ANY (@wanted)` | ✗ **0 rows** (same cause) |
| `Any` over a complex collection | ✓ `jsonb_to_recordset` | ✓ `JSON_TABLE(JSON_EXTRACT(Data,'$.Jobs'), …)` |
| get-only `FullName` by path | ✓ `jsonb_extract_path_text("Raw", 'FullName')` | ✓ `JSON_VALUE(Raw, '$.FullName')` |
| `OrderBy` nested, nulls | ✓, but nulls **last**: `[Cleo,Ann,Bob,Old]` | ✓ nulls **first**: `[Bob,Old,Cleo,Ann]` |
| D16 `Status == default` with the absent-field rewrite | ✓ 3 rows | ✓ 3 rows |
| D16 `!Jobs.Any()` (absent `Jobs`) | ✓ `NOT EXISTS (… jsonb_to_recordset …)` | ⚠ "could not be translated" |
| text round trip | equivalent, not byte-identical (`jsonb`) | byte-identical |
| versioned `UPDATE`, then a stale one | 1, then 0 rows | 1, then 0 rows |

**The root cause on MariaDB, confirmed on the server itself:**

```
SELECT JSON_VALUE('{"a":[1,2]}','$.a'), JSON_VALUE('{"o":{"c":1}}','$.o'), JSON_QUERY('{"a":[1,2]}','$.a'),
       CAST('2020-01-01T09:00:00+02:00' AS datetime(6));
→ NULL   NULL   [1,2]   2020-01-01 09:00:00.000000
```

MariaDB's `JSON_VALUE` returns only scalars. The fork uses it for object and array paths, where `JSON_QUERY` (or
`JSON_EXTRACT`) is needed. Every shape that touches a nested object or array through `JSON_VALUE` is wrong. The
`DateTimeOffset` cast keeps the local time and discards the offset.

**Findings:**
1. **PostgreSQL: D1 carries over unchanged.** 18/18 shapes give the right rows, and the SQL is idiomatic (`->>`, `@>`,
   `jsonb_to_recordset`). The containment `@>` can use a GIN index. PostgreSQL is as cheap as SQL Server 2022.
2. **PostgreSQL 18 is needed for a virtual `Raw`.** Npgsql refuses a virtual generated column below 18
   ("Virtual (non-stored) generated columns are only supported on PostgreSQL 18 and up"). On 17, `Raw` is `STORED`,
   which doubles the storage. Alternatively, select `"Data"::text` directly: that is S2b work for both dialects.
3. **MariaDB: D1's architecture holds, but its EF provider doesn't translate faithfully.** 12/18 are right, 5 are
   silently wrong and 1 throws. The wrong ones share two causes, both fixable in the dialect: `JSON_VALUE` used for
   non-scalar paths, and offset-blind `DateTimeOffset` casts. S13b proves the fix, through an EF translator plugin
   or a query-SQL post-processor. **Until S13b is green, MariaDB is not a supported provider.**
4. **Null ordering differs** (D4). It is a conformance case now.
5. **D3 (bigint version) works the same on both**, and on SQL Server it is the same SQL.
6. **MariaDB `Id` is `varchar(255)`** for the primary key under utf8mb4. Spark ids are short (`people/{guid}`), but
   natural ids (`IHasNaturalId`) must stay under it, and the provider must validate this at store time.

**Decisions:**
- D1 holds on PostgreSQL. On MariaDB it would hold only with translation fixes.
- **MariaDB deferred** (owner, O6). S13 stays as the evidence for that decision and for a later return.
- D2–D7 proposed (§3). D6 and D7 were corrected or added by grilling round 1. Owner sign-off pending.

### 8.3 S-ID1 + S-ID5 (RavenDB half): `Id<T>` on RavenDB 7.2 (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/sid1/Program.cs`, a console app with a minimal `Id<T>`, its Newtonsoft converter and
  the generator's `__DocumentId` written by hand.
- Run against the local dev server (RavenDB 7.2.6, client 7.2.7, .NET 11), throwaway database `spike_sid1`.
- 51 checks, 46 pass. All 5 failures are expected negatives, kept as evidence: four raw `Contains` queries and the
  option-(a) body-field query (C6).

**Results:**

| # | Check | Result |
|---|---|---|
| C1 | Store, load, upper-case load (same session instance) | ✓ |
| C2, R7 | A **private** `__DocumentId` as the identity, on load and in a rewritten query | ✓ |
| C3 | (a) `Where(x => x.Id == v)` via `RegisterQueryValueConverter` → `where Id = $p0` | ✓, but it is a **body** field |
| C4 | `list.Contains(x.Id)`, `array.Contains(x.Id)` and the control `List<string>.Contains(x.Name)` | ⚠ `NotSupportedException` for all, raw client |
| C4 | `x.Id.In(list)`, `x.Id.In(array)`, `x.Id == a \|\| x.Id == b`, a reference `x.CompanyId == v` | ✓ |
| C4 (b) | `Contains` rewritten to `In`, own id and reference | ✓ `where Id in ($p0)` |
| C5 | (a) `OrderBy`, `OrderByDescending`, projection `Select(new { x.Id })` | ✓ |
| C6 | (a) query by `Id` for a document **without** a body `Id` | ✗ 0 hits: every production document today |
| C7 | Identity wins over a disagreeing body `Id` on load | ✓ |
| C8 | (a) RQL `Id` matches the body value, not the document id | ✓, confirms C6 |
| C9 | Legacy `People/Legacy…` document: lowercase load gives one instance; `LoadStartingWith("people/legacy")` finds it; save keeps the stored casing; no duplicate | ✓ |
| C10–C12 | Streaming, lazy load, subscription worker: `Id` set from the identity | ✓ |
| C13 | HiLo: `people/1-A` lowered to `people/1-a` and loaded by the original casing. `store.HiLoIdGenerator` **throws** once `AsyncDocumentIdGenerator` is set, so Spark needs its own `AsyncMultiDatabaseHiLoIdGenerator` | ✓ |
| C14 | `TransformTypeCollectionNameToDocumentIdPrefix` lowered (`People` → `people`) | ✓ |
| R1–R6 | (b) `x.Id` rewritten to the identity: `where id() = $p0`, `id() != `, `id() in (…)`, `order by id()`; finds documents without a body `Id`; ignores a disagreeing body; a lowercase value matches a mixed-case stored id | ✓ |
| P1–P4 | `[JsonIgnore]` public `Id<T>`: today's string-Id document loads; load, change, change back, save is **byte-identical**; rewritten query; projection `select id() as DocId` | ✓ |
| G1 | `OnBeforeStore`'s `DocumentId` for a saved legacy document is the **stored** casing (`People/Legacy…`) | observed |
| G2, G3 | `ApplySparkConventions` lowered: generated `companies/{guid}` and natural `tags/dotnet` | ✓ |

**Decisions:**
- D23 rule 7: **(b), the rewrite decorator.** (a) is rejected by C6/C8, and (c) is not needed.
- D23 rule 6: the public `Id<T>` is `[JsonIgnore]`, so documents stay byte-identical (P2). HiLo uses Spark's own
  generator (C13).
- D7 / S-ID5: `UseGeneratedIds` today builds `{GetCollectionName(type)}/{guid}`, which gives `People/…`. It gets
  `.ToLowerInvariant()`, as `UseNaturalIds` does (G2, G3).
- **The debug guard checks only new documents.** RavenDB reports the stored casing for a legacy document (G1). A
  guard on every store would trip on each legacy save, which O15 (a) allows.
- The decorator sits behind Spark's session (`ISparkSession.Query<T>`). Author code on a raw `IAsyncDocumentSession`
  bypasses it; an analyzer flags `x.Id` in such queries (M3).
- Still open for S-ID4: the byte-diff and the queries on a **copy of the production `Coverage` database**. C9 and P2
  show both on synthetic documents only.
- Still open for S-ID5: the collision build failure (L1) is model-build code, tested with M3's unit tests. It is not
  a RavenDB behaviour to spike.

### 8.4 S-ID2: `Id<T>` on EF Core 11 (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/sid2/Program.cs`. EF Core 11.0.0, Npgsql 11.0.0, InMemory 11.0.0.
- Servers: SQL Server 2025 (`localhost\MSSQLSERVER01`) and 2022 (`localhost`), PostgreSQL 18 (`spark536-pg`),
  InMemory.
- The model has three id kinds:
  - `Company`: Integer, HiLo → `bigint`.
  - `Person`: Guid → `uniqueidentifier`/`uuid`.
  - `Tag`: Natural → `nvarchar(400)`, `BIN2` collation on SQL Server.
- Relations: a nullable converted FK `Person.CompanyId`, a join entity `PersonTag` with a composite converted key,
  and an owned child table `PersonAddresses`.
- The converters are what the generator would emit (`LongIdConverter`, `GuidIdConverter`, `KeyIdConverter`); the
  prefix is a compile-time constant.

**Results:** **25/25 on SQL Server 2025, SQL Server 2022, PostgreSQL 18 and InMemory.**

| Shape | SQL Server | PostgreSQL |
|---|---|---|
| One `SaveChanges` with 2 HiLo, 2 natural, 2 Guid, 3 join and 2 child rows | ✓ | ✓ |
| HiLo key assigned at `Add`, before `SaveChanges` (rule 5) | ✓ `companies/1`, `companies/2` | ✓ |
| `x.Id == v`, `x.CompanyId == v`, `x.CompanyId == null`, natural key | `WHERE [p].[Id] = @annId` (`uniqueidentifier`) | `WHERE p."Id" = @annId` |
| Join-entity navigation, reference navigation through the converted FK | `INNER`/`LEFT JOIN` on the columns | same |
| `OrderBy(x => x.Id)`, projection `Select(x => new { x.Id, x.CompanyId })` | ✓ | ✓ |
| `Contains` over 0 / 1 / 50 / 2000 / 5000 ids | `0 = 1` / `=` / `IN (@ids1..@ids50)` / 2000 parameters / `OPENJSON(@ids) WITH ([Value] uniqueidentifier)` | `= ANY (@ids)` from 50 up |
| `Contains` over HiLo, nullable-FK and natural values | ✓ | ✓ |
| Upper-case id input finds the row (`Id<T>` lowers before the converter) | ✓ | ✓ |
| A dangling converted reference | FK violation (input for D30) | FK violation |

InMemory first failed `OrderBy(x => x.Id)` ("At least one object must implement IComparable") until `Id<T>`
implemented `IComparable<Id<T>>`.

**Guid insert cost.** 100k rows through EF in `SaveChanges` batches of 1000, after a 100k warm-up, each flavour run
twice:

| Key | SQL Server 2025 | PostgreSQL 18 |
|---|---|---|
| v4 `uniqueidentifier`/`uuid` | 2.5 / 2.9 s, ~930 pages, 67% full | 3.4 / 7.6 s, PK index 3.9 MB |
| v7 | 2.4 / 3.7 s, ~930 pages, 67% full | 3.5 / 3.6 s, PK index **3.1 MB** |
| EF `SequentialGuidValueGenerator` | 2.5 / 2.7 s, ~930 pages, 67% full | not run |
| v4 as `varchar` `people/{guid}` | 3.1 / 3.0 s, **~1360 pages** | 4.4 / 4.6 s, PK index **7.5 MB** |

Measurement notes:
- The first run, without the warm-up, put 6.9 s on v4 alone. That was warm-up, not the key.
- On SQL Server every flavour, sequential included, leaves pages about 67% full with ~99% logical fragmentation.
  EF's batched insert gains nothing from key order there.

**Decisions:**
- D23 rule 2 holds: v4. Rule 8: v4 on every provider, in a Guid column, not a string.
- `Id<T>` implements `IComparable<Id<T>>`, and order by a Guid id is unspecified across providers.
- No ValueComparer. EF's own HiLo meets rule 5.

### 8.5 S-ID4: mixed-case legacy ids on a copy of a Coverage database (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/sid4/Program.cs`.
- The **local development** `Coverage` database (4677 documents, 29 collections) was exported with the client
  smuggler (documents, indexes, identities, attachments) and imported into `spike_sid4`. The source was only read.
- Every id is mixed-case:
  - `Accounts/github/9629574`
  - `Commits/github/{repo}/{sha}`
  - `UserAccess/SparkUsers/{guid}`, a prefix that is not its collection name
  - `ApiTokens/0000000000000003149-A`, HiLo with the node tag
- References in bodies are mixed-case too (`Commit.Repository = "Repositories/github/…"`).
- The entity classes stand in for the generator: a `[JsonIgnore]` id, a lowering `__DocumentId`, and every other
  field through `[JsonExtensionData]`. `CommitDoc.Repository` lowers on read, as an `Id<Repository>` converter
  would.

**Results: 15/15.**

| # | Check | Result |
|---|---|---|
| L1 | Batch `LoadAsync` of every document by its lowercased id | 4677/4677 found, `Id` lowercase |
| L2 | Original, lower and upper casing in one session | one instance, 50/50 |
| L3 | `LoadStartingWithAsync(lowercase prefix)` | all 26 prefixes return every document |
| L4 | `Repository = $v` with the original, lower and upper value: the existing `Commits/ByRepository`, an auto-index, an explicit **Lucene** index and an explicit **Corax** index | all agree, 4/4 values each |
| L5 | Load and save twice (add, then remove a field) all 248 documents of 7 collections (Accounts, Repositories, Commits, ApiTokens, SparkUsers, UserAccesses, MergeQueueEntries) | no duplicate (4677 → 4677); **0 ids changed casing**; `@flags` unchanged; the only body difference is `Commits.Repository` lowered ×32 |
| L6 | After the re-save lowered every `Repository`, a query with the original casing on Lucene and Corax | still 4/4 |

**Decisions:**
- **O15 (a) holds: no re-key.** RavenDB keeps the stored id casing on save. Loads, prefix scans and reference
  equality ignore case on both engines.
- References written through `Id<T>` become lowercase on the next save of their document. That is the only byte
  change, and queries are blind to it (L6), so it needs no migration.
- **To rerun on a production copy:** `dotnet run -- <database>` against an imported production export. The checks
  never write to the source. This run used the development database, which has every id shape the production one
  has; a production export is the owner's call.

### 8.6 S20: commands as keyed DI registrations (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/s20/Program.cs`, a `WebApplication` (Microsoft.NET.Sdk.Web, net11.0) built once per
  scenario.
- `AddSparkCore()` registers four keyed scoped commands and the key catalogue. A "provider" registers a document-store
  singleton, an EF `DbContext` aimed at an unreachable SQL Server (`10.255.255.1`, 5 s timeout) with a
  connection-counting interceptor, and its own `--spark-migration-script`.
- Every constructor increments a counter.

**Results: 12/12.**

| Scenario | Result |
|---|---|
| `--spark-migrate` | only `MigrateCommand` constructed (store 0, connections 0) |
| `--spark-synchronize-model` | resolves the `DbContext` and diffs its model (1 `CreateTableOperation`) in 0.7 s; **store 0, connections 0** |
| No `--spark-*` flag | not handled, nothing constructed |
| Unknown `--spark-nonsense` | "Unknown command …. Run --spark-help." |
| `--spark-help` | lists all four keys from the descriptors; no other command constructed |
| The same key from core and from the provider | the provider's (last) registration runs; without the provider, core's default runs |
| Development (`ValidateOnBuild`, `ValidateScopes`) | keyed scoped commands pass; a scoped command resolved from the root throws |

**Found on the way:** with `AddSparkCore()` alone (no `DbContext` registered), Development's `ValidateOnBuild`
failed `Build()` on the synchronize command's dependency. That is the rule added to D34: a core command depends only
on contracts with a core default.

### 8.7 S19: applying migrations with several instances, failures and scripts (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/s19/Program.cs` (console, net11.0, EF Core 11.0.0, Npgsql EF 11.0.0), run as
  `dotnet run -- sqlserver` (SQL Server 2025, local instance) and `dotnet run -- postgres` (PostgreSQL 18 container).
- D33's startup sequence: a framework `DbContext` with its own history table (`__SparkFrameworkMigrations`, one
  migration), then the app's three migrations (`People`; `Slow`, which sleeps 3 s so that instances overlap;
  `Half`, which adds a column), then a data step guarded by its own record, standing in for `ISparkMigration`.
- `S19_LOG=1` writes EF's SQL to `sql.log`; that is how the Npgsql lock was read.

**Results: SQL Server 21/22, PostgreSQL 20/22 (with 1d); every failure is a finding, and the guarded variant and
the Npgsql workaround pass on both.**

| Scenario | SQL Server | PostgreSQL |
|---|---|---|
| 1. Two instances, database does not exist, EF alone | ❌ `CREATE DATABASE` race ("already exists"; in earlier runs also "Cannot open database … login failed" while the winner's database was not online yet) | ❌ the same race, and the migration race below |
| 1b. Three instances, empty database exists, EF alone | ✅ `sp_getapplock` serializes them; each migration applied once | ❌ **"column Email already exists"** (3/3 runs) |
| 1d. As 1b, EF's lock with **the Npgsql workaround** (`AdvisoryLockHistoryRepository`; rerun 2026-11-24) | ✅ the same as 1b (no workaround needed) | ✅ each migration applied once (3.1 s) |
| 1c. Three instances, database does not exist, **Spark's guard** | ✅ one instance migrates (3.1 s), the others find nothing pending; each migration and the data step once | ✅ the same |
| 2. Migration 3 fails halfway | ✅ migrations 1–2 stay applied; the failing one rolls back as a whole (its table is gone); it stays pending; a restart with the fix completes | ✅ the same |
| 3. Idempotent script (framework + app), generated without a database | ✅ applied twice: the second run is a no-op; `MigrateAsync` then finds nothing pending (split on `GO`) | ✅ the same |
| 4. `GetPendingMigrationsAsync` on a database that does not exist | ✅ 3 pending in 6 ms (the basis of the `ApplyMigrations=false` refusal) | ✅ 3 pending in 7 ms |

**Findings.**
- **Npgsql 11.0.0's migration lock is held per migration, not around the run.** EF's SQL log shows
  `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE` inside each migration's transaction. The lock
  ends at that commit, and the next migration's lock is taken again, so a second instance that read the same pending
  list in between applies the same later migration. SQL Server's `sp_getapplock` is session-owned and holds across
  the run. Root cause: EF's migrator plans once and does not re-read the history after reacquiring a
  transaction-scoped lock. Filed as npgsql/efcore.pg#4162 (2026-11-24, owner's go) with a standalone repro and a
  session advisory lock fix (github.com/PieterjanDeClippel/efcore-npgsql-migration-lock-race, `fix` branch: 3/3
  runs pass). The fix is upstream as npgsql/efcore.pg#4163: red on `main` with a new parallel test across three
  migrations, green with the change (56/56 of the migration-lock and history test classes). Until it ships,
  the provider registers the same code as `AdvisoryLockHistoryRepository` (D33); scenario 1d proves it.
- **Neither provider covers database creation.** Instances race on `CREATE DATABASE`; on SQL Server the loser can
  also fail to log into the winner's database before it is online.
- **The fix, now in D33:** Spark takes a session-level lock in the server's maintenance database (`master` /
  `postgres`) keyed `spark:migrations:{database}` (a parameter, never spliced), and holds it across database
  creation, framework schema, app migrations and data migrations. The first instance does the work; the others wait
  and then find nothing pending (a few milliseconds each). A guard taken on the target database itself still failed
  on SQL Server, because the database must exist to connect to it.
- **Raw SQL in a migration must be valid inside PL/pgSQL `DO` blocks** in the PostgreSQL idempotent script, which
  wraps each statement in `DO $EF$ BEGIN IF NOT EXISTS(…) THEN … END IF; END $EF$;`. A bare `SELECT` fails there;
  `PERFORM` works. The SQL Server script needs splitting on `GO`. Hand-written SQL in a Spark-generated migration is
  rare, but the D31 tests run the script once per dialect so a non-scriptable statement fails CI.

### 8.8 S3: synchronize generates EF migrations from a model built from metadata (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/s3/` (console, net11.0, EF Core 11.0.0, Npgsql EF 11.0.0, EF Design 11.0.0), against
  SQL Server 2025 and PostgreSQL 18. `run.sh` runs the sequence.
- The model is **metadata**, not attributes: `OnModelCreating` maps what a per-version model description says
  (attributes, queryable flag and length, AsDetail collections, references) over CLR classes that carry every
  property, as Spark's model JSON does. Keys and references are `Id<T>` with the Guid converter (S-ID2).
- Each version is its own process: `gen n` scaffolds V*n* for both providers, the next `dotnet run` rebuilds with the
  generated files compiled in (what a developer's build does), and `verify n` checks nothing is pending, migrates and
  exercises the new shape.

**Results: 6 versions × 2 databases, every check green (gen 2–6: 4–6 checks each; verify: 6–20 checks each).**

| Model change | Scaffolded `Up` (identical on both databases) | Verified |
|---|---|---|
| v1 initial | `CreateTable(Companies)`, `CreateTable(People)` (Guid keys: `uniqueidentifier` / `uuid`) | round trip |
| v2 add a field | `AddColumn(Email)` | round trip |
| v3 make it queryable (length 256) | `AlterColumn(Email)` `nvarchar(max)` → `nvarchar(256)`, `CreateIndex(IX_People_Email)`; reporter: "may result in the loss of data" | index used by a filter |
| v4 add an AsDetail collection | `CreateTable(PersonAddresses)`: PK (`PersonId`, `Id`), FK to `People` with cascade (D25) | round trip; deleting the owner leaves 0 child rows |
| v5 add a reference | `AddColumn(CompanyId)`, `CreateIndex`, `AddForeignKey(FK_People_Companies_CompanyId)` (D26, no navigation) | filter by reference; a dangling id is refused (SQL Server's FK conflict / SQLSTATE 23503, D30) |
| v6 rename a field | `RenameColumn(LastName → Surname)`, no data-loss warning | a row written in v5 keeps its value |
| v6 again, no change | `HasPendingModelChanges()` false: nothing written | — |

**Findings.**
1. **Only public API is needed.** `services.AddEntityFrameworkDesignTimeServices(reporter)`,
   `AddDbContextDesignTimeServices(context)`, and the provider's `IDesignTimeServices` found through the public
   `DesignTimeProviderServicesAttribute` on its assembly (what the EF tools do). `IOperationReporter` and
   `ISnapshotModelProcessor` are public since EF 11 (dotnet/efcore#38408). No EF1001 warning, no
   `DesignTimeServicesBuilder`, no `dotnet ef`, no `IDesignTimeDbContextFactory`.
2. **Trap: the shared snapshot is finalized.** `HasPendingModelChanges()` finalizes the context's cached snapshot model,
   and `SnapshotModelProcessor.Process` returns `null` for a read-only model (`SnapshotModelProcessor.cs:59-66` on
   release/11.0; the same guard was internal in EF 10). The scaffolder reads `null` as "no previous model" and emits
   a migration that **recreates every table**, silently. Reproduced on both databases for v2–v6; a new context (its
   own scoped `IMigrationsAssembly`) scaffolds correctly. The EF tools never meet it because they run the check
   and the scaffold in separate processes. It is the open dotnet/efcore#36016; the root cause, a SQLite repro and
   a fix (the scaffolder processes a fresh snapshot instance) were posted there on 2026-11-24
   (github.com/PieterjanDeClippel/efcore-scaffold-readonly-snapshot, `fix` branch). The fix is upstream as
   dotnet/efcore#39784: red on `main` with a new scaffolder test, green with the change. **Workaround (rerun
   2026-11-24):** `FreshSnapshotMigrationsAssembly`, an `IMigrationsAssembly` decorator in the design-time services
   that returns a new instance of the snapshot class. With it, every version scaffolds the expected operations on
   the context that ran the check, while the same scaffold without it still recreates every table (both databases,
   v2–v6). Synchronize also refuses a scaffolded migration that creates a table the snapshot already has.
3. **A queryable string needs a length on SQL Server:** `CREATE INDEX` on `nvarchar(max)` is refused ("invalid for
   use as a key column in an index"). PostgreSQL indexes `text`, but one rule for both: a queryable string
   attribute gets a maximum length in the model, and making an existing field queryable narrows the column, which EF
   flags as possible data loss.
4. **EF's differ detected the rename by itself** (same table, same column type). Spark's model attributes carry
   stable ids, so synchronize can check EF's pairing against them and refuse a drop + add of the same attribute id.
5. **Synchronize must show the reporter's warnings** (the data-loss one above) and the generated operations, since
   the developer reviews the migration in the pull request (D33).
6. **Migrations are per provider** (column types differ): one folder and namespace per provider context, bound by
   `[DbContext(typeof(…))]`. An app has one provider, so one set. EF 11 also names the class after the id
   (`_20261124134420_V6_RenameLastName`) and writes the last migration id into the snapshot, so two branches that
   both add a migration conflict on that line, by design: the second regenerates after the rebase.
7. **`Microsoft.EntityFrameworkCore.Design` is heavy:** it brings Roslyn (`Microsoft.CodeAnalysis.CSharp`,
   `.Workspaces`, `.Workspaces.MSBuild`), `Microsoft.Build.Framework`, Humanizer and Mono.TextTemplating: 17 MB of
   DLLs plus 14 culture folders. Synchronize runs on a developer's machine, so production should not ship it (O17).

### 8.9 O17: keeping EF Design out of production (2026-11-24)

Question: can the scaffolding code from S3 ship in a package without its Roslyn closure reaching a production
publish? Probes in a scratch folder (not committed: three throwaway projects and a packed local package); the
console app's publish output was counted and run each time. A Debug publish of the app with EF Design flowing in
carries 46 DLLs, 42 MB, 11 of them Roslyn, MSBuild or Design.

| Shape | Release publish | Runs |
|---|---|---|
| (b) Design inside the relational package, `Publish="false"` on Design only, from a package `.targets` | only `Microsoft.EntityFrameworkCore.Design.dll` dropped; Roslyn stays | yes |
| (b) the same, plus the 19 packages only Design brings (hand-kept list) | 27 DLLs, 16 MB, none of them Roslyn | yes, synchronize reports Design missing |
| (b) the same, the app also references `Humanizer.Core` itself | Humanizer dropped too | **no: `FileNotFoundException: Humanizer` at startup** |
| (b), the app on Humanizer 2.14 | restore fails, NU1605: Design pins Humanizer 3.0.10 | n/a |
| (a) `DevelopmentDependency` package added with `dotnet add` (`PrivateAssets="all"`) | 26 DLLs, 17 MB, no Roslyn, the app's own Humanizer kept; **the same in a Debug publish** | yes |
| (a) the same, `dotnet build` (Debug or Release) | 8 Roslyn and Design DLLs in `bin` | yes, the design package loads |
| (a) reference written by hand, no metadata | 46 DLLs, 42 MB, Roslyn shipped | yes |
| (a) hand-written, package `build/*.props` with `Update … Publish="false"` | 46 DLLs, 42 MB: the `Update` runs before the item exists | yes |
| (a) hand-written, package `build/*.targets` with the same `Update` | 26 DLLs, 17 MB, no Roslyn | yes |
| (a) hand-written, package `buildTransitive/*.targets` adding `Publish="false"` in a target | 26 DLLs, 17 MB, no Roslyn | yes |
| (a) the package referenced by a class library, the host gets it through a `ProjectReference` | only the design package's DLL dropped; Roslyn stays | yes |

Findings:

1. **A package's targets cannot change restore.** Restore runs with `ExcludeRestorePackageImports=true`, and once
   for every configuration, so `PackageReference Remove`, `PrivateAssets` or `IncludeAssets` set from a package file
   never reach it. What can be changed afterwards is publish, through `Publish="false"`.
2. **The SDK computes what is safe to drop; a hand-kept list cannot.** Excluding one package drops the part of its
   closure that no other included reference reaches. That is why the app's own Humanizer survives (a), and why the
   hand-kept list in (b) crashed the app: it named Humanizer outright.
3. **The line falls at publish, not at the configuration.** `PrivateAssets="all"` excludes from publish in every
   configuration (`Microsoft.NET.Sdk.Shared.targets:53-55`), and `bin` keeps the package in every configuration.
   That suits synchronize, which runs from `bin`.
4. **`.props` versus `.targets`:** a package's `.props` is imported before the project's items, a `.targets` file
   after them, so only the `.targets` file can update the app's own reference.
5. **A transitive arrival cannot be stripped from the package side:** the class library keeps the closure reachable.
   The package warns in that case instead (O17 setting 3).

### 8.10 S2b: the real `QueryExecutor` over EF tables (2026-11-24)

**Setup.**
- Code: `docs/relational_spike/s2b/` (its README says how to rerun it).
- A throwaway EF Core implementation of `ISparkStore`, `ISparkSession` and `ISparkQueryProvider` (`EfSpark.cs`). It
  builds a `DbContext` from a model description, with no `DbSet` properties and no EF attributes on the entities.
- The real Spark host (`SparkEndpointFactory`) swaps those three services in. `IQueryExecutor` runs unmodified, with
  the model's attributes, an actions class with a row filter, and `Database.*` query sources.
- Model:
  - `Employee`: a reference `Company` (an FK, no navigation, D26), a `TranslatedString Title` (a JSON column, D1),
    an AsDetail `List<Address>` (a child table keyed (owner id, id), D25), a reference list `List<string> Skills`
    (a join table, D26) and a stored get-only `FullName` (O13)
  - `Vehicle`/`Car`/`Truck` (TPH), `Company`, `Skill`
- EF Core 11.0.0 on SQL Server (`MSSQLSERVER01`) and Npgsql 11.0.0 on PostgreSQL 18.

**Results: 14/14, the same 7 cases on both databases.** SQL from `S2B_LOG=1`:

| Case | SQL Server | PostgreSQL |
|---|---|---|
| row filter (:1055), count and page | `WHERE [e].[Owner] = N'alice'` | `WHERE e."Owner" = 'alice'` |
| restrict-to-ids `In` (:664), composed with the row filter | `… AND [e].[Id] IN (@Values1, @Values2)` | `… AND e."Id" = ANY (@Values)` |
| search pushdown (:2155) | `LOWER([e].[LastName]) LIKE @Value1_contains ESCAPE N'\'` per searchable string member, ORed | `lower(e."LastName") LIKE @Value1_contains` |
| column filter on the reference list | `EXISTS (… FROM [EmployeeSkills] … WHERE [e0].[OwnerId] = [e].[Id] AND [e0].[TargetId] = N'skills/csharp')` | the same, `EXISTS` |
| TPH: a query over `Vehicle` | one table, `Discriminator` column, both rows | the same |
| load: JSON column, child table, join list | `LEFT JOIN [EmployeeAddresses]`, then `[OwnerId] IN (@ids1, @ids2)` on the join table | `= ANY (@ids)` |
| O13: change `FirstName`, save, query by `FullName` | `UPDATE` writes the recomputed `FullName`; `WHERE [e].[FullName] = …` | the same |

**Findings:**
1. **The gate passes: EF translates `QueryExecutor`'s trees as they are.** The row filter, `In`, search, the column
   filters and the sort run in one statement per query, with no rewrite except the join lists (finding 4).
2. **The `Search` contract speaks RavenDB.** `QueryExecutor` hands the provider `*word*` per word
   (`QueryExecutor.cs:2004`), which is RavenDB's wildcard syntax; the first run found 0 rows on both databases,
   because `LIKE '%*love*%'` matches nothing. M2 changes `ISparkQueryProvider.Search` to take the words, and the
   RavenDB provider adds its wildcards itself.
3. **`Expression.Constant` becomes a SQL literal.** The first run's `In` was `IN (N'employees/grace', …)`: correct, but
   one query plan per id set. The provider now puts values in a captured field, which EF parameterizes (the table).
   The same applies to `QueryExecutor`'s own constants: the column-filter value is `N'skills/csharp'` in the SQL.
   EF escapes it, so it is not an injection, but it is one plan per filter value. M2 composes the executor's values
   as captured fields. A row filter written as a lambda in an actions class keeps its literals, as in any EF app.
4. **The D26 join list needs a query rewrite and a load step, and both are small.** EF ignores the CLR
   `List<string>`; an `ExpressionVisitor` turns `x.Skills != null` into `true` and `Any`/`Contains` over it into a
   subquery on the join table (`EXISTS`). After a page is read, one query per join table fills the lists
   (`OwnerId IN (…)`). Writes rewrite an owner's rows in the same transaction.
5. **AsDetail collections are joined into every page read.** EF loads owned collections with the owner, so the page
   query has a `LEFT JOIN [EmployeeAddresses]`; `COUNT` does not. With two AsDetail collections on one type, that is
   a cartesian product: the provider uses `AsSplitQuery()` for such types (M3).
6. **O13 works as decided:** the generated backing field, mapped with `HasField` and
   `PreferFieldDuringConstruction`. EF reads the getter when it saves, so the dirty check sees the recomputed value
   and rewrites the column. Queries on `FullName` hit the column, not the getter.
7. **`TranslatedString` round-trips through its own converter, and search skips it** (`Search` takes string
   members only). Searching translated text is S10's question, not a gate.
8. The S1/S2 leftovers carried as S2b in §8.1 (`JSON_VALUE` for unmapped members, typed path reads, promoted
   computed columns, `x.Id` on the JSON copy) belonged to the hybrid layout. D1 round 3 removed them: every member is
   a column or a JSON column, and `Id` is the key column, as the SQL above shows.

The S2b result table above is from the S2b run itself, 14/14. With the S4 cases added, the whole suite runs 44/44
on both databases.

### 8.11 S4: versions, etags, natural ids and constraint errors (2026-11-24)

**Setup.** The same prototype and host as S2b (`docs/relational_spike/s2b/`, `S4Tests.cs`), extended as follows:
- a shadow `bigint Version` concurrency token on every root table, which the session increments (D3)
- key and FK columns in a binary collation, `Latin1_General_BIN2` and `"C"` (D7)
- a natural-key `Tag` with a unique `Name` of at most 20 characters
- D30's translator

The D14 cases go through Spark's own `IDatabaseAccess` (load, save and delete of a persistent object), not the session.

**Results: 15 cases, all green on both databases** (with S2b's, 44/44 in total). SQL from `S2B_LOG=1`:

| Case | Result and SQL |
|---|---|
| a new row is version 1; a save with nothing changed writes nothing | ✓ |
| a change to a child row only (D25), or to the join list only (D26), moves the aggregate's version | ✓ 1 → 2 → 3 |
| a stale version at commit, after any earlier check | ✓ `UPDATE … SET …, [Version] = @p2 WHERE [Id] = @p3 AND [Version] = @p4` affects 0 rows → `SparkStoreConcurrencyException` |
| `""` inserts only: a second insert under the same id | ✓ the duplicate primary key (2627 / 23505 on the PK) → concurrency, not validation |
| delete by id with a stale version / the current one | ✓ `DELETE … WHERE [Id] = @p0 AND [Version] = @p1`; child and join rows go by `ON DELETE CASCADE` |
| D14 save with the loaded etag | ✓ etag `"1"` in, `"2"` back |
| D14 save / delete with a stale etag | ✓ `SparkConcurrencyException` (409); the row keeps its version |
| query rows carry their etags | ✓ through `GetVersionsAsync`: one `SELECT [Id], [Version] … IN (…)` per page |
| `LoadStartingWith("tags/a_")` and `"tags/a%"` | ✓ only the literal matches: `LIKE @idPrefix_startswith ESCAPE N'\'` (PostgreSQL: backslash is its default escape) |
| `employees/ada` found, `Employees/Ada` not | ✓ the binary collation; lowering is Spark's job (D7), never the database's |
| unique, length, not-null and FK violations | ✓ each a `SparkValidationException` naming `Name` or `Company`; the length message does not carry the value |
| the same on SQL Server with `Current Language=Deutsch` | ✓ |

**Findings:**
1. **D3 holds as decided**, on both databases: an `UPDATE` or `DELETE` carries `WHERE Id = @id AND Version = @v`.
   EF's 0-rows-affected exception becomes `SparkStoreConcurrencyException`, and Spark's existing 409 path takes it
   from there unchanged.
2. **The version belongs to the aggregate, not the table row.** EF marks an owner unchanged when only its owned
   child rows or its join rows change, so its version would stay put while the data moved. The session bumps the
   root's version itself whenever a child or join row of it changes. **D3 refined:** the version is on the root's
   table and covers the aggregate (D25 child rows, D26 join rows).
3. **A duplicate primary key and a duplicate unique index are different errors.** The first is a concurrency failure
   (`""` meant "must not exist"); the second is a validation message. The translator tells them apart by matching the
   constraint name against EF's model (`IKey.GetName()` against `IIndex.GetDatabaseName()`).
4. **SQL Server's constraint errors are localized, and not every language quotes the names.** In German, 2601 reads
   `… mit dem eindeutigen IX_Tags_Name-Index …` and 515 `… in die Name-Spalte …`. A parser keyed on English words
   or on quotes fails there. The translator offers every identifier-like word of the message to the model, failed
   entities first, and the model picks the constraint, index or column it knows. PostgreSQL reports
   `ConstraintName` and `ColumnName` in fields, except for 22001.
5. **Neither database names the column of a length violation reliably:** PostgreSQL's 22001 never does, and
   SQL Server's 8152 (compatibility below 150) doesn't either. The translator finds the value that exceeds its
   column's maximum length among the failed entries. **SQL Server's 2628 message includes the truncated value**
   (`Truncated value: '…'`), so D30's rule that a raw database message never reaches the client is about data, not
   only about tidiness.
6. **The untyped members need the id's collection prefix to find a type** (`ExistsAsync(id)`, `Delete(id, version)`,
   `GetVersionsAsync(ids)`). The prototype parses the prefix; D27 removes this by passing the type.
7. **An unversioned delete by id has to read the version first.** EF always puts the concurrency token in the
   `WHERE` clause, so the stub needs the current value. That is one `SELECT` before the `DELETE`;
   `ExecuteDeleteAsync` is the alternative in M3, in the same transaction.
8. **Query etags cost one query per page,** although the page query has already read `Version`. In M3, the
   relational `GetVersionsAsync` answers rows the session tracks from the tracker.

### 8.12 S5: neutral views (2026-11-24)

**Setup.** The S2b prototype and host (`docs/relational_spike/s2b/`, `S5Views.cs` and `S5Tests.cs`), with one view
declared once:

```csharp
public sealed class EmployeesWithCompany : SparkView<VEmployee>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s =>
        from e in s.Set<Employee>()
        select new VEmployee
        {
            Id = e.Id,
            FullName = e.FirstName + " " + e.LastName,           // computed (not the O13 stored column)
            Owner = e.Owner,
            CompanyName = s.Load<Company>(e.Company)!.Name,     // through a reference: the join D32 needs a view for
            SkillCount = e.Skills.Count(),                      // over a D26 join table
        };
}
```

`ISparkViewSource` has two members: `Set<T>()` is a collection, and `Load<T>(id)` follows a reference id to its row,
or null. Each renderer rewrites those two calls in the declaration:

| Provider | `Set<T>()` | `Load<T>(id)` |
|---|---|---|
| EF | the table's query root | `Where(t => t.Id == id).FirstOrDefault()`, a scalar subquery |
| RavenDB | the map's `docs` | `LoadDocument` |
| InMemory | the stored rows (LINQ to objects) | a lookup, with member access made null-safe |

From the declaration:
- EF writes the view's SQL per dialect (`ToQueryString`) at synchronize time.
- The row type is mapped with `ToView`.
- A subclass of EF's migrations differ puts `CREATE VIEW`/`DROP VIEW` into the migration.

The database is now created from those migration operations (EF's SQL generator), not `EnsureCreated`, which skips
views. S2b's and S4's 44 tests therefore ran on a migration-built schema too.

**Results: 10 cases, all green on both databases.** Together with S2b and S4 that is 64/64. Evidence comes from
`S2B_LOG=1`.

| Case | Result |
|---|---|
| EF writes the view SQL per dialect, with no parameters | ✓ below |
| `Query<VEmployee, EmployeesWithCompany>()` is a set over the view | ✓ `FROM [EmployeesWithCompany]` |
| a committed write is in the view at once | ✓ (D32: no staleness, `WaitForConsistencyAsync` stays a no-op) |
| the same declaration as LINQ to objects (InMemory) | ✓ the same rows as SQL |
| the same declaration deploys as a RavenDB index | ✓ the same rows, via `ProjectInto` |
| `QueryExecutor` queries the view by index name (catalog entry, `OpenIndex`) | ✓ row filter and search compose: see 5 |
| the initial migration creates the view after the tables, and `Down` drops it before them; the snapshot keeps the SQL | ✓ |
| an unchanged model has no pending change | ✓ `HasDifferences` false |
| a changed view: the migration is `DROP VIEW` + `CREATE VIEW` and nothing else | ✓ applied, and the new `FullName` reads back |
| a column change on a table the view reads: the view is dropped before and recreated after | ✓ (see 4) |

The SQL EF wrote, and the RavenDB map:

```sql
-- SQL Server
SELECT [e].[Id], [e].[FirstName] + N' ' + [e].[LastName] AS [FullName], [e].[Owner], (
    SELECT TOP(1) [c].[Name] FROM [Companies] AS [c] WHERE [c].[Id] = [e].[Company]) AS [CompanyName], (
    SELECT COUNT(*) FROM [EmployeeSkills] AS [e0] WHERE [e0].[OwnerId] = [e].[Id]) AS [SkillCount]
FROM [Employees] AS [e]
-- PostgreSQL
SELECT e."Id", e."FirstName" || ' ' || e."LastName" AS "FullName", e."Owner", (
    SELECT c."Name" FROM "Companies" AS c WHERE c."Id" = e."Company" LIMIT 1) AS "CompanyName", (
    SELECT count(*)::int FROM "EmployeeSkills" AS e0 WHERE e0."OwnerId" = e."Id") AS "SkillCount"
FROM "Employees" AS e
-- RavenDB
docs.Employees.Select(e => new { FullName = (e.FirstName + " ") + e.LastName, Owner = e.Owner,
    CompanyName = (this.LoadDocument(e.Company, "Companies")).Name, SkillCount = Enumerable.Count(e.Skills) })
```

**Findings:**
1. **D32 holds: one declaration, four renderings.** The generator emits the declaration, and nothing in it is
   provider-specific. The SQL comes from EF at synchronize time, as decided.
2. **`Load<T>(id)` is the one join form.** It is RavenDB's `LoadDocument`, SQL's scalar subquery on the key, and a
   lookup in memory. A general LINQ `join` has no RavenDB map form, so the declaration API offers `Load` and no
   `join`.
   - Each member read through a `Load` is its own subquery, so three columns of one company make three subqueries.
     That is fine for a reference's display name, and it is an optimisation for later (`OUTER APPLY` /
     `LEFT JOIN LATERAL`).
   - SQL and RavenDB propagate a missing reference as null, but LINQ to objects would throw. The InMemory renderer
     therefore makes member access through `Load` null-safe.
3. **EF's differ ignores a `ToView` entity, so views need a differ of Spark's own.** That differ subclasses the
   pubternal `MigrationsModelDiffer` (EF1001), which has two consequences:
   - Its constructor already differs between `v11.0.0` and EF's main branch, which adds an `IDiagnosticsLogger`.
     The relational provider package pins its EF minor and has a test that builds the differ.
   - `HasDifferences` doesn't call `GetDifferences`, so both are overridden. Without that, the pending-changes test
     (D33) misses a view change.

   The SQL and the tables the view reads travel as two model annotations, `Spark:ViewSql` and `Spark:ViewTables`.
   The snapshot keeps both, so the next migration diffs them.
4. **A view is recreated when a table it reads changes, not only when its SQL changes.**
   - PostgreSQL refuses `ALTER COLUMN … TYPE` under a view: `0A000: cannot alter type of a column used by a view
     or rule`.
   - SQL Server accepts it, and keeps the view's stale column metadata.

   So the differ drops an affected view before EF's operations and creates it after them. "Affected" means its SQL
   changed, or an operation touches a table in `Spark:ViewTables`. The tables come from the declaration (its `Set`
   and `Load` types, plus their D26 join tables).
5. **The executor needs no change.**
   - `OpenIndex` returns the view's set, typed by the row type.
   - `RerootOntoIndexQuery` takes it, because the element types differ.
   - `Project` has nothing to do.
   - `RowSecurity` rebinds the entity's row filter (`Owner == "alice"`) onto `VEmployee` by member name.
   - Search reaches a column read through a reference: `search: "acme"` finds Ada.

   As with RavenDB views, the searchable columns must be attributes of the entity's model (synchronize puts a
   default view's fields there).
6. **The view SQL that EF writes has the platform's line endings** (`\r\n` on Windows, inside the
   `migrationBuilder.Sql("…")` string and the snapshot annotation). Synchronize normalizes it to `\n`. Otherwise a
   migration generated on Windows and one generated on Linux differ, and the snapshot reports a change that isn't
   one. This is the same rule as "Regenerated files are LF".
7. **EF inlines the declaration's constants as literals** (`N' '`), so `ToQueryString` has no `DECLARE` and the
   view body takes no parameters. A declaration that captured a variable would produce one. The generator only
   emits literals, and synchronize refuses SQL that contains a parameter.
8. **Read the declaration once.** An expression-bodied `Map` builds a new tree on every read, with new parameter
   instances. A renderer that reads `Parameters` and `Body` from two reads matches nothing. `SparkView` caches it.
9. **RavenDB's map needs the shape `Set<T>().Select(…)`,** with one source collection. RavenDB supplies a
   projection's `Id` from the document, so the renderer drops the `Id` binding.

**Not covered:** concatenation of nullable columns. The view's string columns are required here. SQL's `NULL + ' '`
is `NULL`, while C#'s is `" "`, so a nullable-column case belongs in D31's database behaviour tests in M6.

## 9. Stale in earlier drafts

**In the 2026-11-09 draft:**
- The line numbers in its §2 contract table predated #551's final contract. The table also lacked `PreloadAsync`,
  `includePaths`, `TrackedEntities`, the request budget, `WaitForConsistencyAsync`, `IIndexCatalog`, `Project` and
  `IndexedFields`.
- It said the conformance suite covered row security, DateTimeOffset and paging. It doesn't.
- It kept every feature library on RavenDB only.
- It proposed LocalDB (2019: no `json`, no full-text).

**In the 2026-11-19 rewrite:**
- §2.3 missed `SearchIndexGenerator.Producer.cs`, the hand-written index producer, the analyzers that key on
  RavenDB type names, `SparkStoreRequirement.cs:50`'s default hint, and two messages.
- §2.5 missed the files library (new since), templates, testing and all_features. It listed a Patch in organizations
  that is now only a comment. It said no library used `ISparkSession`; files now does.
- It treated subscriptions as needing ordering and `@refresh`. §2.6 shows they are a wake-up signal, and `@refresh` is
  gone.
- It assumed "SQL Server, later maybe PostgreSQL/SQLite". The scope is now SQL Server, PostgreSQL and MariaDB, with a
  shared relational core (D2).

**In the 2026-11-24 draft, before grilling round 3:**
- D1 was a hybrid `Documents` table: Newtonsoft owned the JSON, and EF only translated queries through a `Doc<T>`
  rewrite.
- D1a was one global table with a global id space.
- D7 was a lower-cased `IdKey` primary key.
- The equivalents table assumed a `Documents.ChangeStamp` and a `Raw` alias column.

Round 3 replaced all of these (D1, D23–D27).

## 10. Open questions for the owner

Grilling round 2 (owner, 2026-11-24) answered O1, O2, O3 and O6. Round 3 reversed D1 and added D23–D27, and the
commercial-framework comparison added D30. The plan §3 has the log.

- ✅ **O1, one PR:** **one pull request** (rule 8), draft PR #697.
- ✅ **O2, Tier 4:** **both identity_provider and dashboards are ported** in this work. S9 (samples table, rollups,
  retention) is in scope.
- ✅ **O3, demo app:** **a new `apps/SqlDemo`**, dedicated to the relational providers. It is the E2E proof, so it
  uses the ported feature libraries, not core alone.
- ~~**O4, minimum SQL Server**~~ Answered by S1/S2: 2022 and 2025.
- **O5, full-text:** deferred with S10. `LIKE` comes first (§3).
- ✅ **O6, MariaDB:** **dropped for now.** In scope: SQL Server and PostgreSQL. §7 risk 1 says when it returns.
- ✅ **O7, order:** follows from O6. SQL Server and PostgreSQL are built together on the shared core.
- **O8, PostgreSQL floor (decided 2026-11-24, owner): 17 and later, CI on 18.** 18 was needed only for the virtual
  `Raw` column, which round 3 removed. S2b, S4 and S5 used nothing newer than 17 (jsonb, advisory locks, `ILIKE`,
  scalar subqueries, the `"C"` collation). `uuidv7()` is 18-only, but D23 keeps v4 Guids. The full suite runs on
  `postgres:18`. The D31 behaviour-test class also runs on `postgres:17` inside an existing job, which adds no job
  against the 20-concurrent limit. That run is what catches an 18-only feature slipping in.
- **O9, own model configuration (decided 2026-11-24, owner): a public `ModelBuilder` hook, now.** The commercial
  framework's apps write their own `DbContext`. Spark keeps owning the context and the model, and offers
  `UseSqlServer(o => o.ConfigureModel(mb => …))` and `UsePostgreSql(o => o.ConfigureModel(…))`. The callback runs
  last in `OnModelCreating`, after Spark's mapping (where S2b's `Configure` seam runs). An app adjusts the generated
  model there, for example column types, check constraints or extra indexes, and synchronize scaffolds the result
  into the migration like any other model change (D24). A full app-supplied `DbContext` was rejected because it
  splits the ownership of the model and of D33's startup order. `ISparkMigration` stays the escape for raw DDL.
  Lands in M4 with the provider packages.
- **O10, change stamp (decided 2026-11-24, round 5):** a stamp column per entity table plus a `Tombstones` table (D6).
- **O11, absent field versus `NULL` (decided 2026-11-24, round 5):** D16 everywhere, extended to nullable members
  (RavenDB rewrites `== null` to include absent fields); no backfill (§8, "Unknown fields").
- **O12, revisions (decided 2026-11-24, round 5):** a framework `Revisions` table on both SQL databases, in Spark's
  own schema (D24).
  - **Opt-in, as today:** only an app with History, and only for the entity types whose model `Revisions` block or
    `Spark:History:Types` setting enables them (`RevisionsConfigurator.Resolve`, `RevisionsConfigurator.cs:146`).
    Every other save writes nothing extra. RavenDB's database-wide `Default` revisions setting (set outside Spark)
    has no SQL equivalent, so on SQL only opted-in types get revisions.
  - A row is (table, key, `Version`, time, JSON snapshot of the **whole aggregate**, parent plus AsDetail rows),
    serialised by Spark, so it is the JSON RavenDB and InMemory hold and a revert restores the same record. The
    revision id is the `Version` (D3).
  - Written in the save's transaction. Set-based writes on an opted-in type take the changed rows from `OUTPUT`
    (SQL Server) or `RETURNING` (PostgreSQL) and insert their revisions in the same transaction. Retention by count
    and age, and `PurgeOnDelete`, run as the type configures.
  - **The table is always present** in the provider's framework schema, empty until a type opts in (the owner left
    this to me). The relational provider implements the `Revisions` capability itself, as the RavenDB server does,
    so soft_delete and files find it whether or not History is installed. One framework schema with one version
    avoids a schema variant per installed package, and turning History on later needs no migration.
  - Rejected: SQL Server temporal tables (SQL Server only, retention by age only, purging needs versioning off, an
    aggregate spread over several timelines) and triggers (the database's JSON is not Spark's).
  - The trap: a write path that bypasses the unit of work writes no revision. S8 has a conformance case per write
    path, and framework SQL never writes an entity table directly.
- **O14, hierarchical natural ids (decided 2026-11-24):** D23 rule 12 and D7. The LLMs' consensus of rounds 6–9: a
  string primary key with prefix scans, plus an `Id<Parent>` property (a real FK) wherever a segment is another
  entity's id. The owner accepted it with one change: the code is source-generated from one id template per entity,
  so the parent property is read from the id rather than declared by hand, and builder and parser can't drift. Ids
  are lower case (D7, round 10), with binary key columns.
- **O13, get-only persisted properties (decided 2026-11-24, round 5): stored, recomputed on every save**, as RavenDB
  does today (its serialiser writes getters). Example: `Person.FullName => $"{FirstName} {LastName}"`
  (`apps/HR/HR.Library/Entities/Person.cs:24`), which HR searches and sorts on.
  - Every getter works, also ones that call methods or read a `TranslatedString`; the column can be indexed and
    sorted; revisions hold the same JSON. Rejected: a computed column and expanding the getter in queries, because
    both work only for getters that translate to SQL, and an untranslatable one breaks a grid filter at run time.
  - **Source-generated** (the owner's suggestion). Entities are already `partial` for D23's generated
    `__DocumentId`. For each get-only property on a persisted entity the generator emits a private backing field,
    and the EF model maps the property to it with `PropertyAccessMode.PreferFieldDuringConstruction`: EF writes the
    field when it materialises a row (unused) and reads the getter when it saves, so the column always holds the
    getter's current value. `p.FullName` in LINQ then translates to the column directly, with no query rewrite and
    no shadow property. EF's round-3 error was "No backing field could be found … does not have a setter", so the
    field is what it lacks; S2b proves the mapping, the dirty check (a change to `FirstName` rewrites `FullName`)
    and the translation on both databases. Fallback: a shadow column plus a query rewrite of the member.
  - **Analyzer warnings** on a stored getter that reads the clock (`DateTime.Now`, `TimeProvider`) or another
    entity: its stored value would only be as fresh as the last save.
  - **Formula changes:** the generator emits a hash of each stored getter's body into the model, and synchronize
    reports which types' stored getters changed. Recomputing them is an explicit migration the developer chooses,
    never automatic, because rewriting rows re-triggers the subscriptions (O15). The same holds on RavenDB.
- **O15, mixed-case ids in production RavenDB data (decided 2026-11-24: (a), no re-key, gated by S-ID4):** the owner
  first proposed a migration that lowercases every document id in the production apps. All five LLMs that answered
  round 10 chose (a), and the owner followed the recommendation ("I'll follow your decision").
  - **(a) No re-key.** RavenDB lookups already ignore case, and Spark lowers every id it reads (D23 rule 6), so the
    app sees only lowercase ids. Reference fields inside documents need no patch either if RavenDB's string
    equality in indexes ignores case (expected for both engines; to verify), and `LoadStartingWithAsync` must be
    shown to ignore case on mixed-case ids.
  - **(b) Re-key every document.** `Accounts/X` and `accounts/x` are the *same* RavenDB id, so the
    `M_202609210900_ForgeQualifiedDocumentIds` pattern (put at the new id, then delete the old) would delete the
    document. A case-only re-key needs a delete and a put of the same document in one transaction, and the
    attachments, counters, time series and revisions moved by hand. Compare-exchange keys that hold ids must be
    rewritten too.
  - **Either way:** any write to the documents (the re-key, or a reference-field patch) gives them new change
    vectors, so the messaging subscriptions would process them again. Pause the subscriptions and move their
    positions past the migration, or don't write.
  - Recommendation: (a), proven by a spike on a copy of the production `Coverage` database (S-ID4) that loads,
    queries, prefix-scans and compares mixed-case ids through the lowercase `Id<T>`. Choose (b) only if the spike
    fails, and spike the case-only re-key on the same copy first.
- **O16, how a test project picks its database type (decided 2026-11-24, owner: (d)):** the question is the
  provider (RavenDB, SQL Server, PostgreSQL), never the database name, which stays the driver's business as on
  master (`RavenTestDriver.GetDocumentStore` names each database off a process-wide counter,
  `SparkTestDriver.cs:20`). On master nothing chooses: the drivers derive from `RavenTestDriver`. D35 moves them
  into a neutral package, so the provider has to come from somewhere.
  - **(d) The provider package the test project references decides.** Each `MintPlayer.Spark.Testing.<Provider>`
    ships `build/MintPlayer.Spark.Testing.<Provider>.targets` that adds an `AssemblyAttribute` item, so the build
    writes `[assembly: SparkTestDatabase(typeof(SqlServerTestDatabase))]` into the test assembly. An app's test
    project references one provider package, as it already does, and writes nothing. A project that references
    several, in practice Spark's own neutral suites, gets several attributes and must set `SPARK_TEST_DATABASE`;
    the CI matrix sets it per leg.
  - The neutral driver fails at startup, naming the candidates, when there are several attributes and no variable,
    when the variable names a provider the project does not reference, and when there is no attribute at all. It
    never falls back to a default: a SQL Server matrix leg that silently ran PostgreSQL would pass.
  - It is `build/`, not `buildTransitive/`: the test project references the testing package directly. The runtime
    packages (`MintPlayer.Spark.SqlServer`, …) ship no build files for this. They reach a test project only through
    the testing package, so their targets would have to be `buildTransitive/` and would also load into every
    production app. The owner's option of having them expose MSBuild properties stays available if a real need
    appears.
  - Rejected: (a) the same attribute written by hand, one line (d) generates; (b) a generic base class per test
    class, `SparkTestDriver<TDatabase>`, which repeats the choice in every class and needs a subclass per provider
    (the conformance suite's pattern today, §2.4); (c) the environment variable only, which makes a local run depend
    on something set per machine.
  - Checked in S17: the generated attribute, and the three startup failures.
- **O17, where the migration scaffolding lives (decided 2026-11-24, owner, evidence §8.9):** a separate
  **`MintPlayer.Spark.Relational.Design`** package that stays in `bin` and out of every publish. Core's
  synchronize command loads it by name, the way the EF tools find a provider's design-time services (an assembly
  attribute naming an `ISparkSchemaScaffolder` implementation). The app never names it in code. Without it,
  synchronize writes the model JSON and stops with a message that names the package and says to reference it in
  the host project. The package has three settings, and no others:
  1. **`<DevelopmentDependency>true</DevelopmentDependency>`** in the package. `dotnet add` and Visual Studio then
     write `PrivateAssets="all"` (it does not flow to the projects that reference the app's project) and an
     `IncludeAssets` without `compile` (the app cannot compile against design-time types, which it never needs).
     The SDK leaves a `PrivateAssets="all"` package out of publish (`Microsoft.NET.Sdk.Shared.targets:53-55`).
  2. **`PrivateAssets="none"` on its own reference to `Microsoft.EntityFrameworkCore.Design`**, itself a
     development dependency: without it EF Design is not a dependency in the nuspec, and the package arrives without
     it.
  3. **`buildTransitive/MintPlayer.Spark.Relational.Design.targets`**, a `.targets` file (a `.props` file runs
     before the project's own items exist, so its `Update` matches nothing: measured). It marks the app's reference
     `Publish="false"`, which covers a reference written by hand without `PrivateAssets`. When the package reaches a
     project only through another project, it raises a build warning instead, because nothing on the package side
     can keep Roslyn out of that publish (measured).
  - It replaces the Debug-only reference first proposed (a `Condition` on `Configuration`). The line is drawn at
    publish, not at the configuration: synchronize works from `bin` in any configuration, and production images
    are published (`code-coverage-deploy.yml`, `mintplayer-deploy.yml` and the Dockerfiles run `dotnet publish`).
  - **In this repository** apps use `ProjectReference`, which a package's `.targets` file never reaches, so an
    in-repo app that uses a relational provider references the design project in a Debug-only `ItemGroup`, as
    `apps/CodeCoverage/CodeCoverage/CodeCoverage.csproj:93` does for its git writer.
  - Rejected: (b) inside `MintPlayer.Spark.Relational`, whether shipped whole or stripped by a hand-kept list
    (§8.9 shows the list removes packages the app uses itself, and the app crashes at startup in production
    only); (c) a separate `dotnet` tool, which needs the design-time `DbContext` factory D33 avoids.
  - Trap: a deployment that copies `bin` instead of publishing ships 17 MB of Roslyn with no warning. It costs
    size, not correctness, and the guide says "publish, don't copy `bin`".

## 11. Out of scope

- **MariaDB, for now** (O6; investigated, spiked in S13, deferred).
- SQLite and MySQL.
- Migrating data between providers. That is the developer's own one-shot migrator, which maps ids (D23 rule 11).
- Reference integrity (delete guard, rename): a separate issue (D26).
- Composite keys and id obfuscation (D23 rule 10).
- Replication (§5).
- The libraries with no tracked source (§2.5).
