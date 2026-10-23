# Boards: dashboards each user arranges

A board is a page of tiles: query grids, counts, pinned objects, links to program units. The application ships a
default layout. A signed-in user may drag, resize, add, rename and remove tiles, and the result is stored for that
user alone (`MintPlayer.Spark.Boards`, alias `boards`; npm `@mintplayer/ng-spark/boards`). The grid is
`@mintplayer/ng-bootstrap`'s tile manager.

Do not confuse boards with [Dashboards](guide-dashboards.md). A dashboard is one live virtual persistent object that
providers fill with figures. A board arranges existing Spark content, and a dashboard can be one of its tiles
(`persistentObject`).

Decisions and evidence: [dashboard_layout_PRD.md](dashboard_layout_PRD.md) §12. Package reference:
[README](../libs/boards/MintPlayer.Spark.Boards/README.md).

## Server

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddBoards();
    // spark.AddBoards(o => o.RequireCustomizeRight = true);   // saving needs Customize/Board
    // spark.AddBoards(o => o.AddKind<WeatherTileKind>());     // an application tile kind
});
```

The default boards live in `App_Data/boards.json`. You can also declare them in code through `o.Boards`, but an id
declared in both places refuses startup.

```json
{
  "boards": [
    {
      "id": "home",
      "tiles": [
        { "id": "recent-cars", "kind": "query", "target": { "query": "recent-cars" }, "defaultSize": { "colSpan": 2, "rowSpan": 2 } },
        { "id": "stolen-count", "kind": "count", "target": { "query": "stolen-cars" }, "title": { "en": "Stolen" } },
        { "id": "dashboard", "kind": "persistentObject", "target": { "entityType": "OperatorDashboard", "id": "main" } }
      ],
      "layouts": {
        "4": [
          { "id": "recent-cars", "position": { "colStart": 1, "rowStart": 1, "colSpan": 2, "rowSpan": 2 } },
          { "id": "stolen-count", "position": { "colStart": 3, "rowStart": 1, "colSpan": 1, "rowSpan": 1 } }
        ]
      }
    }
  ]
}
```

- **Board ids** use lowercase letters, digits and dashes. By convention the start page's board is `home`. An app may
  have several boards.
- **Tile ids** must stay stable across releases, because a user's layout refers to a default tile by its id. Renaming
  an id is the same as removing the tile and adding a new one.
- **Query targets** are the query's **id or alias**, never its name. Spark derives an alias from the name (`GetCars`
  becomes `cars`), or the query declares one. A name fails at startup.
- **`layouts`** holds positions per column count, the way the tile manager does. A count you leave out is derived from
  the nearest one you give.
- A malformed board, an unknown kind or a badly shaped target **refuses startup**.

### Built-in kinds

| Kind | Target | Shows |
|---|---|---|
| `query` | `query` (id or alias) | the query's grid, with paging and search |
| `count` | `query` | the query's total, counted with row security applied |
| `persistentObject` | `entityType`, `id` | the object's essential fields, or a virtual object's own layout such as a dashboard |
| `programUnit` | `programUnit` (id) | a link to the program unit |

A `persistentObject` tile draws one flat grid of label/value pairs, without tabs or group cards. It shows the
attributes whose `showedOn` includes `Summary`, ordered by their `order`. When the type marks none, it shows every
attribute of the detail page. Mark the few fields that identify the object:

```json
{ "name": "Title", "order": 1, "showedOn": "Query, PersistentObject, Summary" }
```

`Summary` is never part of the default, and synchronize keeps it as long as the attribute stays on the detail page.
A type drawn by a registered detail layout (`SPARK_DETAIL_LAYOUTS`, such as a Dashboards virtual object) keeps that
layout in the tile. Outside a board, `<spark-po-detail objectType objectId [compact]="true" [showChrome]="false"
[showQueries]="false" />` draws the same view.

An application kind implements `ITileKind`. `Validate` checks the target's shape, and `CheckAccessAsync` decides
whether the caller may see the tile. The kind is resolved per request, so it can take scoped services. Its client
renderer is registered with `provideSparkTileRenderers({ weather: WeatherTileComponent })`. The component declares
`tile = input.required<SparkBoardTile>()` and `context = input.required<SparkTileContext>()`, fetches its own
content, and fetches again when `context().reloadToken` changes.

### Rights

Every tile is checked against the caller's rights on **every read and every save**:

- A tile the caller may not open is **left out entirely**, never shown as an error tile, so its existence does not
  leak. A tile for admins only is simply a default tile that only admins can read.
- A save that names a target the caller cannot open is refused with **400**.
- The "Add tile" picker lists exactly what the caller may open.

Anonymous visitors get the default board, filtered by their rights, and cannot save: a `PUT` or `DELETE` answers
**401**. By default every signed-in user may customize. With `RequireCustomizeRight` set, saving also needs
`Customize/Board` (grant it in `security.json`); without it, a save answers **403**.

### Storage and endpoints

| Endpoint | Does |
|---|---|
| `GET /spark/boards/{board}` | the caller's view: the default merged with their layout, filtered by rights |
| `PUT /spark/boards/{board}` | saves the whole state, with the change vector from the last `GET` |
| `DELETE /spark/boards/{board}` | resets the board to the default |
| `GET /spark/boards/{board}/catalog` | what the picker may offer |

A layout is stored as `BoardLayouts/{userId}/{board}`. A save is the full state, and it carries the change vector of
the last read. If the layout changed in between (another tab, another device), the save answers **409** and the client
reloads. Writes need the antiforgery header, like every other Spark write. `MaxUserTiles` (default 50) limits how
many tiles a user may add to one board.

When the application changes its default, the change reaches each user's layout on their next read:

- A new default tile appears.
- A default tile the user removed stays removed.
- A default tile the application dropped disappears.

## Client

```ts
// app.config.ts
providers: [
  provideSparkBoards(),            // the four renderers and "Pin to dashboard" on detail pages
  provideSparkClientOperations(),  // so RefreshQuery reaches the query tiles
]

// app.routes.ts: a page that is only a board
sparkBoardRoute('home', 'home'),
```

To place a board inside a page of your own, use `<spark-board boardId="home" />`. Without the `boardId` input, the
component reads `data.boardId` from the route.

- **Customize** switches the board into edit mode. **Save** stores the changes, **Cancel** throws them away, and
  **Reset** (after a confirmation) deletes the user's layout.
- **Pin to dashboard** on a detail page adds that object to a board as a `persistentObject` tile. The board defaults
  to `home`; change it with `provideSparkBoards({ pinBoard: 'start' })`, or leave the action out with
  `{ pinAction: false }`.
- A custom action's `RefreshQuery("<alias>")` reaches a query tile. The tile's grid matches the query's id, name or
  alias.

### Narrow screens

The tile manager keeps a layout per column count, and a narrower screen gets fewer columns. A count the user never
edited is derived from the nearest stored one; it is never saved, so the wide layout is not changed by looking at it
on a phone. Each width also has **Reset this width** in edit mode.

When a user edits one width and narrower widths already have stored layouts, Save asks: **Also apply this layout to
smaller screens?** Answering yes replaces those narrower layouts with the edited arrangement, and answering no keeps
them as they are.

Known limitation: the question is asked at Save time, about the last width the user edited. A narrower count that only
the application's default stores is not part of the user's layout, so it comes back from the default.

## Who uses it

| App | Board |
|---|---|
| Fleet | the `home` page is the board: recent cars, the stolen count and grid, registrations, mail deliveries. Fleet is the E2E host |
| SeoTester | `home`: sites, audit runs, and the operator dashboard for admins |
| MintPlayer | `home`, below the hero: songs and artists. Playlists can be added from the picker |
| CodeCoverage | the `Home` page stays a virtual object. Its accounts grid is the `my-accounts` tile of the `home` board |

HR and QnA do not use boards (PRD D10).
