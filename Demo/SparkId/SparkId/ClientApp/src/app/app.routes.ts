import { Routes } from '@angular/router';
import { sparkAuthRoutes } from '@mintplayer/ng-spark-auth';
import { sparkRoutes } from '@mintplayer/ng-spark';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      ...sparkAuthRoutes(),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes()
    ]
  }
];
