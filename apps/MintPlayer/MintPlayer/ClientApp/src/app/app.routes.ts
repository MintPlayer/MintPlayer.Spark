import { Routes } from '@angular/router';
import {
  facebookProvider, googleProvider, linkedInProvider, microsoftProvider, sparkAuthRoutes, twitterProvider, withAccount, withExternalLogin,
  withLocalLogin, withPasskeys, withRegistration,
} from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  // At the top level, not among the shell's children: the shell's own path is '', so an empty-path child redirect
  // beside it never runs (CodeCoverage's and SeoTester's app.routes.ts have the same note).
  { path: '', redirectTo: 'home', pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      // Local accounts plus the five legacy external logins (docs/mintplayer_website_PRD.md G10). The page shows a
      // button only for a provider the server registered, so one without secrets never appears. Unlike SeoTester,
      // the migrated users sign in with passwords: Program.cs runs SparkLocalCredentials.Full and passkeys.
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withPasskeys(),
        withExternalLogin(facebookProvider(), googleProvider(), linkedInProvider(), microsoftProvider(), twitterProvider()),
        withAccount(),
      ),
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      ...sparkRoutes(),
    ]
  }
];
