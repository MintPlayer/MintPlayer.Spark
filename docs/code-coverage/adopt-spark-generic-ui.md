# Adopting ng-spark's generic UI — PRD & plan

Status: **M1–M11 built** on branch `adopt-spark-generic-ui` — every milestone is done and the branch
is ready for its single squash PR to `master` (see the milestone sections for what each delivered).
Current pins: `MintPlayer.Spark 10.0.0-preview.51`, `@mintplayer/ng-spark 22.0.11`.

## Upstream scoreboard (2026-08-17)

Every issue this adoption raised, and where it stands:

| Issue | Subject | State |
|---|---|---|
| [#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236) | Complete row-level security | ✅ PR #237 (preview.44) |
| [#239](https://github.com/MintPlayer/MintPlayer.Spark/issues/239) | Async `GetRowFilterAsync` | ✅ PR #240 (preview.45) |
| [#243](https://github.com/MintPlayer/MintPlayer.Spark/issues/243) | Per-row `can` must intersect type-level rights | ✅ PR #244 (preview.46) |
| [#241](https://github.com/MintPlayer/MintPlayer.Spark/issues/241) + [#245](https://github.com/MintPlayer/MintPlayer.Spark/issues/245) | AsDetail renderer value + `item` row context | ✅ PR #250 (ng-spark 22.0.11) |
| [#254](https://github.com/MintPlayer/MintPlayer.Spark/issues/254) | `[IgnoreProperty]` | ✅ PR #255 |
| [#253](https://github.com/MintPlayer/MintPlayer.Spark/issues/253) | Synchronize must not delete attributes | ✅ PR #263 — and it grew into a full model-sync lifecycle rework (see M11) |
| [#242](https://github.com/MintPlayer/MintPlayer.Spark/issues/242) | `parentId` ignored for `Database.*` queries | ⏳ open — worked around with `Custom.*` parent-scoped sources |
| [#251](https://github.com/MintPlayer/MintPlayer.Spark/issues/251) | Reference breadcrumb resolves to the wrong document | ⏳ open — worked around by loading the referenced PO |
| [#252](https://github.com/MintPlayer/MintPlayer.Spark/issues/252) | No value-formatting seam (raw ISO dates) | ⏳ open — worked around with a `date-time` renderer |

Every ClientApp page today is hand-written: it fetches from the bespoke `/api` controllers and
renders its own `<bs-table>`, instead of reusing the generic renderer components shipped in
`@mintplayer/ng-spark`. This document records what the library actually ships, classifies every
page (drop entirely / recompose from library parts / keep custom), names the blockers that make
"just drop the page" impossible today, and lays out the phased plan — including the upstream Spark
work required first.

> Companion documents: [PRD.md](PRD.md) §2 (hard architectural rule: generic code goes upstream),
> [PLAN.md](PLAN.md), and in MintPlayer.Spark: `docs/PRD-CoverageHandoff.md` (which already
> observed that `sparkRoutes()` is "mounted but unreachable").
>
> Research basis: a three-agent investigation (2026-08-14) — ng-spark library API inventory,
> Coverage backend Spark-model/controller audit, and a MintPlayer.Spark documentation survey —
> plus a hand-read census of all five pages. Claims carry `file:line` evidence.

---

## 1. The finding

The app is Spark-native in plumbing only. `app.routes.ts:11,18` spreads `sparkAuthRoutes()` and
`sparkRoutes()`, `app.ts` mounts `<spark-retry-action-modal>`, and pages import only
`SparkLanguageService`, the `t`/`resolveTranslation` pipes, and `SparkAuthService`. **No page uses
`spark-query-list`, `spark-po-detail`, `spark-sub-query`, or `spark-po-form`. No page calls
`SparkService` for data.** All data flows through hand-written `[ApiController]`s
(`Coverage/Controllers/*.cs`) over raw `IAsyncDocumentSession`.

That is not an accident. `Program.cs:36-44` deliberately runs Spark authorization in **DenyAll**
mode (no `security.json` exists in the repo):

```csharp
// Deliberately DenyAll (no security.json): Spark's generic data endpoints are
// fully denied. All data access goes through our own /api controllers, which
// mirror the viewer's GitHub permissions. This also sidesteps the open
// R4-H1 finding (row-level auth missing on query-execute/stream endpoints.
```

So the generic `/query/:queryId` and `/po/:type/...` routes are routable but return denied for
every entity, and nothing links to them. **Any adoption of the generic UI is blocked on
authorization first, rendering second.** The rest of this document is structured around that fact.

---

## 2. What `@mintplayer/ng-spark` ships (verified against 22.0.8)

Full inventory in the research; the parts that matter for this plan, by composability tier:

### Tier A — drop-in anywhere (plain inputs, no route dependency)

| Component | Inputs | What it renders |
|---|---|---|
| `spark-sub-query` | `queryId`, `parentId`, `parentType` (all required) | `bs-card` + server-fetched sortable `bs-datatable` of a query filtered by parent; honors `renderMode`, custom cell renderers; first cell links to `/po/:type/:id`. No action bar/search/create. |
| `spark-po-form` | `entityType`, `[(formData)]`, `validationErrors`, `showButtons`, `isSaving`, `parentId?`, `parentType?` | Full create/edit form: tabs/groups/columnSpan, per-datatype editors, Reference/Lookup pickers, AsDetail inline/modal, drag-reorder, inline validation. Bridge with `nestedPoToDict`/`dictToNestedPo`. |
| `spark-reference-picker`, `spark-lookup-picker` | value/options | standalone pickers |
| 23 pipes (`attributeValue`, `referenceLinkRoute`, `asDetailColumns`, …) | — | per-cell/field formatting reusable from any hand-written template |

### Tier B — route-driven generic pages (already mounted via `sparkRoutes()`)

`spark-query-list`, `spark-po-detail`, `spark-po-create`, `spark-po-edit` read `:queryId`/`:type`/
`:id` from `ActivatedRoute` — **they take no id inputs**, so they can't be embedded in a bespoke
page. Customization: `showCustomActions`, `extraActionsTemplate`, and (detail only)
`extraContentTemplate` (receives `$implicit: PersistentObject` + `entityType`).
`spark-po-detail` auto-renders one `spark-sub-query` per entry in `EntityType.queries[]`.
`sparkRoutes(SparkRouteConfig)` can swap each `loadComponent` (shipped but undocumented upstream).

### Tier C — the extension seam: attribute renderers

`provideSparkAttributeRenderers([{ name, detailComponent, columnComponent, editComponent? }])`
plus `renderer`/`rendererOptions` on the attribute in the model JSON injects a custom cell/field
component into **all four generic hosts** (query list, detail, sub-query, form) via
`NgComponentOutlet`. This — not a hand-written page — is the intended way to get custom visuals
like a coverage bar into a generic grid.

### Not shipped (would stay ours or go upstream)

No standalone action bar, no breadcrumb-trail component, no nav-menu component, and — critically —
**no way to customize the row links** the grids emit: they hardcode
`['/po', entityType.alias || entityType.id, row.id]` (query-list, sub-query, and the
`referenceLinkRoute` pipe alike).

---

## 3. Page census

> **This section records the starting state (2026-08-14), not the current one.** The `repo` and
> `commit` pages listed below no longer exist — M9 deleted them and their routes now forward to the
> generic detail page. Read it as the analysis the plan was built on; `app.routes.ts` is the
> current answer.

Routes from `Coverage/ClientApp/src/app/app.routes.ts:6-21`. All five pages follow the same
pattern: subscribe to route params, `await` a `BrowseService`/`AccountsService`/`TokensService`
call (plain `HttpClient` → `/api/...`), render manually.

| Page | Route | Classification | Spark-shaped parts rendered by hand |
|---|---|---|---|
| home | `/home` | **fully custom** | none — the accounts list is GitHub-installation data (`/api/me/accounts`: installed badge, repo count, aggregate coverage, reauth banner), not a Spark query |
| account | `/a/:login` | **partially generic** | Card 1 (`account.component.html:10-47`): a `<bs-table>` of the account's repositories = the `Account → GetRepositories` relation rendered by hand. Card 2 (upload tokens) is fully custom — `ApiToken` is deliberately not in the Spark model |
| repo | `/r/:owner/:repo` | **partially generic** | Commits card (`repo.component.html:79-121`): `<bs-table>` = `Repository → GetCommits` by hand, plus branch filter, Δ-vs-previous column, coverage bar. Badge management, trend chart, CI-examples tabs are custom |
| commit | `/r/:owner/:repo/c/:sha` | **partially generic** | Builds table (`commit.component.html:35-73`): `Commit → GetBuilds` with `Sessions` AsDetail by hand — structurally the closest match to `spark-sub-query`. Folder tree + sunburst + breadcrumb are custom (backed by `BuildTreeSummary`/`FileCoverage`, deliberately outside the model) |
| file | `/r/…/c/:sha/f` | **fully custom** | none — line-by-line `bs-code-snippet` viewer over `FileCoverage`, not modeled in Spark |

**Pages droppable entirely today: none.** And the question is subtler than "drop the page": the
generic equivalents (`/po/Account/:id`, `/query/GetRepositories`) are *already mounted* — they are
just denied, unlinked, and would render the wrong thing (see §4). The realistic wins are
(a) making the generic routes actually work as a secondary/admin surface, and (b) recomposing the
three hand-rolled query tables onto `spark-sub-query` + attribute renderers.

---

## 4. Why nothing can be dropped today — the gap list

> **Also the starting state.** Every gap below is now closed or worked around — the scoreboard at
> the top of this document is the live status, and `security.json` replaced DenyAll in M2. Kept
> because the *reasons* are what justify the upstream issues.

1. **DenyAll authorization (the hard blocker — smaller than first thought).** Every Spark data
   component fetches through `SparkService` → `/spark/queries/...` / `/spark/po/...`, which deny
   everything (`Program.cs:36-44`). *Correction after the follow-up Spark investigation
   (2026-08-14):* the `Program.cs` comment cites "R4-H1", but per Spark's
   `docs/prd/PRD-SecurityAudit.md` that identifier is fabricated and the real findings (H-2/H-2a)
   were **resolved in Spark M5 (2026-08-09)** — row-level security ships in Spark core today as
   `IRowSecurity` + `DefaultPersistentObjectActions<T>.IsAllowedAsync(action, entity)`, enforced
   on every read path and on Edit/Delete. WebhooksDemo's `GitHubProjectActions` already implements
   almost exactly Coverage's rule (GitHub org membership). What genuinely remains upstream is
   tracked in [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236): a
   projection-path batching bug, expression pushdown (today's filter is post-materialization,
   O(collection) — a real problem for Coverage's commit/build volumes), create-side WITH CHECK,
   custom-action row gating, per-viewer attribute redaction, and per-row permissions for the UI.
2. **Anonymous read.** Public-repo browsing works logged-out (`BrowseController` has no
   `[Authorize]`). Expressible today: grant `Query`/`Read` to the `Everyone` group in
   `security.json` and let the row filter narrow to public rows (Spark#236 open question 2 asks
   to bless and document exactly this pattern).
3. **Secret leakage in the model.** `Repository.BadgeToken` is `isVisible: true`,
   `showedOn: "Query, PersistentObject"` (`App_Data/Model/Repository.json:133-146`). Harmless
   while DenyAll; the moment queries open, every viewer of a repo row sees its badge token.
   Same review needed for `Account.InstallationId`. Visibility must become per-viewer (the
   `canManage` notion), which today Spark's static model can't express — `IsVisible` is
   outbound-advisory only (the value still ships in JSON). Per-viewer attribute redaction is
   G4 of [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236).
4. **Row links are hardcoded to `/po/...`.** Coverage's canonical URLs are `/a/:login`,
   `/r/:owner/:repo`, `/r/…/c/:sha`. A `spark-sub-query` of repositories would link its rows to
   `/po/Repository/:id`. Either those generic routes become acceptable secondary destinations, or
   ng-spark needs a link-mapping seam (upstream).
5. **Empty `queries[]` on every entity type — and parent scoping doesn't exist upstream.**
   `App_Data/Model/*.json` declare `persistentObject.queries: []` everywhere, so no parent→child
   relation is modeled: a generic Account detail page would show no repositories subquery.
   Worse, the follow-up investigation found that for `Database.*` queries the server **validates
   `parentId`/`parentType` and then ignores them** (`Execute.cs:97-109` → `QueryExecutor.cs:36-44`)
   — only `Custom.*` sources can scope to a parent, so a `spark-sub-query` over a `Database.*`
   query would return the whole collection. Flagged in Spark#236 as a related finding needing its
   own upstream issue before Coverage's M4 recomposition can use model-declared relations.
   *Status after Spark PR #237 (2026-08-15): still unfixed, and the follow-up issue the PR plan
   promised was never filed — this is currently tracked nowhere upstream.* Interim option:
   declare the sub-queries as `Custom.*` sources reading `args.Parent`.
6. **Custom columns exceed the generic cell model.**
   - *Coverage bar* (`app-coverage-bar` over a `CoverageSummary` AsDetail): expressible today as a
     registered `columnComponent`/`detailComponent` attribute renderer — this one is pure win.
   - *Sparkline* (account page): data comes from a separate batched endpoint
     (`/api/browse/accounts/{login}/sparklines`), not from the row. Needs a server-computed
     attribute (index-stored recent-percentages array) + a column renderer, or stays custom.
   - *Δ vs previous commit* (repo page): cross-row computation (`repo.component.ts:235-242`);
     either an index-computed attribute on Commit or it stays custom.
   - *Branch filter* (repo page): a parameterized query filter — no generic UI exists for
     query parameters beyond search.
7. **Version skew.** Backend is `MintPlayer.Spark 10.0.0-preview.43`; ClientApp pins
   `ng-spark ^22.0.8` / `ng-spark-auth ^22.1.0`. Any upstream additions land in new previews of
   both — adoption milestones must ride the usual upgrade train.

---

## 5. Target architecture

- **Vanity pages stay.** `/a/:login`, `/r/...`, commit and file pages keep their routes, layout,
  and custom panels — but their query tables become `spark-sub-query` instances (or a thin
  wrapper), and coverage rendering becomes a registered attribute renderer used by *both* the
  custom pages and the generic ones.
- **Generic routes become real.** `/po/Account/:id`, `/po/Repository/:id`, `/query/GetAccounts`
  etc. go from denied-and-unlinked to a working, row-secured secondary surface (useful
  immediately as an admin/debug view, and as the free UI for any future entity — that's the point
  of the framework).
- **The model becomes honest.** Related queries declared, secret attributes hidden per-viewer,
  computed columns (sparkline data, Δ) pushed into the model/index where cheap.
- **`/api/browse` shrinks but does not disappear.** Tree/hierarchy/file/source endpoints (backed
  by `FileCoverage`/`BuildTreeSummary`), `/api/me`, tokens, badges stay custom — they are not
  query-shaped.

---

## 6. Plan

Legend: 🟩 MintPlayer.Spark PR · 🟦 Coverage repo. One PR per repo per milestone
([PLAN.md](PLAN.md) conventions).

### M1 — Complete row-level security in Spark 🟩 (✅ DELIVERED 2026-08-14, Spark PR #237)

**Goal:** superseded by the upstream PRD filed as
[Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236) (2026-08-14), implemented
by [Spark#237](https://github.com/MintPlayer/MintPlayer.Spark/pull/237) (squash `e251208`,
closes #236). All six gaps shipped: batched projection reload (G0), `GetRowFilter` expression
pushdown with the derivation rule + projection fallback (G1), create-side WITH CHECK with
`SparkSystemContext` for module principals (G2), row-gated custom actions via `Submitted*` (G3),
`GetProtectedAttributesAsync` redaction incl. AsDetail read-side + top-level write shielding (G4),
and the per-row `can` block consumed by `spark-po-detail` (G5) — plus a security sweep binding
document ids to the authorized type. **Carriers: `MintPlayer.Spark 10.0.0-preview.44` /
`@mintplayer/ng-spark 22.0.9`** (preview.43 has none of it).

**Not delivered upstream (still open):** #236-M6 (Raven Skip/Take pushdown — perf only), and the
**`parentId` ignored for `Database.*` queries** finding — verified still live on merged master
and *no follow-up issue was filed* despite the PR plan saying there would be. That one blocks M4
below.

### M2 — Coverage adopts the security seam 🟦 (✅ BUILT 2026-08-15, on preview.45/Spark#240)

**Goal:** open `/spark` reads safely. The async-hook dependency
([Spark#239](https://github.com/MintPlayer/MintPlayer.Spark/issues/239), see
[spark-async-row-filter.md](spark-async-row-filter.md)) shipped as
[Spark#240](https://github.com/MintPlayer/MintPlayer.Spark/pull/240) (preview.45), so the rules
are written async-first against `GetRowFilterAsync`.

As built:

1. Pins on `10.0.0-preview.45` / `@mintplayer/ng-spark 22.0.9`. Breaking changes checked:
   no Spark custom actions (`Submitted*` rename inert), no lookup references
   (`Read/LookupReferences` requirement inert).
2. `App_Data/security.json`: `QueryRead` on Account/Repository/Commit/Build for `Everyone` —
   the row filters are the only gate behind that, per the guide's anonymous-read warning.
3. `Coverage/Services/SparkVisibility.cs` — per-request Task-memoized snapshots (owners via
   `IGitHubAccessService`, visible repo ids via one Raven query), because the framework memo
   is per-(type, action): the hook still runs 3× per detail read.
4. `Coverage/Actions/`: `RepositoryActions` (`!IsPrivate || owners.Contains(OwnerLogin)`
   pushdown + `BadgeToken` redaction + `Account` include), `AccountActions` (`InstallationId`
   redaction only — accounts are public), `CommitActions` (pushdown IN over visible repo ids +
   `Repository` include), `BuildActions` (per-row predicate parsing the commit-id shape
   `Commits/{repoGitHubId}/{sha}` — no owner fields to push down on; in-memory after the
   memoized repo-id query, plus `Commit` include). `Enumerable.Contains` used in expressions
   (translates to RQL `in` AND works compiled in-memory; Raven's `.In()` is query-only).
5. Writes stay denied at the type level, so the WITH CHECK path is unreachable and the
   machine-principal trap doesn't apply.
6. Related-query declarations (`EntityType.queries[]`) deliberately **moved to M4**: declaring
   them before parent-aware sources exist would make generic detail pages render sub-queries
   containing the whole (row-filtered but unscoped) collection, since `Database.*` queries
   drop `parentId` upstream.

**Exit criteria — VERIFIED LIVE (Playwright + wire, 2026-08-15):** anonymous
`/spark/queries/repositories/execute` returns only public rows (128, zero private) with
`BadgeToken` nulled + `isVisible: false`; detail reads pass the compiled row check;
`account-repositories?parentId=Accounts/48772716` returns exactly the account's 3 repos.
Two findings fixed during verification:
1. **RavenDB cannot translate `Contains` in these filters on .NET 10** — a `string[]` receiver
   binds to span-based `MemoryExtensions.Contains`, and even `List<string>.Contains` inside
   `!x || list.Contains(y)` throws `TypedParameterExpression`. Raven's `.In()` is the shape that
   both translates and (verified live) evaluates in-memory for the compiled single-row checks.
2. **The per-row `can` block overclaimed upstream** — computed from the row rule alone, never
   intersected with type-level rights, so anonymous viewers got `can: {edit, delete} = true` and
   Edit/Delete buttons on the generic detail page. Filed as
   [Spark#243](https://github.com/MintPlayer/MintPlayer.Spark/issues/243), **fixed upstream by
   [Spark#244](https://github.com/MintPlayer/MintPlayer.Spark/pull/244) (preview.46)** — the
   block now intersects type-level rights server-side. Coverage's interim `x => false`
   write-action guard was removed again with the preview.46 bump; the rules are back to a single
   visibility expression per type.

### M3 — Attribute renderers for coverage visuals 🟦 (✅ BUILT 2026-08-15 — 🟩 gap found and filed)

**As built:** `CoverageBarRendererComponent` (one class, column + detail slots) registered as
`coverage-bar` in `app.config.ts`; `renderer: "coverage-bar"` declared on the three
`CoverageSummary` AsDetail attributes (`Repository.LatestCoverage`, `Commit.Coverage`,
`Build.Coverage`). ⚠️ The upstream gap this milestone anticipated materialized: **renderers on
AsDetail attributes receive `undefined`** (`EntityMapper.cs:276` nulls the flat value; every
ng-spark host passes only `itemAttr?.value`) — filed as
[Spark#241](https://github.com/MintPlayer/MintPlayer.Spark/issues/241) proposing a value fallback
to the nested PO. The renderer already handles that shape, so Coverage lights up with a package
bump and zero code changes when #241 ships. Until then generic hosts show the bar's empty state
(the column was blank before, too). The `parentId` scoping bug was also finally filed upstream as
[Spark#242](https://github.com/MintPlayer/MintPlayer.Spark/issues/242).

Original goal:

**Goal:** one `coverage-bar` renderer (column + detail) registered via
`provideSparkAttributeRenderers`, driven by `renderer: "coverage-bar"` on the `LatestCoverage` /
`Coverage` AsDetail attributes; reused by generic pages and (M4) the custom pages.

**Exit criteria:** the generic repository list shows the same coverage bars as `/a/:login`.

### M4 — Recompose the hand-rolled tables 🟦 (✅ BUILT 2026-08-15)

**As built:** parent scoping via `Custom.*` sources on the Actions classes
(`RepositoryActions.Account_Repositories`, `BuildActions.Commit_Builds` — the Spark#242
workaround; the framework still applies row filters, sorting, and includes on top). Declared as
model queries with aliases `account-repositories` / `commit-builds`, plus `EntityType.queries[]`
on Account/Commit so the generic detail pages auto-render the same sub-queries. The account
page's repositories card and the commit page's builds table are now `<spark-sub-query>`
(`account.component.html`, `commit.component.html`); per-session parse detail moved to the
generic Build detail page. The sparkline survives as the `coverage-sparkline` renderer bound to
`Repository.FullName` (label "Trend") — it works today (scalar value), while `coverage-bar` waits
on Spark#241. `showedOn` trimmed across the model so the generic grids show curated columns
(secrets/ids/plumbing are detail-only). New `/api/browse/accounts/{login}` returns the account
document id (sub-query `parentId`); the commit payload gained `id` for the same reason (its
`builds` array is now unused by the SPA — trim in a follow-up). Test sweep: 54/54 passed.

Original goal (scope fixed by D2–D4):

1. Account page card 1 → `<spark-sub-query queryId="GetRepositories" [parentId]=... parentType="Account" />`,
   with the sparkline preserved as a `FullName` column renderer (D3). Parent scoping needs a
   parent-aware query source until the upstream `Database.*` parentId bug is fixed (§4.5).
2. Commit page builds table → `GetBuilds` sub-query (Sessions renders as the AsDetail sub-table),
   same parent-scoping note.
3. Repo page commits table: **out of scope** (D4) — keeps the hand-written table.

**Exit criteria:** the account repositories card and the commit builds table are rendered by
`spark-sub-query`; `BrowseController` endpoints that became redundant are deleted (endpoints
still consumed elsewhere — e.g. the repos list feeding the token-scope dropdown — stay).

### ~~M5 (optional) — Row-link seam in ng-spark 🟩~~ (DROPPED per D2, 2026-08-15)

The user accepted `/po/...` Spark routes as the grid link targets, so no link seam is needed.
(An implementation-ready design was produced during the investigation — `provideSparkLinks` +
`SparkLinkService` over a `SPARK_LINK_RESOLVERS` token, covering the three grid anchors,
`referenceLinkRoute`, and post-save navigation — and can be revived if that decision ever flips.)

### M6 — Grid parity with the master-branch cards 🟦 (🟩 row-context seam filed upstream)

**Finding (user, 2026-08-15, vs coverage.mintplayer.com):** replacing the hand-written cards
changed their shape. Master's account card shows **Repository (name + inline "private" badge) ·
Coverage (bar) · Trend (sparkline) · Latest commit (7-char sha link)**; master's builds card shows
**Run (`runId.attempt`) · Status (+ finalize reason) · Sessions (job + parse badges) · Coverage ·
Created**. The generic grids showed raw schema columns instead. The cards/grids/columns must match
master; the `/po` links inside them are fine (D2).

**Coverage-side (now):**
1. Model JSON: relabel + reorder + re-trim `showedOn` so the Query columns are exactly master's
   sets — Repository card: `Name`("Repository"), `LatestCoverage`("Coverage"), `FullName`("Trend"),
   `LatestCoverageSha`("Latest commit"); Build card: `CiRunId`("Run"), `Status`, `Sessions`,
   `Coverage`, `CreatedAtUtc`("Created"). `OwnerLogin`/`IsPrivate`/`WorkflowName`/
   `FinalizedAtUtc`/`FinalizeReason` become detail-only.
2. New value-only renderers, registered alongside the existing two: `short-sha` (7-char monospace,
   on `LatestCoverageSha`) and `build-sessions` (on the `Sessions` AsDetail array — renders the
   per-session job/parse badges once the value arrives, "—" until then).
3. Coverage bars and Sessions cells light up when
   [Spark#241](https://github.com/MintPlayer/MintPlayer.Spark/issues/241) ships (AsDetail
   renderer value).

**Upstream (filed):** the row-context-for-renderers seam — optional `item` input on the renderer
contracts, passed only when declared via a `reflectComponentType` filter (which also fixes a
latent upstream bug: a renderer omitting any of the current inputs throws at `NgComponentOutlet`
binding time). Unlocks the remaining master-parity cells: the inline "private" badge next to the
name, the `runId.attempt` composite, and a linkable latest-commit cell.

**Exit criteria:** the account and commit cards show master's exact column sets/labels/orders;
short-sha renders; the remaining cells upgrade automatically as the upstream pieces ship.

**✅ COMPLETE (2026-08-15):** [Spark#250](https://github.com/MintPlayer/MintPlayer.Spark/pull/250)
shipped #241 + #245 as `@mintplayer/ng-spark 22.0.11` (implemented from the PRD posted on #241).
Coverage adopted it: `rendererValue`/`item` verified live — the generic commit detail renders the
Coverage bar (50.0% on the seeded JObject commit), the auto-rendered Builds sub-query shows
**Run (`302.1`, the computed property) | Status | Sessions ("… Parsed" badges via the
build-sessions renderer) | Coverage (bars) | Created**, and two new `item`-consuming renderers
complete the parity cells: `repo-name` (inline "private" badge next to the name) and the upgraded
`short-sha` (links to the vanity commit page derived from the row's FullName). Every open
upstream issue from this adoption is now closed (#236→#237, #239→#240, #243→#244, #241+#245→#250);
only #242 (Database.* parentId — worked around with Custom.* sources) remains open.

### M7 — Rich detail-page parity on the generic surface 🟦 (✅ BUILT 2026-08-15)

**Finding (user):** `/po/repository/...` rendered only the attribute card, while master's
`/r/{owner}/{name}` has the badge, the interactive coverage-over-time graph, commits, and setup
instructions. Requirement: both URLs render the same panels, staying on the **generic Spark
pages** customized only through framework seams.

**As built:** the repo page's panels were extracted into shared standalone components
(`RepoTrendPanelComponent`, `RepoSetupPanelComponent`, new `RepoBadgePanelComponent`) — the vanity
`/r` page renders identically through them — and the app now overrides
`sparkRoutes({ poDetail })` with a thin `PoDetailPageComponent` that renders the stock
`<spark-po-detail>` plus, via its `extraContentTemplate` slot, the three panels when the entity
type is Repository (owner/name derived from the PO's FullName). A parent-scoped
`Custom.Repository_Commits` query declared as `Repository.queries: ["repository-commits"]` gives
the generic detail its Commits card automatically. Verified live on `/po/repository/…` (seeded
acme/demo): attribute card with bar/sparkline/sha-link renderers → Commits sub-query → Coverage
badge card → interactive Coverage-over-time chart → Set up coverage uploads tabs.

**Extended same day — Commit parity + polish (user goal: clicking a repo from the account page
must land on the full master-like content, URL free to differ):**
- The commit page's Files card (sunburst + drill-down folder list) extracted into shared
  `CommitFilesPanelComponent`; the vanity `/r/…/c/:sha` page uses it unchanged, and the generic
  `/po/commit/…` renders it via a `CommitFilesExtrasComponent` that resolves owner/name by
  **loading the referenced Repository PO** (deliberately not the reference breadcrumb — see below).
- Commits grids: message moved out of the cell into a `title` tooltip on the sha link — on the
  vanity repo page's table and, generically, via `rendererOptions: { "titleAttribute": "Message" }`
  on `Commit.Sha`'s `short-sha` renderer (verified live: each sha cell carries its message).
  Commit's Query columns are now master's set: Commit (sha + tooltip) | Branch | Coverage | Date.
- 🐛 Found upstream while wiring this:
  [Spark#251](https://github.com/MintPlayer/MintPlayer.Spark/issues/251) — a Reference
  attribute's resolved breadcrumb can name the wrong document (`Repositories/999001` →
  "JObject", a repo that doesn't even exist, while the doc's own breadcrumb is "acme/demo").
  Coverage sidesteps it by loading the referenced PO.

### M8 — Canonical-route forwarding from the generic grids 🟦 (✅ BUILT 2026-08-15)

**Finding (user):** clicking a repository in the account grid still landed on
`/po/repository/…`, which — even with M7's panels — is not the product page, and Spark has no
link-resolver seam to point grid rows elsewhere (D2's consequence).

**As built (downstream-only, no upstream dependency):** the `poDetail` override now *forwards*.
`spark/vanity-routes.ts` maps the entity types that own a purpose-built page to their canonical
route — Repository → `/r/{owner}/{name}`, Commit → `/r/{owner}/{name}/c/{sha}` (repository
resolved by **loading the referenced PO**, not the breadcrumb — Spark#251), Account → `/a/{login}`
— and `PoDetailPageComponent` navigates there with `replaceUrl: true` (so Back returns to the
grid). Types with no vanity page (Build) and any object whose canonical route can't be derived
fall through to the stock generic detail, still enriched by M7's `extraContentTemplate` panels.
Verified live: clicking CodeCoverage in `/a/MintPlayer` lands on `/r/MintPlayer/CodeCoverage`;
`/po/commit/…` lands on `/r/acme/demo/c/abc1234…` with the coverage ring and Files panel.

**Note on the "missing graph":** the local database has no coverage history for the MintPlayer
repos, so no trend chart renders there — on **either** page (the local vanity page shows the same
empty trend). On a repo with data (`/r/acme/demo`, 50%) the full chart renders. The difference vs
production is data, not code.

### M9 — The generic detail page *is* the repository/commit page 🟦 (✅ BUILT 2026-08-15)

**Decision (user):** now that Spark renders custom cells (renderers), computed attributes, curated
columns and sub-queries, the hand-written pages should go — reuse `spark-po-detail` and keep only
what the framework genuinely can't express. URLs are free to change.

**As built — M8 inverted.** Repositories and commits *are* the generic Spark detail pages:
- **Deleted** `pages/repo/*` and `pages/commit/*` (the hand-written header cards, commits table
  with branch filter + Δ, badge markdown block, files tree wiring) — ~270 lines.
- `/r/{owner}/{name}` and `/r/{owner}/{name}/c/{sha}` survive as **`CanActivateFn` guards** (no
  components) that resolve the document id and forward into `/po/…`; README badge markdown and
  existing shared links keep working. `RepoInfo` gained `Id` for that lookup.
- The coverage **ring** survives as the renderer's *detail slot*
  (`CoverageSummaryDetailRendererComponent`: ring + bar + "x/y lines · a/b branches · n files"),
  with the compact bar staying the *column slot* — the framework's own two-slot design replacing
  hand-written markup.
- What remains custom is only what Spark can't express: the badge/trend/setup panels and the
  commit file tree (mounted through `extraContentTemplate`), the code viewer page (no PO of its
  own), the account page (upload-token management) and the home page.
- `vanity-routes.ts` now forwards **Account only**; Repository/Commit deliberately absent (they'd
  loop against the guards above).

**Knowingly dropped with the old pages:** the commits branch filter (no generic query-parameter
UI — D4) and the Δ-vs-previous column (cross-row computation — D3).

### M10 — Cell fidelity: coverage bar, Δ, formatted dates 🟦 (✅ BUILT 2026-08-15)

**Finding (user):** the generic grids still read thinner than master's table — coverage should be
a `bs-progress` with the percentage beside it, the Δ column was gone, and dates showed as raw ISO
strings.

**As built:**
- **Coverage bar** — already correct (`coverage-bar` column renderer draws `bs-progress` + "82.4%");
  it only looked absent because the local database had no coverage. Seeded realistic history into
  RavenDB to confirm. The *detail* slot draws the ring + bar + "3888/4718 lines · 1633/2595
  branches · 194 files".
- **Δ** — a cell can't see its neighbours, so the delta is computed **server-side in the query**:
  `CommitActions.Repository_Commits` materializes the repository's commits in index order
  (`AuthoredAt` coalesced with `FirstSeenAtUtc`), walks the sequence pairwise, and fills a
  transient `Commit.CoverageDelta`; a `coverage-delta` renderer draws it (+green / −red / neutral
  zero, one decimal — master's exact formatting). No coverage on either side of a pair → no delta,
  rather than a fake 0. Costs nothing extra: Spark materializes every custom query in full before
  paging anyway. Row security is unaffected (element type stays `Commit`, so the row filter still
  composes). The value is `showedOn: "Query"` only — a list-relative number is meaningless on a
  detail page.

  ⚠️ **Rejected: stamping a stored delta at finalize time against `ParentSha`** (built first, then
  reverted). `ParentSha` is a *documented live defect* — the push webhook writes `evt.Before` (the
  previous ref tip) unconditionally while uploads write the PR base with `??=`, so a later push
  clobbers a PR base (`roadmap-2026-08.md` §7 T2.1: "a trap armed for the first consumer"). A
  delta on top of it would have been that first consumer. It also needs a parent *document*, which
  usually doesn't exist (a five-commit push creates one commit — the head), and it answers a
  different question than the deleted UI did (graph-relative vs list-relative). A commit-graph
  delta belongs to T2.1 patch coverage, with its own explicit `BaseSha`.
- **Dates** — `Commit.Date` (computed `AuthoredAt ?? FirstSeenAtUtc`, since upload-only commits
  have no `AuthoredAt`) rendered by a `date-time` renderer → "Aug 15, 2026, 10:14:49 PM";
  `rendererOptions.format` overrides. The commits sub-query sorts through
  `Commits_ByRepository`, whose `AuthoredAt` field already coalesces the same way — a computed CLR
  property is renderable but **not sortable** in RavenDB.

**🐛 Upstream gap filed: [Spark#252](https://github.com/MintPlayer/MintPlayer.Spark/issues/252)** —
ng-spark has *no value-formatting layer at all* (`AttributeValuePipe` branches only on breadcrumb /
AsDetail / lookup / boolean, then returns the raw value; zero hits repo-wide for `DatePipe`,
`Intl`, `LOCALE_ID`; no `format`/type-hint field on the attribute model). Proposed a
`SPARK_VALUE_FORMATTERS` token with built-in `datetime`/`date` defaults keyed on the Spark
language signal via `Intl` — when it ships, Coverage's `date-time` renderer becomes deletable.
The issue also notes a one-line bug it turned up: `spark-sub-query` is missing the
`[indeterminate]` binding the other two hosts have, so a null boolean reads as `false` there.

### ~~Hazard: `--spark-synchronize-model` will delete our computed attributes~~ (✅ FIXED upstream)

The hazard was: `Build.Run` and `Commit.Date` are get-only computed properties that
`ModelSynchronizer` refused to emit (its filter required `CanWrite`) *and* deleted on the next run
(it rebuilt the attribute array wholesale), while `Commit.CoverageDelta` was kept out of the
database only by convention.

All three are resolved upstream, so **the warning against running the Synchronize profile is
lifted** — and M11 has since run it and confirmed it (see M11's "as built"):

- **[PR #263](https://github.com/MintPlayer/MintPlayer.Spark/pull/263)** — attributes with no CLR
  property are carried over **by reference** (so the `Id` clients key on survives) and logged once
  each; get-only properties now become attributes with `IsReadOnly = !CanWrite` and `IsRequired`
  forced false; indexers are excluded. `--prune-orphaned-attributes` was considered and rejected.
  ⇒ `Run` and `Date` become *generated* attributes rather than hand-added ones.
- **[PR #255](https://github.com/MintPlayer/MintPlayer.Spark/pull/255)** — `[IgnoreProperty]`
  ships, applied as a union-wide veto across the synchronizer, `AttributeNames` generation,
  `ReferenceResolver`, `SyncActionHandler`, lookup `Extra` dictionaries and replication's payload +
  write-authorization list. Coverage doesn't need it today (`CoverageDelta` belongs *in* the model),
  but it's the sanctioned tool if that changes.
- `Commit.CoverageDelta` is already `[JsonIgnore]`d here, which stays the right mechanism —
  upstream deliberately did **not** add a "not persisted" attribute.

### M11 — Upgrade to preview.51 🟦 (✅ BUILT 2026-08-17)

PR #263 reworked the whole model-synchronization lifecycle, and three of its changes were breaking
for this app:

1. **`SynchronizeModelsIfRequested` is gone.** `Coverage/Program.cs` read
   `app.UseSpark(o => o.SynchronizeModelsIfRequested<CoverageSparkContext>(args));`; it is now a
   bare `app.UseSpark()` plus a builder-phase call that returns before the app runs:
   ```csharp
   if (builder.SynchronizeSparkModelsIfRequested(args))
       return;                       // ordinary return from Main
   ```
   The context type now comes from `UseContext<T>()`, so it isn't named twice. (The old form also
   hid a defect: `Environment.Exit(0)` ran outside the Development guard, so passing the flag in
   production killed the process with exit code 0 — a restart loop reporting success.)
2. **`App_Data/modelHashes.json` is new and must be committed** — generated by
   `dotnet run --spark-synchronize-model`. A **production app now refuses to start on a model
   mismatch** (`SparkModelOutOfSyncException`; Development only warns), so shipping without it, or
   with a stale one, breaks the deploy rather than the page. The hash covers the CLR shape, never
   the JSON's labels/renderers/groups/order, so our hand-set renderers don't invalidate it.
3. **`--spark-verify-model` (exit 3 on drift)** is the intended PR gate → now a step in
   `.github/workflows/ci.yml`, after the build and before the tests. It writes nothing and opens no
   database, so it needs no RavenDB service in CI. A matching `Verify model` launch profile makes
   the same check one click away locally.

**`spark.AddIndexesFrom(...)` was considered and left out.** It exists to let an assembly *other
than the entry assembly* contribute indexes and `[FromIndex]` projections; `Commits_ByRepository`
lives in `Coverage/Indexes`, which the entry-assembly fallback already covers, so declaring it would
change nothing but the number of ways the same fact is stated. It becomes required the moment that
index moves to `Coverage.Library` — and #263 documents the failure mode if that move is made without
it: the index is silently never created and its projection never registered, so index-computed
fields come back null with no error at all.

**As built.** All exit criteria met:

- Pins on `10.0.0-preview.51` across `Coverage`, `Coverage.Library`, `Coverage.Tests`. Spark now
  requires `MintPlayer.SourceGenerators[.Attributes] 10.20.0`, so those two moved up from 10.19.0 as
  well — `NU1605` (warning-as-error) makes the downgrade a hard build failure, not a warning.
- `Program.cs` on the builder-phase call, `app.UseSpark()` argument-free.
- `modelHashes.json` committed; `--spark-verify-model` reports in sync (exit 0); tests 54/54; the
  app boots with no model warning.
- **The preservation fix is proven.** A full synchronize produced zero semantic change to
  `App_Data/Model/*.json`: every attribute id, label, renderer, `order`, `showedOn` and
  `isReadOnly` — including the hand-set ones on `Run` and `Date`, which were already
  `isReadOnly: true` — survived untouched. The one generated value that moved is
  `Commit.CoverageDelta`'s `dataType`, `number` → `decimal`, which is preview.51 mapping `double?`
  more precisely; the `coverage-delta` renderer coerces with `Number(...)` and is unaffected.
- The *textual* diff is nonetheless large (~320 lines per side) because the synchronizer now writes
  the attribute array **sorted by name** rather than in declaration order. Pure reordering — worth
  knowing before reading the diff of this commit, and worth remembering the next time a model diff
  looks alarming.

**Possible future upstream refinements (not filed):** (a) the link-resolver seam
(`provideSparkLinks`, designed during this work) would let grids emit the canonical URL directly
and make this forwarding unnecessary; (b) a registered per-entity-type *detail panel* seam
(`provideSparkDetailPanels([{ type, component }])`, mirroring the attribute renderers) would make
even the thin `poDetail` wrapper unnecessary.

### Detail-page polish (2026-08-17)

Three small corrections found by looking at the finished page:

- **The coverage ring is gone.** The detail slot of the `coverage-bar` renderer drew a ring *and* a
  bar for the same number — two charts, one datum. The bar carries the percentage and the
  line/branch/file counts, so the ring only added visual weight. `components/coverage-ring/` had no
  other caller and was deleted with it.
- **`Repository.FullName` (label "Trend") is `showedOn: "Query"`.** The sparkline is a grid
  affordance; on the detail page it was a second rendering of coverage the page already shows.
- **`Repository.GitHubId` is `isVisible: false`** — an internal identifier with no reader.
  `isVisible` is the "never" flag, distinct from `showedOn`'s "not on this surface": every ng-spark
  surface filters `isVisible && hasShowedOnFlag(...)`, and server-side `IsWritableBySchema` refuses
  writes to an invisible attribute as well. (`GitHubId` stays `isRequired: true`, which is only
  reachable through a create form — and Repository has no create right, so nothing can hit it.)
- **`Account.GitHubId` too.** The same attribute name, the same rationale, and on the one surface
  that shows it (`/po/account`) it reads as noise for the same reason. `Account.InstallationId`
  deliberately stays visible: `AccountActions.GetProtectedAttributesAsync` already redacts it
  per-viewer, so it is shown to managers on purpose rather than by omission.

All three JSON edits are **synchronize-stable**, which is worth recording because it isn't obvious:
`isVisible` is never reassigned for an existing attribute, and `ShowedOn` is reassigned only when
the entity has a `[FromIndex]` projection type — neither Repository nor Account has one. None of
them touches `modelHash`: `ModelFileShape` names visibility as presentational and does not hash
`showedOn` at all, which is by design so that a hand-authored label can never stop an application
from starting. Confirmed with `--spark-verify-model` after the edits — same hash
(`d64bfee6…`), so `modelHashes.json` stays valid and CI still passes.

Model JSON is read once through a `Lazy` in a singleton `ModelLoader` with no file watcher, so a
running host must be **restarted** to pick up model edits — unlike renderer changes, which the dev
server live-reloads.

### Sequencing

*As executed:* M1 (upstream) → M2 → M3 → M4 → M6 → M7 → M8 → M9 → M10 → M11, with M5 dropped (D2).
Tests batched at the end of each milestone per the global test policy.

**Remaining work, in order:**
1. ~~**M11** — the preview.51 upgrade.~~ ✅ done 2026-08-17.
2. The branch is ready for the single squash PR to `master`. **Deploy note:** `modelHashes.json`
   must reach the server — production *refuses to start* on a mismatch, so a deploy that ships the
   entities without the hash file fails closed at boot rather than degrading a page. Verified that
   it does: the Web SDK's default `**/*.json` content glob copies it to the publish output, the same
   mechanism that already carries `App_Data/Model/*.json` and `security.json` (confirmed present in
   the build output), and the `Dockerfile` copies the publish directory wholesale. No csproj or
   Dockerfile change was needed.
3. Optional follow-ups, all currently worked around and each blocked only on an upstream issue:
   drop the `Custom.*` parent-scoped query sources once [#242](https://github.com/MintPlayer/MintPlayer.Spark/issues/242)
   lands; drop the referenced-PO load in `commit-files-extras` once
   [#251](https://github.com/MintPlayer/MintPlayer.Spark/issues/251) lands; delete the `date-time`
   renderer once [#252](https://github.com/MintPlayer/MintPlayer.Spark/issues/252) lands.

---

## 7. Explicitly rejected

- **Client-side data-source abstraction in ng-spark** (letting `spark-sub-query` fetch from
  `/api/browse` instead of `/spark`): duplicates the query pipeline client-side, leaves DenyAll
  unsolved for the generic pages, and violates "different layer, different abstraction". The
  server-side security seam is the deep fix.
- **Adopting `/po/...` as the canonical URLs**: breaks the shareable vanity URLs
  (`coverage.mintplayer.com/a/PieterjanDeClippel`) and README badge links for zero user benefit.
- **Modeling `FileCoverage`/`BuildTreeSummary`/`ApiToken` into Spark** just to genericize the
  file/tree/token views: these are deliberately outside the model (per-entity XML docs); their
  UIs are genuinely bespoke (code viewer, sunburst, token-reveal flow).

## 8. Decisions

| # | Decision | Resolution (implementation defaults, 2026-08-15 — each cheap to reverse) |
|---|---|---|
| D1 | ~~Shape of the upstream row-security hook~~ | Moved to [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236), shipped in PR #237: both hooks, with derivation (expression is source of truth when present; predicate refines) |
| D2 | Row links from generic grids | **RESOLVED by the user (2026-08-15): `/po/...` Spark routes are accepted as the grid link targets — permanently, not as an interim.** The link-resolver seam (old M5) is dropped; a full implementation-ready design for it exists in the investigation record should it ever be wanted. What must match master instead is the **visual grid parity** (M6) |
| D3 | Sparkline + Δ columns | **Sparkline survives** via a column renderer on `FullName` that batch-fetches `/api/browse/accounts/{login}/sparklines` (renderers are Angular components — they can inject services). **Δ stays hand-written** (cross-row computation, see D4) |
| D4 | Branch filter on commits | **Keep hand-written** — the repo page's commits table is out of M4 scope (branch filter + Δ have no generic home) |
| D5 | Generic surface user-facing or admin-only? | **User-facing**: `spark-sub-query` grids become part of the product pages; `/po`/`/query` routes are a legitimate secondary surface now that rows are secured |
