import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark';
import { sparkAuthRoutes, sparkAuthGuard } from '@mintplayer/ng-spark-auth';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      ...sparkAuthRoutes(),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'github-projects', loadComponent: () => import('./pages/github-projects/github-projects.component'), canActivate: [sparkAuthGuard] },
      ...sparkRoutes()
    ]
  }
];
