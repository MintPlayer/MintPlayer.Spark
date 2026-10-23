# Dashboard layout system (#530): PRD and plan

**Issue:** [#530](https://github.com/MintPlayer/MintPlayer.Spark/issues/530) · **Branch:** `feat/issue530-dashboard` ·
**Date:** 2026-10-23 · **Status:** Implemented (M1–M10), not yet pushed. All of Q1–Q7 are decided (§12, rounds 1–3).

## 0. Status

- 2026-10-23: three investigators covered the server, the client and the apps. Their findings are in §2, with
  file:line evidence. The tile-manager sources in `C:\Repos\mintplayer-ng-bootstrap` were checked by hand (§2.3).
- 2026-10-23, round 1 (owner): the app ships a default layout. A signed-in user edits it, and the result is stored
  per user under a natural id. An app can have **several** dashboards. A narrow-screen design is wanted (§4.7).
- 2026-10-23: the narrow-screen gap was **measured** in a browser, for both our tile manager and Infragistics'
  open-source one (§2.4).
- 2026-10-23: M2–M10 implemented, committed locally on `feat/issue530-dashboard`, not pushed:
  - `954ff197`: the package (`libs/boards/MintPlayer.Spark.Boards`, `11.0.0-preview.1`), the client
    (`@mintplayer/ng-spark/boards`, ng-spark 22.44.0), and the server and vitest tests.
  - `5819adab`: Fleet, with E2E `BoardsTests`.
  - `0032041d`: SeoTester and MintPlayer.
  - `e6b7ab25`: CodeCoverage.
  - M10 docs: [guide-boards.md](guide-boards.md),
    [release-notes-ng-spark-22.44.0.md](release-notes-ng-spark-22.44.0.md), and the README fixes.
  - Gates checked locally: every app builds, and `--spark-verify-model --spark-verify-security` exits 0 for Fleet,
    SeoTester, MintPlayer and CodeCoverage. The one test sweep at the end is still to run.
  - Decisions made while building are D17–D20 in §12. §5's table is the plan; D18 and D19 record where the build
    differs from it.
- 2026-10-23: the `persistentObject` tile now renders through `spark-po-detail` (D15, superseded; `8efc7b5e`).
  Inside a tile it still draws tabs and a card per group, which is too heavy. Two investigators compared the
  owner's `isDefault` group flag with alternatives. Q9 in §11 has the options and a recommendation, and M11 in §10
  is the plan once Q9 is decided.

## 1. Goal

The issue asks for three things:

1. **An app can ship a default layout.**
2. **A user can edit their dashboard layout**: drag and resize tiles.
3. **A user can add tiles** that point to a PersistentObject, a Query, and similar targets they have access to.

It ships as a **new library**: a NuGet package and a new `@mintplayer/ng-spark` entry point. Demo apps and
production apps adopt it in the **same pull request** (one-PR rule). If the ng-bootstrap tile manager needs fixes
(§2.3), those land in the same unit of work too, published first.

### Not to be confused with `MintPlayer.Spark.Dashboards`

`libs/dashboards/MintPlayer.Spark.Dashboards` (#504, [spark_dashboards_PRD.md](spark_dashboards_PRD.md)) shows
**administrator figures**. Each dashboard there is a live virtual PO type filled by an `IDashboardProvider`. It has
no layout, no per-user state and no editing. This library is the opposite: **a per-user arrangement of tiles over
existing targets**. The two compose. A host-metrics dashboard is an ordinary PO tile here (§4.4). This library does
not reference the Dashboards package. The name must keep the two apart (Q1).

## 2. What Spark already gives us (evidence)

### 2.1 Server

- **Add-on library pattern:** `<SparkLibraryAlias>` names the App_Data layer and prefixes rights and slots
  (`libs/settings/.../MintPlayer.Spark.Settings.csproj:10`). App_Data holds `Model/*.json`, `programUnits.json`,
  `security.json` (slot groups such as `settings:administrators`, which the app binds) and `translations.json`.
  `AddSettings(this ISparkBuilder …)` is idempotent and uses `TryAdd*` (`SparkSettingsExtensions.cs:34-56`).
- **Endpoints:** `[assembly: EndpointsMethodName("MapSparkHistoryEndpoints")]` (`libs/history/.../AssemblyInfo.cs:2`).
  The generator emits the method, and the extension wires it with
  `builder.Registry.AddEndpoints(e => e.MapSparkHistoryEndpoints())` (`SparkHistoryExtensions.cs:64`). The registry
  is at `libs/spark/MintPlayer.Spark.Abstractions/Builder/SparkModuleRegistry.cs:120,141`.
- **Per-user documents:** there is no general preferences store. The pattern to copy is `NotificationPreference`:
  - a fixed id `NotificationPreferences/{userId}/{scope}` (`libs/notifications/.../NotificationPreference.cs:7,35`)
  - an owner stamped by an `IBeforeSave` interceptor (`NotificationInterceptors.cs:14-41`)
  - an `OwnRows.For` row filter (`NotificationActions.cs:45-55`)
- **Current user:** `ISparkCurrentUser { Id, IsAuthenticated }` is scoped. It reads `NameIdentifier`, falling back
  to `sub` (`HttpContextSparkCurrentUser.cs:29-39`).
- **Authorization:** `IPermissionService.IsAllowedAsync(action, target, ct)` (`IPermissionService.cs:109,119`).
  - A PO target needs `Read/<EntityType>`.
  - A query target needs `Query/<EntityType>`.
  - The menu applies the same rule (`Endpoints/ProgramUnits/Get.cs:47,125-131`; `GetAliases.cs:23,31`).
- **Listing targets:**
  - `IModelLoader.GetEntityTypes()` (`ModelLoader.cs:9-13`)
  - `IQueryLoader.GetQueries()` and `ResolveQuery(idOrAlias)` (`QueryLoader.cs:6-13`)
  - `IProgramUnitsLoader.GetProgramUnits()` (`ProgramUnitsLoader.cs:8-11`)

  The menu endpoint's `ResolveTarget` (`ProgramUnits/Get.cs:118-140`) maps a unit to (action, entity type). The
  picker reuses it.

### 2.2 Client (`libs/node_packages/ng-spark`, version 22.43.0)

- **Entry points:** a new entry point is a folder holding `ng-package.json` and `index.ts`, and nothing else
  (`dashboards/ng-package.json`). The dev mapping in `tsconfig.base.json:6` picks it up. `@angular/cdk ^22` and
  `@mintplayer/ng-bootstrap ^22.25.0` are already peer dependencies.
- **Tile content:**
  - `spark-query-card` (`grid/src/spark-query-card.component.ts:45`) is already a query tile. SeoTester and MintPlayer
    show two of them on their home pages.
  - `spark-query-grid` (`spark-query-grid.component.ts:90`) takes `queryId`, `presetFilters`, `columns`,
    `reloadToken` and `rowRoute`. It has **no height input**. It virtual-scrolls only for virtual-scrolling queries
    (`:688`) and pages otherwise.
  - `spark-po-detail` (`po-detail/src/spark-po-detail.component.ts:69`) and `spark-query-list` read `ActivatedRoute`
    and have **no id inputs**, so they cannot be embedded as they are (S2).
- **Menu:** `spark-program-units.component.ts:96-120` renders the rights-filtered `getProgramUnits()`. Model:
  `models/src/program-unit.ts:3`.
- **Existing `dashboards` entry point:** `spark-dashboard` renders a `metadata.dashboard` PO as a static CSS grid
  with `columnSpan` (`spark-dashboard.component.ts:44-89`). It uses inline `styles` and plain Bootstrap utility
  classes, which breaks house style. This library does not copy that.

### 2.3 The drag/resize grid: `@mintplayer/ng-bootstrap/tile-manager` (installed 22.25.0, repo at 22.26.0)

The tile manager is a Lit web component (`mp-tile-manager`, `libs/mintplayer-web-components/tile-manager`, 1043
lines) with Angular wrappers `bs-tile-manager`, `bs-tile` and `bs-tile-header`. Nothing in Spark uses it yet.

- **Manager inputs:** `columnCount` (null means auto-fit from `minColumnWidth`, default `200px`), `minRowHeight`
  (`8rem`), `gap`, `dragMode` (`header` by default), `resizeMode` (`hover`) and `animateReflow`.
- **Manager outputs:** `layoutChange(TileLayoutSnapshot)` and `gestureBlocked`, plus a `captureLayout()` method.
- **`TileLayoutSnapshot`** is `{ id, position: { colStart, rowStart, colSpan, rowSpan } }[]`. It is JSON as it
  stands and is the storage format (§4.2).
- **Tiles:** `bs-tile` takes `id`, `position`, `disableMove`, `disableResize` and `label`.
- **Packing:** a vertical-compact packer (`utils/pack.ts:17-24`). Locked tiles stay put, movable tiles are pushed
  and compacted, and a gesture that cannot be placed snaps back.
- **Keyboard and accessibility:** arrow keys move or resize (`:907-989`), with aria labels, a live announcer and
  an instructions string.
- ⚠️ **Gap:** keyboard steps clamp to the effective column count (`:985-989`), but the render path has no clamp.
  The result is measured in §2.4: tiles collapse to slivers on narrow screens.
- **Wide screens:** in auto-fit mode, the column count also *grows* with the width. It was 6 columns at
  1280 px, and would be about 12 at 2560 px. A layout designed for 6 columns would then fill only the left half of
  the screen. `spark-board` therefore always sets `columnCount` explicitly (§4.7).

### 2.4 Narrow screens: measured, ours and Infragistics' (2026-10-23)

Our tile manager is modelled on the Infragistics Ignite UI tile manager, which is open source
(`igniteui-webcomponents` 7.4.1, MIT). Both were put on one page with the same four tiles:

- A spans 3 columns.
- B sits at column 4.
- C spans 2 columns from column 4.
- D spans 1 column.

Both used `min-column-width: 200px`, and the page was measured with `getBoundingClientRect` in Chromium
(playwright_node). The page is at `<scratchpad>/tm/index.html`.

| Viewport | `igc-tile-manager` | `mp-tile-manager` 2.20.0 |
|---|---|---|
| 1280 px | 5 tracks. A 730 px wide, B, C and D 237–484 px | 6 tracks. A 755 px wide, B, C and D 246–501 px |
| 700 px | A 624 px wide. **B is 0 px, C 10 px, D 0 px** | A 666 px wide. **B is 2 px, C 10 px**, D 217 px |
| 390 px | A 314 px wide. **B is 0 px, C 10 px, D 0 px** | A 357 px wide. **B is 2 px, C 10 px**, D 341 px |

- **Cause:** both components use `grid-template-columns: repeat(auto-fit, minmax(<min>, 1fr))`. A tile that names a
  column past the real tracks makes CSS grid add **implicit columns of zero width**.
- **Effect:** there is no horizontal scroll. The tiles simply become invisible, and the document's `scrollWidth`
  equals the viewport width.
- **Infragistics does not solve this either.** Its only clamp, `adjustTileGridPosition`
  (`components/tile-manager/position.js:45-60`), runs only when an explicit `columnCount` changes. It also
  **overwrites** the tile's `colStart` and `colSpan`, so the user's wide layout is lost after one narrow render.
- **What Infragistics gets right** is placement: most of its tiles have no `colStart` and are auto-placed in
  `position` order with `grid-auto-flow: dense`.

## 3. Product scope

- **Boards (decided, round 1):** an app declares **any number of named boards**. `home` is the convention, and a
  board can be placed on any route. Each board has a default layout. Each user has at most one stored layout per
  board.
- **View mode** is the default. Tiles are fixed and show live content.
- **Edit mode** is a toggle. In it the user can:
  - drag and resize tiles
  - remove a tile
  - add a tile from a picker (§4.5)
  - rename a tile
  - reset the board to the app's default

  Saving is explicit (Save or Cancel), not on every gesture (Q6).
- **Built-in tile kinds:**

  | Kind | Target | Renders | Access check |
  |---|---|---|---|
  | `query` | query id or alias | `spark-query-card` (searchable, paged, fixed height) | `Query/<EntityType>` |
  | `count` | query id or alias, optional preset filter | a single number from the query's total (S4) | `Query/<EntityType>` |
  | `persistentObject` | entity type + object id | read-only attribute values. Live virtual POs, such as Dashboards' host metrics, update | `Read/<EntityType>` |
  | `programUnit` | program unit id | a launcher tile (icon, title, link) | as the menu |
  | app kinds | registered by the app | the app's own component | declared by the app (§4.4) |

- **Anonymous visitors** see the default layout, filtered to what they may access, and cannot edit it.

## 4. Architecture

### 4.1 Package

- **NuGet:** `MintPlayer.Spark.<Name>` at `libs/<alias>/`, version `11.0.0-preview.*`, `<SparkLibraryAlias>` = the
  alias (Q1).
- **npm:** a `@mintplayer/ng-spark/<alias>` entry point, in a 22.x **minor** (22.44.0). The major stays 22.
- **Registration:** `spark.Add<Name>(o => …)`, idempotent like `AddSettings`. Endpoints come through
  `[assembly: EndpointsMethodName]` and `Registry.AddEndpoints`.

### 4.2 Data

**The app's default layout** lives in `App_Data/boards.json` (name per Q1). It sits beside `programUnits.json` and
follows the same rules: libraries contribute fragments, the app can `$remove` them, and tile ids are stable
(UUIDv5).

```jsonc
{ "boards": [ { "id": "home",
    "tiles": [ { "id": "6b0c…", "kind": "query", "target": { "query": "Recent_Cars" },
                 "title": { "en": "Recent cars" }, "defaultSize": { "colSpan": 3, "rowSpan": 3 } } ],
    "layouts": { "6": [ { "id": "6b0c…", "position": { "colStart": 1, "rowStart": 1, "colSpan": 3, "rowSpan": 3 } } ] } } ] }
```

**A user's layout** is a document with a **natural id** `BoardLayouts/{userId}/{boardId}` (decided, round 1). It
follows `NotificationPreference`. The id is derived from the user and the board, never assigned by hand:

- A load is a point lookup, with no query and no index.
- A second save cannot create a duplicate.
- Reset is a delete by id.
- Both id parts are validated before use. The board must be a declared board id, and the user id comes from
  `ISparkCurrentUser`, never from the request.

The document:

```jsonc
{ "owner": "…", "board": "home", "defaultsSeen": ["6b0c…", …],
  "tiles": [ { "id": "u-…", "kind": "count", "target": {…}, "title": "…" } ],  // tiles the user added
  "removed": ["9a1f…"],                                               // defaults the user removed (tombstones)
  "layouts": { "6": [ { "id": "6b0c…", "position": {…} }, … ], "1": [ … ] } }  // only the counts the user edited at (§4.7)
```

**What the user sees** is computed on the server from the default and the stored layout (S3):

- Default tiles the user has not seen yet (`defaultsSeen`) are added in the first free spot.
- Defaults the app has removed disappear.
- Defaults the user removed stay removed.
- A default tile's kind, target and title always come from the app. Only its position is the user's.

### 4.3 Security

Every tile, default or user-added, is filtered **on the server, on every read**, through `IPermissionService`.

- A tile whose target the user cannot access is **left out entirely**. Its title and target are not sent, so the
  layout cannot become an existence oracle (memory: private-repo existence).
- On save, each user-added tile must name a target the user can access **now**. Kinds are validated against the
  registered kinds. Ids, query names and object ids go to RavenDB only as parameters and never into RQL (repo rule).
- **Tile content** is fetched by the client through Spark's **existing** query and PO endpoints. Row-level
  security, attribute redaction and the uniform 401/404 rules therefore apply unchanged. The board endpoint
  returns layout metadata only.
- **Writes** need an authenticated user. They go through Spark's antiforgery (PR #451). Optimistic concurrency on
  the document's change vector covers two tabs saving at once (S6).
- **Who may customize:** any signed-in user by default. A `Customize/<alias>` right is available for apps that
  want to restrict it (Q5).

### 4.4 Extensibility

- **Server:** `ITileKind` is registered with `o.AddKind<T>()`. It has a name, a target validator, and an access
  check `(target, permission) => bool`. Built-in kinds use the same interface.
- **Client:** `provideSparkTileRenderers({ kind: Component })` follows `provideSparkScoreRenderers`. The
  component receives the tile (`input.required<SparkTile>()`) and a board context (edit mode, a reload token).
- A host-metrics or OperatorDashboard tile is just `persistentObject` with `{ entityType: "HostMetrics", id:
  "main" }`, gated by `Read/HostMetrics`.

### 4.5 Picker ("Add tile")

`GET /spark/<alias>/{board}/catalog` returns what the user may add:

- the rights-filtered program units, reusing `ResolveTarget`
- the queries the user may run, reusing the `GetAliases` filter
- the kinds each target supports

A PO tile for a **specific object** (Q4) is added from that object's detail page ("Pin to dashboard"), not from
the picker. The picker does not search objects.

### 4.6 Client components (`@mintplayer/ng-spark/<alias>`)

- **`spark-board`** (input `boardId`) loads the board and renders
  `<bs-tile-manager [(layouts)]="layouts">` with `*bsTile="let tile of visible()"`, using the `bsTileHeader` /
  `*bsTileContent` templates (`bs-tile` no longer exists since 22.27.0).
  - In view mode `dragMode` and `resizeMode` are `'off'`.
  - It keeps a working copy while editing. `layoutChange` updates the copy, and Save sends it.
- **The edit toolbar, the picker modal and the tile chrome** use ng-bootstrap **components**, never plain
  Bootstrap classes (memory). Styles go in sibling `.scss` files.
- **`provideSparkBoards()`** registers the built-in renderers. An app replaces its `home` route with
  `{ path: 'home', component: SparkBoardComponent, data: { boardId: 'home' } }`.

### 4.7 Narrow and wide screens

> **Rewritten 2026-10-23 after mintplayer-ng-bootstrap#440 shipped** (PR #441, squash `92b8e662`;
> `@mintplayer/ng-bootstrap` 22.27.0, `@mintplayer/web-components` 3.0.0, `@mintplayer/ui-core` 1.1.0). The
> earlier design (a `designColumnCount` plus Spark-side `overrides`) is **superseded**. The tile manager now does
> all of it, and its PRD is `docs/prd/tile-manager-responsive-440.md` in that repository.

§2.4 shows that letting CSS auto-fit pick the column count breaks in both directions. Since 22.27.0 the tile manager
handles this itself. Spark only stores what it emits.

**The tile manager's model (ng-bootstrap #440, D1–D15):**
- **Positions live in `layouts: Record<columnCount, TileLayoutSnapshot>`.** `tiles` carries identity and options
  only (`MintTile.position` is gone). The tiles have an optional `defaultSize`.
- **Column count:** `effective = min(breakpointColumns(width), fit(width))`. With no `breakpoints`, the map is
  generated from `minColumnWidth`, up to the largest count in `layouts` (else `maxColumns`, 6). Wide screens get
  wider columns, never more of them.
- **A missing count is derived, never stored** (D5):
  - from the nearest *larger* stored layout, by reading order, a plain span cap and `pack()` (S1 chose the cap
    over edge-scaling);
  - else by scaling the edges up from the nearest smaller one;
  - else from `defaultSize`.

  A tile without a position is appended below the last row.
- **Editing is allowed at every count.** `layoutChange` fires on commit only, with
  `{ columnCount, layout, layouts, staleCounts }`. A no-op commit emits nothing.
- **Stale narrower layouts** are surfaced, never fixed automatically (D15). `staleCounts` lists the stored
  narrower counts that a wide edit does not reach. `resetLayout(count)` and `withoutNarrowerLayouts(layouts, count)`
  let the host act on them.
- **Visibility is the host's:** `tiles` *is* the visible set. Layout entries for ids not in `tiles` are passed
  through unchanged (D9).

**What Spark does with it:**
- **Storage:** `layouts` keyed by count, in both the app default (§4.2, typically one entry at the widest count)
  and the user document (only the counts the user edited at). The server merges them **per count**: the user's
  `layouts[n]` replaces the app's `layouts[n]`. The tile-set merge (`defaultsSeen`, `removed`) runs first, so the
  `tiles` sent are the visible set.
- **Denied tiles have no position on the client.** The server strips them from every `layouts[n]` it sends, as it
  strips the tile itself (§4.3). D9's pass-through therefore never carries them. **On save, the server keeps the
  stored entries for ids that are missing from the payload but are stored and currently denied.** It drops entries
  for removed tiles, and it rejects ids that are neither stored nor user-added. A tile that becomes readable again
  returns to its spot.
- **After a save whose `staleCounts` is not empty,** `spark-board` offers "Also apply to smaller screens?". Yes
  sends `withoutNarrowerLayouts(layouts, columnCount)`. The edit toolbar has "Reset this width", which calls
  `resetLayout(columnCount)`.
- **Removing a tile** removes it from every `layouts[n]`. **Reset** deletes the user document (§4.2).

## 5. App adoption (same PR)

| App | Role | Default tiles |
|---|---|---|
| **Fleet** | primary showcase, E2E host | `Recent_Cars` (query), `Stolen_Cars` (count), `Registrations` (query), `GetMailDeliveries` (query) |
| **SeoTester** (prod) | replaces the two hard-coded query cards | `GetSites`, recent `GetAuditRuns`. For admins, `OperatorDashboard` (PO tile, rights-filtered) |
| **MintPlayer** (prod) | replaces the two hard-coded cards | `My_Playlists`, `GetSongs`, `GetArtists`. Anonymous visitors see the public tiles |
| **CodeCoverage** (prod) | last, because its home is the `Home` virtual PO with sign-in redirects | `Home` KPIs (AccountCount, RepoCount), `MyAccounts`, recent `GetBuilds`/`GetCommits`. For admins, `HostMetrics` |
| HR, QnA | optional second demos (Q7) | People and Companies; Questions |
| DemoApp, SparkId, Festival, ReferenceData, MailRelay | not adopted | — |

Every production adoption keeps today's home content as the default layout, so nothing changes for a user who
never edits.

## 6. Versions and cross-repo work

- **NuGet:** a new package at `11.0.0-preview.*`. The major stays 11.
- **npm:** `@mintplayer/ng-spark` gets a 22.x minor. The major stays 22.
- **ng-bootstrap: done.** mintplayer-ng-bootstrap#440 shipped in PR #441 (`92b8e662`, 2026-10-23) and was
  published by run 39961153375. Spark bumps `@mintplayer/ng-bootstrap` `^22.25.0` → `^22.27.0` and
  `@mintplayer/web-components` `^2.20.0` → `^3.0.0`, in the root `package.json` and in ng-spark's peer ranges. It
  also adds `@mintplayer/ui-core` if the tile-manager entry needs it as a peer.
  - **web-components 3.0.0 is a major.** Check the CHANGELOG for anything outside the tile manager that ng-spark
    or the apps use.

## 7. Tests

- **Unit tests:**
  - the merge function (S3 cases)
  - the kind validators
  - the catalog filter
- **RavenDB tests** (SparkTestDriver, JSON seeding):
  - a denied tile is left out entirely
  - a save naming an inaccessible target is refused
  - another user's layout cannot be read or written
  - concurrent saves
  - anonymous users get the default and cannot write
- **Client (vitest):**
  - `spark-board` view and edit modes
  - Cancel restores the layout
  - the renderer registry
- **E2E (Fleet):**
  - drag and resize, then save and reload
  - add a tile from the picker
  - reset to default
  - a user without `Query/Car` never sees the `Stolen_Cars` tile

## 8. Spikes

| # | Question | Done when |
|---|---|---|
| **S1** | Does `bs-tile-manager` 22.25 do what §4.6 needs? View/edit toggle by per-tile `disableMove`/`disableResize`; `layoutChange` on drag, resize and keyboard; interactive content inside a tile (a grid's paging and search) does not start a drag in `header` mode. The narrow-screen gap is **already measured** (§2.4). What is left: prototype §4.7's projection in `mp-tile-manager`, and compare `min` with proportional span scaling at 390, 768, 1280 and 2560 px | the prototype passes §2.4's page at all four widths: no tile under its minimum width, no horizontal scroll, and stored positions unchanged after a narrow render. Screenshots before and after |
| **S2** | Embedding. Can `spark-query-card`/`spark-query-grid` fill a fixed-height tile (internal scroll or a smaller page size)? How much refactoring would `spark-po-detail` need to take `type`/`id` inputs instead of `ActivatedRoute`, and should the PO tile use a lighter read-only renderer instead? | a size estimate and a choice. The query tile renders in S1's page |
| **S3** | Merge semantics between the default and the user's layout (§4.2): a new default, a removed default, a tombstoned default, an app that resizes a default, a board whose `columns` changes, a user tile whose target lost its rights | a pure function with a test table that the owner signs off |
| **S4** | The `count` kind. Is there an endpoint that returns a query's total cheaply and with RLS (execute with page size 1 → `totalRecords`)? Does a `live` virtual PO (Dashboards) update inside a tile through the existing live channel? | a measured call against Fleet `Stolen_Cars`, and HostMetrics updating in a tile |
| **S5** | Authorization. Filter a seeded layout as admin, as a user and anonymously. A pinned PO tile whose object the user may not read must be omitted, never shown as an error tile. Check that the catalog lists exactly what the menu would | a red→green RavenDB test for each case |
| **S6** | Storage shape. Dedicated endpoints over the `BoardLayouts/{user}/{board}` document (recommended) versus a Spark PO with an owner row filter (the `NotificationPreference` way, but the nested tile array does not fit the PO editor). Optimistic concurrency through the change vector | a decision recorded with its reason. The endpoint skeleton compiles |

## 9. Out of scope (not being done)

- **Sharing layouts** between users and **role-level default layouts** beyond rights filtering.
  - An app that wants an admin layout adds admin-only tiles to the default. Rights hide them from everyone else.
  - If the owner wants per-group defaults, that is Q3 and becomes in scope.
- Tile-to-tile interaction, such as one tile filtering another.
- Charts as a tile kind. `spark-trend` exists but has no query-to-series binding. An app can register its own kind
  (§4.4).

## 10. Milestones (one PR)

| M | Content |
|---|---|
| M0 | Spikes S1–S6. Decisions go into §12 |
| M1 | ✅ **Done in [mintplayer-ng-bootstrap#440](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/440)** (PR #441, `92b8e662`): a headless core with four fronts and `layouts` keyed by count (§4.7). What remains on the Spark side is the version bump in §6 |
| M2 | NuGet package skeleton: csproj, alias, App_Data (`security.json` with a `Customize` right and slots, `translations.json`), `Add<Name>()`, `boards.json` loading and fragment merge |
| M3 | Layout document, merge function (S3), `GET`/`PUT`/`DELETE` (reset) board endpoints, concurrency, antiforgery |
| M4 | Kinds: `ITileKind`, the built-in kinds, validation, permission filtering (S5), catalog endpoint |
| M5 | ng-spark entry point: `spark-board`, view and edit modes, the toolbar, the save/cancel/reset flow |
| M6 | Built-in renderers: `query`, `count`, `persistentObject`, `programUnit`. The picker modal. "Pin to dashboard" on the PO detail page |
| M7 | Fleet adoption (showcase) + E2E |
| M8 | SeoTester and MintPlayer adoption (production) |
| M9 | CodeCoverage adoption (production). HR/QnA if Q7 says so |
| M11 | ✅ **Compact object tile (Q9 = B, D21).** Evidence: po-detail + boards specs 76/76 and 22/22 (`compact draws the Summary attributes…`, `compact falls back…`), `ModelSynchronizerTests` 71/71 (`A_hand_set_Summary_survives_re_synchronize`). Browser check pending a host restart. (1) `EShowedOn.Summary = 4` in `libs/model/MintPlayer.Spark.Model/EShowedOn.cs`, plus the TS `ShowedOn` in `models/src/showed-on.ts`. In TS an **absent** `showedOn` must **not** count as `Summary`. (2) Synchronize keeps `Summary` when it narrows (`ModelSynchronizer.cs:1048` intersects with the derived `Query \| PersistentObject`, which would strip it). (3) `spark-po-detail` gains `compact = input(false)`: one `<dl bsRow>` over the existing `detailAttrField` template, no `bs-tab-control`, no `bs-card`, ordered by the attribute's `order` alone (groups and tabs play no part; `visibleAttributes` already sorts so, `spark-po-detail.component.ts:488`). It shows the `Summary` attributes, or every detail attribute when the type marks none. A registered `SPARK_DETAIL_LAYOUTS` layout still wins over `compact`, because SeoTester's `operator-dashboard` tile is a Dashboards virtual object drawn by its layout. (4) `spark-po-tile` passes `[compact]="true"`. (5) Specs: compact draws no tab-control or card, filters to `Summary`, and falls back. A synchronizer test that `Summary` survives a re-sync. (6) Mark a few attributes in the apps' model JSON (MintPlayer songs/artists, Fleet cars), the guide's tile section, and the release notes |
| M10 | Guide `docs/guide-boards.md`, README, CHANGELOG/release notes. Then the full test sweep, once, at the end |

## 11. Open decisions (owner)

- **Q1 — Name.** `Dashboards` is taken. Options:
  - **`MintPlayer.Spark.Boards`** with alias `boards`, `@mintplayer/ng-spark/boards` and `spark-board`
    (recommended: short, and distinct from Dashboards)
  - `MintPlayer.Spark.Tiles`, which matches ng-bootstrap's wording but names the part, not the whole
  - `MintPlayer.Spark.Workspace`
- ~~**Q2 — Boards per app.**~~ Decided in round 1: several named boards per app (§12).
- **Q3 — Default layouts per group.** Is one default per board, filtered by rights, enough? Or should a default
  vary by group, for example an admin default?
- **Q4 — Tiles for a specific object.** Allow "Pin this PO to my dashboard"? Recommended: yes, through the detail
  page, gated by `Read` at save time and on every read.
- **Q5 — Who may customize.** Every signed-in user, or only holders of a `Customize/<alias>` right that the app
  binds? Recommended: every signed-in user by default, and the right exists so apps can narrow it.
- **Q6 — Saving.** An explicit Save/Cancel in edit mode (recommended), or autosave after each gesture?
- **Q7 — HR and QnA.** Adopt them as second demos, or leave them alone?
- ~~**Q8 — ng-bootstrap branch.**~~ Resolved: the fixes were made on a branch from master and merged as PR #441.
- ~~**Q9**~~ Decided as B (D21). **Q9 — Which attributes an object tile shows, and how (2026-10-23).** The tile should be one flat label/value
  grid: no tabs and no cards. Owner constraint: "reuse the existing components as much as possible. Adding inputs
  is acceptable". **The renderer is the same under every option:** `spark-po-detail` gains a `compact` input that
  draws its existing `detailAttrField` template into one `<dl bsRow>` (M11). Only the way the attributes are
  chosen differs.

  What exists today (evidence):
  - `AttributeGroup` has `Id`, `Name`, `Label`, `Tab` and `Order` (`Abstractions/EntityTypeDefinition.cs:452`).
    `AttributeTab` has `Id`, `Name`, `Label`, `Order` and `ColumnCount` (:432).
  - An attribute reaches a tab only through its group (`Group`, :404), and carries `Order` (:284) and `ShowedOn`
    (:397).
  - `EShowedOn` is a flags enum: `None = 0`, `Query = 1`, `PersistentObject = 2`
    (`libs/model/MintPlayer.Spark.Model/EShowedOn.cs`).
  - Nothing like `isDefault`, summary, essential, compact or preview exists on a tab, group, attribute or type. The
    nearest is the type's `Breadcrumb` template.
  - A new field survives synchronize only as a real C# property: JSON without one is dropped
    (`libs/spark/MintPlayer.Spark/AGENTS.md:97-104`). Tabs and groups are copied whole (`ModelSynchronizer.cs:798`).
    `ShowedOn` is intersected with the derived sides (:1048), so a new flag needs that line changed.

  Prior art (web): every product keeps "what the summary shows" apart from the full form's layout, ordered and
  small.
  - Salesforce *compact layouts* are an ordered field list per object. The first four fields fill the highlights
    panel and the hover cards, and a system default applies when none is defined.
  - Dynamics 365 has a separate *Quick View* / *Card* form per purpose.
  - SAP Fiori puts a flagged field group in the object page header, with at most 5 fields, ordered by position.
  - An Odoo kanban card lists its own fields.
  - None of them flattens the whole form (option D).

  | Option | How a tile picks attributes | For | Against |
  |---|---|---|---|
  | A. `isDefault` on `AttributeGroup` (owner's idea) | the attributes of the flagged group | one bool on a type that copies through sync whole; the group's order is reused; SAP Fiori's header field group is the same shape | The essentials usually sit in **several** groups: HR's `Person` has its name under *Personal* and its email under *Contact* (`apps/HR/HR/App_Data/Model/Person.json`). A flag then forces the detail page's grouping to serve the tile, the coupling every product above avoids. "Default" also reads as "the group/tab opened first" |
  | **B. `Summary` value in `EShowedOn` (recommended)** | the attributes with `"showedOn": "Query, PersistentObject, Summary"`, ordered by the attribute's `order` | chosen per attribute across groups; reuses the existing visibility mechanism and `hasShowedOnFlag`; the detail layout is untouched; matches Salesforce's per-object list; usable later for hover cards or a reference's preview | needs the sync change (:1048) and care that an absent `showedOn` does not mean "Summary" in TS; it changes the shared enum |
  | C. a per-tile attribute list in the board config | `target.attributes: [...]` | per-user choice, like Odoo | picker UI, validation against the model and permission filtering per attribute; every user and every default board repeats it. Can come later, on top of B |
  | D. no metadata: flatten every detail attribute | all of them | zero model change | large objects give a long, scrolling tile; no product does it. Kept only as **B's fallback** when a type marks nothing, so an unconfigured app still gets a usable tile |

  **Decided (owner, 2026-10-23): B, with D as the fallback (D21).**

## 12. Decision log

### Round 1 (owner, 2026-10-23)

| # | Decision | Evidence |
|---|---|---|
| D1 | The app ships a default layout. A signed-in user may modify it, and the result is stored **per user in the database** | owner preference |
| D2 | The per-user document gets a **natural id**, `BoardLayouts/{userId}/{boardId}` (§4.2) | owner preference |
| D3 | An app can have **several dashboards** (named boards). This answers Q2 | owner: "some website-developers may want multiple dashboards in a single app" |
| D4 | Narrow screens get a design of our own (§4.7): an explicit column count, a projection that is never saved, and an override per column count. *Superseded in part by §4.7: the tile manager now derives and stores per count itself* | §2.4: measured in Chromium, both tile managers collapse tiles to 0–10 px at 700 and 390 px. Infragistics' clamp is destructive (`position.js:45-60`) |

### Round 2 (2026-10-23, the recommended defaults, taken when the owner said "continue with all remaining work")

The owner did not answer Q1 and Q3–Q7 individually. Each row takes the recommendation §11 made. Any of them can still
be reversed.

| # | Decision | Evidence |
|---|---|---|
| D5 | **Q1:** `MintPlayer.Spark.Boards`, alias `boards`, `@mintplayer/ng-spark/boards`, `spark-board` | `Dashboards` is taken by #504 (§1) |
| D6 | **Q3:** one default per board, filtered by rights. An admin-only tile is a default tile that only admins can read | §9; the filter in §4.3 already hides it |
| D7 | **Q4:** "Pin to dashboard" on the detail page, gated by `Read` on save and on every read | §4.5 |
| D8 | **Q5:** every signed-in user may customize. `SparkBoardsOptions.RequireCustomizeRight` (default `false`) makes saving need `Customize/Board` | §4.3 |
| D9 | **Q6:** explicit Save and Cancel | §3 |
| D10 | **Q7:** HR and QnA are **not** adopted. Fleet is the showcase and three production apps adopt it | §5 |
| D11 | **`boards.json` is application-only**: `App_Data/boards.json` plus boards declared in code (`SparkBoardsOptions.Boards`, which the tests use). Library fragments and `$remove` are not supported. This supersedes the fragment wording in §4.2 | A new App_Data layer kind touches Spark's generators and the model-hash gate, and no library ships a board. Fragments can be added later without breaking the file |
| D12 | **No `security.json` slot.** The library ships `Customize/Board` only as a right name. Tiles are gated by the rights of their targets | D8. A slot binds groups to a library's own types, and boards have no types of their own |
| D13 | **S6:** dedicated endpoints over the `BoardLayouts/{userId}/{boardId}` document, not a PO. `PUT` takes the change vector it read. A mismatch answers 409, and a save with no change vector over an existing document is a 409 too | The nested `layouts` map does not fit the PO editor. RavenDB's `Store(entity, changeVector, id)` gives the concurrency check |
| D14 | **S4:** the `count` kind executes the query with page size 1 and shows `totalItems`. It runs through `SparkService.executeQuery`, so RLS applies unchanged | §4.3, "tile content is fetched through the existing endpoints" |
| D15 | ~~**S2:** the `persistentObject` tile is a light read-only renderer over `SparkService.get(type, id)`. It shows the attributes marked visible, and does not refactor `spark-po-detail`~~ **Superseded 2026-10-23 (owner):** the tile is `spark-po-detail` itself, with new `objectType`/`objectId` inputs in place of the route, `[showQueries]="false"` and `[showChrome]="false"` (no action bar, title or add-on panels) | ~~`spark-po-detail` reads `ActivatedRoute`. A light renderer needs no refactor~~ The light renderer had no padding (its text touched the tile's edges) and drew values as plain text, unlike the detail page. The inputs cost one constructor check: the route's first emission comes before the inputs are set, so a route without `:type` loads nothing. A load overtaken by a move to another object now publishes nothing. Evidence: spec `embedded (objectType/objectId, #530)`, 97/97 po-detail + boards specs green |
| D16 | **The id needs no escaping.** It is `BoardLayouts/{userId}/{boardId}` with the user id as `ISparkCurrentUser` gives it, which may itself contain `/`. The board id is the last segment and is checked against `^[a-z0-9][a-z0-9-]{0,63}$`, so it never contains `/` and the id cannot be ambiguous | §4.2 says both parts are validated |

### Round 3 (2026-10-23, made during M7–M10)

| # | Decision | Evidence |
|---|---|---|
| D17 | **A query target is the query's id or alias, never its name.** A name refuses startup. The apps use derived aliases (`recent-cars`, `songs`, `my-accounts`) or a GUID | `QueryTileKind.ValidateQuery` resolves through `IQueryLoader.ResolveQuery(idOrAlias)` (`Kinds/BuiltInTileKinds.cs:39`), which matches an id or an alias only. The README's first example (`"GetCars"`) would have refused startup and was fixed |
| D18 | **MintPlayer's default board is `songs` and `artists` only.** `My_Playlists` is left out, unlike §5's plan, so the home page is unchanged for a user who never customizes. A user can add playlists from the picker | §5: "Every production adoption keeps today's home content as the default layout". Today's home showed only the two cards |
| D19 | **CodeCoverage keeps `Home` as the page.** Only the `my-accounts` sub-query moves, into a one-tile `home` board that `HomeExtrasComponent` renders for a signed-in user. `HOME_URL`, the sign-in redirects and the KPI attributes are unchanged. §5's KPI, build/commit and HostMetrics tiles are not added | The redirects and `HOME_URL` point at the Home PO. `ResyncAction`'s `RefreshQuery("my-accounts")` (asserted in `BoardAndResyncActionTests.cs:284`) still reaches the tile, because `spark-query-grid` matches a refresh by id, name or alias (`grid/src/spark-query-grid.component.ts:753`). `--spark-verify-model` is in sync after `--spark-synchronize-model` (hash `b66581cc…`) |
| D20 | **Known limitation: "Also apply this layout to smaller screens?" is asked at Save time**, about the last width edited. A narrower count that only the app default stores is not part of the user's layout, so it comes back from the default | `spark-board.component.ts:200` (`askNarrower` when the last edit left stale stored counts), `board-draft.ts` `applyToNarrower` |

### Round 4 (owner, 2026-10-23)

| # | Decision | Evidence |
|---|---|---|
| D21 | **Q9 = B. An object tile shows the attributes flagged `EShowedOn.Summary`**, as one flat grid ordered by the attribute's `order` alone, with no tabs, cards or groups. When a type flags none, it shows every detail attribute (D). Rendering reuses `spark-po-detail` with a new `compact` input, not a new presenter component | Owner: "B looks great", "the essential attributes being spread across several groups is correct", "still also the attribute's offset value determines how the attributes are ordered" (the `order` field), "reuse the existing components as much as possible. Adding inputs is acceptable". **No attribute presenter component exists.** All value rendering (renderers, AsDetail tables, primitive lists, booleans, colors, images, urls, reference chips and links, multiline, dates) is the inline `#detailAttrField` template in `po-detail/src/spark-po-detail.component.html:115-312`. That template reads po-detail state the component loads itself: `lookupReferenceOptions`, `asDetailTypes`, `rendererRegistry`, `allEntityTypes` (`spark-po-detail.component.ts:585`, `:603`). A separate presenter would need its host to repeat those loads, while `compact` reuses them as they are. Query grids have their own `spark-grid-cell` |
