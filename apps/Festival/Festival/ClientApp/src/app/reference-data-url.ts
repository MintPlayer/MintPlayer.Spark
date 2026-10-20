/**
 * The reference-data instance the address form talks to (reference-data plan R12: "the commerce demo points at a
 * host-configured endpoint; null = free text").
 *
 * In local development (the app on its launch-profile port 5015) that is `apps/ReferenceData` on https 5017, which
 * holds the basic seed set (BE, NL, FR, LU, DE; commerce D39). Everywhere else, the E2E host included (random ports,
 * no reference-data instance), the form is free text, which the address input always allows.
 */
export const FESTIVAL_DEV_ORIGIN_PORT = '5015';
export const REFERENCE_DATA_DEV_URL = 'https://localhost:5017';

export function referenceDataBaseUrl(location: Pick<Location, 'hostname' | 'port'>): string | null {
  return location.hostname === 'localhost' && location.port === FESTIVAL_DEV_ORIGIN_PORT ? REFERENCE_DATA_DEV_URL : null;
}
