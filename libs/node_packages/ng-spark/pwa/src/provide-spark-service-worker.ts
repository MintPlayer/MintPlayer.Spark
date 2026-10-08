import { EnvironmentProviders, inject, isDevMode, makeEnvironmentProviders, provideAppInitializer } from '@angular/core';
import { SwRegistrationOptions, provideServiceWorker } from '@angular/service-worker';

import { SparkServiceWorkerUpdates } from './spark-service-worker-updates';

export interface SparkServiceWorkerOptions {
  /** Whether the worker registers. Default: `!isDevMode()`, so only production builds register it. */
  enabled?: boolean;
  /** The worker script, relative to the base href. Default: `ngsw-worker.js`. */
  script?: string;
  /** When the worker registers. Default: `registerWhenStable:30000`. */
  registrationStrategy?: SwRegistrationOptions['registrationStrategy'];
}

/**
 * Registers Angular's service worker with Spark's update policy (#464, Q1b); see
 * {@link SparkServiceWorkerUpdates} for what happens when a new version is deployed.
 *
 * The application also needs an `ngsw-config.json` with Spark's navigation exclusions and
 * `serviceWorker` in its production build configuration; see `docs/guide-pwa.md`.
 */
export function provideSparkServiceWorker(options: SparkServiceWorkerOptions = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideServiceWorker(options.script ?? 'ngsw-worker.js', {
      enabled: options.enabled ?? !isDevMode(),
      registrationStrategy: options.registrationStrategy ?? 'registerWhenStable:30000',
    }),
    provideAppInitializer(() => inject(SparkServiceWorkerUpdates).start()),
  ]);
}
