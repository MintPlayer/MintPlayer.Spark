import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { withDetailLayouts } from '@mintplayer/ng-spark/panels';
import type { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { isSparkDashboard, SparkDashboardComponent } from './spark-dashboard.component';
import { SparkDashboardTableComponent } from './spark-dashboard-table.component';
import { SparkMeterComponent } from './spark-meter.component';
import { SparkStatComponent } from './spark-stat.component';

/**
 * The dashboard renderers under their model names, registered with the app's own (`spark-trend` comes from
 * `withScoreRenderers`):
 *
 * @example
 * provideSpark(withScoreRenderers([...appRenderers, ...sparkDashboardRenderers]), withDashboards())
 */
export const sparkDashboardRenderers: readonly SparkAttributeRendererRegistration[] = [
  { name: 'spark-stat', columnComponent: SparkStatComponent, detailComponent: SparkStatComponent },
  { name: 'spark-meter', columnComponent: SparkMeterComponent, detailComponent: SparkMeterComponent },
  { name: 'spark-dashboard-table', detailComponent: SparkDashboardTableComponent },
];

/**
 * Draws every dashboard (an object the server marks with `metadata.dashboard`, the dashboards package's live virtual
 * types) with the tile layout instead of the attribute cards, on the routed detail page. The renderers are registered
 * separately: see {@link sparkDashboardRenderers}.
 */
export function withDashboards(): SparkFeature {
  return sparkFeature('dashboards', SparkFeatureOrder.modules, withDetailLayouts({
    id: 'spark-dashboard',
    component: SparkDashboardComponent,
    matches: item => isSparkDashboard(item),
  }).providers);
}
