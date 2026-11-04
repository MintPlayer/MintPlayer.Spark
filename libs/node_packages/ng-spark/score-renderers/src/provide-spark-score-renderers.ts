import { SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SparkAttributeRendererRegistration, withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { SparkGaugeComponent } from './spark-gauge.component';
import { SparkThresholdBarComponent } from './spark-threshold-bar.component';
import { SparkTrendComponent } from './spark-trend.component';
import { SparkDeltaComponent } from './spark-delta.component';

/**
 * The score renderers under their generic names, each drawing both a grid cell and a detail value.
 * Configured per attribute through the model's `rendererOptions`.
 */
export const sparkScoreRenderers: readonly SparkAttributeRendererRegistration[] = [
  { name: 'spark-gauge', columnComponent: SparkGaugeComponent, detailComponent: SparkGaugeComponent },
  { name: 'spark-threshold-bar', columnComponent: SparkThresholdBarComponent, detailComponent: SparkThresholdBarComponent },
  { name: 'spark-trend', columnComponent: SparkTrendComponent, detailComponent: SparkTrendComponent },
  { name: 'spark-delta', columnComponent: SparkDeltaComponent, detailComponent: SparkDeltaComponent },
];

/**
 * Registers the score renderers (`spark-gauge`, `spark-threshold-bar`, `spark-trend`, `spark-delta`), for
 * `provideSpark(...)`, together with `renderers` if given. They come after every `withAttributeRenderers(...)`
 * listing, so an app renderer of the same name replaces a score renderer.
 *
 * @example
 * withScoreRenderers([
 *   { name: 'coverage-delta', columnComponent: SparkDeltaComponent, detailComponent: SparkDeltaComponent },
 * ])
 */
export function withScoreRenderers(renderers: readonly SparkAttributeRendererRegistration[] = []): SparkFeature {
  return withAttributeRenderers([...renderers, ...sparkScoreRenderers], SparkFeatureOrder.builtInRegistrations);
}
