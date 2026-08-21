import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { githubProvider, sparkAuthRoutes, withExternalLogin } from '@mintplayer/ng-spark-auth/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // Matches the server's LocalCredentials = Disabled: no login/register/forgot/reset pages,
      // just the provider-button landing at /sign-in. githubProvider() only decorates the button —
      // the server stays authoritative over which providers exist.
      ...sparkAuthRoutes(withExternalLogin(githubProvider())),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'github-projects', loadComponent: () => import('./pages/github-projects/github-projects.component') },
      ...sparkRoutes()
    ]
  }
];
