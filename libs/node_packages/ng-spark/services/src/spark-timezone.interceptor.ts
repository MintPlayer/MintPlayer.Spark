import { HttpFeature, HttpFeatureKind, HttpInterceptorFn, withInterceptors } from '@angular/common/http';

/**
 * The header carrying the viewer's IANA timezone id, e.g. `Europe/Brussels`.
 * Must stay in lockstep with `RequestTimeZoneResolver.HeaderName` on the server.
 */
export const SPARK_TIMEZONE_HEADER = 'X-Spark-Timezone';

/**
 * Returns the browser's IANA zone id, or `null` when the environment cannot name one.
 *
 * An id is deliberately sent rather than a numeric offset: an offset is only valid at one instant,
 * so a server asked to render "next Tuesday at 09:00" from an offset captured today gets it wrong
 * across a DST boundary. A zone id carries the rules, not one sample of them.
 */
export function resolveBrowserTimeZone(): string | null {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
}

/**
 * Attaches {@link SPARK_TIMEZONE_HEADER} to same-origin requests.
 *
 * **This does not participate in reading or writing timestamps.** A `DateTimeOffset` means an
 * instant, and the browser converts in both directions on those paths. The header exists for work
 * the server starts on its own — a scheduled export, a notification email — where there is no
 * browser in the loop to do the converting but the output still has to read as local time.
 */
export const sparkTimezoneInterceptor: HttpInterceptorFn = (req, next) => {
  // Never annotate a cross-origin call: the viewer's timezone is a (small) fingerprinting signal and
  // third-party hosts have no business receiving it.
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(req.url)) {
    try {
      if (new URL(req.url).origin !== window.location.origin) return next(req);
    } catch {
      return next(req);
    }
  }

  const zone = resolveBrowserTimeZone();
  if (!zone) return next(req);

  return next(req.clone({ setHeaders: { [SPARK_TIMEZONE_HEADER]: zone } }));
};

/**
 * `provideHttpClient(...withSparkTimezone())` — registers {@link sparkTimezoneInterceptor}.
 *
 * Spreadable alongside `withSparkAuth()`:
 * `provideHttpClient(...withSparkAuth(), ...withSparkTimezone())`.
 */
export function withSparkTimezone(): HttpFeature<HttpFeatureKind>[] {
  return [withInterceptors([sparkTimezoneInterceptor])];
}
