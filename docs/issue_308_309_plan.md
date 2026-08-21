# Plan — a query declares its own chrome

**PRD:** `docs/issue_308_309_PRD.md` (v2)
**Issues:** #308 (implemented), #309 (items 1–4)
**PR:** #308, grown to cover both
**Branch:** `fix/parentless-sub-query`
**Base:** `master` @ `7ad2e30`
**Release:** `10.0.0-preview.61` + `@mintplayer/ng-spark@22.3.0` — **both mandatory**

---

## Milestones

| M | Title | Server? | Blocking Coverage? |
|---|---|---|---|
| M0 | Spikes | — | — |
| M1 | Template restructure: explicit states, one gate | no | prerequisite |
| M2 | `SparkQuery` gains four fields, in one change | **yes** | **yes** |
| M3 | Query actions render in the sub-query header | no | **yes** |
| M4 | `headerRenderer` registry + `headerTemplate` + `showCard` | no | no |
| M5 | Error surface: signal, alert, output | no | no |
| M6 | Refresh: `reload()` + `reloadToken` | no | no |
| M7 | `rowsNavigable` at every link site | no | no |
| M8 | Drift fixes D-1, D-2, D-3, D-6 | no | no (D-1 is severe) |
| M9 | Grid core: `@mintplayer/ng-spark/grid`, D-4/D-5/D-7 | no | no |
| M10 | `refreshQuery` + `DisableQueryActions` handlers; sub-query refresh | no | no |
| M11 | Loose ends: `Execute.cs` clone, doc corrections | small | no |
| M12 | Version, lock, release notes, docs | — | **yes** |

M1 is a prerequisite for M3–M5. M2 lands early because every later server-fed behaviour
depends on the fields existing; **all four fields go in one edit** so `preview.61` and the
model-file churn happen once. M9 lands after M1–M8 so the core is extracted from code that is
already correct, rather than refactoring and fixing at the same time.

**#308 is already implemented and green** (235 specs). It is not re-planned here — it only
needs the version bump it never carried (M12).

**Nothing is deferred to a follow-up PR.** Every extra PR costs another full round of workflow
runs, and waiting on CI is the bottleneck — so a large diff is the intended outcome, not a
problem to design around. The Coverage-side migration is part of the same unit of work and is
planned in the PRD's §Migration; it can only be executed after this publishes.

---

## Spikes

Run all of M0 before writing M1. Each spike can come back "no".

### S1 — Does an action bar in the card header disturb the stacked-card layout?

`spark-po-detail` stacks N sub-queries and relies on the card's `margin: 1rem 0` as the only
separator. M1 moves the spinner out of the gate; M3 adds a `bs-priority-nav` into the header.

**Method:** run DemoApp or HR, open a PO detail with ≥2 sub-queries, screenshot before M1 and
after M3, including one query with a declared action and one without.
**Pass:** spacing and card boundaries unchanged; a spinner now appears during the initial
load where nothing did before; the action bar does not change header height for queries with
no actions.
**Fail:** keep the margin on a wrapper that renders in every state, and/or collapse the nav
when the action list is empty.

### S2 — Does `executeCustomAction` succeed for a query whose rows are fabricated?

`MyAccountRow` has no documents. The action passes no parent and no selection, so
`ExecuteCustomAction.cs:108-131` should skip the parent reload — **assert it, don't assume
it.** This is the spike that decides whether the PRD's F3 ("Resync becomes a Spark action")
is real.

**Method:** a .NET test invoking a `showedOn: "query"` action on an entity type whose rows
are synthesised in memory, with no parent and no `selectedItems`.
**Pass:** 200, handler ran, no attempted document load.
**Fail:** query actions need a no-parent dispatch path, which changes M3's size materially —
stop and re-scope.

### S3 — Does `reloadToken` actually preserve page and sort?

R9 is the whole point of splitting data-level from metadata-level refresh, and the
ng-bootstrap `fetch` setter *defeats its own dedupe* by resetting `_initialFetchDone`
(`mp-datatable.ts:344-357`).

**Method:** spec — load, go to page 2, sort by a second column, bump `reloadToken`, assert
`executeQuery` was called again with the **same** `skip`/`take`/`sortColumns`.
**Pass:** identical params, one extra call.
**Fail:** drop the token, ship `reload()` only, and document that refresh resets paging.

### S4 — Do the four new fields survive a double synchronize?

The PRD's F11 says yes by construction (`CollectQueriesFor` returns the same references; all
mutating passes are `Database.`-filtered) and `ModelSynchronizerTests.cs:961-974` already
pins `Custom.*` pass-through. **But the fatal case is different**: with no
`[JsonExtensionData]` anywhere in the repo, an *undeclared* JSON property is destroyed on the
first run and runs 2–3 are byte-identical, so **the loss is itself a fixed point** and
invisible to the idempotency tests. #279 lost `"useProjection": false` from 17 places exactly
this way.

**Method:** extend the existing test with the four new fields on both a `Database.*` and a
`Custom.*` query; run synchronize three times; assert byte-identity **and** assert the fields
are still present — presence, not just stability. Then delete one field from the C# model
locally and confirm the test goes red.
**Pass:** stable and present; the negative control fails.
**Fail:** add explicit preservation, as #274 did for `showedOn`.

### S5 — Reproduce the nested anchor

**Method:** a spec on a query whose first attribute has a renderer emitting an `<a>`; assert
`querySelectorAll('a a').length === 1` today, `0` after M7.
**Pass:** the nested anchor is observed before the fix.
**Fail:** the PRD's F9 is wrong and M7's justification needs revisiting before the server
field is used.

### S6 — Do the M-3 security tests still pass untouched?

R12. M5 changes how the client *renders* a 404; nothing server-side moves.

**Method:** run `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` unmodified.
**Pass:** green with no edits. **Any required edit is a stop-and-ask**, not a fix.

### Not spiked: whether omitting `bs-card` breaks the datatable

Answered by reading ng-bootstrap: the card components are pure content projection,
`.card-header` is a **global class rule** not `::slotted`, and `bs-datatable` is
`:host{display:block;width:100%}` with no card dependency. Only `overflow:hidden` clipping is
lost — covered by S1's eyeball.

### Not spiked: the 404-vs-403 decision

The PRD's F8 settled it from four independent sources. Nothing left to measure.

### Withdrawn: v1's S3 (`<ng-content>` default content)

Moot — projection is no longer the mechanism.

---

## M1 — Template restructure

`spark-sub-query.component.{html,ts}`. Replace the single `@if (query(); as q)` with:

```
[error alert — outside every gate]
@if (loading())           { spinner }
@else if (query())        { header + body }
@else if (errorMessage()) { card shell + message }
```

- Spinner out of the gate, so it is reachable on first load (F4).
- `loadData` resets `query`, `entityType` and `canRead` alongside `resultCount`/`fetchFn` at
  `.ts:85-87` — this is drift fix **D-3**.
- Delete `resultCount` (F6) and its spec assertion at `.spec.ts:122`.

**Intended behaviour change:** a first load now shows a spinner; a failed load now shows
something.

**Verify:** S1; the 235 existing specs stay green.

## M2 — `SparkQuery`: four fields, one change *(server)*

`libs/spark/MintPlayer.Spark.Abstractions/SparkQuery.cs` + `models/src/spark-query.ts`:
`Actions`, `HeaderRenderer`, `HeaderRendererOptions`, `RowsNavigable` — **all nullable**
(`WhenWritingNull`, or synchronize stamps 23 model files).

- `RowsNavigable` default resolution: `Database.*` → true; `Custom.*` → true unless
  explicitly false. **Never default `Custom.*` to false** (F10).
- Checklist item: `Endpoints/Queries/Execute.cs:112-123` hand-clones `SparkQuery` and already
  drops `Description` — either extend it or delete it in favour of the real object.
- No JSON schema exists to update (F11).

**Verify:** S4; AC 19.

## M3 — Query actions in the header

- Fix the filter to `a.showedOn === 'query' || a.showedOn === 'both'`
  (`spark-query-list.component.ts:171`). Breaking in name only — `'query'` renders nowhere
  today (F2).
- Render the action bar in `<bs-card-header>`, reusing the `bs-priority-nav` loop from
  `spark-query-list.component.html:9-15`.
- Apply the `Actions` allowlist: `null` → today's set; a list → narrowed **display**.
- Wire `refreshOnCompleted` to M6's `reload()`.

⚠️ **`Actions` is not an authorization boundary** — the grant is, enforced at
`ExecuteCustomAction.cs:52` regardless of which query the caller clicked from. Say so in the
code comment, not just here.

**Verify:** S2; AC 2, 3, 4.

## M4 — `headerRenderer`, `headerTemplate`, `showCard`

- `SPARK_QUERY_CHROME` token + `provideSparkQueryChrome`, shaped exactly like
  `SPARK_ATTRIBUTE_RENDERERS` including `factory: () => []`. Resolve through
  `withDeclaredInputs`; pass `reload` as an **input callback** (`NgComponentOutlet` has no
  outputs).
- `headerTemplate = input<TemplateRef<{$implicit: SparkQuery}> | null>(null)`, matching
  `extraActionsTemplate` — **not** `<ng-content>`.
- `showCard = input(true)`; the bare branch emits the body div **and** the spinner.
- Precedence, implemented once: `headerRenderer` → `headerTemplate` → caption + actions.

**Verify:** AC 5, 6, 7.

## M5 — Error surface

`errorMessage` signal; both catches bind their error; chain
`e.error?.error || e.message || t(fallback)`; **404 → generic message**;
`error = output<HttpErrorResponse>()`; alert outside every gate; cleared on `loadData` entry
and fetch success. This subsumes drift fix **D-6**.

**Verify:** AC 9, 10, 11; S6.

## M6 — Refresh

- `reload(): void` — public, **data-level**, mirroring `spark-query-list.component.ts:268-276`.
- `reloadToken = input<unknown>(null)` in a **second** effect that skips its first run; the
  existing effect must not read it (R9).
- `spark-query-list.refresh()` → public `reload()`, so both components agree (**D-8**).

**Verify:** AC 12; S3.

## M7 — `rowsNavigable` at every link site

- First collapse `spark-query-list.component.html:87-104` ≡ `:119-136` (a same-file copy),
  which reduces three link sites to two.
- Guard becomes `first && canRead() && rowsNavigable()`.
- Set `rowsNavigable: false` on `Stock.json` (`Custom.StreamItems`) and `ProjectColumn.json`
  (`Custom.GetProjectColumns`) — both render dead links today, so the demos carry the
  evidence.

**Verify:** AC 13; S5. Fleet's `Stolen_Cars`/`Recent_Cars` links still work.

## M8 — Drift fixes

- **D-1** — wrap `spark-query-list.onParamsChange` (`:87-91`) in try/catch and route the
  failure into the `errorMessage` signal already rendered at `html:67-71`. Three lines.
  Shipping "a failed load must never render nothing" while the sibling renders a permanent
  spinner on the same 404 would be incoherent.
- **D-2** — bind `[indeterminate]` in the sub-query's boolean cell (`html:50-53`).
- D-3 and D-6 land in M1 and M5 respectively; D-4, D-5, D-7 fall out of M9.

**Verify:** AC 14, 15, 16.

## M9 — Grid core

New leaf entry point `@mintplayer/ng-spark/grid` (folder + `ng-package.json` + `index.ts`;
`tsconfig.base.json:6` maps `@mintplayer/ng-spark/*` by wildcard, so **no config edit**):

- `injectSparkGrid(source): SparkGridState` — the whole loading pipeline: query resolution,
  entity type, permissions, lookups, settings, `makeFetch`, `reload()`/`reloadMetadata()`.
  **The only writer of the reset sequence**: one `resetForNewSource()` clearing every derived
  signal, one `try/catch` around the metadata load. That single invariant *is* the fix for
  **D-4** and **D-5**.
- `SparkGridRowsComponent` — the row/cell rendering, killing the remaining duplicate anchor
  block and the two copies of `#cellContent`.
- `styles/_grid.scss` for the virtual-scrolling sizing → **D-7**. Relative `@use`, following
  `query-list/src/spark-query-list.component.scss:1`; `styles/` is not an entry point.
- Both components reduce to chrome. **Do not** put the shared code in `query-list` — that
  creates `po-detail → query-list` and drags `bs-priority-nav`, `bs-form`,
  `SparkStreamingService` and the websocket code into every detail page's bundle.

Breaking, and enumerated: the components' incidentally-public internals (`query`,
`entityType`, `canRead`, `fetchFn`, `settings`, `visibleAttributes`, `getColumnRenderer*`, …)
move onto the state object. Selectors, entry points and every bound input survive, so no demo
and nothing in Coverage changes. ~150-250 spec lines get rewritten against the state object.

**Verify:** AC 17, 18, 19; the full ng-spark suite.

## M10 — Client-operation handlers

- Register a `refreshQuery` handler in `client-operations/src/provide.ts` (only `notify` is
  wired today, and the dispatcher drops unknown types silently). M6's `reload()` is the
  missing piece.
- `spark-po-detail` calls `reload()` on its sub-queries after `refreshOnCompleted`
  (`:248-251` refreshes only the PO today).
- `DisableQueryActions` (`IClientAccessor.cs:62`): wire it, or make the no-op explicit in
  code rather than leaving it silently inert now that query actions render.

**Verify:** AC 20.

## M11 — Loose ends

- Delete the `SparkQuery` hand-clone at `Endpoints/Queries/Execute.cs:112-123` in favour of
  the real object — it already drops `Description`, and M2 gives it three more fields to drop.
- `docs/guide-custom-actions.md:159` — "available to all users" contradicts the deny-all
  default at `PermissionService.cs:9-13`.
- `docs/Spark-API-Specification.md:470-483` — still documents `useProjection`, deleted in #279.
- Add the missing `| resolveTranslation` in `spark-po-edit.component.html:5` and
  `spark-po-create.component.html:8` (`[object Object]` today).

## M12 — Release

1. `libs/node_packages/ng-spark/package.json` → **22.3.0**; peer ranges unchanged.
2. All 20 `libs/**/*.csproj` → **`10.0.0-preview.61`** (hand-maintained per file; there is no
   `Directory.Build.props` carrying the version).
3. `npm install` **from the repo root** — the lock records a stale `22.0.8`. Commit it.
4. `docs/release-notes-preview-61.md`, following the preview-60 shape. Since the versioning
   policy makes even breaks minors, the notes must state plainly which category this is, and
   must carry the `showedOn` filter change.
5. Docs: `docs/guide-custom-actions.md` (query actions; and fix the false "available to all
   users" at `:159`), `docs/guide-queries-and-sorting.md`,
   `docs/Spark-API-Specification.md:470-483` (still documents `useProjection`, deleted in
   #279), `libs/spark/MintPlayer.Spark/README.md:394,475,503-504`.
6. **Review the version diff before merging** — `npm-publish@v4` no-ops on an existing
   version, so a forgotten bump is a *green run that publishes nothing*.

**Verify:** AC 20.

---

## Verification

- `nx run @mintplayer/ng-spark:test` — 235 today, plus ~18 new. Full suite once, at the end.
- .NET suite, with `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` **unmodified**
  (S6), plus the extended `ModelSynchronizerTests` (S4) and the query-action dispatch test
  (S2).
- Manual: DemoApp/HR PO detail for S1; a bare `showCard=false` grid for the responsive-table
  overflow eyeball.
- Do **not** run `ng serve`/`ng build` against a demo ClientApp — the ASP.NET host owns the
  dev server.

## Open questions

1. ~~Does M5 ship?~~ **Moot in v2** — the server is touched by M2 regardless, so `preview.61`
   is mandatory and `rowsNavigable` no longer swings the release shape.
2. ~~`refreshQuery` / `DisableQueryActions` — implement or defer?~~ **Both ship (M10).**
3. **Coverage's migration lands after this merges** — it cannot compile until `preview.61`
   and `22.3.0` publish. Merge, publish, then migrate, as one PR in that repo.
4. **Is a single sweep enough at the end?** M9 rewrites ~150-250 spec lines on top of M1–M8.
   Per the batching rule the suite runs once, at the end — but if M9's rewrite churns, that
   one sweep may need a second pass. Not a reason to split the PR.

## Outcome

_(filled in as milestones land — deviations, and why)_
