# @mintplayer/ng-spark

The Angular front end for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark): routed
persistent-object pages (list, detail, create, edit) driven by the server's model, the query grid,
attribute renderers, client operations, the program-units shell, and the UI halves of the Spark add-on
packages (soft delete, history, moderation).

Sign-in, registration and the account pages are in the companion package
[`@mintplayer/ng-spark/auth`](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/node_packages/ng-spark/auth/README.md).

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
| `@mintplayer/ng-spark` | `provideSpark(config?)`, `SPARK_CONFIG` (`baseUrl`), `SPARK_AUTH_STATE` (the bridge `ng-spark/auth` fills) |
| `/routes` | `sparkRoutes()` — the routed persistent-object and query pages |
| `/services` | `SparkService`, `SparkLiveService`, `SparkLanguageService`, `SparkQueryActionsService`, `RetryActionService`; the timezone feature `withSparkTimezone()` |
| `/models` | The wire types (persistent objects, attributes, queries, envelopes) |
| `/shell` | `<spark-shell>` with its slot directives, `<spark-program-units>`, `<spark-language-selector>` ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-program-units.md)) |
| `/query-list` | `<spark-query-list>` — the routed query page |
| `/grid` | `<spark-query-grid>`, `<spark-query-card>`, `<spark-grid-cell>`, `<spark-search-box>` and the caption/icon/actions slot directives; `SPARK_GRID_PAGE_SIZES` |
| `/column-filter` | `<spark-column-filter-panel>` |
| `/po-detail`, `/po-create`, `/po-edit` | The routed detail, create and edit pages |
| `/po-form` | `<spark-po-form>`, `<spark-reference-picker>`, `<spark-lookup-picker>` |
| `/renderers` | `provideSparkAttributeRenderers([...])`, `SPARK_ATTRIBUTE_RENDERERS` (with the core `paragraph` renderer built in), `withDeclaredInputs` ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-custom-attribute-renderers.md)) |
| `/score-renderers` | `provideSparkScoreRenderers([...])` — the `spark-gauge`, `spark-threshold-bar`, `spark-trend` and `spark-delta` renderers, their components, bands and grade presets ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-score-renderers.md)) |
| `/panels` | `provideSparkDetailPanels(...)`, `provideSparkDetailActions(...)`, `provideSparkQueryListActions(...)`, `provideSparkDetailTitleBadges(...)` (badges after the detail title; core's "Deleted" is one), `provideSparkSaveSubmittedHandlers(...)` (where the create/edit pages go after a save diverted for approval) — how add-ons put components on the routed pages |
| `/client-operations` | `provideSparkClientOperations()` — notify, navigate and refresh operations from server-side action code; the toast container |
| `/retry-action-modal` | The modal that answers a server's retry action ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-manager-retry-actions.md)) |
| `/attribute-description` | The [i] tooltip for attribute help text ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-attribute-descriptions.md)) |
| `/translated-string` | `<spark-translated-string>` (detail view) and `<spark-translated-string-editor>` (one input per language) for a `TranslatedString` attribute ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-translated-strings.md)) |
| `/icon` | `<spark-icon>` |
| `/pipes` | The pipes the pages use (attribute values, references, lookups, translations, router links) |
| `/soft-delete` | `provideSparkSoftDelete()` — the Deleted toggle on query lists, the recycle bin, Restore / Purge |
| `/history` | `provideSparkHistory()` — `<spark-po-history>`: revision list, read-only view, diff, Revert |
| `/moderation` | `provideSparkModeration()`, `sparkModerationRoutes()`, `sparkModerationRenderers` — vote widget, flag button, review queue, reputation badge, moderator panel |
| `/approvals` | `provideSparkApprovals()`, `sparkApprovalsRenderers`, `<spark-approvals-inbox-link>` — the decision panel on an approval request (proposal diff, Approve / Reject with a reason / Withdraw), the `approval-status` pill, the "Approval pending" title badge on governed rows, the inbox link with its live count — the client of MintPlayer.Spark.Approvals |
| `/organizations` | `withOrganizationRoutes()` for `sparkAuthRoutes(...)`: the page a mailed organization invitation or join request opens (`organizations/accept/:token`), accepting on an explicit click; `<spark-organization-switcher>`: the current organization in the top bar, which lists of organization-owned rows and new rows follow ([guide](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-organizations.md)) |
| `/pwa` | `provideSparkServiceWorker()` — Angular's service worker with Spark's update policy (`SparkServiceWorkerUpdates`) and in-app navigation for a clicked push notification (`SparkNotificationClicks`) |
| `/share` | `withShareRoutes(options?)` — the anonymous page a share link opens (`shared/:type/:token?id=…`); `SparkSharedObject` (the `/spark/share/load` answer). The page itself is `SparkSharedObjectComponent` in `/share/page`, lazy-loaded |
| `/notifications` | `<spark-notification-bell>`, `<spark-push-toggle>`, `SparkNotificationsService` (live unread count, latest notifications, mark read, Web Push opt-in), `provideSparkNotifications()` — the client of MintPlayer.Spark.Notifications |

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
right, with the overflow under the translated "More" label (`common.more`), and the query page's
action bar renders it too.

The grid also renders:
- an "N selected" chip while rows are selected. There is no select-all: with paged, lazy or
  virtual-scrolled rows it could only tick the rows loaded so far. Deselect-all is the datatable's
  own header checkbox, shown only while a row is selected. Single selection has no checkbox
  column, so there the chip carries a ⊗ that clears the selection;
- a per-row `⋮` menu of the actions whose rule accepts one row. A menu item runs on that row only and
  leaves the checkbox selection alone. The menu is a CDK connected overlay with ng-bootstrap's
  `<bs-dropdown-menu>` inside, not `@mintplayer/ng-bootstrap/dropdown`, pending
  [ng-bootstrap #419](https://github.com/MintPlayer/mintplayer-ng-bootstrap/issues/419) (the
  dropdown fesm fails to initialise in unlinked JIT test runs).

- **Selection** follows `selectionMode` (`'auto' | 'none' | 'multiple'`).
  - It is set on the query, or on the parent type's `queries` entry, which `<spark-query-card
    [selectionMode]>` and `<spark-query-grid [selectionModeSetting]>` take.
  - `'auto'` shows checkboxes when an action the user may run needs a selection: Edit, Delete or a
    custom action (#467).
- **Delete** posts the selection to `SparkService.deleteMany()` (`/spark/po/delete-many`), each row
  with the `etag` its query row carried (#467, D14). One request deletes all the rows or none; a row
  changed since the list loaded is a 409 that names it.
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
- **Recycle bin.** While the grid lists `deleted: 'only'`, or sits under a deleted parent, its
  toolbar and row menu are empty (no New, no Delete, no custom action): the recycle bin offers only
  Restore and Purge. `<spark-query-card>` takes `[deleted]` (the rows' mode, forwarded to its grid)
  and `[parentDeleted]` (the parent is a deleted row; the detail page sets it for a row opened with
  `?deleted=only`). Under a deleted parent the grid sends `parentDeleted: 'include'` on execute and
  distinct-values, which the server honours only for holders of `ViewDeleted/{ParentType}`.

```html
<spark-search-box [(term)]="term" />
<spark-query-grid queryId="all-cars" [search]="term()" />
```

See [guide-custom-actions.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-custom-actions.md)
for the server side of this.

### Live updates

A type or query marked `"live": true` in its model is followed over one WebSocket (`/spark/live`) with
nothing to write: `spark-po-detail` replaces the object when it changes and shows the unavailable state when
it is deleted; every `spark-query-grid` (the query page, sub-queries, cards) re-renders its page as rows
change, appear or leave, with server-side paging, sort, search and filters intact (`[live]` overrides the
query's flag); `spark-po-edit` adopts a newer version while the form is untouched and otherwise shows a
banner that runs the three-way merge. For a custom page:

```ts
import { SparkLiveService } from '@mintplayer/ng-spark/services';

const live = inject(SparkLiveService);
readonly run = live.livePersistentObject(signal('AuditRun'), this.runId);  // Signal<PersistentObject | null | 'gone'>
readonly running = live.liveQuery(computed(() => ({ queryId: 'runningaudits', skip: 0, take: 20 })));  // Signal<QueryResult | null>
// or the observables: live.watchPersistentObject(type, id), live.watchQuery(request | request$)
```

This replaces streaming queries (`isStreamingQuery`, `SparkStreamingService`); there is no shim. See
[guide-live-updates.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-live-updates.md).

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

### Approvals

```ts
import { provideSparkApprovals, sparkApprovalsRenderers } from '@mintplayer/ng-spark/approvals';

providers: [
  provideSparkApprovals(),
  provideSparkAttributeRenderers([...sparkApprovalsRenderers]),
]
// shell: <spark-approvals-inbox-link linkClass="btn btn-sm btn-link" />
```

- An `ApprovalRequest`'s detail page gets a decision card: for a document proposal, the attribute diff
  (`<spark-po-diff>` from `/grid`; a creation lists the proposed values, a modification what changes, now →
  proposed) and a warning when the row changed since; then Approve, Reject (with a required reason) and
  Withdraw (the requester), as the server offers them on the row.
- A governed row with a pending proposal the caller made or may decide (`metadata.approval`) shows an
  "Approval pending" badge after its title, linking to the request.
- A creation diverted for approval opens its request rather than the object, which does not exist yet.
- The inbox link shows how many requests await the caller (the `pending-approvals` query), live.
- A renderer may register a `diffComponent` to draw its attribute in the diff; without one each side is
  drawn as a grid cell.

Needs the server package [Approvals](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/approvals/MintPlayer.Spark.Approvals/README.md).

### Contributions (preview)

```ts
import { provideSparkContributions, sparkContributionRenderers } from '@mintplayer/ng-spark/contributions';

providers: [
  provideSparkContributions(),   // "Revert to this version" (row menu + contribution page)
  provideSparkAttributeRenderers([...sparkContributionRenderers]), // attribution row line, lineDiff
]
```

No routes of its own: the History link opens `query/:queryId` with `?parentId=&parentType=` and one
column filter per slot, which the query page reads for any query. Needs
[MintPlayer.Spark.Contributions](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/libs/contributions/MintPlayer.Spark.Contributions/README.md).

### Score renderers

```ts
import { provideSparkScoreRenderers } from '@mintplayer/ng-spark/score-renderers';

providers: [
  provideSparkScoreRenderers([/* the app's own renderers */]),  // instead of provideSparkAttributeRenderers
]
```

```json
{ "name": "Score", "renderer": "spark-gauge", "rendererOptions": { "bands": "letter-grades" } }
```

Four renderers for scores and their history, each drawing a grid cell and a detail value and configured
through `rendererOptions`:

- `spark-gauge`: a ring with the score inside and, with `"bands": "letter-grades"`, an A–F grade (A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50).
- `spark-threshold-bar`: a compact bar coloured by band (default green ≥ 80, amber ≥ 60, red), the value beside it.
- `spark-trend`: a sparkline, or with `"display": "chart"` a trend chart with an optional goal line.
- `spark-delta`: a signed change, green up and red down, or the reverse with `"higherIsBetter": false`.

No value draws a muted dash, never a zero. The components (`<spark-threshold-bar>` and so on) can also be
used in a template with `[value]` and `[options]`; `<spark-trend-sparkline>` and `<spark-trend-chart>` are the
two halves of `spark-trend`, each loading only its own chart. CodeCoverage draws its coverage bar, sparkline, trend
chart and Δ column through them. See
[guide-score-renderers.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-score-renderers.md)
for every option.

### Notifications

```ts
import { SparkNotificationBellComponent, SparkPushToggleComponent } from '@mintplayer/ng-spark/notifications';
```

```html
<spark-notification-bell />          <!-- in the top bar -->
<spark-push-toggle />                <!-- on an account or settings page -->
```

The client of the server package MintPlayer.Spark.Notifications (`spark.AddNotifications()`):

- The bell shows the unread count and, in its menu, the latest notifications (title, message, relative time).
  Choosing one marks it read and follows its link, through the router when it is on this origin.
  "Mark all as read" and a link to the full list (`/query/notifications`; `[allLink]` or
  `provideSparkNotifications({ listLink, recentCount })` change it) complete the menu.
- `SparkNotificationsService` holds `unreadCount`, `recent`, `markRead(ids?)` and `open(notification)`. Both
  lists are live queries on the one `/spark/live` socket: no polling. They resubscribe when the signed-in user changes.
- Web Push: `enablePush()` (call it from a click: it asks permission first), `disablePush()`, `pushState`
  (`unsupported` · `denied` · `off` · `on`) and `pushConfigured` (the server has VAPID keys). Push needs
  `provideSparkServiceWorker()` with the worker enabled. On iPhone and iPad the app must be on the Home Screen
  first; until then the state is `unsupported` and the toggle says so.
- A push clicked while the app is open becomes an in-app navigation (`SparkNotificationClicks`, started by
  `provideSparkServiceWorker()`). When the app is closed, Angular's service worker opens the page itself
  from the payload's `onActionClick`.

### Share links

```ts
import { withShareRoutes } from '@mintplayer/ng-spark/share';

export const routes: Routes = [
  ...withShareRoutes(),                     // shared/:type/:token, no guard
  { path: '', component: ShellComponent, children: [...sparkRoutes()] },
];
```

A share link (`spark.AddShareLinks(...)` on the server) is `{origin}/shared/{type}/{token}?id={id}`. The page shows,
read-only and to anyone holding the link, exactly what `POST /spark/share/load` returns: the declared attributes
through the usual cells and renderers (the score renderers included) and one table per declared child query,
paged through `POST /spark/share/query`. A revoked, replaced or unknown link reads "This link does not work (any
more)" (`share.notFound`). `withShareRoutes({ path, component })` moves or replaces the page; keep
`Spark:Share:LinkPath` in step with the path.

## More

- [Repository README](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/README.md) — the developer guides
- [HTTP API specification](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/Spark-API-Specification.md)
- [Release notes 11.0.0-preview.91 / ng-spark 22.24.0](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/release-notes-preview-91.md)
