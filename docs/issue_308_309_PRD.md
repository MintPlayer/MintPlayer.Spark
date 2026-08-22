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

### F13 — `selectionRule` is a half-delivered security remediation, not new feature work

`CustomActionDefinition.cs:19-22` declares `string? SelectionRule` (*"`=0` none, `=1` exactly
one, `>0` one or more"*), `ListCustomActions.cs:52` transports it, `custom-action.ts:9`
receives it — and **nothing reads it**. `ExecuteCustomAction` never fetches the definition
object at all, so the field is unreachable on the execute path by construction.

**It is already authored.** `Demo/Fleet/Fleet/App_Data/customActions.json:7` sets
`"selectionRule": "=1"` on `CarCopy`, `showedOn: "both"`.

**Which makes it a live bug.** The `'both'` filter renders the button in the car list;
clicking POSTs no parent and no selection; `CarCopyAction.cs:16-18` throws
`InvalidOperationException("No item selected")` → **500 "Operation failed"**. The demo's
flagship custom action is broken from the grid, and the field whose entire job is preventing
exactly that is inert.

**And it is a named, unfinished remediation.** `docs/issue_236_security_sweep_PRD.md:72`
records *"`SelectionRule` is advisory"* as finding **M3**, and
`docs/issue_236_security_sweep_plan.md:62` names the fix: *"…404s if absent (mirror
`ListCustomActions`); **enforce `SelectionRule` server-side**."* The first clause shipped
(`ExecuteCustomAction.cs:62-69`); the second never did.

**The semantics are Vidyano's, and the two Spark specs contradict each other.**
`Vidyano.Core/Common/ExpressionParser.cs:11-13` parses a cardinality expression to
`Func<int,bool>`: whitespace stripped, split on the `X` count placeholder and AND-combined
(so `1<X<5` is a range), operators `<= >= < > != =` matched in that order, number-first
mirrored (`0<X` → `>0`), unrecognised input → **always true**. Vidyano evaluates it
**client-side only**, to disable (not hide) the button; `Vidyano.Service` never parses it.

Spark's own docs disagree on the default: `docs/prd/custom-actions-prd.md:134` says omitting it
means `"=0"`; `docs/guide-custom-actions.md:126` says no requirement. Those are opposite, and
`"=0"` is itself glossed two ways — "no selection required" in prose versus `count == 0` as a
predicate. **This PR picks one and writes it down.**

**The client half is far cheaper than assumed.** `BsDatatableComponent` already supports
selection natively in the pinned version: `selectionMode: 'none'|'single'|'multiple'`,
`selection` as a two-way `ModelSignal`, and `rowKey` defaulting to `String(row.id)` — which
`PersistentObject` satisfies. The checkbox column is rendered by the component. This is a
signal plus two template attributes, not a control to build.

**Security posture.** Selected ids *are* row-checked — `ExecuteCustomAction.cs:125` calls
`GetPersistentObjectAsync` with the **route's** entity type, and `DatabaseAccess.cs:84/99/106`
applies type grant, collection guard and row rule, refusing with 404. Two gaps:

- **The row gate is hardcoded to `"Read"`** (`DatabaseAccess.cs:106`) even though
  `ISparkRowRule<T>.IsAllowedAsync(action, …)` is action-parameterised and the detail path
  *does* pass `"Edit"`/`"Delete"`. So a row rule cannot express "may `Archive` cars they own,
  but not cars they can merely see" — every such policy must be hand-rolled inside each
  action, which is precisely the silent-drift failure `ISparkRowRule`'s own doc comment warns
  about.
- ⚠️ **New security finding — unbounded attacker-controlled amplification.**
  `ExecuteCustomAction.cs:93-94` computes `estimatedRequests` from `SelectedItems.Length` and
  passes it to `IgnoreMaxRequests`, which reads like a budget but is not:
  `SessionExtensions.cs:73` sets `MaxNumberOfRequestsPerSession = int.MaxValue` and uses the
  parameter **only as a log threshold**. `SelectedItems.Length` is unbounded client input, each
  entry costing a load, a collection-guard check, a row-rule evaluation, breadcrumb resolution
  and redaction. No cap, no rate limit on `ActionsGroup`, ~30 MB default body limit. The
  ceiling *scales with the attacker's input*, so the warning fires later the worse the abuse.
  Predates this work; fixed here.

**`selectionRule` is a UX affordance and an input-validation contract. It is not an
authorization boundary** — the grant at `ExecuteCustomAction.cs:52` and the per-item row gate
are. Enforcing it server-side buys integrity and DoS containment, not access control.

### F14 — M-3 can be completed, and the audit contradicts its own reference implementation

`docs/prd/PRD-SecurityAudit.md:210-224` requires uniform 404 **for authenticated-but-not-
authorized**, byte-identical bodies, explicitly *"keep 401 for unauthenticated"*. Status is
PARTIAL (`:1136`).

**The anonymous-401 is not an oracle.** Authorization is evaluated against the principal alone
and *before* any load (`DatabaseAccess.cs:84` precedes `:91`), so for a denied type the
response is constant across every id. A 401 tells an anonymous prober only that they are
anonymous.

**But `Queries/Get.cs` — the file the audit calls fixed — returns 404 to anonymous callers**,
with no 401 branch, because `PermissionService` has no notion of authenticated-ness. So two
contracts already ship. The resolution:

> A **catalogue** endpoint (`/spark/types`, `/spark/queries`, `/spark/aliases`,
> `/spark/program-units`) is loaded by the shell on boot for every visitor; a 401 there would
> bounce every anonymous visitor to `/sign-in` merely for loading a page. It answers **404**.
> An **access** endpoint (`/spark/po/*`, `/spark/actions/*`, `/spark/lookupref/*`) is the
> caller *doing* something; **401** is the correct answer to "you are not signed in".

**Offenders the audit does not list**, found by sweeping every endpoint:

- `Queries/Execute.cs:31-79` parses `?sortColumns=` **before** authorizing, so an unauthorized
  caller enumerates the entity's attribute names via 400-vs-403. Same file, same fix.
- `ListCustomActions.cs:22-25` and `Permissions/GetPermissions.cs:20-23` return 404 for an
  unknown type but 200 for a known-but-denied one.
- `StreamExecuteQuery.cs:77-80` accepts the socket and then closes it with `"Access denied"`.

**Row-level security is already fully compliant** and is *more* correct than the type level:
row-denied → `null` → 404 everywhere (`DatabaseAccess.cs:106-107`, `Update.cs:93-100`,
`Delete.cs:73-78`, `Execute.cs:102-108`, `ExecuteCustomAction.cs:111-129`). Today a caller with
no `Read/Car` at all gets a *different* code from one refused a single row — backwards.

**Client impact is nil.** Nothing in `ng-spark`, `ng-spark-auth` or the demos branches on 403;
the only status branches are two `=== 400`. The one consequence is `SparkClient`, where an
authenticated-denied call stops throwing and returns `null` — a widening of a contract
`SparkClientException.cs:9` already documents as "missing **or row-level-denied**".

**Tests:** neither protected file needs an edit. `NotFoundVsForbiddenTests` is deliberately
shape-agnostic (`:56` asserts `forbidden == nonExistent`, true whether both are 403 or both
404); `MetadataEndpointAuthTests` asserts content, not status. Exactly one assertion changes —
`ExecuteCustomActionTests.cs:84-98`, which pins the 403 M-3 says must become 404. Every 401
assertion is an *anonymous* caller and must be preserved;
`AnonymousPersistentObjectAccessTests.cs:41-43` says so in the code.

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
| R13 | A grid supports row selection, and `selectedItems` reaches the server | F13 |
| R14 | `selectionRule` is evaluated — client for affordance, **server for enforcement** | F13, M3 |
| R15 | A selection payload cannot be used to amplify server work without bound | F13 |
| R16 | An authenticated caller denied a type, query or action is indistinguishable from not-found | F14 |
| R17 | Anonymous callers still receive 401 from access endpoints, so the login redirect works | F14 |

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

### D10 — `selectionRule`: Vidyano's grammar, evaluated on both sides

**Semantics, resolving the contradiction in favour of the guide:** `null`/`""` → **always
true** (no requirement). That is what `guide-custom-actions.md:126` says, what Vidyano does,
and the only non-breaking choice — `WebhooksDemo`'s action omits the field.
`docs/prd/custom-actions-prd.md:134`'s *"defaults to `=0`"* is **wrong and corrected here**,
along with its "(none)" gloss: `"=0"` means the predicate `count == 0`, i.e. the action is
disabled the moment anything is selected.

**Grammar:** port `Vidyano.Core/Common/ExpressionParser.cs` to
`Services/SelectionRuleParser.cs` returning `Func<int,bool>` — `X` placeholder, split and
AND-combine (`1<X<5` is a range), operators `<= >= < > != =` in that order, number-first
mirrored. A Vidyano-literate author's rules then mean what they expect. **Two deliberate
deviations:**

1. **Fail closed, at load time.** Vidyano falls back to *always true* on unparseable input,
   which is wrong for a server-enforced gate — `"1-5"` would silently permit everything.
   `CustomActionsConfigurationLoader` rejects a malformed rule when the file loads, matching
   the repo's loud-config-error posture. This also removes Vidyano's own C#-throws /
   JS-passes port divergence.
2. **Thread-safe cache** — Vidyano mutates a plain `Dictionary` without a lock.

Mirrored in TS as `models/src/selection-rule.ts`. **Both ports are generated against one
committed fixture table**, or they will drift exactly as Vidyano's two did.

**Client:** `selection` signal on `SparkGridState` (D9), cleared by `resetForNewSource()` —
otherwise it is drift bug D-4's exact shape, with route A's selection POSTed as ids of route
B's type. `[selectionMode]` computed to `'none'` unless a visible action is gated, so grids
without gated actions are pixel-identical; `'single'` when every gated action satisfies
`rule(1) && !rule(2)`, else `'multiple'`. Buttons are **disabled, not hidden** — hiding makes
the affordance undiscoverable and would reflow the priority-nav on every selection change.

**Server:** enforced between the action-existence gate and the reload loop, so a violation
costs zero database work. **400**, not 403 — malformed input, not an authorization decision;
the caller already proved they hold the grant. Plus an **unconditional ceiling** on
`SelectedItems.Length` (default 200) that applies even when the rule is null, because F13's
amplification finding is not fixed by the rule alone.

**Row gate per action:** additionally call
`rowSecurity.IsAllowedAsync(entityType, actionName, entity)` per item when the type has a row
rule, refusing with the same 404. One in-memory hook call per item; makes
`ISparkRowRule.IsAllowedAsync(action, …)` finally mean what its signature promises. Called out
in the release notes, since a consumer whose override returns `false` for unknown action names
would start refusing actions that work today.

**`selectionRule` is not an authorization boundary** — say so in a comment at the enforcement
site, not only here.

### D11 — M-3 completed, including type existence

Status is a function of the **principal and the endpoint's role — never of the resource's
existence**, at any granularity.

| endpoint class | anonymous | authenticated, denied | not found | row-denied |
|---|---|---|---|---|
| **Access** (`/spark/po/*`, `/spark/actions/*`, `/spark/lookupref/*`) | **401** | **404**, byte-identical to not-found | **404** | 404 ✅ already |
| **Catalogue** (`/spark/types`, `/spark/queries`, `/spark/aliases`, `/spark/program-units`) | 404 ✅ already | 404 ✅ already | 404 | n/a |
| **Query execute** (`/spark/queries/{id}/execute`, `/stream`) | 404 — follows its metadata sibling | 404 | 404 | 404 ✅ already |

**Unknown entity types adopt the denied shape** (owner decision, 2026-08-21, reversing an
earlier call). Otherwise the status discloses *which model JSON files exist and are
queryable* — a map of the application's data surface, recoverable one probe at a time from
the very endpoint `/spark/types` filters. That outweighs the cost, which is real and
accepted: `GET /spark/po/Bogus` answers **401** to an anonymous caller, which is a lie about a
type that will never exist and is hostile to debug against. `EntityTypes/Get.cs:23-25` is the
precedent. Affects `Get.cs:22-25`, `List.cs:21-24`, `Create.cs:33-36`, `Update.cs:34-37`,
`Delete.cs:32-36`, `ExecuteCustomAction.cs:43-46`, `ListCustomActions.cs:22-25`, and perturbs
six unknown-type unit tests that must be reviewed rather than assumed.

`Permissions/GetPermissions.cs` is the one carve-out: it is deliberately anonymous-callable
(audit M-1), so it closes its leak the other way — an **unknown type returns the same
200-all-false** as a denied one, rather than 404.

**Also fixed, since it is the same file:** `Queries/Execute.cs` authorizes **before** parsing
`?sortColumns=`, so an unauthorized caller can no longer enumerate attribute names via
400-vs-403. And `StreamExecuteQuery.cs` refuses at the handshake rather than accepting the
socket and closing it with `"Access denied"`.

**Byte-identity is not free:** `Get.cs:34`, `Update.cs:46` and `Delete.cs:57` interpolate the
requested id into the not-found body, so the denial body must interpolate it too.

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
| `selectionRule` enforced **server-side**, not client-only like Vidyano | F13 — Spark's own audit already decided this (`issue_236_security_sweep_plan.md:62`); it is a half-delivered remediation, not a new feature |
| `null` selection rule means "no requirement", not `"=0"` | F13 — the two Spark specs contradict each other; this is the non-breaking reading and matches the guide and Vidyano |
| Malformed rule = loud config error at load, not fail-open at execute | F13 — Vidyano's always-true fallback would make `"1-5"` silently permit everything |
| Selection violation returns **400**, not 403 | Malformed input, not an authorization decision — the caller already holds the grant |
| Unconditional selection ceiling even with no rule | F13 — `IgnoreMaxRequests` sets `int.MaxValue`; `estimatedRequests` is log-only and scales with attacker input |
| **Unknown entity types adopt the denied shape** | D11 — otherwise the status maps which model files exist and are queryable. Reverses an earlier call; the `401`-on-typo cost is accepted |
| `GetPermissions` closes its leak with 200-all-false, not 401 | It is deliberately anonymous-callable (audit M-1) |

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
24. **Fleet's `CarCopy` button in the car list returns 200 instead of today's 500**, with one
    row selected — and is disabled with zero or two selected.
25. `CarCopy` from the **detail** page still succeeds with no selection (the rule is scoped to
    the query path).
26. The C# and TS selection-rule parsers agree on every row of the shared fixture, including
    the malformed ones.
27. A malformed `selectionRule` fails at configuration load with a clear error, not at execute.
28. Submitting more than the ceiling of `selectedItems` is refused before any document load.
29. A row rule that denies `{actionName}` for a selected item refuses the action with 404.
30. **M-3:** authenticated-denied and not-found are **byte-identical** — status and raw body —
    for PO Get/List/Create/Update/Delete, query execute, and custom-action execute.
31. **M-3:** an unknown entity type is indistinguishable from a denied one, including
    `GET /spark/po/Bogus` returning 401 to an anonymous caller.
32. **M-3:** a denied query execute carrying a bogus `?sortColumns=` does **not** return 400.
33. **M-3:** anonymous callers still receive 401 from every access endpoint — the negative
    control that stops a future over-correction from killing the login redirect.
34. **M-3:** row-denied and type-denied produce the same code.
35. `NotFoundVsForbiddenTests` and `MetadataEndpointAuthTests` pass **unmodified**; exactly one
    pre-existing assertion changes (`ExecuteCustomActionTests.cs:84-98`, 403 → 404).
36. `npm view @mintplayer/ng-spark version` reports `22.3.0`; NuGet reports `preview.61`.

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

- **`AllSelected` / select-all-across-pages**, and Vidyano's `maxSelectedItems`. Vidyano
  transports an *exclusion set* re-materialised server-side
  (`ServiceImplementation.cs:1524-1549`); Spark has no equivalent and no caller asking. The
  unconditional selection ceiling (D10) covers the safety half.
- **Reshaping `[SparkAuthorize]`'s 403 on controllers.** ASP.NET's own middleware emits it;
  changing it needs a custom `IAuthorizationMiddlewareResultHandler`, and a controller route
  has no framework-level coupling between authorization and resource existence, so there is
  no oracle to close.
- **Replication and IdentityProvider status codes.** mTLS machine-to-machine and OAuth2
  respectively; OAuth2 *mandates* 401 with `WWW-Authenticate`.
