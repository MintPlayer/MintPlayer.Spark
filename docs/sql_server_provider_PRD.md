# SQL Server provider — PRD and plan

Issue: [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). Builds on the storage-agnostic core of
[#551](https://github.com/MintPlayer/MintPlayer.Spark/issues/551) (PR #620, `docs/spark_editor_PRD.md` §3
"Storage-agnostic core", §8). One pull request (global rule 8; see open question O1).

Status: **plan, awaiting spikes and owner sign-off.** Rewritten 2026-11-19 from a read-only investigation of master
`5433a99dc`: the provider contract, every feature library, and the SQL Server versions on the owner's machine. It
supersedes the 2026-11-09 draft (last changed in `ead29b682`). §9 lists what in that draft was stale.

## 1. Goal

A web-app developer chooses the database at registration: `spark.UseSqlServer(...)` instead of `spark.UseRavenDb(...)`.
The model, the actions classes and the policy pipeline stay the same (D15 portability goal, `docs/spark_editor_PRD.md` §4a).

- **As many Spark features as possible run on SQL Server** (owner, 2026-11-19). The 2026-11-09 draft left
  every feature library on RavenDB only. This plan replaces that with tiers (§5): ports, ports behind new neutral
  contracts, and a short list of libraries that stay on RavenDB only, each with a stated reason.
- `MintPlayer.Spark.SqlServer` builds on `MintPlayer.Spark.Abstractions`, like `MintPlayer.Spark.RavenDB` (#551 D18).
  Only classes that depend on the database live in it.
- It passes the provider conformance suite. This PR also extends the suite (§2.4).

## 2. Findings

### 2.1 The provider contract today

All of it lives in `libs/spark/MintPlayer.Spark.Abstractions/Storage/`.

| Contract | Members | What it means for SQL Server |
|---|---|---|
| `ISparkSession` (`ISparkSession.cs:22-127`) | `Query<T>`, `Query<T,TIndex>` (:25/:32); loads, batch loads with `includePaths`, untyped `LoadAsync`, `PreloadAsync` (:34-57); `LoadStartingWithAsync` (:60); `ExistsAsync`; `StoreAsync` with `expectedVersion` (:65-73); `Delete(id, expectedVersion)` (:76); `GetVersion`/`GetVersionsAsync` (:85/:88); `GetCollectionName`/`GetStoredCollectionName` (:91/:94); tracking (`IsLoaded`, `HasChanged`, `TrackedEntities` :101, `Evict`, `Clear`); `BeforeStore`/`BeforeDelete` (:109/:112); request budget (:115-118); `Native` | a unit of work plus an identity map. Versions map to `rowversion`, and an expected version of `""` means insert-only. `LoadStartingWith` is a `LIKE 'prefix%'` on the key. |
| `ISparkStore` (:133-155) | `ProviderName`, `Capabilities`, `OpenSession`, `WaitForConsistencyAsync` | `WaitForConsistencyAsync` returns at once, because SQL Server is consistent on commit |
| `SparkStoreCapabilities` (:166-176) | Subscriptions, Revisions, FullTextSearch, Attachments, Expiration, ChangeNotifications, Indexes | per capability, see §4 |
| `ISparkQueryProvider` | `IsStoreQuery`, `OpenIndex`, `Project`, `Include`, `In`, `IndexedFields`, `Search`, `CountAsync`, `ToListAsync` | `IsStoreQuery => true` only if the LINQ really becomes SQL (spike S2) |
| `ISparkChangeFeed` | `OnCommitted`, `WatchDocument`, `WatchCollection`, `Reconnected` | in process at first, as InMemory does it; across processes through the outbox (S7) |
| Optional: `ISparkIndexDeployment`, `ISparkStoreExpiration`, `ISparkStoreRowKeyScan`, `ISparkDataProtectionKeyStore` | only RavenDB registers them today | all four can be done on SQL Server: views/computed columns, a sweeper, a JSON scan, and a keys table |
| `IIndexCatalog` (`Abstractions/Indexes/IIndexCatalog.cs:17`) | outside `Storage/`; core falls back to `EmptyIndexCatalog` | S5 |

The in-memory provider (`libs/spark/MintPlayer.Spark.InMemory/Storage/InMemory/`) is the reference implementation. It
stores Newtonsoft JSON snapshots and deserialises a fresh copy on every load (`InMemoryJson.cs:13`). It detects dirty
entities by diffing snapshots (`InMemorySparkSession.cs:245`). It commits versions atomically under one lock
(`InMemoryDatabase.cs:47-53`). Its ids are `{Collection}/{Guid}` or the `IHasNaturalId` id (`InMemoryConventions.cs:7-25`).
Its queries are LINQ-to-objects (`IsStoreQuery => false`). **The SQL provider reuses its JSON shape and its id
conventions**, so all three providers store the same document.

### 2.2 Registration and refusal

- `UseRavenDb` is in `SparkRavenDbExtensions.cs:22` and `UseInMemory` in `SparkInMemoryExtensions.cs`. Each carries
  `[SparkStoreProvider(name, capabilities)]`.
- A feature library declares what it needs with `[SparkRequiresStore]` and `RequireStore()` (`SparkStoreRequirement.cs`).
- `UseSpark` refuses unmet requirements at startup (`SparkMiddleware.cs:275-283`). Analyzer SPARK060 refuses them at
  compile time (`StoreRequirementAnalyzer.cs:29`).
- `SparkStoreProviders` has no `SqlServer` constant yet (`:28-32`).

### 2.3 Coupling left in core

`MintPlayer.Spark.csproj` references no RavenDB package. Of the 48 core files that mention "Raven", almost all are
comments. The ones that need a change:

- `SparkDataProtectionStorage.RavenDb` (`SparkDataProtectionOptions.cs:53`, `SparkDataProtection.cs:101`) is already
  gated on `ISparkDataProtectionKeyStore`. Rename it to a neutral `Store`.
- RavenDB wording in messages: `LiveHub.cs:190`, `SparkIndexBindingStartupCheck.cs:32`.
- `ModelSynchronizer.cs:29` needs only `IIndexCatalog` and never touches the store.
- `[GenerateIndex]` emits only `SparkIndexCreationTask`/`AbstractIndexCreationTask`, and only when RavenDB is referenced
  (`GenerateIndexGenerator.cs:16/:36/:155`).
- `QueryExecutor` asks the store for:
  - `IsStoreQuery` and `In` (:664)
  - `OpenIndex`/`Project` (:959-967) and `Include` (:1049, :1365)
  - the server-side translation of the row filter (:1055) and of `AnyContains` (:2368)
  - `IndexedFields` (:2062) and `Search` (:2155)
  - `Skip`/`Take` paging (:438)

  The row-security gate runs on materialised rows (:1162), so it works on any provider.

### 2.4 Conformance suite

`tests/MintPlayer.Spark.Tests/Storage/SparkStoreConformanceTests.cs:15` is abstract. A provider overrides
`CreateStoreAsync()` (:56) and optionally `Settled<T>` (:58). RavenDB (:347) and InMemory (:399) subclass it, and SQL
Server joins as a third subclass.

It has 13 facts: round-trip, absent field, collections, identity map, tracking, `BeforeStore`, delete, absent and stale
versions, `LoadStartingWith`, query filters and the request budget. **The 2026-11-09 draft said it also covered row
security, DateTimeOffset and paging. It does not (0 hits).** This PR adds those cases (M3), and they run on all three
providers.

### 2.5 Feature libraries

**No feature library uses `ISparkSession`.** Every one that stores data calls `IAsyncDocumentSession`/`IDocumentStore`
directly. Only logging is neutral, through `ISparkStore` (`libs/logging/.../Actions/LogActions.cs`). Even the simple
libraries therefore need a port, but `ISparkSession` already has most of what they call: `Load`, `Query`,
`LoadStartingWith`, `Exists`, `Store(expectedVersion)`, `GetVersion`, `Evict` and `IsLoaded`.

They also use RavenDB features that `ISparkSession` does not cover yet:

| RavenDB API | Used by | Neutral replacement (new contract) |
|---|---|---|
| `@expires` metadata | mail, mcp, moderation, messaging, identity_provider | `ISparkSession.SetExpiry(entity, at)`, backed by `ISparkStoreExpiration` |
| Compare-exchange leases and locks | cron, migrations, dashboards, messaging, identity_provider | `ISparkStoreLock` (RavenDB: compare-exchange; SQL Server: `sp_getapplock` or a lease table; InMemory: a semaphore) |
| Compare-exchange uniqueness (cluster transaction) | authorization (usernames), identity_provider | a unique constraint, declared neutrally (`[SparkUnique]`, or a reservation document stored with `expectedVersion: ""`). Spike S6 decides which. |
| `StreamAsync`, `Lazily` | contributions, moderation, authorization | `ISparkSession.StreamAsync<T>(IQueryable<T>)`; `Lazily` becomes plain batched calls |
| `Advanced.Patch`, `DeleteByQuery`, `PatchByQuery` | apps, organizations, authorization, identity_provider, messaging | load + modify + save, or a `DeleteWhereAsync(Expression)` |
| `WaitForNonStaleResults` | notifications, organizations | `WaitForConsistencyAsync` (already in the contract) |
| Static indexes | moderation (5), messaging (3), identity_provider (2) | neutral views (S5) |
| Subscriptions, `@refresh`, counters | subscription_worker, messaging, replication | the Subscriptions capability through an outbox (S7) |
| Revisions | history, soft_delete, authorization (backfill), dashboards | the Revisions capability (S8) |
| Time series | dashboards | a samples table (S9) |
| `Search()` | search | the FullTextSearch capability (S10) |

`approvals`, `challenges`, `commerce`, `fulfilment`, `printing`, `refdata`, `sms`, `waiting_room` and `warehouse`
have no tracked source on master (`git ls-files` returns 0; commerce is paused on draft PR #529), so they are out of
this plan. `gs1`, `sitemap`, `webpush`, `webhooks`, `model` and `attributes` don't touch the store.

### 2.6 SQL Server on the owner's machine and on CI

Read-only checks, 2026-11-19:

| Instance | Version | Notes |
|---|---|---|
| `localhost` (default, `MSSQLSERVER`) | SQL Server 2022 Developer, 16.0.1210.3, compat 160 | no native `json` type; full-text **not installed** |
| `localhost\MSSQLSERVER01` | SQL Server 2025 Developer, 17.0.1145.4, compat 170 | the `json` type and `JSON_CONTAINS` work (verified); full-text **not installed** |
| `(localdb)\MSSQLLocalDB` | LocalDB 2019 (v150) | full-text unsupported |

The SQL Browser service is stopped, and `sqlcmd` needs `-C` (trust the certificate). Docker 29.9.2 is available.

- **EF Core 11.0.0 is GA** (`Microsoft.EntityFrameworkCore.SqlServer`).
  - Complex types mapped to JSON (`ComplexProperty().ToJson()`) arrived in EF Core 10.
  - EF Core 11 uses the native `json` column at compat level 170 or higher, and translates `Contains` over JSON
    collections to `JSON_CONTAINS`.
  - The sources for these points are RC-era blog posts, so S1 checks them against the GA package.
- The repository has no `Directory.Packages.props` and no EF Core reference. The only SQL pin is
  `Microsoft.Data.SqlClient` 7.1.2 (`apps/MintPlayer/MintPlayer.Migration/MintPlayer.Migration.csproj:19`), held there
  for GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7. The provider uses the same floor.
- CI can run `mcr.microsoft.com/mssql/server` as a service container on ubuntu. The tag must be pinned: 2025 for the
  `json` type. The Linux image has no full-text search unless `mssql-server-fts` is installed.
- `Testcontainers.MsSql` 4.17.0 exists. A plain connection string is enough, though, because both CI and the owner's
  machine already have a server running.
- SQL Server Express limits: 10 GB per database, no SQL Agent (so no CDC jobs), and full-text only in the "Advanced
  Services" edition. **The provider must not rely on CDC or Agent jobs** if Express is to work.

## 3. Design direction (to be confirmed by spikes)

**Proposal D1: hybrid document tables.** The provider creates one table per collection:

```sql
Id         nvarchar(450) NOT NULL PRIMARY KEY  -- "people/1", polymorphic string id as today
Data       json (2025) | nvarchar(max) CHECK (ISJSON(Data)=1) (2019/2022)
Collection nvarchar(128) NOT NULL              -- the stored collection CollectionGuard reads
Version    rowversion                          -- ISparkSession version string
ExpiresAt  datetime2 NULL                      -- Expiration capability
-- promoted, persisted computed columns + indexes for fields that queries filter/sort on
```

- **Why not a fully relational EF model?** That model needs a migration for every model change. It maps
  `TranslatedString` dictionaries, AsDetail collections and string ids like `people/1` badly. It would also store a
  different document shape from the other two providers.
- **Why the document table?**
  - Absent fields stay absent (D16).
  - Schema evolution is additive with no migrations, which agrees with Synchronize's "never remove" (D8).
  - The JSON is byte-compatible with InMemory's snapshots, so the dirty-check and serialisation code can move to a
    shared helper.
- **The cost** is query translation. `QueryExecutor` hands the provider an `IQueryable<T>`, and the provider must turn
  it into `JSON_VALUE`/`OPENJSON` SQL, not client evaluation. That is spike S2, and the risk this plan is built
  around.

Promoted columns follow `IndexedFields` and the neutral views (S5). They match "indexes only in the application"
(feedback memory): the app declares what is queryable, and the provider renders that as computed columns.

## 4. Capabilities on SQL Server

| Capability | SQL Server mechanism | In this PR? |
|---|---|---|
| ChangeNotifications | in process at commit; across processes, a poll over the outbox's `rowversion` cursor | yes |
| Expiration | `ExpiresAt` column plus a sweeper (`ISparkStoreExpiration.EnableSweep`) | yes |
| Indexes | neutral views rendered as SQL views or computed columns (S5) | yes, if S5 holds |
| Subscriptions | an outbox table written in the same transaction, a `rowversion` cursor per subscription, `READPAST`/`UPDLOCK` dequeue | yes, if S7 holds |
| Revisions | a revisions table written by the session at commit, or system-versioned temporal tables (S8) | yes, if S8 holds |
| FullTextSearch | `CONTAINS`/`FREETEXT` when the Full-Text feature is present (probed at startup); otherwise the capability stays off | S10 |
| Attachments | `varbinary(max)` in a side table, or off | S11 (low priority) |

## 5. Feature tiers

Libraries are grouped by effort, from the inventory in §2.5. Each port moves the library from
`IAsyncDocumentSession` to `ISparkSession`, and replaces its `RequireStore(provider: RavenDb)` with a capability
requirement.

| Tier | Libraries | Needs |
|---|---|---|
| 0: works already | core (CRUD, queries, row security, `security.json`, translations, live updates in process), logging | — |
| 1: port to `ISparkSession` | settings, boards, notifications, apps, organizations, templates/CLI scaffolding | `WaitForConsistencyAsync`; replace Patch/DeleteByQuery |
| 2: port plus one new neutral contract | mail, mcp (expiry); cron, migrations (lock); contributions (streaming); authorization user store (uniqueness); search (FullTextSearch) | the contracts in §2.5 |
| 3: port plus capability | history, soft_delete (Revisions); subscription_worker, messaging (Subscriptions); moderation (views + expiry + streaming) | S5, S7, S8 |
| 4: large | identity_provider (70 RavenDB files, 2 indexes, PatchByQuery, RawQuery, compare-exchange); dashboards (time series, RQL) | owner's call, O2 |
| RavenDB only | replication (RavenDB ETL, RavenDB to RavenDB by design) | stays, refused by SPARK060 |

Every port also keeps working on RavenDB, and on InMemory where the capability exists. The neutral contracts get
three implementations each.

## 6. Spikes

Each spike ends with its result recorded in a new §8 entry, with evidence (numbers, file:line, a red→green test).

| # | Question | Done when |
|---|---|---|
| S1 | **Mapping:** does the hybrid document table (D1) hold? Compare it with EF Core 11 `ComplexProperty().ToJson()` on a relational entity. Verify the EF Core 11 GA JSON claims (§2.6). | DemoApp's and HR's models stored both ways, on 2022 (`nvarchar`) and 2025 (`json`), with the trade-offs recorded |
| S2 | **Query translation:** do the filters, sort, `In`, `AnyContains`, the row filter (:1055), the D16 absent-field rewrites and DateTimeOffset translate with no client evaluation? Candidates: EF Core over a JSON-mapped entity, or a dedicated `ExpressionVisitor` → SQL over `JSON_VALUE`/`OPENJSON`. | `QueryExecutor`'s query cases produce SQL (logged), and a non-translatable expression throws rather than evaluating in memory |
| S3 | **Schema:** how the provider creates and widens tables and computed columns from the model, against Synchronize's never-remove (D8). Can EF migrations be avoided? | the round trip shown: add a field, add a queryable field, remove a field (column kept) |
| S4 | **Concurrency:** `rowversion` as the version string; `""` = insert-only; the D14 etag (#467) on the wire | the conformance tests for absent and stale versions pass; a conflicting save gives `SparkStoreConcurrencyException` |
| S5 | **Neutral views** (carried over from the 2026-11-10 draft, owner): a view is one provider-neutral declaration, rendered by RavenDB as an index, by SQL Server as a view or computed columns, and by InMemory as a LINQ projection. `[GenerateIndex]` emits the declaration plus the provider's rendering. | DemoApp's `VPerson` (with its computed `FullName`) served by InMemory and SQL Server from one declaration |
| S6 | **Neutral contracts for Tier 2:** `SetExpiry`, `ISparkStoreLock`, `StreamAsync`, uniqueness, `DeleteWhereAsync`. Placement and shape follow the design-principles skill (deep modules, one contract per concern). | settings (Tier 1), mail (expiry) and cron (lock) ported and green on all three providers |
| S7 | **Outbox / Subscriptions** without CDC or Agent (Express-safe): an outbox table in the same transaction; a `rowversion` cursor per subscription; `READPAST`; the `@refresh` wake-up as a due-time column | subscription_worker's tests on SQL Server; a message survives a host restart |
| S8 | **Revisions:** temporal tables (they need a fixed schema per table, which is fine with D1) or a revisions table written at commit | history's tests on SQL Server; soft-delete restores |
| S9 | **Dashboards:** time series as a samples table, and RQL rewritten as LINQ | decide whether dashboards are in or out (O2) |
| S10 | **Full-text:** install the Full-Text feature on the 2025 instance (owner's action) and on a CI image (`mssql-server-fts`), or ship the capability off with a `LIKE` fallback | search's tests on SQL Server, or a recorded "off" decision |
| S11 | **Attachments** (low priority) | in or out |

S1–S4 come first and gate the rest. S5–S8 can run in parallel after them.

## 7. Milestones

1. **Spikes S1–S4**, this PRD updated, owner sign-off on D1.
2. **Neutral contract additions** (S6), implemented on RavenDB and InMemory first, with no behaviour change on RavenDB.
   Also the clean-ups from §2.3: the `SparkDataProtectionStorage` rename and the messages.
3. **`MintPlayer.Spark.SqlServer`:**
   - `UseSqlServer()` and the `SparkStoreProviders.SqlServer` constant
   - the store, session, query provider, change feed, expiration, the data-protection key store and the lock
4. **Conformance:** the SQL Server subclass, plus new cases for row security, DateTimeOffset, paging, expiry, lock and
   streaming, on all three providers.
5. **Tier 1 and Tier 2 ports.**
6. **S5 views, then Tier 3** (S7, S8), then search (S10).
7. **Tier 4** as decided in O2.
8. **Demo, CI, docs:**
   - a demo app on SQL Server (O3)
   - a `sqlserver` CI job with a pinned `mcr.microsoft.com/mssql/server:2025-*` service container that joins an
     existing job group (20-job limit, `.github/workflows/test.yml`)
   - guides
   - the pull request

   The new package targets `net11.0`, so its first version is 11.x. The engine assigns it; never write a version by
   hand.

Tests: single classes during development (TDD red→green). The full suite runs once at the end, via push → PR → CI
(feedback memory: no local sweeps).

## 8. Spike results and decision log

*(empty: each spike adds an entry with its evidence)*

## 9. Stale in the 2026-11-09 draft

- The line numbers in its §2 contract table predate #551's final contract (now `:60`, `:73/:85/:88`, `:91/:94`,
  `:109/:112`). The table also lacked `PreloadAsync`, `includePaths`, `TrackedEntities`, the request budget,
  `WaitForConsistencyAsync`, `IIndexCatalog`, and the query provider's `Project` and `IndexedFields`.
- It said the conformance suite covered row security, DateTimeOffset and paging. It doesn't (§2.4).
- Its RavenDB-only list (§4) omitted apps, search and settings, which also pin RavenDB through `RequireStore`. It also
  kept every feature library on RavenDB, which the owner's goal of 2026-11-19 reverses (§5).
- It proposed LocalDB for local runs. LocalDB here is 2019, with no `json` type and no full-text. Use
  `localhost\MSSQLSERVER01` (2025) instead.

## 10. Open questions for the owner

- **O1, one PR:** Tiers 1–3 make this pull request large. Keep it as one (rule 8), or allow the core provider and the
  library ports to go separately? This plan assumes one.
- **O2, Tier 4:** port identity_provider and dashboards in this PR, or keep them RavenDB-only for now (refused by
  SPARK060)?
- **O3, demo app:** which app runs on SQL Server? DemoApp is core-only and "new features don't land there". HR or QnA
  with a switch, or a new `apps/SqlDemo`?
- **O4, minimum SQL Server:** 2022 (an `nvarchar` JSON column) or 2025 only (native `json`, `JSON_CONTAINS`)?
  Supporting 2022 doubles the S1/S2 matrix, but SQL Server hosting is mostly 2019/2022 today.
- **O5, full-text:** install the Full-Text feature on the local 2025 instance for S10?

## 11. Out of scope

- Providers other than SQL Server (PostgreSQL, SQLite). The new neutral contracts should make them cheaper later.
- Migrating existing RavenDB data to SQL Server.
- Replication (§5).
- The libraries with no tracked source (§2.5).
