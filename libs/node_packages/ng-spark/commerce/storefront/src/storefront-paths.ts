/**
 * The storefront's URL segments and the pure helpers built on them (commerce #521 M17). The defaults match the
 * server's page-path options, so the links the server puts in mails, sitemaps and meta tags land on these routes:
 *
 * | Segment | Server option (default) |
 * |---|---|
 * | `shop/products/{slug}` | `Spark:Commerce:Shop:ProductPagePath` (`/shop/products/{slug}`) |
 * | `shop/categories/{slug}` | `Spark:Commerce:Shop:CategoryPagePath` (`/shop/categories/{slug}`) |
 * | `events/{slug}` | `Spark:Ticketing:Events:EventPagePath` (`/events/{slug}`) |
 * | `tickets/{code}`, `tickets/transfer/{id}` | Tickets' `Delivery:TicketPagePath`, `Delivery:TransferPagePath` |
 * | `checkout/orders/{number}` | Checkout's `OrderPagePath`, `Refunds:GuestOrderPagePath` (`?access=`) |
 * | `shop/item?id={itemId}` | Promotions' `ItemPagePath` (`/shop/item?id={itemId}`): redirects to the product page |
 * | `promotions/back-in-stock/confirm`, `/unsubscribe` | Promotions' `ConfirmPagePath`, `UnsubscribePagePath` |
 * | `consent/settings`, `consent/unsubscribe` | `Spark:Consent:UnsubscribePagePath` (`/consent/unsubscribe`) |
 *
 * A host that changes a server path changes the segment here too.
 */
export interface SparkStorefrontPaths {
  shop: string;
  events: string;
  queue: string;
  tickets: string;
  checkout: string;
  account: string;
  legal: string;
  consent: string;
  /** Promotions' mail pages: `promotions/back-in-stock/confirm` and `/unsubscribe` (`ConfirmPagePath`, `UnsubscribePagePath`). */
  promotions: string;
  /** `commerce/core`'s pages: trader pages (`/commerce/traders/{id}`, the trader label's default link), ranking, seller documents. */
  commerce: string;
}

export const SPARK_STOREFRONT_DEFAULT_PATHS: Readonly<SparkStorefrontPaths> = {
  shop: 'shop',
  events: 'events',
  queue: 'queue',
  tickets: 'tickets',
  checkout: 'checkout',
  account: 'account',
  legal: 'legal',
  consent: 'consent',
  commerce: 'commerce',
  promotions: 'promotions',
};

/** The paths with the host's overrides; leading and trailing slashes are dropped. */
export function resolvePaths(overrides: Partial<SparkStorefrontPaths> | undefined): SparkStorefrontPaths {
  const merged = { ...SPARK_STOREFRONT_DEFAULT_PATHS, ...(overrides ?? {}) };
  return Object.fromEntries(Object.entries(merged).map(([k, v]) => [k, String(v).replace(/^\/+|\/+$/g, '')])) as unknown as SparkStorefrontPaths;
}

/** An absolute in-app URL from a segment and further parts, each part escaped. */
export function storefrontUrl(segment: string, ...parts: string[]): string {
  const tail = parts.filter((p) => p !== '').map((p) => encodeURIComponent(p)).join('/');
  return '/' + [segment, tail].filter((p) => p !== '').join('/');
}

/** Whether `url` points into this app (a relative path, or the same origin), so it may be followed with the router. */
export function isInAppUrl(url: string | null | undefined, origin: string): boolean {
  if (!url) return false;
  if (url.startsWith('/') && !url.startsWith('//')) return true;
  try {
    return new URL(url).origin === origin;
  } catch {
    return false;
  }
}

/** The path, query and fragment of an in-app URL (for `router.navigateByUrl`). */
export function inAppPath(url: string, origin: string): string {
  if (url.startsWith('/') && !url.startsWith('//')) return url;
  const parsed = new URL(url, origin);
  return parsed.pathname + parsed.search + parsed.hash;
}

/**
 * The automatic waiting room of a sale phase, as the server keys it (`CommerceWaitingRoomScopes.RoomKey`):
 * `ticketing-`, the phase id folded to the key alphabet, and the first 8 hex digits of the id's SHA-256. Keep the two in
 * step; the unit test pins known values.
 */
export async function phaseRoomKey(phaseId: string): Promise<string> {
  let folded = '';
  for (const c of phaseId.toLowerCase()) {
    const kept = /[a-z0-9._]/.test(c) ? c : '-';
    if (kept !== '-' || (folded.length > 0 && !folded.endsWith('-'))) folded += kept;
  }
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(phaseId));
  const hash = Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 8);
  let body = folded.replace(/^[-._]+|[-._]+$/g, '');
  if (body.length > 80) body = body.slice(0, 80).replace(/[-._]+$/g, '');
  return body.length === 0 ? `ticketing-${hash}` : `ticketing-${body}-${hash}`;
}

/** The legal-document kinds the storefront's legal routes show (`LegalDocumentKinds` on the server). */
export const SPARK_LEGAL_KINDS = {
  terms: 'terms-of-sale',
  privacy: 'privacy-policy',
  withdrawal: 'withdrawal-information',
  accessibility: 'accessibility-statement',
  cookies: 'cookie-policy',
} as const;
