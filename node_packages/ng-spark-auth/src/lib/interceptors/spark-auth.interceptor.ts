import { inject } from '@angular/core';
import { HttpInterceptorFn } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { SPARK_AUTH_CONFIG } from '../models';

export const sparkAuthInterceptor: HttpInterceptorFn = (req, next) => {
  const config = inject(SPARK_AUTH_CONFIG);
  const router = inject(Router);

  return next(req).pipe(
    tap({
      error: (error) => {
        if (
          error.status === 401 &&
          !req.url.startsWith(config.apiBasePath)
        ) {
          router.navigate([config.loginUrl], {
            queryParams: { returnUrl: router.url },
          });
        }
      },
    }),
  );
};
