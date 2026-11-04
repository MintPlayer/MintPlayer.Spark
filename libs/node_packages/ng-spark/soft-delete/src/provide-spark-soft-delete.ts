import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { withDetailActions, withQueryListActions } from '@mintplayer/ng-spark/panels';
import { SparkDeletedToggleComponent } from './spark-deleted-toggle.component';
import { SparkSoftDeleteActionsComponent } from './spark-soft-delete-actions.component';

/**
 * Adds the SoftDelete UI (#460) to the pages `sparkRoutes()` routes to:
 *
 * - the query page gets a **Deleted** toggle (holders of `ViewDeleted/T`) that lists the recycle bin
 *   (`?deleted=only`); its rows open with the same mode;
 * - the detail page of such a row gets **Restore** (`Restore/T`) and **Purge** (`Purge/T`).
 *
 * Needs the server package `MintPlayer.Spark.SoftDelete`: `provideSpark(withSoftDelete())`.
 */
export function withSoftDelete(): SparkFeature {
  return sparkFeature('softDelete', SparkFeatureOrder.modules, [
    ...withQueryListActions({ id: 'spark-soft-delete-toggle', component: SparkDeletedToggleComponent, priority: 55 }).providers,
    ...withDetailActions({ id: 'spark-soft-delete-actions', component: SparkSoftDeleteActionsComponent, priority: 4 }).providers,
  ]);
}
