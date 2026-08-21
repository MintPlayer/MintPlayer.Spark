import { InjectionToken, Type } from '@angular/core';

export type SparkAuthRouteEntry = string | { path: string; component?: Type<unknown> };

/** The routable authentication pages. */
export interface SparkAuthRouteEntries {
  /** The provider-button landing page, mounted by `withExternalLogin()`. */
  signIn?: SparkAuthRouteEntry;
  login?: SparkAuthRouteEntry;
  twoFactor?: SparkAuthRouteEntry;
  register?: SparkAuthRouteEntry;
  forgotPassword?: SparkAuthRouteEntry;
  resetPassword?: SparkAuthRouteEntry;
}

/**
 * The paths of the pages that were actually mounted.
 *
 * Partial, and that is the change: pages are opted into individually now, so a template cannot
 * assume its siblings exist. `login` links to `register` only when registration was opted into, and
 * the sign-in landing page links to `login` only when local login was. A `[routerLink]` bound to
 * `undefined` silently navigates to the current route, so every cross-feature link is guarded.
 */
export type SparkAuthRoutePaths = Partial<Record<keyof SparkAuthRouteEntries, string>>;

export const SPARK_AUTH_ROUTE_PATHS = new InjectionToken<SparkAuthRoutePaths>('SPARK_AUTH_ROUTE_PATHS');

/**
 * Presentation for one external provider's button — an icon, a label, an ordering.
 *
 * **It decorates; it does not declare.** The server stays authoritative over which providers exist
 * (`GET /spark/auth/capabilities`), because letting the client declare them would reintroduce exactly
 * the string-literal mismatch that endpoint exists to prevent. A scheme the server reports with no
 * matching declaration falls back to the default button, so adding a provider server-side never
 * yields a blank page; a declaration matching no reported scheme is simply unused.
 */
export interface SparkExternalProviderPresentation {
  /** The ASP.NET Core authentication scheme, as the server reports it. Matched case-insensitively. */
  scheme: string;
  /** Overrides the server's display name. */
  displayName?: string;
  /** A CSS class for an icon element rendered before the label. */
  iconClass?: string;
  /** Lower sorts first. Providers with no declared order keep the server's order, after declared ones. */
  order?: number;
}

export const SPARK_EXTERNAL_PROVIDERS = new InjectionToken<SparkExternalProviderPresentation[]>(
  'SPARK_EXTERNAL_PROVIDERS',
);
