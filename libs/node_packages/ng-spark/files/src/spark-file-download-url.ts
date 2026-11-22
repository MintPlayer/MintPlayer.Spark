/**
 * The checked download of a stored file (`GET /spark/files/download`): authorized on every request, so it never expires.
 * An image falls back to it when its short-lived content URL has expired (the page stayed open past five minutes); the
 * server serves a raster image inline.
 */
export function sparkCheckedDownloadUrl(
  baseUrl: string,
  objectTypeId: string,
  id: string,
  attribute: string,
  fileId: string,
  variant?: string,
): string {
  const query = new URLSearchParams({ type: objectTypeId, id, attribute, file: fileId });
  if (variant) query.set('variant', variant);
  return `${baseUrl}/files/download?${query.toString()}`;
}

/** The variant name `sparkImageUrl` picked for a URL, so the fallback asks for the same size; undefined for the original. */
export function variantNameOf(variants: Record<string, { url?: string }> | undefined, url: string | null): string | undefined {
  if (!variants || !url) return undefined;
  return Object.entries(variants).find(([, v]) => v?.url === url)?.[0];
}
