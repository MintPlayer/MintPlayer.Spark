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
}

export interface RefreshQueryOperation {
    type: 'refreshQuery';
    queryId: string;
}

export type DisableTarget =
    | { kind: 'persistentObject'; objectTypeId: string; id: string }
    | { kind: 'query'; queryId: string }
    | { kind: 'currentResponse' }
    | { kind: 'session' };

export interface DisableActionOperation {
    type: 'disableAction';
    actionName: string;
    target: DisableTarget;
}

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
    | DisableActionOperation
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
