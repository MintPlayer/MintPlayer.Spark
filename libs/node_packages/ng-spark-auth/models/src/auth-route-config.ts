import { InjectionToken, Type } from '@angular/core';

export type SparkAuthRouteEntry = string | { path: string; component?: Type<unknown> };

/**
 * How much of the local-credential (email + password) page family to route.
 *
 * Mirrors the server's `SparkLocalCredentials`, and for the same reason it is a mode rather
 * than five independent switches: the pages form a star centred on the login page, and every
 * template dereferences its siblings' paths unconditionally, so removing any proper subset
 * leaves a dangling link. The family is the unit on both tiers.
 */
export type SparkLocalCredentialsMode = 'full' | 'sign-in-only' | 'disabled';

/** The routable local-credential pages. Kept separate from {@link SparkAuthRouteConfig} so that
 * adding non-route options below cannot widen {@link SparkAuthRoutePaths}. */
export interface SparkAuthRouteEntries {
  login?: SparkAuthRouteEntry;
  twoFactor?: SparkAuthRouteEntry;
  register?: SparkAuthRouteEntry;
  forgotPassword?: SparkAuthRouteEntry;
  resetPassword?: SparkAuthRouteEntry;
}

export interface SparkAuthRouteConfig extends SparkAuthRouteEntries {
  /**
   * Defaults to `'full'`, which routes every page — the behaviour of every version before this
   * option existed. Set it to match the server's `LocalCredentials` mode; `GET /spark/auth/capabilities`
   * reports what the server is actually running.
   */
  localCredentials?: SparkLocalCredentialsMode;
}

/**
 * Every path is present regardless of mode. The pages that are dropped are dropped together, so no
 * surviving template can dereference a missing sibling — which is what lets this stay `Required`
 * and keeps the change additive for consumers.
 */
export type SparkAuthRoutePaths = Required<Record<keyof SparkAuthRouteEntries, string>>;

export const SPARK_AUTH_ROUTE_PATHS = new InjectionToken<SparkAuthRoutePaths>('SPARK_AUTH_ROUTE_PATHS');
