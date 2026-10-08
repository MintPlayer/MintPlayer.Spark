import { Routes } from '@angular/router';
import { oidcProvider, sparkAuthRoutes, withAccount, withExternalLogin, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // Pages are opted into one feature at a time now. Fleet keeps the full password family and the account pages (passkeys among them),
      // matching its server's LocalCredentials = Full.
      // SparkId (the demo identity provider) is offered as an OpenID Connect sign-in; the scheme must equal
      // the server's Spark:Auth:Providers entry, "SparkId" (docs/identity_provider_platform_PRD.md R1).
      ...sparkAuthRoutes(
        withExternalLogin(oidcProvider('SparkId', 'Spark Identity')),
        withLocalLogin(),
        withRegistration(),
        withAccount(),
      ),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes({
        poCreate: () => import('./pages/po-create/po-create.component'),
        poEdit: () => import('./pages/po-edit/po-edit.component'),
      })
    ]
  }
];
