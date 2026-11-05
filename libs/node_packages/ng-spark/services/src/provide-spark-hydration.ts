import { InjectionToken } from '@angular/core';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { provideClientHydration, withEventReplay, withHttpTransferCacheOptions } from '@angular/platform-browser';

/**
 * The language a server render resolved for its request (`spark-lang` cookie, then Accept-Language,
 * then the app default). Provided by `@mintplayer/ng-spark/ssr` on the server only; in the browser the
 * language comes from localStorage and the cookie as before.
 */
export const SPARK_SERVER_LANGUAGE = new InjectionToken<string>('SPARK_SERVER_LANGUAGE');

/**
 * The page's public origin (`https://mintplayer.com`), as the .NET host saw the request. Provided by
 * `@mintplayer/ng-spark/ssr` on the server only, where the render URL is relative (PRD I4) and
 * `document.location` cannot say it; in the browser, `document.location.origin` is the answer.
 */
export const SPARK_SERVER_ORIGIN = new InjectionToken<string>('SPARK_SERVER_ORIGIN');

/**
 * The server render's own answer about the page, read back by `createSparkServerRenderer` once the page is stable.
 * `status` is a status the page decided itself, such as the 404 of a URL no route matches; it replaces the one the
 * .NET side set from the page's reads. Provided by `@mintplayer/ng-spark/ssr` on the server only.
 */
export interface SparkServerResponse {
  status?: number;
}

export const SPARK_SERVER_RESPONSE = new InjectionToken<SparkServerResponse>('SPARK_SERVER_RESPONSE');

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
export function withHydration(): SparkFeature {
  return sparkFeature('hydration', SparkFeatureOrder.hydration, [provideClientHydration(
    withEventReplay(),
    withHttpTransferCacheOptions({ includePostRequests: true }),
  )]);
}
