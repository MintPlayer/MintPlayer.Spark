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
- Update `guide-queries-and-sorting.md` with the slot pattern. *(Done late — the guide had no
  frontend rendering section at all; added one covering the grid, the `data` seam, the `Read`-gated
  first-column link, the three slots and the cell.)* **There is no ng-spark `AGENTS.md`** — the
  plan named a file that does not exist; every `AGENTS.md` in the repo is .NET-side.

## M8 — verify, then PR

Full suite in **one** sweep at the end, not per milestone. Then open the PR and close #308.

---

# Milestones added after the PR opened

Scope found during review and live testing. All of it belongs in this PR — it is the same
surface, and splitting would buy nothing but a second round of CI.

## M9 — extract `spark-grid-cell`

The plan counted the cell markup twice (M2, "the `#cellContent` template"). There was a **third
copy**, in the AsDetail table on the PO detail page, and it had already drifted:

| Column type | Query grid | AsDetail table |
|---|---|---|
| `boolean` | checkbox, indeterminate when null | the text `"true"` / `"false"` |
| `color` | swatch | the hex string |
| custom renderer | registry lookup | a second, hand-copied lookup |

New `grid/src/spark-grid-cell.component.{ts,html}` owns **presentation** — which control a
`dataType` becomes, plus renderer dispatch. Callers keep **value resolution**, because the row
models genuinely differ: a query row is a `PersistentObject` with an attribute list and an id, an
AsDetail row is a plain dictionary with neither. `SparkGridRenderers.columnInputsFor` delegates to
a new `cellInputsFor` so the renderer contract is stated once.

Two things deliberately not merged: the first-column link (the grid's rule, gated on `Read`, wraps
the cell from outside — passing `link` too would nest anchors), and chips (an AsDetail row's
`__sparkBreadcrumbs` is keyed by column, not by id, so it cannot label array members).

**Done when:** the cell exists once and both tables render through it. *(Done — `3adc3af`.)*

## M10 — register the cell in po-detail's `imports:`

M9 added the import statement but not the `imports:` array entry, so five bindings compiled to
nothing. Caught by CI as 5× NG8002, by nothing local.

The lesson is the check, not the typo: **`npx ngc --noEmit` without `-p` exits 0 while checking no
templates at all.** The command that reproduces CI is

```
npx ngc -p libs/node_packages/ng-spark/tsconfig.lib.json --noEmit
```

which also surfaces NG8113 (a directive in `imports:` the template never uses — `NgComponentOutlet`
in the grid, now that the cell owns dispatch). Unit tests cannot cover this class of bug here: the
po-detail specs call the cell-renderer methods directly and never render the AsDetail table.

**Done when:** the ng-packagr build is green. *(Done — `df60d16`.)*

## M11 — carry the server-resolved breadcrumb through the AsDetail flatten

Reported live: the edit form rendered `(click to edit)` where the detail page rendered
`Abdijsteeg 30, 9700 Oudenaarde`.

Not a data problem and not a regression — structural, and true for every row of every type shaped
this way. HR's `Address` renders its breadcrumb as `{Crumb}`, and `Crumb` is
`[Breadcrumb, IgnoreProperty]`. That pairing is **sanctioned by design**: `ModelSynchronizer.cs:931`
whitelists it by name, because the value is persisted and `EmbeddedBreadcrumbRenderer` resolves it
by reflecting over the CLR property. What the sanction does not say is that the guarantee is
server-side only — the same unresolvable template is shipped to the client verbatim, and
`[IgnoreProperty]` is precisely the instruction that stops the client from ever satisfying it.

The server already sends the resolved string on the nested object. `nestedPoToDict` was discarding
it — its own docblock said so — so the fix belongs at the **flatten step**, not in the pipe:

- both flatteners keep it under a reserved key (`AS_DETAIL_SELF_BREADCRUMB_KEY`)
- `AsDetailDisplayValuePipe` prefers it, keeping template substitution as the fallback — still the
  only strategy on the **create** path, where no server object exists yet
- `selfBreadcrumb` filters the mapper's blank-render placeholder, which is the bare CLR type name
  rather than an empty string (`EntityMapper.cs:209-211`); rendering `Address` would be worse than
  rendering nothing, because it reads as data. It matches on the last dotted segment, since
  callers hold the type name as either `EntityType.name` or a full `asDetailType` CLR name.

Same mechanism applied to po-detail, which had the identical bug one level down: a nested-AsDetail
column stringified its inner dict to `[object Object]`.

The reserved key rides in a dict that is posted back on save. Safe because `dictToNestedPo` walks
the entity type's attributes, never the dict's keys — now pinned by a round-trip test rather than
left to inspection.

**Done when:** both pages render the address. *(Done — `cec8533`; verified live on HR.)*

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
