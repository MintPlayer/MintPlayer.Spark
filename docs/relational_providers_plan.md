# Relational storage providers: plan

Issue [#536](https://github.com/MintPlayer/MintPlayer.Spark/issues/536). The what and why are in
[`relational_providers_PRD.md`](relational_providers_PRD.md). This file holds the how and when: spikes, grilling
rounds, milestones and status. Branch `feat/536-sql-server-provider`, draft PR #697, **one pull request** (O1).

## 1. Status (2026-11-24)

- **Docs and spikes only, no product code yet.**
  - Spikes S1, S2, S12 and S13 are done.
  - The PRD was rewritten, and both grilling rounds are done (§3).
- **Scope:** SQL Server (2022, 2025) and PostgreSQL (18; 17 is open, O8). MariaDB is deferred (O6). Tier 4
  (identity_provider, dashboards) is in. The demo is a new `apps/SqlDemo`.
- The branch was rebased onto master `0b9915c50` on 2026-11-24. It conflicted on nothing: it held docs only.
- **Next:** owner sign-off on D1–D7 (PRD §3), then the gate spikes S2b, S3, S4 and S5 (§2).
- Local spike servers left running:
  - SQL Server: `SparkSpike536` on `localhost` and `localhost\MSSQLSERVER01`
  - Docker containers `spark536-pg` (`postgres:18`, 127.0.0.1:55432) and `spark536-maria` (`mariadb:11.8`,
    127.0.0.1:53306)

  Remove the containers with `docker rm -f spark536-pg spark536-maria`. The MariaDB one is no longer needed.

## 2. Spikes

Each spike ends with an entry in PRD §8, with evidence (numbers, file:line, a red→green test).

| # | Question | Done when | Status |
|---|---|---|---|
| S1 | Mapping: does the hybrid document table hold? (SQL Server) | DemoApp's and HR's models stored on 2022 and 2025 | ✅ PRD §8.1 |
| S2 | Query translation on SQL Server: the filters, sort, `In`, `AnyContains`, D16, `DateTimeOffset`, with no client evaluation | the shapes produce SQL | ✅ PRD §8.1 |
| S12 | S1+S2 on **PostgreSQL 18** (Npgsql 11) | the same 18 shapes | ✅ PRD §8.2: 18/18 |
| S13 | S1+S2 on **MariaDB 11.8** (Microting 11) | the same 18 shapes | ✅ PRD §8.2: 12/18, 5 silently wrong → **MariaDB deferred** |
| ~~S13b~~ | MariaDB translation fixes | — | dropped with MariaDB (O6) |
| **S2b** | The real `QueryExecutor` end to end on a prototype session. Also: <ul><li>a query model generated from arbitrary Spark entities</li><li>typed path reads</li><li>`x.Id` mapped onto the key column</li><li>promoted columns measured on a few thousand rows</li><li>PostgreSQL `"Data"::text` against a virtual `Raw` (O8)</li></ul> | the row filter (:1055), `In` (:664) and search pushdown (:2155) run as SQL on both databases | gate |
| **S3** | Schema: create and widen tables and generated columns from the model, with Synchronize's never-remove (D8), and no EF migrations | add a field, add a queryable field, remove a field (column kept), on both | gate |
| **S4** | Concurrency and keys: the `bigint` version (D3); `""` = insert-only; the D14 etag on the wire; case-insensitive `IdKey` and an escaped `LoadStartingWith` (D7) | the conformance tests for absent and stale versions pass, plus a case-insensitivity case with `_` and `%` in ids | gate |
| **S5** | Neutral views: one declaration, rendered by RavenDB as an index, by relational databases as generated columns/views, and by InMemory as LINQ. *Moved into the gates by round 1:* Tier 0 apps and the demo use views. | DemoApp's `VPerson` (computed `FullName`) from one declaration on InMemory + both databases | gate |
| S6 | Neutral contracts: <ul><li>`SetExpiry`</li><li>`ISparkStoreLock`</li><li>`ISparkAtomicValues`</li><li>`StreamAsync`</li><li>`Reserve/Release` (in the `SaveChanges` transaction)</li><li>`UpdateWhereAsync`/`DeleteWhereAsync`</li><li>`PatchAppend`</li></ul>(design-principles: deep modules, one contract per concern) | settings, mail (expiry) and cron (atomic value) green on all providers | after the gates |
| S7 + S14 | **Subscriptions:** <ul><li>a per-subscription cursor over `Documents.ChangeStamp`, below the safe horizon (D6)</li><li>tombstones for deletes</li><li>a neutral predicate instead of RQL</li><li>a lease replacing `WaitForFree`</li></ul> | <ul><li>two interleaved transactions never skip a row (the D6 horizon)</li><li>a new subscription sees the backlog</li><li>a message survives a host restart</li><li>two hosts never process one message</li><li>subscription_worker's tests on both</li></ul> | after S6 |
| S8 | Revisions: a `Revisions` table written by **every** write path, including `UpdateWhere`/`PatchAppend` and sweepers; revision id = `Version`; per-collection retention and `PurgeOnDelete` | history's tests on both; a soft-delete restore; a set-based update leaves a revision | after S6 |
| S9 | Dashboards: a samples table, a rollup job and retention; RQL aggregation as LINQ (in scope, O2) | dashboards' tests on both | after S6 |
| S10 | Full-text: SQL Server FTS (probe), PostgreSQL `tsvector`; `LIKE` fallback | search's tests on both, or a recorded "off" for SQL Server without FTS | later (O5) |
| S11 | Attachments (low priority) | in or out | later |
| S15 | `ISparkAtomicValues` and leases: a table with compare-and-swap, against advisory locks under connection pooling and loss | <ul><li>cron: one occurrence fires once across two hosts, and poisoned values are reclaimed</li><li>migrations: one runner, and takeover after a killed host</li></ul> | with S6 |
| S16 | Aggregate tables for moderation's 3 map-reduce indexes: updated in the same transaction from the loaded old row; set-based writes refused on aggregated collections | moderation's reputation tests on both | with Tier 3 |
| S17 | A provider-neutral test driver (`libs/testing`): a database per test class on a service container or a local instance; RavenDB keeps `RavenTestDriver` | one Spark.Tests class runs on all four providers | with M4 |
| S18 | Null ordering (D4), with RavenDB's own order measured | a conformance case green on all four providers | with M4 |

**Gates:** S2b, S3, S4 and S5 gate the provider work (M3). S6–S9 and S14–S16 can run in parallel after them.

## 3. Grilling rounds

### Round 1: an adversarial review of the design (2026-11-24)

A reviewer agent read the PRD and the code cold and tried to break D1–D6 and the equivalents table. **The
architecture (D1, D1a, D2, D3) held**, as did D4 and D5, the S13 findings, the `SKIP LOCKED`/`READPAST` claims and
PostgreSQL 18 virtual columns. The findings, each checked against the code before being accepted:

| # | Sev | Finding | Change |
|---|---|---|---|
| 1 | blocker | A cursor on a database sequence skips rows: a value is allocated before commit, and commits land out of order. | D6 rewritten: a commit-ordered `ChangeStamp` with a safe read horizon (`MIN_ACTIVE_ROWVERSION()`, `pg_snapshot_xmin`). S14 tests interleaved transactions. |
| 2 | blocker | Cron is not a lease. `TryClaimOccurrenceAsync` is a never-expiring compare-and-swap of "latest claimed occurrence" with poison handling (`SparkCronScheduler.cs:236-274`, **verified**). A lease would let a second host fire the same occurrence. | A neutral `ISparkAtomicValues` (the compare-exchange equivalent) with leases built on it; cron uses it directly. S15 extended. |
| 3 | important | Ids are case-insensitive on InMemory and RavenDB (`InMemoryDatabase.cs:12/:43-44`, **verified**); PostgreSQL `text` isn't. `LIKE 'prefix%'` mis-handles `_` and `%`. | D7: a lower-cased `IdKey` primary key, `LIKE … ESCAPE`. S4 extended. |
| 4 | important | Revisions written "by the session at commit" miss set-based writes and sweepers. | Every write path writes revisions; revision id = `Version`. S8 extended. |
| 5 | important | Messaging's `Advanced.Patch` list appends are concurrency-safe; load-modify-save would lose updates or 409. | A neutral `PatchAppend`. |
| 6 | important | A change-only outbox gives a new subscription no backlog; RavenDB subscriptions deliver current state. | The cursor runs over `Documents.ChangeStamp`, plus tombstones for deletes. |
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

Still open: O5 (install full-text locally, needed only by S10) and O8 (the PostgreSQL 17 floor, decided by S2b).

## 4. Milestones

1. **Gate spikes S2b, S3, S4, S5.** The PRD updated; owner sign-off on D1–D7.
2. **Neutral contract additions** (S6, S15), implemented on RavenDB and InMemory first, with no behaviour change on
   RavenDB. Also the core clean-ups from PRD §2.3:
   - the `SparkDataProtectionStorage` rename
   - the `SparkStoreRequirement` default hint
   - the RavenDB wording in messages
   - the generators' neutral declarations (with S5)
3. **`MintPlayer.Spark.Relational`, `MintPlayer.Spark.SqlServer`, `MintPlayer.Spark.PostgreSql`** (D2):
   - `UseSqlServer()`, `UsePostgreSql()` and their `SparkStoreProviders` constants
   - the store, session, query provider, change feed, expiration, the data-protection key store, atomic values,
     leases and reservations
4. **Conformance:** the two relational subclasses, plus new cases on all four providers:
   - row security, `DateTimeOffset`, paging and null ordering
   - case-insensitive ids and escaped prefixes
   - expiry, atomic values, leases, reservations and streaming
   - **a wrong-rows guard for every query shape**

   This comes with the neutral test driver (S17).
5. **Tier 1 and Tier 2 ports** (PRD §5), plus the lookup lost-update fix.
6. **Tier 3:** S7/S14 subscriptions and messaging, S8 revisions, S16 moderation. Then search (S10).
7. **Tier 4:** identity_provider, and dashboards (S9).
8. **Demo, CI, docs:**
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
