import { Location } from '@angular/common';

/**
 * The router URL for `url` when it points into this application, else `null`.
 *
 * `url` may be app-relative (`/runs/42`) or absolute. It is resolved against the document's base URI;
 * only an `http(s)` URL on the page's own origin is in-app, and its path loses the `<base href>`
 * (through `Location.normalize`) because the router works below it. Everything else (another origin,
 * `javascript:`, `mailto:`, an unparseable value) is `null`, so a caller never hands data-borne URLs
 * to the router or to `location.assign` unchecked.
 */
export function sparkInAppUrl(url: string | null | undefined, document: Document, location: Location | null): string | null {
  if (!url) return null;
  let parsed: URL;
  try {
    parsed = new URL(url, document.baseURI);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
  if (parsed.origin !== document.location?.origin) return null;
  const path = location ? location.normalize(parsed.pathname) : parsed.pathname;
  return (path.startsWith('/') ? path : '/' + path) + parsed.search + parsed.hash;
}
