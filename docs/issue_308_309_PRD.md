# PRD — a query declares its own chrome: `spark-sub-query` becomes reusable without a host

**Status:** Planned (v2 — the projection design of v1 is withdrawn; see §Superseded)
**Issues:** [#308](https://github.com/MintPlayer/MintPlayer.Spark/issues/308) (implemented), [#309](https://github.com/MintPlayer/MintPlayer.Spark/issues/309)
**PR:** [#308](https://github.com/MintPlayer/MintPlayer.Spark/pull/308) — grows to cover both
**Branch:** `fix/parentless-sub-query`
**Plan:** `docs/issue_308_309_plan.md`
**Base:** `master` @ `7ad2e30`
**Release:** `10.0.0-preview.61` + `@mintplayer/ng-spark@22.3.0` — **both mandatory**
**Breaking changes:** allowed and used (libraries are in preview)

---

## Problem

`SparkSubQueryComponent` was written for exactly one caller: `spark-po-detail`, which stacks
N sub-queries under a persistent object. Every assumption of that caller is hardcoded, so
the component is unusable anywhere else — and each way it fails is **silent**.

1. **It required a parent.** Fixed in #308; unreleased.
2. **It owns the card and the header.** A hardcoded `<bs-card>` with
   `<bs-card-header>{{ description || name }}</bs-card-header>`, and **no way for anything
   to put a button there**.
3. **It cannot be refreshed** — the fetch runs in a constructor `effect()`.
4. **A failure is indistinguishable from an empty result**, and often from nothing at all.
5. **It links the first column to `/po/{alias}/{id}`** whether or not the row is a document.

## Origin

[MintPlayer/CodeCoverage](https://github.com/MintPlayer/CodeCoverage) tried to adopt the
component for a standalone "your accounts" grid. Commit `62943e1` had to delete the host's
`<bs-card>`, whose header carried a **Resync** button; `8fa21af` reverted the client half and
filed the blockers. The server half stands.

## Superseded — why v1 was wrong

v1 proposed `<ng-content select="[subQueryHeader]">` so the *host page* could project a
header. The owner rejected it: the frontend↔backend interaction must be **reusable and
generic**.

That objection is correct, and v1 refutes itself. Its own §D5 argued *"a client input is not
the mechanism — `spark-sub-query` is auto-rendered from `EntityTypeDefinition.Queries` by
`spark-po-detail.component.html:174-178`, so in the common case there is no host to ask."*
That reasoning applies verbatim to `<ng-content>`. **A projection slot serves only
hand-instantiated hosts and leaves the majority call site — the auto-rendered one — with no
way to declare a header at all.**

What replaces it: **the query declares its chrome, server-side.** The header follows the
query wherever it is rendered, with or without a host.

## Investigation findings

Six investigations. Everything below was read in the code.

### F1 — The host wants the card with a *different header*; but the header belongs to the query

Coverage's target shape (`home.component.html:31-41`) is a card whose header holds a title
and a Resync button. The v1 reading ("no card" is a workaround for "I cannot control the
header") stands; the inference to projection does not.

The distinction that matters: the reauth `<bs-alert>` and install-hint paragraph are **page**
chrome and stay on the host page. Only the **caption and the Resync button** are query
chrome, and those are exactly what should move server-side.

### F2 — Custom actions already support `showedOn: "query"`. The client filters on `"list"`.

This is the finding the whole redesign turns on.

- `Models/CustomActionDefinition.cs:14-17` documents three values: **`"detail"`, `"query"`,
  `"both"`**. `docs/guide-custom-actions.md:111` says the same.
- `spark-query-list.component.ts:171` filters
  `a.showedOn === 'list' || a.showedOn === 'both'`.

**An action authored per the documentation renders nowhere.** That is a live bug, not a
missing feature. Fixing it is breaking in name only — nothing can regress, because `'query'`
currently renders in zero places.

Further, the framework already presumes query-scoped actions exist:
`Abstractions/ClientOperations/IClientAccessor.cs:62` is
`DisableQueryActions(string queryId, params string[] actionNames)` — with no client handler,
so it is inert today.

Dispatch already handles the parentless, selectionless case:
`spark-query-list.component.ts:181` calls `executeCustomAction(entityType.id, action.name)`
with no parent and no selection, and `ExecuteCustomAction.cs:108-131` skips the parent reload
when nothing is submitted.

What is genuinely missing is only: **per-query scoping** (`/spark/actions/{objectTypeId}` is
entity-type-keyed, so every query over an entity sees an identical list) and **rendering in
`spark-sub-query`**, which contains zero occurrences of `action` today.

### F3 — Coverage's Resync *is* a custom action, not markup

`Coverage/Controllers/MeController.cs:93-102`:

```csharp
[HttpPost("accounts/resync")]
public async Task<ActionResult<AccountsResponse>> Resync(CancellationToken ct) {
    await gitHubAccess.InvalidateAsync(ct);
    await userAccess.InvalidateAsync(ct);
    return await GetAccounts(ct);
}
```

Two cache invalidations and a re-read — `ICustomAction.ExecuteAsync` with
`refreshOnCompleted: true`, verbatim. It maps onto `MyAccountRowActions`, the class that
already owns `Custom.My_Accounts`. Once it is a Spark action, the bespoke controller route,
the client `resync()` method **and the `gridEpoch` remount hack all delete themselves** — and
the button appears in the auto-rendered case too.

### F4 — The `@if (query())` gate makes the component invisible

`spark-sub-query.component.html:1` gates the **entire** template on `query()`, set only at
`.ts:94`, after the awaits. So: the spinner at `.html:4-7` is **unreachable on first load**;
a first-load failure (`.ts:124-128`) renders **zero DOM**; and a *re-load* failure leaves
stale chrome, because `loadData` resets `resultCount`/`fetchFn` (`.ts:86-87`) but not
`query`. One structural fix resolves all three.

### F5 — The two grid components have diverged, and the drift has produced eight bugs

~120 of `spark-sub-query`'s 180 TS lines exist verbatim in `spark-query-list`:
`getColumnRenderer*`, `loadLookupReferenceOptions`, `visibleAttributes`,
`isVirtualScrolling`, the `DatatableSettings` construction, and the `#cellContent` template
are byte-identical. `spark-query-list.component.html:87-104` and `:119-136` are an exact copy
of each other **inside the same file**.

| # | Drift bug | User-visible |
|---|---|---|
| D-1 | `spark-query-list.onParamsChange` (`:87-91`) is `async`, called from `subscribe`, **no try/catch**. The deliberate 404 rejects into nothing → `entityType()` stays null → **spinner forever**. It *has* an `errorMessage` surface; the metadata load never reaches it. | **yes** |
| D-2 | `spark-sub-query` never binds `[indeterminate]` (present at `query-list.html:152`) → a null boolean renders as unchecked, indistinguishable from `false`. | **yes** |
| D-3 | `spark-sub-query` never resets `entityType`/`query`/`canRead` on reload → switching `queryId` can build `/po/{previous type}/{new id}` with the **previous** permission. | **yes** |
| D-4 | `spark-query-list` never resets `canRead`/`canCreate`/`customActions` on route change → A's buttons survive onto B. | **yes** |
| D-5 | `spark-query-list` sets `allEntityTypes` only when a type resolved → cells render against an empty type list in the partial window. | minor |
| D-6 | `spark-sub-query`'s fetch catch returns an empty page; the sibling's identical code sets `errorMessage`. Same code, one line, opposite UX. | **yes** |
| D-7 | `VirtualScrolling` is sized only in `query-list` (host class + `.scss`); sub-queries silently get ng-bootstrap's 480px default. | **yes** |
| D-8 | `spark-query-list.refresh()` is **private** (`:269`) — the exact mechanism #309(2) asks for, unreachable. | — |

### F6 — `resultCount` is dead in `spark-sub-query`

Commit `05a1404` removed its only template consumer. Written at `.ts:138`/`:147`, read only
by the spec. The fetch catch writes to nothing: it is a pure swallow.

### F7 — Both missing behaviours already have house conventions

Refresh: *re-seed by reassigning a signal, never call a method on a child* —
`spark-query-list.component.ts:268-276`. **Zero** `viewChild` in the library.
Errors: an `errorMessage` signal in a `bs-alert`, chain
`e.error?.error || e.message || <fallback>`, in **11 templates**.
`BsDatatableComponent` has **no** `reload()`; assigning `fetch` is the only refetch, and its
setter resets `_initialFetchDone`, defeating its own dedupe.

**One-off host chrome has a house convention too, and it is not `<ng-content>`:**
`spark-po-detail.component.ts:57-58` exposes `extraActionsTemplate`/`extraContentTemplate` as
`TemplateRef` inputs, mirrored at `spark-query-list.component.ts:57`. Match that.

### F8 — The 404 on a denied query is deliberate. #309(3) is client-side only.

`Endpoints/Queries/Get.cs:23`: *"Return 404 (not 403) when the caller isn't authorized — so
existence isn't leaked."* Both 404 bodies byte-identical on purpose; introduced by `ae37fed`
(PR #155) as audit remediation **M-3**; pinned by `Security/NotFoundVsForbiddenTests.cs` and
`MetadataEndpointAuthTests.cs:56-66`. The audit marks M-3 PARTIAL and names the *remaining*
403s as the defect.

**Changing it would be a security regression.** Consequence: the component cannot distinguish
denied from missing, so its 404 message must be **generic**, and a login hint may only come
from a channel that does not vary per query id.

`SparkService` is a bare `firstValueFrom` passthrough with no interceptor, so the rejection
**is** the raw `HttpErrorResponse` — the component is discarding information it already has.

### F9 — The first-column link produces nested anchors; the reported workaround does not work

`#cellContent` (hosting `*ngComponentOutlet`) is projected **into** the anchor
(`spark-sub-query.component.html:27-30`). Coverage's `account-login` renderer therefore emits
a valid `/a/{login}` anchor **inside** a dead `/po/{alias}/{id}` one, and `canRead()` is true
so the wrong outer link is live. Nested `<a>` is invalid HTML.

> #309 records that renderer as the workaround. Two independent readings confirm it cannot
> be. Recorded here as **ineffective**, not as prior art.

Duplicated in **three** sites: `spark-query-list.component.html:92-95`, `:124-127`,
`spark-sub-query.component.html:27-30`. (`:92-95` and `:124-127` are the same-file copy from
F5 — collapsing that first reduces this to two.)

### F10 — Navigability is undecidable except by the query author

No flag expresses "these rows have no detail page". `QueryType`/`IndexName` mean "projected
list of *real* documents"; `InCollectionType`/`InQueryType` are per-attribute; `[FromIndex]`
types are skipped by `ModelShapeDiscovery` so they can never *be* a query's `entityType`.

The real failure is a **registered type whose rows are fabricated** —
`StockActions.cs:39` builds `new Stock { Id = $"stocks/{symbol}" }`; `Stock` is a queryable
root, so `canRead` is true and `/po/stock/stocks/AAPL` 404s. Same in
`ProjectColumnActions.cs:15-27`.

**`Custom.*` is a false friend in both directions:** Fleet's `Custom.Stolen_Cars`,
`Custom.Recent_Cars` and `Custom.Company_People` return real loadable documents;
`Custom.StreamItems` and `Custom.GetProjectColumns` fabricate. Only the reverse holds —
`Database.*` ⇒ ids are real.

### F11 — The wire is free; the round-trip is not

`Endpoints/Queries/List.cs:29` and `Get.cs:61` are `Results.Json(...)` over the domain
objects — no DTO. **A C# property plus a TS line is the whole wire change.** Three caveats:

- **`Endpoints/Queries/Execute.cs:112-123` is a hand-written clone of `SparkQuery`** that
  already silently drops `Description`. New fields are dropped there too. Harmless for
  presentation-only fields, but a landmine — it goes on the checklist.
- **New fields must be nullable.** `ModelSynchronizer.cs:29-35` writes with
  `DefaultIgnoreCondition = WhenWritingNull`; a non-nullable `string[] Actions = []` would
  stamp `"actions": []` into all 23 demo model files.
- **There is no JSON schema to update** — no `*.schema.json`, no `$schema`, and
  `extensions/vscode` has zero tracked files.

Synchronize **preserves** declared fields on both `Database.*` and `Custom.*`:
`CollectQueriesFor` (`ModelSynchronizer.cs:407-422`) returns the same object references, and
all three mutating passes are `Database.`-filtered (`:124`, `:135-136`, `:159-183`). Already
pinned by `ModelSynchronizerTests.cs:961-974`. Hash-neutral — `ModelFileShape.cs:115-129`
hashes only `name` + `indexName`.

> ⚠️ **The trap, and it is fatal:** there is **no `[JsonExtensionData]` anywhere in the
> repo**. A JSON property not declared on `SparkQuery` is destroyed on the *first*
> synchronize, and runs 2 and 3 are then byte-identical — **the loss is itself a fixed
> point**, invisible to `SynchronizeIdempotencyTests` and to `--spark-verify-model`. Not
> hypothetical: #279 deleted `SparkQuery.UseProjection` and the next synchronize stripped
> `"useProjection": false` from 17 places. Every field below must be a real C# property
> before any model file mentions it.

Second trap: `CollectQueriesFor` filters on `query.EntityType == entityTypeName` (`:414`), so
a `Custom.*` query with **no `entityType`** is dropped from the rewritten file entirely.
Coverage's `My_Accounts` sets one, so it is safe.

### F12 — Release path

`ng-spark` is at 22.2.0 (also newest on npm); #308 carries **no** bump. Major stays 22 per
`CLAUDE.md`; additive → **22.3.0**. The server is now touched, so **`preview.61` is
mandatory** (20 `.csproj` files).

`npm-publish@v4` **no-ops on an already-published version**, so a forgotten bump is a *green
run that publishes nothing*. `ng-spark-auth` does not depend on ng-spark and needs no bump.
No demo declares an ng-spark range. `package-lock.json` records a stale `22.0.8`.

## Requirements

| # | Requirement | Source |
|---|---|---|
| R1 | **A query declares its header content and actions server-side**, so the header is right where `spark-sub-query` is auto-rendered and no host exists | owner, F2 |
| R2 | An action authored with the documented `showedOn: "query"` renders | F2 |
| R3 | A host may still suppress the card entirely, keeping a working grid *and* spinner | #309(1) |
| R4 | A hand-instantiated host may override the header for a one-off, via the house `TemplateRef` idiom | F7 |
| R5 | The component is visible while loading, on first load | F4 |
| R6 | A failed load renders a visible, intelligible message — never zero DOM | #309(3) |
| R7 | A failed page-fetch is distinguishable from an empty result | #309(3) |
| R8 | A host can re-run the query without destroying the component | #309(2) |
| R9 | Refresh must not reset the user's page, sort, or scroll | F7 |
| R10 | The first-column link is absent when the query's rows are not documents | #309(4) |
| R11 | No user-visible drift bug (F5) survives in a path this PR touches | F5 |
| R12 | The 404-on-denied contract is preserved exactly | F8 |

## Design

### D1 — One `SparkQuery` change, four fields

All nullable, all JSON-authored, all real C# properties (F11):

```csharp
public string[]? Actions { get; set; }                            // per-query action allowlist
public string? HeaderRenderer { get; set; }                       // registered chrome component
public Dictionary<string, object>? HeaderRendererOptions { get; set; }
public bool? RowsNavigable { get; set; }                          // F10
```

Mirrored in `models/src/spark-query.ts`. One edit, one `preview.61`, one round of model-file
churn.

- **`Actions`** — `null` means today's behaviour (every entity-type action whose `showedOn`
  includes the query side). A list narrows *display* to those names.
  ⚠️ **`Actions` is not an authorization boundary.** The grant is the gate, enforced
  independently at `ExecuteCustomAction.cs:52` regardless of which query the caller clicked
  from. A caller can always POST directly. (Same class as "a scoped context property is not
  an authz boundary".)
- **`RowsNavigable`** — `Database.*` → true; `Custom.*` → true unless explicitly false.
  **`Custom.*` must not default to false**: that silently kills the working links on
  `Stolen_Cars`, `Recent_Cars` and `Company_People`.

### D2 — Query actions render in the sub-query header

Fix the filter to `a.showedOn === 'query' || a.showedOn === 'both'` (F2 — breaking in name
only), and render the action bar in `<bs-card-header>` reusing the `bs-priority-nav` loop
from `spark-query-list.component.html:9-15`.

This is the mechanism that satisfies R1: it needs **no host cooperation at all**, so it works
in the auto-rendered po-detail case.

### D3 — `headerRenderer`, a registry symmetric with attribute renderers

```ts
export interface SparkQueryChromeRegistration { name: string; headerComponent: Type<any>; }
export const SPARK_QUERY_CHROME = new InjectionToken<SparkQueryChromeRegistration[]>(
  'SparkQueryChrome', { factory: () => [] });
export function provideSparkQueryChrome(items: SparkQueryChromeRegistration[]): Provider;
```

Shaped exactly like `SPARK_ATTRIBUTE_RENDERERS` (`renderers/src/spark-attribute-renderer-registry.ts:3-35`),
including `factory: () => []` so a host that registers nothing is not a special case.
Resolved through `withDeclaredInputs`, since `NgComponentOutlet` throws on undeclared inputs.
`reload` is passed as an **input callback**, matching
`SparkAttributeEditRenderer.valueChange` (`spark-attribute-renderer.ts:66`) — outputs are not
available through `NgComponentOutlet`.

When set, `headerRenderer` **replaces the whole header** (caption *and* action bar), so the
two mechanisms never fight.

### D4 — Precedence, stated once

```
headerRenderer  →  headerTemplate  →  (description || name) + declared actions
```

`headerTemplate = input<TemplateRef<{$implicit: SparkQuery}> | null>(null)` covers the
hand-instantiated one-off (R4), matching `extraActionsTemplate` — **not** `<ng-content>`.
`showCard = input(true)` is retained unchanged for the genuinely bare embed (R3); its bare
branch must emit the spinner **and** the body div, because the spinner is the body div's
*sibling*.

### D5 — Restructure the template around explicit states

```
[error alert]          ← always, outside every gate
@if (loading())        { spinner }
@else if (query())     { header (D4) + body }
@else if (errorMessage()) { card shell + message }
```

The alert **must** sit outside the `query()` gate or R6 is unmet. This is what makes the
spinner reachable and the failure visible, and it is a prerequisite for a correct bare
branch.

### D6 — Errors: the house convention plus an output

`errorMessage` signal set in **both** catches (which stop being bare `catch {}`), chain
`e.error?.error || e.message || t(fallback)` with the fallback through
`SparkLanguageService`; **404 → generic message** (F8); `error = output<HttpErrorResponse>()`
for hosts in bespoke chrome. **No toast** (reserved for server `notify`; only 1 of 4 demos
mounts a container). **No retry modal** (that is the HTTP-449 protocol). Delete `resultCount`
(F6).

### D7 — Refresh: `reload()` and `reloadToken`, both data-level

| Level | Re-runs | Cost |
|---|---|---|
| **Data** | `executeQuery` — `fetchFn.set(makeFetch(...))` | 1 request; keeps page, sort, scroll |
| **Metadata** | `getQuery` + `getEntityTypes` + `getPermissions` + lookups | 4+; **resets page and sort** |

`reload()` public and data-level; `reloadToken = input<unknown>(null)` read in a **second**
effect that skips its first run. The existing effect must not read it, or the token triggers
the expensive reload and silently resets the user's page (R9). `spark-query-list.refresh()`
becomes public and is renamed `reload()` so both components agree (D-8).

### D8 — All eight drift fixes, and the grid core that prevents the ninth

**All of D-1 … D-8 land here.** D-1 in particular: shipping a PR whose thesis is "a failed
load must never render nothing" while the sibling renders a permanent spinner **on the same
404** would be incoherent.

D-4, D-5 and D-7 are refactor-shaped, which is why they come with the refactor:

### D9 — One grid core, consumed by both components

A new leaf entry point `@mintplayer/ng-spark/grid`:

```ts
export function injectSparkGrid(source: SparkGridSource): SparkGridState;
export interface SparkGridState { /* query, entityType, visibleAttributes, permissions,
  settings, fetchFn, loading, error, errorMessage, reload(), reloadMetadata() … */ }
@Component({ selector: 'spark-grid-rows' }) export class SparkGridRowsComponent { … }
```

`injectSparkGrid` becomes **the only writer of the reset sequence** — one
`resetForNewSource()` clearing *every* derived signal, and one `try/catch` around the whole
metadata load. D-1, D-3, D-4, D-5 and D-6 then stop being five fixes and become one
invariant, and the ninth drift bug cannot be written.

**Not one merged component.** `spark-query-list` is route-coupled (`route.paramMap.subscribe`)
and carries streaming, search and a websocket dependency graph; merging would drag all of it
into every detail page's bundle and produce a nine-input component whose valid combinations
are not orthogonal. Two thin presentational shells over one headless core.

Shared SCSS (the virtual-scrolling sizing that fixes D-7) goes to `styles/_grid.scss`,
following the existing `@use '../../styles/actionbar';` precedent — `styles/` has no
`ng-package.json`, so it is not an entry point and a relative `@use` is correct.

Cross-entry-point imports are already the norm here (`po-detail` imports
`@mintplayer/ng-spark/{services,pipes,renderers,icon,models}`), and `tsconfig.base.json:6`
maps `@mintplayer/ng-spark/*` by wildcard, so a new `grid/` folder needs **zero config**. The
shared code must **not** live in `query-list` — that would create `po-detail → query-list` and
drag the websocket graph along with it.

## Decisions

| Decision | Why |
|---|---|
| **Query-declared chrome, not host projection** | Owner's requirement; and projection cannot serve the auto-rendered call site, which is the majority |
| Actions are the primary mechanism; `headerRenderer` secondary | F3 — Coverage's Resync is an action with a server handler, not markup. Arbitrary client markup is also unauthorizable |
| Fix `showedOn` to `'query'` rather than change the server to `'list'` | F2 — the server model and the guide already say `'query'`; the client is the outlier |
| `headerRenderer` ships anyway | ~15 lines, symmetric with the named precedent, and it permanently removes the pressure to re-add projection |
| `headerTemplate` as `TemplateRef`, not `<ng-content>` | F7 — `extraActionsTemplate` is the house idiom for one-off host chrome |
| `showCard` retained | A genuinely bare embed is a real, different use case |
| No `showHeader` | Redundant once the header is declarable; a 4-way matrix with no caller |
| All four `SparkQuery` fields in one change | F11 — one `preview.61`, one model-file churn, one synchronize risk |
| `Custom.*` defaults to navigable | F10 — the opposite breaks three working Fleet/HR queries |
| `Actions` narrows display only | Authorization stays at the grant; the allowlist is not a gate |
| Fix #309(3) purely client-side | F8 — the 404 is a named remediation with tests pinning it |
| **Grid-core unification ships here, not in a follow-up** | Owner: one PR. Every extra PR is another full round of workflow runs, and waiting on CI is the bottleneck. Size is not a reason to split |
| Two shells over one headless core, not one merged component | D9 — merging drags route-coupling, streaming and websockets into every detail page |

## Acceptance criteria

1. `<spark-sub-query queryId="x">` with no parent loads and renders. *(#308, done)*
2. **A query declaring an action with `showedOn: "query"` renders that action in its header
   when auto-rendered by `spark-po-detail`, with no host cooperation.** *(The criterion the
   redirection exists for.)*
3. A query with `actions: ["X"]` shows only X; with `actions: null`, today's set.
4. Executing a query action with no parent and no selection succeeds and refreshes the grid
   via `refreshOnCompleted`.
5. A registered `headerRenderer` replaces caption *and* action bar; unset, the caption is
   byte-identical to today.
6. `headerTemplate` overrides the caption for a hand-instantiated host.
7. `[showCard]="false"` renders grid **and** spinner, with no `bs-card` in the DOM.
8. A spinner is visible during the **first** load. *(Fails today.)*
9. A first-load failure renders a visible alert. *(Renders zero DOM today.)*
10. A page-fetch failure renders an alert, not an empty grid.
11. A 404 renders the generic message and emits the `error` output.
12. `reload()` and a `reloadToken` bump re-fetch without changing page or sort.
13. `rowsNavigable: false` renders no first-column anchor, in every remaining site.
14. **D-1:** a denied query on `spark-query-list` renders an alert, not a permanent spinner.
15. **D-2:** a null boolean renders indeterminate in a sub-query.
16. **D-3:** switching `queryId` never emits a link built from the previous type/permission.
17. **D-4:** navigating between query routes never carries the previous route's action buttons
    or `canCreate` onto the next.
18. **D-7:** `renderMode: VirtualScrolling` sizes identically in a sub-query and a query list.
19. Both components' loading pipelines route through `injectSparkGrid`; the reset sequence and
    the metadata `try/catch` exist in exactly **one** place.
20. A server-emitted `refreshQuery` operation refreshes the grid; `refreshOnCompleted` on a
    po-detail action refreshes its sub-queries.
21. `spark-po-detail`'s stacked sub-queries are visually unchanged apart from a new action bar
    where a query declares one.
22. `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` pass **unmodified**.
23. A double `--spark-synchronize-model` leaves the four new fields byte-identical.
24. `npm view @mintplayer/ng-spark version` reports `22.3.0`; NuGet reports `preview.61`.

## Migration

**In-repo:** none forced. Every new field is nullable and every new input defaulted. The one
behaviour change is the `showedOn` filter — and no action anywhere uses `'query'` today, so
nothing moves. Demos gain `rowsNavigable: false` on `Stock.json` and `ProjectColumn.json`,
which currently render dead links.

**Coverage — this is a cross-repo migration, not a template edit:**

1. Delete `MeController.Resync` (`:93-102`); re-home it as an `ICustomAction` on
   `MyAccountRowActions` with `showedOn: "query"` and `refreshOnCompleted: true`.
2. Add a `ResyncAccounts/MyAccountRow` grant to `security.json`, beside the existing
   `QueryRead/MyAccountRow` grant on `authenticated`.
3. Set `rowsNavigable: false` on `My_Accounts` (`MyAccountRow.json`).
4. Delete the client `resync()` method **and** the `gridEpoch` remount hack.
5. Replace the hand-rolled card body with `<spark-sub-query queryId="my-accounts" />` inside
   the page's own card — or drop the card and let the query own it.
6. Keep the reauth alert and install-hint paragraph on the page: they are page chrome.

## Also in scope — everything this work uncovered

This PR is the single unit of work. Nothing related is deferred to a follow-up; every extra
PR costs another full round of workflow runs.

- **Grid-core unification** — §D9. `@mintplayer/ng-spark/grid`, both components reduced to
  chrome, D-4/D-5/D-7 falling out of the single reset path and shared SCSS. ~165 duplicated
  lines removed, ~150-250 spec lines rewritten.
- **`refreshQuery` client handler** — the server can already emit the operation
  (`client-operations/src/operations.ts:37-40`) but `provide.ts:14-30` wires only `notify`, so
  it is silently dropped. `reload()` is the missing piece; wire it.
- **`DisableQueryActions` client handler** — `IClientAccessor.cs:62` has no handler either. A
  server that can disable a query action the client always renders is a visible
  inconsistency once query actions ship. Wire it, or make the no-op explicit in code.
- **`spark-po-detail` does not refresh its sub-queries** after `refreshOnCompleted`
  (`:248-251` refreshes only the PO). Once `reload()` exists, it must.
- **`docs/guide-custom-actions.md:159`** claims actions are "available to all users" —
  contradicted by the deny-all default at `PermissionService.cs:9-13`.
- **`Endpoints/Queries/Execute.cs:112-123`** hand-clones `SparkQuery` and already drops
  `Description`. Delete it in favour of the real object rather than extending the clone.
- **`docs/Spark-API-Specification.md:470-483`** still documents `useProjection`, deleted in
  #279.
- **`[object Object]`** from a missing `| resolveTranslation` in
  `spark-po-edit.component.html:5` and `spark-po-create.component.html:8`.
- **Coverage's migration** (§Migration) is part of this unit of work, not a later chore. It
  cannot compile until `preview.61` and `22.3.0` publish, so it lands as one PR in that repo
  immediately after this one publishes — planned here, executed there.

### Genuinely out of scope

- **`selectionRule` is transported (`custom-action.ts:9`) but never evaluated**, and
  `selectedItems` is never populated by the query list. That is unimplemented feature work
  with no caller, not a defect in this path — a query action needs no selection.
- **M-3 is still PARTIAL** — `PersistentObject/Get.cs:39-49` and `Queries/Execute.cs:128-138`
  still return 403 where the audit wants 404. A security change to endpoints this PR does not
  otherwise touch, tracked by its own audit.
