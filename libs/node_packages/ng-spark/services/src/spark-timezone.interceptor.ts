import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpFeature, HttpFeatureKind, HttpInterceptorFn, withInterceptors } from '@angular/common/http';
import { PLATFORM_ID, inject } from '@angular/core';

/**
 * The header carrying the viewer's IANA timezone id, e.g. `Europe/Brussels`.
 * Must stay in lockstep with `RequestTimeZoneResolver.HeaderName` on the server.
 */
export const SPARK_TIMEZONE_HEADER = 'X-Spark-Timezone';

/**
 * The default name of the cookie carrying the same id, for requests the browser makes without any
 * script running first (a server-side render, a link opened from a mail). Must stay in lockstep
 * with `SparkTimeZoneOptions.DefaultCookieName` (`Spark:TimeZone:CookieName`) on the server.
 */
export const SPARK_TIMEZONE_COOKIE = 'spark-timezone';

/** Options for {@link withSparkTimezone}. */
export interface SparkTimezoneOptions {
  /**
   * The cookie to write the viewer's zone into; `false` writes none (the header is still sent).
   * Defaults to {@link SPARK_TIMEZONE_COOKIE}. Use the same name as the server's
   * `Spark:TimeZone:CookieName`.
   */
  cookieName?: string | false;
}

/** One year: the zone only changes when the viewer travels, and each visit refreshes it. */
const COOKIE_MAX_AGE_SECONDS = 31_536_000;

/** The server's shape check (`RequestTimeZoneResolver`): an id failing it would be ignored anyway. */
const ZONE_ID_SHAPE = /^[A-Za-z][A-Za-z0-9_+\-]*(\/[A-Za-z0-9_+\-]+){0,2}$/;

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

/** The value of cookie `name` in `cookies` (`document.cookie`), or `null`. */
function readCookie(cookies: string, name: string): string | null {
  for (const part of cookies.split(';')) {
    const eq = part.indexOf('=');
    if (eq > 0 && part.slice(0, eq).trim() === name) return part.slice(eq + 1).trim();
  }
  return null;
}

/**
 * Writes `zone` into cookie `name` unless it already holds it. Browser only; the server never
 * writes this cookie. `Secure` on https, `SameSite=Lax` so a link from a mail still carries it.
 */
function writeTimezoneCookie(doc: Document, name: string, zone: string): void {
  if (readCookie(doc.cookie, name) === zone) return;
  const secure = doc.location?.protocol === 'https:' ? '; Secure' : '';
  doc.cookie = `${name}=${zone}; Path=/; Max-Age=${COOKIE_MAX_AGE_SECONDS}; SameSite=Lax${secure}`;
}

/**
 * Builds the interceptor for {@link withSparkTimezone}. Exported for apps that compose their own
 * `withInterceptors([...])`.
 *
 * **This does not participate in reading or writing timestamps.** A `DateTimeOffset` means an
 * instant, and the browser converts in both directions on those paths. The header and cookie exist
 * for work the server does on its own — a scheduled export, a notification email, a server-side
 * render — where there is no browser in the loop to do the converting but the output still has to
 * read as local time.
 *
 * ⚠️ **Nothing happens on the server platform.** During a server-side render `Intl` names the Node
 * process's zone (UTC in a container), and a header carrying it would win over the viewer's cookie
 * on the server (spike S-TZ3, #460).
 */
export function createSparkTimezoneInterceptor(options: SparkTimezoneOptions = {}): HttpInterceptorFn {
  const cookieName = options.cookieName === undefined ? SPARK_TIMEZONE_COOKIE : options.cookieName;

  return (req, next) => {
    if (!isPlatformBrowser(inject(PLATFORM_ID))) return next(req);

    const zone = resolveBrowserTimeZone();
    if (!zone) return next(req);

    if (cookieName && ZONE_ID_SHAPE.test(zone)) {
      writeTimezoneCookie(inject(DOCUMENT), cookieName, zone);
    }

    // Never annotate a cross-origin call: the viewer's timezone is a (small) fingerprinting signal
    // and third-party hosts have no business receiving it.
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(req.url)) {
      try {
        if (new URL(req.url).origin !== window.location.origin) return next(req);
      } catch {
        return next(req);
      }
    }

    return next(req.clone({ setHeaders: { [SPARK_TIMEZONE_HEADER]: zone } }));
  };
}

/**
 * Attaches {@link SPARK_TIMEZONE_HEADER} to same-origin requests and keeps the
 * {@link SPARK_TIMEZONE_COOKIE} cookie current — with the default options. See
 * {@link createSparkTimezoneInterceptor}.
 */
export const sparkTimezoneInterceptor: HttpInterceptorFn = createSparkTimezoneInterceptor();

/**
 * `provideHttpClient(...withSparkTimezone())` — registers the timezone interceptor.
 *
 * Spreadable alongside `withSparkAuth()`:
 * `provideHttpClient(...withSparkAuth(), ...withSparkTimezone())`.
 *
 * The cookie is written on the first request the browser app makes, so the very first server-side
 * render of a first visit has no cookie and renders in UTC.
 */
export function withSparkTimezone(options?: SparkTimezoneOptions): HttpFeature<HttpFeatureKind>[] {
  return [withInterceptors([options ? createSparkTimezoneInterceptor(options) : sparkTimezoneInterceptor])];
}
