import { Routes } from '@angular/router';
import { sparkAuthRoutes, withAccount, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
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
      // data / account deletion. QnA has no external providers and no passkeys, so those two pages are out.
      ...sparkAuthRoutes(
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
