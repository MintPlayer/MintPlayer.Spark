import { Routes, Route } from '@angular/router';

export interface SparkRouteConfig {
  queryList?: Route['loadComponent'];
  poCreate?: Route['loadComponent'];
  poEdit?: Route['loadComponent'];
  poDetail?: Route['loadComponent'];
}

export function sparkRoutes(config?: SparkRouteConfig): Routes {
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
      loadComponent: config?.poDetail ?? (() => import('@mintplayer/ng-spark/po-detail').then(m => m.SparkPoDetailComponent))
    },
    {
      path: 'po/:type',
      loadComponent: config?.queryList ?? (() => import('@mintplayer/ng-spark/query-list').then(m => m.SparkQueryListComponent))
    }
  ];
}
