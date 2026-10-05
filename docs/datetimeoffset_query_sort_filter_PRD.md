# PRD — Sorting and filtering a SparkQuery on a `DateTimeOffset` column

**Branch:** `test/dto-query-sort-filter` off `master` (`ca068ea1`)
**Plan:** [datetimeoffset_query_sort_filter_plan.md](datetimeoffset_query_sort_filter_plan.md)
**Status:** drafted 2026-10-05, after a three-agent code audit and three measured spikes (§3).
Owner decisions recorded in §8; plan spikes SP1–SP4 completed the same day (§3).
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
| T14 | `Includes [null]` on nullable column | explicit-null rows; pin absent-row behaviour as measured |
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
