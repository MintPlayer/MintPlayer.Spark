# A page of your own for one type, with Spark's attributes (#601)

**Packages:** npm `@mintplayer/ng-spark` → a minor. The versioning engine computes the number from the tags; the major stays
22 (Angular 22). It ships once the merged pull request is backported to `release/angular-22.x`. No NuGet package changes.

Until now, giving one type a page of its own (a header, a layout) meant rebuilding everything Spark's detail page does:
loading the object, its canonical URL and SEO tags, the server render, and drawing its attributes. This release splits
those out of `spark-po-detail`, which now uses them itself, so a custom page composes them. mintplayer.com's song page is
the first user: a large thumbnail with a play button above the song's attributes.

Decisions and evidence: [mintplayer_song_header_PRD.md](mintplayer_song_header_PRD.md) (D19, D20).

---

## New

1. **`<spark-po-presenter>`** (`@mintplayer/ng-spark/po-detail`) draws a loaded object's tabs, groups and attribute rows,
   renderers included, exactly as the detail page does. Inputs: `item` (required), `entityType` (required),
   `allEntityTypes`, `compact`. It renders attributes only: no action bar, no sub-queries.
2. **`sparkObjectPage(options?)`** (`@mintplayer/ng-spark/po-detail`), called in a component's field initializer, runs a
   detail page's lifecycle: it loads the object named by the route (`:type`/`:id`, or `data.sparkType`), drops loads a
   newer navigation overtook, holds the server render until the object is in, then applies the object's canonical URL and
   SEO tags, marks a refused page `noindex`, and resets the tags on destroy. It exposes `item()`, `entityType()`,
   `allEntityTypes()`, `errorMessage()`, `type` and `id`.
   - `beforePublish(entityType, item)` runs before the object is shown and may return a callback that runs together with
     the publish, so per-object state (rights, buttons) never shows the previous object's values on a reused page.
   - `embedded()` turns off the URL and SEO handling, for a page shown inside another.
3. **`detailBuiltInActions(actions, permissions, item, deletedView?)`** (`@mintplayer/ng-spark/models`) returns the detail
   page's Edit and Delete actions (or `null` for each) by the detail page's own rules: the object's `can` over the type's
   permissions, `disabledActions`, and the actions' `showedOn`. A custom page shows the same buttons as Spark's.

## Fixed

4. **`sparkObjectRoute()` no longer swallows the edit page.** Its slug route (`<path>/:slug`) also matched
   `<path>/edit`, so a custom route on Spark's own `po/<alias>/:id` path opened the custom page instead of the editor. The
   slug route now refuses a last segment `edit`, which `sparkRoutes()` then serves.

## Breaking changes

None. `spark-po-detail` keeps its inputs and behaviour; its internals moved into the pieces above.
