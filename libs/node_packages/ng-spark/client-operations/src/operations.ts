// Wire types matching MintPlayer.Spark.Abstractions.ClientOperations on the server.
// Discriminator is the `type` field. Unknown operation types are silently dropped
// by the dispatcher (forward-compat: new types can land server-side without updating
// older clients).

import type { PersistentObject } from '@mintplayer/ng-spark/models';

export enum NotificationKind {
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}

export interface NavigateOperation {
    type: 'navigate';
    objectTypeId?: string;
    id?: string;
    routeName?: string;
}

export interface NotifyOperation {
    type: 'notify';
    message: string;
    kind: NotificationKind;
    durationMs?: number;
}

export interface RefreshAttributeOperation {
    type: 'refreshAttribute';
    objectTypeId: string;
    id: string;
    attributeName: string;
    value?: unknown;

    /**
     * Nested rows of an `AsDetail` attribute. A loaded AsDetail attribute carries its rows in
     * `object` / `objects` and leaves `value` null, so a patch limited to `value` could never
     * refresh a detail grid -- it always sent null, and a null over a null repaints nothing.
     *
     * An empty `objects` array means the grid is now empty, and is distinct from omitting it.
     */
    object?: unknown;
    objects?: unknown[];
}

export interface RefreshQueryOperation {
    type: 'refreshQuery';
    queryId: string;
}

// `disableAction` is gone (#460, D13): the server no longer emits it. Disabled actions arrive on
// `PersistentObject.disabledActions` / `QueryResult.disabledActions`, and a submitted disabled action
// is refused with 403.

export interface RetryOperation {
    type: 'retry';
    step: number;
    title: string;
    options: string[];
    defaultOption?: string | null;
    persistentObject?: PersistentObject | null;
    message?: string | null;
}

/**
 * Discriminated union of known operation types, plus an open shape for unknown
 * future operations. Handlers should narrow via the `type` discriminator before
 * accessing fields specific to their operation type.
 */
export type ClientOperation =
    | NavigateOperation
    | NotifyOperation
    | RefreshAttributeOperation
    | RefreshQueryOperation
    | RetryOperation
    | { type: string; [key: string]: unknown };

/**
 * Wire envelope returned by every action endpoint. `result` carries the primary
 * payload (the PersistentObject for a Create, the QueryResult for an Execute,
 * etc.); `operations` carries the side-effects the frontend dispatches.
 */
export interface ClientOperationEnvelope<T = unknown> {
    result: T | null;
    operations: ClientOperation[];
}
