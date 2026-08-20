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
