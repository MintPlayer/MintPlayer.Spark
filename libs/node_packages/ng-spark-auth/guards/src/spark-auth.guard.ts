import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SPARK_AUTH_CONFIG, resolveSignInUrl } from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';

export const sparkAuthGuard: CanActivateFn = (route, state) => {
  const authService = inject(SparkAuthService);
  const router = inject(Router);
  const config = inject(SPARK_AUTH_CONFIG);

  if (authService.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree([resolveSignInUrl(config, router)], {
    queryParams: { returnUrl: state.url },
  });
};

/**
 * Like {@link sparkAuthGuard}, but waits for the session check when the user is not known to be
 * signed in yet. `SparkAuthService` reads `/me` asynchronously at start-up, so on a hard reload of an
 * account page the synchronous guard would send a signed-in user to the sign-in page. The account
 * pages mounted by `withAccount()` use this one.
 */
export const sparkAuthenticatedGuard: CanActivateFn = async (route, state) => {
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
