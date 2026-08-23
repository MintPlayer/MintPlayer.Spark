# PRD — `spark-query-grid` and `spark-query-card`

Status: **proposed**. Branch `feat/spark-query-grid-card`, cut from `master` at `cc18aa3`.
Requirements of record: [`query-grid-card-requirements.md`](./query-grid-card-requirements.md).
Supersedes `docs/issue_308_309_PRD.md` and `docs/issue_308_309_plan.md`, which describe the
server-declared-chrome design that is **not** being built.

---

## 1. What changed since the requirements were written

Two of the four comments on #308 turn out to need no work. Both were verified against the
tree, not inferred:

| Comment | Finding |
|---|---|
| (1) `rowsNavigable` — not needed | **Correct, and already absent.** `grep` over `libs/` and `Demo/` returns nothing. `Query`-without-`Read` is the only click-through gate, and `spark-sub-query.component.html:113-120` documents it as such. Nothing to do. |
| (2) `selectionRule` — "will need to be shipped next" | **Already shipped in full.** The requirements file records it as missing; that is wrong. Server (`SelectionRuleParser`, `ExecuteCustomAction` enforcement, the 200-item ceiling, `ListCustomActions` projection) and client (`selection-rule.ts`, the shared `selection-rule.fixture.json`, `selection-mode.ts`, and the wiring in **both** grids) are byte-identical to the #308 branch, and `release-notes-preview-62.md:147` documents the release. Nothing to do. |
| (3) `guide-custom-actions.md` | **Already on master**, via #311's M1. The only line unique to the #308 branch documents `spark.AddAuthorization()` / `AllowAnonymousAccess()` — APIs **#310 deleted**. It must not come back. |
| (4) the two components | **This is the whole of the remaining work.** |

So this PRD covers comment (4) only. Two loose ends found while checking are folded in
(§7) rather than deferred, per the one-PR rule.

## 2. Problem

Two components render a Spark query as a grid, and they are 60% the same component:

- **`spark-query-list`** (`query-list/`, 482 TS + 181 HTML) — the routed page behind
  `/query/:alias`. Owns an action bar, an `<h2>` caption, a LIVE badge, a search box, and
  **two** `<bs-datatable>` branches (streaming vs. paged).
- **`spark-sub-query`** (`po-detail/`, 323 TS + 153 HTML) — auto-rendered once per entry in
  `EntityTypeDefinition.Queries` from `spark-po-detail.component.html:176`, inside a `bs-card`.

#311 already extracted what could be shared without restructuring — `@mintplayer/ng-spark/grid`
holds `visibleGridAttributes`, `initialGridSettings`, `isVirtualScrollingQuery` and
`SparkGridRenderers`, precisely because the renderer lookup had been duplicated byte-for-byte
(`spark-grid-renderers.ts:14`). That fixed the *helpers*. The **grid body itself** — the column
loop, the row template, the first-column link gate, the cell renderer dispatch, the boolean
`[indeterminate]` handling, the selection wiring — is still written out twice, and has already
drifted once (the `[indeterminate]` binding existed in one and not the other).

Separately, a host that wants to *adjust* a query card cannot. `showCard` is all-or-nothing
and `headerTemplate` replaces the entire header — so changing just the icon means
re-implementing the caption and the action bar too.

## 3. What we build

### `spark-query-grid` — the grid, and nothing else

A `<bs-datatable>` that renders a query or a sub-query. No card, no caption, no action bar.
It owns: query and entity-type resolution, permissions, lookup options, paging/sorting, the
fetch closure, the error alert, the row link gate, selection state, and custom-action execution.

Inputs: `queryId` (required), `parentId`, `parentType`, `reloadToken`, `search`, `data`,
and `settings` as a two-way `model`. Outputs: `error`, `rowClicked`, `customActionExecuted`.

**It does not own streaming, and that is deliberate.** `spark-grid-renderers.ts:20-24` already
records the decision, from when the shared helpers were extracted: merging the two components
outright "would drag [streaming, search, a websocket dependency graph] into every detail page's
bundle." That reasoning still holds, and a design that quietly reverses it would put a WebSocket
client in the bundle of every PO detail page, none of which stream.

Instead the grid takes an optional **`data`** input of externally-supplied rows. Bound, it
renders those and runs no fetch; unbound, it fetches for itself. `bs-datatable` already draws
this exact line — `[data]` and `[fetch]` are mutually exclusive (`datatable.component.ts:240-246`)
— so the grid is expressing the datatable's own contract rather than inventing one.

`spark-query-list` keeps `SparkStreamingService`, the snapshot/patch handling and the
client-side filter-and-sort, and feeds the result in as `[data]`. The socket stays in the page
bundle; the **three** duplicated `<bs-datatable>` blocks still collapse to one. `settings` is a
`model` so the page can run its client-side sort against the sort the user actually clicked.

### `spark-query-card` — chrome around it

A `<bs-card>` with a header (icon · caption · actions) and a `spark-query-grid` in the body.
Everything a host does not override renders exactly as it does today.

### The slots

Three structural directives, projected by the host, each collected with `contentChild`:

| Directive | Slot | Default when absent |
|---|---|---|
| `*sparkQueryIcon` | header, left | nothing |
| `*sparkQueryCaption` | header, centre | `query.description \| resolveTranslation` &#124;&#124; `query.name` |
| `*sparkQueryActions` | header, right | the `bs-priority-nav` of server-declared custom actions |

Each takes an **optional query alias or id as its value** — `*sparkQueryIcon="'cars'"`. A detail
page renders one card per entry in `EntityTypeDefinition.Queries`, so a bare slot would decorate
all of them identically, which is rarely the intent. A targeted slot wins over an untargeted one,
which is the catch-all. This is `bsPriorityNavItem`'s shape: a value input aliased to the
selector, collected with `contentChildren`.

Named after ng-bootstrap's `*bsDatatableColumn` convention — `{prefix}{Component}{Slot}` —
with the `spark` prefix, since these are ng-spark directives. The `*bs…` prefix in the
requirements is ng-bootstrap's and would collide.

Each is a four-line directive: `@Directive({selector:'[sparkQueryIcon]'})` +
`readonly templateRef = inject(TemplateRef)`. The card reads them with `contentChild()`,
matching `datatable.component.ts:170-173`.

`*sparkQueryCaption` is included because it is the third of three header regions. Without it
the slots would be strictly less expressive than the `headerTemplate` they replace, which
would force a host wanting a custom title back onto re-implementing the whole header — the
exact problem being fixed.

## 4. The auto-render question

> *How does this serve the auto-rendered sub-query, where there is no host to supply templates?*
> — the objection that killed the host-side design in #308 v1.

**Because the slots override defaults; they do not replace them.** An absent slot is not an
empty slot. `contentChild` returns `undefined`, and the card renders the default in the table
above. The auto-render call site passes no templates and is therefore pixel-identical to today.

The premise of #308 v1 — *"a sub-query is auto-rendered, so there is nobody to project a
toolbar in"* (`spark-sub-query.component.ts:121-127`) — is true, and is an argument for the
server declaring the **default** action set. It is not an argument against a host **override**,
because the two answer different questions. The server already declares actions via
`getCustomActions` + `filterQueryActions`, and that stays exactly as it is.

The icon slot makes this concrete from the other direction: neither `SparkQuery` nor
`EntityType` carries an icon field. The server has nothing to say, so the default is nothing
and the slot is the *only* source. Actions are the mirror image: the server has plenty to say,
so the slot is a rare override. One mechanism, correct at both ends.

This is also why `SparkQuery.actions`, `headerRenderer`, `headerRendererOptions` and the
`SPARK_QUERY_CHROME` registry from #308 are not revived: they existed to let the server supply
chrome a host could not. The host can now supply it directly.

### Customising an auto-rendered sub-query

The above keeps the auto-rendered path *working*. Making it *customisable* needs one more step,
because `contentChild` genuinely cannot reach that call site: `spark-po-detail` is instantiated
by the router (`spark-routes.ts:26`), so in a default app there is no `<spark-po-detail>` tag
anywhere to project content into.

The route out already exists and is already used. `SparkRouteConfig.poDetail` lets an app
substitute its own component, and the Fleet demo does exactly that for two routes with one-line
wrapper shells. So `spark-po-detail` gains three forwarded `TemplateRef` inputs, which it passes
to each card. A structural directive cannot cross a component boundary, but its `TemplateRef`
can — and this is already the house idiom in that very component: `extraActionsTemplate` and
`extraContentTemplate` are forwarded `TemplateRef` inputs consumed with `*ngTemplateOutlet`.

So the card accepts a slot from either direction — `contentChild` for hand-written markup, a
forwarded input for the auto-rendered path — and the directives are sugar that populate the
same three template references. Targeting (§3) is what makes one forwarded set serve a page
rendering several sub-queries.

## 5. What happens to the existing components

- **`spark-sub-query` is deleted.** It becomes `spark-query-card` with a `parentId`/`parentType`.
  The single call site (`spark-po-detail.component.html:176`) is updated. Its `showCard` input
  disappears — a host that wants no card uses `spark-query-grid` directly, which is what
  `showCard="false"` meant. `headerTemplate` disappears, replaced by the three slots.
- **`spark-query-list` keeps its name, selector and both routes**, and loses its grid: it becomes
  page chrome (action bar, caption, LIVE badge, search, New button) hosting one
  `spark-query-grid`. `spark-routes.ts:14,30` is untouched.

  It stays **route-param driven and keeps its resolution logic**. It has no `queryId` input at
  all — it reads `paramMap` — and it serves *two* routes: `query/:queryId`, and `po/:type`,
  which resolves a type to a query through a hand-rolled `singularize` table. That is
  type-to-query resolution, not query rendering, and it does not belong in a grid.
- Both new pieces live in the existing **`@mintplayer/ng-spark/grid`** entry point, which already
  holds the shared grid core. `po-detail` re-exports nothing grid-shaped any more.

Blast radius is small and was checked: `spark-sub-query` has exactly **one** consumer,
`spark-query-list` is reached only through `spark-routes.ts`, and **no demo app references
either component by tag.**

## 6. Breaking changes

Nothing is released yet from this branch, and both are ng-spark-internal:

1. `SparkSubQueryComponent` is removed from `@mintplayer/ng-spark/po-detail`.
2. `showCard` and `headerTemplate` are removed with it.

Per `CLAUDE.md`, ng-spark's major tracks **Angular**, not our API: this ships as
`22.3.0 → 22.4.0`, with the break described in the release notes. NuGet packages are
untouched by this work.

## 7. Loose ends folded in

Both found while verifying the above. Neither is large, and the one-PR rule applies.

1. **`SelectionRuleParser.IsValid` is never called.** Its own doc comment says "call at
   configuration load so a typo fails loudly at startup", and `guide-custom-actions.md:141`
   promises "a malformed rule is a startup error, not a silent permit". Both are false today:
   the only callers are tests, and a malformed rule instead throws `FormatException` out of
   `ExecuteCustomAction.cs:125` as a **500 at execute time**. Wire it into the custom-actions
   configuration load so the documented contract holds.
2. **A sub-query resolves its entity type differently from the page.** `spark-sub-query`
   matches on `query.entityType` only; `spark-query-list` falls back to source name and
   `singularize`. So a `Database.*` query with no explicit `entityType` renders as a page and
   is **blank as a sub-query** — no columns, no rows, no error. Unifying the grid fixes this by
   construction, which is the whole argument for doing it: this is the fourth instance of
   exactly the drift the shared helpers were extracted to stop.
3. **`SPARK_GRID_PAGE_SIZES` has one consumer.** `spark-query-list` hard-codes `[10, 25, 50]`
   instead. The constant was created so the two could not disagree, and one of them ignores it.
4. **`docs/Spark-API-Specification.md`** must not describe `actions` / `headerRenderer` /
   `headerRendererOptions` / `rowsNavigable`; and `guide-custom-actions.md` must keep master's
   post-#310 wording. Verified: neither currently does — hold the line, don't reintroduce.

## 8. Out of scope — genuinely not being done

- Reviving anything from the #308 chrome design (§4).
- Changing the query/streaming wire protocol, `security.json` semantics, or the row link rule.
- A tree/grouping mode for the grid. `bs-datatable` supports it; no query declares it.

## 9. Acceptance

- The auto-rendered sub-query on a PO detail page renders **identically** to `cc18aa3`, with no
  host template supplied.
- Each slot, supplied alone, overrides only its own region.
- `/query/:alias` keeps its action bar, caption, LIVE badge, search box, and **both** the
  streaming and paged branches.
- The column loop, row template, link gate and cell renderer dispatch exist in **one** file.
- A malformed `selectionRule` fails at startup, not on execute.
- Existing `spark-query-list` and `spark-sub-query` specs are carried over onto whichever
  component now owns the behaviour, not dropped.
