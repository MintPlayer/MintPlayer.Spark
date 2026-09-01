# PRD — Adopt Spark program units: a composed home page, a server-driven menu, and controllers under `security.json`

**Status: ✅ IMPLEMENTED 2026-08-29 · `10.0.0-preview.57` → `10.0.0-preview.67` (ten releases) and `@mintplayer/ng-spark` `22.1.0` → `22.8.0`**

See [§9 As-built](#9-as-built) for what the plan got wrong and what it missed.

Companion plan: [program-units-plan.md](program-units-plan.md).
Predecessor: [adopt-spark-preview-57.md](adopt-spark-preview-57.md).

**The upstream blocker is gone.** Every ask in §7 was filed as
[MintPlayer.Spark#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327) and shipped in
[PR #328](https://github.com/MintPlayer/MintPlayer.Spark/pull/328) (`preview.67` / ng-spark `22.8.0`,
merged 2026-08-29 as `fd570906`). Spark went further than we proposed — a query row is now a
lightweight `QueryResultItem`, not a `PersistentObject`. Consequences for this document:

- **D1/D3 and M6 shrink.** `MyAccountRow` as a CLR class and its hand-authored model file are gone: a
  composed query on a `clrType`-less type is now first-class. `MyAccountRowActions` becomes a plain
  actions class returning `IEnumerable<T>`; a readable `Id` of any type narrows without a hook.
- **D2 still holds, for a new reason.** Grant `Query` and never `Read` — but the framework now also
  defaults the row link to `null` when `clrType` is absent, so a composed row cannot link to a detail
  page that does not exist.
- **A new milestone appears: M9, the renderer migration** — the cost moved from the server to the
  client. See §4.7.
- [composed-queries-PRD.md](composed-queries-PRD.md) is now a **superseded** proposal record; three of
  its claims are wrong and are annotated there.

Trigger: [Spark PR #325](https://github.com/MintPlayer/MintPlayer.Spark/pull/325) ("Program units: PO page
targets, JSON-only virtual PO pages, and spark-shell with slots", closes #324) squash-merged 2026-08-28.
The target is now `10.0.0-preview.67` / npm `22.8.0` (lockstep), which also adds
`MintPlayer.Spark.Controllers` to the package set for D4. `@mintplayer/ng-spark` peer-requires
`@mintplayer/ng-bootstrap ^22.13.0`; Coverage is on `22.16.0`, so no bootstrap bump is needed.

File:line references are to `C:\Repos\MintPlayer.Spark`: `5ebfaa45` (preview.65) for §4.1–4.6, which were
written against that baseline, and `fd570906` (preview.67) for §4.7 and the `.67` rows of §4.2.

---

## 1. Goal

Three things, in one PR (per the one-PR rule — this is a single unit of work, not a phased rollout):

1. **The home page becomes a Spark-composed page.** A JSON-only virtual persistent object
   (`Home.json` + `HomeActions`) reached from a `persistentObject` program unit, with the accounts list as
   a real Spark query over a transient row type, and **the page title coming from the PO breadcrumb**
   instead of a `bs-card-header`.
2. **The sidebar becomes server-driven.** `<spark-shell>` replaces the hand-rolled shell; `programUnits.json`
   replaces the one hardcoded `routerLink`.
3. **The six controllers move under Spark**, mounted via `spark.AddControllers()` / `spark.UseControllers()`
   with their authorization declared in `security.json` instead of hand-rolled in C#.

Carrying the ten-release upgrade is not a separate goal — it is the unavoidable cost of (1) and (2),
and it is where most of the risk lives.

## 2. Why now

The feature landed upstream today and Coverage is its first out-of-tree consumer. Every gap found while
adopting it is worth more to Spark now, while #324's design is fresh, than after it has calcified. §7
records what we found; it goes upstream as one issue.

Secondary: the upgrade is already overdue. Coverage has been pinned to `.57` across ten releases, five
of which carry breaking changes that will only get more expensive to absorb.

## 3. Non-goals

- **Not** re-theming the app. `<spark-shell>` ships `--spark-shell-*` custom properties; we match today's
  dark sidebar / `#f8f9fa` main and stop there.
- **Not** rewriting the account or file pages. Only `/home` becomes a composed PO page this round.
- **Not** adopting `triggersRefresh` (preview.64). Additive, no forms need it.
- **Not** turning on `RequireAntiforgery`. It defaults off this preview and becomes true at the next major;
  we dry-run with `WarnOnly` and record the finding (D6).

---

## 4. Findings

### 4.1 The named breaking changes cost Coverage nothing

Audited across `Coverage`, `Coverage.Library`, `Coverage.Tests`:

- **`OnLoadAsync` reshape (`Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)`) — 0 sites.**
  Coverage overrides no lifecycle hook. The only overrides in the four actions classes are
  `IsAllowedAsync`, `GetRowFilterAsync`, `GetProtectedAttributesAsync`, `GetDefaultIncludes` — all unchanged.
- **`ActionsResolver.Attach` — 0 sites.** All four actions classes are `partial` with `[Inject]` fields
  (MintPlayer.SourceGenerators); no hand-written constructor threads framework plumbing anywhere.
- **Optional `ClrType` — 0 sites.** All nine current model files carry a `clrType`.
- **`Read ⇒ Query` (`SparkRightImplications`) — a genuine no-op.** All four grants are `QueryRead/…`, which
  `SparkCombinedActions` already expands to `Query` + `Read`. No bare `Read`, no denials, so the implication
  adds nothing already granted. It *does* matter for the new work (D2).
- **`IPersistentObjectActions<T>`'s three new members — 0 sites.** All four derive from
  `DefaultPersistentObjectActions<T>`; only hand-written implementers break.

### 4.2 The real cost is the `.58`–`.67` pile in between

Four of these fail at **runtime**, not compile time, and one is outside C# entirely — which is what makes
this a medium rather than a small migration. F10–F13 are the `preview.67` additions; note that three of
the four cost Coverage nothing, so the row/PO separation is a far smaller migration for us than its size
upstream suggests.

| # | Change | Release | Coverage site | Failure mode |
|---|---|---|---|---|
| F1 | `Everyone` group refused at load | .60 | `Coverage/App_Data/security.json:3` | **startup throw** |
| F2 | `spark.AddAuthorization()` deleted | .62 | `Coverage/Program.cs:51` | **compile error** |
| F3 | `LocalCredentials` defaults `Disabled` + unreachable-sign-in guard | .60 | `Coverage/Program.cs:54-81` | **startup throw when `GitHub:{env}:ClientId` unset** |
| F4 | `sparkAuthRoutes()` became opt-in | .60 | `ClientApp/src/app/app.routes.ts:12`, `src/spark-auth.setup.ts:61` | **silent — auth pages mount nothing** |
| F5 | `SparkSubQueryComponent` removed | .63 | `pages/account/account.component.ts:14` + `.html:11` | compile error |
| F6 | `AddGitHub` requests `user:email` | .59 | github.com App settings | **invisible to build/test/deploy** |
| F7 | `triggersRefresh` added to model JSON | .64 | `App_Data/Model/*.json`, `modelHashes.json` | CI `--spark-verify-model` drift |
| F8 | SPARK010 analyzer on `MapControllers()` | .60 | `Coverage/Program.cs:271` | build warning |
| F9 | Async custom queries gain real capabilities | .59 | `Coverage/Actions/CommitActions.cs:38-59` | behavioural — see 4.3 |
| F10 | Query wire: `result.data`/`totalRecords` → `items`/`totalItems` | .67 | — | **none** — Coverage reads neither |
| F11 | Column renderers: `attribute` input → `column` (`SparkCellColumn`) | .67 | all 7 registered renderers | **compile error** — see 4.7 |
| F12 | `IQueryExecutor`/`IRowSecurity` gain `CancellationToken` | .67 | — | none — Coverage implements neither |
| F13 | Rows with a null or duplicate id now throw | .67 | `CommitActions.Repository_Commits` | none — `Commit.Id` is a document id |

**The whole selection/action half of the `.67` checklist is inapplicable.** Coverage ships no
`customActions.json` and references neither `CustomActionArgs` nor `SelectedItems` — verified by grep
across `Coverage/` and `Coverage.Library/`. So `SubmittedSelectedItems`, `selectedItemIds`,
`MaterializeAsync` and `RestrictToIds` are all no-ops for this upgrade. They become relevant only when
the Resync action lands (M7), and even then only if it becomes selection-gated, which it is not.

**F3 is the item most likely to ship unnoticed**: it passes on a developer machine and in production, and
fails only for a fresh clone or a fork-PR runner. **Decision D5 accepts this deliberately.**

**F6 lives on github.com.** Nothing in the build, the test suite or the deploy can observe it; the symptom
is first-time sign-in failing in production with a generic "email not verified". It must be granted by hand
on both the development and production Apps.

### 4.3 One behavioural change worth reading closely

`CommitActions.Repository_Commits` (`Coverage/Actions/CommitActions.cs:38`) is
`async Task<IQueryable<Commit>>` returning an in-memory `commits.AsQueryable()`. Before #294 an async
custom query lost both its declared `sortColumns` and row-filter pushdown; as of preview.65 and unchanged since
(`QueryExecutor.cs:322` and `:337`) both are applied to any `isQueryable` result.

- **Sorting is benign.** The model declares `Date desc`, and `Commit.Date => AuthoredAt ?? FirstSeenAtUtc`
  (`Coverage.Library/Entities/Commit.cs:44`) is exactly what the in-code `OrderByDescending(r => r.AuthoredAt)`
  produces. Same order — but the two orderings are now redundant and the comment at `CommitActions.cs:54-57`
  ("the framework materializes every custom query in full before paging anyway") is stale. Collapse to one.
- **Row-filter composition is the open question.** `GetRowFilterAsync` returns `c => c.Repository!.In(repoIds)`,
  and Raven's `.In()` will now be composed onto an in-memory `EnumerableQuery`. Strong indirect evidence it
  is fine — `RowSecurity.GetCompiledFilter` (`RowSecurity.cs:581`) already compiles and invokes this same
  expression per row today at preview.57, since `FilterAsync` is the enforcement point and pushdown is only
  an optimization. **Resolved by SP1** (§6).

### 4.4 The home page: what the feature supports, and what it does not

> ⚠️ **Written against `preview.65`; largely resolved by `preview.67`.** Kept because the *reasoning*
> still explains the design, but the constraints below are stale: S1 (a row needs a readable `Id`) is now
> enforced loudly by `QueryResultProjector` instead of collapsing the grid; S2 (a query on a virtual type
> returns an empty list) is gone — that is the feature `preview.67` shipped; S3 (`sortColumns` ignored for
> an `IEnumerable`) is fixed by an in-memory sort fallback. The "does not exist" items — an `image` data
> type and a per-row route — both shipped too (`image`/`url` data types, and a `rowRoute` input now
> forwarded through `spark-query-card` and `spark-query-list`).
>
> ⚠️ **But `image` does not remove the need for an avatar renderer.** Verified in
> `spark-grid-cell.component.html:22-30`: it emits `<img>` only when the value is non-empty, consults no
> `rendererOptions`, and hardcodes `alt=""`. An account with no avatar URL would render an **empty cell**,
> where today it shows `bi-person`/`bi-people`. The fallback, and the translated installed badge, both
> remain custom renderers — see plan M6 step 5. **No functionality from `home.component.html:61-79` is
> given up in this adoption**; the built-ins remove workarounds, not behaviour.

Verified against upstream source. Three of the four pieces the owner asked about already work; the fourth
does not exist.

**Works today, no upstream change needed:**

- **A query over a transient, non-document CLR type.** `Custom.*` over `IEnumerable<T>` where `T` is a plain
  class with no Raven collection, no `[Entity]`, and no `SparkContext` registration. Precedent ships:
  `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/ProjectColumn.cs`. This is exactly the abandoned
  `MyAccountRow` shape found at `Coverage/bin/Debug/net10.0/App_Data/Model/MyAccountRow.json` (a stale build
  artifact, no source counterpart, no git history — an earlier attempt at this same idea).
- **`renderer` / `rendererOptions` on *query columns*, not just detail attributes.**
  `spark-grid-cell.component.html` dispatches the renderer **before** any dataType branch, so all seven of
  Coverage's existing registered renderers are reusable here.
- **`extraContentTemplate` on a *virtual* PO page.** Gated only on `item()` and `entityType()`, never on
  `clrType` (`spark-po-detail.component.ts:59-60`, rendered at `:188`). Coverage already overrides
  `SparkRouteConfig.poDetail`, which is the required plumbing.
- **A custom action invoked from the shell topbar.** `SparkService.getCustomActions(alias)` /
  `executeCustomAction(alias, name)` are public, accept an alias, and handle the 449-retry modal, client
  operations and `refreshQuery` inside `sendWithEnvelope`. A parentless, selectionless action is legal —
  `ExecuteCustomAction.cs:124` skips the selection rule unless `invokedFromQuery` and skips row security
  when `rowIds.Length == 0`.

**Three sharp edges that fail silently — these shape the design:**

- **S1 — `T` must expose an `Id`.** `QueryExecutor`'s closing `DistinctBy(po => po.Id)` (`:382`) collapses
  the *entire* result set to one row when every row maps to `Id == null`. No error.
  `MyAccountRow` therefore carries `public string Id => Login;`.
- **S2 — the accounts query cannot hang off the virtual Home type.** A `Custom.*` query whose entity type
  is unresolvable *or virtual* returns `([], false)` with no diagnostic
  (`QueryExecutor.cs:381` and `:243`; `SparkTypeResolver.Resolve(null) → null`). It must be declared on the
  real `MyAccountRow` type, and reach the Home page via `Home.json`'s `persistentObject.queries` array,
  which `spark-po-detail.component.html:182` renders as one `<spark-query-card>` per entry.
- **S3 — `sortColumns` is ignored for an `IEnumerable<T>` return.** `ApplySorting` runs only when
  `isQueryable`. Return `.AsQueryable()` if server-side sort is wanted.

**Does not exist:**

- **No `image` data type.** The vocabulary is one switch (`SparkModelShape.cs:173`): string, number, decimal,
  boolean, datetime, date, guid, color, TranslatedString, AsDetail, plus Reference and MultiLineString.
  An avatar needs a custom column renderer — and because query cells render inside `mp-datatable`'s
  **shadow root**, where neither component-scoped CSS nor Bootstrap utilities arrive
  (`spark-grid-cell.component.scss`, measured 2026-08-23), sizing/rounding/fallback must be **inline styles**.
- **No arbitrary row navigation.** A grid row links to exactly one hardcoded place —
  `['/po', alias||id, row.id]` (`spark-query-grid.component.html`) — and `rowsNavigable` was deliberately
  deleted in .62 as "a second authority over a decision the rights model already makes", with nothing
  replacing it. `rowClicked` fires only from that same anchor and does not `preventDefault`.
  **A renderer can emit its own `routerLink`** (router context is guaranteed: `provideRouter` registers
  `ActivatedRoute` on the environment injector), but `cellContent` is projected **inside** the framework's
  anchor, so a renderer on the **first** column with `Read` granted produces nested anchors.
  → **Grant `Query/MyAccountRow` and never `Read`.** `guide-queries-and-sorting.md:610` recommends exactly
  this for "a `Custom.*` query that fabricates rows no detail page could load", and it is what
  WebhooksDemo's `security.json` does for `ProjectColumn`.
- **No way to render a query card's action slot outside the card.** `*sparkQueryActions` is a `TemplateRef`
  instantiated inside `spark-query-card`'s own header; the shell is not an ancestor of the routed page.
  The imperative path (above) is the answer — at the cost of re-implementing the confirm /
  `showedOn` / `selectionRule` / error chain that `spark-query-grid.onCustomAction` already contains.

### 4.5 Controllers and endpoint access control

**`spark.AddControllers()` / `UseControllers()` do not authorize anything.** The package is one ~95-line
source file (`libs/controllers/.../SparkControllersExtensions.cs`) that installs no filter and no
middleware. `UseControllers()` is `Registry.AddEndpoints(e => e.MapControllers())`, replayed verbatim by
`MapSpark()`. A controller mounted this way is exactly as authorized as one mounted by
`endpoints.MapControllers()`.

What it actually changes: **pipeline placement** (behind `UseAuthentication` → `UseAuthorization` →
`UseSparkAntiforgery` → `UseAntiforgery` → the XSRF cookie minter) and **antiforgery path scoping**.
SPARK010's message ("Spark's authorization … do not apply") is misleading, and worth an upstream note (§7.5).

**The capability we want is real, and lives entirely in core — no dependency on
`MintPlayer.Spark.Authorization`:**

- `MintPlayer.Spark.Services.SparkAuthorizeAttribute`, in `MintPlayer.Spark`. Two forms:
  `[SparkAuthorize("Upload", "Coverage")]` (checks the `security.json` right `Upload/Coverage` via
  `IAccessControl`) and `[SparkAuthorize(Group = "…")]` (string-matches `IGroupMembershipProvider` output).
  Both must pass when both are given. Handler registered by `AddSpark()` (`SparkMiddleware.cs:82`), enforced
  by stock `AuthorizationMiddleware` via `IAuthorizationRequirementData`. Works on controller classes,
  action methods, and minimal APIs (`.RequireAuthorization(new SparkAuthorizeAttribute(...))`).
- **Resource names are pure invention.** The validator checks only the `<action>/<target>` shape and id
  uniqueness (`SecurityConfigurationValidator.cs`) — no entity lookup, no verb allowlist. `Upload/Coverage`
  and `Admin/RepoSettings` load, validate, index and match. Wildcards `Read/*`, `*/Person`, `*/*` work.
- **Public API from arbitrary code:** `IPermissionService.IsAllowedAsync(action, target)` /
  `EnsureAuthorizedAsync(...)` (scoped, memoised per resource per request, throws `SparkAccessDeniedException`).
- **Coverage's non-Identity callers count as authenticated.** `IsAmbient` drives exactly one decision —
  whether antiforgery is demanded — and is read in only two places, both in `SparkAntiforgeryMiddleware`.
  Once any scheme succeeds, `User.Identity.IsAuthenticated` is true, so `wellKnown.authenticated` applies and
  `ClaimsGroupMembershipProvider` reads `group`/`groups`/role claims. **`UploadsController` can therefore move
  onto a declared `Upload/Coverage` right**, authenticated by the existing `covt_` scheme or the OIDC JWT.

**Three sharp edges, all recorded as upstream asks (§7.4–7.6):**

- **Group names are matched by *display name*, case-insensitively, against any translation**
  (`SecurityFileAccessControl.ResolveGroupIds`). Renaming a group in `security.json` without renaming the
  claim silently drops membership. Group **ids** do not resolve.
- **`[SparkAuthorize(Group=…)]` bypasses the `wellKnown` reservation.** `SecurityFileAccessControl` drops
  provider-returned names resolving to a reserved id; `SparkAuthorizeHandler` does not. A caller carrying
  `group: "Signed-in users"` satisfies `[SparkAuthorize(Group = "Signed-in users")]`, contradicting
  `UseGroupMembershipProvider`'s documented guarantee. **We use the right form, not the group form (D7).**
- **`[SparkAuthorize]` returns a plain 403** to an authenticated-but-denied caller, leaking existence —
  inconsistent with `/spark/po/*` on the *same right string*, which returns Spark's anti-enumeration 404.
  `SparkDenial` is `internal`, so apps cannot match it.

### 4.6 Client-side inventory

- **`<spark-shell>` subsumes ~60 lines of `shell.component.ts`**: `shellState`, `isSidebarVisible`,
  `toggleSidebar`, `onShellToggle`, `setupResizeListener` + the `window.addEventListener('resize')` and its
  `destroyRef` cleanup, `updateSidebarVisibility`, `isAboveBreakpoint`, `afterNextRender`/`PLATFORM_ID`
  guards — plus `::ng-deep mp-shell::part(hamburger){display:none}` and the slotted-nav SCSS.
  **Two of them are already redundant today**: `onMenuItemClick` duplicates ng-bootstrap 22.16's
  `dismissOnNavigate`, and the hardcoded `window.innerWidth >= 768` duplicates `[breakpoint]="'md'"`.
- **Slot names**: `*sparkShellTopbarStart` / `TopbarEnd` / `SidebarHeader` / `SidebarTop` / `SidebarFooter` /
  `MainHeader`, plus `*sparkShellTab` for an extra accordion tab. There is **no** `*sparkShellSidebarTabs`.
  `*sparkShellTopbarEnd` **replaces** its default (`<spark-language-selector/>`), so a host using it must
  re-render the language selector itself.
- **Must survive as app code**: `GitHubLoginService` (popup handshake, popup→redirect fallback,
  concurrency dedupe, the four-code error map), the login-error `bs-alert`, and the GitHub reconnect banner.
- **Page titles today** are all `bs-card-header`: home `app.welcomeTitle` (`home.component.html:5`),
  `app.yourAccounts` (`:32`), account `login()` (`account.component.html:2`), `Upload tokens` (`:22`), the
  file page's hand-rolled breadcrumb (`file.component.html:2-7`), and four panel headers. Generic Spark
  pages **already** render `<h2>{{ breadcrumb || name }}</h2>` — so the breadcrumb-as-title win applies to
  the hand-written pages only, and there is a **double-title risk** if a page keeps its card header after
  becoming a PO page.
- **Untranslated strings block a server-driven surface**: "Resync" (`home.component.html:37`, including its
  `title=` tooltip), "Upload tokens", "Files", and all four panel headers are hardcoded English.
  `programUnits.json` and `Home.json` are `TranslatedString`-shaped (en/fr/nl), so anything moving server-side
  must be translated.

### 4.7 The renderer migration — where the cost moved

`preview.67` separated the row from the persistent object, so the expensive half of this upgrade is now
client-side. Measured against the registrations in `app.config.ts:25-61`:

- **Seven registered renderer names, eight components.** `coverage-bar` maps two distinct components
  (`CoverageSummaryDetailRenderer` for detail, `CoverageBarRenderer` for column); the other six register
  **the same component in both slots**.
- **Those six dual-role components are the awkward case.** A detail renderer still receives `attribute`
  (`EntityAttributeDefinition`); a column renderer now receives `column` (`SparkCellColumn`). A component
  serving both slots must declare **both** inputs, or be split in two. `withDeclaredInputs` filters what
  is not declared, so a component that declares only `attribute` silently receives nothing on the grid
  path — the same shape of failure this upgrade is otherwise removing.
- **`row-attr.ts` is deleted, not migrated.** Spark's `valueFor(item, key)` reads all three row shapes
  (`QueryResultItem`, `PersistentObject`, and the flat record an AsDetail sub-table passes), which is
  exactly what our helper hand-rolled. Call sites become `valueFor(item, 'IsPrivate')?.value`.
- **Three attributes must be marked `"showedOn": "Query", "isVisible": false`** so their values reach a
  renderer without drawing a column: `Repository.IsPrivate` (read by `repo-name-renderer`),
  `Repository.FullName` and whichever attribute `short-sha`'s `rendererOptions.titleAttribute` names.
  Under `preview.65` a row carried every attribute and this was free; the wire now ships only the query
  surface.
- ⚠️ **The `rendererOptions` trap.** `short-sha` reads a sibling whose *name is chosen at the model-JSON
  call site*, not in the component. Forget to mark it and the tooltip is silently absent — nothing in the
  component or the model is wrong. Upstream documents this in the renderer guide; we should comment it at
  the declaration.
- **Eight `AsDetail` attributes exist** (`Build.Coverage/Feedback/GateSnapshot/Patch/Sessions`,
  `Commit.Coverage`, `Repository.Gate/LatestCoverage`), one of them an array (`Build.Sessions`, drawn by
  `build-sessions-renderer`). So we exercise the AsDetail row shape as well as the grid shape — which is
  precisely why the unified `valueFor` matters to us.
- **No collision risk**: no attribute in any model file is named `values` or `attributes`, so Spark's
  element-based shape detection cannot misread a Coverage row.

---

## 5. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | **Home becomes a virtual PO page with a real accounts query.** `Home.json` (no `clrType`) + `HomeActions.OnLoadAsync(id, parent)` for the greeting and live counts; `MyAccountRow.json` + `MyAccountRowActions.MyAccounts(CustomQueryArgs)` for the grid; the grid reaches the page via `Home.json`'s `persistentObject.queries`. | Owner's direction. Exercises the feature end to end; the missing pieces go upstream rather than being designed around. |
| **D2** | **Grant `Query/MyAccountRow`, never `Read`.** | Withholding `Read` is the documented idiom for fabricated rows with no detail page. *Rationale updated for `.67`*: the nested-anchor half is now moot — the framework defaults a row's link to `null` when `clrType` is absent, and the account link comes from `rowRoute`, not a renderer's own `<a>`. What remains is the plain one: `Read` would advertise a detail page that does not exist. `Read ⇒ Query` still applies, so granting it would do both. |
| **D3** | **`MyAccountRow` rows carry a readable `Id` (`=> Login`).** | *Rationale updated for `.67`*: the failure is no longer silent — `QueryResultProjector` throws by name on a null or duplicate row id at first render, and in-memory narrowing reads the id off the row's **runtime** type via `ToString()`, so any readable id works. The decision stands; it is now enforced rather than merely required. No CLR class is needed to carry it. |
| **D4** | **Adopt `spark.AddControllers()`/`UseControllers()` and move all six controllers' authorization into `security.json`.** | Owner's direction, reaffirmed. Closes a real gap: `BrowseController` carries no attribute at all today and `BadgeController` is `[AllowAnonymous]` — the anonymous `/api` read surface sits entirely outside the rights model, and outside the committed `securityPosture.txt` baseline that CI gates. |
| **D5** | **Fail loud on missing GitHub credentials.** Accept the `LocalCredentials = Disabled` startup throw; document that `GitHub:{env}:ClientId` is mandatory to run. | Owner's direction. Matches upstream intent; the cost is that a fresh clone and a fork-PR runner now crash on boot instead of running with sign-in disabled. Recorded here because it is a deliberate regression in contributor experience. |
| **D6** | **Set `AddAntiforgeryProtection` with `PathPrefixes = ["/spark","/connect","/api"]` and `WarnOnly = true`.** | `RequireAntiforgery` becomes true at the next major. Dry-running now surfaces every offending call site while it is still a log line, not a 400. Uploads are unaffected either way — the gate skips non-ambient credentials by design. |
| **D7** | **Use `[SparkAuthorize(action, target)]`, never `[SparkAuthorize(Group = …)]`.** | The right form honours the four-tier precedence, wildcards, combined-action expansion and the `wellKnown` reservation, and lets an operator move who holds a right without a redeploy. The group form does none of that and can be asserted by a claim (4.5). |
| **D8** | **Migrate each `Everyone` grant into *two* grants** (anonymous + authenticated). | `Everyone` was the floor for every caller; moving a grant to `anonymous` alone **narrows** it. Dropping the authenticated half would lock signed-in users out entirely, because type-level rights gate the row rules — `GetRowFilterAsync` / `IsAllowedAsync` would never run. |
| **D9** | **Delete the stale `bin/**/MyAccountRow.json`** and treat the new one as a fresh design. | It has no source counterpart and no git history; leaving it confuses a `modelHashes.json` diff and a synchronize run. |
| **D10** | **Regenerate and commit `securityPosture.txt`, and add `--spark-verify-security` to CI** beside the existing `--spark-verify-model`. | The anonymous surface is deliberate here and about to grow by six controllers; a committed baseline is what makes a future widening reviewable rather than invisible. |

---

## 6. Spikes

Each is cheap and answers something the source could not settle.

> **Revised 2026-08-29.** `preview.67` resolved **SP4**'s premise in the framework rather than in a spike:
> a composed type needs no CLR class, and `QueryResultProjector` now throws by name on a null or duplicate
> row id, so an unrooted model file is no longer the fragile part. **SP2 survives** — a custom avatar
> renderer still draws inside `mp-datatable`'s shadow root — and gains the empty-`AvatarUrl` fallback
> case. **SP3 is re-aimed** from "no nested anchor" to "`rowRoute` navigates from the auto-rendered
> sub-query card". **SP5** is unchanged. A new **SP6** covers the two renderer regressions M9 can break
> silently. The authoritative list is [program-units-plan.md](program-units-plan.md) M8.

- **SP1 — `.In()` over an in-memory queryable.** Boot the upgraded app and open `/query/repository-commits`.
  A `NotSupportedException` there is 4.3's row-filter composition and nothing else. *Resolution: if it
  throws, materialise the filter in `GetRowFilterAsync`'s caller or drop the pushdown by returning
  `IEnumerable<Commit>` instead of `IQueryable<Commit>` — noting S3, that this also gives up declared sort.*
- **SP2 — avatar renderer inside the shadow root.** Render one `<img>` column with inline styles and confirm
  visually (Playwright screenshot) that sizing and rounding survive; confirm a Bootstrap class does **not**.
  Pins the constraint we are about to file upstream.
- **SP3 — nested-anchor check.** With `Query`-only granted, confirm the first column carries **no**
  framework anchor and the renderer's own `routerLink` navigates to `/a/{login}`. If `Read` leaks in via a
  wildcard, this is where it shows.
- **SP4 — unrooted model file across a synchronize.** Run `--spark-synchronize-model` twice and diff
  `App_Data/Model` + `modelHashes.json`. Upstream reasoning says an unrooted `MyAccountRow.json` is never
  rewritten and appears under `files` but not `entities`; this proves it. **A regression here would silently
  delete a hand-authored model file**, so it is worth the two minutes.
- **SP5 — topbar custom action.** Confirm `getCustomActions('myaccountrow')` returns `Resync` and
  `executeCustomAction` refreshes the grid, with `SparkRetryActionModalComponent` mounted and
  `provideSparkClientOperations()` in bootstrap. Watch that `SparkQueryRefreshService` matches on the
  **exact string** passed as `queryId` (alias vs GUID).

---

## 7. Upstream asks — [MintPlayer.Spark#327](https://github.com/MintPlayer/MintPlayer.Spark/issues/327)

**Status: ✅ ALL SHIPPED** in [PR #328](https://github.com/MintPlayer/MintPlayer.Spark/pull/328)
(`10.0.0-preview.67` / ng-spark `22.8.0`, merged 2026-08-29 as `fd570906`), after four rounds of review
from this repo. Filed 2026-08-28 as a single issue; the list below is kept as the record of what was
asked and why.

Two asks shipped **differently** from the way they are worded here, and the difference matters when
reading the workarounds above:

- **Ask 3** grew from "make `clrType` optional on the query path" into the full row/persistent-object
  separation — which is why §4.7 exists and why M9 is the largest client-side item.
- **Ask 1** shipped as an `image` data type with **no fallback and no options**, so it covers a column
  whose URL is always present and *not* the avatar-with-icon-fallback case. See the ⚠️ in §4.4.

Everything else landed as described. Two items found during review are also in: the `Database.*` path's
`RestrictToIds` hook (it was silently never consulted) and in-memory narrowing on a row's **runtime**
type by `ToString()`, so a row keyed by an `int` or a `Guid` narrows like one keyed by a string.

1. **No `image` (or `url`) data type.** Every avatar/thumbnail needs a bespoke renderer, and because query
   cells live in `mp-datatable`'s shadow root, every such renderer must hand-roll inline sizing, rounding
   and a broken-image fallback.
2. **No way to say "rows of this type live at *this* app route".** `rowsNavigable` was correctly dropped as a
   second authority over the `canRead()` gate, but the gap it left is different: an app whose rows have a
   canonical non-PO route must withhold `Read` and re-implement the link in a renderer — and only if that
   column is not first. Proposal: an optional `rowRoute` input that **replaces the target** of the existing
   anchor while leaving the rights gate exactly where it is, and/or a declarative
   `"detailRoute": "/a/{Login}"` template resolved the way `breadcrumb` already is.
3. **Transient-row queries work but are undiscoverable, and three requirements fail silently:** the `Id`
   requirement (`DistinctBy` collapse), the empty-list-no-diagnostic for an unresolvable *or virtual* entity
   type — now the obvious first thing an author tries, since #324 introduced virtual types — and
   `sortColumns` being ignored on the `IEnumerable<T>` path.
4. **`[SparkAuthorize(Group=…)]` does not apply the `wellKnown` reservation**, so a claim can assert
   `authenticated`, contradicting `UseGroupMembershipProvider`'s doc comment.
5. **SPARK010's message is misleading** — `[SparkAuthorize]` is wired by `AddSpark()` and works on either
   mounting; only antiforgery scoping and pipeline ordering are actually at stake.
6. **`SparkDenial` is `internal`**, so an app's own endpoints cannot get the anti-enumeration refusal shape
   that `/spark/po/*` applies to the *same right string*.
7. **A query card's actions cannot be rendered outside the card**, and the imperative path requires
   re-implementing `onCustomAction`'s confirm/selection/error chain plus the private
   `resolveEntityType` + `singularize` fallback. Proposal: export a `SparkQueryActionsService`.
8. **Latent one-line hardening:** `SparkQueryGridComponent.resolveEntityType` does an unguarded
   `t.clrType.endsWith(...)`, and virtual types now put `"clrType": null` in the catalogue.
9. **`[SparkAuthorize]` on a SignalR hub** would resolve the scoped `IAccessControl` from the root provider
   (`context.Resource` is a `HubInvocationContext`) and throw. Document as unsupported or fall back to
   `IHttpContextAccessor`.

---

## 8. Exit criteria

1. `dotnet build` clean; `--spark-verify-model` and `--spark-verify-security` both exit 0 in CI.
2. Signing in with GitHub still works end to end, including first-time provisioning (F6 granted on both Apps).
3. `/` renders the composed Home page: **title from the breadcrumb**, no card header, greeting and live
   counts as read-only attributes, and the accounts grid with avatars, per-account links to `/a/{login}`,
   repo counts, coverage and the installed badge.
4. **Resync sits in the shell topbar**, executes, and refreshes the grid.
5. The sidebar is driven entirely by `programUnits.json` — zero `routerLink`s for navigation in the shell.
6. Anonymous browsing of a public repo still works; the badge endpoint still works unauthenticated; a
   CI upload with a `covt_` token and with an OIDC JWT both still work, now via `Upload/Coverage`.
7. `securityPosture.txt` is committed and its anonymous surface is reviewed line by line.
8. All five spikes resolved and recorded in an as-built section here.

---

## 9. As-built

Implemented 2026-08-29 on `adopt-spark-program-units`. All eight exit criteria met; `dotnet build`
clean, 144/144 tests pass, both verify gates exit 0, and the composed page was driven in a browser.

### 9.1 Where the plan was wrong

**`rowRoute` is unreachable from the composed page.** §4.4 and M6 step 5 assumed the per-account link
would come free from `spark-query-card`'s `rowRoute`. It cannot: the accounts grid is auto-rendered by
`spark-po-detail`, which forwards template slots but **not** `rowRoute` — it is a function input, and
there is no host tag to bind it on. The link is an `account-link` column renderer instead. **SP3 as
written is not testable and was replaced** by verifying the renderer's `routerLink`.

**`MeController` could not take `[SparkAuthorize("Read", nameof(Account))]` alone.** M4's table called
this "an existing right, already granted" — true, but it is granted to the **anonymous** role as well
(public repo pages read account documents). `SparkAuthorizeAttribute` derives from `AuthorizeAttribute`
without `RequireAuthenticatedUser`, so the attribute alone would have opened "the accounts *I*
administer" to callers with no identity. It keeps `[Authorize]` **and** declares the right.

**Fail-loud credentials broke the CI gates.** D5's throw runs inside `AddSpark`, which is *before* the
`--spark-verify-*` blocks return — so the model gate, which is supposed to need no secrets, threw on a
runner that has none. `Program.cs` now exempts any `--spark-*` argument from the credential check;
verified by running both gates with `--no-launch-profile` and no user-secrets.

**A `_comment` key is fatal in `customActions.json`.** It is a flat `Dictionary<string,
CustomActionDefinition>`, so a comment key is an *action* that fails to deserialize — a 500 on every
`/spark/actions/*` request. Harmless in the model files, which deserialize into typed objects that
ignore unknown properties. Documented inside the action's own `description`.

**The empty-path redirect cannot be a child of the shell.** `{ path: '', redirectTo, pathMatch: 'full' }`
beside the shell's other children never runs — the shell's own path is `''`, it consumes the empty URL,
and `/` renders an empty outlet with no error at all. Hoisted above the shell route.

### 9.2 What the plan missed

**The language selector never reaches the server.** `SparkLanguageService` keeps the choice in
`localStorage` only. The composed page has two *server-resolved* strings — the breadcrumb that becomes
the `<h2>`, and the subtitle — which Spark resolves from `Accept-Language`, so the title came back in
the browser's language and never followed the selector. Fixed in-app with an `Accept-Language`
interceptor. ⚠️ It must read the module-level `currentLanguage` signal and **must not**
`inject(SparkLanguageService)`: that service fetches `/culture` and `/translations` from its own
constructor, so injecting it yields `NG0200` on every request and the whole UI renders raw keys.

**`--spark-verify-model` does not gate hand-authored virtual model files.** Probed directly: editing an
attribute label in `MyAccountRow.json` and re-running the gate still exits 0. There is no CLR class to
be out of sync with, so a typo in a `clrType`-less model file ships past CI. `securityPosture.txt`
gates the security half; the model half of a virtual type is ungated.

**`--spark-synchronize-model` strips unknown keys from *generated* files.** So a `_comment` documenting
why `Repository.IsPrivate` is marked `showedOn: "Query"` cannot live beside the attribute; it lives in
`repo-name-renderer.component.ts` instead. Hand-authored virtual files keep their comments — the
synchronizer does not touch them at all.

**`Repository.IsPrivate` was the real M9 regression, and `titleAttribute` was not.** `IsPrivate` was
stored as `showedOn: "PersistentObject"` — free under preview.65, where a row carried every attribute,
and silently fatal under `.67`, where a row carries only the query surface. Now `"Query,
PersistentObject"` with `isVisible: false`; verified in a browser (`ArromanchesBNB private`). The
`short-sha` tooltip needed nothing: no attribute in this repo sets `rendererOptions.titleAttribute`.

**Six dual-role renderers needed no split.** M9 anticipated splitting or widening them. In fact none of
the eight renderers ever read `attribute` or `formData` — the inputs were declared and dead. Deleting
the dead declarations makes registering one component in both slots correct rather than silently broken.

**The install-App link cannot be a `url` program unit.** Its address is per-environment
(`coveragedevelopment` vs `coverageproduction`, resolved from configuration at request time) and
`programUnits.json` is static. It stays in the Angular extra content, where it is correct everywhere.

### 9.3 Deviations worth knowing

- **Resync moved again — see §11.** It was briefly a topbar button calling `executeCustomAction` by
  hand; it is now a custom action on the Home persistent object, drawn by Spark in that page's own
  action bar.
- **`/home` is kept as a redirect**, not retired: it is the OAuth handler's failure redirect in
  `Program.cs`, the post-sign-in return URL, and whatever is bookmarked. No `returnUrl` literals moved.
- **The file page keeps its `bs-card-header`.** It is a hand-rolled *breadcrumb trail*, not a title,
  and the code viewer has no PersistentObject, so it stays hand-written as §4.5 said.
- **The account page was converted after the fact — see §10.**

### 9.4 Still outstanding

- **F6 — the `user:email` account permission is NOT granted yet** on either GitHub App. Nothing in the
  build can verify it; it must be ticked by hand in both App settings pages. First-time sign-in cannot
  resolve a user's address without it.
- **SP1 was not exercised**: `/query/repository-commits` renders, but the `.In()`-over-`EnumerableQuery`
  composition in `CommitActions.GetRowFilterAsync` (§4.3) has not been driven with a filter applied.
- **Antiforgery is `WarnOnly`.** Flip it once the logs show nothing a strict gate would reject.

---

## 10. The account page — converted to a generic detail page

Follow-up to §9, decided after seeing `/a/{login}` still render a `bs-card-header` title.

**There is no page-header component in `@mintplayer/ng-spark` 22.8.0.** Every exported selector in the
package was enumerated; there is no `spark-page-header`, `spark-page-title` or `spark-breadcrumb`. The
generic title is inline markup — `<h2>{{ currentItem.breadcrumb || currentItem.name }}</h2>` in
`po-detail/src/spark-po-detail.component.html:38` — and Spark copies it by hand into `po-create`,
`po-edit` and `query-list` rather than sharing it. The action bar is the same story: duplicated between
`po-detail` and `query-list`, and its `.spark-actionbar` style is component-scoped SCSS that is not
shipped in `dist/`, so a host page cannot reuse it. `<spark-shell>`'s `title` input feeds only the
sidebar `<h5>`; `*sparkShellMainHeader` is app chrome above the router-outlet, not a per-page title.

So "use the Spark feature" meant **being** a generic page rather than imitating one, and Account was
already most of the way there: `Account.json` declared `breadcrumb: "{Login}"` and
`queries: ["account-repositories"]`, and `Read/Account` was already granted to both roles because
`QueryRead` expands to `Query` + `Read`. Nothing new was needed in `security.json`.

What changed:

- **`vanity-routes.ts` is deleted.** Account was its only entry, so `resolveVanityRoute` always returned
  null. With it went `PoDetailPageComponent`'s resolving/spinner state and a **full PersistentObject
  pre-fetch performed on every detail navigation of every type**, purely to decide whether Account
  should redirect. The component is now its template plus two accessors.
- **The redirect reversed direction**, matching what Repository and Commit already did:
  `accountRedirectGuard` in `vanity-redirects.ts` resolves `/a/{login}` → `/po/account/{id}`. People hold
  the readable URL, so that is the one that forwards. ⚠️ The address bar therefore now shows the
  document id (`/po/account/Accounts%2F48772716`), as it already did for repositories and commits —
  `/a/{login}` remains a working entry point, and both `account-link-renderer` and `file.component.html`
  still build it by hand.
- **The upload-tokens card became `AccountTokensPanelComponent`**, mounted through
  `extraContentTemplate`. Extras render last, so the order is title → attributes → repositories →
  tokens, which is the hand-written page's order with an attribute card inserted. Its own heading stays
  a `bs-card-header`: it is a section inside a page, not the page's title.
- **`Account.AvatarUrl` is `dataType: "image"`** so the attribute card shows the avatar rather than a raw
  URL, and **`InstallationId` is `isVisible: false`** — an internal id the hand-written page never showed.
  Both survive `--spark-synchronize-model`: `SparkStringPresentations.Preserves` explicitly keeps a
  hand-set `image`/`url`/`MultiLineString` on a string property, and `isVisible` is an author choice.

The hand-written page also fetched `/api/browse/accounts/{login}` on every visit solely to obtain
`acc.id` for the grid's `parentId`; the PO route supplies it, so that round-trip is gone too.

**Not done, and worth its own decision:** the duplicated title/action-bar markup is a genuine gap in
Spark — `po-detail`, `po-create`, `po-edit` and `query-list` all hand-copy it, and no consumer can reuse
it. A `spark-page-header` upstream would fix that for every app and would give the file page a real
heading too. Filed as a possibility, not as work.

---

## 11. Resync — a custom action on Home, not app chrome

§9.3 recorded Resync as a topbar button. That was wrong-shaped: the button lived in app chrome shown
on every route, was hidden again by a route check (`onHome()`), and reached the server through a
hand-written `executeCustomAction` call whose first argument had to be an object *type* — a detail
easy to get wrong, and got wrong once (passing the query alias 404s).

Home **is** a persistent object, so the action belongs on it. `Resync/MyAccountRow` became
`Resync/Home` in `security.json`, and `showedOn: "detail"` now means something: Spark draws the button
in the Home page's own action bar, beside Back, next to the grid it refreshes. The shell went back to
being chrome — no `SparkService`, no `Router`, no route check, no action plumbing — and `HOME_ROUTE`
lost the two entries that existed only to feed that call.

Two things surfaced while wiring it:

- **`refreshOnCompleted: true` and an explicit `RefreshQuery` both fire.** Leaving both on ran the
  accounts query twice per click. `refreshOnCompleted` is now `false`; the action names what to
  refresh.
- **A blanket refresh does not re-read the persistent object.** The `Accounts`/`Repositories` counts
  above the grid are Home attributes, and a resync that changes org membership would leave them
  contradicting the rows underneath — the exact case the button exists for. `ResyncAction` now
  re-reads `IMyAccountsService` after invalidating and emits `refreshAttribute` for both counts
  alongside `refreshQuery`. Verified on the wire: the action's response carries all three operations
  with real values.

Also: `Account.AvatarUrl` is `showedOn: "Query"`. The built-in `image` type is sized for a grid cell
(`max-height: 2.5em`) and unconstrained on a detail page, so the avatar rendered oversized there. It
stays on the query surface, where the accounts grid draws it through the `account-avatar` renderer.
