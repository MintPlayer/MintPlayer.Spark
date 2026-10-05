# Plan — Sorting and filtering a SparkQuery on a `DateTimeOffset` column

**PRD:** [datetimeoffset_query_sort_filter_PRD.md](datetimeoffset_query_sort_filter_PRD.md)
**Branch:** `test/dto-query-sort-filter` off `master` (`ca068ea1`)
**One PR.** Tests, the defects they expose (F1–F6), the E2E rewrite and the ng-spark fix land together.

## Status

| Milestone | State |
|---|---|
| SP1–SP4 spikes | ✅ 2026-10-05: results in PRD §3; F3 confirmed as a 500, D11 decided |
| M1 test infrastructure + JSON fixture | ✅ `5116b09a` |
| M2 write all server tests first (expected red recorded) | ✅ baseline below |
| M3 F1 importer fix | ✅ `5116b09a` |
| M4 F2 shared DTO parse helper (`WireDateTimeOffset`) | ✅ `5116b09a` |
| M5 F3 `ApplySorting` ordering flag | ✅ `5116b09a` |
| M6 F5 reject unknown sort direction (endpoint 400 + `--spark-verify-model`); F6 no change | ✅ `5116b09a` |
| M7 F4 ng-spark streaming comparator, 22.28.1 | ✅ `5116b09a`, specs red→green measured |
| M8 E2E rewrite + HTTP filter + multi-column test | ✅ `5116b09a` (runs in the M9 sweep) |
| M9 full sweep, docs, memory | ⏳ sweep running |
| #270 investigation (3 agents + SP-270 measured) | ✅ 2026-10-05 → PRD §9 |
| SP5–SP7 | ✅ 2026-10-05 → PRD §9.1; D15 = widen SPARK006 (Warning), D16 shapes from SP6 |
| M9 sweep | ⏹ stopped by the owner mid-run (builds + ng-spark green); folded into the M14 sweep |
| M10 narrow SPARK005 (strings only; Exact → `{F}Sort`) | ✅ `456e593d` (T33 red 4 → green) |
| M11–M12 code fix for SPARK005 and SPARK006; SPARK006 widened | ✅ `3fc64b81`. 40/40. Solution build clean, and the clean result was falsified by unmapping CodeCoverage's `DateRaw` |
| M13 runtime tests T37/T38 | ✅ `bd0675a9` (3/3) |
| M14 docs, versions (preview.98, nupkg has no Workspaces) | ✅ committed with this table |
| M14 full sweep | ⏳ |
| M14 IDE light-bulb check (owner, by hand) | ⏳ Comment out `Commits_ByRepository.cs:125` (`DateRaw = …`), expect the SPARK006 squiggle on `Date` and the "Map 'DateRaw' in the index" light bulb, apply it, then `git checkout` the file. No demo-app changes (PRD D20) |
| Release notes | ✅ `docs/release-notes-preview-98.md` |
| PR | ✅ #487 |
| Sweep (wired caveat: run with `--skip-remote-cache`; the first run lost every .NET test to remote-cache `504`s) | Spark.Tests 3823/3824, CodeCoverage 1107/1108, E2E 150/155, SourceGenerators / Client / ng-spark green. CI on #487: everything green except the one below. The two other local failures are this machine under load: nx's plugin worker died while building Fleet (CrossModuleSync ×5), and an auto-index creation took over 15 s (UploadActionWindowsPaths). Both are green on CI |
| Found by CI, fixed here | `ModelSynchronizerDescriptionTests.Second_sync_pass_is_byte_identical…` assumed no `"$schema"` line. The schema revision comes from the nearest `schemas/v*` git tag (0 = none written). Master's CI built before the deploy created `schemas/v1` and passed; every later build stamps the line. So master is latently red, not because of this branch. The test now expects what the build's revision writes, verified at revision 1 and with `-p:SparkSchemaRevision=0` |

## Baseline — red/green before any fix (measured 2026-10-05, one targeted run)

`RegCarQuerySortFilterTests`, `RegCarLocalZoneFilterTests`, `JsonFixtureImporterTests`,
`ExecuteQueryRequestValidationTests`, all against the unfixed code: **27 passed, 19 failed** (32.7 s).

| Red | Cause | Fixed by |
|---|---|---|
| T0, T8, importer test ×5 (all but the `Z` case) | F1: the stored value was the machine-local clock with no offset | M3 |
| T11, T12, T13, T18 | F1 as well, not a filter defect. The corrupted documents stored `"…T15:00:00.0000000"` with no offset, and RavenDB indexes that text as written, so a 15:00Z filter matched B (Brussels 15:00 = 14:00Z) | M3 |
| T15, T16 | F2 (offset-less value read as Brussels time under the simulated zone) | M4 |
| T22, T22b | F3: `IndexOutOfRangeException`, as SP1 predicted | M5 |
| direction 400 ×3 | F5 | M6 |
| T14 | **wrong expectation, not a defect.** A null filter cannot select an absent field: that limitation is already measured and pinned by `AbsentVersusNullFieldTests`. T14 now pins B and E only, and cross-references that test | test corrected |

Green as expected: T1–T7, T9, T10, T17, T20, T21, both accepted-direction cases, every existing
validation test, and the `Z` importer case.

ng-spark (M7), run with the comparator change stashed: the three new specs were red and produced the
text order (`c/10, c/100, c/9`, and so on). With the change: 118/118 green. T31 was green both
times: the grid already passed every sort column through.

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
- Fixture `@metadata` on every row: `"@collection": "RegCars"`, `"Raven-Clr-Type":
  "MintPlayer.Spark.Tests.Services.RegCarQuerySortFilterTests+RegCar, MintPlayer.Spark.Tests"` (SP2).
- The entity definition has **no** `IndexName`: the index binding goes on the `Registrations` query only,
  so `RegistrationsUnindexed` really runs on an auto index (SP2).
- `tests/MintPlayer.Spark.Tests/_Infrastructure/LocalZone.cs`: the `LocalZone` collection definition
  (`DisableParallelization = true`), the `LocalZone : IDisposable` that sets and restores `TZ` plus
  `TimeZoneInfo.ClearCachedData()`, and `RequireNonUtc(instant)`, which throws `XunitException`
  (PRD D11, code shape from SP3).
- `RegCarLocalZoneFilterTests.cs` (same folder): its own `SharedSparkHost` over the same fixture, in the
  `LocalZone` collection, holding T15.

Verify by compiling only (`dotnet build` of the test project, output redirected to a log).

## M2 — All server tests, written before any fix

Write T0–T18 (index path), T19 (direction, written against the D5 recommendation), T20 (unindexed),
T21–T22b (custom queries; add two custom-query methods on `RegContext` returning `IEnumerable` and
`list.AsQueryable()`; for T22 the refused first column is a model attribute not shown on the query,
and for T22b a declared sort starts with an array attribute — both measured to 500 today, SP1), T23 (`JsonFixtureImporter` unit test, in the same project), T24 (helper —
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
- npm version: bump `libs/node_packages/ng-spark/package.json` **22.28.0 → 22.28.1**. It is a patch,
  and the major stays on the Angular major. CI auto-publishes on merge and skips an already-published
  version (SP4).
- T31 in `spark-query-grid.component.spec.ts`: simulate the datatable's sort change (header click then
  shift-clicks) and assert the `executeQuery` body's `sortColumns`. T31b in the query-list spec.
- Type switch: `datetime`/`date` → `parseWireDate(v)?.getTime()`; `number`/`decimal` → numeric;
  anything else → today's `localeCompare`. Test inputs from SP4: `'2027-03-01T09:00:00-05:00'` vs
  `'2027-03-01T09:00:00.5-05:00'` vs `'2027-03-01T15:00:00+00:00'`, and `9` vs `10`.

## M8 — E2E

- `tests/MintPlayer.Spark.E2E.Tests/Mapper/DateTimeOffsetRoundTripTests.cs:112`: create the three
  cars as today, then **one** `client.ExecuteQueryAsync("registrations", search: prefix,
  sortColumns: [new SortColumn { Property = "RegisteredAt", Direction = "asc" }])` and assert the
  plate order of that page equals instant order; keep the not-wall-clock guard; add the `desc` case.
  Delete the misleading "Ordering was never broken" comment and say what is now measured.
- T27: the same three cars, `columns: [new QueryColumnFilter { Name = "RegisteredAt", Includes =
  [<another offset spelling of car 1>] }]`, scoped by the prefix search → exactly car 1.
- T32: three cars sharing one `Brand` value (unique per test) plus one with another brand; sort
  `[Brand asc, RegisteredAt asc]` (and `RegisteredAt desc`); assert group order, then instant order
  inside the group.
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
- PR body: one PR, listing F1–F6 and the tests that prove each, plus the #270 work (M10–M14), and
  `Closes #270`.

---

## Issue #270 — code fix for hand-written indexes (PRD §9)

Same PR. The spikes come first; each writes its measured result into PRD §9.1. The test runs are
batched into a second full sweep at M14 (the M9 sweep covers F1–F5 only).

### SP5 — Runtime effect of an unmapped companion (dev RavenDB, localhost:8080)
- Hand-written index plus a partial `[FromIndex]` projection with `RegisteredAtRaw` declared but not
  assigned in the map; run it through the real executor (the throwaway-test approach of SP2).
- Measure:
  - Every `RegisteredAt` comes back `+00:00`?
  - Is `ProjectedOffsetRestorer`'s warning silent, because the property exists?
  - The same for an unmapped `ModelSearch`: does search return nothing?
- **Decides** T37's assertions and whether D15 is a warning or an error.

### SP6 — Which map shapes the D16 fix can edit safely
Using the SourceGenerators test harness, build fixtures for:
- query syntax `select new { … }`
- query syntax `select new V { … }`
- method syntax `.Select(x => new { … })`
- `let` clauses
- a ternary in the initializer
- a helper-method projection
- `AddMap` multi-map
- map + reduce

For each, decide whether the target initializer is located unambiguously and whether the edit compiles.
- **Output:** the supported list for D16, recorded in PRD §9.3.
- Unsupported shapes get the diagnostic with no code action (T36).

### SP7 — Diagnostic id and SPARK006 overlap
- Find the next free `SPARKnnn` id. Check `AnalyzerReleases.Unshipped.md`, if the repo tracks it, and
  every `Rules.cs`.
- Decide whether D15 **replaces** SPARK006 for the `Search`/`Raw` suffixes or sits beside it. Two
  warnings for one cause is the outcome to avoid.
- Record the choice as a decision in PRD §9.3.

### M10 — Narrow SPARK005 (D13)
- `SortCompanionAnalyzer.cs:201-252`: fire only for `FieldIndexing.Search` on a `string` property.
  Drop `Exact`, and drop every `DateTimeOffset`.
- Update the rule's message and description to name `{Name}Search`.
- T33, written first, red before the change.

### M11 — SPARK005 code fix (D12, D14)
- `MintPlayer.Spark.SourceGenerators.csproj`: add `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0
  `PrivateAssets="all" ExcludeAssets="runtime"`, with the explanatory comment copied from
  `MintPlayer.Spark.LibraryGenerators.csproj:31-44`.
- `MintPlayer.Spark.SourceGenerators/CodeFixes/SortCompanionCodeFixProvider.cs`: a solution-level
  edit that adds the property, adds the map assignment, and retargets `Index`. Reuse the
  syntax-editing approach of `LibraryGenerators/CodeFixes/ValueObjectSyntaxEditor.cs`.
- T34 via `CodeFixHarness.RunAnalyzerFixAsync`.
- Confirm that the SourceGenerators package still packs only what it should (inspect the `.nupkg`
  contents: Workspaces must not be inside it).

### M12 — Widen SPARK006 + its fix (D15, D16)
- `SortCompanionAnalyzer`: SPARK006 covers every `{F}Search` and `{F}Raw` on a hand-written index's
  projection, generated or hand-written, not only the fields found by the hand-written `Index(...)`
  scan.
  - Report location: `F`; fallback to a hand-written companion, then the projection type (SP7).
  - Exempt indexes deriving from `SparkIndexCreationTask<T>`.
  - Retitle "Sort companion" → "Index companion" and mention `{F}Raw` in the message.
- Fix provider: map assignment insertion for exactly SP6's supported shapes. Port the prototype from
  `scratchpad/sp6/MapShapeSpikeTests.cs`. Refused shapes get no code action.
- T35, T36.
- Docs: `docs/diagnostics.md:31`, `docs/guide-queries-and-sorting.md:607,613`, the
  `release-notes-preview-53.md:111,124` entries (with the moved location noted), and the comment at
  `GenerateIndexGenerator.HandWrittenProducer.cs:145`.

### M13 — Runtime tests (T37, T38)
- In `RegCarQuerySortFilterTests`' folder: an unmapped-Raw index variant (T37) and an Exact-indexed
  variant (T38). Each gets its own `SharedSparkHost` and its own index names, so they never collide
  with `RegCars_Overview`.

### M14 — Docs, version, IDE check, second sweep
- Stale-doc corrections (PRD D17):
  - `docs/issue_210_PRD.md` R24/R26: add a dated superseded note, keeping the old text
    ([[feedback-document-decisions-with-evidence]]);
  - `release-notes-preview-53.md`;
  - `docs/issue_298_…PRD.md`;
  - `docs/issue_272_…PRD.md`.
- Bump the `MintPlayer.Spark.SourceGenerators` minor version (D19). Also check whether
  MintPlayer.Spark.AllFeatures repacks it and needs the same bump.
- **IDE check (owner, by hand, D18):** open a scratch consumer in Visual Studio with a hand-written
  index and confirm both light bulbs appear and apply cleanly. The automated harness cannot prove that
  a fix is *offered*.
- Second sweep: `npm run test:affected` (SourceGenerators.Tests, Spark.Tests).
- Update PRD §9 with every red→green result. Update memory notes [[project-analyzer-code-fixes]] and
  [[reference-codefix-needs-own-assembly]]: the trap is fixed in the harness, and fixes live in either
  generator assembly (owner).
