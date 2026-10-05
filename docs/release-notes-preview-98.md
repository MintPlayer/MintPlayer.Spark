# Spark 11.0.0-preview.98 — `DateTimeOffset` sorts and filters correctly, and hand-written index companions are guarded (#270)

**Packages:** `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.SourceGenerators`,
`MintPlayer.Spark.Testing` and `MintPlayer.Spark.AllFeatures` → `11.0.0-preview.98`. npm:
`@mintplayer/ng-spark` → `22.28.1`. No other package changed. The majors do not move: the packages
still target .NET 11 and Angular 22.

Nothing proved that a query sorts or filters correctly on a `DateTimeOffset` column, and the one E2E
test named for it never sent a sort column. This release adds that proof, which is about 50 tests
across the server, E2E and ng-spark, and fixes what it found. It also delivers #270's code fix,
rescoped to where it matters: hand-written RavenDB indexes.

Decisions and evidence: `docs/datetimeoffset_query_sort_filter_PRD.md` (§3 measurements, §4 defects,
§9 for #270) and `docs/datetimeoffset_query_sort_filter_plan.md` (red-before-green baselines).

---

## ⚠️ Breaking changes

### 1. An unknown sort direction is a 400

`POST /spark/queries/execute` refuses a `sortColumns[].direction` other than `asc` or `desc`, in any
case. It answers `400` with `Unknown sort direction '<value>' for <property>; expected 'asc' or 'desc'.`
Before, anything else sorted **ascending without a word**, so a caller asking for "newest first"
silently got the oldest first.

ng-spark and `SparkClient` already send only `asc`/`desc`. A hand-written caller that sends
`"descending"`, `"dsc"` or an empty string must be fixed.

`--spark-verify-model` now also rejects a model's declared `sortColumns` with another spelling,
because those never pass through the endpoint check.

### 2. A filter value without an offset means UTC

A `QueryColumnFilter` value like `"2027-03-01T15:00:00"`, which has no offset, is now the UTC
instant, the same reading a save always used. Before, the filter path read it as **server-local**
time, so the same string named two different instants depending on where the server ran and on DST.
Values with an offset or `Z`, which is what ng-spark sends, are unaffected.

### 3. SPARK006 reports on the base field

The widened SPARK006 (below) reports on the field the companion belongs to (`Date` for `DateRaw`),
not on the companion property. A `#pragma warning disable SPARK006` placed around a companion
property must move.

---

## Fixed

- **`JsonFixtureImporter` (`SeedFromJsonAsync`) kept the instant but lost the offset.** It let
  Newtonsoft parse ISO dates, which converted them to the machine's local clock and dropped the
  offset: `"…09:00-05:00"` was stored as `"…15:00:00.0000000"` on a Brussels machine. Fixture strings
  are now stored verbatim.
- **A refused first sort column could return a 500.** If the first sort column was refused (not on
  the query surface, `canSort: false`, or a collection), the next column was applied as `ThenBy` on
  an unordered sequence. On a custom query returning an in-memory `IQueryable` that threw. Ordering
  now starts at the first accepted column.
- **ng-spark's streaming grid sorted dates and numbers as text.** For streaming queries the browser
  sorts the rows itself, and it compared raw wire strings, so dates in different offsets sorted by
  wall clock and `10` came before `9`. Dates now sort by instant and numbers numerically.

## Analyzers and code fixes (#270, hand-written indexes)

| | Before | Now |
|---|---|---|
| `SPARK005` | fired for any `Search`/`Exact` field, including `DateTimeOffset`, and always prescribed `{Name}Search` | **strings only**. `Search` → add `{Name}Search`; `Exact` → add `{Name}Sort`, the plain copy sorting reads. A `DateTimeOffset` orders by instant under every indexing (measured on Corax and Lucene), so it needs no companion. A field that *is* the `{Name}Search` copy is no longer flagged |
| `SPARK006` | only companions found by scanning hand-written `Index(...)` calls, reported on the companion, so a generated companion was never reported | every `{Name}Search`, `{Name}Sort` and `{Name}Raw` companion, hand-written or generated, that the map never assigns. A companion named only in `Index(..., No)` counts as unassigned |
| Code fixes | none | ✅ both, in `MintPlayer.Spark.SourceGenerators`: move search to a mapped `{Name}Search`; add a mapped `{Name}Sort`; map an existing companion, using the generator's exact text |

Why SPARK006 matters: an unmapped `{Name}Raw` returns every `DateTimeOffset` at offset `+00:00` in
the grid while the detail page shows the real one, and an unmapped `{Name}Search` makes search find
nothing. Both happen with a healthy index, correct sorting and no warning anywhere (measured).

The fixes edit only map shapes proven safe: query or `.Select` projections, `let`, ternaries,
implicit members, `into`, and multi-map. They offer nothing for map/reduce, helper projections and
other shapes, where the warning stays. Code fixes are IDE-only; `dotnet build` is unaffected.

#270's premise that a code fix needs its own assembly is retired. The old failure was the generator
test harness lacking `Microsoft.CodeAnalysis.Workspaces`, not a problem in consumers' builds.

## Testing

- `MintPlayer.Spark.Tests`: `RegCarQuerySortFilterTests`, a JSON-seeded fixture over Fleet's
  `Cars_Overview` shape whose instant order differs from its wall-clock order. It covers sorting on
  every execution path, multi-column sorts, paging, includes, excludes, null and distinct values.
- **Timezone simulation inside `dotnet test`:** `_Infrastructure/LocalZone.cs` sets `TZ` and calls
  `TimeZoneInfo.ClearCachedData()` in a non-parallel collection, so server-local bugs reproduce on
  UTC CI. It fails rather than skips when the zone stays UTC (Windows ignores `TZ`).
- E2E: the Fleet sort test really sorts now, plus an HTTP filter test and a two-column sort test.

## Documentation

`docs/diagnostics.md` (SPARK005/006 rows and code fixes), `docs/guide-queries-and-sorting.md`
(unmapped `{Name}Raw`, and `DateTimeOffset` never needs a sort companion), and
`MintPlayer.Spark.Testing/AGENTS.md` (fixtures store dates verbatim; `Raven-Clr-Type` for nested
types). Superseded notes were added where older PRDs claimed a code fix needs its own assembly.
