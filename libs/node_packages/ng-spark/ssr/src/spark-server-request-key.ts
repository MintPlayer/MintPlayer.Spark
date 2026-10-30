/**
 * The key a server-supplied response is filed under: `METHOD pathAndQuery canonicalBody`.
 *
 * The .NET supplier (`SparkPrerenderRequestKey`) computes the same string for the requests it
 * predicts, so the two sides must agree byte for byte. The body is canonical JSON — object keys sorted
 * ordinally at every depth, `undefined` members dropped as `JSON.stringify` drops them — because the
 * order a component happens to build its request object in is not something the server can predict.
 * An absent body is the empty string.
 *
 * `url` may be absolute: platform-server resolves a relative request URL against the render's
 * document (`/spark/culture` arrives as `http://localhost/spark/culture`), so only the path and query
 * are kept. The server's own origin is not part of the key.
 */
export function sparkServerRequestKey(method: string, url: string, body: unknown): string {
  return `${method.toUpperCase()} ${pathAndQuery(url)} ${body === null || body === undefined ? '' : canonicalJson(body)}`;
}

function pathAndQuery(url: string): string {
  if (url.startsWith('/') && !url.startsWith('//')) return url;
  const parsed = new URL(url, 'http://localhost');
  return parsed.pathname + parsed.search;
}

export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortKeys(value));
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value === null || typeof value !== 'object') return value;
  const sorted: Record<string, unknown> = {};
  for (const key of Object.keys(value).sort()) {
    const member = (value as Record<string, unknown>)[key];
    if (member !== undefined) sorted[key] = sortKeys(member);
  }
  return sorted;
}
