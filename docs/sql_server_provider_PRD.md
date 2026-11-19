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

> **Refined by S1/S2 (2026-11-19, §8):** D1 holds, with a precise division of labour. **Newtonsoft owns the
> document.** It writes and reads the JSON with the InMemory/RavenDB settings, through the provider's own
> parameterised SQL. **EF Core 11 only translates queries.** It sees `Data` as a complex type mapped `ToJson()`,
> derived from the CLR type, and `QueryExecutor`'s `IQueryable<T>` is rewritten onto it. Members EF cannot map are
> read by path through `JSON_VALUE`. The single-table-per-collection layout below is unchanged; `Raw` is added as a
> non-persisted computed alias of `Data`.

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

**S1 and S2 are done (§8.1).** S2b, their remainder, comes next with S3–S4. S1–S4 gate the rest. S5–S8 can run in parallel after them.

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
- **D1 confirmed and refined** (header note in §3). Owner sign-off pending.
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
- ~~**O4, minimum SQL Server:** 2022 or 2025 only?~~ **Answered by S1/S2 (§8.1):** both, at almost no cost. EF
  emits per compat level, and the provider differs only in the `Raw` alias. Confirm whether to also test 2019.
- **O5, full-text:** install the Full-Text feature on the local 2025 instance for S10?

## 11. Out of scope

- Providers other than SQL Server (PostgreSQL, SQLite). The new neutral contracts should make them cheaper later.
- Migrating existing RavenDB data to SQL Server.
- Replication (§5).
- The libraries with no tracked source (§2.5).
