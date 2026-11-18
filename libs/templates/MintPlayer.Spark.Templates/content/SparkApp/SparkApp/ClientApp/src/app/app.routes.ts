import { Routes } from '@angular/router';
//#if (HasOidc)
import { oidcProvider, sparkAuthRoutes, withAccount, withExternalLogin, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
//#elseif (HasAuth)
import { sparkAuthRoutes, withAccount, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
//#endif
//#if (UseIdentityProvider)
import { withConnectedApplications, withDeveloperRoutes, withIdentityProvider, withManagementRoutes } from '@mintplayer/ng-spark/identity-provider';
//#endif
//#if (UseOrganizations)
import { withOrganizationRoutes } from '@mintplayer/ng-spark/organizations';
//#endif
//#if (UseBoards)
import { sparkBoardRoute } from '@mintplayer/ng-spark/boards';
//#endif
//#if (UseSearch)
import { sparkSearchRoutes } from '@mintplayer/ng-spark/search';
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
//#if (UseIdentityProvider)
        withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes()),
//#endif
//#if (UseOrganizations)
        // The page a mailed invitation or join request opens.
        withOrganizationRoutes(),
//#endif
      ),
//#elseif (HasAuth)
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withAccount(),
//#if (UseIdentityProvider)
        withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes()),
//#endif
//#if (UseOrganizations)
        // The page a mailed invitation or join request opens.
        withOrganizationRoutes(),
//#endif
      ),
//#endif
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
//#if (UseBoards)
      // The 'home' board of App_Data/boards.json.
      sparkBoardRoute('board', 'home'),
//#endif
//#if (UseSearch)
      // The results page (/search?q=), which the server's SearchPagePath names.
      ...sparkSearchRoutes(),
//#endif
      ...sparkRoutes(),
    ]
  }
];
