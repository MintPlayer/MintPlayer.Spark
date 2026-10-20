import { inject, InjectionToken } from '@angular/core';
import { resolvePaths, SparkStorefrontPaths } from './storefront-paths';

/** What the pages read: the resolved paths and which features are on. */
export interface SparkStorefrontConfig {
  paths: SparkStorefrontPaths;
  shop: boolean;
  ticketing: boolean;
  waitingRoom: boolean;
  /** Where the account page sends an anonymous visitor to sign in (the host's own sign-in page); null hides the link. */
  signInUrl: string | null;
}

/** Provided on the storefront's routes (and by `provideSparkCommerce`); the default is core pages only. */
export const SPARK_STOREFRONT = new InjectionToken<SparkStorefrontConfig>('SPARK_STOREFRONT', {
  providedIn: 'root',
  factory: () => ({ paths: resolvePaths(undefined), shop: false, ticketing: false, waitingRoom: false, signInUrl: null }),
});

/** The storefront's configuration, for pages and host components. */
export function injectStorefront(): SparkStorefrontConfig {
  return inject(SPARK_STOREFRONT);
}
