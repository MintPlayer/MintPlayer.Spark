import { Routes } from '@angular/router';
import {
  githubProvider, googleProvider, microsoftProvider, oidcProvider, sparkAuthRoutes, twitterProvider, withAccount, withExternalLogin,
} from '@mintplayer/ng-spark/auth/routes';
import { withOrganizationRoutes } from '@mintplayer/ng-spark/organizations';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { withShareRoutes } from '@mintplayer/ng-spark/share';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  // A share link's page (X6): anonymous, the token is the credential, so outside the signed-in chrome.
  ...withShareRoutes(),
  {
    path: '',
    component: ShellComponent,
    children: [
      // External sign-in only (docs/seo_analyzer_PRD.md D2): the page shows a button per provider the SERVER
      // registered, so an unconfigured one never appears. SparkId is Development's (appsettings.Development.json).
      // The account pages include the connected logins (ConfirmByEmail linking, D7) and passkeys (D46); no passwords.
      // withOrganizationRoutes() is the page a mailed invitation or join request opens (X11).
      ...sparkAuthRoutes(
        withExternalLogin(microsoftProvider(), githubProvider(), twitterProvider(), googleProvider(), oidcProvider('SparkId', 'Spark Identity')),
        withAccount(),
        withOrganizationRoutes(),
      ),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      // /bot is the server's page (Bot/BotPageEndpoint.cs), excluded from the service worker in ngsw-config.json.
      ...sparkRoutes(),
    ]
  }
];
