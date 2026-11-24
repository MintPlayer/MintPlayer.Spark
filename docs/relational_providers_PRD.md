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

**D3: the version is a `bigint` column on every table**, a concurrency token the provider increments.
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
change feed's reconnect and messaging's fallback order. **Round 3 makes it per table, and where it lives is open**
(§10 O10): a stamp column on every entity table with a cursor per (subscription, table), or one change-log table
written in the same transaction as the entity.

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
  rejection of a non-lowercase id on store, the prefix escaping, FK equality under the copied collation, and
  non-ASCII keys (`ToLowerInvariant` runs in .NET, so the providers never fold case themselves).
- Existing RavenDB data with mixed-case ids: O15.

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
     lowercase form (D7, O15).
   - Int ids are RavenDB's **native HiLo**, lowered (D7): `"people/1-a"` stays opaque, and the node tag is never
     stripped. On RavenDB, "Integer" just means HiLo-generated. Sorting by id is alphabetical there (`people/10-a`
     sorts before `people/2-a`), and that is documented.
   - The stored JSON is unchanged: references stay strings.
7. **Id lookups made by the framework itself** (detail load, reference resolution, row security, lookups) go through
   a provider hook: `Load` on RavenDB, key equality on EF. The commercial framework does the same: `Session.Load` on RavenDB, a
   key-equality expression on EF.
   - The hook has a batch form (`LoadMany`: one `IN` query on EF), so reference resolution is never N+1. D30 uses the
     same hook to name the referenced row in an FK-violation message.
   - **Only LINQ that an app author writes against `x.Id`** needs translation on RavenDB. Spike S-ID1 tries, in
     order, and the first that passes wins:
     - (a) `Id` also stored as a body field (`"Id": "people/42"`) + `RegisterQueryValueConverter`, with a one-time
       patch per collection `from People update { this.Id = id(this) }`. On load, RavenDB's identity must win over
       the body field.
     - (b) an execution-time expression rewrite `x.Id → x.__DocumentId` through a provider decorator.
     - (c) the native `IRavenQueryable` plus framework `WhereId`/`WhereIds` helpers.
8. **EF Core:**
   - Per-model value converters parse `"people/42"` into `int`/`bigint`/`Guid` columns, range-checked.
   - Int keys use **HiLo** by default; IDENTITY is a documented opt-in that breaks rule 5.
   - Guids are generated client-side: sequential on SQL Server, v7 on PostgreSQL.
   - A ValueComparer is added only where a failing test shows EF needs one (a readonly struct over one string
     compares correctly by default).
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
12. **Hierarchical natural ids** (O14, rounds 6–9). About 75 id builders nest other ids, for example
    `"coverage/{repoId}/commits/{sha}"`.
    - They stay one string primary key (Natural kind). There is no composite key and no hidden surrogate key. Ids are
      immutable, so an embedded parent key can never go stale.
    - **When a segment of a natural id is another entity's id, the entity also declares that reference as an
      ordinary property** (`Id<Repository> RepositoryId`). D26 gives it a real FK and a delete rule, so the relational
      model sees the link and no FK hides inside a string.
    - A generator or analyzer checks that the id template and the property agree: the id is built from the property,
      never from a free string.
    - `LoadStartingWithAsync` stays an escaped `LIKE` prefix on the key (D7).
    - The `LastIndexOf('/')` parsers (`ContributionHandler.cs:707`, `WebhookEndpointManager.cs:263`,
      `MergeQueue.RepositorySuffix`) read that property instead of parsing the id.

The full consensus and the spike details are in plan §3, rounds 4 and 6–9.

**D24 (round 3): model synchronize generates the EF migration files** (option C).
- The app's schema follows its model through ordinary EF migrations that the developer reviews and commits.
- **The framework's own tables** (outbox, leases, atomic values, reservations, revisions, change log) live in their
  own schema and are versioned separately from the app's. The commercial framework keeps its own tables the same way, with
  hand-written versioned scripts.
- The S3 gate decides how the migration is produced from a model built from metadata.

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

**Unknown fields.** A row has only the mapped columns, so undeclared fields are lost, as on InMemory. RavenDB keeps
them (§8.1 finding 6). The conformance suite pins each provider's behaviour. **Absent field versus `NULL`** (D16): a
column always exists, so "absent" can only be `NULL`. The consequences are open (§10 O11).

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
- **O8, PostgreSQL floor (open, probably moot):** 18 was needed only for the virtual `Raw` column, which round 3
  removed. Support 17 as well, or test only 18?
- **O9, bring-your-own `DbContext` (open, low priority):** The commercial framework's apps write their own `DbContext`. Should Spark
  offer a hook to configure what its generated model can't map? Recommendation: not until a real case needs it.
- **O10, change stamp (open, next grilling):** a stamp column per entity table with a cursor per (subscription,
  table), or one change-log table written in the same transaction (D6)?
- **O11, absent field versus `NULL` (open, next grilling):** a column always exists, so what does D16 mean on
  relational providers, and does RavenDB change to match?
- **O12, revisions (open, next grilling):** a framework `Revisions` table (portable, per-collection retention,
  `PurgeOnDelete`), or SQL Server temporal tables (PostgreSQL has none)?
- **O14, hierarchical natural ids (resolved by the LLMs in rounds 6–9, awaiting the owner):** D23 rule 12 and D7.
  A string primary key with prefix scans, plus an explicit `Id<Parent>` property (a real FK) wherever a segment is
  another entity's id. Ids are lower case (D7, round 10), with binary key columns.
- **O13, get-only persisted properties (open, next grilling):** for example `FullName`. Persist it as a computed column,
  recompute on save (as the commercial framework's RavenDB-only calculated attribute does), or keep it out of storage?
- **O15, mixed-case ids in production RavenDB data (open, owner's call):** the owner wants a migration that lowercases
  every document id in the production apps. All five LLMs that answered round 10 chose the alternative.
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

## 11. Out of scope

- **MariaDB, for now** (O6; investigated, spiked in S13, deferred).
- SQLite and MySQL.
- Migrating data between providers. That is the developer's own one-shot migrator, which maps ids (D23 rule 11).
- Reference integrity (delete guard, rename): a separate issue (D26).
- Composite keys and id obfuscation (D23 rule 10).
- Replication (§5).
- The libraries with no tracked source (§2.5).
