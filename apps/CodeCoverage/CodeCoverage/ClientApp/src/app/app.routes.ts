import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkAuthRoutes, withExternalLogin, githubProvider } from '@mintplayer/ng-spark-auth/routes';
import { ShellComponent } from './shell/shell.component';
import { accountRedirectGuard, commitRedirectGuard, repositoryRedirectGuard } from './spark/vanity-redirects';
import { HOME_URL } from './spark/home-route';

export const routes: Routes = [
  // Home is a virtual persistent object now, so its real URL is the program unit's
  // (/po/home/main), rendered by the poDetail route inside the shell below.
  //
  // ⚠️ This one lives at the TOP level, not among the shell's children, and that is
  // load-bearing: the shell's own path is '', so an empty-path child redirect beside
  // it never runs — the parent consumes the empty URL and '/' renders an empty
  // outlet, silently and with no error. Matched before the shell because pathMatch
  // 'full' only takes the bare '/' and leaves every other URL to it.
  { path: '', redirectTo: HOME_URL, pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      // /home is kept as a redirect rather than retired: it is the OAuth handler's
      // failure redirect (server-side, in Program.cs), the post-sign-in return URL,
      // and whatever anyone has bookmarked. A non-empty path, so unlike the one
      // above it works fine as a child.
      { path: 'home', redirectTo: HOME_URL, pathMatch: 'full' },
      // Opt-in since ng-spark-auth 22.2: passing no features mounts NO pages at
      // all. GitHub is the only provider — the server's LocalCredentials are
      // Disabled — so withLocalLogin()/withRegistration() would mount pages
      // posting to endpoints that aren't mapped.
      ...sparkAuthRoutes(withExternalLogin(githubProvider())),
      // Accounts, repositories and commits ARE the generic Spark detail pages;
      // these shareable URLs (README badge markdown links to /r/{owner}/{name},
      // and /a/{login} is what the accounts grid links to) resolve the document
      // id and forward there.
      { path: 'a/:login', canActivate: [accountRedirectGuard], children: [] },
      { path: 'r/:owner/:repo', canActivate: [repositoryRedirectGuard], children: [] },
      { path: 'r/:owner/:repo/c/:sha', canActivate: [commitRedirectGuard], children: [] },
      // The code viewer has no persistent object of its own, so it stays a page.
      { path: 'r/:owner/:repo/c/:sha/f', loadComponent: () => import('./pages/file/file.component') },
      // poDetail override: the generic detail page plus the app panels that
      // can't be expressed as attribute renderers (badge, trend chart, CI
      // setup, the commit file tree).
      ...sparkRoutes({ poDetail: () => import('./spark/po-detail-page.component') })
    ]
  }
];
