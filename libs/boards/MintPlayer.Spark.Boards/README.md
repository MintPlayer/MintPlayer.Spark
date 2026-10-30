# MintPlayer.Spark.Boards

Dashboards of tiles for MintPlayer.Spark. The application ships default boards; each signed-in user
drags, resizes, adds, renames and removes tiles, and the result is stored per user. Every tile is checked
against the caller's rights on every read and save, so a board never shows (or leaks) what the caller
cannot open.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddBoards();                                   // App_Data/boards.json, Spark:Boards
    // spark.AddBoards(o => o.RequireCustomizeRight = true);   // saving needs Customize/Board
});
```

```json
// App_Data/boards.json
{
  "boards": [
    {
      "id": "home",
      "paths": ["/home"],   // the pages a server render supplies it on (optional)
      "tiles": [
        { "id": "cars", "kind": "query", "target": { "query": "cars" }, "defaultSize": { "colSpan": 2, "rowSpan": 2 } },
        { "id": "car-count", "kind": "count", "target": { "query": "cars" } }
      ],
      "layouts": {
        "4": [ { "id": "cars", "position": { "colStart": 1, "rowStart": 1, "colSpan": 2, "rowSpan": 2 } } ]
      }
    }
  ]
}
```

A query target is the query's id or alias (`cars` is the alias Spark derives from `GetCars`), never its name.

Built-in kinds: `query`, `count`, `persistentObject` (`entityType` + `id`) and `programUnit`. An
application adds its own with `o.AddKind<TKind>()` and a matching client renderer
(`provideSparkTileRenderers`). Client: `@mintplayer/ng-spark/boards` (`provideSparkBoards()`, then `<spark-board boardId="home" />` or `sparkBoardRoute('home', 'home')`).

Endpoints: `GET/PUT/DELETE /spark/boards/{board}` and `GET /spark/boards/{board}/catalog`. A layout is
stored as `BoardLayouts/{userId}/{board}`. See `docs/guide-boards.md`.
