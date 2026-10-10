import type { CanActivateFn } from '@angular/router';
import { sparkAuthGuard } from '@mintplayer/ng-spark/auth/guards';
import type { SparkAuthRouteEntry } from '@mintplayer/ng-spark/auth/models';
import type { SparkAuthRoutesFeature } from '@mintplayer/ng-spark/auth/routes';

/**
 * The organizations package's one SPA page (`docs/seo_analyzer_PRD.md` X11): the target of a mailed
 * invitation or join request, `organizations/accept/:token?id=<invitationId>`, as a `sparkAuthRoutes()`
 * feature:
 *
 * ```ts
 * sparkAuthRoutes(withExternalLogin(), withAccount(), withOrganizationRoutes())
 * ```
 *
 * The page needs a signed-in user: the guard (default `[sparkAuthGuard]`) sends everyone else to the
 * sign-in page and back. Organizations, members and invitations themselves are the generic `po/:type`
 * and `query/:id` screens. Moving the page means setting `Spark:Organizations:AcceptPath` to match, so
 * the mailed links follow.
 */
export function withOrganizationRoutes(
  options?: { canActivate?: CanActivateFn[]; accept?: SparkAuthRouteEntry },
): SparkAuthRoutesFeature {
  const guard = options?.canActivate ?? [sparkAuthGuard];
  const entry = options?.accept;
  const path = entry === undefined ? 'organizations/accept/:token' : typeof entry === 'string' ? entry : entry.path;
  const component = typeof entry === 'object' && entry.component ? entry.component : undefined;
  return {
    paths: {},
    children: [
      {
        path,
        loadComponent: component
          ? () => Promise.resolve(component)
          : () => import('@mintplayer/ng-spark/organizations/accept').then(m => m.SparkAcceptOrganizationInvitationComponent),
        ...(guard.length ? { canActivate: guard } : {}),
      },
    ],
  };
}
