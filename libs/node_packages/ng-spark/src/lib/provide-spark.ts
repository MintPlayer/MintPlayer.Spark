import { EnvironmentProviders, makeEnvironmentProviders, Provider } from '@angular/core';
import { SPARK_CONFIG, SparkConfig, defaultSparkConfig } from './spark-config';

/**
 * Where a feature's providers go among the others (`docs/ng_spark_provide_spark_PRD.md` P3). `provideSpark` sorts by
 * it, so an app lists its features in any order. Ties keep the app's order.
 */
export const SparkFeatureOrder = {
  config: 0,
  hydration: 10,
  languageUrls: 15,
  serviceWorker: 20,
  auth: 100,
  clientOperations: 110,
  modules: 200,
  registrations: 500,
  /** Renderers that ship with ng-spark come after the app's, so an app renderer replaces one by name. */
  builtInRegistrations: 510,
  seo: 900,
} as const;

/**
 * One Spark feature for {@link provideSpark}: `withAuth()`, `withBoards()`, `withAttributeRenderers(...)`. Each comes
 * from its feature's own entry point, so an unused feature is never bundled.
 */
export interface SparkFeature {
  /** The feature's name; listed twice is an error unless {@link multi}. */
  readonly kind: string;
  /** See {@link SparkFeatureOrder}. */
  readonly order: number;
  readonly providers: readonly (Provider | EnvironmentProviders)[];
  /** A registration (renderers, panels, actions, ...): every listing adds. */
  readonly multi?: boolean;
  /** Features this one needs: `provideSpark` adds each one whose kind is not listed. */
  readonly requires?: readonly (() => SparkFeature)[];
}

/** Builds a {@link SparkFeature}. */
export function sparkFeature(
  kind: string,
  order: number,
  providers: readonly (Provider | EnvironmentProviders)[],
  options: { multi?: boolean; requires?: readonly (() => SparkFeature)[] } = {},
): SparkFeature {
  return { kind, order, providers, ...options };
}

/** Overrides {@link SparkConfig}; anything left out keeps its default. */
export function withConfig(config: Partial<SparkConfig>): SparkFeature {
  return sparkFeature('config', SparkFeatureOrder.config, [
    { provide: SPARK_CONFIG, useValue: { ...defaultSparkConfig, ...config } },
  ]);
}

/**
 * Spark's client features, in one call at the application level:
 *
 * ```ts
 * provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
 * provideSpark(withAuth(), withClientOperations(), withBoards(), withAttributeRenderers(appRenderers)),
 * ```
 *
 * The features are sorted by their own order, a feature another one needs is added, and a feature listed twice
 * throws (registrations such as renderers and panels excepted: every listing adds). Without `withConfig(...)` the
 * default configuration applies.
 */
export function provideSpark(...features: SparkFeature[]): EnvironmentProviders {
  const all = [...features];
  const kinds = new Map<string, SparkFeature>();
  for (const feature of features) {
    if (!feature.multi && kinds.has(feature.kind))
      throw new Error(`provideSpark: the Spark feature "${feature.kind}" is listed twice.`);
    kinds.set(feature.kind, feature);
  }
  // Required features, transitively; a listed one always wins over the default a requirement would add.
  for (let i = 0; i < all.length; i++) {
    for (const require of all[i].requires ?? []) {
      const required = require();
      if (!kinds.has(required.kind)) {
        kinds.set(required.kind, required);
        all.push(required);
      }
    }
  }
  if (!kinds.has('config'))
    all.push(withConfig({}));

  const sorted = all.map((feature, index) => ({ feature, index }))
    .sort((a, b) => a.feature.order - b.feature.order || a.index - b.index)
    .map(entry => entry.feature);
  return makeEnvironmentProviders(sorted.flatMap(feature => [...feature.providers]));
}
