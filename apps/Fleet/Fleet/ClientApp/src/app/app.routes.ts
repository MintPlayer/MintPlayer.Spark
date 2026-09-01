import { Routes } from '@angular/router';
import { sparkAuthRoutes, withLocalLogin, withRegistration } from '@mintplayer/ng-spark-auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // Pages are opted into one feature at a time now. Fleet keeps the full password family,
      // matching its server's LocalCredentials = Full.
      ...sparkAuthRoutes(withLocalLogin(), withRegistration()),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes({
        poCreate: () => import('./pages/po-create/po-create.component'),
        poEdit: () => import('./pages/po-edit/po-edit.component'),
      })
    ]
  }
];
