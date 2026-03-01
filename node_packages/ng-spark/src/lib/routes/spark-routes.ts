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
      loadComponent: config?.queryList ?? (() => import('../components/query-list/spark-query-list.component').then(m => m.SparkQueryListComponent))
    },
    {
      path: 'po/:type/new',
      loadComponent: config?.poCreate ?? (() => import('../components/po-create/spark-po-create.component').then(m => m.SparkPoCreateComponent))
    },
    {
      path: 'po/:type/:id/edit',
      loadComponent: config?.poEdit ?? (() => import('../components/po-edit/spark-po-edit.component').then(m => m.SparkPoEditComponent))
    },
    {
      path: 'po/:type/:id',
      loadComponent: config?.poDetail ?? (() => import('../components/po-detail/spark-po-detail.component').then(m => m.SparkPoDetailComponent))
    },
    {
      path: 'po/:type',
      loadComponent: config?.queryList ?? (() => import('../components/query-list/spark-query-list.component').then(m => m.SparkQueryListComponent))
    }
  ];
}
