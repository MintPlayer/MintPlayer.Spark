# Plan — Sorting and filtering a SparkQuery on a `DateTimeOffset` column

**PRD:** [datetimeoffset_query_sort_filter_PRD.md](datetimeoffset_query_sort_filter_PRD.md)
**Branch:** `test/dto-query-sort-filter` off `master` (`ca068ea1`)
**One PR.** Tests, the defects they expose (F1–F6), the E2E rewrite and the ng-spark fix land together.

## Status

| Milestone | State |
|---|---|
| SP1–SP4 spikes | ⏳ |
| M1 test infrastructure + JSON fixture | ⏳ |
| M2 write all server tests first (expected red recorded) | ⏳ |
| M3 F1 importer fix | ⏳ |
| M4 F2 shared DTO parse helper | ⏳ |
| M5 F3 `ApplySorting` ordering flag | ⏳ |
| M6 F5 reject unknown sort direction (F6: no change) | ⏳ |
| M7 F4 ng-spark streaming comparator | ⏳ |
| M8 E2E rewrite + HTTP filter test | ⏳ |
| M9 full sweep, docs, memory | ⏳ |

Test-run discipline: **no suite runs per milestone.** One targeted run of the new test classes
after M2 records the red baseline (the evidence that each fix is needed). Then one sweep at M9
(`RAVENDB_LICENSE=… npm run test:affected`). Never push to get a CI run.

---

## Spikes (run first; each writes its measured result into PRD §3)

All spikes work in the session scratchpad, against `localhost:8080` where a database is needed
(databases prefixed `spike-`, deleted afterwards). Nothing in the repo changes during a spike.

### SP1 — Does F3 throw on an in-memory `IQueryable`?
- Console spike: `new[]{…}.AsQueryable()`, then invoke `Queryable.ThenBy` through `MethodInfo.Invoke`
  exactly as `QueryExecutor.cs:1894-1896` does, without a prior `OrderBy`. Enumerate.
- Same against a Raven `IRavenQueryable` (dev server): does `ThenBy`-first behave as `OrderBy`?
- **Decides:** whether T22 is red (throws / wrong order) or merely pins harmless behaviour. D3 is
  done either way, since it costs one line.

### SP2 — `SharedSparkHost` + `JsonFixtureImporter` with a nested test type
- Confirm the `Raven-Clr-Type` spelling for a nested type
  (`MintPlayer.Spark.Tests.Services.RegCarQuerySortFilterTests+RegCar, MintPlayer.Spark.Tests`)
  loads typed, and that deploying `RegCars_Overview` **before** import plus the importer's own
  non-stale wait leaves the index ready for the first test.
- Confirm whether the importer can be called from `SharedSparkHost.BeforeHostAsync` without
  adding a helper (it has no `SeedFromJsonAsync`).
- Run with the F1 fix applied in a scratch copy, otherwise the fixture is corrupt.

### SP3 — Simulating a non-UTC zone inside `dotnet test` (PRD §8.3, D11)
CI is `ubuntu-latest` (UTC); F2 cannot reproduce there unless the test changes the zone. Measure
on Linux (WSL or a `mcr.microsoft.com/dotnet/sdk` container) **and** on Windows:
- (a) **In-process, preferred.** `Environment.SetEnvironmentVariable("TZ", "Europe/Brussels")` +
  `TimeZoneInfo.ClearCachedData()` then read `TimeZoneInfo.Local` and run the STJ
  `Deserialize<DateTimeOffset>("\"2027-03-01T09:00:00\"")` from SP-B. Does the offset become +01:00?
  Restore `TZ` + `ClearCachedData()` in `Dispose`. Run inside an xUnit collection with
  `DisableParallelization = true` so no other class observes the changed zone. Confirm with a
  deliberately parallel neighbour test that it really is isolated.
- (b) **Child process.** `Microsoft.DotNet.RemoteExecutor` (check that a usable version is on
  nuget.org, not only the dotnet-eng feed) or a small `dotnet exec` helper with `TZ` in
  `StartInfo.Environment`. Heavier; it is the fallback if (a) leaks across classes.
- (c) **Whole run** via `.runsettings` `<EnvironmentVariables><TZ>`. This changes the zone for
  3500+ tests at once: rejected unless (a) and (b) both fail. The side effect, every test running
  non-UTC on CI, may be desirable, but it belongs in a separate decision.
- **Windows:** `TZ` is ignored, so a Windows dev box in UTC would pass vacuously. Every variant
  includes the guard from PRD §8.3: the test **fails** when `TimeZoneInfo.Local` has a zero offset at
  the test instant. Measure that the guard fires (set the Windows zone to UTC once, or simulate the
  guard with a forced zero).
- Record the chosen variant and measured output as D11 in PRD §5. Apply it to T15 (integration)
  and T24 (helper unit test).

### SP4 — Real streaming wire shape for a datetime cell
- Read `StreamExecuteQuery.cs:43-46` serialisation and capture (or construct faithfully) the exact
  strings a streamed `DateTimeOffset` produces, including trimmed fractions
  (`"2027-03-01T09:00:00-05:00"` vs `"…09:00:00.5-05:00"`), and the column `dataType` names used
  for dates and numbers (`datetime`, `date`, `number`, `decimal`, …).
- **Decides:** the inputs for T28/T29 and the type switch in D4.

---

## M1 — Test infrastructure and JSON fixture

Files:
- `tests/MintPlayer.Spark.Tests/Services/RegCarQuerySortFilterTests.cs`
  - nested `RegCar { Id, LicensePlate, DateTimeOffset RegisteredAt, DateTimeOffset? DeregisteredAt }`
  - `RegCars_Overview` mirroring the generated `Cars_Overview` shape: plain fields plus
    `RegisteredAtRaw`/`DeregisteredAtRaw = new SparkIndexValue<…>{ V = … }`,
    `Index(raw, FieldIndexing.No)`, `StoreAllFields(FieldStorage.Yes)` — copy the pattern from
    `Services/DateTimeOffsetRoundTripTests.cs:76-116`
  - `[FromIndex]` `VRegCar` with `[IgnoreProperty]` raw wrappers
  - `RegContext : SparkContext` exposing `RegCars`
  - `SeededRegCars : SharedSparkHost<RegContext>`: deploy index, import fixture, register index and
    projection in the catalog
  - model built in code (`EntityTypeFile`) mirroring `Car.json` (`RegisteredAt` `datetime`), with two
    queries: `Registrations` (index-bound, declared sort `RegisteredAt desc`) and
    `RegistrationsUnindexed` (`Database.RegCars`, no index — T20)
- `tests/MintPlayer.Spark.Tests/Services/Data/reg-cars.json` — rows A–G from PRD §3, plus
  `DeregisteredAt` (mixed offsets; explicit `null` on B and E; **absent** on G), plates chosen so
  plate order ≠ instant order. Every date string written with an explicit offset or `Z`, 7-digit
  fraction, exactly as the client writes.
- `MintPlayer.Spark.Tests.csproj` — `<Content Include="Services\Data\*.json" CopyToOutputDirectory="PreserveNewest" />`.

Verify by compiling only (`dotnet build` of the test project, output redirected to a log).

## M2 — All server tests, written before any fix

Write T0–T18 (index path), T19 (direction, written against the D5 recommendation), T20 (unindexed),
T21–T22 (custom queries; add two custom-query methods on `RegContext` returning `IEnumerable` and
in-memory `IQueryable`), T23 (`JsonFixtureImporter` unit test, in the same project), T24 (helper —
compile-stubbed until M4), plus T15/T24 inside the SP3 zone-simulation collection.

Assertion rules (PRD D9/D10): order as a list of plates; offsets via `EqualsExact`; each sort test
has a "not also wall-clock/ordinal ordered" guard; no-value rows asserted as a group only.

**One targeted run** of `RegCarQuerySortFilterTests` and the importer test class, output to a log in
the scratchpad. Record in the plan, per test, red or green and the failure message. Expected red:
T0/T23 (F1), T15/T24 (F2, under the simulated zone), T22 (F3, if SP1 confirms), T19 (F5).
Every other test is expected green and pins existing behaviour. **A test expected green that comes
out red is a new finding: stop and investigate, do not adjust the assertion.**

## M3 — F1: `JsonFixtureImporter` keeps strings verbatim (D1)

- `libs/testing/MintPlayer.Spark.Testing/JsonFixtureImporter.cs:38`: replace `JObject.Parse(content)`
  with a `JsonTextReader { DateParseHandling = DateParseHandling.None }` + `JObject.Load(reader)`
  (diff measured in SP-B).
- Existing importer consumers (grepped 2026-10-05): `Issue467DeleteManyGateTests` (`notes.json`),
  `JsonSeededReflectionCacheTests` (`people.json`), and CodeCoverage's `ActionDogfoodHarness.cs:102`
  (`Fixtures/Dogfood/dogfood.json`). The only date among them is dogfood's
  `"CreatedAtUtc": "…T00:00:00.0000000Z"`, and a `Z` value is stored identically before and after the
  fix (SP-B), so no expectation changes. CodeCoverage.Tests is affected and runs in the M9 sweep.
- Update the importer's XML docs and `MintPlayer.Spark.Testing` `AGENTS.md` (the fixture-format
  section, `AGENTS.md:315-317`) to state that dates are stored verbatim.

## M4 — F2: one DTO parse contract for writes and filters (D2)

- Extract the `EntityMapper.cs:1149-1160` parse (`DateTimeOffset.Parse(s, InvariantCulture,
  AssumeUniversal)` and its `DateTime` sibling, if any) into one internal helper.
- `QueryExecutor.TryConvertFilterValue` (`:2451-2491`): for `DateTimeOffset`/`DateTimeOffset?`
  targets with a JSON **string** element, use the helper instead of `JsonSerializer.Deserialize`.
  Non-string elements and other types unchanged.
- Check whether `DateTime` filters have the same local-vs-UTC split (measure with T24 inputs);
  if they do, fix in the same helper — same bug class.
- Apply SP3's decision for falsifiability.

## M5 — F3: `ApplySorting` orders on "already ordered", not `i == 0` (D3)

- `QueryExecutor.cs:1854-1896`: track `ordered` (bool) like `SortMappedRows` (`:1499-1521`).

## M6 — F5: reject an unknown sort direction (D5, owner-decided)

- `Endpoints/Queries/Execute.cs:100-134` returns 400, with the same problem shape as an unknown sort
  property, for any direction other than `asc`/`desc` (case-insensitive).
- Check every caller first and fix any that sends another spelling:
  - ng-spark `spark.service.ts:159-161` (maps to `'asc'|'desc'`)
  - `SparkClient` (`SortColumn.Direction` defaults to `"asc"`)
  - the streaming endpoint, if it accepts sort input
  - every test and E2E that builds a `SortColumn` (grep `Direction =`)
  - model JSON `sortColumns` in `apps/**/App_Data/Model/*.json`
- **Declared** sorts never pass through this endpoint check. They must be validated at model load,
  or the same silent-ascending path survives for model authors. Check where model `sortColumns`
  are read and reject or warn there too.
- F6: **no change** (D6).

## M7 — F4: type-aware streaming comparator (D4)

- `libs/node_packages/ng-spark/query-list/src/spark-query-list.component.ts:440-446`: look up the
  column's `dataType` in `streamColumns`; `datetime`/`date` → compare `parseWireDate(v).getTime()`;
  numeric types → numeric compare; otherwise the current `localeCompare`. Keep null/empty
  placement exactly as the specs at `:312,329` pin it.
- Specs T28–T29 in `spark-query-list.component.spec.ts` using the existing `setup()` / `live()` /
  `settle()` helpers (`:58-85`, `:181-185`, `:301-335`); inputs from SP4. Run the new specs once
  before the change to record red (`nx run @mintplayer/ng-spark:test -c local`). No app host is
  running, so this does not collide with a live dev server.
- npm version: a fix is a **patch** bump; the major stays on the Angular major (CLAUDE.md
  versioning). Check whether CI publishes on merge, and bump accordingly.

## M8 — E2E

- `tests/MintPlayer.Spark.E2E.Tests/Mapper/DateTimeOffsetRoundTripTests.cs:112`: create the three
  cars as today, then **one** `client.ExecuteQueryAsync("registrations", search: prefix,
  sortColumns: [new SortColumn { Property = "RegisteredAt", Direction = "asc" }])` and assert the
  plate order of that page equals instant order; keep the not-wall-clock guard; add the `desc` case.
  Delete the misleading "Ordering was never broken" comment and say what is now measured.
- T27: the same three cars, `columns: [new QueryColumnFilter { Name = "RegisteredAt", Includes =
  [<another offset spelling of car 1>] }]`, scoped by the prefix search → exactly car 1.
- Confirm the plate-prefix search narrows to just the test's rows (search pushdown on
  `LicensePlate`) so other tests' cars sharing the collection cannot interleave.

## M9 — Sweep, docs, memory

- `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`,
  in the background, output raw to a scratchpad log. Read failures from the log; re-run only the
  failing class while fixing.
- Update PRD §3/§4 with every red→green result (test name, failure message before, commit after),
  and mark this plan's status table.
- Memory: update `reference_raven_datetimeoffset_index_projection.md` with SP-A (ordering, ties,
  null vs absent per engine), and add one entry for F1 (importer) and F2 (filter vs write parse).
- PR body: one PR, listing F1–F6 and the tests that prove each.
