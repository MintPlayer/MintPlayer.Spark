import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { HttpBackend } from '@angular/common/http';
import { provideServerRendering } from '@angular/platform-server';
import { SPARK_SERVER_LANGUAGE, SPARK_SERVER_ORIGIN } from '@mintplayer/ng-spark/services';
import { SPARK_SERVER_DATA, SparkServerData } from './spark-server-data';
import { SparkServerBackend } from './spark-server-backend';

/**
 * The server half of an app's config (`app.config.server.ts`), merged over the browser config:
 *
 * ```ts
 * export const config = mergeApplicationConfig(appConfig, { providers: [provideSparkServerRendering()] });
 * ```
 *
 * It replaces the `HttpBackend` (not an interceptor; see {@link SparkServerBackend}), so it must come
 * after `provideHttpClient` — which `mergeApplicationConfig` guarantees by appending.
 */
export function provideSparkServerRendering(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideServerRendering(),
    { provide: HttpBackend, useClass: SparkServerBackend },
    {
      provide: SPARK_SERVER_LANGUAGE,
      useFactory: (data: SparkServerData | null) => data?.language ?? 'en',
      deps: [[SPARK_SERVER_DATA]],
    },
    {
      provide: SPARK_SERVER_ORIGIN,
      useFactory: (data: SparkServerData | null) => data?.origin ?? '',
      deps: [[SPARK_SERVER_DATA]],
    },
  ]);
}
