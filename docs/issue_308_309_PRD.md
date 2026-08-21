# PRD — `spark-sub-query` becomes embeddable: the host can own the chrome, the refresh, and the failure

**Status:** Planned
**Issues:** [#308](https://github.com/MintPlayer/MintPlayer.Spark/issues/308) (already implemented), [#309](https://github.com/MintPlayer/MintPlayer.Spark/issues/309)
**PR:** [#308](https://github.com/MintPlayer/MintPlayer.Spark/pull/308) — grows to cover both
**Branch:** `fix/parentless-sub-query`
**Plan:** `docs/issue_308_309_plan.md`
**Base:** `master` @ `7ad2e30`
**Release:** `@mintplayer/ng-spark@22.3.0` (minor — additive), `10.0.0-preview.61` only if M5 ships

---

## Problem

`SparkSubQueryComponent` was written for exactly one caller: `spark-po-detail`, which
stacks N sub-queries under a persistent object. Every assumption of that caller is
hardcoded, so the component is unusable anywhere else — and each way it fails is
**silent**.

1. **It required a parent.** `parentId`/`parentType` were `input.required`, and the load
   effect guarded on `qId && pId && pType`. A parentless grid rendered nothing, with no
   request, no error and no log. Fixed in #308; not yet released.
2. **It owns the card.** A hardcoded `<bs-card style="margin: 1rem 0">` with a
   `<bs-card-header>{{ description || name }}</bs-card-header>`. A host with its own card
   must either nest cards or delete its own — losing whatever the header carried.
3. **It cannot be refreshed.** The fetch runs in a constructor `effect()`, so the only way
   to re-run a query is to destroy and recreate the component.
4. **A failure is indistinguishable from an empty result** — and in the worst case from
   nothing at all.
5. **It links the first column to `/po/{alias}/{id}`** whether or not that row is a
   document with a detail page.

## Origin

[MintPlayer/CodeCoverage](https://github.com/MintPlayer/CodeCoverage) tried to adopt the
component for a standalone "your accounts" grid on its home page. Commit `62943e1` (M4)
made the swap and had to delete the host's `<bs-card>`, whose header carried a **Resync**
button; the button was re-homed into a naked flex div and the owner reported the result as
a regression. Commit `8fa21af` reverted the client half — restoring the card and fixing the
actual reported bug (flex-wrap on the row, text-nowrap on the badge) — and recorded the two
upstream blockers. The server half of M4 was never affected and still stands.

Coverage deliberately did **not** vendor a local copy of the component.

## Investigation findings

Four parallel investigations. Everything below was read in the code, not inferred from the
issue text.

### F1 — The host wants the card. It wants a *different header*.

The shape Coverage is trying to reach is `home.component.html:31-41`: a `<bs-card>` whose
header is a flex row with a title and a Resync button. `showCard=false` reaches it only by
making the host re-implement border, padding, and inter-query margin. **"No card" is a
workaround for "I cannot control the header", not the requirement.**

The host's chrome is also bigger than a header: the page keeps a reauth `<bs-alert>`, an
install-hint paragraph, and a spinner-gated button, and will keep calling
`/api/me/accounts` after adopting the grid (it carries `gitHubAppUrl` and
`gitHubReauthRequired`, which the grid does not provide).

### F2 — The `@if (query())` gate makes the component invisible, and that is the root cause of #309(3)

`spark-sub-query.component.html:1` gates the **entire** template on `query()`, which is
only set at `.ts:94` — after the awaits. Consequences, all verified:

- The spinner at `.html:4-7` is **unreachable on first load**. `loading` starts `true`, but
  it renders inside the gate. There is no state in which a user sees "loading".
- A first-load failure (`.ts:124-128` sets `fetchFn` to null, never sets `query`) renders
  **zero DOM** — no card, no message, nothing. This is also the unauthorised path.
- A *re-load* failure is inconsistent: `loadData` resets `resultCount` and `fetchFn`
  (`.ts:86-87`) but **not `query`**, so the stale card survives and an empty grid renders
  under it. Two different visual outcomes for the same error, decided by whether an earlier
  load happened to succeed.

Fixing the gate is one structural change that resolves #309(3), the invisible spinner, and
the inconsistency at once. It is also what makes a correct `showCard` branch possible —
a naive `@else` that emits only `<div class="p-3">` drops the spinner, because the spinner
is the body div's *sibling*, not its child.

### F3 — `resultCount` is dead

Commit `05a1404` removed its only template consumer. `.ts:53` is written at `:138` and
`:147` and read nowhere but the spec. So `this.resultCount.set(0)` in the fetch catch
writes to nothing: the catch is a pure swallow.

### F4 — Both missing behaviours already have house conventions; this component is the outlier

- **Refresh:** the library's idiom is *re-seed by reassigning a signal, never call a method
  on a child*. `spark-query-list.component.ts:268-276` refreshes by pushing a new fetch
  closure into `fetchFn`. There are **zero** `viewChild`/`@ViewChild` in the entire library.
- **Errors:** `errorMessage` signal rendered as a `bs-alert` danger, with the extraction
  chain `e.error?.error || e.message || <fallback>`. Used in **11 templates** across
  ng-spark and ng-spark-auth, including the sibling `spark-query-list`.

`BsDatatableComponent` has **no** `reload()`/`refresh()` — the only supported same-state
refetch is assigning `fetch`, whose setter resets `_initialFetchDone` and defeats its own
dedupe. (A comment at `mp-datatable.ts:243` references a non-existent `invalidateData()`;
ignore it.)

### F5 — The 404 on a denied query is deliberate. #309(3) must be fixed client-side.

`Endpoints/Queries/Get.cs:23` carries the comment *"Return 404 (not 403) when the caller
isn't authorized — so existence isn't leaked."* Both 404 bodies (`:21`, `:27`) are
byte-identical on purpose. It was introduced by the security-audit commit `ae37fed`
(PR #155) as remediation **M-3** (`docs/prd/PRD-SecurityAudit.md:210,390`), and is pinned by
`tests/.../Security/NotFoundVsForbiddenTests.cs` and
`MetadataEndpointAuthTests.cs:56-66`. The audit's status table marks M-3 **PARTIAL** and
names the *remaining* 403s as the defect — this endpoint is the target state, not the
outlier.

**Changing it to 403 would be a security regression.** The design must absorb the cost
instead: the component cannot distinguish "denied" from "missing", so its 404 message must
be generic. A login hint may only come from a channel that does not vary per query id.

`SparkService` is a bare `firstValueFrom` passthrough with no interceptor, so the rejection
value **is** the raw `HttpErrorResponse` — `err.status`, `err.status === 0` for network, and
`err.error?.error` are all available. The component is discarding information it already
has: both catches are bare `catch {}` with no binding.

### F6 — The first-column link produces **nested anchors**, and the reported workaround does not work

The `<a>` is *outside* the renderer: `#cellContent` (which hosts `*ngComponentOutlet`) is
projected **into** the anchor at `spark-sub-query.component.html:27-30`. So giving the first
attribute a custom `renderer` does not suppress the link — Coverage's `account-login`
renderer emits its own `<a [routerLink]="['/a', value()]">` **inside** the dead
`/po/{alias}/{id}` one. Nested `<a>` is invalid HTML, and `canRead()` is true there, so the
outer wrong link is live.

> Issue #309 states the renderer works around the auto-link. Two independent readings
> confirm it cannot. The PRD records the workaround as **ineffective**, not as prior art.

The same anchor is duplicated in **three** template sites:
`spark-query-list.component.html:92-95` (streaming) and `:124-127` (non-streaming, a
byte-identical copy), plus `spark-sub-query.component.html:27-30`.

### F7 — Navigability is undecidable client-side, and undecidable from `Source`

There is **no** flag on `EntityTypeDefinition` or `SparkQuery` expressing "these rows have
no detail page". Three near-misses are all false signals: `QueryType`/`IndexName` mean
"the list projects through an index" (rows are still real documents); `InCollectionType`/
`InQueryType` are per-attribute; and `[FromIndex]` projection types are *structurally
unrepresentable* — `ModelShapeDiscovery`/`ModelSynchronizer` skip them, so such a type never
becomes a query's `entityType` at all.

The real failure mode is a **registered type whose rows are fabricated**:
`DemoApp/Actions/StockActions.cs:39` builds `new Stock { Id = $"stocks/{symbol}" }` in
memory; `Stock` is a queryable root with a model file, so `canRead` is true, the link
renders, and `/po/stock/stocks/AAPL` 404s. Same shape in
`WebhooksDemo/Actions/ProjectColumnActions.cs:15-27`.

`Custom.*` is a **false friend in both directions**: `Fleet`'s `Custom.Stolen_Cars`,
`Custom.Recent_Cars` and `Custom.Company_People` all return real, loadable documents, while
`Custom.StreamItems` and `Custom.GetProjectColumns` fabricate rows. `QueryExecutor.cs:357`
casts arbitrary in-memory instances, and `EntityMapper.cs:194` reads whatever `Id` the
action put there. Only the reverse holds: `Database.*` ⇒ ids are always real.

So the fact — "is `row.id` loadable" — is knowledge only the **query author** has. It must
be declared, and it belongs on `SparkQuery` (per-query), not `EntityType`: `Car` has both
navigable `Database.*` and navigable `Custom.*` queries, while `Stock` is a registered type
whose one query fabricates. A type-level flag would be wrong for one of them.

### F8 — Release path

`ng-spark` is at **22.2.0**, which is also the newest on npm; PR #308 carries **no** version
bump. Under the policy committed in `CLAUDE.md` the major stays **22** (Angular 22), and
additive inputs/outputs are a **minor** → **22.3.0**.

`.github/workflows/dotnet-build-master.yml` publishes on push to `master` only, via
`JS-DevTools/npm-publish@v4`, which **no-ops on an already-published version**. A forgotten
bump is therefore a *green run that publishes nothing* — the most likely way this fix fails
to reach Coverage. `ng-spark` and `ng-spark-auth` publish independently; auth does not
depend on ng-spark and needs no bump. No demo declares an `ng-spark` range (they consume it
through the `tsconfig.base.json` source mapping). `package-lock.json` records a stale
`22.0.8` for the workspace and must be regenerated from the repo root.

## Requirements

| # | Requirement | Issue |
|---|---|---|
| R1 | A host can supply the card header's content, keeping the component's card | #309(1) |
| R2 | A host can suppress the card entirely and keep a working grid *and spinner* | #309(1) |
| R3 | The component is visible while loading, on first load | F2 |
| R4 | A failed load renders a visible, intelligible message — never zero DOM | #309(3) |
| R5 | A failed page-fetch is distinguishable from an empty result | #309(3) |
| R6 | A host can re-run the query without destroying the component | #309(2) |
| R7 | Refresh must not reset the user's page, sort, or scroll | F4 |
| R8 | The first-column link is absent when the query's rows are not documents | #309(4) |
| R9 | Nothing above changes the behaviour of any existing caller | — |
| R10 | The 404-on-denied contract is preserved exactly | F5 |

## Design

### D1 — Chrome: a projected header, with `showCard` as the escape hatch

```html
<spark-sub-query queryId="my-accounts">
  <div subQueryHeader class="d-flex align-items-center">
    <span class="me-auto">Your accounts</span>
    <button class="btn btn-sm btn-outline-secondary" (click)="resync()">Resync</button>
  </div>
</spark-sub-query>
```

`<ng-content select="[subQueryHeader]">` sits inside `<bs-card-header>`, with the existing
`{{ description || name }}` as its **default content**. Projecting a header therefore
*replaces* the caption rather than stacking with it — which **defines the duplicate-heading
complaint out of existence** instead of adding an opt-out for it.

`showCard = input(true)` remains, for the genuinely chromeless embed (a tab body, a modal, a
dashboard tile). Its bare branch emits the spinner **and** the body div.

**No `showHeader`.** With projection, "no header" is expressible and better expressed as
`showCard=false`; the 4-way matrix buys nothing and must be kept visually sane for no
caller.

Omitting `<bs-card>` is safe: ng-bootstrap injects `.card-header` as a **global class rule**,
not `::slotted`, and `bs-datatable` has no dependency on card context. The only real loss in
bare mode is the card's `overflow:hidden` clip, which affects how a wide responsive table
overflows — an eyeball check, not a blocker.

### D2 — Restructure the template around three states

```
[error alert]        ← always, outside every gate
@if (loading())      { spinner }
@else if (query())   { card / bare body with the grid }
@else if (error)     { card shell with the message, so the host's layout does not collapse }
```

The alert **must** sit outside the `query()` gate or R4 is unmet. This is the change that
makes the spinner reachable and the failure visible; D3/D4 hang off it.

### D3 — Errors: the house convention, plus an output

- `errorMessage = signal<string | null>(null)`, set in **both** catches (which stop being
  bare `catch {}`), cleared at the start of `loadData` and on fetch success, rendered as
  `<bs-alert [type]="colors.danger">`.
- Extraction chain `e.error?.error || e.message || <fallback>`; the fallback goes through
  `SparkLanguageService.t(...)` rather than hardcoding a twelfth copy of
  `'An unexpected error occurred'`.
- **404 renders a generic message** ("This list is not available"), never "Not found" or
  "Access denied" — per F5 the component cannot know which, and guessing would either leak
  existence or mislead.
- `error = output<HttpErrorResponse>()` for hosts in bespoke chrome. Secondary: an output
  alone is insufficient, because the default must be visible with no host cooperation.
- **No toast** — `SparkNotificationService` is reserved for server-issued `notify`
  operations, and only 1 of 4 demos mounts `<spark-toast-container>`. **No retry modal** —
  that is the HTTP-449 protocol, not an error surface.
- `resultCount` is deleted (F3) or wired to the template. Deleting is preferred.

### D4 — Refresh: `reload()` and `reloadToken`, both data-level

Two levels exist and must not be conflated:

| Level | Re-runs | Cost |
|---|---|---|
| **Data** | `executeQuery` — `fetchFn.set(makeFetch(...))` | 1 request; keeps page, sort, scroll |
| **Metadata** | `getQuery` + `getEntityTypes` + `getPermissions` + lookups | 4+ requests; **resets page and sort** |

- `reload(): void` — public, **data-level**, mirroring `spark-query-list.refresh()`.
- `reloadToken = input<unknown>(null)` — read in a **second** effect that calls `reload()`
  and skips its first run. It must not be read by the existing effect, or the token would
  trigger the expensive metadata reload and silently reset the user's page and sort (R7).
- Both, not either: `viewChild` requires a component handle no ng-spark consumer holds
  today, and does not compose with the `@if` wrappers hosts use. The token is declarative
  and survives conditional rendering; the method is there for hosts that do hold a handle.
- Not exposing the datatable's refresh: there is nothing to expose (F4).

### D5 — Navigability: server-declared, per query

- `SparkQuery.RowsNavigable` — `bool?`, JSON-authored; `rowsNavigable?: boolean` on the TS
  interface. **No serialisation work**: the endpoints serialise the domain objects directly
  (`Endpoints/Queries/List.cs:17-29`), so a C# property plus a TS line is the whole wire
  change.
- Server-side default, pulled downwards so the common case declares nothing: `Database.*` →
  `true`; `Custom.*` → `true` unless explicitly `false`. **`Custom.*` must not default to
  `false`** — that would silently kill the working links on `Stolen_Cars`, `Recent_Cars`
  and `Company_People`.
- The template guard becomes `@if (first && canRead() && rowsNavigable())`. `canRead` stays:
  it answers *may I*, the new flag answers *is there anything there*.
- Applied to **all three** sites (F6), not just the sub-query.
- A client input is **not** the mechanism. `spark-sub-query` is auto-rendered from
  `EntityTypeDefinition.Queries` by `spark-po-detail.component.html:174-178`, so in the
  common case there is no host to ask.

## Decisions

| Decision | Why |
|---|---|
| Projection slot as the primary chrome fix, not `showCard` | F1 — the host wants the card with a different header. `showCard` alone pushes the chrome back up to every caller. |
| `showCard` still ships | A genuinely bare embed is a real, different use case, and it is what the issue asked for. |
| No `showHeader` | Redundant once a header can be projected; a 4-way matrix with no caller. |
| Restructure the template gate first | F2 — three reported symptoms share one cause, and a correct `showCard` branch is impossible without it. |
| Fix #309(3) purely client-side | F5 — the 404 is a named security remediation with tests pinning it. |
| Generic 404 message | The component cannot distinguish denied from missing; either specific message is wrong. |
| `reload()` **and** `reloadToken` | F4 — no consumer holds a component handle, and handles do not survive `@if`. |
| Refresh is data-level by default | R7 — a metadata reload silently resets page and sort on every button press. |
| `RowsNavigable` on `SparkQuery`, not `EntityType` | F7 — `Car` is navigable in both query flavours; `Stock` is not in its only one. |
| `Custom.*` defaults to navigable | F7 — the opposite default breaks three working Fleet/HR queries. |
| Record the renderer workaround as ineffective | F6 — it nests anchors rather than suppressing them. |
| One release, `22.3.0` | F8 — bundling #308 with #309 costs one version instead of two. |

## Acceptance criteria

1. `<spark-sub-query queryId="x">` with no parent loads and renders. *(#308, done)*
2. A projected `[subQueryHeader]` replaces the default caption; with nothing projected the
   caption is byte-identical to today.
3. `[showCard]="false"` renders the grid **and** the spinner, with no `bs-card` in the DOM.
4. A spinner is visible during the **first** load. *(Fails today.)*
5. A first-load failure renders a visible alert. *(Renders zero DOM today.)*
6. A page-fetch failure renders an alert and does not present as an empty grid.
7. A 404 renders the generic message, and the `error` output emits the `HttpErrorResponse`.
8. `reload()` re-fetches without changing page or sort; bumping `reloadToken` does the same.
9. A query with `rowsNavigable: false` renders no first-column anchor, in all three sites.
10. `spark-po-detail`'s stacked sub-queries are visually unchanged.
11. `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` still pass, unmodified.
12. `npm view @mintplayer/ng-spark version` reports `22.3.0` after merge.

## Migration

**None for existing callers.** Every new input is defaulted, the projection slot falls back
to today's caption, and the sole in-repo consumer
(`spark-po-detail.component.html:176`) passes only `queryId`/`parentId`/`parentType`.

For Coverage, after `22.3.0`: wrap the grid in its own `<bs-card>` — or better, project the
header — bind `reloadToken` to the existing `gridEpoch` signal (or call `reload()` and
delete the signal), set `rowsNavigable: false` on `My_Accounts`, and drop the
`@for`-over-one-element remount hack.

## Out of scope / follow-ups

- **`spark-query-list.onParamsChange`** (`:92-172`) is an `async` method called from a
  `subscribe` with no try/catch — a 404 there is an unhandled rejection and a permanently
  blank list. Same bug class, different component. **File separately.**
- **`refreshQuery` has no registered handler.** The server can emit the client operation
  (`client-operations/src/operations.ts:37-40`) but `provide.ts:14-30` wires only `notify`.
  A public `reload()` is the missing piece; wiring it is follow-up work.
- **`spark-po-detail` does not refresh its sub-queries** after `refreshOnCompleted`
  (`:248-251` refreshes only the PO). Once `reload()` exists, it should.
- **`[object Object]` in two templates** — `spark-po-edit.component.html:5` and
  `spark-po-create.component.html:8` interpolate a `TranslatedString` with no
  `| resolveTranslation`.
- **M-3 is still PARTIAL** — `PersistentObject/Get.cs:39-49` and `Queries/Execute.cs:128-138`
  still return 403. Not this PR's business, but the audit tracks it.
