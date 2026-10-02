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
  /**
   * Passkey management, mounted by `withPasskeys()`.
   *
   * Unlike its siblings this page is for a user who is already signed in, so it belongs behind
   * whatever guard the application uses for its account area rather than on the public sign-in path.
   */
  passkeys?: SparkAuthRouteEntry;
  /**
   * The link target of confirmation mails, mounted by `withAccount()`. Public (the link is opened from a
   * mailbox, possibly signed out); its path must equal the server's `Spark:Auth:Links:ConfirmEmailPath`
   * (default `/confirm-email`).
   */
  confirmEmail?: SparkAuthRouteEntry;
  /** The account overview (links to every mounted account page), mounted by `withAccount()`. */
  account?: SparkAuthRouteEntry;
  /** User name, email change, preferred mail language and app fields (`withAccount()`). */
  profile?: SparkAuthRouteEntry;
  /** Change the password, or set a first one on a social-only account (`withAccount()`). */
  changePassword?: SparkAuthRouteEntry;
  /** Authenticator enrollment, recovery codes, disabling 2FA (`withAccount()`). */
  twoFactorSetup?: SparkAuthRouteEntry;
  /** Connected external logins: link and unlink providers (`withAccount()`). */
  externalLogins?: SparkAuthRouteEntry;
  /** Download personal data and delete the account (`withAccount()`). */
  personalData?: SparkAuthRouteEntry;
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

/** The slice of an Angular `Route` the lookup below reads — kept structural so `/models` stays router-free. */
interface RouteLike {
  providers?: readonly unknown[];
  children?: readonly RouteLike[];
}

/**
 * The paths `sparkAuthRoutes()` mounted, read from the router configuration.
 *
 * For components that live **outside** that route subtree, such as `<spark-auth-bar>` in the
 * application shell: `SPARK_AUTH_ROUTE_PATHS` is provided on the subtree's own route, so it is not
 * injectable from there. Walks the eagerly declared routes only — a `sparkAuthRoutes()` behind a
 * `loadChildren` is not loaded yet and is not found, which reads as "nothing mounted".
 */
export function findSparkAuthRoutePaths(routes: readonly RouteLike[] | undefined): SparkAuthRoutePaths | null {
  for (const route of routes ?? []) {
    for (const provider of route.providers ?? []) {
      const p = provider as { provide?: unknown; useValue?: SparkAuthRoutePaths };
      if (p?.provide === SPARK_AUTH_ROUTE_PATHS && p.useValue) return p.useValue;
    }
    const nested = findSparkAuthRoutePaths(route.children);
    if (nested) return nested;
  }
  return null;
}

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
