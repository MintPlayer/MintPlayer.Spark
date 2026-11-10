# SQL Server provider — PRD and plan

Issue: [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). Builds on the storage-agnostic core of
[#551](https://github.com/MintPlayer/MintPlayer.Spark/issues/551) (`docs/spark_editor_PRD.md` §3 "Storage-agnostic core",
§8). One pull request.

Status: **draft**, written 2026-11-09 from a read-only investigation while #551 was being implemented. The spikes
run, and this plan is finalised, after #551's pull request, against its final contracts.

## 1. Goal

An application switches from RavenDB to SQL Server by registration alone: `spark.UseSqlServer(...)` in place of
`spark.UseRavenDb(...)`. The model, the actions classes and the policy pipeline do not change (D15 portability goal,
`docs/spark_editor_PRD.md` §4a).

- `MintPlayer.Spark.SqlServer` builds on `MintPlayer.Spark.Abstractions`, like `MintPlayer.Spark.RavenDB`. Owner,
  2026-11-09, recorded as #551 D18.
- Only classes that depend on the database live in it. Everything neutral stays in core (D18's placement rule).
- It runs the provider conformance suite of #551 M7, which covers row security, absent fields (D16),
  DateTimeOffset and paging.

## 2. What the contract asks of SQL Server

The contract is `MintPlayer.Spark.Abstractions/Storage/ISparkSession.cs` and `ISparkQueryProvider.cs`. Line numbers
are at #551 M2.

| Member | Difficulty on EF Core |
|---|---|
| `Query<T>`, `LoadAsync`, `ExistsAsync`, `StoreAsync`, `Delete`, `SaveChangesAsync`, tracking (`IsLoaded`, `HasChanged`, `Evict`, `Clear`), `ToListAsync`/`CountAsync`, `IsStoreQuery`, `Native` (the `DbContext`) | direct |
| `LoadStartingWithAsync` (:39) | `LIKE 'prefix%'` on a string key |
| Versions (`StoreAsync(expectedVersion)`, `GetVersion`, `GetVersionsAsync`, :52/:64/:67) | a `rowversion` serialised as the opaque string; an empty expected version means "must not exist", which is an insert-only |
| `GetCollectionName` / `GetStoredCollectionName` (:70/:73) | a table, or a discriminator |
| `BeforeStore` / `BeforeDelete` (:88/:91) | `SavingChanges` plus the change tracker |
| `WaitForConsistencyAsync` | returns at once: SQL Server is consistent on commit |
| `In()` | `Contains`, which EF translates |
| `Include` | a no-op, or an owned-type include |
| `Search` | null, or `CONTAINS` when full-text is configured |
| `OpenIndex` | no static indexes; the `Indexes` capability stays off (#551 D20) |

## 3. Document semantics that a relational mapping must answer

- **Ids with a collection prefix.** Ids look like `people/1`. `[Reference]` values are bare ids, so the
  foreign key is polymorphic, and `CollectionGuard` reads the stored collection (`Services/CollectionGuard.cs:16-43`,
  `ReferenceTargetValidation.cs:39-44`, `EntityMapper.cs:1229`).
- **Nested values.** Value objects, AsDetail collections and `TranslatedString`
  (`libs/model/.../TranslatedString.cs`) are owned types or JSON columns.
- **Collection predicates.** `AnyContains` over a collection property (`QueryExecutor`) may not translate without
  client evaluation (`docs/spark_editor_PRD.md` §4c "Risk").
- **Absent fields.** D16 makes "absent = CLR default" the defined semantics, which a NOT NULL column with a default
  already satisfies.
- **Patch scripts and `RqlIdentifier`.** These are used only by Raven-only feature libraries, which stay Raven-only.

## 4. Capabilities

- Off at first: `Subscriptions`, `Revisions`, `Indexes`, `ChangeNotifications` across processes (in-process committed
  changes are on), and `FullTextSearch` unless it is configured.
- So these stay Raven-only, and fail at startup and through SPARK060 when combined with SQL Server:
  - messaging and the subscription worker
  - history, contributions and soft delete
  - the identity provider, organizations, replication, moderation, dashboards, cron, migrations, boards, mail, mcp
    and notifications
  - Authorization's user store
- `security.json` enforcement (`IPermissionService`) is neutral and works unchanged.

## 5. Spikes

| # | Question | Done when |
|---|---|---|
| Q1 | Mapping: a table per type with owned types and computed columns, or one JSON column per entity? Decides ids, polymorphism, the stored collection and nested values. | DemoApp's model stored both ways, with the trade-offs recorded here |
| Q2 | Schema evolution: EF migrations, or a schema-less JSON column synchronised from the model, against Spark's Synchronize, which must never remove (D8)? | a decision with the round trip shown |
| Q3 | Query translation: do row-security, column-filter, `AnyContains` and the D16 rewrites translate with no client evaluation? | the conformance suite's query cases on SQL Server (LocalDB on the owner's machine, a service container on CI) |
| Q4 | Concurrency: `rowversion` as the version string, and the D14 etag (#467) on the wire | a conflicting save refused with the existing concurrency error |
| Q5 | Neutral views (owner, 2026-11-10): a view becomes a provider-neutral declaration that each provider renders — RavenDB as an index, SQL Server as a database view, in-memory as a LINQ projection evaluated on read — and `[GenerateIndex]` emits the declaration plus the provider's rendering. Today both halves are RavenDB-only: the generator emits an `AbstractIndexCreationTask`, and `ISparkSession.Query<T, TIndex>()` binds to RavenDB's index base. | DemoApp's `VPerson` (with the computed `FullName` column it lost in #551 M7) served by the in-memory store and by SQL Server from one declaration |

## 6. Milestones

1. Spikes Q1–Q5, with this PRD updated and the owner's sign-off.
2. `MintPlayer.Spark.SqlServer`: `UseSqlServer()`, session, store, query provider and capabilities.
3. The conformance suite on SQL Server, in a CI job that stays within the 20-job limit
   (`.github/workflows/test.yml`).
4. A demo on SQL Server (which app is the owner's call), plus guides and the pull request.

## 7. Out of scope

- Porting the Raven-only feature libraries (§4).
- Providers other than SQL Server (PostgreSQL, SQLite).
