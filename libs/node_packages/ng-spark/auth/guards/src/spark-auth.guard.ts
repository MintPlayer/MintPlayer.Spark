import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SPARK_AUTH_CONFIG, resolveSignInUrl } from '@mintplayer/ng-spark/auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';

/**
 * Lets a signed-in user through and sends everyone else to the sign-in page with a `returnUrl`.
 *
 * `SparkAuthService` reads `/me` asynchronously at start-up, so on a hard reload the user is not
 * known to be signed in yet when the router runs this guard. When the session is not known, the
 * guard waits for the session check before deciding, so a signed-in user is never bounced to the
 * sign-in page. A session that is already known passes without a round trip.
 */
export const sparkAuthGuard: CanActivateFn = async (route, state) => {
  const authService = inject(SparkAuthService);
  const router = inject(Router);
  const config = inject(SPARK_AUTH_CONFIG);

  if (authService.isAuthenticated()) return true;
  const user = await authService.checkAuth();
  if (user?.isAuthenticated) return true;

  return router.createUrlTree([resolveSignInUrl(config, router)], {
    queryParams: { returnUrl: state.url },
  });
};

/**
 * The same guard as {@link sparkAuthGuard}. It existed separately while `sparkAuthGuard` decided
 * before the session check had finished; both now wait for it.
 */
export const sparkAuthenticatedGuard: CanActivateFn = sparkAuthGuard;
