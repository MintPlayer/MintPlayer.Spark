# PRD — Sorting and filtering a SparkQuery on a `DateTimeOffset` column

**Branch:** `test/dto-query-sort-filter` off `master` (`ca068ea1`)
**Plan:** [datetimeoffset_query_sort_filter_plan.md](datetimeoffset_query_sort_filter_plan.md)
**Status:** drafted 2026-10-05, after a three-agent code audit and three measured spikes (§3).
Owner decisions are recorded in §8, and plan spikes SP1–SP4 were completed the same day (§3).
**F1–F5 fixed and tested in `5116b09a`.** The red baseline is in the plan, and the full sweep is pending.
Issue #270 was added to scope on 2026-10-05 (§9).
**One PR.** The test coverage, and every defect the coverage work found, land together.

---

## 1. Problem

Nothing proves that a `SparkQuery` sorts or filters correctly on a `DateTimeOffset` property —
the canonical example being Fleet's `Car.RegisteredAt` behind the `registrations` query
(`Cars_Overview` / `VCar`, `apps/Fleet/Fleet/App_Data/Model/Car.json:280-293`).

What exists today (audited across all five test projects and the ng-spark specs):

| Test | What it really proves | Sort | Filter |
|---|---|---|---|
| `Spark.Tests/Services/DateTimeOffsetFilterTests.cs:110,133` | an offered distinct value selects its row; another offset spelling of the same instant matches | ❌ | `Includes` only, on a toy `Slot` entity |
| `Spark.Tests/Services/DateTimeOffsetRoundTripTests.cs` | the offset survives projection from a stored index | ❌ | ❌ |
| `E2E/Mapper/DateTimeOffsetRoundTripTests.cs:112` `Sorting_across_mixed_offsets_is_chronological_by_instant` | **vacuous** — never sends `sortColumns`; it fetches each row by plate search, in seed order, and asserts the seed is ascending | ❌ | ❌ |
| `Client.Tests/SparkClientQueryTests.cs:142-159` | `RegisteredAt desc` is serialised into a mocked request | wire only | ❌ |
| `CodeCoverage.Tests/ApiTokens/ApiTokenRowFilterIsTranslatableTests.cs:130` | the test applies `OrderByDescending` itself, then asserts order-insensitively | vacuous | ❌ |
| Contributions fixtures (`UpdatedAt desc`) | declared sort, order never asserted | ❌ | ❌ |

**No test anywhere executes a sort on a date column through Spark and asserts the row order.**
No test covers `Excludes`, `null`, distinct ordering, or search on a date column. No E2E test
clicks a date header or uses the filter panel on a date column.

## 2. Goals

- **G1** Server-side integration tests (`SparkTestDriver` family, seeded from **JSON fixture
  files**) that pin sort and filter behaviour of a `DateTimeOffset` column on the index-bound
  path (the Fleet shape), the plain-collection path, and the in-memory custom-query path.
- **G2** Make the existing E2E sort test non-vacuous, and add one HTTP-level filter test.
- **G3** Cover the ng-spark client-side (streaming) sort for date and numeric columns.
- **G4** Fix every defect found while doing G1–G3 (§4), each with a test that was red before —
  except F6, which is deliberately left as is (D6).

## 3. Evidence — what was measured

All spikes ran 2026-10-05 against the dev RavenDB at `localhost:8080` (7.2.6, build 72033),
RavenDB.Client 7.2.6, on this machine (**Romance Standard Time**, +01:00 in March, +02:00 in
summer). Spike sources and logs are in the session scratchpad (`spike-json/`, `spike-order/`);
the repo was not modified.

### SP-A RavenDB ordering and equality (both Corax and Lucene, static and auto index)

Seed (true instant order is F G A B C=D E; ordinal order of the written strings is
F G B D C E A — so any string sort is visible):

| Row | `RegisteredAt` as written | Instant |
|---|---|---|
| A | `2027-03-02T01:00+12:00` | `2027-03-01T13:00Z` |
| B | `2027-03-01T09:00-05:00` | `14:00Z` |
| C | `2027-03-01T16:00+01:00` | `15:00Z` |
| D | `2027-03-01T15:00+00:00` | `15:00Z` (ties with C) |
| E | `2027-03-01T22:15+05:45` | `16:30Z` |
| F | `2026-12-31T23:59-08:00` | `2027-01-01T07:59Z` (different UTC day) |
| G | `2027-02-01T00:00Z`, `DeregisteredAt` **absent** | |

| Question | Measured |
|---|---|
| `OrderBy` asc / desc, static index | `F G A B C D E` / `E C D B A G F` — **by instant**, both engines |
| Auto index (`Query<Car>()` no index) | identical to static — by instant |
| Ties C/D | real tie; only `ThenBy` makes it deterministic (`ThenByDescending(plate)` → D C) |
| Nullable asc / desc | no-value rows first asc, last desc. **Corax** orders *absent* before explicit `null`; **Lucene** mixes them in storage order |
| `== null` | matches explicit `null` only, **never** an absent field (both engines) |
| `==`, `In`, `>=` with any offset spelling | matches by **instant** (`== 16:00+01:00` ≡ `== 15:00Z` ≡ `== 10:00-05:00` → C D) |
| Projection from stored index fields | `+00:00`; only the `{Name}Raw.V` wrapper keeps the original offset |

**Conclusion:** RavenDB itself sorts and matches `DateTimeOffset` correctly. The risks are in
Spark's own layers, which is what §4 found.

### SP-B Fixture seeding and filter-value parsing

| Input (offset written) | `JsonFixtureImporter` today stores | typed `StoreAsync` stores |
|---|---|---|
| `09:00-05:00` | `"2027-03-01T15:00:00.0000000"` (local clock, **no offset**) | `"…09:00:00.0000000-05:00"` |
| `09:00+12:00` | `"2027-02-28T22:00:00.0000000"` | `"…+12:00"` |
| `09:00Z` | `"…09:00:00.0000000Z"` | `"…Z"` |
| June `-05:00` | `"2027-06-01T16:00:00.0000000"` (+02:00 DST) | `"…-05:00"` |

| Wire filter value | `QueryExecutor.TryConvertFilterValue` (STJ) | `EntityMapper` write path |
|---|---|---|
| `"2027-03-01T09:00:00"` (no offset) | `09:00+01:00` — **server local** | `09:00+00:00` — **UTC** |
| `"2027-03-01"` (date only) | `00:00+01:00` — server local | `00:00+00:00` — UTC |
| `"…Z"`, `"…+02:00"`, `"…-05:00"` | kept | kept (agree) |

Filter values arrive as `JsonElement` (measured), deserialised with no options
(`QueryExecutor.cs:2473`).

### SP-C Code-read findings (no execution)

- `ApplySorting` (`QueryExecutor.cs:1854-1896`) chooses `OrderBy` vs `ThenBy` by loop index
  `i == 0`; a refused or unknown first column `continue`s (`:1872-1877`, `:1880-1888`), so the
  next column becomes `ThenBy` on an unordered queryable. The in-memory `SortMappedRows`
  (`:1499-1521`) is correct (keys on `ordered is null`).
- Direction: anything but `"desc"` (case-insensitive) is ascending (`:1893`, `:1510`);
  `Endpoints/Queries/Execute.cs:100-133` validates `Property` only.
- Request `sortColumns` **replace** the declared sort (`Execute.cs:202`, `SparkQuery.cs:113-118`);
  an empty array keeps it.
- In-memory sort (`RowSortComparer`, `:2712-2728`) compares boxed `DateTimeOffset` via
  `CompareTo` — instant order; nulls last ascending (note: the opposite of RavenDB's nulls-first).
- Streaming queries are never sorted server-side (`StreamingQueryExecutor.cs:60-185`). The
  client sorts them in `spark-query-list.component.ts:440-446` with
  `String(a).localeCompare(String(b))` over the **raw wire ISO string** (offset included, trailing
  fraction zeros trimmed). `parseWireDate` (`models/src/datetime-local.ts:30-38`) already exists.
- Free-text search: pushdown covers `string` properties only (`QueryExecutor.cs:2079`); the
  in-memory fallback matches `attr.Value?.ToString()` (`:1964`), i.e. server-culture text of the
  restored value.
- Distinct values de-duplicate via `HashSet<object?>` (`:200,222`) → same-instant rows with
  different offsets collapse to one entry (by design: `DateTimeOffsetFilterTests.cs:133`).
- `IRequestTimeZoneResolver` has no consumer in the query pipeline.

### SP1–SP4 — plan spikes (2026-10-05, all measured unless marked)

**SP1 — F3 impact (measured, exact reflection call copied from `QueryExecutor.cs:1894-1908`).**

| Source | Result of `ThenBy` without a prior `OrderBy` |
|---|---|
| `list.AsQueryable()` | `Invoke` succeeds; enumeration throws `IndexOutOfRangeException`, surfacing from `MaterializeQueryable` (`:1716`) as a `TargetInvocationException` → **500** |
| `list.AsQueryable().Where(..)` | `Invoke` throws `ArgumentException` ("`IQueryable` cannot be used for parameter of type `IOrderedQueryable`") → **500** |
| Raven auto and static index | no error; the client emits `order by Name` → correctly ordered |

Custom queries reach `ApplySorting` whenever they return any `IQueryable` (`:1271`, `:1348`, `:1373-1376`).
A first column is refused, while `i` still advances, in four cases:
- not on the query surface (`:2607`)
- an array attribute (`:2614`)
- `canSort: false` (`:2619`)
- no property on the row type (`:1883-1891`)

The first three pass the endpoint allow-list, so **F3 is reachable over HTTP**. A model whose
*declared* sort starts with an array attribute triggers it with no caller involved.
**F3 → CONFIRMED (measured), a 500 on in-memory custom queries.**

**SP2 — test host (measured, in-repo throwaway spike, removed; 2/2 green).**
- Works as planned with the F1-fixed importer called directly from `SharedSparkHost.BeforeHostAsync`,
  after deploying the index. The index is non-stale and fully populated on the first test.
- `Raven-Clr-Type` must be `MintPlayer.Spark.Tests.Services.RegCarQuerySortFilterTests+RegCar,
  MintPlayer.Spark.Tests`. Without it the importer stamps a `Dictionary` type: typed paths still
  work, but `LoadAsync<object>` breaks. Keep the tag.
- Index-bound and plain-collection queries both sort by instant and return exact offsets.
  - A `Z` value comes back as `+00:00`.
  - Both explicit-null and absent `DeregisteredAt` come back `null`.
- The plain-collection query runs on an auto index provided the entity definition has no
  `IndexName`.
- Declared query sorts are set via `EntityTypeFile.Queries[].SortColumns` and are honoured.

**SP3 — simulating a zone in `dotnet test` (measured on WSL Ubuntu 24.04 with the process in
`Etc/UTC`, and on Windows; xUnit 2.9.3 as the repo).**

| Variant | Linux | Windows |
|---|---|---|
| (a) `TZ=Europe/Brussels` + `TimeZoneInfo.ClearCachedData()` in a `DisableParallelization` collection | `Local` = Brussels +01:00, STJ yields `+01:00` → **F2 reproduces**. 2×784 samples by parallel neighbours saw only UTC; a deliberately leaky control *was* detected | `TZ` ignored; Local stays Romance (+01:00 here) |
| (b) child process | works (64–157 ms); `RemoteExecutor` is **not** on nuget.org | `TZ` ignored |
| (c) guard: fail when `Local` offset is zero | fires for `Etc/UTC` and for an invalid `TZ` (silent UTC fallback); silent under Brussels | not measured on a UTC Windows box (would need `tzutil`); fires by construction |

**SP4 — streaming wire shape (code read).**
- A `DateTimeOffset` cell is serialised raw by default System.Text.Json: the fraction is trimmed
  (`…T09:00:00-05:00`, `…T09:00:00.5-05:00`) and a zero offset is written `+00:00`, never `Z`.
- `DateTime` follows its `Kind`; `DateOnly` is written `"2027-03-01"`.
- Column `dataType` values: dates are `datetime` and `date` (`isDateDataType`, `datetime-local.ts:115`);
  numbers are `number` and `decimal` (`SparkModelShape.cs:194-210`).
- `parseWireDate(value: unknown): Date | null` accepts every shape above.
- ng-spark is **22.28.0** and auto-published on merge to master (`dotnet-build-master.yml:261-277`),
  so the D4 fix ships as **22.28.1**.

## 4. Defects

| # | Defect | Status | Evidence |
|---|---|---|---|
| **F1** | `JsonFixtureImporter` rewrites every ISO date to the machine's local clock and drops the offset (`JObject.Parse` default `DateParseHandling.DateTime`, `JsonFixtureImporter.cs:38`, persisted via `:53`). Any JSON-seeded DTO test is checking a timezone-dependent corruption of its own fixture. | **CONFIRMED (measured)** | SP-B table 1; fix measured: `JsonTextReader { DateParseHandling = None }` + `JObject.Load` stores exactly what `StoreAsync` stores |
| **F2** | Offset-less filter value is read as **server-local**; the write path reads the same string as **UTC** (`EntityMapper.cs:1149-1160`, `AssumeUniversal`). The same wire string is a different instant in a filter than in a save; the gap moves with the server's zone and DST. | **CONFIRMED (measured)** | SP-B table 2 |
| **F3** | Refused/unknown first sort column turns the next column into `ThenBy` on an unordered queryable. Harmless on Raven; a **500** on an in-memory `IQueryable` custom query, reachable over HTTP. | **CONFIRMED (measured)** | SP1 |
| **F4** | Client streaming sort compares raw ISO strings: mixed offsets and different fraction lengths sort by text, not instant. Numbers sort as text too (`"10" < "9"`). | **CONFIRMED (code read)** | SP-C; red spec in M5 |
| **F5** | Sort direction is not validated; `"dsc"`/`"descending"` silently sort ascending. | CONFIRMED | SP-C |
| **F6** | Date search is inconsistent: pushdown never matches a date, the in-memory fallback matches culture-formatted text — the result depends on whether pushdown was possible. | CONFIRMED (code read) — **not fixed (D6)** | SP-C |

Not defects, but pinned by tests because they look like bugs (§5): same-instant values are one
filter term and one distinct entry; no-value ordering is nulls-first on Raven and nulls-last in
memory; ties need a tie-breaker.

## 5. Decisions

| # | Decision | Why |
|---|---|---|
| **D1** | Fix **F1** in `JsonFixtureImporter` (`DateParseHandling.None`). Strings in a fixture are stored verbatim. | The user's stated seeding mechanism is JSON fixtures; without this every DTO fixture is wrong in a zone-dependent way. Measured fix (SP-B). Also a correctness fix for every existing consumer of the importer. |
| **D2** | Fix **F2**: `TryConvertFilterValue` parses `DateTimeOffset`/`DateTimeOffset?` from a JSON string with `DateTimeOffset.Parse(s, InvariantCulture, AssumeUniversal)` — the **same contract as the write path**. Factor that parse into one shared helper used by both `EntityMapper` and the filter path so they cannot drift again. | A filter and a save must agree on what a string means. UTC is the existing documented write contract; changing the write path instead would alter stored data. |
| **D3** | Fix **F3**: key `OrderBy`/`ThenBy` on "has an ordering been applied", mirroring `SortMappedRows`. | One-line, mirrors the already-correct sibling. |
| **D4** | Fix **F4**: the streaming comparator becomes type-aware using the column's `dataType` — `datetime`/`date` via `parseWireDate(...).getTime()`, numeric types numerically, everything else `localeCompare` as today. Null placement stays as the existing spec pins it (`spark-query-list.component.spec.ts:312,329`). | Matches server semantics (instant order) without changing the null contract the spec locks in. |
| **D5** | **F5 — DECIDED (owner, 2026-10-05):** the endpoint returns **400** for a direction other than `asc`/`desc` (case-insensitive), consistent with the existing 400 for an unknown sort property (`Execute.cs:100-134`). Every in-repo caller (ng-spark, `SparkClient`, tests) is checked to send a valid direction. | Owner: *"the Spark frontend (and any other caller) must post a correct request, otherwise these failures become silent."* |
| **D6** | **F6 — DECIDED: no change.** The in-memory search fallback keeps matching non-string attributes; F6 is recorded as a known inconsistency, not fixed, and no test pins it (T25 dropped). | Owner leaned "no", decision delegated. Removing it would silently stop matches that custom-query users may rely on today, while the real fix (search on what the *viewer* sees, in the viewer's zone) is out of scope (§7). Pinning an inconsistency in a test would only make the real fix harder. |
| **D7** | Server tests live in `MintPlayer.Spark.Tests` (the `SparkTestDriver` family — they are integration tests, not E2E). Use a **test-local copy** of Car/`Cars_Overview`/`VCar` named `RegCar`/`RegCars_Overview`/`VRegCar`. | Fleet's index is source-generated inside the Fleet web app; Spark.Tests has no generator reference; simple-name assembly scans would collide on `Car`/`CarActions`. |
| **D8** | Fixture lifecycle: `SharedSparkHost<TContext>` (one database + host per class) for the read-only sort/filter class; seeding via `JsonFixtureImporter.ImportAsync` in `BeforeHostAsync`. | Many read-only tests over one dataset; per-case databases cost ~200 ms each with an index. |
| **D9** | Tests assert the **order of instants** and the **offset of every returned row** (`EqualsExact`), never `==` on `DateTimeOffset`. Each sort test carries a guard that the fixture is *not* also ordered by wall clock / string. | `DateTimeOffset.Equals` compares instants; that is how the vacuous E2E test survived. |
| **D11** | Zone simulation (SP3 variant a + guard c): the F2 tests (T15, T24) live in their own classes in a `[CollectionDefinition("LocalZone", DisableParallelization = true)]` collection. Each sets `TZ=Europe/Brussels` + `TimeZoneInfo.ClearCachedData()` and restores both in `Dispose`, and fails (never skips) via `RequireNonUtc` when `Local` has a zero offset. T15's `SharedSparkHost` is created and disposed **inside** that collection, so no host thread from another class runs while the zone is changed. The helper lives in `tests/MintPlayer.Spark.Tests/_Infrastructure/LocalZone.cs`. | Owner asked for simulation inside `dotnet test`; SP3 measured it reproducing F2 on a UTC Linux process, with no leakage and a working leak detector. |
| **D10** | No-value ordering is pinned only as a **group** (first asc, last desc) — not null-vs-absent within the group. | Corax and Lucene disagree on null vs absent (SP-A); the test must not pin an engine artefact. |

## 6. Test catalogue

### 6.1 `Spark.Tests` — index-bound path (Fleet shape), `RegCarQuerySortFilterTests`

Fixture `Services/Data/reg-cars.json` with rows A–G (§3) plus `DeregisteredAt` (mixed offsets,
`null` on B and E, **absent** on G), and plates chosen so plate order ≠ instant order.

| # | Test | Asserts |
|---|---|---|
| T0 | Fixture guard | every seeded document loads with its exact offset (`EqualsExact`) — catches F1 regressing |
| T1 | asc by instant | `F G A B {C,D} E`; guard: not ordinal/wall-clock order |
| T2 | desc | exact reverse (with tie-breaker) |
| T3 | no `sortColumns` → declared `RegisteredAt desc` applies | |
| T4 | request sort **replaces** declared sort | sort by `LicensePlate` ignores the declared date sort |
| T5 | tie C/D with `ThenBy LicensePlate` asc and desc | deterministic order both ways |
| T6 | `ThenBy RegisteredAt` as secondary key | primary on a string column, date breaks ties by instant |
| T7 | paging: concatenation of `take: 2` pages = full sorted list | index-bound paging is in memory after sort |
| T8 | every sorted row keeps its original offset | `EqualsExact` against the fixture (restorer after sort) |
| T9 | nullable asc / desc | no-value group first asc, last desc (D10); dated rows by instant |
| T10 | sort + `Includes` filter combined | |
| T11 | `Includes` with another offset spelling of C's instant | matches C **and** D |
| T12 | multiple `Includes` | union |
| T13 | `Excludes` | complement |
| T14 | `Includes [null]` on nullable column | explicit-null rows **only** (B, E). The absent G is not selected: a known RavenDB limitation, already pinned by `AbsentVersusNullFieldTests` (there is no LINQ shape for "absent or null"). The first draft expected B, E, G and went red; corrected |
| T15 | **offset-less** include `"2027-03-01T15:00:00"` | matches C and D (UTC, D2). Red before the fix under the simulated zone; **moved to its own class `RegCarLocalZoneFilterTests` in the `LocalZone` collection (D11)** |
| T16 | date-only include `"2027-03-01"` | matches only an instant at `00:00Z` (no range semantics) — pins it |
| T17 | unparseable include | zero rows, not 500 |
| T18 | distincts: ordered by instant; C/D collapse; each offered value round-trips as a filter | |
| T19 | `"dsc"` / `"descending"` / `""` direction | 400 at the endpoint (D5); `asc`/`ASC`/`desc`/`DESC` accepted |

### 6.2 `Spark.Tests` — other execution paths

| # | Test | Path |
|---|---|---|
| T20 | asc/desc by instant on a **plain collection** source (no index bound) | auto index |
| T21 | custom query returning `IEnumerable` → in-memory `RowSortComparer` sort by instant; nulls last | `needsInMemorySort` |
| T22 | custom query returning `list.AsQueryable()`, `sortColumns = [<not-on-surface column>, RegisteredAt]` → rows sorted by instant, no exception (F3) | **red today: 500** (SP1) |
| T22b | same, with a declared model sort whose first column is an array attribute | red today (SP1) |
| T23 | `JsonFixtureImporter` unit test: offsets and `Z` stored verbatim | F1 |
| T24 | shared parse helper: offset-less and date-only → UTC; explicit offsets kept | D2 |
| ~~T25~~ | ~~search on a date column~~ | dropped — D6 |

### 6.3 E2E (Fleet over HTTP)

| # | Test |
|---|---|
| T26 | rewrite `Sorting_across_mixed_offsets_is_chronological_by_instant` to send `sortColumns: RegisteredAt asc` and read the order from one result page, scoped by a plate prefix search |
| T27 | `Includes` filter on `RegisteredAt` over HTTP with another offset spelling, scoped by plate prefix |
| T32 | multi-column sort over HTTP: `registrations` sorted by a string column, then `RegisteredAt` → order within each group is by instant (owner question 2026-10-05: "will we be able to sort on 2 and more columns from the frontend?") |

### 6.4 ng-spark (`spark-query-list.component.spec.ts`)

| # | Test |
|---|---|
| T28 | streaming sort on a `datetime` column with mixed offsets and mixed fraction lengths → instant order (red before D4) |
| T29 | streaming sort on a numeric column → `9 < 10` (red before D4) |
| T30 | existing null-placement specs still pass unchanged |
| T31 | multi-column sort from the grid: a header click then two shift-clicks on a second header → `executeQuery` posts `sortColumns: [{A, asc}, {B, desc}]`, in click order, mapped from the datatable's `ascending`/`descending`. Shift-click semantics live in `mp-datatable` (`H(...)`: add asc → flip to desc → remove) |
| T31b | streaming multi-column sort: a string column first, then a `datetime` column → ties on the string are broken by instant |

## 7. Out of scope (genuinely not being done)

- **Date range / date-only / "on this day" filtering.** `QueryColumnFilter` has only
  `Includes`/`Excludes`; a range filter is a new feature with UI, wire and index design, not a
  defect. T16 pins today's behaviour so the future feature has a baseline.
- **Viewer-timezone-aware search or distinct labels.** Requires the viewer zone in the query
  pipeline, which has no consumer today.
- **A Playwright test clicking a date header.** The HTTP-level E2E (T26/T27) plus the client
  spec (T28) cover both halves; a UI click adds a browser dependency for no extra assertion.

## 8. Owner answers (2026-10-05)

1. **D5** — yes, 400. Callers must send a correct request.
2. **D6** — delegated, owner leaning "no" → decided no change (D6).
3. **T15 on CI** — owner: *"Isn't there a way to simulate this during `dotnet test` too?"* →
   **D11:** T15/T24 run under a **simulated non-UTC zone inside `dotnet test`**, chosen by SP3:
   - (a) in-process: set `TZ` and call `TimeZoneInfo.ClearCachedData()` in a non-parallel xUnit
     collection, restore afterwards. On Linux, .NET resolves `TimeZoneInfo.Local` from `TZ`.
   - (b) a child process (e.g. `Microsoft.DotNet.RemoteExecutor`) started with `TZ` set.
   - (c) `TZ` for the whole run via `.runsettings` `EnvironmentVariables`, which makes the entire
     suite non-UTC.

   On Windows `TZ` is ignored, so every variant carries a **guard**: the test *fails* (not skips)
   when `TimeZoneInfo.Local` has a zero offset at the test instant. A vacuous run is then impossible
   anywhere.

## 9. Issue #270: the code fix, for hand-written indexes only

**Added to scope 2026-10-05.** [#270](https://github.com/MintPlayer/MintPlayer.Spark/issues/270)
asks for an "Add Sort property" code fix for SPARK005. The owner's intent: the tests in §6 verify
the correct sort and filter behaviour; the code fix is still needed, **but only for hand-written
RavenDB indexes**. Generated indexes (`[GenerateIndex]`, `SparkIndexCreationTask<T>`) already get
every companion from the generator.

A three-agent investigation found most of #270's premises out of date (§9.1). It also found a
gap that #270 did not name, and that matters more for this PR's subject (§9.2).

### 9.1 Evidence

**SP-270 — which hand-written index shapes need a companion (measured 2026-10-05, RavenDB 7.2.6,
Corax and Lucene, 8 hand-written indexes, all `Normal`/0 errors; `scratchpad/sp270/`).**

| Shape → ordered by | Corax | Lucene | Correct |
|---|---|---|---|
| `DateTimeOffset`, default indexing | by instant | by instant | ✅ |
| `DateTimeOffset`, `FieldIndexing.Exact` | by instant | by instant | ✅ (term is canonical UTC `…Z`; Exact does **not** keep the offset; equality matches every offset spelling) |
| `DateTimeOffset`, Exact + a `{Name}Sort` copy (`= x.P` or `= x.P.UtcDateTime`) | by instant | by instant | ✅, but no better than without it |
| `DateTimeOffset`, `FieldIndexing.Search` | by instant | by instant | ✅ (Lucene tokenizes the text but sorts on its numeric date field) |
| string, `FieldIndexing.Search` | **arbitrary** (`EBFAC`) | **arbitrary** (`CBFEA`), and the two engines disagree | ❌ |
| string, Search, ordered by a plain copy `= x.Model` at default indexing | alphabetical | alphabetical | ✅ (case-insensitive) |
| string, `FieldIndexing.Exact` | alphabetical | alphabetical | ✅ (ordinal, case-sensitive) |

**Conclusion:** only a **Search-indexed string** needs a sort companion. No `DateTimeOffset`
shape does.

**Code read (two agents, file:line in their reports):**

- **The convention changed after #270 was filed.** For `[Search]` the roles were swapped:
  - The base field stays plain and sortable.
  - The analyzed copy is `{Name}Search`, declared `FieldIndexing.Search` (`Naming/IndexNaming.cs:61-82`).
  - `{Name}Sort` survives only for `[Breadcrumb]`.
  - A `DateTimeOffset` gets a `{Name}Raw` wrapper (`:89`).
  - So #270's title, `docs/issue_210_PRD.md` R24/R26 and `release-notes-preview-53.md:123,176` are stale.
- **SPARK005** (`SortCompanionAnalyzer.cs`, in `MintPlayer.Spark.SourceGenerators`):
  - Registered with `RegisterSymbolAction(NamedType)` (`:49`) and reported on the projection's own
    property (`:92-101`). That satisfies both measured light-bulb prerequisites ([[project-analyzer-code-fixes]]).
  - It fires on a hand-written `Index(f, Search | Exact)` in the index constructor when the projection
    lacks `{f}Search` (`:52-118`, `:201-252`).
  - **It never checks the field's type** (`:225-226`). So it also fires for `Exact` strings and for
    `DateTimeOffset` with `Exact`, and for those it prescribes `{f}Search`, the wrong remedy. Both
    are false positives (SP-270).
- **The packaging premise is gone:**
  - `GetTypes()` no longer throws: the shared test harness references a real
    `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0
    (`tests/MintPlayer.Spark.SourceGenerators.Tests/…csproj:14-26`).
  - Code fixes ship today in `MintPlayer.Spark.LibraryGenerators/CodeFixes/` (SPARK016, SPARK017)
    and `MintPlayer.Spark.Contributions.SourceGenerators/CodeFixes/` (SPARK029, SPARK031).
  - Workspaces is referenced with `PrivateAssets="all" ExcludeAssets="runtime"`.
  - Tests use `tests/…/_Infrastructure/CodeFixHarness.cs` (`RunAnalyzerFixAsync`, multi-project
    `AdhocWorkspace`, real file paths, fails on a no-op fix).
- **No app in `apps/` triggers SPARK005.** Every index there is a `SparkIndexCreationTask<T>` with
  generated companions. The fix serves external consumers and the fixtures.

**SP5 — runtime effect of an unmapped companion (measured 2026-10-05, the real `IQueryExecutor`,
RavenDB 7.2.6 Corax, the §6 fixture's offsets).** Every index deployed `Normal`, with 0 errors,
including the ones with `Index(...)` declarations on fields the map never assigns.

| Variant | Measured |
|---|---|
| `RegisteredAtRaw` declared + `Index(No)`, **not mapped** | every row `+00:00`; the instant is right on 7/7, `EqualsExact` only 2/7 (D and G, already `+00:00`); sort still `F G A B C D E`; **no warning** |
| mapped (control) | `EqualsExact` 7/7 |
| no `Raw` property at all | same values; `ProjectedOffsetRestorer` **does** warn, once. The warning covers a *missing* property only |
| `ModelSearch` declared + `Index(Search)`, **not mapped** | search "Volvo" / "Audi" → **0 rows, HTTP 200** (mapped control: 3 / 4); nothing logged |

The generator-declared variant could not run in `MintPlayer.Spark.Tests`: the test project does not
reference the source generator, because an `OutputItemType="Analyzer"` reference does not flow to
project consumers. The generated companion is the same shape as the hand-written one
(`HandWrittenProducer.cs:155-260`), so the measured rows stand for it. Two indexes over one entity
need `[DefaultIndex]` on one of them for the host to boot.

**SP6 — map shapes a code fix can edit (25 fixtures, each compiled before and after the edit, 0
errors).**
- **Editable:**
  - `Map = x => …` or `AddMap<T>(x => …)` in a single constructor;
  - a query whose final `select` (following any `into` continuation) is an initializer, or a method
    chain whose **outermost** call is `.Select(x => <initializer>)`;
  - anonymous `new { … }` or `new V { … }`;
  - the field appearing **exactly once** as a top-level member, named or implicit (`car.F`);
  - `let` clauses and ternaries are safe;
  - under multi-map, every map must qualify, and the fix edits all of them.
- **Refused** (diagnostic, no code action):
  - any `Reduce`. A map-only edit still *compiles*, so the compiler would not catch the
    map/reduce mismatch: the refusal must be deliberate;
  - a helper-method projection;
  - a terminal operator other than `Select`, or a query ending in `group`;
  - a block-bodied lambda;
  - the field missing, duplicated, or only nested;
  - the companion present in only some maps.
- **No-op:** the companion is already mapped in every map.
- **Index retarget** works for `nameof(V.F)`, `nameof(F)`, `"F"` and `x => x.F`.
- **Reuse:** the *pattern* of `ValueObjectSyntaxEditor` (idempotent transforms), not its code, which
  is internal to LibraryGenerators. The initializer edit is a text insertion after F's member,
  matching that member's line indentation.

**SP7 — id and overlap (code read).**
- Next free id: SPARK038. The registry is `docs/diagnostics.md`; there are no `AnalyzerReleases`
  files.
- **SPARK006 is effectively blind today**:
  - `ConfigureGeneratedCodeAnalysis(None)`, and a diagnostic located in a generated tree is dropped,
    so it can never fire for a *generated* companion;
  - it never looks at `{F}Raw` or at the `[Search]`/`IndexSearchFields()` shape.
- Its title already reads "…never assigned in the index map".
- Nothing in the repo suppresses SPARK005 or SPARK006.
- The house rule (`SortCompanionAnalyzer.Rules.cs:7-10`) is Warning for silent wrong data, Error
  only for a contract the runtime refuses. SPARK018 is a Warning although its `DateTimeOffset` case
  breaks the index outright.

### 9.2 The gap #270 did not name: a declared companion that the map never assigns

For a **partial** hand-written pair, the generator declares the companion property and its
`Index`/`Store` calls:
- `[Search]` gives `{Name}Search`;
- a `DateTimeOffset` gives `{Name}Raw` with `FieldIndexing.No`.

It **cannot write the map assignment**, because a generator cannot edit an object initializer in
someone else's constructor (`docs/issue_210_PRD.md` R31). **Nothing reports a missing
assignment**:
- SPARK006 only covers fields found by the hand-written `Index(...)` scan, so it is silent for the
  `[Search]`/`IndexSearchFields()` shape.
- `ProjectedOffsetRestorer` warns only when the `{Name}Raw` *property* is missing
  (`ProjectedOffsetRestorer.cs:156-167`), not when it exists but is never mapped.

The consequences are this PR's subject, on a hand-written index:
- **an unmapped `{Name}Raw`** returns every `DateTimeOffset` in that column as `+00:00` in the grid
  while the detail page shows the real offset. That is the defect T8 pins for the generated path.
- **an unmapped `{Name}Search`** makes search silently find nothing.

This is the diagnostic a #270 code fix belongs on.

### 9.3 Decisions

| # | Decision | Why |
|---|---|---|
| **D12** | Code fixes live in the generator assembly that hosts their diagnostic: `MintPlayer.Spark.SourceGenerators` or `MintPlayer.Spark.LibraryGenerators`. **Never a separate `.CodeFixes` assembly.** For SPARK005 and the new diagnostic that means `MintPlayer.Spark.SourceGenerators`, next to `SortCompanionAnalyzer`, with the Workspaces reference added as LibraryGenerators has it. | Owner, 2026-10-05: "codefixes can live in the MintPlayer.Spark.SourceGenerators and MintPlayer.Spark.LibraryGenerators". Only SourceGenerators reaches every consumer that can see SPARK005, including NuGet users of the standalone package; `CodeCoverage.Tests` references it alone. |
| **D13** | **SPARK005 and SPARK006 apply to `string` fields only, and name the right companion per indexing.** <ul><li>Every non-string field (`DateTimeOffset`, numbers…) stops firing.</li><li>`Search` on a string → `{F}Search`, as today.</li><li>**`Exact` on a string stays in scope**, but its remedy becomes **`{F}Sort`**: a plain copy, which `ResolveSortProperty` (`QueryExecutor.cs:2622-2629`) orders by. Not `{F}Search`, an analyzed copy that sorts nothing.</li></ul>*Revised 2026-10-05, at implementation. The first draft dropped `Exact` entirely.* | The `DateTimeOffset` triggers are measured false positives (SP-270: by instant under every indexing). Keeping `Exact` strings follows the documented earlier decision at `SortCompanionAnalyzer.cs:217-224`: Exact orders by ordinal, case-sensitively ("ZZ Top" before "Zeta One"), and SP-270 measured exactly that. The draft would have re-litigated a decided point. Prescribing `{F}Search` for an `Exact` field was a second, unmeasured-until-now defect: a companion of that name is not what sorting reads. |
| **D14** | **SPARK005 code fix ("Make Search a companion")**, for a hand-written `Index(nameof(V.F), FieldIndexing.Search)` on a string: <ul><li>add `[IgnoreProperty] public string? FSearch { get; set; }` to the projection (nullability mirrored, `= default!` when non-nullable);</li><li>add `FSearch = <same expression as F>` to the map's projection initializer;</li><li>retarget the `Index` call to `nameof(V.FSearch)`.</li></ul>One solution-level edit (`createChangedSolution`), because the index and the projection are usually in different documents. | Mirrors exactly what the generator emits (`GenerateIndexGenerator.cs:759-768`). Leaves `F` plain and sortable (SP-270: an Exact or default string sorts correctly). Adding only the property would trigger SPARK006, and a fix that leaves a diagnostic behind is worse than none ([[project-analyzer-code-fixes]]). |
| **D15** | **Widen SPARK006, keeping its id**, to "index companion declared but never assigned in the map", for hand-written indexes. Its trigger is a projection that has a `{F}Search` or `{F}Raw` property, generated or hand-written, whose name appears nowhere in the index constructor. It reports on the hand-written base property `F`. The fallback is the companion if that is hand-written, otherwise the projection type, because a diagnostic located in generated code is dropped (SP7). It is a symbol action, like SPARK005, and its title and message change from "Sort companion" to "Index companion". **Severity: Warning.** The owner may choose Error instead; SP5 argued for Error because the detection is exact and the runtime has no visible symptom. Indexes deriving from `SparkIndexCreationTask<T>` are exempt: the generator writes their map. | §9.2, measured in SP5: an unmapped Raw gives `+00:00` on every row and an unmapped Search gives 0 rows, both silently. Same defect as SPARK006's title, so the same id: no two warnings for one cause and no dead id (SP7). Warning follows the house rule and SPARK018's precedent, and turning an existing Warning into an Error would break consumers' builds. The release notes state the moved report location, which affects any `#pragma` around a companion property. **Revised at implementation: there is no exemption for `SparkIndexCreationTask<T>`.** Its base class supplies the `IndexSearchFields()` *call*, but its map is still hand-written, so it is exactly where a companion can go unassigned (CodeCoverage's `Commits_ByRepository` maps a `{Name}Raw` by hand). Only a fully generated index, which has no hand-written constructor, is skipped. A companion that appears only inside an `Index`/`Store`/… field-option call counts as **unassigned**: SP5's unmapped index named its Raw field in `Index(..., No)` and still returned `+00:00`. |
| **D16** | **Code fix for D15:** insert the map assignment exactly as the generator writes it (`GenerateIndexGenerator.cs:779-789`, pinned at `GenerateIndexGeneratorTests.cs:663-664`): <ul><li>`FSearch = <F's expression>`, or</li><li>`FRaw = new global::MintPlayer.Spark.Abstractions.SparkIndexValue<T> { V = <F's expression> }`, with `T` taken from the projection property's type, nullability mirrored.</li></ul>**Supported shapes and refusals are exactly SP6's list** (§9.1): `let`, ternaries, implicit members, `into` and multi-map are editable; `Reduce`, helper projections, a non-`Select` terminal, block lambdas and partial presence are refused, with the diagnostic and no code action. | Measured, not guessed: 25 fixtures compiled before and after. `Reduce` is refused deliberately, because the compiler would not catch the mismatch. |
| **D17** | Stale documentation is corrected in this PR: <ul><li>#270's premises in `docs/issue_210_PRD.md` R24/R26;</li><li>`release-notes-preview-53.md`;</li><li>the "own assembly" claims in `docs/issue_298_…PRD.md:168,416` and `docs/issue_272_…PRD.md:399`.</li></ul>The PR body explains the reframing and closes #270. | One PR (CLAUDE.md). A doc that tells the next person a CodeFixProvider cannot live in the analyzer assembly would cost them a day. |
| **D18** | The code fixes are an **IDE-only affordance**: `dotnet build` gains nothing ([[project-analyzer-code-fixes]]). The light bulb is verified once by hand in Visual Studio (plan M14); the automated proof is `CodeFixHarness`. | Measured lesson: a diagnostic can squiggle perfectly and offer no fix. Only the IDE shows that. |
| **D19** | `MintPlayer.Spark.SourceGenerators` gets a **minor** version bump (major stays `11` for net11.0, CLAUDE.md). | New analyzer behaviour and a new diagnostic. Not a platform change. |

**Found while implementing (2026-10-05).** T34 re-analyzes the fixed documents, and SPARK005 flagged
the fix's own output: `Index(nameof(VCar.ModelSearch), Search)` beside a plain `Model` drew
"add `ModelSearchSearch`". That is the convention itself, exactly what the generator emits, so any
hand-written index that followed it already got this false warning. The fix: a `{F}Search` field
whose base `F` exists is the analyzed copy and needs no companion.

**The apps are clean, and that was checked rather than assumed.** The full solution build
(2026-10-05) reports no SPARK005/006. To show that silence is not an analyzer that never ran, the
`DateRaw = …` assignment in CodeCoverage's `Commits_ByRepository.cs:125` was commented out and only
CodeCoverage rebuilt. It reported `SPARK006 … 'DateRaw' is never assigned … every 'Date' is read
back from the index at offset +00:00` at `:40`. The file was then restored and rebuilt clean.

### 9.4 Tests

| # | Test | Where |
|---|---|---|
| T33 | SPARK005 never fires for a `DateTimeOffset`, whether `Exact` or `Search`. It still fires for a `Search` string, naming `{F}Search`. For an `Exact` string it fires naming **`{F}Sort`**, and a mapped `{F}Sort` silences it. | `SortCompanionAnalyzerTests` (red before D13) |
| T34 | D14 fix on a two-document fixture (index file + projection file): property added, map assignment added, `Index` retargeted; the result compiles and SPARK005 **and** SPARK006 are gone | `CodeFixHarness.RunAnalyzerFixAsync` |
| T35 | widened SPARK006 fires for an unmapped `{F}Raw` and an unmapped `{F}Search`, both as a generated property on a partial pair and hand-written, and also when the companion is named only in `Index(..., No)`. It reports on `F`. Silent when mapped, and silent for a lookalike domain property without `[IgnoreProperty]`. The existing SPARK006 test still passes. | analyzer test |
| T36 | D16 fix inserts the exact generator text for `DateTimeOffset` and `DateTimeOffset?` (nullability mirrored) and for a Search string; an unsupported map shape gets the diagnostic and **no** code action | `CodeFixHarness` |
| T37 | **Runtime proof of §9.2** (SP5's assertions). A hand-written `RegCars_Overview` variant declares `RegisteredAtRaw` but does not map it. Through the executor it must show: <ul><li>every row `Offset == 0`, with the same `UtcTicks` as the fixture;</li><li>rows A, B, C, E and F failing `EqualsExact`;</li><li>sort order still `F G A B C D E`, so the defect is invisible from sorting.</li></ul>A search twin: `ModelSearch` declared and unmapped, searching "Volvo" gives 0 rows where the mapped control gives 3. The variant index needs `[DefaultIndex]` on the existing one. | `Spark.Tests`, reusing the §6 fixture |
| T38 | A hand-written index with `Index(nameof(V.RegisteredAt), FieldIndexing.Exact)` sorts and filters by instant through the executor (SP-270 as a regression test for the D13 rationale) | `Spark.Tests` |

### 9.5 Out of scope (genuinely not being done)

- **A light bulb for generated indexes.** They have nothing to fix: the generator writes the map.
- **Rewriting arbitrary map shapes.** D16 covers the shapes SP6 proves safe; the rest keep the
  diagnostic without an automatic fix. That is a limit of safe automation, not deferred work.

