import { Routes, Route } from '@angular/router';

export interface SparkRouteConfig {
  queryList?: Route['loadComponent'];
  poCreate?: Route['loadComponent'];
  poEdit?: Route['loadComponent'];
  poDetail?: Route['loadComponent'];
  /**
   * Puts the object's breadcrumb in the detail URL as a slug, `/po/song/Songs%2F23/a-sky-full-of-stars`. The slug is
   * cosmetic: the id alone still opens the page, and a stale or wrong slug is corrected in place.
   */
  slugs?: boolean;
}

export function sparkRoutes(config?: SparkRouteConfig): Routes {
  const poDetail = config?.poDetail ?? (() => import('@mintplayer/ng-spark/po-detail').then(m => m.SparkPoDetailComponent));
  // Read by SparkPoDetailComponent, which writes the slug into the URL.
  const data = config?.slugs ? { sparkSlug: true } : undefined;
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
      loadComponent: poDetail,
      data
    },
    // After `:id/edit`, so the edit page keeps its URL.
    ...(config?.slugs ? [{ path: 'po/:type/:id/:slug', loadComponent: poDetail, data }] : []),
    {
      path: 'po/:type',
      loadComponent: config?.queryList ?? (() => import('@mintplayer/ng-spark/query-list').then(m => m.SparkQueryListComponent))
    }
  ];
}
