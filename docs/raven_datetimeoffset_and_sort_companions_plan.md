# Plan — `DateTimeOffset` fidelity (read + write) and `*Sort` companion correction

**PRD:** [raven_datetimeoffset_and_sort_companions_PRD.md](raven_datetimeoffset_and_sort_companions_PRD.md)
**Status:** **IMPLEMENTED** (M1–M10); **M11 — a demo-app demonstration — in progress.**
Suite green: `MintPlayer.Spark.Tests` 2134/2134, `CodeCoverage.Tests` 438/438,
`SourceGenerators` 278/278, `Client` 38/38.
**Branch:** `fix/datetimeoffset-fidelity`.
**Issues:** none — the issue owner chose to implement directly; the PR references this PRD instead.

## Decisions taken (issue owner)

| Decision | Choice |
|---|---|
| Offset on save | **Client sends a full ISO-8601 string carrying the offset.** The server never infers or re-applies one. See M5. |
| Wrapper type name | **`SparkIndexValue<T>`** in `MintPlayer.Spark.Abstractions` — namespaced and self-describing, since it appears in generated code consumers read. |
| Missing-wrapper diagnostic | **Error, with an opt-out** (`<NoWarn>$(NoWarn);SPARK018</NoWarn>`). |
| Workflow | **No GitHub issues** — branch and implement directly. |
| `AdditionalSources` | **Out of scope.** Not to be re-proposed; see the PRD for why generic embedding still cannot pay off. |

One PR, per the repo's single-PR rule: generator, runtime, analyzer, write path, dead-code removal,
CodeCoverage and docs land together. The CodeCoverage change is a different app but the same
repository and the same defect — it does not get its own PR.

---

## Pre-work — done, uncommitted

`RavenDB.Client` 7.2.5 → **7.2.6** in 8 libs, `RavenDB.TestDriver` 7.2.5 → **7.2.6** in
`MintPlayer.Spark.Testing`. `MintPlayer.Spark` builds clean.

**The bump fixes nothing** — measured identical across clients 7.1.12/7.2.5/7.2.6. Keep it as currency
(it matches the installed server and Vidyano's net10 pin) or drop it; not load-bearing. Decide before
opening the PR so the diff says what it means.

`apps/CodeCoverage/CodeCoverage.Tests` stays on `RavenDB.TestDriver` **7.2.1** — documented deliberate
skew. **Open decision (M6):** bumping it puts CodeCoverage's 435 tests on the same embedded server as
everything else, arguably right since it is the app holding `DateTimeOffset` data.

---

## Spikes — RESOLVED (see PRD for evidence)

| Spike | Verdict | Effect on this plan |
|---|---|---|
| **S1** `FieldIndexing.Exact` on `DateTimeOffset` | **DROP it.** 62 measurements, zero behavioural differences; the index term is the same UTC-normalised string in both modes. The premise was false — both modes match by instant. | M3 also removes the `Exact` arm. Same release, same rebuild. |
| **S2** mixed `DateTimeKind` ordering | **No inversion.** RavenDB re-serialises every date at index time to a fixed-width 7-digit fraction, so `Z` can only break ties. A ticks companion is measurably worse — and can take the whole index to `state=Error`. | **No `DateTime` companion.** Release scope does not grow. |
| **S3** production at coverage.mintplayer.com | **Not corrupted, no migration.** `Commit` is the only entity with a `DateTimeOffset` and the only one without a generated index, so the `StoreAllFields` trigger never meets the type. Three writes, all from a clock or a webhook. | M6 loses the migration; gains a guard test. |

Harnesses: `scratchpad/spike-s1/`, `scratchpad/spike-s2/`, `scratchpad/spike-s3/`.

---

## No backward-compatibility requirement

Confirmed by the issue owner: **the framework has no external consumers yet**, so any shape may change.

⚠️ One caveat that survives that: **`apps/CodeCoverage` is a live deployment** on coverage.mintplayer.com
built on this framework. S3 established its *data* is not corrupted and needs no migration — but changing
the emitted index shape still triggers a **rebuild of its indexes on deploy**. "No production users" means
no external API consumers, not no running system. Sequence the deploy accordingly (M6).

What this licenses:

- **Remove `{Name}Sort` from `DateTimeOffset` outright** — no alias, no deprecation window, no dual
  lookup in `ResolveSortProperty`.
- **Narrow SPARK005 immediately** and ship the new missing-wrapper diagnostic at **Error** from day one,
  rather than Warning-then-Error across two releases.
- **Keep the wrapper implicit** (triggered by the type, no opt-in attribute). Every existing
  `DateTimeOffset` index changes shape; that is acceptable, and opt-in would leave most consumers broken
  for no benefit.
- **No compatibility shims, no dual-read path, no staged rollout**, and no need to keep the dead code in
  M4 alive for anyone.

What it does **not** license: the tolerant read path in M4. A missing wrapper must still degrade to the
un-restored value rather than throwing or half-restoring — not for compatibility, but because **the
wrapper is genuinely absent while RavenDB rebuilds the index after deploy.** That is a live transient on
every consumer, not a legacy concern.

---

## Milestones

Tests batched to the end (M8), per repo rule. Intermediate milestones verified by reading code,
generator snapshot diffs and type-checking.

### M1 — Red tests first
- `tests/MintPlayer.Spark.Tests/Services/DateTimeOffsetRoundTripTests.cs` (new): store a
  `DateTimeOffset` with a **non-zero** offset, read back through a Spark index query, assert **both**
  wall clock and `.Offset`. Include a **negative** offset (shifts *later* — disproves "always minus two
  hours"), a `TimeSpan.Zero` control that must pass today, and a `DateTimeOffset?` null.
- Assert the same value via `session.Load` (passes today) to pin the asymmetry in the suite.
- **Defect D test:** edit a `DateTimeOffset` attribute through the save pipeline and assert it persists.
  Fails today, silently.
- **Raw-projection assertion** pinning the defect itself, so a future RavenDB that fixes this upstream
  fails loudly rather than leaving dead reconstruction code.
- Extend `SortCompanionRedirectTests.cs` with plain-vs-`{X}Sort` for a non-analyzed field asserting the
  orderings are **identical** — pins the refutation so nobody re-adds companions later.
- Seed offsets that are neither UTC nor the machine's local offset. **Do not** set a process-wide
  timezone; pin it in the data, or a UTC-only CI box passes a wrong implementation.

**Exit:** new tests fail for the documented reasons; `session.Load` and `TimeSpan.Zero` pass.

### M2 — Test-driver parity (prerequisite, not cleanup)
`SparkTestDriver.cs:86-90` and `SparkSharedDatabase.cs:94-96` apply `UseNaturalIds().UseGeneratedIds()`
but never `Conventions.Serialization`, while production does (`SparkMiddleware.cs:84-101`), and
`SparkEndpointFactory.cs:127-129` substitutes the store. Factor the convention block into a public
`ApplySparkConventions(IDocumentStore)` called from the middleware **and** both drivers.

The defect is an indexing conversion, not a serialization one, so this probably does not change the
symptom — but a green test proves nothing about production while the two configurations differ.

**Exit:** one convention path; drivers and middleware provably identical.

### M3 — Generator: nested wrapper carrier
`libs/source_generators/MintPlayer.Spark.SourceGenerators/`

- Generalise `IndexPropertyInfo.IsSortCompanion : bool` (`Models/GeneratedIndexInfo.cs:139-144`) into
  `CompanionKind Companion` + `bool HiddenFromModel`. The boolean already conflates three meanings, and
  the breadcrumb companion is marked `IsSortCompanion` despite carrying a **different value from a
  different path** — `ResolveSortProperty` would redirect an `OrderBy` onto it today. Splitting is a
  latent-bug fix, not decoration.
- One suffix table, the only place a suffix is written (today it is derived in three places:
  `IndexNaming.cs:62`, `SortCompanionAnalyzer.cs:74`, `QueryExecutor.cs:1668`).
- Ship **one generic box** in `MintPlayer.Spark.Abstractions` (measured: class/struct/generic/cross-assembly
  are all erased to the same server-side bytes, so one type covers every case):
  ```csharp
  public sealed class SparkIndexValue<T> { public T V { get; set; } = default!; }
  ```
- Emit on the index entity — `T` matching the source property's declared type **exactly** (a mismatch makes
  Raven inject a cast: `V = ((DateTimeOffset ? ) car.Starts)` — it works, but it is avoidable noise):
  ```csharp
  [MintPlayer.Spark.Abstractions.IgnorePropertyAttribute]
  public MintPlayer.Spark.Abstractions.SparkIndexValue<global::System.DateTimeOffset>? StartsRaw { get; set; }
  ```
  and in the map body:
  ```csharp
  StartsRaw = new MintPlayer.Spark.Abstractions.SparkIndexValue<global::System.DateTimeOffset> { V = car.Starts },
  ```
- ⚠️ **One line per wrapper, after `StoreAllFields`, NOT optional:**
  ```csharp
  Index(x => x.StartsRaw, FieldIndexing.No);
  ```
  Without it Corax deploys the index clean and then sits at `state=Error, entries=0, isInvalid=True`
  (`NotSupportedInCoraxException`). Lucene is unaffected — so this fails only on the engine both CI and
  production actually run.
- **Drop** `{Name}Sort` for `DateTimeOffset`; **keep** it for `[Search]` strings. Emit **nothing** for
  `DateTimeOffset` inside a `[ValueObject]` child or complex property — measured already correct.
- **Drop the `isDateTimeOffset ? "Exact"` arm** at `GenerateIndexGenerator.cs:379` (S1: zero measured
  consequence). The line becomes `isComplex ? "No" : isSearchableText ? "Search" : null`.
- **Emit no `DateTime` companion** (S2). `SparkModelSymbols.cs:70-77` stays as written.
- Update the golden snapshot.

**Why generalise now:** if S2 lands later as a parallel mechanism it is a **second full index rebuild at
every consumer**. With a kind table it is one row, riding the same release. One rebuild, once.

**Exit:** snapshot diff shows exactly the intended field changes and nothing else.

### M4 — Runtime: restore from the wrapper
- Hook in `RowSecurityGate.ApplyAsync` between `kept` (`:188-193`) and `mapped` (`:199`), via a new
  scoped service injected beside `entityMapper` (`:109`). Single architectural choke point; covers index,
  custom/composed and streaming paths; never sees a `session.Load` row.
- ⚠️ **It must run before `ToPersistentObject`** (called at `:199`), because
  `EntityMapper.PopulateAttributeValues` (`EntityMapper.cs:236-258`) resolves properties **by attribute
  name** — the `Starts` attribute reads `VCar.Starts`, the flattened one. Restoring later means fighting a
  name-based lookup. The hook point above already satisfies this; do not move it downstream.
- **Prefer a generator-emitted strongly-typed restore method over reflection.** Measured: `ProjectInto<T>()`
  materialises the box as its **real CLR type** (not a `JObject`), and the property does **not** need to be
  `object`-typed — so the generator can emit a typed restore per index entity, and the runtime calls it.
  No per-row reflection, correctness checked at compile time, and a generated index cannot drift from its
  own restorer. (An `object`-declared wrapper *does* materialise as `JObject` with the offset still intact
  — that is the reflective fallback, not the default.)
- For hand-written index entities, fall back to convention keyed on **`row.GetType()`** — not
  `RowSecurityContext.ResultType`, which is deliberately null for composed types (`QueryExecutor.cs:1066`)
  and may be a base type on the streaming path. Plan cached per runtime type via `ReflectionCache`.
- **Never throw, never half-apply.** Absent wrapper → today's behaviour. Announce once per
  (type, property) at `Information`, following `RowSecurity`'s `announced` pattern (`RowSecurity.cs:306-323`)
  — never per row, never for entity types.
- **Delete** dead `DatabaseAccess.QueryEntitiesWithIncludesAsync` (`:344`) and correct the false
  doc-comment at `DatabaseAccessIntegrationTests.cs:237`. Delete dead `EntityMapper.GetDataType` (`:1161`).

**Exit:** M1's read tests pass; `session.Load` untouched.

### M5 — Write path (Defect D)
- Add a `DateTimeOffset`/`DateTimeOffset?` branch to `EntityMapper.SetPropertyValue` beside `:1116`,
  parsing with `DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)` and
  accepting an already-typed instance (the same-type fast path is measured to succeed).
- ⚠️ **Never pass `AdjustToUniversal`** — measured to flatten `+02:00`/`-08:00`/`+05:45` all to `00:00:00`.
  **`AssumeUniversal` is harmless** (it only applies when the string carries no offset at all, which never
  happens for a round-tripped value) — an earlier draft of this plan wrongly named it as dangerous.
  `RoundtripKind` and `None` both preserve the offset; `RoundtripKind` verified culture-proof on `ar-SA`,
  `th-TH`, `fa-IR`, though the fix should still pass `InvariantCulture`.
- Confirmed by execution, not reading: `Convert.ChangeType("2026-03-09T10:00:00+02:00", typeof(DateTimeOffset))`
  throws `System.InvalidCastException: Invalid cast from 'System.String' to 'System.DateTimeOffset'`, because
  `typeof(DateTimeOffset)` does **not** implement `IConvertible` (`DateTime` does). The asymmetry is real:
  SET is swallowed, CLEAR succeeds via the pre-`try` branch at `:1095-1101`.
- **Client contract (decided): the client sends a full ISO-8601 string carrying the offset.** The server
  does not infer, assume, or re-apply anything — an offset-less value is a client bug, not a server
  decision. This is the explicit option and the only one that keeps the wire self-describing.

  ⚠️ **This adds client-side scope to the PR.** `<input type="datetime-local">` has no offset, so
  `ng-spark` must keep the value's original offset alongside the control and reattach it on submit:
  - `libs/node_packages/ng-spark/pipes/src/input-type.pipe.ts:12-13` maps `"datetime"` to
    `datetime-local` — the control stays, but the surrounding binding must preserve the offset.
  - Editing the wall clock keeps the **record's own** offset, not the browser's.
  - **Decided: a value with no offset is `+00:00`.** Spark does not invent offsets — it preserves ones it
    is given (from a webhook, an import, server-side code) and defaults everything else to UTC. This covers
    a brand-new record and any stored stamp whose offset was already lost.
    ⚠️ Consequence to document, not to fix: the user types `14:30` in a `datetime-local` control and it is
    stored as `14:30+00:00`, so for a browser at `+02:00` the *instant* is two hours from what they meant.
    Ordering and filtering run on the instant, so such a record sorts differently from one that carries a
    real offset. Acceptable given there are no external consumers; revisit if a timezone-aware editor is
    ever added.
  - Server side, an offset-less string should fail loudly rather than be guessed at — consistent with the
    "client sends the offset" contract.
  - npm major tracks the Angular major, so this is a **minor** bump of `@mintplayer/ng-spark`.
- Consider whether the blanket `catch { }` at `:1137` should narrow — it is what made this silent.

**Exit:** M1's write test passes; an unedited round-trip does not rewrite the offset.

### M6 — Analyzers + CodeCoverage
- **SPARK005 narrowed to `Search` only** — drop the `Exact` text-suffix match at `SortCompanionAnalyzer.cs:138`,
  which is how `DateTimeOffset` got swept in. SPARK006 stays Warning for the Sort kind.
- **New diagnostic at `DiagnosticSeverity.Error`** (next clean id — SPARK003/SPARK015 are unused): a
  `[FromIndex]` entity with a `DateTimeOffset` reachable as a **stored scalar index field** and no wrapper.
  Errors run at `dotnet build`; **code fixes do not** and can never be load-bearing.
  **Suppressible** via `<NoWarn>$(NoWarn);SPARK018</NoWarn>` — document the escape hatch in the diagnostic
  message itself, so someone hitting it on a build they did not write knows the way out without a search.
  ⚠️ Scope on **any `FieldStorage.Yes`**, not `StoreAllFields` — per-field `Store(...)` triggers the
  identical defect (measured).
- Code fix writing the map assignment goes in `MintPlayer.Spark.LibraryGenerators/CodeFixes/` beside the
  two that exist, reusing the `Microsoft.CodeAnalysis.CSharp.Workspaces` + `ExcludeAssets="runtime"`
  arrangement. **No new assembly** — a third independently-versioned analyzer package is what breaks VS.
  Register with `RegisterSymbolAction`.
- The analyzer may read CLR types — it already holds `INamedTypeSymbol`/`IPropertySymbol`, and
  `System.DateTimeOffset` always resolves. The documented cross-assembly limit is about *locating* a
  declaration, not reading a type.
- **CodeCoverage needs no code fix** (S3: exposure is structurally empty) and **no migration**. Instead add
  a **guard test** asserting `Commits_ByRepository` declares no stored fields and `Commit` carries no
  `[GenerateIndex]` — today the protection is one missing line, and adding either silently converts zero
  exposure into a live display defect.
- Decide the `CodeCoverage.Tests` TestDriver 7.2.1 bump.

**Exit:** analyzer tests updated; no diagnostic fires on a correct generated pair.

### M7 — CI index-health check *(new, from measurement)*
A bad map expression **does not fail deployment** — `PutIndexesOperation` succeeds and the index then sits
at `state=Error, mapErrors=N, entries=0`. RavenDB binds map members dynamically at runtime, so a
generator-emitted expression is never validated by a build.

Add a CI assertion that every deployed index reaches a healthy state with `mapErrors=0`. Without it, a
generator typo ships silently and every query returns nothing.

### M11 — A working demonstration in a demo app ✅ DONE

**Built in `apps/Fleet`** — the only demo app with both `[GenerateIndex]` (so the wrapper is emitted
automatically, exercising the real path) and a working custom-action triplet to copy. `apps/DemoApp`
was rejected: its indexes are hand-written, which would have demonstrated the *other* half of the fix.
`apps/HR` has no custom-action machinery at all. Not `apps/CodeCoverage` — production.

| Piece | Where |
|---|---|
| The property | `Car.RegisteredAt` (`apps/Fleet/Fleet.Library/Entities/Car.cs`) — no attribute; the type is its own trigger |
| The button | `ScatterRegistrationOffsetsAction` (`apps/Fleet/Fleet/CustomActions/`), `selectionRule: "=0"`, `showedOn: "query"` |
| Registration | `App_Data/customActions.json` + two grants in `App_Data/security.json` (default is deny — no grant, no button, silently) |
| The grid | query `Registrations` in `App_Data/Model/Car.json`, `alias: registrations`, `renderMode: Pagination`, sorted on `RegisteredAt` |
| The menu entry | `App_Data/programUnits.json` |
| **The renderer** | `offset-datetime-column-renderer.component.ts` + registration in `app.config.ts` |

**Verified:** the generator emitted, against the real entity and with nothing hand-written —
```csharp
RegisteredAtRaw = new SparkIndexValue<DateTimeOffset> { V = car.RegisteredAt },
Index(nameof(VCar.RegisteredAtRaw), FieldIndexing.No);
```
`--spark-verify-model` exits 0; the wrapper does **not** appear as a model attribute (`[IgnoreProperty]`
is vetoed by the synchronizer), so no stray column shows up in the grid.

#### ⚠️ The renderer is not decoration — without it the demo shows nothing

The default `datetime` column pipes the value through Angular's `DatePipe` with no timezone argument,
which formats in the **browser's** local zone. Every row would render in *your* offset and the demo
would look exactly like the unfixed bug. A column renderer receives the **raw wire value** rather than
the piped one, so it can print the ISO string verbatim. The component deliberately never constructs a
JS `Date` — `new Date(...)` discards the offset immediately, which is the same loss in the browser that
RavenDB used to inflict in the index.

This generalises beyond the demo: **a correctly-restored `DateTimeOffset` still *displays* shifted**
under the default renderer. That is presentation, not data loss, and it is exactly the kind of thing
that restarts a folklore cycle if nobody writes it down.

#### Running it

Needs RavenDB database `SparkFleet` on `localhost:8080`, and a signed-in user in `Administrators` or
the Fleet-manager group — `CarActions.GetRowFilterAsync` gives an anonymous caller `car => false`, so
an unauthenticated visitor sees an empty grid and no button. Then `dotnet run --project apps/Fleet/Fleet`
(the host spawns the Angular dev server; never run `ng serve` beside it) and open `/query/registrations`.

The button needs `Car` documents to exist first — it stamps, it does not create.

#### ✅ Verified end to end in a browser (2026-09-11)

Ran against RavenDB 7.2.6 and the real Fleet app with 10,010 `Car` documents, driven through Playwright.

**The index deployed healthy — the Corax check that matters:**
`State=Normal`, `MapErrors=0`, `EntriesCount=10010`, `IsStale=false`, and the definition carries
`"RegisteredAtRaw": { "Indexing": "No" }`. Had `FieldIndexing.No` been omitted this would have read
`state=Error, entries=0` after a clean deploy.

**The defect and the fix, side by side in one projection** (`from index 'Cars/Overview' select
RegisteredAt, RegisteredAtRaw`):

| stored in document | scalar field (RavenDB flattens) | wrapper (preserved) |
|---|---|---|
| `13:47+02:00` | `11:47Z` | `13:47+02:00` |
| `18:40-03:30` | `22:10Z` | `18:40-03:30` |
| `12:10+09:30` | `02:40Z` | `12:10+09:30` |

The scalar field is *still* flattened — the fix does not stop RavenDB doing it, and was never going to.
The wrapper is what carries the truth, and the runtime reads from it.

**The grid renders restored offsets**, five distinct ones on a single page: `-08:00`, `+02:00`,
`-03:30`, `+05:45`, `+00:00`.

**Sorting is provably by instant, not by displayed text.** Measured over the rendered rows:
`sortedByInstantDesc: true` while `wallClockAlsoMonotonic: false` — the wall clocks run
`23:59, 19:42, 19:28, 17:48, 12:36, 16:01, 20:03, 19:25, 04:55, 08:54`. A sort operating on the shown
value could not produce that order, which is exactly the property the design intends: order by instant,
display each row's own local time.

**Pagination is continuous across the sort**: page 1 ends at `12:24Z`, page 2 begins at `12:15Z`, still
descending, no overlap and no gap.

The point is to make the fix *visible* rather than only asserted: a grid whose timestamps carry real,
mixed-sign offsets, sorted and paginated correctly, where the offsets survive the round trip. It is
also the first end-to-end exercise of the wrapper through a generated index, the Angular client and a
real browser — every other check so far has been a test or a harness.

- A `DateTimeOffset` property on an existing entity (preferred over a new entity).
- **A button** that fills existing documents with random timestamps carrying deliberately varied
  offsets — at minimum one positive, one negative, one fractional (`+05:45`) and one `+00:00`. A
  UTC-only corpus would demonstrate nothing, since `TimeSpan.Zero` round-trips even when broken.
- The query must **sort and paginate** on that column. Since #295 a sort column must name a modelled
  attribute of the query surface, so the property has to reach `App_Data/Model/*.json` via
  `--spark-synchronize-model` before the column can sort at all.
- Verify in the browser, not only by test: `dotnet run` on the app (never a separate `ng serve` — the
  host spawns it), then read the grid.

**What this demonstrates that a test cannot:** that the value survives `EntityMapper` → JSON →
Angular's `DatePipe`. The client renders in the *browser's* local zone, so a correctly-restored value
still displays shifted relative to UTC — that is presentation, not data loss, and the demo is where
that distinction becomes obvious rather than alarming.

⚠️ Note the client contract is **not yet implemented**: `<input type="datetime-local">` has no offset,
so editing a timestamp in the UI still sends an offset-less string, which the server now reads as UTC.
The demo should fill values server-side (the button) rather than through the editor, and the gap
should be stated in the demo's own copy.

### M8 — Docs

**Flip the not-yet-implemented markers first — these are the checklist:**
- `docs/guide-dates-and-sorting.md` — remove the ⚠️ status banner at the top, and rewrite the
  "today / after" comparison columns into plain present tense. The banner explicitly says *"do not write
  code against the `After` shape until this banner is removed"*, so leaving it is a correctness bug in the
  docs, not a cosmetic one.
- `README.md` — drop the `⚠️ *Planned, not yet implemented.*` prefix from the Dates & Sort Companions row.
- The three `raven_datetimeoffset_and_sort_companions_*.md` files — set **Status: Implemented**, and record
  the shipped preview version.

Then the substantive doc work:
- `docs/guide-queries-and-sorting.md`: replace the companion guidance with the measured mechanism —
  companions are for `FieldIndexing.Search` only; typed LINQ `OrderBy` emits the correct ordering type;
  `OrderBy(string)` defaults to `OrderingType.String` and is the real numeric trap.
- Document the `DateTimeOffset` index normalisation, that **nesting** (not `FieldIndexing.No`) is what
  preserves fidelity, and that a **date-shaped `string` property is normalised too**.
- Document `DateTime.Kind=Local` → `Unspecified` on every path.
- Correct `SearchAttribute.cs` XML docs — `DateTimeOffset` no longer gets a sort companion.

### M9 — Full verification sweep
Single batched run: `MintPlayer.Spark.Tests`, generator tests, client tests, E2E, CodeCoverage.Tests.
Redirect to a log file and check the exit code — never pipe the only copy into `grep`/`tail`.
**Watch for** the known E2E teardown flake; pull the Blame hang-dump if it fires.

### M10 — Upstream + sideways
- Comment on [ravendb#17901](https://github.com/ravendb/ravendb/issues/17901) with the layer isolation and
  the nesting discriminator — better evidence than the original report.
- Hand 2sky/cronos Defect C: their numeric mis-ordering is `OrderBy(string)` → `OrderingType.String`, no
  companion fixes it, and the blanket convention can be narrowed to `[Search]` strings.

---

## Risks

| Risk | Handling |
|---|---|
| Index rebuild at every consumer | Unavoidable for any shape change. Gate on S2 so it happens once. Release notes + sequencing for coverage.mintplayer.com. |
| Generator emits a bad map expression → index Error, zero rows, silent | M7's CI index-health check. This is the highest-severity new risk. |
| Wrapper absent during the rebuild window | M4 degrades to today's behaviour rather than throwing or half-restoring. |
| `FieldIndexing.No` field accidentally filtered on Corax | HTTP 500 `NullReferenceException`. The wrapper is additive; the typed field remains the only filterable/orderable one. Cover with a test. |
| Narrowing SPARK005 stops warning someone who relied on it | Public diagnostic id — release notes; the new Error covers the case that mattered. |
| Fixing writes before reads persists corruption | Both in this PR; M4 precedes M5. |

---

## Versioning

NuGet major tracks the targeted .NET major, so this is a **minor/patch bump inside `10.0.0-preview.*`**,
never a major, regardless of the behaviour break. CI auto-publishes on push to `master` — check the
version diff in review.
