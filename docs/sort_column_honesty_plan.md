# Plan — Sortable means sortable

**PRD:** `docs/sort_column_honesty_PRD.md`
**Issue:** not yet filed
**Branch:** `fix/sort-column-honesty` (proposed)
**Base:** `master` @ `c1c12b9b`
**Release:** `10.0.0-preview.72` · `@mintplayer/ng-spark` `22.11.0` (`ng-spark-auth` untouched at `22.9.0`)

---

## Milestones

| M | Title | Breaking? |
|---|---|---|
| M0 | Spikes S1–S7 | — |
| M1 | Model: `isOrderable` on `EntityAttributeDefinition` + TS mirror | no |
| M2 | Sync: classify orderability, write the field, stay a fixed point | no |
| M3 | Server: capability gate + `ILogger` + refusals reported; reconcile the endpoint 400-gate | yes (F8) |
| M4 | Wire: `QueryColumn.isOrderable` | no |
| M5 | Client: bind the affordance; delete both hardcoded `sortable: true` | yes (UI) |
| M6 | Generator: computed indexed field (D5) | no |
| M7 | Coverage app: sortable coverage percentage + defined null/zero placement | no |
| M8 | Reference picker: no dead sort affordance | yes (UI) |
| M9 | Tests: one batched run | — |
| M10 | Docs: `guide-queries-and-sorting.md`, release notes, PRD status | — |
| M11 | Versions | — |

**Sequencing.** M0 first and it can reshape most of what follows. M1 → M2 → M4 are sequential
(M2 needs the field, M4 mirrors it). M3 depends on M2 for the classification but its `ILogger` and
gate-reconciliation halves are independent and can land first. M5 needs M4's TS type but can be
built against a stubbed field. M6 is independent of M1–M5 and is the long pole — start it right
after S3. M7 needs M6 + M2. M8 is independent throughout. M9 runs **once**, after M8, per repository
convention; intermediate milestones are verified by reading and type-checking only
(`dotnet build`, `nx run ng-spark:build`).

**One PR.** Every milestone, every spike verdict, and every incidental defect found along the way
lands in the single PR for this work — framework, generator, and the Coverage app together. No
follow-up PR, no phase 2.

## M0 — Spikes

Each can come back "no" and reshape its milestone. **Record the verdict under its heading in this
file before starting the milestone it shapes.** S1 and S3 are gating: a "no" on either changes the
shape of the whole fix.

### S1 — Does Raven silently ignore `order by` on a `FieldIndexing.No` field? (gates everything)

The entire diagnosis in PRD F2 rests on this being a silent no-op rather than an error or a partial
ordering. It is asserted from the generator's own comment, not measured.

1. Against a test RavenDB instance, build an index over an entity with a complex property mapped
   `FieldIndexing.No` (mirroring `Repositories_Overview`/`LatestCoverage`).
2. Issue `from index 'X' order by LatestCoverage` in RQL directly. Record: error, no-op, or some
   ordering (by nothing? by id? by term presence?).
3. Repeat through the LINQ path `session.Query<T, Index>().OrderBy(x => x.LatestCoverage)` and
   capture the generated RQL — confirm it is `order by LatestCoverage` and not silently dropped
   client-side by the Raven LINQ provider, which would move the no-op one layer earlier.
4. Repeat with `order by LatestCoverage desc` and diff the two row orders. **If they differ, the
   field is partially ordered and S7 becomes a security question, not a formality.**

Verdict shapes: nothing (confirms F2) · errors (then the bug is a swallowed exception somewhere and
M3 changes shape) · partially orders (then S7 blocks and the fix is urgent).

### S2 — Does `sortable: false` actually suppress the affordance? (shapes M5, M8)

1. In `C:\Repos\mintplayer-ng-bootstrap`, confirm `DatatableColumnDef.sortable` flows through
   `effectiveColumns` (`mintplayer-ng-bootstrap-datatable.mjs:236-240`) to the WC.
2. Confirm `onHeaderClick`'s guard `if (!(t.sortable ?? !0) …) return`
   (`mp-datatable-UJI8E73X.mjs:1231`) means no `mp-datatable-sort-change` and no
   `scheduleFetchReload()`.
3. Confirm the header renders as non-interactive markup (not a `<button class="header-sort">`,
   `:826-829`) so it is not focusable and announces correctly — otherwise M5 needs an ARIA fix too.
4. Confirm the `[i]` description glyph still renders and still opens inside a **non**-sortable
   header (its click handler assumed a sortable parent, `spark-attribute-description.component.ts:70-73`).
5. Note the peer range: does this require an ng-bootstrap bump? Workspace currently pins `^22.17.0`.

### S3 — How does a computed indexed field get into a generated index? (gates M6, decides O7 vs O8)

1. Read `GenerateIndexGenerator.cs` around the map construction (`:294-352`, `:380`, `:389-390`) and
   establish whether the emitted `AbstractIndexCreationTask` can be extended without editing the
   generator's output — a `partial` hook, an additional-map mechanism, or an attribute carrying an
   expression.
2. Check `docs/guide-queries-and-sorting.md:495` ("Keeping a hand-written index, without hand-writing
   the boilerplate") — this may already be the intended escape hatch, in which case O8 is cheaper
   than O7 and M6 shrinks to "document and use it".
3. Establish how a computed field interacts with `ResolveSortProperty`: is the natural shape a
   `{Name}Sort` companion (`LatestCoverageSort` as a `double`, `[IgnoreProperty]`), which needs *no*
   sort-pipeline change at all? **If yes, prefer it** — it reuses a tested path and D5 becomes
   generator-only.
4. Confirm nothing computed is persisted: inspect the document JSON after a save.
5. Cost the alternative honestly: O8 means a hand-written `Repositories_Overview` and nine call
   sites (`RepositoryActions.cs:53`, `BadgeController.cs:34`, `BrowseController.cs:62,176,512`,
   `RepoSettingsController.cs:74`, `TokensController.cs:55`, `UploadsController.cs:498`,
   `MyAccountsService.cs:43`, `SparkVisibility.cs:31`) continuing to compile against it.

### S4 — Can the synchronizer reach the orderability classification? (shapes M1, M2)

1. Read `ModelSynchronizer.cs` around `:720` and establish what it knows per property: does it have
   the CLR `PropertyInfo` and the entity assembly at that point? (PRD #348's S-series established it
   runs offline with entity assemblies loaded and reflects per property, so this is expected to be
   yes.)
2. Locate the generator's complex/searchable classifier (`isComplex`, `isSearchableText`,
   `isDateTimeOffset`, `ResolveBreadcrumbPath`) and determine whether it is reusable from the
   synchronizer or lives in a generator-only assembly.
3. Verdict: **shared** (extract the classifier to a common place, one answer for both) or
   **duplicated** (the synchronizer reimplements it, and a test pins the two against each other so
   they cannot drift).
4. Decide what `isOrderable` is for an entity with **no** `[GenerateIndex]` and a hand-written index:
   this is the `null` case in D1 and must default to permissive.

### S5 — Percentage-sort semantics for the undefined cases (shapes M7, D6)

1. Query production-shaped data: how many repositories have `LatestCoverage == null`, and how many
   have `LinesCoverable == 0`? If either is empty in practice, seed both.
2. Decide placement in both directions and write it down: a `-1` sentinel puts zero-coverable repos
   below 0% ascending; `NULL_VALUE` sorts first in Raven
   (`docs/guide-queries-and-sorting.md:411`) which puts no-data repositories *above* 0%-covered ones
   ascending — probably not what a reader wants.
3. Check what the `coverage-bar` renderer currently draws for both cases
   (`coverage-bar-renderer.component.ts`, `coverage-summary.ts:9-28`) so the order matches the glyph.
4. Confirm with the product view: should "never uploaded" and "0% covered" be adjacent or at
   opposite ends? This is a judgement call, not a measurement — record the decision either way.

### S6 — Blast radius: which affordances vanish, which sorts must survive (gates M5, AC3, AC4)

1. For all five apps (`apps/CodeCoverage`, `DemoApp`, `Fleet`, `HR`, `WebhooksDemo`), enumerate every
   attribute with `showedOn` containing `Query` and classify each as orderable / not / unknown.
2. Cross-check against `isSortable: true` in `apps/DemoApp/.../StartPage.json:77,91` and
   `apps/HR/.../Person.json:236` (PRD F14) — reconcile or delete those, and say which.
3. For every column classified not-orderable, click its header in the running app **before** the
   change and record whether it currently reorders. Any that *does* reorder is a classifier bug, and
   must be fixed before M5 ships or AC4 fails.
4. Produce the table that goes into the release notes: app, query, column, before, after.

### S7 — Is a `FieldIndexing.No` sort a partial disclosure oracle? (security, gated by S1)

Formality if S1 says "no ordering at all"; a blocker if it says "partially orders".

1. Take S1's ascending/descending row orders for the complex field. If they differ at all, an
   attacker can extract a comparison signal from a field the index does not index.
2. Extend `tests/MintPlayer.Spark.Tests/Services/SortColumnDisclosureTests.cs` with the complex-field
   case regardless of the verdict — the absence of a signal deserves a pinning test as much as its
   presence deserves a fix.
3. Confirm the new capability gate cannot be used as a *positive* oracle: a reported refusal must not
   distinguish "not orderable" from "not on your query surface", or the refusal message itself leaks
   the existence of an attribute the caller may not see. **Report one message for both, and log the
   distinction server-side only.**

## M1 — Model field

- `isOrderable : bool?` on `EntityAttributeDefinition` (three-state per PRD D1), plus the TS mirror
  in `entity-type.ts`.
- Keep it **out** of `StructuralAttributeFields` (AC8), as `description` is (#348 F2).
- Docblock: states the three states, and points at `isSortable` as the other, unrelated thing.
- Verify: `dotnet build`, and `--spark-verify-model` reports no hash drift.

## M2 — Sync classification

- Per S4's verdict, either extract the generator's classifier or reimplement it with a drift-pinning
  test.
- `ModelSynchronizer` writes `isOrderable` per attribute. Cases to get right: scalar (`true`),
  searchable text with companion (`true`), `DateTimeOffset` with companion (`true`), complex with a
  resolved `[Breadcrumb]` companion (`true`), complex without (`false`), `[IgnoreForIndex]`
  (`false`), no `[GenerateIndex]` on the entity (`null`).
- Fixed point (AC7): run `--spark-synchronize-model` twice across all five apps, `git diff --stat`
  empty after the second. Watch key ordering, which bit #348 (its S2) on `TranslatedString`.
- Expect this milestone to rewrite model JSON in all five apps. That diff is the evidence for S6's
  table.

## M3 — Server gate, logging, and reconciliation

Three separable changes, all in `libs/spark/MintPlayer.Spark/`:

1. **Capability gate.** `QueryExecutor.IsSortableAttribute` (`:1412-1418`) gains the
   `isOrderable != false` test alongside the existing `showedOn` test. Two gates, two log messages,
   one caller-visible message (S7.3). `SortMappedRows` (`:848-861`) gets the same treatment — an
   in-memory sort over mapped rows is *not* index-bound, so the capability gate must **not** apply
   there; confirm and comment why.
2. **`ILogger`.** Replace `Console.WriteLine` at `:1227-1233` and `:854-860` (PRD F9). `QueryExecutor`
   takes an `ILogger<QueryExecutor>`; check how it is constructed and whether `[Inject]` applies.
3. **Reconciliation.** `Execute.cs:77-102` `allowedProperties` gains the `ShowedOn` filter so the
   400-gate and the executor agree (PRD F8). Refused columns are returned in the query result
   (`QueryResult`) rather than as a 400 — a refusal stays a 200 with unchanged order (AC5).

Verify by reading and `dotnet build`; do not run the suite yet.

## M4 — Wire

- `QueryColumn.isOrderable` (`Abstractions/QueryResult.cs`), filled in
  `Services/QueryResultProjector.cs` beside the existing `IsSortable` line (`:56`).
- TS mirror `models/src/query-result.ts`.
- `QueryColumn.isSortable` keeps its behaviour and gains the clarifying docblock (PRD D4).
- Refused-sort-columns field on `QueryResult`, mirrored in TS.

## M5 — Client affordance

- `spark-query-grid.component.html:43` → `sortable: col.isOrderable !== false`; the hardcoded `true`
  goes.
- Per S2.4, confirm `spark-attribute-description` still behaves inside a non-sortable header.
- Optional, decide during implementation: render the reported refusal somewhere a user can see it,
  rather than only logging it. A tooltip on a non-sortable header ("this column cannot be sorted") is
  the cheap version and reuses the `[i]` machinery from #348.

## M6 — Computed indexed field

Shape set entirely by S3. Two candidate shapes, in preference order:

1. **Companion-shaped** (preferred if S3.3 allows): the generator emits `LatestCoveragePercentSort`
   as an `[IgnoreProperty]` `double` computed in the index map. `ResolveSortProperty` already
   redirects to `{Name}Sort` — no sort-pipeline change, and the tested path is reused.
2. **Explicit computed field**: a new declaration carrying an expression, mapped and indexed, never
   persisted.

Either way: nothing lands in the document (S3.4), and `isOrderable` must report `true` for the
column the computed field backs.

## M7 — Coverage app

- Declare the coverage percentage as a computed index field per M6:
  `LinesCoverable == 0 ? -1 : LinesCovered * 100.0 / LinesCoverable`, guarding the divide.
- Handle `LatestCoverage == null` per S5's decision.
- Re-synchronize the model; `LatestCoverage` becomes orderable.
- Confirm the nine `Repositories_Overview` consumers still compile and behave (list at S3.5).
- Sanity-check the sibling case: `MyAccountRow.json:94-111` `AggregateCoverage` is a flat `number`
  on the Home page's `Custom.MyAccounts` query. Does *its* header sort? It is a different query and a
  different shape; if it is also dead, it is in scope for this PR.

## M8 — Reference picker

- `spark-reference-picker.component.html:66`: same hardcoded `sortable: true` over `[data]`-mode rows
  whose in-memory sort is a structural no-op (PRD F10).
- Default action: `sortable: false`. If S2 shows the WC accepts a key extractor that understands
  `QueryResultItem`, make it sort properly instead — but do not leave the affordance dead.

## M9 — Tests (single batched run)

Written as milestones land; **run once**, here.

Server (`tests/MintPlayer.Spark.Tests/`):
- Sorting a complex/`AsDetail` column with no companion is refused and reported, not silent (AC10).
- Sorting a complex column *with* a resolved `[Breadcrumb]` companion still works.
- `isOrderable == null` (no `[GenerateIndex]`) still sorts — permissive default (S4.4).
- `SortColumnDisclosureTests` and `SortInjectionTests` pass **unmodified** (AC6), plus the new
  complex-field disclosure case from S7.2.
- The refusal message does not distinguish capability from surface (S7.3).
- `Execute.cs` allow-list and `IsSortableAttribute` agree — a `showedOn: "PersistentObject"`
  attribute takes one path, not two.
- `ModelSynchronizerTests`: the seven `isOrderable` classification cases from M2; existing
  `isSortable` drag-reorder tests (`:160-212`, `:1233`) unchanged (AC9).

Client (`libs/node_packages/ng-spark/`):
- `spark-query-grid.component.spec.ts`: a header-click test — the first one to exist — asserting a
  refetch with the new `sortColumns`, and that an `isOrderable: false` column renders no affordance.
- `spark-grid-columns.ts` `initialGridSettings` gets its first spec.
- `spark.service.spec.ts:52-66` wire-format assertion unchanged.

E2E:
- The Coverage acceptance path: sort by coverage percentage both directions, including a
  high-ratio/low-count repository (AC1) and both undefined cases (AC2).

Generator:
- Snapshot the emitted index for a computed field; assert the document JSON does not contain it.

## M10 — Docs

- `docs/guide-queries-and-sorting.md`: a section on the capability gate beside the existing security
  gate at `:420-446`; correct or annotate `:546-553`, which currently states the complex-column
  limitation as absolute; document the computed-field escape hatch.
- `SPARK_INDEX_010`'s message (`GenerateIndexDiagnostics.cs:115-121`) points at `[Breadcrumb]` as
  *the* fix. Per PRD F5 that is wrong for a ratio. Add the computed-field option to the message and
  its docs.
- `docs/release-notes-preview-72.md`: lead with the visible change — sort affordances disappearing
  from columns that never worked — carrying S6's table so nobody reads it as a regression.
- Flip this PRD's status line and record every spike verdict in this file.

## M11 — Versions

- `10.0.0-preview.72` across the NuGet packages (major stays `10` — .NET target unchanged).
- `@mintplayer/ng-spark` `22.10.0` → `22.11.0`. Minor, not major: the Angular target is unchanged, and
  per `CLAUDE.md` a break in our own API is a minor bump with the break described in the release
  notes. `@mintplayer/ng-spark-auth` is untouched — leave it at `22.9.0`.
- Check the ng-bootstrap peer range if S2.5 says a bump is needed.
- CI publishes on push to `master`: check the version diff in review.
