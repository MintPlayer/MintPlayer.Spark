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
It owns: query and entity-type resolution, permissions, lookup options, paging/sorting,
**streaming**, the fetch closure, the error alert, the row link gate, and selection state.

Inputs: `queryId` (required), `parentId`, `parentType`, `reloadToken`, `search`.
Outputs: `error`, and a `query`/`entityType`/`customActions`/`selection` surface the card reads.

Streaming is included deliberately. Leaving it in `spark-query-list` would mean the shared
component cannot serve the page, and the duplication survives under a new name.

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

## 5. What happens to the existing components

- **`spark-sub-query` is deleted.** It becomes `spark-query-card` with a `parentId`/`parentType`.
  The single call site (`spark-po-detail.component.html:176`) is updated. Its `showCard` input
  disappears — a host that wants no card uses `spark-query-grid` directly, which is what
  `showCard="false"` meant. `headerTemplate` disappears, replaced by the three slots.
- **`spark-query-list` keeps its name, selector and route**, and loses its grid: it becomes the
  page chrome (action bar, caption, LIVE badge, search) hosting one `spark-query-grid`. The
  route in `spark-routes.ts:14,30` is untouched.
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
2. **`docs/Spark-API-Specification.md`** must not describe `actions` / `headerRenderer` /
   `headerRendererOptions` / `rowsNavigable`; and `guide-custom-actions.md` must keep master's
   post-#310 wording. Verify, don't assume.

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
