import { Routes } from '@angular/router';
import {
  githubProvider, googleProvider, microsoftProvider, oidcProvider, sparkAuthRoutes, twitterProvider, withAccount, withConnectedAgents, withExternalLogin,
} from '@mintplayer/ng-spark/auth/routes';
import { withOrganizationRoutes } from '@mintplayer/ng-spark/organizations';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { withShareRoutes } from '@mintplayer/ng-spark/share';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  // A share link's page (X6): anonymous, the token is the credential, so outside the signed-in chrome.
  ...withShareRoutes(),
  // At the top level, not among the shell's children: the shell's own path is '', so an empty-path child redirect
  // beside it never runs and '/' rendered an empty page (found in M11; CodeCoverage's app.routes.ts has the same note).
  { path: '', redirectTo: 'home', pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      // External sign-in only (docs/seo_analyzer_PRD.md D2): the page shows a button per provider the SERVER
      // registered, so an unconfigured one never appears. SparkId is Development's (appsettings.Development.json).
      // The account pages include the connected logins (ConfirmByEmail linking, D7) and passkeys (D46); no passwords.
      // withOrganizationRoutes() is the page a mailed invitation or join request opens (X11).
      // withConnectedAgents() is Account → Connected applications (MCP, docs/seotester/mcp.md): the AI agents the user
      // connected, what each may see and change, revoking them, and personal access tokens.
      ...sparkAuthRoutes(
        withExternalLogin(microsoftProvider(), githubProvider(), twitterProvider(), googleProvider(), oidcProvider('SparkId', 'Spark Identity')),
        withAccount(),
        withConnectedAgents(),
        withOrganizationRoutes(),
      ),
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      // /bot is the server's page (Bot/BotPageEndpoint.cs), excluded from the service worker in ngsw-config.json.
      // The detail page is the generic one plus SeoTester's panels (seo/po-detail-page.component.ts).
      ...sparkRoutes({ poDetail: () => import('./seo/po-detail-page.component') }),
    ]
  }
];
