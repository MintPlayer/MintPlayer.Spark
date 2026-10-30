import { EnvironmentProviders, InjectionToken } from '@angular/core';
import { provideClientHydration, withEventReplay, withHttpTransferCacheOptions } from '@angular/platform-browser';

/**
 * The language a server render resolved for its request (`spark-lang` cookie, then Accept-Language,
 * then the app default). Provided by `@mintplayer/ng-spark/ssr` on the server only; in the browser the
 * language comes from localStorage and the cookie as before.
 */
export const SPARK_SERVER_LANGUAGE = new InjectionToken<string>('SPARK_SERVER_LANGUAGE');

/**
 * Hydrates a server-rendered page instead of rendering it again, and lets the browser reuse every
 * response the server render was answered with (TransferState), so none is fetched twice.
 *
 * `includePostRequests` because Spark's reads are POSTs (`/spark/po/load`, `/spark/queries/execute`);
 * they are safe to replay — they are the same reads, for the same user, a moment earlier. The cache
 * answers only until the app is first stable, so later navigations always fetch fresh.
 *
 * Harmless on a page that was not server-rendered (the service worker's shell): Angular finds no
 * serialized state and renders on the client as before.
 */
export function provideSparkHydration(): EnvironmentProviders {
  return provideClientHydration(
    withEventReplay(),
    withHttpTransferCacheOptions({ includePostRequests: true }),
  ) as unknown as EnvironmentProviders;
}
