import { Routes } from '@angular/router';
//#if (HasOidc)
import { oidcProvider, sparkAuthRoutes, withAccount, withExternalLogin, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
//#elseif (HasAuth)
import { sparkAuthRoutes, withAccount, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
//#endif
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
//#if (HasOidc)
      ...sparkAuthRoutes(
        // The scheme name matches Spark:Auth:Providers:OpenIdConnect:Oidc in appsettings.json.
        withExternalLogin(oidcProvider('Oidc', 'OpenID Connect')),
        withLocalLogin(),
        withRegistration(),
        withAccount(),
      ),
//#elseif (HasAuth)
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withAccount(),
      ),
//#endif
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes(),
    ]
  }
];
