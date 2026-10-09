import { Routes } from '@angular/router';
import { sparkAuthRoutes, withLocalLogin, withRegistration, withAccount } from '@mintplayer/ng-spark/auth/routes';
import {
  withConnectedApplications,
  withDeveloperRoutes,
  withIdentityProvider,
  withManagementRoutes,
} from '@mintplayer/ng-spark/identity-provider';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // The identity provider's SPA pages (docs/identity_provider_platform_PRD.md D7): account/applications,
      // developers, developers/invitations/:token and identity-provider/admin. All have a literal first
      // segment, so their place relative to sparkRoutes() does not matter.
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withAccount({ exclude: ['externalLogins'] }),
        withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes()),
      ),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes()
    ]
  }
];
