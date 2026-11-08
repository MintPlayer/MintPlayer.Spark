import { Routes, Route } from '@angular/router';

export interface SparkRouteConfig {
  queryList?: Route['loadComponent'];
  poCreate?: Route['loadComponent'];
  poEdit?: Route['loadComponent'];
  poDetail?: Route['loadComponent'];
  /**
   * Ends the routes with a catch-all page that a server render answers with a 404, instead of a 200 for any URL
   * (a soft 404). Only for an app that lists nothing after `sparkRoutes()`: the catch-all shadows every later route.
   * `true` for Spark's own page, or a component of the app's.
   */
  notFound?: boolean | Route['loadComponent'];
}

export function sparkRoutes(config?: SparkRouteConfig): Routes {
  const poDetail = config?.poDetail ?? (() => import('@mintplayer/ng-spark/po-detail').then(m => m.SparkPoDetailComponent));
  return [
    {
      path: 'query/:queryId',
      loadComponent: config?.queryList ?? (() => import('@mintplayer/ng-spark/query-list').then(m => m.SparkQueryListComponent))
    },
    {
      path: 'po/:type/new',
      loadComponent: config?.poCreate ?? (() => import('@mintplayer/ng-spark/po-create').then(m => m.SparkPoCreateComponent))
    },
    {
      path: 'po/:type/:id/edit',
      loadComponent: config?.poEdit ?? (() => import('@mintplayer/ng-spark/po-edit').then(m => m.SparkPoEditComponent))
    },
    {
      path: 'po/:type/:id',
      loadComponent: poDetail
    },
    // The object's canonical page (#602): the slug is the breadcrumb's, written by the server into `href`. After
    // `:id/edit`, so the edit page keeps its URL. The slug is cosmetic: the id alone opens the page too.
    { path: 'po/:type/:id/:slug', loadComponent: poDetail },
    {
      path: 'po/:type',
      loadComponent: config?.queryList ?? (() => import('@mintplayer/ng-spark/query-list').then(m => m.SparkQueryListComponent))
    },
    ...(config?.notFound ? [{
      path: '**',
      loadComponent: typeof config.notFound === 'function'
        ? config.notFound
        : () => import('./spark-not-found.component').then(m => m.SparkNotFoundComponent),
    }] : []),
  ];
}

export interface SparkObjectRouteConfig {
  /** The entity type's name, alias or id. */
  type: string;
  /**
   * The route path: the type's `route` template as is (`songs/{id}/{slug}`) or in Angular's form (`songs/:id`). It must
   * have an id segment. A trailing slug segment is optional: the route is mounted with and without it.
   */
  path: string;
  /** The page. Defaults to Spark's detail page, which reads the type from the route's data. */
  loadComponent?: Route['loadComponent'];
}

/**
 * The page of one entity type at its own URL (#602, tiers a and b): `songs/:id` (and `songs/:id/:slug`) for a type
 * whose model says `"route": "songs/{id}/{slug}"`, or the default `po/<alias>/:id` with a custom component. List it
 * before `sparkRoutes()`. The route's data names the type (`sparkType`), so Spark's detail page works on it too.
 */
export function sparkObjectRoute(config: SparkObjectRouteConfig): Routes {
  const loadComponent = config.loadComponent ?? (() => import('@mintplayer/ng-spark/po-detail').then(m => m.SparkPoDetailComponent));
  const path = config.path
    .replace(/^\/+|\/+$/g, '')
    .replace(/\{(\w+)\}/g, ':$1')
    .replace(/\/:slug$/, '');
  if (!path.includes(':id'))
    throw new Error(`sparkObjectRoute(${config.type}): the path '${config.path}' has no :id segment.`);
  const data = { sparkType: config.type };
  return [
    { path, loadComponent, data },
    { path: `${path}/:slug`, loadComponent, data },
  ];
}
