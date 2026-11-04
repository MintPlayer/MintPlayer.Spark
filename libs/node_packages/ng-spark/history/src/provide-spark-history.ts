import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { withDetailPanels } from '@mintplayer/ng-spark/panels';
import { SparkHistoryPanelComponent } from './spark-history-panel.component';

/**
 * Adds a History card (`<spark-po-history>`: revision list, read-only view, diff, Revert) to every
 * detail page `sparkRoutes()` routes to, for holders of `History/T` (#460). Needs the server package
 * `MintPlayer.Spark.History` and revisions enabled for the type in its model.
 *
 * `provideSpark(withHistory())`
 */
export function withHistory(options?: { order?: number }): SparkFeature {
  return sparkFeature('history', SparkFeatureOrder.modules,
    withDetailPanels({ id: 'spark-history', component: SparkHistoryPanelComponent, order: options?.order ?? 100 }).providers);
}
