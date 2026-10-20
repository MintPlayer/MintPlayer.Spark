import { describe, expect, it } from 'vitest';

import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { replicationMetadataOf } from './spark-replication.models';
import { sparkReplicationPendingBadge } from './provide-spark-replication';

// Warehouse §4.2.5 (WM1): the ng-spark client of MintPlayer.Spark.Replication's write-back.

function item(metadata?: Record<string, unknown>): PersistentObject {
  return { id: 'Cars/1', name: 'Car', objectTypeId: 't-car', attributes: [], metadata } as unknown as PersistentObject;
}

function context(po: PersistentObject): SparkDetailContext {
  return { type: 'car', id: 'Cars/1', item: po } as unknown as SparkDetailContext;
}

describe('replicationMetadataOf', () => {
  it('reads a pending change', () => {
    expect(replicationMetadataOf(item({ replication: { pending: true, owner: 'Fleet', syncActionId: 'SparkSyncActions/x', own: true } })))
      .toEqual({ pending: true, owner: 'Fleet', syncActionId: 'SparkSyncActions/x', own: true });
  });

  it('ignores a row without one', () => {
    expect(replicationMetadataOf(item())).toBeNull();
    expect(replicationMetadataOf(item({ replication: { pending: false, owner: 'Fleet' } }))).toBeNull();
  });
});

describe('sparkReplicationPendingBadge', () => {
  it('shows on a pending row and links the caller to their pending changes', () => {
    const view = sparkReplicationPendingBadge.badge(context(item({ replication: { pending: true, owner: 'Fleet', syncActionId: 'SparkSyncActions/x', own: true } })));
    expect(view?.labelKey).toBe('replication.pendingBadge');
    expect(view?.link).toEqual(['/query', 'my-pending-changes']);
  });

  it('does not link someone else\'s change', () => {
    const view = sparkReplicationPendingBadge.badge(context(item({ replication: { pending: true, owner: 'Fleet', syncActionId: null, own: false } })));
    expect(view?.link).toBeUndefined();
  });

  it('is absent on a row with nothing pending', () => {
    expect(sparkReplicationPendingBadge.badge(context(item()))).toBeNull();
  });
});
