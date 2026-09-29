# @mintplayer/ng-spark

The Angular front end for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark): routed
persistent-object pages (list, detail, create, edit) driven by the server's model, the query grid,
attribute renderers, client operations, the program-units shell, and the UI halves of the Spark add-on
packages (soft delete, history, moderation).

Sign-in, registration and the account pages are in the companion package
[`@mintplayer/ng-spark-auth`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/node_packages/ng-spark-auth/README.md).

## Install

```bash
npm install @mintplayer/ng-spark @mintplayer/ng-bootstrap @mintplayer/pagination
```

The major version follows Angular's: `22.x` targets Angular 22. Peer dependencies: `@angular/core`,
`common`, `router`, `forms`, `cdk` (^22), `@mintplayer/ng-bootstrap` (^22.19), `@mintplayer/pagination`,
`rxjs` (~7.8).

## Minimal setup

```ts
// app.config.ts
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideSpark } from '@mintplayer/ng-spark';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(...withSparkTimezone()),
    provideSpark(),              // optional: { baseUrl: '/spark' } is the default
  ],
};

// app.routes.ts
import { sparkRoutes } from '@mintplayer/ng-spark/routes';

export const routes: Routes = [
  { path: 'home', loadComponent: () => import('./home.component') },
  ...sparkRoutes(),              // last: po/:type, po/:type/:id, query/:queryId, …
];
```

Routes with a parameterised first segment of your own must come **after** `sparkRoutes()`.

## Entry points

Import from the entry point, not from the package root; each one is a separate chunk.

| Entry point | What it provides |
|---|---|
| `@mintplayer/ng-spark` | `provideSpark(config?)`, `SPARK_CONFIG` (`baseUrl`), `SPARK_AUTH_STATE` (the bridge `ng-spark-auth` fills) |
| `/routes` | `sparkRoutes()` — the routed persistent-object and query pages |
| `/services` | `SparkService`, `SparkStreamingService`, `SparkLanguageService`, `SparkQueryActionsService`, `RetryActionService`; the timezone feature `withSparkTimezone()` |
| `/models` | The wire types (persistent objects, attributes, queries, envelopes) |
| `/shell` | `<spark-shell>` with its slot directives, `<spark-program-units>`, `<spark-language-selector>` ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-program-units.md)) |
| `/query-list` | `<spark-query-list>` — the routed query page |
| `/grid` | `<spark-query-grid>`, `<spark-query-card>`, `<spark-grid-cell>`, `<spark-search-box>` and the caption/icon/actions slot directives; `SPARK_GRID_PAGE_SIZES` |
| `/column-filter` | `<spark-column-filter-panel>` |
| `/po-detail`, `/po-create`, `/po-edit` | The routed detail, create and edit pages |
| `/po-form` | `<spark-po-form>`, `<spark-reference-picker>`, `<spark-lookup-picker>` |
| `/renderers` | `provideSparkAttributeRenderers([...])`, `SPARK_ATTRIBUTE_RENDERERS`, `withDeclaredInputs` ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-custom-attribute-renderers.md)) |
| `/panels` | `provideSparkDetailPanels(...)`, `provideSparkDetailActions(...)`, `provideSparkQueryListActions(...)` — how add-ons put components on the routed pages |
| `/client-operations` | `provideSparkClientOperations()` — notify, navigate and refresh operations from server-side action code; the toast container |
| `/retry-action-modal` | The modal that answers a server's retry action ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-manager-retry-actions.md)) |
| `/attribute-description` | The [i] tooltip for attribute help text ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-attribute-descriptions.md)) |
| `/icon` | `<spark-icon>` |
| `/pipes` | The pipes the pages use (attribute values, references, lookups, translations, router links) |
| `/soft-delete` | `provideSparkSoftDelete()` — the Deleted toggle on query lists, the recycle bin, Restore / Purge |
| `/history` | `provideSparkHistory()` — `<spark-po-history>`: revision list, read-only view, diff, Revert |
| `/moderation` | `provideSparkModeration()`, `sparkModerationRoutes()`, `sparkModerationRenderers` — vote widget, flag button, review queue, reputation badge, moderator panel |

### Timezone (`withSparkTimezone`)

```ts
provideHttpClient(...withSparkTimezone({ cookieName: 'spark-timezone' }))
```

Sends the browser's IANA zone as a header and writes it to a cookie (only when it changed) so a
server-side render can read it. The header is never sent on the server platform. Use the same cookie
name as the server's `Spark:TimeZone:CookieName`; `cookieName: false` writes no cookie. See
[guide-dates-and-sorting.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-dates-and-sorting.md).

### Panels and actions on the routed pages

```ts
import { provideSparkDetailPanels, provideSparkDetailActions } from '@mintplayer/ng-spark/panels';

providers: [
  provideSparkDetailPanels({ id: 'audit', component: AuditPanelComponent, order: 10 }),
  provideSparkDetailActions({ id: 'export', component: ExportButtonComponent }),
]
```

Each component declares `context = input.required<SparkDetailContext>()` (query-list actions:
`SparkQueryListContext`) and does its own permission checks. A second registration with the same `id`
replaces the first.

### Query toolbar, selection and sub-queries

The grid (`/grid`) owns one toolbar model, `SparkQueryGridComponent.toolbarActions()`. It holds the
server's default `New` and `Delete` (`isDefault` entries from `/spark/actions/list`) and the custom
actions, each enabled from the live selection count. The sub-query card's header renders it on the
right, with the overflow in `…`, and the query page's action bar renders it too.

The grid also renders:
- a select-all box for the page on screen;
- an "N selected ⊗" chip;
- a per-row `⋮` menu of the actions whose rule accepts one row. A menu item runs on that row only and
  leaves the checkbox selection alone.

- **Selection** follows `selectionMode` (`'auto' | 'none' | 'single' | 'multiple'`).
  - It is set on the query, or on the parent type's `queries` entry, which `<spark-query-card
    [selectionMode]>` and `<spark-query-grid [selectionModeSetting]>` take.
  - `'auto'` derives it from the custom actions, as before.
- **Delete** posts the selection to `SparkService.deleteMany()` (`/spark/po/delete-many`). One
  request deletes all the rows or none.
- **New** on a sub-query navigates to the create page with `parentId`, `parentType` and `queryId`.
  The create page always asks `SparkService.newObject()` (`/spark/po/new`) for its blank object, so
  the server's `OnNewAsync` sets the defaults. For a sub-query, its base fills the reference to the
  parent.
- Read `EntityType.queries` with `subQueriesOf(type)`. An entry is a bare alias or
  `{ query, selectionMode?, parentReference? }`.
- **Search.** The card's header has a search box on the right, after the actions, as Vidyano's
  sub-query tabs do. It is `<spark-search-box [(term)]>`, the same component the query page uses: a
  300 ms debounce, a clear button, and Escape to clear. The term goes to the grid's `search`, so the
  server searches the sub-query's rows (with `parentId`/`parentType`), and to the column filters'
  value lists as `querySearch`. **A new term clears the selection.** The card's `search` input is the
  starting term; `[searchable]="false"` hides the box, and a card over bound `data` has none.

```html
<spark-search-box [(term)]="term" />
<spark-query-grid queryId="all-cars" [search]="term()" />
```

See [guide-custom-actions.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-custom-actions.md)
for the server side of this.

### Soft delete, history and moderation

```ts
import { provideSparkSoftDelete } from '@mintplayer/ng-spark/soft-delete';
import { provideSparkHistory } from '@mintplayer/ng-spark/history';
import { provideSparkModeration, sparkModerationRenderers, sparkModerationRoutes } from '@mintplayer/ng-spark/moderation';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';

providers: [
  provideSparkSoftDelete(),
  provideSparkHistory(),
  provideSparkModeration(),
  provideSparkAttributeRenderers([...sparkModerationRenderers]),
]

// routes: ...sparkModerationRoutes() before ...sparkRoutes()
```

Each needs its server package: [SoftDelete](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/soft_delete/MintPlayer.Spark.SoftDelete/README.md),
[History](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/history/MintPlayer.Spark.History/README.md),
[Moderation](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/moderation/MintPlayer.Spark.Moderation/README.md)
([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-moderation.md)). `apps/QnA` uses all three.

## More

- [Repository README](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/README.md) — the developer guides
- [HTTP API specification](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/Spark-API-Specification.md)
- [Release notes 11.0.0-preview.91 / ng-spark 22.24.0](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/release-notes-preview-91.md)
