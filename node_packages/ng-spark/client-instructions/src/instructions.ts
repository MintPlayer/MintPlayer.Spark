// Wire types matching MintPlayer.Spark.Abstractions.ClientInstructions on the server.
// Discriminator is the `type` field. Unknown instruction types are silently dropped
// by the dispatcher (forward-compat: new types can land server-side without updating
// older clients).

import type { PersistentObject } from '@mintplayer/ng-spark/models';

export enum NotificationKind {
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}

export interface NavigateInstruction {
    type: 'navigate';
    objectTypeId?: string;
    id?: string;
    routeName?: string;
}

export interface NotifyInstruction {
    type: 'notify';
    message: string;
    kind: NotificationKind;
    durationMs?: number;
}

export interface RefreshAttributeInstruction {
    type: 'refreshAttribute';
    objectTypeId: string;
    id: string;
    attributeName: string;
    value?: unknown;
}

export interface RefreshQueryInstruction {
    type: 'refreshQuery';
    queryId: string;
}

export type DisableTarget =
    | { kind: 'persistentObject'; objectTypeId: string; id: string }
    | { kind: 'query'; queryId: string }
    | { kind: 'currentResponse' }
    | { kind: 'session' };

export interface DisableActionInstruction {
    type: 'disableAction';
    actionName: string;
    target: DisableTarget;
}

export interface RetryInstruction {
    type: 'retry';
    step: number;
    title: string;
    options: string[];
    defaultOption?: string | null;
    persistentObject?: PersistentObject | null;
    message?: string | null;
}

/**
 * Discriminated union of known instruction types, plus an open shape for unknown
 * future instructions. Handlers should narrow via the `type` discriminator before
 * accessing fields specific to their instruction type.
 */
export type ClientInstruction =
    | NavigateInstruction
    | NotifyInstruction
    | RefreshAttributeInstruction
    | RefreshQueryInstruction
    | DisableActionInstruction
    | RetryInstruction
    | { type: string; [key: string]: unknown };

/**
 * Wire envelope returned by every action endpoint. `result` carries the primary
 * payload (the PersistentObject for a Create, the QueryResult for an Execute,
 * etc.); `instructions` carries the side-effects the frontend dispatches.
 */
export interface ClientInstructionEnvelope<T = unknown> {
    result: T | null;
    instructions: ClientInstruction[];
}
