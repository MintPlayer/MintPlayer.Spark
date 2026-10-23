# ng-spark 22.44.0 / MintPlayer.Spark.Boards 11.0.0-preview.1 — boards users arrange

**Packages:**
- NuGet: a new package, `MintPlayer.Spark.Boards`, at `11.0.0-preview.1`.
- npm: `@mintplayer/ng-spark` → `22.44.0`, with the new entry point `@mintplayer/ng-spark/boards`.
- The majors do not move: the packages still target .NET 11 and Angular 22.

A **board** is a page of tiles: query grids, counts, pinned objects, program-unit links. The application ships a
default layout. Each signed-in user drags, resizes, adds, renames and removes tiles, and the result is stored for that
user (`BoardLayouts/{userId}/{board}`). Every tile is checked against the caller's rights on every read and save, so
a board never shows what the caller cannot open.

Guide: [guide-boards.md](guide-boards.md). Decisions and evidence: [dashboard_layout_PRD.md](dashboard_layout_PRD.md)
(#530).

---

## Dependencies

- `@mintplayer/ng-bootstrap` `^22.25.0` → `^22.27.0`. The tile manager now keeps a layout per column count
  (mintplayer-ng-bootstrap#440).
- `@mintplayer/web-components` `^2.20.0` → `^3.0.0`. The new major comes from the tile manager's headless core.
- `@mintplayer/ui-core` `^1.1.0` is new.

## Adopting

```csharp
spark.AddBoards();   // App_Data/boards.json
```

```ts
provideSparkBoards(),                 // app.config.ts
sparkBoardRoute('home', 'home'),      // app.routes.ts, or <spark-board boardId="home" /> in a page
```

- A tile's query target is the query's **id or alias**, never its name. A name refuses startup.
- `provideSparkBoards()` also adds **Pin to dashboard** to every detail page. `{ pinAction: false }` leaves it out.

## Endpoints

| Endpoint | Does |
|---|---|
| `GET /spark/boards/{board}` | the caller's view of the board |
| `PUT /spark/boards/{board}` | saves the whole state. 401 if anonymous, 403 without `Customize/Board` when required, 400 for a target the caller cannot open, 409 for a stale change vector |
| `DELETE /spark/boards/{board}` | resets the board to the default |
| `GET /spark/boards/{board}/catalog` | what the "Add tile" picker may offer |

## In the repository's apps

- **Fleet:** the home page is the `home` board, and it is the E2E host (`BoardsTests`).
- **SeoTester:** the home cards are the board.
- **MintPlayer:** the cards below the hero are the board.
- **CodeCoverage:** Home's accounts grid is the board's `my-accounts` tile. The `Home` page itself is unchanged.
