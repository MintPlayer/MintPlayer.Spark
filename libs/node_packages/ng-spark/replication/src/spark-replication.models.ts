import { PersistentObject } from '@mintplayer/ng-spark/models';

/** The private live topic the server publishes a write-back's terminal outcome on, per initiator. */
export const SPARK_REPLICATION_OUTCOMES_TOPIC = 'replication:outcomes';

/** The `MyPendingChanges` query (alias `my-pending-changes`): the caller's waiting and failed write-backs. */
export const SPARK_MY_PENDING_CHANGES_QUERY_ID = '121f1d22-5929-523d-bb9a-f0535abe51a1';
export const SPARK_MY_PENDING_CHANGES_QUERY_ALIAS = 'my-pending-changes';

/** The `ReplicationRequests` query (alias `replication-requests`): the owner's approval gate. */
export const SPARK_REPLICATION_REQUESTS_QUERY_ALIAS = 'replication-requests';

/** The receipt kinds a save of a write-back replica answers with (`202 { submitted: { kind, referenceId } }`). */
export type SparkReplicationSubmittedKind = 'replication' | 'replication-pending';

/**
 * `PersistentObject.metadata.replication` on a replica row whose change waits at its owner
 * (`ReplicaWriteBackInterceptor.OnAfterLoadAsync`): queued, or applied and not yet delivered back.
 */
export interface SparkReplicationMetadata {
  pending: boolean;
  /** The owner module. */
  owner: string;
  /** The queued write-back (`SparkSyncActions/…`), or null when the owner applied it within the save. */
  syncActionId: string | null;
  /** The caller made the change. */
  own: boolean;
}

/** The replication facts of a loaded object, or null when it carries none. */
export function replicationMetadataOf(item: PersistentObject | null | undefined): SparkReplicationMetadata | null {
  const replication = item?.metadata?.['replication'] as Partial<SparkReplicationMetadata> | undefined;
  return replication && replication.pending === true && typeof replication.owner === 'string'
    ? { pending: true, owner: replication.owner, syncActionId: replication.syncActionId ?? null, own: replication.own === true }
    : null;
}

/** What `replication:outcomes` carries (`SyncOutcomeFrame`): a write-back that ended without being applied. */
export interface SparkReplicationOutcome {
  syncActionId: string;
  ownerModule: string;
  targetCollection: string;
  documentId: string;
  /** `Conflict`, `Refused` or `GaveUp`. */
  status: string;
  reason?: string | null;
  attributes: string[];
}
