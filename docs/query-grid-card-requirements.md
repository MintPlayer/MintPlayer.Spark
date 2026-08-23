# Requirements as given — `spark-query-grid` / `spark-query-card`

Captured verbatim from the owner before investigation, so the detail survives context compaction.
The PRD and plan derive from this; **this file is the record of what was asked for**, not a design.

Branch: `feat/spark-query-grid-card`, cut from `master` at `cc18aa3` (the merged #310/#311).

---

## Context

PR **#308** (`spark-sub-query becomes embeddable`) is being re-reviewed and **will not be merged as
it stands**. Its non-authorization fixes already came across via #311's M1. What remains of it is
being reconsidered from scratch.

## The four comments on #308

1. **`rowsNavigable` — not needed.** Click-through is fully managed through `security.json`:
   `Query` without `Read` lists rows and withholds the link. Already dropped in #311; do not
   reintroduce.

2. **`selectionRule` — will need to be shipped next.** It exists on the #308 branch (server-side
   `SelectionRuleParser`, the client port, one shared fixture driving both, enforcement, the
   ceiling, and the row-action gate) but did **not** come across in #311. It has to ship.

3. **`docs/guide-custom-actions.md` — "looks interesting too."** The #308 branch has changes to it.
   Worth reviewing for what should carry over. ⚠️ Intent not fully specified — confirm before
   acting on more than a review.

4. **All the sub-query work from #308 is to be revised — "we don't need any of that."**
   Replace it with two components in the `ng-spark` library:

   - **`spark-query-grid`** — a `<bs-datatable>` that renders **a query or a sub-query**.
   - **`spark-query-card`** — a `<bs-card>` containing a `<spark-query-grid>`, with
     **structural-directive template slots** so a host can modify parts of the card's chrome.

   Named slots given as examples:
   - the **icon in the header-left** — e.g. `<span *bsQueryIcon>`
   - the **buttons in the header-right** — e.g. `<span *bsQueryActionButtons>`

   **Directive names are mine to choose.** The `*bs…` prefix in the examples belongs to
   `@mintplayer/ng-bootstrap`; these are `ng-spark` directives, so `*spark…` is likely the correct
   prefix — confirm rather than assume.

   The pattern to follow is the one used at
   `C:\Repos\mintplayer-ng-bootstrap\apps\ng-bootstrap-demo\src\app\pages\enterprise\datatables\datatables.component.html:32`
   (`*bsDatatableColumn`-style structural directives as named slots).

## What this replaces

The #308 design had the **query declare its own chrome server-side** (`SparkQuery.actions`,
`headerRenderer`, `headerRendererOptions`, plus a `SPARK_QUERY_CHROME` registry). That was chosen
because a sub-query is auto-rendered from `EntityTypeDefinition.Queries`, where no host exists to
project into. **The new direction is host-side structural directives instead** — so the
investigation must establish how the auto-rendered call site is served, since that was the original
objection to a host-side mechanism.

## Open questions for the investigation

- How does `spark-query-grid` serve the **auto-rendered** sub-query case, where there is no host to
  supply templates? (This is the objection that killed the host-side design in #308 v1.)
- What is the relationship to the **existing** `spark-query-list` and `spark-sub-query` components,
  and to the shared `@mintplayer/ng-spark/grid` entry point that #311 already shipped?
- Do the two new components **replace** those, or sit alongside them?
- Which parts of #308's client work are still wanted (the three-state template, the permanent-spinner
  fix, `reloadToken`, `[indeterminate]`, the `showedOn: 'query'` filter) versus already shipped in
  #311's M1?

---

## Verified findings — appended after investigation (2026-08-23)

The captured text above is left exactly as given. This section records where the tree
disagreed with it. Design consequences live in
[`query-grid-card-PRD.md`](./query-grid-card-PRD.md).

**Comment (2) is already satisfied. `selectionRule` came across in #311 in full.** Server
(`SelectionRuleParser`, `ExecuteCustomAction` enforcement, the 200-item ceiling,
`ListCustomActions` projection, `CustomActionDefinition.SelectionRule`) and client
(`selection-rule.ts`, the shared `selection-rule.fixture.json`, `selection-mode.ts`,
`selection-rule.spec.ts`, and the wiring in **both** grids) are byte-identical to
`fix/parentless-sub-query`, and `release-notes-preview-62.md:147` documents the release.
Nothing to ship. There is nothing to recover from that branch at all.

**Comment (1) is correct and already actioned.** `rowsNavigable` appears nowhere in `libs/`
or `Demo/`.

**Comment (3) needs no change.** The #308 branch's edits to `guide-custom-actions.md` are all
on master via #311's M1. The single branch-unique line documents `spark.AddAuthorization()`
and `spark.AllowAnonymousAccess()` — both **deleted by #310**. It must not be carried over.

**Comment (4) is the whole of the remaining work**, and is what the PRD covers.

### Answers to the open questions above

- **The auto-render case is served because slots override defaults rather than replacing
  them.** An absent slot renders the built-in default, so the auto-rendered call site is
  unchanged. See PRD §4.
- **The `*bs` prefix was confirmed to be ng-bootstrap's**, as suspected. The directives are
  `*sparkQueryIcon`, `*sparkQueryCaption`, `*sparkQueryActions`.
- **The two components replace both existing ones.** `spark-sub-query` is deleted;
  `spark-query-list` keeps its name and route and becomes page chrome over the shared grid.
- **Every M1 item listed as possibly-outstanding is already on master** — the three-state
  template, the permanent-spinner fix, `reloadToken`, `[indeterminate]`, the `showedOn: 'query'`
  filter.
