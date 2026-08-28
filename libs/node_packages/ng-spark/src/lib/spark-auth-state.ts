import { InjectionToken, Signal } from '@angular/core';

/**
 * A signal that changes whenever the authenticated user changes — the bridge that lets ng-spark
 * components react to sign-in/out without a dependency on `@mintplayer/ng-spark-auth` (no
 * dependency exists between the two packages, in either direction, on purpose).
 *
 * `@mintplayer/ng-spark-auth`'s `provideSparkAuth()` supplies it from `SparkAuthService.user`;
 * an app with its own auth stack provides any signal that changes on sign-in/out. Consumers
 * inject it `{ optional: true }` — absent, auth-sensitive data (the program-units menu) is
 * fetched once and never re-fetched.
 *
 * The signal's VALUE is deliberately opaque (`unknown`): consumers only track it for change,
 * never read it — what "the user" looks like belongs to the auth package.
 */
export const SPARK_AUTH_STATE = new InjectionToken<Signal<unknown>>('SPARK_AUTH_STATE');
