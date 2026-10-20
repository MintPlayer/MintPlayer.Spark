import { PersistentObject } from '@mintplayer/ng-spark/models';

/** An approval request's status (`MintPlayer.Spark.Approvals.ApprovalStatuses`). */
export type SparkApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn' | 'Expired' | 'Stale' | 'Revoked';

/** Every status, in lifecycle order: what the status renderer knows a colour and a label for. */
export const SPARK_APPROVAL_STATUSES: readonly SparkApprovalStatus[] = [
  'Pending', 'Approved', 'Rejected', 'Withdrawn', 'Expired', 'Stale', 'Revoked',
];

/** The entity type name of an approval request, and its route alias. */
export const SPARK_APPROVAL_REQUEST_TYPE = 'ApprovalRequest';
export const SPARK_APPROVAL_REQUEST_ALIAS = 'approval-request';

/** The `PendingApprovals` query (alias `pending-approvals`): the caller's inbox; `totalItems` is the badge count. */
export const SPARK_PENDING_APPROVALS_QUERY_ID = 'f359cfc6-e319-50b3-9763-264d92b7c541';
export const SPARK_PENDING_APPROVALS_QUERY_ALIAS = 'pending-approvals';

/** The private live topic the server publishes `{ requestId, status }` on, per recipient, when their inbox changes. */
export const SPARK_APPROVALS_INBOX_TOPIC = 'approvals:inbox';

/**
 * `PersistentObject.metadata.approval` on a governed row the caller proposed an edit of, or may decide:
 * the newest pending request (`DocumentProposals.OnAfterLoadAsync`). Anyone else is not told it exists.
 */
export interface SparkApprovalMetadata {
  requestId: string;
  status: SparkApprovalStatus | string;
  /** The caller proposed it. */
  own: boolean;
  /** The caller may approve or reject it. */
  mayDecide: boolean;
}

/** The approval facts of a loaded object, or null when it carries none. */
export function approvalMetadataOf(item: PersistentObject | null | undefined): SparkApprovalMetadata | null {
  const approval = item?.metadata?.['approval'] as Partial<SparkApprovalMetadata> | undefined;
  return approval && typeof approval.requestId === 'string' && approval.requestId
    ? approval as SparkApprovalMetadata
    : null;
}

/** What a decision endpoint answers (`ApprovalDecisionResponse`), for success (200) and refusal by rule (400/409/422) alike. */
export interface SparkApprovalDecisionResult {
  /** `done`, `reasonRequired`, `invalid`, `notPending`, `stale`, `cancelled`, `unwritable`. */
  outcome: string;
  status?: string | null;
  conflictReason?: string | null;
  attributes?: string[];
  errors?: { attributeName?: string; errorMessage?: unknown }[];
  object?: PersistentObject | null;
}

/** `GET /spark/approvals/proposal?id=…`: one document proposal and its target, for the diff. */
export interface SparkApprovalProposal {
  requestId: string;
  status: SparkApprovalStatus | string;
  operation: 'Create' | 'Modify';
  /** The governed entity type's id. */
  objectTypeId: string;
  /** The row the proposal edits; null for a creation. */
  targetId: string | null;
  /** The row changed since the proposal was made: approving it will answer 409. */
  isStale: boolean;
  /** The proposed object, as the proposer saved it. */
  proposed: PersistentObject;
  /** The row as it is now, loaded as the caller; null for a creation or a row the caller may not read. */
  current: PersistentObject | null;
}
