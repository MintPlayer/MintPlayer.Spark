import { PersistentObject } from './persistent-object';

export interface StreamingSnapshotMessage {
  type: 'snapshot';
  data: PersistentObject[];
}

export interface StreamingPatchItem {
  id: string;
  attributes: Record<string, any>;
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
