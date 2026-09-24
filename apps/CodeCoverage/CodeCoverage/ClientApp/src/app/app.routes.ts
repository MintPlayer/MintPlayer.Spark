import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkAuthRoutes, withExternalLogin, githubProvider, withPasskeys } from '@mintplayer/ng-spark-auth/routes';
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
      // withPasskeys() adds a credential page for an already signed-in user, which is why it sits
      // alongside withExternalLogin() rather than replacing it: GitHub remains the way a new account
      // is created, and a passkey is added to it afterwards. It is the app's only forge-independent
      // credential — the server has LocalCredentials Disabled and always will.
      ...sparkAuthRoutes(withExternalLogin(githubProvider()), withPasskeys()),
      // poDetail override: the generic detail page plus the app panels that
      // can't be expressed as attribute renderers (badge, trend chart, CI
      // setup, the commit file tree).
      //
      // ⚠️ DECLARED BEFORE the provider-scoped routes below, and the order is the whole
      // collision story. Those routes start with a :provider parameter, which matches any
      // first segment — including 'po'. Declared first, `/po/r/123/edit` would bind
      // provider='po' and shadow the persistent-object editor. Declared here, every Spark
      // route is tried first, and none of them can match a forge-scoped URL because each
      // needs a literal first segment ('po', 'query', or an auth path). So the ambiguity
      // resolves by construction rather than by a route constraint, a hardcoded forge list
      // or a guard test — none of which would survive someone adding a forge and forgetting.
      ...sparkRoutes({ poDetail: () => import('./spark/po-detail-page.component') }),
      // Accounts, repositories and commits ARE the generic Spark detail pages; these
      // shareable URLs resolve the document id and forward there. README badge markdown
      // links to /{provider}/r/{owner}/{name}, and /{provider}/a/{login} is what the
      // accounts grid links to.
      //
      // The forge is the outermost segment because that is how the URL reads — "on GitHub,
      // this repository" — and it always precedes the owner (D27). :provider is a plain
      // parameter: the canonical spellings live in EForgeProvider on the server, and
      // repeating them here would mean a new forge needed routes added by hand.
      { path: ':provider/a/:login', canActivate: [accountRedirectGuard], children: [] },
      { path: ':provider/r/:owner/:repo', canActivate: [repositoryRedirectGuard], children: [] },
      { path: ':provider/r/:owner/:repo/c/:sha', canActivate: [commitRedirectGuard], children: [] },
      // The code viewer has no persistent object of its own, so it stays a page.
      { path: ':provider/r/:owner/:repo/c/:sha/f', loadComponent: () => import('./pages/file/file.component') }
    ]
  }
];
