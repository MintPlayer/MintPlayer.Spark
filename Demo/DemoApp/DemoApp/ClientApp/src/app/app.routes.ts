import { Routes } from '@angular/router';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'query/:queryId', loadComponent: () => import('./pages/query-list/query-list.component') },
      { path: 'po/:type/new', loadComponent: () => import('./pages/po-create/po-create.component') },
      { path: 'po/:type/:id/edit', loadComponent: () => import('./pages/po-edit/po-edit.component') },
      { path: 'po/:type/:id', loadComponent: () => import('./pages/po-detail/po-detail.component') },
      { path: 'po/:type', loadComponent: () => import('./pages/query-list/query-list.component') }
    ]
  }
];
