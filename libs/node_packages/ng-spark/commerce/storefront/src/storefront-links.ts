/** Path prefixes that are never a page: Spark's API (PDFs, `.ics`, feeds) and the identity provider. */
export const STOREFRONT_NON_PAGE_PREFIXES: readonly string[] = ['/spark/', '/connect/', '/.well-known/'];

/**
 * The in-app path a click should be routed to, or null to let the browser follow it: only a plain left click on an
 * `<a href>` of this origin that nothing else handled, without `target`, `download` or `rel="external"`, and not an
 * API path or a file (anything with an extension, such as a PDF).
 */
export function inAppLinkTarget(event: MouseEvent, origin: string): string | null {
  if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return null;
  const anchor = (event.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
  if (!anchor) return null;
  if ((anchor.target && anchor.target !== '_self') || anchor.hasAttribute('download') || /\bexternal\b/.test(anchor.rel)) return null;
  return inAppPathOf(anchor.getAttribute('href') ?? '', origin);
}

/** The routable path of `href`, or null (other origin, API path, file, fragment-only link). Pure, for the unit test. */
export function inAppPathOf(href: string, origin: string): string | null {
  if (!href || href.startsWith('#') || /^(mailto|tel|javascript):/i.test(href)) return null;
  let url: URL;
  try {
    url = new URL(href, origin + '/');
  } catch {
    return null;
  }
  if (url.origin !== origin) return null;
  if (STOREFRONT_NON_PAGE_PREFIXES.some((p) => url.pathname.startsWith(p))) return null;
  if (/\.[a-z0-9]{2,5}$/i.test(url.pathname)) return null;
  return url.pathname + url.search + url.hash;
}
