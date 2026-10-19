/**
 * The message protocol between an embed and the organiser's `<spark-embed>` loader
 * (`/spark/embed/loader.js`, `SparkEmbedLoaderScript` on the server). Keep both in step.
 */
export const SPARK_EMBED_MESSAGES = {
  /** `{ type, height }`: the embed's content height in CSS pixels; the loader sizes the iframe to it. */
  resize: 'spark-embed:resize',
  /** `{ type, url }`: navigate the organiser's page to a platform URL (the hand-over when a popup is blocked). */
  navigate: 'spark-embed:navigate',
} as const;

/** The hand-over token's query parameter on the top-level page. */
export const SPARK_EMBED_HANDOVER_PARAMETER = 'spark-handover';

/**
 * Whether `origin` (`scheme://host[:port]`) is one of `allowed`, with the server's rules: an exact origin, or
 * `https://*.host` for subdomains (not the bare domain), as in CSP `frame-ancestors`.
 */
export function sparkEmbedOriginAllowed(origin: string | null | undefined, allowed: readonly string[]): boolean {
  if (!origin) return false;
  let url: URL;
  try {
    url = new URL(origin);
  } catch {
    return false;
  }
  // An origin, not a page: nothing after the authority.
  if (url.pathname !== '/' || url.search || url.hash || url.username) return false;
  for (const entry of allowed) {
    const wildcard = entry.includes('://*.');
    let pattern: URL;
    try {
      pattern = new URL(wildcard ? entry.replace('://*.', '://wildcard.') : entry);
    } catch {
      continue;
    }
    if (pattern.protocol !== url.protocol || pattern.port !== url.port) continue;
    if (wildcard) {
      const suffix = '.' + pattern.hostname.slice('wildcard.'.length);
      if (url.hostname.endsWith(suffix) && url.hostname.length > suffix.length) return true;
    } else if (pattern.hostname === url.hostname) {
      return true;
    }
  }
  return false;
}
