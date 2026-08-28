# PRD — Program units: PersistentObject page targets, composed virtual PO pages, and a shipped shell with slots

**Status:** Implemented (M0–M8; consumer-app adoption M9 pending publish)
**Issue:** [#324](https://github.com/MintPlayer/MintPlayer.Spark/issues/324)
**Branch:** `feat/issue-324-program-units`
**Plan:** `docs/issue_324_plan.md`
**Base:** `master` @ `7d8e0a68`
**Release:** `10.0.0-preview.65` + `@mintplayer/ng-spark@22.6.0` + `@mintplayer/ng-spark-auth@22.6.0` (npm packages in lockstep from here)
**Breaking changes:** allowed — the libraries are in preview (majors stay locked to the platform)

---

## Problem

A program unit can point at a Spark query, and nothing else. Three consequences:

1. **No menu entry can open a page.** `ProgramUnit.PersistentObjectId` names an entity
   *type* and the client routes it to `/po/{type}` — the query list. There is no way to say
   "open this specific object" and no way to say "open a composed page" (a start page, a
   dashboard, a per-user landing object). Apps that want a home page write a hand-coded
   Angular component instead, outside the model, invisible to `security.json`.

2. **Every consumer re-implements the whole shell, not just the sidebar.** The accordion
   markup and the re-fetch-on-auth-change effect are copy-pasted across all four demo
   shells (HR and Fleet are byte-identical), and an external consumer app doesn't use the
   subsystem at all — its shell hardcodes a single link and its home page is a bespoke
   component calling bespoke endpoints. Beyond the menu, all five apps carry a
   **verbatim-identical** block of hand-rolled responsive logic (`shellState` signal,
   window-resize listener, hardcoded 768px check, collapse-on-navigate handler) that the
   underlying `mp-shell` web component already implements, four byte-identical copies of a
   `bsShellTopbar` workaround directive, and ~80 identical lines of shadow-DOM-seam SCSS.
   A host that wants to choose *what renders where* (a language selector above the
   accordion, a user chip in the topbar) has no slots to do it with — it must own the
   entire layout to change any of it.

3. **The client trails the server.** The server passes untyped units through the permission
   filter deliberately (contemplating home links and external URLs); the client maps every
   unknown type to `/`. The server matches `Type` case-insensitively; the client
   case-sensitively — a `"Query"` unit passes the filter and silently routes to `/`.

## Prior art

Vidyano's program-unit model (the direct ancestor of this design) supports everything this
PRD adds:

- A menu item references either a query or a persistent object; the PO reference carries an
  **optional object id** — "open PO type X for row N" as a declarative deep link.
- A PO reference *without* an id is the **start-page pattern**: the target is a model-only
  type (no backing collection), marked read-only, and its `OnLoad` ignores the requested id
  entirely — it composes the attribute values in code (a personalized greeting, a stats
  table rendered as a CommonMark attribute, build info). Breadcrumb becomes the page title;
  tabs/groups decide the layout. A sharper variant redirects instead of composing: resolve
  the current user, reroute to that user's real object.
- **No authorization data lives in the menu file.** An item is visible iff the user holds
  the Read right on the target; units whose items all filtered away are not emitted at all.
- Extras Spark doesn't need yet: separators, live row counts, URL items (we adopt only the
  URL item), server-side filter hooks beyond rights.
- One finding worth recording for later: in mature deployments the declarative rights
  filter is only the **baseline** — most real-world menu policy runs through a server-side
  build hook (role-based pruning of units/items, per-user profile flags hiding a unit,
  runtime rewriting of titles and target ids, manual rights probes), and unit/item names
  are projected into generated compile-time constants with an analyzer flagging raw string
  literals. Neither ships in this PR (no consumer needs them yet), but the compose/filter
  seams should not be designed in a way that forecloses a `IProgramUnitsFilter`-style hook
  later.

## Investigation findings

Four parallel investigations (Spark core, ng-spark libraries, the consumer app, prior art).
Everything below was read in the code.

### F1 — The subsystem already exists end to end

| Layer | Where |
|---|---|
| Model | `libs/spark/MintPlayer.Spark.Abstractions/ProgramUnit.cs` — `ProgramUnitsConfiguration` → `ProgramUnitGroup[]` → `ProgramUnit { Id, Name (TranslatedString), Icon?, Type, QueryId?, PersistentObjectId?, Order, Alias? }` |
| Loader | `Services/ProgramUnitsLoader.cs` — fixed `App_Data/programUnits.json`, lazy singleton, **fail-soft** (missing file → empty config) |
| Endpoint | `Endpoints/ProgramUnits/Get.cs` — `GET /spark/program-units`, filters per unit via `IPermissionService`, fails **closed** for typed units, drops empty groups, passes untyped units through |
| TS | `ng-spark/models/src/program-unit.ts`, `SparkService.getProgramUnits()` (`services/src/spark.service.ts:85`), `RouterLinkPipe` (`pipes/src/router-link.pipe.ts`) |
| Demos | all four shells: `bs-accordion` + `effect()` keyed on `authService.user()` → re-fetch on sign-in/out |
| Tests | `GetProgramUnitsEndpointTests.cs`, `ProgramUnitsLoaderTests.cs` |

So requirement "(1) an endpoint that lists accessible program units" from the issue's
origin is **already shipped**. This PRD is about the gaps, not the subsystem.

### F2 — The read path is entity-first; a composed PO cannot flow through it

`DatabaseAccess.GetPersistentObjectAsync(Guid objectTypeId, string id)`
(`Services/DatabaseAccess.cs:80-136`) runs, in order:

1. `EnsureAuthorizedAsync("Read", entityTypeDefinition.Name)`
2. `OnLoadAsync(session, id)` on the actions class — typed `Task<T?>`, **must return an
   entity**
3. `ICollectionGuard.BelongsToAuthorizedCollection` — id-to-type binding, 404 on mismatch
4. `rowSecurity.IsAllowedAsync(...)` → 404 on deny
5. `entityMapper.ToPersistentObject(entity, ...)` + `RedactAsync` + `Can` block + `Etag`

There is **no hook that returns a `PersistentObject` on the read path**. The framework maps
entity → PO itself; the actions class never sees the PO. Vidyano's hook is the inverse
(PO-first: the actions class receives the scaffolded PO, may rewrite `ObjectId`, then the
base loads the entity), which is what makes its start-page and resolve-my-object patterns
one-override affairs.

### F3 — Virtual PO types exist, but only for dialogs

`Demo/Fleet/Fleet.Library/VirtualObjects/ConfirmDeleteCar.cs` is the shipped precedent: a
hand-authored model JSON (`App_Data/Model/ConfirmDeleteCar.json`) + a CLR **marker class**
(required because `EntityTypeDefinition.ClrType` must resolve) + **no context root** (not
in `FleetContext`). It is scaffolded via `IManager.GetPersistentObject(...)`
(`CarActions.cs:130`) inside a retry-action round-trip and never touches the database.
`IManager.GetPersistentObject` builds a blank PO with full attribute metadata, `Value ==
null`, and has no `isNew` parameter — POs from it are exactly the composition surface a
start page needs, but nothing routes a GET request to one: `/spark/po/{type}/{id}` for a
virtual type 404s at step 2 of F2 (`session.LoadAsync<T>(id)` → null).

### F4 — `security.json` is mandatory; authorization needs no new machinery

Since #310 (`cc18aa38`), `SparkMiddleware` refuses to start without
`App_Data/security.json`. The endpoint's per-unit filtering (`IsAllowedAsync`) and the
per-request permission memo (`PermissionService`, explicitly documented as existing for the
program-units loop) already give us the authorization story: **a PO unit is visible iff the
user holds `Read` on the target type; a query unit iff `Query` on the entity type.** No
rights vocabulary changes needed.

### F5 — Known defects in the existing subsystem

- **Type-case mismatch:** `Get.cs` compares `ProgramUnit.Type` case-insensitively;
  `RouterLinkPipe` compares `'query'`/`'persistentObject'` case-sensitively. A `"Query"`
  unit passes the server filter, then routes to `/`.
- **Untyped units dead-end client-side:** the server passes them through unfiltered by
  design; the pipe maps them to `/`.
- **WebhooksDemo ships no `programUnits.json`** — identical shell wiring, silently empty
  sidebar (the loader's fail-soft path).
- Units within a group are not re-sorted client-side (groups are; units trust server
  order — the server should own the ordering).

### F6 — The sidebar extraction is mechanical, except for two things

Adding an entry point is folder + `ng-package.json` + `index.ts`, nothing else (wildcard
tsconfig path; ng-packagr synthesizes `exports`). The markup and the auth-effect to extract
are proven in four apps. The two real problems:

1. **Dependency direction.** No dependency exists between `ng-spark` and `ng-spark-auth`
   in either direction, and the sidebar (in `ng-spark`) must observe sign-in state (in
   `ng-spark-auth`'s `SparkAuthService.user` signal). Precedent for the fix:
   `SPARK_CONFIG` is already an optional-injected token with a default.
2. **Accordion styling crosses a shadow boundary.** Since ng-bootstrap 22.13 the accordion
   internals are a Lit web component; `.accordion-*` selectors are dead. The working seams
   (`--bs-*` custom properties inheriting across the boundary, `data-bs-theme` on the nav,
   `mp-accordion::part(content)`) currently live in each demo app's SCSS and reach the
   sidebar via `::ng-deep bs-shell`. Extracting the markup without carrying this produces a
   white-on-white accordion.

### F7 — The consumer app is greenfield for this feature

The reference consumer has no `programUnits.json`, a sidebar of exactly one hardcoded link,
and a home page component whose content is: a static welcome card; then (authenticated
only) a re-auth warning banner with a reconnect button, an accounts card with a resync
button and a list of account rows (avatar, link, repo count, coverage %, installed badge),
and an install-app footer hint — all fed by a bespoke `/api` endpoint, all labels through
`translations.json`. Its `security.json` grants four `QueryRead/*` rights to a single
group. Its Angular routing mounts `sparkRoutes()` at the root with a `poDetail` override
that intercepts every `/po/:type/:id` for vanity redirects and falls through to
`spark-po-detail` otherwise — a new virtual type falls through cleanly.

Full parity of that home page through a composed PO is **not** a given: the account list
is interactive (router links, buttons). What a composed PO can carry today is attribute
values (including CommonMark-ish text if a renderer exists — spike), breadcrumb (→ page
title), tabs/groups, and custom actions. The parity line is a spike, decided in the
consumer repo.

### F8 — The shell itself is five hand-rolled copies of what already exists

`bs-shell` (`@mintplayer/ng-bootstrap/shell`) is a thin Angular wrapper over the `mp-shell`
Lit web component, which exposes named slots `hamburger` / `topbar` / `sidebar` / `toggle`
plus a default slot for main content, and **owns the responsive behavior in pure CSS**:
the breakpoint matrix, open/close state, a built-in hamburger, `dismissOnNavigate`
(auto-close on sidebar link click, with a `data-no-dismiss` opt-out), and its own
rAF-throttled resize handling. Angular surface: `state`, `breakpoint`,
`dismissOnNavigate` inputs, `statechange` output, `toggle(force?)`.

Despite that, every host app hand-rolls the same logic on top — `shellState` signal,
window-resize listener, `isAboveBreakpoint()` with a hardcoded 768 (re-deriving the `md`
breakpoint the web component already resolves), `onMenuItemClick()` (a re-implementation
of `dismissOnNavigate`), and toggler↔state mirroring — **verbatim, comments included, in
all five apps**. Only the toggler mirroring is genuinely needed, and only because the apps
hide the built-in hamburger (`::part(hamburger){display:none}`) to substitute
`bs-navbar-toggler`.

Additional duplication:

- `bs-shell-topbar.directive.ts` — byte-identical in all four demos; its own comment says
  it exists because ng-bootstrap exports only `bsShellSidebar` and forbids host-binding
  `slot` on an `<ng-template>`. The consumer app (and ng-bootstrap's own React/Vue demos)
  use a plain `<div slot="topbar">` instead — the static attribute is the sanctioned
  authoring form, so a shell component that owns the topbar deletes all four copies
  rather than promoting the directive.
- `shell.component.scss` — identical across the four demos (the consumer app carries a
  subset): all the shadow-seam knowledge (`::part(hamburger)`, `::part(content)`,
  `--bs-*` palette overrides, sidebar/main backgrounds, `:host{height:100vh}`).
- The topbar language selector (`bs-select` over `SparkLanguageService`) is identical in
  the four apps that have one, and every dependency it needs is already legal in
  ng-spark. The auth region is the opposite: two apps use `spark-auth-bar`
  (ng-spark-auth — a package ng-spark must not reference), two hand-roll different
  GitHub/returnUrl flows. **The auth region must be a slot; the language selector can be
  a built-in default.**

### F9 — The slot pattern is already house style

ng-bootstrap's `*bsAccordionTabHeader`/`*bsDatatableColumn`/`*bsTabPageHeader` and
ng-spark's own `grid/src/spark-query-slots.ts` (`*sparkQueryIcon`, …) share one shape: an
attribute directive that injects `TemplateRef` (with `ngTemplateContextGuard`), parent
discovery via `contentChild(ren)` by directive type, rendering via `ngTemplateOutlet`, an
explicitly documented doctrine that **an omitted slot renders the default** (override, not
replace), and a parallel `TemplateRef` input per slot as the escape hatch for hosts that
cannot use content projection. Naming convention: prefix + component + slot. The new shell
copies this pattern verbatim.

### F10 — Client routes

`sparkRoutes()` emits `query/:queryId`, `po/:type/new`, `po/:type/:id/edit`, `po/:type/:id`,
`po/:type` (list). A fixed-id unit needs only `/po/{type}/{objectId}` — no new route. The
pipe prefers `alias` over GUID by design (`docs/guide-aliases.md`).

---

## Design

### D1 — Schema: `ObjectId` on `ProgramUnit`, plus a `url` unit type

```jsonc
// App_Data/programUnits.json — a unit, new fields marked
{
  "id": "…",
  "name": { "en": "My car" },
  "type": "persistentObject",
  "persistentObjectId": "…",        // entity TYPE id (existing)
  "objectId": "cars/1-A",           // NEW: optional — deep link to one object
  "order": 3
}
{
  "id": "…",
  "name": { "en": "Status page" },
  "type": "url",                    // NEW unit type
  "url": "https://status.example.com",
  "order": 9
}
```

Semantics (server filter × client route):

| `type` | fields | visible iff | routes to |
|---|---|---|---|
| `query` | `queryId` | `Query` right on the entity type | `/query/{alias ?? queryId}` |
| `persistentObject` | `persistentObjectId` | `Read` right on the type | `/po/{alias ?? typeId}` (list — unchanged) |
| `persistentObject` | + `objectId` | `Read` right on the type | `/po/{alias ?? typeId}/{objectId}` |
| `url` | `url` | always | external anchor (`target="_blank" rel="noopener"`) |

Decisions folded in:

- **A dedicated `Url` property, not an overloaded `ObjectId`.** The prior art overloads one
  string with three meanings (row id / external URL / arbitrary payload); that fails the
  obviousness test. Explicit `type: "url"` + `url` field.
- **`persistentObject` without `objectId` keeps its current meaning** (the type's default
  list). The composed start page uses an explicit `objectId` — any stable string the app
  chooses (`"home"`, `"0"`); the compose hook receives and may ignore it. This keeps every
  existing programUnits.json valid and needs no new client route.
- **Type normalization at load time.** `ProgramUnitsLoader` canonicalizes `Type` (and
  validates the field combinations: `query` requires `queryId`, `url` requires `url`, …)
  so the endpoint and the pipe compare exact strings. Pull the tolerance down into the
  loader; everything above it becomes exact. Invalid units fail loudly at load (consistent
  with `SecurityConfigurationValidator`'s philosophy), not silently at click time.
- The endpoint's right check per unit type is corrected/confirmed: `Query` for query
  units, `Read` for PO units (with or without objectId).

### D2 — The composed virtual PO read path

The Vidyano start-page pattern, adapted to Spark's entity-first pipeline.

**Design revisions during implementation (owner directives):**

1. A virtual type needs **no CLR class at all** — F3's marker-class shape was itself
   boilerplate. `EntityTypeDefinition.ClrType` became optional; every document-shaped path 404s
   for a JSON-only type (`ISparkTypeResolver.Resolve(null) → null`, defining the error out of
   existence at each call site). Fleet's `ConfirmDeleteCar` marker class was deleted on the same
   grounds — dialogs scaffold via `IManager.GetPersistentObject` from the JSON alone.
2. **No compose concept at all — `OnLoadAsync` reshaped instead** — the `OnComposeAsync` hook
   sketched below was replaced: a program unit simply triggers the corresponding Actions
   class's existing hook, whose signature became `Task<PersistentObject?> OnLoadAsync(string
   id, PersistentObject? parent)` (id in, page out; the session is `[Inject]`ed — it was a
   pass-through parameter). The whole per-row read pipeline (load + includes, collection
   guard, row security, breadcrumbs, mapping, redaction, per-row can, etag) moved from
   `DatabaseAccess` into `DefaultPersistentObjectActions<T>.OnLoadAsync`, so an override can
   finally touch the page (`await base.OnLoadAsync(id, parent)` then decorate) — skipping the
   base takes the pipeline over, the read-side twin of `OnSaveAsync`'s WITH CHECK caveat; the
   type-level `Read` right stays framework-owned. A JSON-only type's actions resolve by *name*
   (`{Name}Actions`, a plain class — no base) with the identical signature, scaffolding via
   `IManager.GetPersistentObject` and served read-only, with the guard-skipping rationale below
   unchanged. A wrong-shaped `OnLoadAsync` throws loudly (the contract is reflective); no
   actions class means 404.

The original design (kept for the record of what was considered and rejected):

New seam on the actions class (final shape decided by spike S1):

```csharp
// DefaultPersistentObjectActions<T> — default returns null → existing pipeline unchanged
public virtual Task<PersistentObject?> OnComposeAsync(ComposeArgs args) => Task.FromResult<PersistentObject?>(null);

public sealed class ComposeArgs
{
    public required string RequestedId { get; init; }   // from the URL; compose may ignore it
    public required PersistentObject PersistentObject { get; init; } // scaffolded via IManager, full metadata, null values
}
```

`DatabaseAccess.GetPersistentObjectAsync` calls `OnComposeAsync` **after** the type-level
`Read` check and **before** `OnLoadAsync`. Non-null return short-circuits: the composed PO
is returned as-is (read-only unless the actions class says otherwise), skipping
CollectionGuard, row security, EntityMapper and Etag.

Why skipping those is sound, not a hole:

- CollectionGuard exists to stop id-to-type confusion over *database documents*; a
  composed PO corresponds to no document, and the actions class that composes it is the
  same authority CollectionGuard defers to.
- Row security filters and redacts *entities*; here the actions class hand-picks every
  value it exposes, under a type-level `Read` right that `security.json` must grant
  explicitly. The rule stays: **no grant, no page** — the program-units endpoint and the
  GET both enforce it.
- `Can = { Edit: false, Delete: false }` is forced on the composed PO. A start page is not
  editable through the generic pipeline; anything interactive on it is a custom action
  (which has its own authorization).

The alternative considered (design-twice): a Vidyano-style **id-redirect hook**
(`OnResolveIdAsync(string? requested) → string`) that keeps the entity pipeline and covers
the "open *my* object" pattern (resolve current user → their document). It composes
nothing, so it cannot serve a start page; the compose hook subsumes it awkwardly (compose →
client-operation Navigate). Spike S1 prototypes compose first; if the redirect variant
falls out nearly free (it is one string substitution before step 2 of F2), it ships in the
same PR, else it is genuinely out of scope.

### D3 — Client: pipe + models + defect fixes

- `ProgramUnit` TS model gains `objectId?: string` and `url?: string`.
- `RouterLinkPipe`: emits `['/po', alias ?? typeId, objectId]` when `objectId` is present;
  string comparisons become exact (the loader now canonicalizes — D1); `url` units are not
  its business (the component renders an `<a href>` for them, not a routerLink).
- `spark-po-detail` renders a composed PO as it would any read-only PO: breadcrumb is
  already the page heading, absent `can.edit`/`can.delete` already hide the affordances
  (verified in spike S2, along with what a CommonMark-ish long-text attribute renders as).

### D4 — `spark-shell` + `spark-program-units`: the shipped shell, with slots

New entry point `@mintplayer/ng-spark/shell` with two components. `spark-program-units`
is the server-driven menu (usable standalone by a host that owns its own layout);
`spark-shell` is the primary API — it wraps `bs-shell`, owns the whole frame, embeds the
menu, and exposes **slots** so the host chooses what renders where without owning the
layout:

```html
<spark-shell title="My App">
  <ng-container *sparkShellTopbarEnd>
    <spark-auth-bar />                      <!-- or any custom auth/user chip -->
  </ng-container>
  <div *sparkShellSidebarTop>
    <a routerLink="/github-projects">GitHub projects</a>
  </div>
  <router-outlet />                         <!-- default slot = main content -->
</spark-shell>
```

**Slot set** — attribute structural directives per F9's house pattern
(`TemplateRef`-injecting directive + `contentChild` discovery + `ngTemplateOutlet`, an
omitted slot renders the default, and a parallel `TemplateRef` input per slot):

| Directive | Region | Default when omitted |
|---|---|---|
| `*sparkShellTopbarStart` | topbar, left | `bs-navbar-toggler` mirroring the shell state |
| `*sparkShellTopbarEnd` | topbar, right | the language selector (self-hides when ≤ 1 language) |
| `*sparkShellSidebarHeader` | sidebar, above everything | `<h5>{{ title }}</h5>` |
| `*sparkShellSidebarTop` | sidebar, between header and accordion | nothing |
| `*sparkShellSidebarTabs` | inside the accordion, after the generated groups | nothing |
| `*sparkShellSidebarFooter` | sidebar, below the accordion | nothing |
| `*sparkShellMainHeader` | main, above the projected content | nothing |
| *(default `<ng-content>`)* | main | — (the host's `<router-outlet>` goes here) |

The auth region is **deliberately slot-only** (`*sparkShellTopbarEnd`): two apps use
`spark-auth-bar` (ng-spark-auth, which ng-spark must not reference) and two hand-roll
different flows — F8 shows it is genuinely app-specific. The language selector is the
opposite — identical wherever it appears and all its dependencies are already legal in
ng-spark — so it becomes a small exported `spark-language-selector` component that is also
the `TopbarEnd` default's left half.

**What `spark-shell` deletes from every host** (F8): the `shellState`/resize/768px block,
`onMenuItemClick` (replaced by `bs-shell`'s `dismissOnNavigate`; accordion group headers
get `data-no-dismiss`), the four `bs-shell-topbar.directive.ts` copies (`spark-shell`
emits `<div slot="topbar">` in its own template — the sanctioned authoring form), and the
~80-line shadow-seam SCSS (moves into the component; hosts re-theme via the same `--bs-*`
custom properties on the element — spike S4 verifies packaged reach).

**Inputs** (kept few): `title` (sidebar header default), `breakpoint` (forwarded,
default `md`), `sidebarTheme` (`'dark' | 'light' | null`, default `'dark'` —
the consumer app runs light).

**The menu is never hand-rendered.** The contract of both components: consumers write
zero routerLinks for navigation — every group, unit, icon, label and link is sourced from
`programUnits.json` via `GET /spark/program-units` (so it reflects the caller's rights),
and the menu re-fetches itself when the user signs in or out. The slots exist for content
*around* the menu (auth bar, branding, a one-off extra link); a host that finds itself
writing unit anchors in a slot should be adding units to `programUnits.json` instead.

**`spark-program-units`** (embedded by `spark-shell`, exported for shell-less hosts):

- Fetches via `SparkService.getProgramUnits()`; sorts groups **and units** by `order`
  (closing the F5 inconsistency — the server order stops being load-bearing).
- Renders the proven demo markup: `bs-accordion` → one tab per group (icon + translated
  name) → per unit a routerLink anchor or, for `url` units, an external anchor.
  `spark-icon` + `iconName` fallbacks (`bi-folder` / `bi-file`), `resolveTranslation` on
  names.
- Public `reload()` for imperative refresh.
- **Auth re-fetch without a package dependency:** new token in `ng-spark`:

  ```ts
  export const SPARK_AUTH_STATE = new InjectionToken<Signal<unknown>>('SPARK_AUTH_STATE');
  ```

  Injected `{ optional: true }`; the fetch runs in an `effect()` that reads the signal
  when present. `ng-spark-auth`'s `provideSparkAuth()` supplies it
  (`useFactory: () => inject(SparkAuthService).user`). Apps with custom auth provide
  their own signal; apps with none get a one-shot fetch. A `reloadToken` input (the
  `spark-query-card` idiom) is the manual escape hatch.

Deliberately *not* slots or inputs (somewhat-general-purpose): per-item templates inside
the accordion, menu filtering, a footer for main. The slot set above is exactly what the
five existing shells demonstrably need; the next real consumer tells us which knob to add.

### D5 — Demos become consumers

- All four shells collapse to `<spark-shell>` + slots: Fleet/HR put `spark-auth-bar` in
  `*sparkShellTopbarEnd`; WebhooksDemo puts its hand-rolled auth there and its
  auth-gated `/github-projects` link in `*sparkShellSidebarTop`; DemoApp puts its
  hand-written "Component demos" tab in `*sparkShellSidebarTabs`. The copied
  `shellState`/resize logic, the four `bs-shell-topbar.directive.ts` copies, and the
  duplicated shell SCSS are **deleted**.
- WebhooksDemo gets a real `programUnits.json` (F5).
- One demo (DemoApp) gains a **Start page** dogfooding D1+D2 end to end: a virtual
  `StartPage` type (marker class + model JSON, read-only, one long-text attribute +
  a couple of value attributes), `StartPageActions.OnComposeAsync` composing a greeting +
  live counts from the session, a `persistentObject`+`objectId` unit pointing at it, and
  the `Read/StartPage` grant in its `security.json`. This is what the tests bite on.

### D6 — Consumer app adoption (sequenced tail, separate repo)

Same unit of work, lands right after the packages publish:

- `App_Data/programUnits.json`: a group with the Home unit
  (`persistentObject` + `objectId`, virtual `Home` type) and the four existing queries as
  query units.
- `Home` marker class + model JSON + `HomeActions.OnComposeAsync` composing the welcome
  content server-side; `Read/Home` grant. The parity line for the interactive parts
  (accounts list, resync, re-auth banner) is spike S5 — resync becomes a custom action;
  whatever cannot be expressed as attributes/actions **stays a client component for now,
  documented as such** rather than faked.
- The shell collapses to `<spark-shell sidebarTheme="light">` with slots: the language
  selector default stands; the hand-rolled GitHub auth block goes in
  `*sparkShellTopbarEnd`; the login-error `bs-alert` goes in `*sparkShellMainHeader`; the
  hand-rolled `shellState`/resize block is deleted. Package bumps to the versions this PR
  publishes.
- Its `poDetail` vanity-redirect override falls through for unknown types already — `Home`
  needs no change there (F7).

---

## Breaking changes

Preview rules apply (breaks ship as minors; majors stay platform-locked).

1. `ProgramUnitsLoader` starts **validating**: a unit with an unknown `type` or missing
   required fields now fails at load instead of passing through. (Behavioral; existing
   demo files are valid.)
2. The program-units endpoint gates PO units on `Read` (if it gated them on `Query`
   before, visibility for PO units changes for grants that had one right but not the
   other).
3. `provideSparkAuth()` additionally provides `SPARK_AUTH_STATE` (additive, not breaking).

No shims, no `[Obsolete]` — deleted means deleted, consistent with #310.

## Out of scope (genuinely not being done)

- Separators, per-query live counts, server-side item-filter hooks beyond rights
  (the `IProgramUnitsFilter`-style build hook noted under Prior art — likely the first
  follow-up when a consumer needs role/profile-driven menus), generated name constants +
  analyzer for unit names, and `OpenFirst`/auto-navigate — prior-art features with no
  requesting consumer yet.
- Nested groups (groups within groups). The flat group → units shape stays.
- Promoting a `bsShellTopbar` directive into ng-bootstrap (the demos' copies are deleted
  because `spark-shell` owns the topbar; ng-bootstrap's own gap is its own repo's call).
- An editor/generator for `programUnits.json` (belongs to the Spark Editor initiative).

## Spikes

| # | Question | Answered by |
|---|---|---|
| S1 | Compose hook shape: prototype `OnComposeAsync` in `DatabaseAccess` against a virtual type; confirm virtual-type detection (no context root) is reliable; measure whether the id-redirect variant is nearly free | prototype on branch |
| S2 | Does `spark-po-detail` render a composed read-only PO acceptably today: page title from breadcrumb, hidden edit/delete, long-text/markdown attribute rendering (does a CommonMark renderer exist?) | run DemoApp with a hand-scaffolded PO |
| S3 | `SPARK_AUTH_STATE` optional-token wiring works across packages with tree-shaking intact (provideSparkAuth in an app that lazy-loads) | wire in HR demo |
| S4 | The shell extraction holds up packaged: shadow-boundary styling reach (component styles vs the current `::ng-deep bs-shell`), `dismissOnNavigate` + `data-no-dismiss` on accordion group headers replacing `onMenuItemClick`, toggler↔state mirroring inside `spark-shell` | build + run one migrated demo |
| S5 | Consumer home-page parity line: which parts of the current page are expressible as composed attributes + custom actions | consumer repo session |
