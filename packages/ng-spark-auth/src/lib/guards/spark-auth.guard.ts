import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SPARK_AUTH_CONFIG } from '../models';
import { SparkAuthService } from '../services/spark-auth.service';

export const sparkAuthGuard: CanActivateFn = (route, state) => {
  const authService = inject(SparkAuthService);
  const router = inject(Router);
  const config = inject(SPARK_AUTH_CONFIG);

  if (authService.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree([config.loginUrl], {
    queryParams: { returnUrl: state.url },
  });
};
