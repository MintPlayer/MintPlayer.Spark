import { Routes } from '@angular/router';
import {
  githubProvider, googleProvider, microsoftProvider, sparkAuthRoutes, withAccount, withExternalLogin, withLocalLogin, withPasskeys,
  withRegistration,
} from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  // At the top level, not among the shell's children: the shell's own path is '', so an empty-path child redirect
  // beside it never runs (MintPlayer's app.routes.ts has the same note).
  { path: '', redirectTo: 'home', pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      // Program.cs runs LocalCredentials Full and passkeys; a provider button shows only when the server registered it (D7).
      ...sparkAuthRoutes(
        withLocalLogin(),
        withRegistration(),
        withPasskeys(),
        withExternalLogin(googleProvider(), microsoftProvider(), githubProvider()),
        withAccount(),
      ),
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // The anonymous drawing in this browser (D3), an account's drawing, and a shared read-only link (D20).
      { path: 'draft', data: { mode: 'draft' }, loadComponent: () => import('./pages/editor/editor-page.component') },
      { path: 'p/:key', data: { mode: 'owned' }, loadComponent: () => import('./pages/editor/editor-page.component') },
      { path: 's/:token', data: { mode: 'shared' }, loadComponent: () => import('./pages/editor/editor-page.component') },
      // The link in a report mail; the server answers 403 to anyone but an administrator.
      { path: 'admin/takedown', loadComponent: () => import('./pages/takedown/takedown.component') },
      // Last: sparkRoutes() has parameterised first segments that would shadow anything after them.
      ...sparkRoutes({ notFound: true }),
    ],
  },
];
