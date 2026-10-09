import { Routes } from '@angular/router';
import { oidcProvider, sparkAuthRoutes, withAccount, withExternalLogin, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkModerationRoutes } from '@mintplayer/ng-spark/moderation';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // The password family (the server runs LocalCredentials = Full) and the #460 D16 account pages:
      // confirm-email (the target of the confirmation mail), profile, password, two-factor and personal
      // data / account deletion. QnA has no passkeys, and its connected-logins page is left out.
      // #490 M6: the sign-in page offers SparkId as an OpenID Connect provider; the scheme must
      // equal the server's Spark:Auth:Providers entry, "SparkId". It links on to the password login.
      ...sparkAuthRoutes(
        withExternalLogin(oidcProvider('SparkId', 'Spark Identity')),
        withLocalLogin(),
        withRegistration(),
        withAccount({ exclude: ['externalLogins', 'passkeys'] }),
      ),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // moderation/review — a literal first segment, before sparkRoutes() as the entry point asks.
      ...sparkModerationRoutes(),
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      ...sparkRoutes(),
    ]
  }
];
