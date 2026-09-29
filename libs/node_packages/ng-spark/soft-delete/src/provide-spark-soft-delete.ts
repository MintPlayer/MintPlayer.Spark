import { Provider } from '@angular/core';
import { provideSparkDetailActions, provideSparkQueryListActions } from '@mintplayer/ng-spark/panels';
import { SparkDeletedToggleComponent } from './spark-deleted-toggle.component';
import { SparkSoftDeleteActionsComponent } from './spark-soft-delete-actions.component';

/**
 * Adds the SoftDelete UI (#460) to the pages `sparkRoutes()` routes to:
 *
 * - the query page gets a **Deleted** toggle (holders of `ViewDeleted/T`) that lists the recycle bin
 *   (`?deleted=only`); its rows open with the same mode;
 * - the detail page of such a row gets **Restore** (`Restore/T`) and **Purge** (`Purge/T`).
 *
 * Needs the server package `MintPlayer.Spark.SoftDelete`. Add to the app's providers:
 * `providers: [provideSpark(...), provideSparkSoftDelete()]`.
 */
export function provideSparkSoftDelete(): Provider[] {
  return [
    ...provideSparkQueryListActions({ id: 'spark-soft-delete-toggle', component: SparkDeletedToggleComponent, priority: 55 }),
    ...provideSparkDetailActions({ id: 'spark-soft-delete-actions', component: SparkSoftDeleteActionsComponent, priority: 4 }),
  ];
}
