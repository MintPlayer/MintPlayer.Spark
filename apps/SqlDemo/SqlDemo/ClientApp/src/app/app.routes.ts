import { Routes } from '@angular/router';
import { sparkAuthRoutes, withAccount, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // Password accounts (the server runs LocalCredentials = Full), stored in PostgreSQL.
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withAccount({ exclude: ['externalLogins', 'passkeys'] }),
      ),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      ...sparkRoutes(),
    ]
  }
];
