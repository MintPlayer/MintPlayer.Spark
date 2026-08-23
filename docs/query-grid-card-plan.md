# Implementation plan — `spark-query-grid` / `spark-query-card`

Companion to [`query-grid-card-PRD.md`](./query-grid-card-PRD.md). Branch
`feat/spark-query-grid-card`. One PR; commit per milestone.

Comments (1), (2) and (3) on #308 need no code — see PRD §1. Everything below is comment (4)
plus the two loose ends from PRD §7.

---

## M0 — correct the record

The requirements file states `selectionRule` did not come across in #311. It did, in full.
Append a verified-findings note to `query-grid-card-requirements.md` rather than editing the
captured text — the file's job is to record what was asked, and the ask was made in good faith
on that belief.

Verified while writing this: `docs/issue_308_309_{PRD,plan}.md` were **branch-only and never
landed**, so there is nothing to delete. `Spark-API-Specification.md` has no
`headerRenderer`/`rowsNavigable` block. The `rowsNavigable` mentions in `issue_310_{PRD,plan}.md`
are historical narrative that correctly records it never shipping — leave them.

**Done when:** no document claims `selectionRule` is outstanding. *(Done — commit `M0`.)*

## M1 — the three slot directives

New: `grid/src/spark-query-slots.ts` — `SparkQueryIconDirective`,
`SparkQueryCaptionDirective`, `SparkQueryActionsDirective`.

Each is the `BsDatatableColumnDirective` shape: a selector, and
`readonly templateRef = inject(TemplateRef)`. No inputs — these are slots, not configuration.
Give the actions directive a typed context (`{ $implicit: CustomActionDefinition[] }`) so a
host that wants the server's actions *plus* its own can render both; `ngTemplateContextGuard`
as in `row-template.directive.ts:31`.

Export from `grid/index.ts`.

**Done when:** `tsc` is clean and the directives are importable from `@mintplayer/ng-spark/grid`.

## M2 — `spark-query-grid`

New: `grid/src/spark-query-grid.component.{ts,html}`.

Move the body of `spark-sub-query` in wholesale — resolution, permissions, lookups, fetch
closure, error handling, selection, the `#cellContent` template — **minus** the card and the
header. Keep every comment: they document bugs (the three-state ordering, the
`[indeterminate]` null case, the nested-anchor trap, the 404-is-deliberately-vague rule). They
are the most valuable thing in the file.

Then add what the page needs, **without** taking streaming (PRD §3 — the WebSocket must not
reach every detail page's bundle):
- **`data`** — optional external rows. Bound, the grid renders them and runs no fetch. This is
  how the page's streaming rows get in.
- **`settings`** as a two-way `model`, so the page's client-side sort sees the clicked sort.
- **`search`** as an input, so the page owns the box and the grid owns the request.
- **`rowClicked`** and **`customActionExecuted`** outputs, which query-list has and sub-query
  does not.

Take **query-list's** entity-type resolution (source name + `singularize` fallback), not
sub-query's `query.entityType`-only version — PRD §7.2. Use `SPARK_GRID_PAGE_SIZES` — §7.3.

Expose `query`, `entityType`, `customActions`, `selection`, `canRead`, `canCreate` and
`resultCount` as readable signals. The card and the page read them through a template reference
variable (`<spark-query-grid #grid>` … `grid.query()`), not `viewChild`: hosts wrap grids in
`@if`, where a `viewChild` is intermittently undefined, and the house style is already to avoid
it (`spark-sub-query.component.ts:74-81`).

Keep the three-state template ordering exactly as documented in
`spark-sub-query.component.html:1-14`. Folding it back into one `@if (query())` gate is the
bug it was written to prevent.

**Done when:** the component compiles and renders a query standalone.

## M3 — `spark-query-card`

New: `grid/src/spark-query-card.component.{ts,html}`.

`<bs-card>` + `<bs-card-header>` + `<spark-query-grid>`. Collect the three slots with
`contentChild(...)`; for each, `*ngTemplateOutlet` when present, the PRD §3 default when not.
Forward `queryId`, `parentId`, `parentType`, `reloadToken` to the grid and re-emit `error`.

The default action bar is the existing `bs-priority-nav` block from
`spark-sub-query.component.html:29-40`, including `[disabled]="!isActionEnabled(action)"`.

**Done when:** a card with no slots is byte-identical in output to today's `spark-sub-query`.

## M4 — rewire the two call sites

- `spark-po-detail.component.html:176` → `<spark-query-card>`; swap the import at
  `spark-po-detail.component.ts:29,45`. Add the three forwarded `TemplateRef` inputs (PRD §4)
  alongside the existing `extraActionsTemplate`/`extraContentTemplate`, and pass them to each
  card.
- `spark-query-list` keeps its selector, **both** routes and its page chrome (action bar, `<h2>`,
  LIVE badge, search box, New button). It keeps `paramMap`, the `po/:type` type-to-query
  resolution and `singularize`, and it keeps streaming: `SparkStreamingService`,
  `connectStreaming`/`disconnectStreaming`, `handleStreamingMessage` and `applyFilter`, whose
  output it now feeds to the grid as `[data]`. Its **two** datatable branches and all cell
  markup are replaced by one `<spark-query-grid>`.
- Move the `::ng-deep bs-datatable` virtual-height fix (`spark-query-list.component.scss:9-29`)
  to travel with the datatable, or it silently stops applying — `::ng-deep` is scoped to the
  component that declares it, and the datatable will no longer be in this one.
- Delete `spark-sub-query.component.{ts,html,spec.ts}` and its `po-detail/src/index.ts` export.
- Update the entry-point comment at `src/public-api.ts:11`.

**Done when:** nothing references `SparkSubQueryComponent` and the build is clean.

## M5 — tests

Carry both existing specs over rather than writing fresh ones — they encode fixed bugs.

- `spark-sub-query.component.spec.ts` (276 lines) → `spark-query-grid.component.spec.ts` for
  the data cases, plus card cases for the header. Keep the `SparkLanguageService` stub: the
  real one fetches `/spark/culture` on construction and vitest rejects unhandled requests.
- `spark-query-list.component.spec.ts` (286 lines) → split the same way.
- **New**, one per slot: supplied → override renders and the default does not; absent →
  default renders. This is the PRD §4 claim, and it is the one thing no existing test covers.
- **New**: a card with no slots renders the caption and the server's actions — the auto-render
  guarantee.

## M6 — the loose ends (PRD §7)

- Call `SelectionRuleParser.IsValid` at custom-actions configuration load; throw naming the
  action and the rule. Add a test that a malformed rule fails at load, and one that
  `ExecuteCustomAction` no longer 500s on one.
- Audit `Spark-API-Specification.md` and `guide-custom-actions.md` per PRD §7.2.

## M7 — release

- `ng-spark` `22.3.0 → 22.4.0` (minor: Angular major unchanged — `CLAUDE.md`). Check whether
  `ng-spark-auth` needs a matching bump; it does not depend on these entry points.
- `docs/release-notes-preview-63.md`: the two components, the three directives, and the two
  removals from PRD §6 called out as breaking.
- Update `guide-queries-and-sorting.md` and the ng-spark AGENTS.md with the slot pattern.

## M8 — verify, then PR

Full suite in **one** sweep at the end, not per milestone. Then open the PR and close #308.

---

## Risks

- **Streaming is the real merge risk.** It exists in one grid only and has a subscription
  lifecycle. Reconnect-on-query-change and disconnect-on-destroy both need a test; a leaked
  socket is silent.
- **The three-state template ordering** and the `[indeterminate]` binding are previously-fixed
  bugs living in comments. Moving code is where they get re-broken.
- **`spark-query-list` is the bigger rewrite**, not the sub-query: it loses two datatable
  branches and keeps four pieces of page chrome.

## Open question for the owner — not blocking

`*sparkQueryActions` defaults to the server's actions and replaces them when supplied. A host
wanting "the server's, plus mine" gets them via the template context (M1). If the more common
want is *append* rather than *replace*, the default should instead render server actions and
then the slot — say so and it is a one-line change. Building the override first: it is the
stricter of the two and can express the other.
