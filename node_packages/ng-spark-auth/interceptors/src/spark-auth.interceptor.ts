import { inject } from '@angular/core';
import { HttpInterceptorFn } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { SPARK_AUTH_CONFIG, sanitizeReturnUrl } from '@mintplayer/ng-spark-auth/models';

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
          // R2-L4: drop query string and fragment from the captured URL before
          // putting it on the login URL. The unmodified router.url leaks
          // in-app draft text / document IDs / search queries into the next
          // access log entry for /login. Also pass through the same sanitizer
          // the login component uses so a path with embedded // can't reflect
          // back as an open redirect after authentication.
          const pathOnly = router.url.split('?')[0].split('#')[0];
          const returnUrl = sanitizeReturnUrl(pathOnly, config.defaultRedirectUrl);
          router.navigate([config.loginUrl], {
            queryParams: { returnUrl },
          });
        }
      },
    }),
  );
};
