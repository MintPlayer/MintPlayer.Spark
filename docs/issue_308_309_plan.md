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
| M12 | Row selection in the grid core | no | no |
| M13 | `selectionRule` evaluated — client affordance, **server enforcement** | **yes** | no |
| M14 | Selection hardening: per-action row gate, payload ceiling | **yes** | no |
| M15 | **M-3 completed** — uniform 404, incl. type existence | **yes** | no |
| M16 | Version, lock, release notes, docs | — | **yes** |

M1 is a prerequisite for M3–M5. M2 lands early because every later server-fed behaviour
depends on the fields existing; **all four fields go in one edit** so `preview.61` and the
model-file churn happen once. M9 lands after M1–M8 so the core is extracted from code that is
already correct, rather than refactoring and fixing at the same time. M12 depends on M9 (the
`selection` signal belongs on `SparkGridState`, so the reset invariant covers it) and M13
depends on M2, M3 and M12. M15 is independent of everything else and can land any time after
M0.

**#308 is already implemented and green** (235 specs). It is not re-planned here — it only
needs the version bump it never carried (M16).

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

### ~~S2~~ — withdrawn, already answered green

*Was: does `executeCustomAction` succeed for a query whose rows are fabricated?* It does, and
an existing test pins it: `ExecuteCustomActionTests.cs:282-296`
(`Empty_body_forwards_null_parent_and_empty_selected_items` → 200). The reload loop at
`ExecuteCustomAction.cs:118-131` is simply skipped when the selection is empty. No spike
needed; the budget goes to S9.

### S9 — Does enforcing `selectionRule` break `CarCopy` from the detail page? **Run this first**

`Fleet/CarCopy` is `"=1"` with `showedOn: "both"`. The **detail** invocation sends a parent and
`selectedItems: []` → `count == 0` → **400** under naive enforcement. That is a regression the
obvious implementation ships, and it decides a semantic rather than an implementation detail.

**Method:** a .NET test invoking `CarCopy` from the detail path (parent set, no selection) with
`"=1"` in force.
**Pass:** 200 — the rule is scoped to the query path only, keyed off "the request named no
parent", matching `CustomActionDefinition.cs:20` (*"Selection rule for **query-view**
actions"*) and `custom-actions-prd.md:134` (*"Only relevant when `showedOn` includes
`query`"*).
**Fail:** the path cannot be inferred from the wire, and the request must carry an explicit
origin — a wire change; stop and re-scope.

### S7 — Does `[selectionMode]` survive virtual scrolling and server-side paging?

`selection` is a `ModelSignal<TData[]>` with identity via `rowKey`, but the grid reassigns
`fetchFn` to refetch and `mp-datatable`'s `fetch` setter resets `_initialFetchDone`. A
selection that silently empties on every page turn — or worse, survives as stale object
references with `compareWith` unset — makes the feature a trap.

**Method:** spec — `selectionMode='multiple'`, select two rows on page 1, go to page 2 and
back, assert `selection()` identity; repeat with `virtualScroll=true`; repeat across a
`reload()`.
**Pass:** preserved by `rowKey` across refetch and paging, or deterministically cleared.
**Fail:** bind `compareWith: (a,b) => a.id === b.id`; if still insufficient, scope selection to
the current page — which changes M13's semantics and must be documented.

### S8 — Do the C# and TS selection-rule parsers agree?

The biggest risk is Vidyano's own measured failure: two ports of one algorithm that diverged
(unguarded `int.Parse` vs `isNaN`; a `var`-capture bug in the JS AND-combine loop that breaks
3+ terms).

**Method:** one committed fixture — `""`, `null`, `" "`, `"=0"`, `"=1"`, `">0"`, `">=1"`,
`"<=5"`, `"!=0"`, `"0<X"`, `"1<X<5"`, `">=1X<=5"`, `"1"`, `"1-5"`, `"*"`, `"=abc"`, `"=1.5"`,
`">= 1"`, `"x>0"` — asserted against counts 0, 1, 2, 5, 10 in **both** an xUnit theory and a
Vitest spec, generated from that one file.
**Pass:** identical results in every cell, including the malformed rows.
**Fail:** cut the grammar to `=N` / `>N` / `>=N` / `<=N` / `!=N`, drop the `X` placeholder and
ranges, and document the reduction as a deliberate divergence from Vidyano.

### S10 — What does a large selection actually cost?

Quantify the amplification finding before picking a ceiling.

**Method:** POST an action with N = 1, 10, 100, 1000 `selectedItems` against a type **with** a
row rule; measure wall time and `session.Advanced.NumberOfRequests`.
**Pass:** the numbers justify a specific default, written into the config with a comment citing
the measurement.
**Fail:** if N=100 is already pathological, the loop needs batch loading and the ceiling drops.

### S11 — Does the unknown-type shape change perturb the existing unit tests?

M15 makes unknown entity types answer in the denied shape, so `GET /spark/po/Bogus` returns 401
to an anonymous caller. Six unknown-type tests (`ListEndpointTests.cs:36`,
`CreateEndpointTests.cs:58`, `UpdateEndpointTests.cs:51/61`, `DeleteEndpointTests.cs:36/45`)
run with no authz configured, so their outcome depends on the stub principal.

**Method:** run them against the changed endpoints before rewriting anything.
**Pass:** still green — the stub authenticates, so they see 404.
**Fail:** they see 401; update each deliberately and record that unknown-type-as-anonymous is
now 401 by design, not by accident.

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

## M12 — Row selection in the grid core

- `selection = signal<PersistentObject[]>([])` on `SparkGridState`, **cleared by
  `resetForNewSource()`** — otherwise it is D-4's shape, with route A's selection POSTed as
  ids of route B's type.
- `[selectionMode]` + `[(selection)]` on both `bs-datatable` instances
  (`spark-query-list.component.html:75-80`, `:107-112`) and on the sub-query's, via
  `SparkGridRowsComponent`. `rowKey` stays at its `String(row.id)` default.
- `selectionMode` computed `'none'` unless a visible action is gated → grids without gated
  actions are pixel-identical.

**Verify:** S7.

## M13 — `selectionRule` evaluated

- `Services/SelectionRuleParser.cs` (C#) + `models/src/selection-rule.ts`, both generated
  against **one committed fixture**.
- Loader-time validation: a malformed rule is a loud config error, not a fail-open at execute.
- Client: button `[disabled]` from the parsed rule; `spark-query-list.component.ts:181` passes
  `selection()`.
- Server: check between the existence gate (`:69`) and the reload loop (`:118`) → **400** on
  violation, before any document load. Scoped to the query path (S9).
- Comment at the enforcement site: **this is not an authorization boundary; the grant is.**
- Fix `CarCopyAction.cs:16` to `args.SelectedItems.FirstOrDefault() ?? args.Parent` and drop
  the `"No item selected"` throw — unreachable once `"=1"` is enforced, and this file is the
  template consumers copy.

**Verify:** S8, S9; AC 24, 25, 26, 27.

## M14 — Selection hardening

- Per-item `rowSecurity.IsAllowedAsync(entityType, actionName, entity)` when the type has a row
  rule, refusing with the same 404 as `:128`. Closes the gap where the gate is hardcoded to
  `"Read"` while `ISparkRowRule` is action-parameterised.
- **Unconditional ceiling** on `SelectedItems.Length` (default 200), applying even when the
  rule is null — with a comment recording that `IgnoreMaxRequests` sets `int.MaxValue` and
  `estimatedRequests` is log-only, so the ceiling is the only real bound.

**Verify:** S10; AC 28, 29.

## M15 — M-3 completed

Per the PRD's D11 contract table.

1. `Queries/Execute.cs` — hoist an up-front gate mirroring `Queries/Get.cs:23-28`, **before**
   the sort-column parse, then make `:128-138` a uniform 404. Closes the query oracle and the
   attribute-name disclosure together.
2. `PersistentObject/{Get,List,Create,Update,Delete}.cs` — `isAuthed ? 403 : 401` becomes
   `isAuthed ? 404 : 401`, with the denial body **byte-identical** to that endpoint's
   not-found body (three of them interpolate the requested id, so the denial must too).
3. `Actions/ExecuteCustomAction.cs:54-60` and `:148-154` — same, body `"Not found"`.
4. **Unknown entity types adopt the denied shape** — `Get.cs:22-25`, `List.cs:21-24`,
   `Create.cs:33-36`, `Update.cs:34-37`, `Delete.cs:32-36`,
   `ExecuteCustomAction.cs:43-46`, `ListCustomActions.cs:22-25`.
5. `Permissions/GetPermissions.cs` — unknown type returns the same **200 all-false** as a
   denied one (it is deliberately anonymous-callable; audit M-1).
6. `StreamExecuteQuery.cs` — refuse at the handshake, not by closing the socket afterwards.
7. **Not changed:** controllers (`[SparkAuthorize]`), Replication, IdentityProvider, all
   `LookupReferences/*` (already leak-free — they authorize before resolving the name).

**Tests.** Edit exactly one: `ExecuteCustomActionTests.cs:84-98` → 404. Preserve every 401
assertion — each is an *anonymous* caller, and
`AnonymousPersistentObjectAccessTests.cs:41-43` explains in code why the distinction matters.
Add: byte-identical comparisons (status **and** raw body) for each endpoint; denied-execute vs
unknown-query-id; denied execute with a bogus sort column not returning 400; the
anonymous-still-401 negative control; row-denied ≡ type-denied. Strengthen
`NotFoundVsForbiddenTests` by making its second principal genuinely authenticated and
comparing bodies — an addition, not a rollback, so S6 still holds.

**Verify:** S6, S11; AC 30–35.

## M16 — Release

1. `libs/node_packages/ng-spark/package.json` → **22.3.0**; peer ranges unchanged.
2. All 20 `libs/**/*.csproj` → **`10.0.0-preview.61`** (hand-maintained per file; there is no
   `Directory.Build.props` carrying the version).
3. `npm install` **from the repo root** — the lock records a stale `22.0.8`. Commit it.
4. `docs/release-notes-preview-61.md`, following the preview-60 shape. Since the versioning
   policy makes even breaks minors, the notes must state plainly which category this is, and
   must carry: the `showedOn` filter change; **`selectionRule` becoming enforced** (a rule that
   was decoration now returns 400, and the per-action row gate can refuse actions that work
   today); and **M-3** (authenticated-denied is now 404, `SparkClient` callers get `null`
   where they used to get a throwing 403, unknown entity types answer in the denied shape).
5. Docs: `docs/guide-custom-actions.md` — query actions, the false "available to all users"
   at `:159`, and `:119-127` becomes the single normative `selectionRule` grammar (operator
   table, `X` placeholder, malformed = config error, query-path-only scope);
   **`docs/prd/custom-actions-prd.md:134`'s "defaults to `=0`" is wrong and must be
   corrected**; `docs/guide-queries-and-sorting.md`,
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
