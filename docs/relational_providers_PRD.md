# Relational storage providers (SQL Server, PostgreSQL, MariaDB): PRD

Issue: [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). Builds on the storage-agnostic core of
[#551](https://github.com/MintPlayer/MintPlayer.Spark/issues/551) (PR #620, `docs/spark_editor_PRD.md` §3
"Storage-agnostic core", §8). The spikes, milestones, grilling rounds and status are in
[`relational_providers_plan.md`](relational_providers_plan.md).

Status (2026-11-24): **every decision is made (§10) and every gate spike is done (§8.3–8.12); implementation starts
at milestone M2** (plan §1, §4). In scope: **SQL Server and PostgreSQL** (PostgreSQL 17 and later, O8).
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
- 2026-11-24, later: grilling round 5 and the owner decided O10–O17 and D31–D35. The gate spikes S-ID1/2/4/5,
  S2b, S3, S4, S5, S19 and S20 proved the design (§8.3–8.12), and two upstream EF bugs got pull requests and
  workarounds (D24, D33). O8 and O9 closed the open list.
- 2026-11-26: **split** (owner). Typed ids (milestone M2: D7, D23 rules 1–7, 9 and 10, D27, O15) shipped first as
  #713, with its own [PRD](typed_ids_PRD.md) and [plan](typed_ids_plan.md). This PRD keeps the relational side of
  those decisions and points there for the rest. #697 was rebuilt on master after #713 merged.

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
  - ⚠️ **Corrected 2026-11-25:** `apps/MintPlayer`'s `Likes_Count` is a multi-map with fan-out and a reduce, and the
    generated `Spark_Search` is a multi-map. Map-reduce and multi-map views are D43 (plan M7e). Search does not
    become a view: D44 searches the collections on every store but RavenDB.
  - **Only moderation has map-reduce:** 3 `group by` + `Sum` indexes (`ModerationIndexes.cs:44/:72/:126`), read with
    `WaitForNonStaleResults` (×10) for read-your-writes. This is the hardest part to port.
  - The rest are plain map indexes: messaging (by priority), identity_provider (2), replication (by status), and
    moderation (2).
- **Time series:**
  - Dashboards append samples with raw and rollup retention policies, and aggregate them in RQL `group by`/`avg()`
    (`DashboardStorage.cs:62/:78-106/:130/:145`). Ported by D45: RavenDB keeps them; other stores keep rows.
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
service containers. The spikes pulled and ran `postgres:18` and `mariadb:11.8` on Docker Desktop. With MariaDB
deferred (O6) and the PostgreSQL floor at 17 (O8), CI uses `mssql/server:2025-*`, `postgres:18`, and `postgres:17`
for the D31 behaviour tests only.

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
- **As built (M5a, 2026-11-25; plan §1):**
  - The choice: each provider package's build targets write `[assembly: SparkTestDatabase(typeof(…))]`. One attribute
    decides; with several, `SPARK_TEST_DATABASE` picks one, and there is no default. `MintPlayer.Spark.Tests`
    references all four, so CI and `test:affected` set `RavenDB`.
  - The InMemory provider lives in the neutral package (it needs no client). The guard covers the neutral Testing
    package too.
  - **Create-and-migrate per test case is cheap enough, so no template clone and no Respawn:** measured 0.77–1 s per
    case on SQL Server and 0.65–1 s on PostgreSQL, including the host boot and the drop. The first case pays the
    server's warm-up (17 s and 4–5 s).
  - The schema comes from the model's migration operations (framework migrations first), through a test
    `ISparkSchemaMigrator`. A later host on the same database applies only its differences.
  - The embedded PostgreSQL runs as one named instance per machine (`spark-tests`), kept running and attached to.
    The library's default, an ephemeral instance per process, left nine `postgres.exe` running after each test run.
  - Not moved: the JSON fixtures (RavenDB's query-result format) and `RqlRecorder` stay in `.RavenDB`. A neutral
    query recorder comes when a ported library's test needs one.

**D36 (M4a, 2026-11-25): core says which types are tables.** A relational provider needs its entity list when it
builds the EF model; RavenDB and InMemory never needed one. Core's `ISparkStoredTypes` (Abstractions, implemented by
`SparkStoredTypes` in `MintPlayer.Spark`) lists the model's entity types that `ISparkTypeResolver` resolves to a
non-abstract class with a public writable `Id`, minus `[ValueObject]` types, plus the types registered with
`services.AddSparkStoredType<T>()` (stored documents outside the app model: the framework's own, from M4b on). A
derived type with a stored base becomes TPH in the base's table. Table names come from `SparkCollectionNames.Of(type)`
(Abstractions), the rule InMemory already had (the base name pluralized), so `relcompanies/…` ids are the same on
InMemory and SQL. A type a query or load names that is not in the list fails with a message that names
`AddSparkStoredType`.
- **Deviation from D26:** a `[Reference]` **string** that points at an `Id<T>`-keyed table gets the target's key
  converter, so the column holds the key, but no FK constraint: EF requires the same CLR type on both ends of a
  relationship. The FK appears once the property is converted to `Id<T>`. *(M4a; HR's `Person.Company` is one.)*

**D37 (M6, 2026-11-25): `ISparkSession.OptimisticConcurrency`, the neutral `OptimisticConcurrencyMode.Writes`.** The
Tier 1 libraries set RavenDB's mode in about 15 places (settings, quotas, boards, apps, organizations) so a
read-modify-write loses no concurrent change and a create never overwrites. Passing an expected version at each
`StoreAsync` would rewrite every one of those flows, so the session gets the switch instead. When set, every write
checks with no version passed: a loaded entity must still be at the version it was read at, a new one must not exist,
and the save throws `SparkStoreConcurrencyException`. RavenDB maps it to its mode (keeping `WritesAndReads` if a raw
session set it); InMemory derives the expected version per tracked row at save; the relational session already checks
every loaded row (the version is EF's concurrency token, D3) and adds the "must not exist" check for new rows. Off, a
new entity at a stored id replaces it on every provider. *Evidence:* `OptimisticConcurrency_checks_every_loaded_and_new_document`
and `Without_OptimisticConcurrency_a_new_document_replaces_a_stored_one`, 4/4 on RavenDB and InMemory, 4/4 on SQL
Server and PostgreSQL.

**D38 (M6, 2026-11-25): `ISparkSession.Query<T>(TimeSpan waitForIndexes)`, the per-query index wait.** Organizations
and Notifications decide on query answers (is another Owner left, which links are pending, what to export) and
waited with `Customize(c => c.WaitForNonStaleResults(15 s))`. `ISparkStore.WaitForConsistencyAsync` is no substitute:
it waits only for the indexes that already exist over the collections, and the first query of a new shape makes
RavenDB create its auto index on the spot, which that wait never sees. So the session takes the wait per query.
RavenDB customizes the query; InMemory and the relational providers read committed data anyway and call `Query<T>()`.
*Evidence:* `A_waited_query_sees_a_document_committed_just_before` (a fresh name, so a new query shape), green on
RavenDB, InMemory, SQL Server and PostgreSQL; the Organizations and Notifications classes 67/67 on RavenDB.

**D39 (M7a, 2026-11-25): `ISparkSubscriptions`, the neutral subscription (S7 + S14).** Messaging, the subscription
worker and replication all run on RavenDB data subscriptions. The neutral contract keeps their guarantees: durable
(a named definition survives restarts), exclusive (one runner per name; the rest wait), at least once (a batch whose
handler threw comes again), current state, the backlog included. A definition is a name plus a LINQ predicate.
- **RavenDB** keeps its data subscriptions: the typed predicate is rendered by the client to the subscription's RQL,
  `UpdateAsync(CreateNew = true)` creates or moves the definition, exclusivity is `WaitForFree`. Two runners that
  create the same name at once race: the loser gets "already in use in a subscription with different Id"
  (`RavenException`), so the create is retried, which then updates the winner's definition.
- **InMemory, SQL Server and PostgreSQL** share `SparkCursorSubscriptions`: a cursor, "every stamp up to here is
  handled", in `ISparkAtomicValues` (`spark/subscriptions/{name}`, a registry under `spark/subscriptions`), and the
  `ISparkStoreLock` lease `spark/subscriptions/{name}/runner` (30 s). A poll reads the D6 horizon, then the matching
  rows with a stamp in (cursor, horizon) by stamp. The cursor moves by compare-and-swap after the handler returned; a
  runner whose swap fails has lost the subscription and waits for the lease again. A batch never splits a stamp:
  PostgreSQL stamps a whole transaction with one `xid8`, so a transaction is one batch even past `MaxDocsPerBatch`.
  This process's commits wake the runners (the change feed); others' are seen on the next poll.
- Deletes are not delivered (a subscription sees current documents, as RavenDB's does); the change feed's tombstones
  (M4e) serve the consumers that need deletes.
*Evidence:* `SparkSubscriptionsConformanceTests` (backlog + later commits in batches of 2, an enum/bool/null filter, a
handler that threw, a restart resuming after the cursor, a second runner waiting until the first stops, names and
delete): 6/6 on RavenDB (three runs in a row) and InMemory, 6/6 on SQL Server, 7/7 on PostgreSQL with
`One_transaction_is_never_split_across_batches`. The first RavenDB run failed the exclusivity case on the create race
above.

**D40 (M7b, 2026-11-25): messaging on the neutral storage seam.** The messaging package drops `RavenDB.Client` and
requires only the Subscriptions capability (`[SparkRequiresStore("Spark.Messaging", Subscriptions)]`, no provider).
Each RavenDB mechanism it used has a neutral replacement:
- **The sequence.** The feeder's FIFO key was `QueuedAtUtc ?? @last-modified`, computed by the static index
  `SparkMessages_ByPriority` (S-M7). No other store has `@last-modified`. `MessageFeeder.AssignSequences` now keeps a
  captured `QueuedAtUtc` and gives an uncaptured message `now + n ticks` on the lease holder's clock: first the batch
  in delivery order (commit order on every store), then what only the page holds, by `CreatedAtUtc`. The claim or
  deferral write stores it, as before.
- **The indexes.** `SparkMessages_ByPriority` and `SparkMessages_ByQueue` are deleted, along with the startup middleware
  that deployed them. The page and the sweeper's candidates are plain `Query<SparkMessage>()` queries: an auto-index on
  RavenDB, a table scan or ad-hoc index elsewhere. The trade-off: under D4's nulls-first order an uncaptured message
  sorts first in its priority on the page. `AssignSequences` still places it after the captured ones, and the batch
  serves it anyway.
- **The sweeper.** Patch scripts that re-checked the predicate on the server become a batch load, a predicate check in
  C#, and one versioned store per document (`OptimisticConcurrency`, expected version). A
  `SparkStoreConcurrencyException` skips the document; the next sweep sees it again. The stale-query guarantee holds:
  `MessageRetrySweeperStaleIndexTests` freezes RavenDB's indexing (`StopIndexingOperation`, after the sweep's own
  auto-indexes exist) and checks that a message which moved on is left alone.
- **The handler session (`MessageSession`).** `ClaimedExecution` renews the claim from its own session while handlers
  run. RavenDB's session is last-write-wins and never noticed. A relational store checks the loaded version on every
  UPDATE, so the owner's next save conflicted. On conflict, `MessageSession` probes the stored message: if it is still
  Processing under this owner, only a renewal wrote it, and the save is retried with the stored version (at most 3
  attempts). Otherwise the claim is lost and the conflict is rethrown.
- **Progress sidecars.** The terminal save used a deferred patch to set `@expires` on each sidecar. Now the sidecars
  are evicted, reloaded and given `SetExpiry` in the same save, so steps appended by `PatchAppend` are not overwritten.
- **The lease.** The compare-exchange value becomes an `ISparkAtomicValues` entry holding the lease as JSON, under a new
  key `spark/messaging/lease`. The old key `spark/messaging/leader` held a typed compare-exchange object; it lapses
  within one TTL of the upgrade.
- **The outbox.** `IMessageOutbox.EnqueueAsync` and `ISparkAfterCommitOutbox.EnqueueAsync` take an `ISparkSession`.
  Callers still on a raw RavenDB session bridge with `session.AsSparkSession()` until their own port.
- **The subscription worker.** `SparkSubscriptionWorker<T>` runs on `ISparkSubscriptions` (D39).
  `LegacySubscriptionCleanup` lists and deletes through it too. `RetryNumerator`, a RavenDB counter, moves to
  Replication, its only user.
- **Stored types.** `AddSparkMessaging` registers `SparkMessage` and `SparkMessageProgress` with `AddSparkStoredType`,
  so a relational provider maps their tables.

*Evidence:*
- `NeutralMessagingTests` (new, on the neutral driver) passes 3/3 on RavenDB, InMemory, SQL Server and PostgreSQL: a
  broadcast message, an outbox message and a failed-once message retried by the sweeper, each handled and completed.
- On RavenDB, the messaging test folder, the subscription worker, replication's worker and the subscription
  conformance tests pass 265/265. The subscription conformance tests also pass on SQL Server and PostgreSQL, 15/15,
  including the new `A_subscription_deleted_under_its_runner_ends_the_run`.
- CodeCoverage's outbox users pass 286/286, and the generator tests 2/2.
- Found on the way:
  - A shared read helper in `MessageSubscriptionManagerLifecycleTests` and one assertion read the old lease key, so
    three "the lease is released" assertions passed without checking anything. They now read the new key.
  - The outbox's explicit `SparkMessages/{guid}` id had been dropped in the port. It is restored.

**D41 (M7b, 2026-11-25): `ISparkStoreReadiness`, background work waits for the schema.** ASP.NET Core starts hosted
services before the web host builds its pipeline, and `UseSpark` applies the relational schema while building it (D33).
On a fresh SQL Server or PostgreSQL database, messaging's lease read found no `AtomicValues` table (or no database at
all), and `MessageSubscriptionManager.ExecuteAsync` died. All three `NeutralMessagingTests` failed after a 60 s wait,
with "relation spark.AtomicValues does not exist" on PostgreSQL and "Cannot open database" on SQL Server. RavenDB has
no schema, so it never showed.
- `ISparkStoreReadiness.WaitAsync` returns once the store can serve background work. `SparkStoreReadiness` is ready
  at construction when no `ISparkSchemaMigrator` is registered (RavenDB, InMemory, a test's bare container).
  Otherwise `UseSpark` completes it after the schema step, or faults it with the step's exception.
- `AddSparkStoreReadiness()` registers it once: core calls it, and so does every feature with a background service.
  A container built without `AddSpark` still resolves it.
- Messaging's manager waits on it before the legacy cleanup and the lease. The retry sweeper needs no gate of its own,
  because it runs only while the lease is held. Cron, the subscription worker and the other hosted services adopt it
  when they are ported. They have (audit below, 2026-11-26).
- Rejected: catching the store error in each loop and retrying. That puts an error in every first boot's log, delays
  messaging by a poll interval, and leaves each new background service to rediscover the race.

*Evidence:* `NeutralMessagingTests` went from 0/3 to 3/3 on SQL Server and PostgreSQL. The RavenDB messaging set,
including the containers built without `AddSpark`, stayed green (227/227).

**The rest, audited 2026-11-26.** The plan audit found two hosted services whose first step reads a store table and did
not wait.
- **The services that now wait:**
  - `SparkSubscriptionWorker<T>` writes the subscription registry (`EnsureAsync`) on its first line. That is outside
    its catch, so a failure stops the host. This affected app-defined workers and Replication's
    `SyncActionSubscriptionWorker`.
  - Files' `DeferredFileScanService` sweeps the uploads at once.

  Both now wait on `ISparkStoreReadiness` before that first step. A stop during the wait ends them without a fault.
  The base worker takes the readiness through `[Inject]`. Messaging's `MessageFeeder` and `MessageSubscriptionWorker`
  pass the readiness their manager already waited on. `AddSubscriptionWorker<T>` and `AddSparkFiles` register it.
- **The services that need no gate:**
  - Cron already waits.
  - Contributions' shape check already waits.
  - `SparkUserBackfill` and `DashboardSampler` wait for `ApplicationStarted`. That comes after the pipeline is built,
    and `UseSpark` applies the schema synchronously while building it.
  - `MessageRetrySweeper` and Replication's retry sweeper delay first and run only under the lease.
  - The identity provider's key rotation and agent-client cleanup first tick after 6 hours.
  - `FilesReconcileService` touches only object storage at once; it reads the store at its first tick.
  - `DashboardNotifier` reads no store.
  - `OpenSearchSync` reads RavenDB's `IDocumentStore` directly.
- *Evidence:* `BackgroundStoreReadinessTests` (2). A substitute readiness signals when it is awaited and holds the
  service, because `BackgroundService` runs `ExecuteAsync` on its own task, so "nothing ran yet" after `StartAsync`
  proves nothing.
  - Red first: neither service ever awaited the readiness.
  - Green after the change: nothing touches the store before release, and the registry write or the first sweep
    follows it.
  - The worker, messaging, replication and file-scanning classes on RavenDB: 41/41.
  - `NeutralMessagingTests` + `NeutralFilesStoreTests` on PostgreSQL: 8/8.

**D42 (M7b follow-up, 2026-11-25): messaging queries a declared view, never the collection.** D40's plain collection
queries made RavenDB build `Auto/SparkMessages/ByExpediteAndNextAttemptAtUtcAndPriorityAndQueuedAtUtcAndStatusAndWakeUp`
on first use. CI failed on it: CodeCoverage forbids auto indexes (`DependencyOnboardingTamperEndToEndTests`, D-O22),
because an app cannot deploy them ahead of time. The owner confirmed it: "you need to query the index to prevent
auto-indexes".
- **The view.** `SparkMessages_ByState : SparkView<SparkMessageState>` holds the fields the feeder's page and the
  sweeper filter and sort on. Per D32 it is a RavenDB static index, an SQL view and LINQ in memory. `AddMessaging`
  declares messaging's assembly an index assembly, so `UseSpark` deploys the view with the app's indexes.
- **`ISparkSession.Query<TRow, TView>()` takes a view on every store.** SQL already did. RavenDB now queries the
  static index of the view's name, and InMemory renders the view. A native RavenDB index still works as before, and
  InMemory still refuses one.
- **Tests.** The hand-built RavenDB messaging fixtures derive from `MessagingRavenTestDriver`. It deploys the view and
  waits for it before the first test, as `UseSpark` does. The shared `RavenIndexHelper` stays native-only: the smoke
  fixture passes the whole test assembly, which holds SQL-only views RavenDB cannot render.
- **Staleness.** The page and the sweeper read the index as it is, as they did before D40. Neither waits: the
  subscription batch covers what the page misses, and every write re-checks its predicate on the loaded document.

**D43 (S16, 2026-11-25): map-reduce and multi-map indexes are views; SQL computes the reduce on read.** It supersedes
the aggregate tables of the S16 row and the §"Map-reduce index" table row (*round 1*).
- `SparkView<TRow>.Reduce` (`group … into g select new TRow { keys, g.Sum(…) }`) and several maps joined by
  `Concat` declare it once. RavenDB renders a multi-map index (`AddMap` per map, the `Reduce`); SQL Server and
  PostgreSQL a `UNION ALL` under `GROUP BY` that EF writes; memory a LINQ grouping.
- **Why not aggregate tables:** the owner's goal equates RavenDB indexes with SQL views. A view is always current, so
  reads need no non-stale wait, and set-based writes on an aggregated collection stay allowed (moderation's account
  deletion is one). Aggregate tables needed the old row on every write and refused `UpdateWhere`/`DeleteWhere`.
- **Cost, measured (spike S16-V, plan M7e):** 100k rows, no index on the group key: one key's cells in 10 ms
  (PostgreSQL) and 55–66 ms (SQL Server) warm; the whole 2,250-cell view in 23–33 ms and 565–711 ms.
- **Limits:** every map assigns every member (EF's set operations require it), a sum a map lacks is `0`, the reduce
  uses keys and scalar aggregates only, a reduce view has no `Id` and is never an entity type's default.
  - **Refused by name (2026-11-26):** EF's own errors for the first two did not say which map or member, so the view
    renderer refuses them first, with a `NotSupportedException` that names the view.
    - Maps that assign different members are refused, naming each map's source type and the members it leaves out.
    - A reduce that gathers a list per group (`g.ToList()`, `g.SelectMany(…)`) is refused, naming the members.
    - Tests: `RelationalViewTests.A_multi_map_whose_maps_assign_different_members_is_refused` and
      `A_reduce_that_gathers_a_list_is_refused`, red first on both SQL databases.
    - RavenDB has neither limit.
- **Fan-out maps (M7g, 2026-11-25):** `from x in s.Set<T>() from item in x.List select new TRow { … }` yields one row
  per list item (`Likes_Count`).
  - RavenDB renders `docs.SelectMany(x => x.List, (x, item) => …)`.
  - EF translates the primitive collection: `OPENJSON` on SQL Server, `unnest` on PostgreSQL.
  - Memory runs `SelectMany`, with a missing list read as empty, as RavenDB and SQL read it.
  - A fanned-out row has no `Id`.
  - Only the query-syntax pair (`SelectMany` with a result selector) is accepted. A `where` between the two `from`s
    makes transparent identifiers, which RavenDB's map shape does not take.
  - *Evidence:* `NeutralReduceViewTests` 6/6 on all four databases, with a two-map fan-out reduce through an update
    and a map-only fan-out.
  - SQL Server and PostgreSQL passed before any change: EF already translated the shape. RavenDB failed to deploy
    ("Could not find index"), and memory threw `NullReferenceException` on a null list.
- **One row-per-entity view per entity type, one SQL view per row type.** Two row-per-entity views over the same type
  make the catalog demand a `[DefaultIndex]`, so moderation folded `EntriesToCredit` into `Moderation_Ledger`.
- **RavenDB reads a map view's rows with `ProjectInto`** (`RavenSparkSession.ViewRows`), added at execution when the
  caller made no projection. Without it RavenDB returns the source documents and a computed column is lost (measured:
  `NeutralReduceViewTests.A_map_view_reads_its_computed_columns_…`). Not when the query is opened: RavenDB refuses a
  second projection, and callers project ids (`Select(r => r.Id)`) after their filters.
- **Moderation (S16's consumer) runs on any store:** 9 views (3 reduces, 6 row-per-entity), reads by id after a view
  query (D42), `ISparkSession.Query<TRow, TView>(TimeSpan)` where a read must include every write (RavenDB waits,
  SQL and memory do not need to).

*Evidence:*
- `NeutralMessagingTests` 4/4 on RavenDB, InMemory, SQL Server and PostgreSQL. The new assertion: on RavenDB a
  delivery leaves no `Auto/SparkMessages` index, and `SparkMessages_ByState` exists.
- RavenDB messaging 217/217, in 49 s against 5 min 13 s before the fixtures waited for the view. Before the fix, 13
  failed with `IndexDoesNotExistException` or a sweeper that found nothing in a freshly deployed index.
- Subscription worker, replication, storage, notifications, apps, webhooks and layering 663/663.
- `DependencyOnboardingTamperEndToEndTests` and `NoAutoIndex*` 39/39.

**D44 (S10, 2026-11-25): site search runs on every store. RavenDB keeps its index; the others search the
collections.** `spark.AddSearch()` no longer requires RavenDB.
- **RavenDB:** unchanged. It uses the generated `Spark_Search` multi-map index and `RavenSearchProvider`: Lucene,
  fuzzy matching, a score.
- **Every other store (SQL Server, PostgreSQL, memory):** `StoreSearchProvider` (`libs/search/…/StoreSearchProvider.cs`)
  answers.
  - **Fields:** for each searchable type it reads the type's search fields. These are the placeholders of
    `search.label` (else of the breadcrumb), then `search.fields`, kept when they hold a string or a string
    collection: the same fields the generator indexes.
  - **The prefilter, in the store:** every word must be a lower-cased substring of one field, and `IsDeleted` rows are
    excluded. Each word is a captured value, so SQL gets it as a parameter (`LIKE`, `strpos`), never as text. The
    query reads `take × 4` rows.
  - **The match, in memory:** every word must be a whole word of the text, split by `SearchTerms.Words`, the same
    tokenizer as the term. The last word may be a prefix while typing (suggest).
  - **Order:** more whole words first, then the type's order, then natural id order.
  - The selector picks the store's provider from `ISparkStore.ProviderName`.
- **Why not a multi-map view (D43):**
  - Relational views key a row beside the row (`KeyBesideRow`), which supports a single top-level `Select` only.
  - Ids from different collections have different key shapes (text, natural, integer, Guid converters), so a
    `UNION ALL` over the collections has no common key column.
  - The index row's `Terms` array and its `@last-modified` metadata have no SQL equivalent.
  - The collections already answer the question without a second schema object to migrate.
- **Limits:**
  - No typo tolerance: `search.fuzzy` needs RavenDB.
  - The SQL prefilter does not fold diacritics, so a stored `Café` is not found by `cafe` on SQL. The term is folded,
    and memory folds both sides.
  - The prefilter scans: it is a `LIKE '%word%'` on SQL. A full-text dialect (SQL Server `CONTAINS`, PostgreSQL
    `tsvector`) stays the opt-in of §4.1 (O5).
  - The OpenSearch engine syncs from RavenDB's `Spark_Search` index (spark_search_PRD D8), so `UseOpenSearch`
    requires RavenDB (store requirement "Spark.Search.OpenSearch", also `[SparkRequiresStore]` for SPARK060). Another
    engine (`UseProvider<T>`) is not bound to a store: it need not read that index.
- **Template:** `features.json` no longer pins Search and OpenSearchDescription to RavenDB. The OpenSearch engine
  stays pinned.

*Evidence:*
- `NeutralSearchTests` 3/3 on InMemory, SQL Server, PostgreSQL and RavenDB. It goes through the real endpoints
  (`/spark/search`, `/suggest`, `/resolve`): a search field, words from different fields, a string collection, a
  soft-deleted row, case, every-word-must-match, prefix only while suggesting, a substring that is not a word.
- RavenDB search, OpenSearchDescription and store requirements: 28 passed, 9 skipped (the OpenSearch container
  tests), 0 failed.
- Templates generator and manifest tests 17/17.
- `SearchStoreRequirementTests` and `SparkStoreRequirementTests` 8/8: search and a custom engine have no unmet
  requirement on InMemory, OpenSearch does.

**D45 (S9, 2026-11-25): Dashboards run on every store. RavenDB keeps its time series; the others keep sample rows.**
`spark.AddDashboards()` no longer requires RavenDB. Three RavenDB dependencies are replaced:
- **The statistics:** `ISparkStoreStatistics` (Abstractions) answers the `DatabaseStatistics` widget, through
  `StoreStatisticsProvider` (was `RavenStatisticsProvider`). It returns the size on disk, the document count, and each
  collection's count and size.
  - RavenDB: the two statistics operations, as before (`RavenStoreStatistics`).
  - SQL Server and PostgreSQL: an exact `COUNT` per collection table (`RelationalStoreStatistics`). Sizes come from
    the dialect's catalog queries: `sys.database_files` and `sys.dm_db_partition_stats`, or `pg_database_size` and
    `pg_total_relation_size`. A login without rights on the catalog still gets the counts, with no sizes.
  - Memory: the counts, no sizes (`null`, which the sampler skips).
- **The sampler lease (D29):** an `ISparkAtomicValues` value with JSON text, under the new key `dashboards/sampler`.
  RavenDB's old compare-exchange value `spark/dashboards/sampler` held a JSON object, which the text values of
  `RavenAtomicValues` (`GetCompareExchangeValueOperation<string>`) cannot read. A RavenDB migration
  (`M_202611251400_DashboardsSamplerLeaseRetired`, D46/M8c) deletes it against its index. During a
  rolling deploy, an old and a new instance hold different keys, so one slot may be sampled twice: a point is
  overwritten, never doubled.
- **The history:** the internal `IDashboardHistory`, chosen by `ISparkStore.ProviderName`
  (`DashboardHistoryRegistration.AddDashboardHistory`).
  - RavenDB: `DashboardStorage`, the time series of `SparkDashboards/{name}`, unchanged. CodeCoverage and SeoTester
    keep their history.
  - Every other store: `StoreDashboardHistory`. Each sample is a `SparkDashboardSample` row,
    `sparkdashboardsamples/{dashboard}/{series}/raw/{epoch ms}`, which expires after `RawRetention`. It is also added
    to the row of its rollup interval (`…/rollup/{epoch ms}`: `Sum`, `Count`, `LastAt`), which expires after
    `RollupRetention`. These are RavenDB's two policies, kept by hand. `LastAt` makes a slot written twice count once.
    The sampler is the single writer under the lease.
  - **Read:** a bucket at least as coarse as the rollup interval reads the rollups only (2,016 rows for the default
    7-day window). A finer one also reads the raw samples, and a bucket the raw samples still cover uses them. Buckets
    are whole minutes, as RavenDB's `group by N minutes`. The values are averages rounded to 3 decimals, oldest
    first, and an empty bucket has no point.
  - Never used on RavenDB, so its query of the collection builds no auto index (D42). Its table is unindexed beyond
    the key: a read scans the dashboard's rows, which retention bounds.
- **Why not one design for every store:** RavenDB time series are what production uses (CodeCoverage, SeoTester).
  Moving them to rows would drop their history for no gain, as Search keeps `Spark_Search` (D44).
- **Template:** `features.json` no longer pins Dashboards to RavenDB.

*Evidence:*
- `SparkStoreStatisticsTests` 1/1 on InMemory, SQL Server, PostgreSQL and RavenDB: per-collection counts, and sizes
  on the three databases.
- `NeutralDashboardHistoryTests` 4/4 and `DashboardEndpointTests` 8/8 (now `SparkTestDriver`), on all four:
  - two concurrent samplers claim each slot once;
  - the averages at 5-minute (rollup) and 1-minute (raw) buckets, with a slot written twice counted once;
  - the shipped statistics dashboard through `/spark/po/load`.
- The RavenDB Dashboards suite 24/24, CodeCoverage `HostMetricsDashboardTests` 11/11, and the templates tests 23/23.

**D46 (M8b, 2026-11-25): RavenDB's stored format may change, through a RavenDB data migration.** The owner, on the
IdentityProvider port: "You can also write a RavenMigration that corrects the documents - currently everything in
production is a ravendb database", and then: "My remark about how we CAN in fact modify the documents in the database,
counts for everything, also for the already-implemented features in this pull-request ... so it applies overall".
- A port takes the clean store-neutral design. Where that changes what RavenDB stores (a field moved to its own
  collection, a key renamed, a value reshaped), an `ISparkMigration` converts the production documents. It runs on
  RavenDB only (`ISparkStore.ProviderName`), with parameters for values and `RqlIdentifier` for identifiers.
- This applies to the milestones already done too: a design that kept RavenDB's old format only for its own sake is
  revisited (the list is in the plan, M8c), and stale values a port left behind are removed.

**D47 (M8b, 2026-11-25): the identity provider runs on every store.** `AddIdentityProvider` and `features.json` drop
the RavenDB pin. What replaces each RavenDB construct:
- **Sessions:** `ISparkSession` and `ISparkStore`. A second session is `ISparkStore.OpenSession()`. Optimistic
  concurrency is `ISparkSession.OptimisticConcurrency`, and a conflict is `SparkStoreConcurrencyException`.
- **Queries (D32, D42):** a query that filters a collection inside a service queries a `SparkView` instead, then
  batch-loads the rows it needs and compares exactly (a view compares case-insensitively). The views, in
  `Indexes/OidcViews.cs`:
  - `OidcTokens_ByState`, `OidcTokens_ByScope` (fanned out per scope): the disable cascade, the sweeps, logout;
  - `OidcGrants_BySubject` (the static index of that name, now a view; RavenDB redeploys it side by side);
  - `OidcDevelopers_ByStatus`: the administrators' queue of developer requests;
  - `OidcApplications_ByMember`, `OidcApplications_ByScope` (both fanned out), `OidcApplications_WithGoLiveReview`:
    "applications I administer" (the audit row filter), the scope-approval and the go-live queues;
  - `OidcAgentClients_ByLastUse`: the cleanup of unused dynamic agent registrations.

  `AddIdentityProvider` declares the package an index assembly, so `UseSpark` deploys its views with the app's. The
  keys (`from OidcKeys`) stay a scan of the collection, which builds no index. Query pages and row filters stay LINQ
  over `ISparkSession.Query<T>()`, as for every Spark type.
- **Finding an application by its client id** is the client-id reservation alone (`oidc/client-ids/{lower client
  id}` → the application id): strongly consistent on every store, where the `OidcApplications_ByClientId` index was
  eventually consistent. `ISparkSession.Reserve` writes the same compare-exchange key and value as the hand-written
  code did. The reservation is claimed in the application's own save (its before-save interceptor, through
  `SparkInterceptorContext.StorageSession`) and released in its delete's own save (the before-delete interceptor),
  so a refused write takes neither back. A RavenDB migration (`M_202611251310_OidcClientIdReservations`) claims the
  reservation of every application saved before reservations existed, leaves a key another live application holds to
  it (logged), and deletes the index.
- **Developer status** moves from a `Developer` field on the user's document to an `OidcDeveloper` document of its
  own, `oidcdevelopers/{sha256(lower user id)}` (a user id holds a `/`). The field was undeclared on the user type:
  only RavenDB keeps such a field, and a relational table has no column for it. A RavenDB migration
  (`M_202611251300_OidcDevelopersCollection`) moves every user's field into its document and removes it from the user.
  The administrators' queue queries `OidcDevelopers_ByStatus` and loads the users in one batch.
- **Users by id or e-mail:** `OidcUserDocuments` loads users as the application's user type in a session of its own
  (one session cannot track one id as two types); by e-mail through the e-mail reservation
  (`SparkUserReservations.EmailKey`), instead of a raw query of the users' collection. A user seeded without
  `UserManager` holds no reservation and is not found by e-mail.
- **The key ring** reloads every 5 minutes in the background: the store's session is asynchronous, and the signing
  path is synchronous. A failed reload keeps the loaded keys.
- **Expiry:** `ISparkSession.SetExpiry`/`GetExpiry`. RavenDB still writes `@expires`.
- **Bulk writes:** the disable cascade and `LastUsedAt` use `UpdateWhereAsync`. The cascade finds its ids through a
  view first, so RavenDB's patch is `id() in (…)`, which needs no index.
- **`INC:Tokens`** (a per-application token counter that nothing reads) stays a RavenDB time series, written through
  the RavenDB session, as Dashboards keep theirs (D45). Other stores write no counter; the token rows themselves are
  the record.
- **The historic migration** `M_202610090900_OidcDataModel` converted RavenDB documents from before the data model. It
  now runs on RavenDB only; a new database of another store has nothing to convert.
- **Tests:** the OIDC suite moves to the neutral harness, so it runs on InMemory, SQL Server, PostgreSQL and RavenDB.

**D48 (M8b, 2026-11-25): a `SparkView` is an entity type's default index only when it claims `[DefaultIndex]`.**
default_index_PRD D3 elects an entity type's sole index *with a view* implicitly, because a `[FromIndex]` view is an
author's choice; with two such indexes and no claim, startup throws. Every `SparkView` has its row type, so having one
chooses nothing. The identity provider's four helper views over `OidcApplication` (D47) made every host refuse to
start ("4 indexes with a [FromIndex] view map entity type OidcApplication ... and none claims it", 443 of 603 OIDC
tests on RavenDB, 2026-11-25). With one helper view, it would have become the query shape of any application that
lists the entity type, under an `indexName` nobody chose.
- **Both catalogs** (RavenDB's `IndexCatalog`, core's `SparkViewCatalog`) elect a view only by its claim. A native
  index keeps D3 unchanged. `[GenerateIndex]` already writes `[DefaultIndex]` on the view it emits without RavenDB.
- **A fan-out view** (`SelectMany`, a row per list item) has no row per entity, like a reduce or a multi-map (S16). Its
  entry maps no collection, and a claim on it is refused.
- No model file in the repository bound a helper view (`indexName`/`queryType` of `Moderation_*`, `Oidc*` or
  `SparkMessages_*`: none in `apps/` or `libs/`).
- Tests: `SparkViewTests.The_view_catalog_enters_a_view_with_its_row_as_projection_and_elects_no_view_it_does_not_claim`,
  `SparkViewTests.A_fan_out_view_has_no_row_per_entity`, `RavenSparkViewTests.The_catalog_elects_no_view_it_does_not_claim`
  (red first: all three failed before the change).

**D49 (M8b, 2026-11-25): the identity provider's documents with computed ids are Natural.** On a relational store, the
default Guid kind makes the key a `uniqueidentifier`/`uuid` column. It refused 158 of 602 OIDC tests per SQL store
("'oidctokens/{hex}' is not an id of collection OidcTokens", 2026-11-25).
- **`OidcToken`**: every id is a SHA-256 of the handle it stands for (`OidcTokenReference`, `OidcRequestReference`,
  device codes), set before the store. A bearer value is never persisted, so the id cannot be a Guid.
- **`OidcGrant`**: the id hashes the (subject, application) pair (`OidcGrantReference`), one grant per pair by
  construction.
- **`OidcApplication`**: the provider sets every application's id. An agent client's id derives from its client id
  (`oidcapplications/agent-{hash}`, `OidcAgentClients.ApplicationId`, a point load). Every other id is
  `oidcapplications/{guid}` in the 32-digit form. The Guid kind fails both shapes: a relational key refuses the agent
  ids, and `ISparkIds.Parse` rewrites a 32-digit key into the dashed form, which names another document on RavenDB.
- **Why not re-key (D46):** production CodeCoverage and SeoTester run `ForMcp().AgentsOnly()`. Their agent clients'
  grants hash the application id, and tokens, refresh families and agent grants hold it. A re-key would rewrite five
  collections of live data to gain a column type. The Natural kind changes no stored document. On a relational store,
  the key becomes a text column holding the part after `oidcapplications/`.
- The other IdP types with a business key (`OidcResource`, `OidcKey`, `OidcRefreshFamily`, `OidcDeveloper`) were
  already Natural.

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
*Built in M5b:* `Nulls_sort_first_ascending_and_last_descending` passes on all four providers. RavenDB already sorts
nulls first. Npgsql's `ReverseNullOrdering()` went internal in 11.0, so the PostgreSQL dialect replaces
`IQuerySqlGeneratorFactory` with one that constructs Npgsql's generator with that flag on.

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
  **On PostgreSQL that is any transaction on the server** (M4e, 2026-11-25): transaction ids are cluster-wide, so
  an open transaction in another database holds the horizon back too. Late, never skipped; give a Spark database a
  server of its own when other databases there run long transactions.
- **As built (M4e):** the horizon doubles as the probe (it moves whenever a transaction commits), so a poll with
  nothing new is one query. Tombstones are kept a day.

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

**The rule itself shipped with typed ids in #713** (2026-11-26). [`typed_ids_PRD.md`](typed_ids_PRD.md) D7 records it:
where Spark lowers an id, how `Parse` normalises a key (a Guid to its `D` form, digits without leading zeros), the
RavenDB conventions (`ApplySparkConventions`), comparing a loaded id with a stored string ignoring case, hashing an id
lower case, and the object-store keys. O15 (no re-key, and no migration that lowers document ids) is there too. What
follows is the relational side.
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
- Code that builds an id prefix itself (`GetCollectionName(type) + "/"`) goes through `Id<T>` or the convention
  above. Prefix checks that already compare `OrdinalIgnoreCase` (`SoftDeleteRawDeleteGuard.cs:36`) keep working.
- **Collection collisions fail the model build (L1, round 10, unanimous).** Two entity classes whose collection
  names are equal ignoring case, or one class name in two namespaces, share one RavenDB collection today (RavenDB
  compares collection names ignoring case, and `FindCollectionName` ignores the namespace), and would share one
  SQL Server table name under its default collation. Building the relational model fails on such a pair
  (`SparkRelationalModel`).
- `LoadStartingWith` over natural keys is `LIKE @prefix ESCAPE '\'`, with `%`, `_` and `\` escaped; the prefix is
  lowered first.
- Conformance cases pin the canonical round trip, `GetById` with an upper-case variant (found on every provider), the
  prefix escaping, FK equality under the copied collation, and non-ASCII keys (`ToLowerInvariant` runs in .NET, so
  the providers never fold case themselves). The debug guard that was to refuse storing a new id that is not lower
  case was not built (`typed_ids_PRD.md` §2).
- PostgreSQL compares text case-sensitively, but no Spark data lives there yet: the relational providers write lower
  case from the first row. A tool that copies a RavenDB database into SQL lowers the ids then.

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

**Rules 1–7, 9 and 10 shipped with typed ids in #713** (2026-11-26). [`typed_ids_PRD.md`](typed_ids_PRD.md) D23
records them, with the deviations found while building them: `FromTrusted` is public and hidden from IntelliSense, a
Guid key is canonical in its `D` form (not `N`), and `__DocumentId` is internal (not private). The rule numbers are
kept, because the code comments cite them. Their relational parts:

- **Rule 2, the Guid kind:** new Guids stay random (v4), not UUIDv7. **S-ID2 measured no large difference (§8.4):**
  100k EF inserts take the same time within noise on both databases. v7 only shrinks PostgreSQL's key index by 21%.
- **Rule 5, the id before `StoreAsync`:** on SQL, `ids.NewAsync<T>()` draws from EF's HiLo sequence for the Integer
  kind. HiLo burns ranges on abandoned saves.
- **Rule 7, the framework's own id lookups** (detail load, reference resolution, row security, lookups) go through a
  provider hook: `Load` on RavenDB, key equality on EF. The commercial framework does the same. The hook has a batch
  form (`LoadMany`: one `IN` query on EF), so reference resolution is never N+1. D30 uses the same hook to name the
  referenced row in an FK-violation message.

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
- **Defect found and fixed 2026-11-26: a list's order was lost on SQL.**
  - **The defect:** a document keeps a list in its stored order. A child table (and D26's join table) had no ordinal
    column, so rows loaded in key order. So an AsDetail list showed reordered after a reload, and `[Sortable]` (order is
    the array position) could not persist a drag.
  - **Evidence:** `RelationalStoreTests.Child_and_reference_lists_keep_their_order` was red on SQL Server (LocalDB) and
    PostgreSQL 18. Jobs stored as c, a, b loaded as a, b, c; skills stored as sql, csharp loaded as csharp, sql.
  - **The fix:** every child and join table has a framework column `SparkPosition` (`SparkDbContext.PositionProperty`).
    - On save, the session numbers each child row by its place in the list (`WriteChildPositions`), and writes a join
      row's position. A kept join row moves in place rather than being deleted and inserted. A reorder is a change, so
      it moves the owner's version, as it changes a document on RavenDB.
    - On load, join rows are ordered by position.
    - EF loads an owned collection in key order and takes no ordered include for it. So a materialization interceptor
      (`SparkListPositions`) notes each child row's position, and the session sorts the lists it has just loaded.
    - A list holding a row the session already tracked is left alone, because its order may be an unsaved edit.
  - **Verified:** the test is green on both SQL databases, and Relational.Tests passes on both.
    - SqlDemo has no child or join tables, so its migration is unchanged.
    - RavenDB and memory keep a list's order in the document. Their primitive-list order is already asserted by the
      conformance suite's `PatchAppend` test.

**D26 (round 3): references are real foreign keys, with no navigation properties.**
- `Id<T>` and `Id<T>?` are FK columns.
- `List<Id<T>>` maps to a framework-owned join table, which EF sees as a join entity, never as an EF collection.
- The `Id<T>` property is itself the reference attribute, so no FK column surfaces twice in the UI model. That is
  the commercial framework's "hide the FK scalars" rule, met by construction. The child tables' owner keys and the join tables'
  columns are framework-only.
- **Reference integrity** (a delete guard, and what happens on rename) on every provider is a **separate issue** with
  its own investigation.

**D27 (round 3): a typed contract.** `ExistsAsync`, `Delete` and `GetVersionsAsync` take `Id<T>` (about 89 call
sites). With D23 the type is inferred: `Delete(person.CompanyId)` can only delete a `Company`. Shipped in #713
(`typed_ids_PRD.md` D27).

**D30 (the commercial-framework comparison): database constraint errors become validation messages.**
- The dialects translate unique, foreign-key, not-null and length violations into Spark's validation error, naming
  the attribute:
  - SQL Server error numbers 2601, 2627, 547, 515, 2628 and 8152
  - PostgreSQL SQLSTATEs 23505, 23503, 23502 and 22001
- A raw database exception never reaches the client.
- The commercial framework's SQL Server package does this.

**Full-text search: `LIKE` first, an FTS dialect opt-in later** (the commercial-framework comparison, owner 2026-11-24).
- The commercial framework runs production apps on SQL with `LIKE` only. It has no `CONTAINS`, `FREETEXT` or `tsvector` anywhere.
- So `Search()` on relational databases translates to `LIKE` (escaped). S10 moves off the critical path. *(Done
  2026-11-25: site search on SQL is D44.)*
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

*Built in M5c (2026-11-25):* `tests/MintPlayer.Spark.Relational.Tests/Behaviour/`, 25 tests, all green on SQL Server
(local 2022 instance) and PostgreSQL 18 (Docker). Each class creates its own `spark_beh_<12 hex>` database and drops
it afterwards; without the dialect's variable (`SPARK_TEST_SQLSERVER`, `SPARK_TEST_POSTGRESQL`) the tests skip. Every
row above has a test except O13, which tests EF's mapping rather than the server and joins the conformance suite with
the O13 tier port (M6). Beyond the table: the SQL Server unique-index error is 2601 (the constraint's is 2627, both
mapped), and the equality cases record what the servers do by default (SQL Server ignores case, PostgreSQL does not).
O20 (decided) added `O20_an_ICU_level2_collation_…`, which records what the PostgreSQL dialect's collation relies on,
on 17 and 18.

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
  *Built in M5b, without a backfill:* the LINQ provider cannot say "exists", but it sends a null as a parameter
  (`Rating = $p0`, `p0 = null`). `RavenAbsentNullRewriter` rewrites those comparisons in the RQL's `where` clause
  before it is sent: `F = $p` → `(F = $p or (true and not exists(F)))`, `F != $p` → `(F != $p and exists(F))`. The
  field name is checked with `RqlIdentifier.FieldPath`, and values stay parameters. Spark's session applies it to
  `Query<T>`, `Query<T, TIndex>` and the set-based writes; a raw RavenDB session keeps RavenDB's meaning
  (`AbsentVersusNullFieldTests` still pins that). The conformance case `An_absent_nullable_field_is_null_to_queries_and_sorting`
  covers `==`, `!=`, negation, conjunction, sorting (absent sorts with the nulls) and `DeleteWhere`.
- Sorting and distinct values (the `< none >` entry) are conformance cases too: RavenDB sorts an absent field
  apart from the default, and the result must match SQL's.
- The generator warns on an initializer on a persisted value-type property (`int Priority { get; set; } = 5`): a
  document without the field shows 5 but is queried as 0.

**JSON columns on PostgreSQL** use `jsonb`, which normalises the text (keys reordered, whitespace changed, duplicate
keys collapsed; S12). That is harmless: the dirty check diffs the snapshot taken at load, not the stored text.

**Reservations** (`ISparkSession.Reserve/Release`) join the session's `SaveChanges` transaction. That replaces
`UserStore.cs:206`'s cluster-wide transaction.
*Built in M3g:* a key whose holder document no longer exists is taken over, which is the callers' existing rule. On
RavenDB a wrapped session is not cluster-wide, so the claim (compare exchange) precedes the documents' write. A failed
write frees the claim. ⚠️ A second claimant arriving between the claim and the owner's first write sees no owner and
takes the key over. That race exists in `UserStore` and `OidcClientIdReservation` today; the relational transaction
and InMemory's commit lock close it. Evidence: `SparkStoreConformanceTests` reservation cases, green on both providers.

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
| FullTextSearch | `LIKE`; FTS opt-in later | `LIKE`; `tsvector` opt-in later | site search on `LIKE` + whole words in memory (D44); the capability itself stays RavenDB's |
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
- Left for S-ID4 at the time: the byte-diff and the queries on a **copy of the production `Coverage` database**.
  C9 and P2 show both on synthetic documents only. **Done since: §8.5,** 15/15 on a copy of the development
  database; a rerun on a production export is optional.
- Left for S-ID5: the collision build failure (L1) is model-build code, tested with M3's unit tests. It is not a
  RavenDB behaviour to spike.

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
   **Built 2026-11-26** (found missing by the milestone audit, plan §1): `SparkSplitQueries`, an
   `IQueryExpressionInterceptor` on every `SparkDbContext`, wraps each query root over a type with two or more child
   tables (its own, its derived types', nested ones) in `AsSplitQuery()`, so loads, session queries and the change
   feed all split. A type with one child table keeps the single statement: no extra round trip, one row per child. A
   query that states `AsSingleQuery()` or `AsSplitQuery()` keeps it. The price: the split statements are not one
   snapshot, so a write between them can pair an owner with newer child rows. A RavenDB document and a single statement
   are read whole. Proven by `A_type_with_two_child_tables_reads_each_in_a_statement_of_its_own`: red on both servers
   (one statement, `LEFT JOIN "RelPeopleCourses" … LEFT JOIN "RelPeopleJobs"`), green with the interceptor.
   A view is the exception: it is one `SELECT`, and the renderer states `AsSingleQuery()`. Split, EF's
   `ToQueryString()` adds the note "This LINQ query is being executed in split-query mode" to the view's SQL, which
   broke every relational host on CI (`A_view_over_a_type_with_two_child_tables_is_one_statement`).
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

### 8.13 S-ID6: RavenDB static indexes over typed ids (2026-11-24, during M2)

**Question.** 36 files declare static RavenDB indexes, and their maps read `x.Id` and `LoadDocument(x.RefId)`. After
D23 the identity member is the generated `__DocumentId`, the own `Id` is not in the body, and references are
`Id<T>`. What does RavenDB's index translator write for those maps?

**Without a rewrite (RavenDB 7.2 client):**
1. `p.Id.Value` and `p.Id.ToString()` are written as `p.Id.Value` and `p.Id.ToString()`. The translator writes
   `Id(p)` only for the identity member, so these read a body field that isn't there.
2. `LoadDocument<T>(p.CompanyId!.Value.Value)` is written as `LoadDocument(p.CompanyId.Value, …)`. The stored
   reference is a string with no `Value` member, so the map fails on every document and RavenDB marks the index
   errored: "1 map attempts, 1 has failed".
3. `LoadDocument<T>(p.CompanyId.ToString())` works as written.

**With `RavenIndexIdRewriter`** (an expression visitor in the RavenDB package):
- The own `x.Id`, `x.Id.Value` and `x.Id.ToString()` become the `__DocumentId` member, which prints as `Id(p)`.
- A bare `x.Id` in an `Id<T>` slot becomes `Convert(__DocumentId, FromTrusted)`, which prints as `(Id(p))`.
- A reference's `.Value` becomes `.ToString()`.

The maps then translate to `Id(p)` and `LoadDocument(p.CompanyId.ToString(), "IdCompanies")`. There are 0 index
errors, the rows hold the right id, reference and loaded name, and `Where(r => r.CompanyId == acme.Id)` on the index
returns the row through the query value converter.

**Decision.**
- `SparkIndexCreationTask` rewrites `Map` in `CreateIndexDefinition`, and `SparkMultiMapIndexCreationTask` hides
  `AddMap` with a rewriting one. The rewrite is idempotent.
- Authors write typed ids as they would anywhere else.
- An index on a converted entity must derive from a Spark base: a raw `AbstractIndexCreationTask` is converted along
  with its entity.

Test: `RavenTypedIdIndexTests` (single map with `.Value` forms, with `ToString()` forms, and the multi-map).

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

**Nothing is open.** O13 (decided 2026-11-24) is built since 2026-11-26: that day's audit found it had never been
implemented (SQL ignored every getter; no generator emitted the `__stored_` field). O21 was found in the plan audit and decided by the owner on 2026-11-26 (built). O18–O20 were
found in M4b/M5b and decided by the owner on 2026-11-26 (built).
Everything before them was answered on 2026-11-24. Grilling round 2 (owner) answered O1, O2, O3 and O6. Round 3 reversed
D1 and added D23–D27, and the commercial-framework comparison added D30. Round 5 decided O10–O13. The owner
decided O14–O17, and O8 and O9 last. The plan §3 has the log.

- ✅ **O1, one PR:** **one pull request** (rule 8), draft PR #697.
- ✅ **O2, Tier 4:** **both identity_provider and dashboards are ported** in this work. S9 (samples table, rollups,
  retention) is in scope.
- ✅ **O3, demo app:** **a new `apps/SqlDemo`**, dedicated to the relational providers. It is the E2E proof, so it
  uses the ported feature libraries, not core alone.
- ~~**O4, minimum SQL Server**~~ Answered by S1/S2: 2022 and 2025.
- **O5, full-text:** `LIKE` came first (§3, D44: site search on every store). A full-text dialect (`CONTAINS`,
  `tsvector`) stays deferred: it needs a full-text catalog or a generated column per searchable type.
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
  - **Implemented (M7d, 2026-11-25)** behind `ISparkRevisions`, one implementation per store. Deviation: set-based
    updates read the changed rows back by key inside the transaction (one SELECT after the chunked UPDATE) instead of
    `OUTPUT`/`RETURNING`, which EF's `ExecuteUpdate` does not expose; same atomicity, one extra round trip, and only
    for opted-in types. ⚠️ Relational versions count per document and restart after a delete and re-create.
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
  - **Built 2026-11-26.** The audit that day found the decision unimplemented: `SparkDbContext` mapped a
    `__stored_<Name>` field when one existed, but nothing emitted it (only spike S2b had hand-written one), so SQL
    ignored every getter and HR's `FullName` could not be filtered or sorted there.
    - `SparkStoredGetterAttribute(property, formula)` (`MintPlayer.Spark.Attributes`, `FieldPrefix = "__stored_"`).
    - `StoredGetterGenerator` (`MintPlayer.Spark.LibraryGenerators`). The stored types are an `Id<Self>` entity, a
      type deriving from one, and a `[ValueObject]`. The members are public instance getters with a body, with no
      setter, not an override, and not `[JsonIgnore]`. For each one it emits `private T __stored_<Name>;` and
      `[SparkStoredGetter("<Name>", "<sha-256 of the body's tokens>")]` on the type. Trivia doesn't count, so
      reformatting or a comment keeps the hash.
    - Two diagnostics:
      - SPARK067 (warning): no field possible. Either a non-partial derived type (SPARK064 and SPARK016 already
        cover the others), or a record, whose synthesized equality would include the field.
      - SPARK068 (warning): the getter reads the clock (`DateTime`/`DateTimeOffset` `Now`/`UtcNow`/`Today`,
        `Environment.TickCount`, `Stopwatch`, any `TimeProvider`) or an expression of another entity type.
    - `SparkDbContext.MapValue`:
      - A get-only member with the field takes the column path of a settable member of its type: JSON, primitive
        list, enum by name (O21), id, or scalar. On top of that it gets `HasField` plus `PreferFieldDuringConstruction`.
      - The field is looked up on the declaring type.
      - A get-only id is a plain value, with no foreign key.
    - `modelHashes.json` `storedGetters`, keyed `{Entity}.{Property}`, including a base type's getters. It is part
      of the model hash when non-empty, so a model without stored getters keeps its hash. A new or changed formula
      fails verification with the line "stored getter X: formula changed". Synchronize prints the changed and the
      new getters, says saved rows keep the old value until saved again, and points to a data migration
      (`ISparkMigration`).
    - RavenDB documents are unchanged: `StoredGetterRavenDocumentTests` shows that Spark's contract resolver writes
      the getter and not the private field.
    - **Evidence:**
      - `StoredGetterGeneratorTests`: 12 tests, green. Every clock is covered, and a read of the type itself is not
        reported.
      - `A_stored_getter_is_written_on_every_save_and_queries_as_its_column` (PostgreSQL 18 + LocalDB):
        - red first, on `RelPerson.Home` (JSON): "could not be mapped";
        - green after the `MapValue` change;
        - covers string, bool, enum, list, JSON and child-table getters;
        - a change to `Name` rewrites `Display`;
        - filter, sort, ignore-case;
        - no column without the field or with `[JsonIgnore]`.
      - The neutral `A_get_only_member_is_stored_and_filters_and_sorts`: green on all four stores.
      - `StoredGetterHashTests`: 5/5 (formula changed, new, removed), plus the existing hash and gate tests (60).
      - CI was green on bf8c643b3. Its coverage showed untested clock arms and new/removed drift lines; tests for both
        followed.
    - **The apps:** the solution build gives no SPARK067/068 warning. `--spark-verify-model` then failed exactly
      for the models that gained stored getters:
      - CodeCoverage: 7 — `Account`/`Repository.OwnerKey`, `Commit.Date`, `UpcomingChangelog.Notes`/`Packages`,
        `VersioningRun.Tags`/`RefusedPackages`;
      - HR: `Person.FullName`;
      - MintPlayer: `Person.FullName`.

      All of these RavenDB already writes into the documents. Their `modelHashes.json` was re-synchronized, and
      verify passes. SqlDemo's synchronize wrote no migration ("no model change"), so no feature-package table
      moved.
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
  - **Reaffirmed 2026-11-26** (owner: "No migration needed"), after #713's review found grid rows of pre-upgrade
    documents without an etag: the fix compares the stored id ignoring case. `typed_ids_PRD.md` O15 has the record.
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
- **O18, `DateTime.Kind` on SQL (decided 2026-11-26 by the owner: (b), read as UTC; built):** the base
  `SparkRelationalDialect.ConfigureConventions` maps every `DateTime` and `DateTime?` through `UtcDateTimeConverter`: a
  local value is written as its UTC instant, every value goes to the column as unspecified ticks (Npgsql's rule), and
  reads back with Kind Utc. PostgreSQL's and SQL Server's dialects call the base. Evidence: the conformance suite's
  `Values_round_trip_through_a_new_session` now asserts Kind Utc for a UTC value on every store (red on both SQL
  databases first), `UnspecifiedReadsAs` is Utc on SQL, and `A_local_DateTime_is_stored_as_its_utc_instant` (with a
  `DateTime?`) and `A_utc_DateTime_filters_against_the_stored_value` are green on SQL Server (LocalDB) and PostgreSQL 18:
  74/74; RavenDB and InMemory 83/83. The analyzer note is SPARK066 (Info, `RelationalDateTimeAnalyzer`): a public
  `DateTime`/`DateTime?` property of a type a `SparkContext` exposes as `IQueryable<T>` (or of its base type), raised
  only where the compilation references a relational store (`SparkRelationalDialect` resolves), since RavenDB keeps
  the kind. `RelationalDateTimeAnalyzerTests`: red first (no analyzer), then green; the base type's property is
  noted, `DateTimeOffset`/`DateOnly`/static/unstored properties are not, nor anything without a relational store.
  The question as it stood (found in M4b, 2026-11-25): a document keeps a `DateTime`'s kind (RavenDB writes
  `Z` for UTC, nothing for Unspecified); a `datetime2` or `timestamp without time zone` column keeps the ticks only.
  The store conformance suite (`Values_round_trip_through_a_new_session`) shows it: on both SQL databases a UTC value
  reads back with the same ticks and Kind Unspecified. PostgreSQL makes it worse: Npgsql refuses to write a UTC
  `DateTime` to `timestamp` and a non-UTC one to `timestamptz`, so M4b writes the ticks to `timestamp` whatever the
  kind (`PostgreSqlSparkDialect.ConfigureConventions`).
  - (a) **Interim, in place:** ticks only, read as Unspecified, on both databases. Code that calls
    `ToUniversalTime()` on a value it wrote as `DateTime.UtcNow` shifts it by the server's offset.
  - (b) **Read as UTC** (a converter that sets Kind Utc): right for `UtcNow` values, which is what Spark and the
    feature libraries write; a `datetime-local` value written Unspecified would serialize with `Z` and display
    shifted in the browser.
  - (c) **Keep the kind:** a hidden column per `DateTime` property, or encode it. Lossless, but every `DateTime` costs
    a column, and EF's converters cannot span two columns, so it needs its own mapping.
  - Recommendation: (b), plus an analyzer note steering new code to `DateTimeOffset` (D5) and `DateOnly`. The suite's
    `KeepsDateTimeKind` switch skips only the kind assertions until this is decided.
- **O19, a renamed attribute on SQL (decided 2026-11-26 by the owner: (b), rename + alter, with the mirror split;
  built):** `SparkMigrationsModelDiffer` overrides EF's column diff. It pairs a table's columns by attribute id
  (`Spark:AttributeId` on the property) first, then applies EF's own seven rules (name, property + path, property, shape
  + property, shape + annotations, shape), each only where one side has no attribute id: the key, the stamp, a snapshot
  from before attribute ids. Paired, EF's `Diff(IColumn, IColumn)` emits `RenameColumn`, then `AlterColumn` for the new
  type. Two attributed columns with different ids are never paired, so the mirror case becomes a drop and an add.
  Replacing the rules, not correcting the operations afterwards, is required: `DiffCollection` records every pair in
  the `DiffContext` the index and key diffs read. `RefuseSplitAttributes` is gone. Evidence:
  - `RelationalSchemaScaffolderTests` (no server, both dialects). Name (text) → Rank (int) with one id gives a single
    `RenameColumn` and a single `AlterColumn`, with different ids a drop and an add. Name → Title (both text) is
    renamed only with one id. Red before the override (4 cases), green after. `The_snapshot_keeps_each_columns_attribute`
    shows the ids reach the snapshot, which the next migration diffs against.
  - `RenamedAttributeTests` on SQL Server (LocalDB) and PostgreSQL 18, data applied through the operations. `int` Score
    42 → `long` Points reads 42. `string` Label "not a number" → `int` Number fails the migration: PostgreSQL with
    42804 "cannot be cast automatically", SQL Server with "Conversion failed". PostgreSQL refuses any type change that
    needs a USING clause (text → int), even for text that holds numbers, because Npgsql emits none. SQL Server converts
    value by value. Either way the migration fails, and the column is never silently emptied; the developer edits the
    scaffolded migration.
  The question as it stood: D24 refused a migration that dropped and re-added
  the column of one attribute id (`RelationalSchemaScaffolder.RefuseSplitAttributes`), because applying it loses the
  column's data. Measured: EF's differ renames a column itself when only its name changed (its heuristic matches
  columns of the same shape, even for unrelated attributes), so the refusal fires on a rename **with** a type change.
  - (a) **Interim, in place:** refuse, and tell the developer to give a really new attribute a new id.
  - (b) **Rewrite the pair** into `RenameColumn` + `AlterColumn` when the ids match; EF converts the data where the
    database can, and a failing conversion fails the migration, not silently.
  - (c) Leave the migration to the developer (edit the scaffolded file).
  - Recommendation: (b); it keeps the data for every convertible change and fails loudly otherwise. The flip side
    of EF's heuristic (an unrelated drop + add of the same shape becomes a rename, carrying data into a new
    attribute) is the mirror case: (b) would also split that rename when the ids differ.
- **O20, case in string equality (decided 2026-11-26 by the owner: (a), ignore case everywhere; built):** every
  string comparison a query makes ignores case on every provider, as on RavenDB. Measured first on PostgreSQL 17.12
  and 18.7 with a probe collation (ICU `und-u-ks-level2`, nondeterministic), recorded as
  `PostgreSqlBehaviourTests.O20_an_ICU_level2_collation_ignores_case_and_ILIKE_needs_the_default_collation`:

  | On a nondeterministic column | 17.12 | 18.7 |
  |---|---|---|
  | `=`, `IN`, `left()`/`right()` = | ignores case | ignores case |
  | `LIKE`, `strpos` | refused ("nondeterministic collations are not supported for substring searches") | ignores case |
  | `ILIKE`, `starts_with` | refused | refused |
  | `(x) COLLATE "default" ILIKE p`, `strpos(lower((x) COLLATE "default"), lower(p))` | ignores case | ignores case |
  | `('RED' COLLATE ci) = ANY (text[])` | ignores case | ignores case |
  | `DISTINCT`, `GROUP BY` | fold `Alpha` and `ALPHA` into one | same |

  Built:
  - **PostgreSQL:** `PostgreSqlSparkDialect.TextColumn` gives every ordinary string column (and a stored computed one)
    the collation `spark_ignore_case`, which framework migration `SparkFramework0004` creates (`CREATE COLLATION IF NOT
    EXISTS`, through `SparkRelationalDialect.IgnoreCaseFrameworkSql`). Keys stay `C` (D7), so the key scans' `LIKE`
    still works on 17. `SparkQuerySqlGenerator` renders every `LIKE`/`ILIKE` as `(x) COLLATE "default" ILIKE p`, `strpos`
    over `lower()` on the default collation, and `item = ANY (text[] column)` with the collation on the item. The
    collation cannot go on the `text[]` column itself: EF then fails to translate `List<string>.Contains` (measured on
    both databases, reverted).
  - **SQL Server:** nothing; ordinary columns take the database's case-insensitive default collation.
  - **InMemory:** `IgnoreCaseQuery` wraps the session's and the views' queryables and rewrites string `==`, `!=`,
    `Equals`, `StartsWith`, `EndsWith`, `Contains` and a string list's `Contains` to `OrdinalIgnoreCase`. A set keeps
    its own comparer.
  - **ICU is required** on the PostgreSQL server. Every server tested has it: the `postgres` Docker images CI runs, and
    the embedded test server (zonky's binaries: `icu*77.dll` on Windows, `--with-icu` in its Debian and Alpine build
    scripts for every non-lite build).
  - Evidence: the neutral conformance cases `String_comparisons_ignore_case` (`==`, captured `==`, `!=`, a key list's
    `Contains`, `StartsWith`/`EndsWith` with a constant and a captured value, a tag list's `Contains`) and
    `Substrings_and_column_patterns_ignore_case` (`Contains`, `!StartsWith`, a pattern read from another column;
    RavenDB opts out, its LINQ has no `Contains`). Red first on PostgreSQL and InMemory; green on RavenDB, SQL Server
    (LocalDB), PostgreSQL 17.12 (39/39 conformance) and 18.7, and InMemory. SqlDemo's migration, regenerated, puts
    the collation on its 49 text columns; `--spark-migrate` applies it to an empty database.
  - What it costs: `DISTINCT` and `GROUP BY` fold case (as RavenDB's facets do); a unique index on a string column
    refuses values that differ only in case; a `LIKE` on a text column cannot use a B-tree index; a `text[]` filter
    written with `@>` (a GIN index's operator) is not rewritten and still respects case; a complex value stored as JSON
    is not collated.

  The question as it stood (found in M5b, 2026-11-25): measured with the store conformance suite, a
  row named `Alpha` queried with `Name == "ALPHA"` and `Name.StartsWith("al")`:


  | Provider | `==` | `StartsWith` | Why |
  |---|---|---|---|
  | RavenDB | found | found | the auto-index's default analyzer lowercases terms |
  | SQL Server | found | found | ordinary columns take the database's case-insensitive collation (only keys are `BIN2`, D7) |
  | PostgreSQL | not found | not found | the default collation is deterministic |
  | InMemory | not found | not found | LINQ to objects compares ordinally |

  An app moved from RavenDB to PostgreSQL silently loses rows from every filter that differs only in case. Search
  already ignores case on both SQL databases (`ILIKE` on PostgreSQL).
  - (a) **Ignore case everywhere** (RavenDB's meaning, which apps were written against): PostgreSQL text columns get a
    nondeterministic ICU collation (`und-u-ks-level2`), which PostgreSQL 18 also honours in `LIKE`; on 17 the dialect
    rewrites `StartsWith` to `ILIKE`. InMemory rewrites string comparisons to `OrdinalIgnoreCase`. Keys stay binary (D7).
  - (b) **Respect case everywhere:** RavenDB would need exact-match analyzers on every index field, which changes the
    behaviour of every existing app.
  - (c) Leave it to the provider and document it.
  - Recommendation: (a). The trap: a nondeterministic collation makes `DISTINCT` and `GROUP BY` fold `Alpha` and `ALPHA`
    into one value, as RavenDB's facets already do. The conformance guard keeps its data unambiguous in case until this
    is decided.
- **O21, how an enum is stored on SQL (decided 2026-11-26 by the owner: (a), by name, `[Flags]` stays an integer;
  built):** an enum is stored by its name on every provider, and sorts by its name. The owner's condition: if sorting
  by name were not convenient on SQL Server, keep integers and document it. It is: a column that holds names sorts by
  name with a plain `ORDER BY`, on its ordinary collation.

  Built:
  - **SQL (both):** `SparkDbContext.PlainColumn` gives every enum property without `[Flags]` (nullable too, and a
    stored computed one) `HasConversion<string>()` and the dialect's text column (on PostgreSQL the `spark_ignore_case`
    collation, O20). A `List<enum>` converts its elements (`PrimitiveCollectionBuilder.ElementType(e =>
    e.HasConversion<string>())`; in EF 11 an element type is fixed when the property is created, so
    `IMutableProperty.SetElementType` afterwards leaves a property without one, measured as a `NullReferenceException`
    in `MapValue`). An enum inside a JSON column (an embedded object, a dictionary) is written by name too
    (`JsonStringEnumConverter` in `SparkJsonColumns`; a number still reads). A `[Flags]` enum stays an integer column,
    so `(x & Flag) != 0` still translates.
  - **InMemory:** it already stored names, but LINQ to objects sorts an enum by value. `IgnoreCaseQuery` rewrites an
    `OrderBy`/`ThenBy` (and descending) whose key is an enum without `[Flags]` to sort by the name, as a string sorts
    there.
  - **RavenDB:** nothing; it already stored and sorted by name.
  - **SqlDemo:** its migration, regenerated, turns the framework's five enum columns (`SparkMailDeliveries.Kind`,
    `.Status`, `.Reason`, `SparkMessages.Status`, `.DeadLetterReason`) from `integer` into `text`. The view SQL did
    not change: no view compares an enum with a constant. `--spark-migrate` applies it to an empty PostgreSQL 18
    database (the five columns and the `SparkMessages_ByState` view's `Status` read back as `text`), and a second
    synchronize writes nothing.
  - Also green with the change: the full Relational.Tests (249, 2 skipped), the neutral `NeutralMessagingTests`,
    `NeutralMailStoreTests` and `NeutralReduceViewTests` on both SQL databases (11/11 each), and the RavenDB and InMemory
    conformance classes.
  - Evidence: the neutral conformance case `An_enum_sorts_by_its_name` (red first on SQL Server, PostgreSQL and
    InMemory: `Draft` before `Archived`; green on RavenDB) and `RelationalStoreTests.An_enum_is_stored_by_its_name_and_a_flags_enum_as_a_number`
    (reads the raw columns: `"Senior"`, a nullable `"Medior"`, a list of names, a JSON column with `"Medior"`, a
    `[Flags]` value `3`; then filters by `==`, a nullable `==`, a list's `Contains` and a bitwise flag). Green on
    RavenDB, InMemory, SQL Server (LocalDB) and PostgreSQL 18.
  - What it costs: a wider column and a string compare. An enum's members can now be reordered or inserted freely on
    SQL; renaming a member orphans stored rows there, as it already did on RavenDB (a data migration renames them).
  - Not converted: an existing database's integer enum columns. There is no released SQL provider, so there are none.

  The question as it stood (found 2026-11-26): RavenDB stores an enum by name (the client's
  `SaveEnumsAsIntegers` is false and Spark keeps it), InMemory too (`StringEnumConverter`), but EF maps an enum to an
  integer column (SqlDemo's `Status` is `integer`). Equality filters agree (`Queries_filter_on_strings_enums_dates_and_guids`
  is green everywhere); two things differ:
  - **Sorting:** RavenDB orders by name, SQL by value.
  - **Changing the enum:** inserting or reordering members without explicit values silently changes the meaning of
    stored rows on SQL; renaming a member orphans stored values on RavenDB.
  - (a) **Store by name on SQL too** (`HasConversion<string>()` for every enum): RavenDB's meaning, sorting agrees,
    and the column is readable; it costs a wider column and a string compare.
  - (b) **Keep integers** and document the difference.
  - Recommendation: (a), the same reasoning as O20: apps were written against RavenDB. The trap: an enum with
    `[Flags]` stored by name is `"Read, Write"`, which a bitwise filter cannot use; (a) would keep `[Flags]` enums as
    integers.

## 11. Out of scope

- **MariaDB, for now** (O6; investigated, spiked in S13, deferred).
- SQLite and MySQL.
- Migrating data between providers. That is the developer's own one-shot migrator, which maps ids (D23 rule 11).
- Reference integrity (delete guard, rename): a separate issue (D26).
- Composite keys and id obfuscation (D23 rule 10).
- Replication (§5).
- The libraries with no tracked source (§2.5).
