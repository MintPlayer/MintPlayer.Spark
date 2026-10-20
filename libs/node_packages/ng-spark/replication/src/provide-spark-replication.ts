import { Provider } from '@angular/core';
import { SparkDetailTitleBadge, provideSparkDetailTitleBadges } from '@mintplayer/ng-spark/panels';
import { SPARK_MY_PENDING_CHANGES_QUERY_ALIAS, replicationMetadataOf } from './spark-replication.models';

/**
 * "Pending at the owner" after the title of a replica row whose change waits at the owning module
 * (`metadata.replication`): queued because the owner did not answer, or applied and not yet delivered back. The
 * caller's own pending change links to "My pending changes".
 */
export const sparkReplicationPendingBadge: SparkDetailTitleBadge = {
  id: 'spark-replication-pending',
  order: 20,
  badge: context => {
    const replication = replicationMetadataOf(context.item);
    return replication
      ? {
          labelKey: 'replication.pendingBadge',
          color: 'info',
          cssClass: 'spark-replication-pending-badge',
          link: replication.own && replication.syncActionId ? ['/query', SPARK_MY_PENDING_CHANGES_QUERY_ALIAS] : undefined,
        }
      : null;
  },
};

/**
 * Adds the Replication UI (warehouse §4.2.5) to the pages `sparkRoutes()` routes to: the pending badge on replica
 * rows. Put `<spark-pending-changes-link />` in the shell for the caller's waiting and failed write-backs. Needs the
 * server package `MintPlayer.Spark.Replication`.
 *
 * ```ts
 * providers: [provideSpark(...), provideSparkReplication()]
 * // shell: <spark-pending-changes-link linkClass="btn btn-sm btn-link" />
 * ```
 */
export function provideSparkReplication(): Provider[] {
  return [...provideSparkDetailTitleBadges(sparkReplicationPendingBadge)];
}
