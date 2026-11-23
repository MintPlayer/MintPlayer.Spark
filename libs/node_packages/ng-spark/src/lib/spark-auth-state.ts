import { InjectionToken, Signal } from '@angular/core';

/**
 * A signal that changes whenever the authenticated user changes — the bridge that lets ng-spark
 * components react to sign-in/out without importing `@mintplayer/ng-spark/auth/*` (the non-auth
 * entry points must not import the auth ones, on purpose, so an app without auth pulls no auth code).
 *
 * `@mintplayer/ng-spark/auth`'s `withAuth()` supplies it from `SparkAuthService.user`;
 * an app with its own auth stack provides any signal that changes on sign-in/out. Consumers
 * inject it `{ optional: true }` — absent, auth-sensitive data (the program-units menu) is
 * fetched once and never re-fetched.
 *
 * The signal's VALUE is mostly opaque (`unknown`): consumers track it for change, and when they must
 * know whether someone is signed in they ask {@link sparkSignedIn}, never the shape itself.
 */
export const SPARK_AUTH_STATE = new InjectionToken<Signal<unknown>>('SPARK_AUTH_STATE');

/**
 * Whether an auth-state value means "signed in": a falsy value, or an object with `isAuthenticated: false`
 * (what `ng-spark/auth` holds for an anonymous visitor), is signed out; anything else is signed in.
 */
export function sparkSignedIn(state: unknown): boolean {
  if (!state) return false;
  return typeof state !== 'object' || (state as { isAuthenticated?: unknown }).isAuthenticated !== false;
}
