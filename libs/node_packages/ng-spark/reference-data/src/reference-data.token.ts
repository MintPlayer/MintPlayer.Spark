import { InjectionToken } from '@angular/core';

/** Where the reference-data instance lives (PRD §4.15). `baseUrl: null` is the explicit free-text mode (RD-DEF 8). */
export interface ReferenceDataConfig {
  /** The instance's origin, e.g. `https://refdata.example.org`; `null` = free-text address entry. */
  baseUrl: string | null;
}

/**
 * The reference-data configuration. **No default**: an app that uses the address form without
 * {@link provideReferenceData} fails loudly (NG0201) instead of silently talking to nobody.
 */
export const REFERENCE_DATA_CONFIG = new InjectionToken<ReferenceDataConfig>('REFERENCE_DATA_CONFIG');

/** Absolute http(s) only, without a trailing slash; anything else is free-text mode. */
export function normalizeBaseUrl(baseUrl: string | null | undefined): string | null {
  if (!baseUrl) return null;
  try {
    const url = new URL(baseUrl);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
    return url.toString().replace(/\/+$/, '');
  } catch {
    return null;
  }
}
