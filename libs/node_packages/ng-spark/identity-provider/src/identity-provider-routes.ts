import type { CanActivateFn } from '@angular/router';
import { sparkAuthGuard } from '@mintplayer/ng-spark/auth/guards';
import type { SparkAuthRouteEntry, SparkAuthRoutePaths, SparkAccountOverviewLink } from '@mintplayer/ng-spark/auth/models';
import type { SparkAuthRoutesFeature } from '@mintplayer/ng-spark/auth/routes';

type Child = SparkAuthRoutesFeature['children'][number];
type Loader = () => Promise<any>;

/**
 * One group of identity-provider pages, passed to {@link withIdentityProvider}. Produced only by
 * `withConnectedApplications()`, `withDeveloperRoutes()` and `withManagementRoutes()`.
 */
export interface SparkIdentityProviderRoutesFeature {
  readonly children: Child[];
  readonly paths: SparkAuthRoutePaths;
  readonly accountLinks?: SparkAccountOverviewLink[];
}

/** Every feature's guard option: defaults to `[sparkAuthGuard]`; `[]` under an already guarded parent. */
export interface SparkIdentityProviderGuardOptions {
  canActivate?: CanActivateFn[];
}

function entryPath(entry: SparkAuthRouteEntry | undefined, defaultPath: string): string {
  if (entry === undefined) return defaultPath;
  return typeof entry === 'string' ? entry : entry.path;
}

/** An explicitly supplied component wins over the library's lazy import, as in `sparkAuthRoutes()`. */
function child(entry: SparkAuthRouteEntry | undefined, path: string, loader: Loader, guard: CanActivateFn[]): Child {
  const component = typeof entry === 'object' && entry.component ? entry.component : undefined;
  return {
    path,
    loadComponent: component ? () => Promise.resolve(component) : loader,
    ...(guard.length ? { canActivate: guard } : {}),
  };
}

/**
 * The identity provider's SPA pages (`docs/identity_provider_platform_PRD.md` D7), as one
 * `sparkAuthRoutes()` feature:
 *
 * ```ts
 * sparkAuthRoutes(
 *   withLocalLogin(), withAccount(),
 *   withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes()),
 * )
 * ```
 *
 * The sub-features merge into one feature, and each page keeps its own `import()` (its own secondary
 * entry point), so a page nobody opts into is never bundled. The pages popups show (`/connect/*`:
 * sign-in, consent, device, logout, error) are server-rendered and not part of this.
 *
 * Every path has a literal first segment, so the group neither shadows `sparkRoutes()` nor is shadowed
 * by it. Apps and resources are edited in the generic `po/:type` screens (PRD Q1 = A).
 */
export function withIdentityProvider(...features: SparkIdentityProviderRoutesFeature[]): SparkAuthRoutesFeature {
  const children: Child[] = [];
  const paths: SparkAuthRoutePaths = {};
  const accountLinks: SparkAccountOverviewLink[] = [];
  for (const feature of features) {
    children.push(...feature.children);
    Object.assign(paths, feature.paths);
    if (feature.accountLinks) accountLinks.push(...feature.accountLinks);
  }
  return { children, paths, accountLinks };
}

/**
 * Mounts the connected-applications page (default `account/applications`, PRD D6): the applications
 * the user granted access to, with whole-grant and per-scope withdrawal. Sets
 * `SPARK_AUTH_ROUTE_PATHS.connectedApplications` and adds a link to the account overview.
 */
export function withConnectedApplications(
  options?: SparkIdentityProviderGuardOptions & { connectedApplications?: SparkAuthRouteEntry },
): SparkIdentityProviderRoutesFeature {
  const guard = options?.canActivate ?? [sparkAuthGuard];
  const path = entryPath(options?.connectedApplications, 'account/applications');
  return {
    paths: { connectedApplications: '/' + path },
    accountLinks: [{ key: 'connectedApplications', path: '/' + path, label: { en: 'Connected applications', fr: 'Applications connectées', nl: 'Verbonden applicaties' } }],
    children: [
      child(options?.connectedApplications, path,
        () => import('@mintplayer/ng-spark/identity-provider/connected-applications').then(m => m.SparkConnectedApplicationsComponent),
        guard),
    ],
  };
}

/**
 * Mounts the developer portal (PRD D2/D3): `developers` (status, terms, request, registration token)
 * and `developers/invitations/:token?app=<applicationId>` (accept a team invitation; the mailed link's
 * target, which the server builds from this default path). Both need a signed-in user; the guard sends
 * anyone else to sign in and back.
 */
export function withDeveloperRoutes(
  options?: SparkIdentityProviderGuardOptions & { developers?: SparkAuthRouteEntry; invitation?: SparkAuthRouteEntry },
): SparkIdentityProviderRoutesFeature {
  const guard = options?.canActivate ?? [sparkAuthGuard];
  const developers = entryPath(options?.developers, 'developers');
  const invitation = entryPath(options?.invitation, 'developers/invitations/:token');
  return {
    paths: { developers: '/' + developers },
    children: [
      child(options?.developers, developers,
        () => import('@mintplayer/ng-spark/identity-provider/developers').then(m => m.SparkDevelopersComponent),
        guard),
      child(options?.invitation, invitation,
        () => import('@mintplayer/ng-spark/identity-provider/developers').then(m => m.SparkAcceptInvitationComponent),
        guard),
    ],
  };
}

/**
 * Mounts the management page (default `identity-provider/admin`, PRD D9): signing keys with
 * rotate-now, and links to the queues (developer requests, scope approvals, go-live reviews) and
 * records (applications, resources, grants, audit trail). Mount it for everyone signed in: the server
 * decides who is an administrator, and the page tells anyone else they may not see it.
 */
export function withManagementRoutes(
  options?: SparkIdentityProviderGuardOptions & { identityProviderManagement?: SparkAuthRouteEntry },
): SparkIdentityProviderRoutesFeature {
  const guard = options?.canActivate ?? [sparkAuthGuard];
  const path = entryPath(options?.identityProviderManagement, 'identity-provider/admin');
  return {
    paths: { identityProviderManagement: '/' + path },
    children: [
      child(options?.identityProviderManagement, path,
        () => import('@mintplayer/ng-spark/identity-provider/management').then(m => m.SparkIdentityProviderManagementComponent),
        guard),
    ],
  };
}
