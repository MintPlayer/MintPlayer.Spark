import type { Location } from '@angular/common';

/** The application's own address: the page's origin and the router's base href. */
export interface SparkAppBase {
  /** The page's origin, `document.location.origin`. */
  readonly origin: string;
  /**
   * The router's base href, as `Location.prepareExternalUrl('/')` or `LocationStrategy.getBaseHref()` returns it:
   * `/`, `/app/`, or an absolute URL, whose path is taken on the page's origin.
   */
  readonly baseHref: string;
}

/** Where a link leads (`sparkAppUrl`). */
export type SparkAppUrl =
  /** Into this application: the router URL below the base href, its query and fragment kept. */
  | { readonly kind: 'internal'; readonly route: string }
  /** Anywhere else over http(s): another origin, or a path outside the base on this one, as the browser parses it. */
  | { readonly kind: 'external'; readonly href: string };

/**
 * Classifies `url` against the application's base, or `null` when it is not an http(s) link (`javascript:`, `data:`,
 * `mailto:`, an unparseable value): such a link is never handed to the router, an `href` or `location.assign`.
 *
 * - The string is cleaned as the browser cleans it before parsing (WHATWG URL): tab, LF and CR are removed
 *   anywhere, C0 controls and spaces trimmed from both ends. So `/<TAB>/evil.example` is judged as the browser
 *   reads it, the protocol-relative `//evil.example`.
 * - An app path, one leading `/` but not `//` or `/\`, resolves below the base, like a router link.
 * - Anything else resolves against the base. It is internal only on the page's origin, at the base path or below
 *   it, compared per segment and case-sensitively (`/application` is not below `/app/`, nor is `/APP/x`). The
 *   route is the path below the base, without a trailing slash, with the query and fragment.
 */
export function sparkAppUrl(url: string | null | undefined, base: SparkAppBase): SparkAppUrl | null {
  if (typeof url !== 'string') return null;
  const cleaned = url.replace(/[\t\n\r]/g, '').replace(/^[\u0000- ]+|[\u0000- ]+$/g, '');
  if (!cleaned) return null;

  let root: URL;
  let parsed: URL;
  try {
    const page = new URL(base.origin);
    const baseUrl = new URL(base.baseHref || '/', page.origin + '/');
    root = new URL(baseUrl.pathname.endsWith('/') ? baseUrl.pathname : baseUrl.pathname + '/', page.origin);
    const appPath = cleaned.startsWith('/') && !cleaned.startsWith('//') && !cleaned.startsWith('/\\');
    // "./" keeps an app path relative even when its first segment holds a colon ("/a:b" is not the scheme "a").
    parsed = new URL(appPath ? '.' + cleaned : cleaned, root);
  } catch {
    return null;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
  const basePath = root.pathname;
  const inside = parsed.origin === root.origin
    && (parsed.pathname === basePath.slice(0, -1) || parsed.pathname.startsWith(basePath));
  if (!inside) return { kind: 'external', href: parsed.href };

  let path = '/' + parsed.pathname.slice(basePath.length);
  if (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
  return { kind: 'internal', route: path + parsed.search + parsed.hash };
}

/**
 * The router URL for `url` when it points into this application, else `null` (`sparkAppUrl`): another origin, a
 * path outside the base href, and every non-http(s) value are not in-app, so a caller never hands a data-borne URL
 * to the router unchecked. The base is `location`'s (`/` without one).
 */
export function sparkInAppUrl(url: string | null | undefined, document: Document, location: Location | null): string | null {
  const origin = document.location?.origin;
  if (!origin) return null;
  const target = sparkAppUrl(url, { origin, baseHref: location?.prepareExternalUrl('/') ?? '/' });
  return target?.kind === 'internal' ? target.route : null;
}
