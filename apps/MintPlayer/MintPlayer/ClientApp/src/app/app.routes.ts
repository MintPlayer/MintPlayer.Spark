import { Routes } from '@angular/router';
import {
  facebookProvider, googleProvider, linkedInProvider, microsoftProvider, sparkAuthRoutes, twitterProvider, withAccount, withExternalLogin,
  withLocalLogin, withPasskeys, withRegistration,
} from '@mintplayer/ng-spark/auth/routes';
import { sparkObjectRoute, sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkSearchRoutes } from '@mintplayer/ng-spark/search';
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
      // /search?q=: the results page, server-rendered (MintPlayer.Spark.Search's prerender contributor).
      ...sparkSearchRoutes(),
      // #601: a song's page has a header with its thumbnail and a play button. Before sparkRoutes(), whose
      // po/:type/:id would open Spark's generic detail page; the URL is the same (/po/song/Songs%2F23/a-sky-full-of-stars).
      ...sparkObjectRoute({ type: 'song', path: 'po/song/:id', loadComponent: () => import('./pages/song/song-page.component') }),
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      // Slugs, as the legacy URLs had: /po/song/Songs%2F23/a-sky-full-of-stars.
      // notFound: a URL nothing matches renders a 404 page, answered with status 404 (not a soft 404).
      ...sparkRoutes({ notFound: true }),
    ]
  }
];
