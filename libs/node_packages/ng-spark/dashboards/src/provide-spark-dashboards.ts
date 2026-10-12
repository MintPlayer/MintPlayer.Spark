import { Provider } from '@angular/core';
import { provideSparkDetailLayouts } from '@mintplayer/ng-spark/panels';
import type { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { isSparkDashboard, SparkDashboardComponent } from './spark-dashboard.component';
import { SparkDashboardTableComponent } from './spark-dashboard-table.component';
import { SparkMeterComponent } from './spark-meter.component';
import { SparkStatComponent } from './spark-stat.component';

/**
 * The dashboard renderers under their model names. Renderers are one list, so add these to the app's own in its
 * single `provideSparkAttributeRenderers` (or `provideSparkScoreRenderers`, which `spark-trend` comes from) call:
 *
 * @example
 * provideSparkScoreRenderers([...appRenderers, ...sparkDashboardRenderers]),
 * provideSparkDashboards(),
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
export function provideSparkDashboards(): Provider[] {
  return provideSparkDetailLayouts({
    id: 'spark-dashboard',
    component: SparkDashboardComponent,
    matches: item => isSparkDashboard(item),
  });
}
