import { Routes } from '@angular/router';
import { sparkAuthRoutes, withLocalLogin } from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // Administrators only: no registration page (the first administrator comes from user secrets).
      ...sparkAuthRoutes(withLocalLogin()),
      { path: '', redirectTo: 'address-form', pathMatch: 'full' },
      { path: 'address-form', loadComponent: () => import('./pages/address-form/address-form.component') },
      ...sparkRoutes()
    ]
  }
];
