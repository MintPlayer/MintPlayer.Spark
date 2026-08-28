import { QueryColumn, QueryResultItem } from './query-result';

/**
 * The opening message of a stream: the column metadata plus the rows so far.
 *
 * Columns arrive once, here. A stream is one result whose rows arrive over time, so its shape is
 * fixed when it opens — patches carry values only.
 */
export interface StreamingSnapshotMessage {
  type: 'snapshot';
  columns: QueryColumn[];
  data: QueryResultItem[];
}

export interface StreamingPatchItem {
  id: string;
  /** Changed cell values, keyed by column name. Never metadata — the snapshot fixed that. */
  values: Record<string, any>;
}

export interface StreamingPatchMessage {
  type: 'patch';
  updated: StreamingPatchItem[];
}

export interface StreamingErrorMessage {
  type: 'error';
  message: string;
}

export type StreamingMessage = StreamingSnapshotMessage | StreamingPatchMessage | StreamingErrorMessage;
