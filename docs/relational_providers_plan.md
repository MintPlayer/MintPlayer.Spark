# Relational storage providers: plan

Issue [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). The what and why are in
[`relational_providers_PRD.md`](relational_providers_PRD.md). This file holds the how and when: spikes, grilling
rounds, milestones and status. Branch `feat/536-sql-server-provider`, draft PR #697, **one pull request** (O1).

## 1. Status (2026-11-24)

- **Docs and spikes only, no product code yet.**
  - Spikes S1, S2, S12 and S13 are done. Since round 3 they are history for the storage model (PRD §8).
  - Grilling rounds 1–4, the commercial-framework comparison and the LLM rounds 6–9 on it are done (§3).
- **Scope:** SQL Server (2022, 2025) and PostgreSQL (18; 17 is open, O8). MariaDB is deferred (O6). Tier 4
  (identity_provider, dashboards) is in. The demo is a new `apps/SqlDemo`.
- **Storage model (round 3):** a table per entity, mapped by EF Core. The model synchronize generates EF migrations;
  AsDetail collections are child tables; references are real FKs; the contract is typed; ids are `Id<T>` (D23).
  Breaking changes are allowed, with no backward compatibility.
- **Next:** the gate spikes S5, S4 and S2b (§2). S3, S19, S20, S-ID1, S-ID2, S-ID4 and S-ID5's RavenDB half are
  done (PRD §8.3–8.8, 2026-11-24). The two upstream bugs S3 and S19 found have pull requests upstream and
  workarounds in D24 and D33 (§5). Grilling round 5 decided O10–O13
  (2026-11-24). O16 and O17 are decided (2026-11-24; O17's evidence is PRD §8.9). O8 and O9 are open, both low
  priority.
- Local spike servers left running:
  - SQL Server: `SparkSpike536` on `localhost` and `localhost\MSSQLSERVER01`
  - Docker containers `spark536-pg` (`postgres:18`, 127.0.0.1:55432) and `spark536-maria` (`mariadb:11.8`,
    127.0.0.1:53306)

  Remove the containers with `docker rm -f spark536-pg spark536-maria`. The MariaDB one is no longer needed.

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
| **S2b** | **The real `QueryExecutor` end to end** on an EF model generated from arbitrary Spark entities: TPH, JSON columns for `TranslatedString` (EF 10's `ToJson()` complex types; a `Dictionary` as a JSON column is unverified), child tables, join tables | the row filter (:1055), `In` (:664) and search pushdown (:2155) run as SQL on both databases | gate |
| **S3** | **Schema:** the model synchronize generates EF migration files (D24) from a model built from metadata, through the public design-time services (`IMigrationsScaffolder`, `IMigrationsModelDiffer`, via `DesignTimeServicesBuilder`; never construct `MigrationsScaffolderDependencies`) | add a field, add a queryable field, add an AsDetail collection, add a reference, on both | ✅ PRD §8.8: all six versions (plus a rename and a no-change run) scaffold the expected operations and migrate on both, from public API only. the EF 11 snapshot trap is worked around by `FreshSnapshotMigrationsAssembly` (dotnet/efcore#39784 upstream); queryable strings need a length; Design's 17 MB → O17, decided (§8.9: a development-dependency package, out of every publish) |
| **S4** | **Concurrency and keys:** the `bigint` version per table (D3); `""` = insert-only; the D14 etag on the wire; the typed contract (D27); lowercase ids and an escaped `LoadStartingWith` (D7); constraint errors become validation messages (D30) | the conformance tests for absent and stale versions pass, plus a natural-key case with `_` and `%`, plus a unique and an FK violation | gate |
| **S5** | Neutral views: one declaration, rendered by RavenDB as an index, by relational databases as an SQL view or indexes, and by InMemory as LINQ | DemoApp's `VPerson` (computed `FullName`) and one view with a join, from one declaration on InMemory + both databases; synchronize writes the EF-translated `CREATE VIEW` into the migration (PRD D32) | gate |
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

- O8 (the PostgreSQL 17 floor, probably moot)
- O9 (bring-your-own `DbContext`, low priority)

## 4. Milestones

1. **Gate spikes S-ID1, S-ID2, S-ID4, S-ID5, S2b, S3, S4, S5, S19, S20.** The PRD updated with their evidence.
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
3. **Neutral contract additions** (S6, S15), implemented on RavenDB and InMemory first, with no behaviour change on
   RavenDB. Also the core clean-ups from PRD §2.3:
   - the `SparkDataProtectionStorage` rename
   - the `SparkStoreRequirement` default hint
   - the RavenDB wording in messages
   - the generators' neutral declarations (with S5): one `MintPlayer.Spark.SourceGenerators` package that renders the
     RavenDB index or the EF view mapping depending on the referenced provider (PRD D32)
   - IoC (PRD D34): `ISparkCommand` as keyed DI registrations (no registry class), with the five existing commands
     moved onto it and every app's `Program.cs` (and the template's) reduced to one command line after `Build()`; `ISparkSchemaMigrator` with the RavenDB
     implementation; `--spark-migrate` and `--spark-migration-script` in core; the data-migration runner on the
     neutral lease; a layering test against provider references in core
4. **`MintPlayer.Spark.Relational`, `MintPlayer.Spark.SqlServer`, `MintPlayer.Spark.PostgreSql`** (D2):
   - `UseSqlServer()`, `UsePostgreSql()` and their `SparkStoreProviders` constants
   - the EF model built from the Spark model, the migration generator (D24), child and join tables (D25, D26)
   - applying migrations (D33): at startup by default (framework schema, app migrations, then the data migrations),
     `--spark-migrate`, `--spark-migration-script`, the `ApplyMigrations=false` startup check, and the per-app
     pending-model-changes test
   - the store, session, query provider, change feed, expiration, the data-protection key store, atomic values,
     leases and reservations
   - constraint-error translation (D30)
5. **Conformance:** the two relational subclasses, plus new cases on all four providers:
   - row security, `DateTimeOffset`, paging and null ordering
   - `Id<T>` round trips, lowercase ids (an upper-case `GetById` is found everywhere) and escaped prefixes
   - expiry, atomic values, leases, reservations and streaming
   - **a wrong-rows guard for every query shape**
   - the absent-field rewrite for nullable members, and absent-field sorting and distinct values (O11)
   - **the database behaviour tests (PRD D31):** `SqlServerBehaviourTests` and `PostgreSqlBehaviourTests`, one test per
     assumption, named after the decision it protects. Each one is written when its milestone first relies on it,
     not all at once.

   This comes with the split test harness (S17, PRD D35): `MintPlayer.Spark.Testing` becomes neutral, today's
   RavenDB code moves to `MintPlayer.Spark.Testing.RavenDB`, and `.SqlServer`/`.PostgreSql` join.
6. **Tier 1 and Tier 2 ports** (PRD §5), plus the lookup lost-update fix.
7. **Tier 3:** S7/S14 subscriptions and messaging, S8 revisions, S16 moderation. Then search with `LIKE` (S10).
8. **Tier 4:** identity_provider, and dashboards (S9).
9. **Demo, CI, docs:**
   - **`apps/SqlDemo`**, using the ported libraries, with E2E coverage
   - CI: pinned `mcr.microsoft.com/mssql/server:2025-*` and `postgres:18` service containers. They join existing job
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
