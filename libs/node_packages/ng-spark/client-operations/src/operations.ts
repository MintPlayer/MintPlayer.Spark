// Wire types matching MintPlayer.Spark.Abstractions.ClientOperations on the server.
// Discriminator is the `type` field. Unknown operation types are silently dropped
// by the dispatcher (forward-compat: new types can land server-side without updating
// older clients).

import type { PersistentObject, TranslatedString } from '@mintplayer/ng-spark/models';

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
    /** The text in the request's language (the browser's `Accept-Language`). */
    message: string;
    /**
     * The text in every language, when the server has it. Shown in the language the user picked in
     * the app (`SparkLanguageService`), which the server never learns; `message` is the fallback.
     */
    translatedMessage?: TranslatedString | null;
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
    /** Whether the client offers its own translated Cancel, answered as `'Cancel'` (`IRetryAccessor.Action(..., cancellable)`). */
    cancellable?: boolean;
    /** A client method to run instead of a modal (`IRetryAccessor.Invoke`); see `provideSparkClientMethods`. */
    clientMethod?: string | null;
    /** Its argument, as the server serialized it. */
    arguments?: unknown;
}

/**
 * A value the server shows exactly once (a generated client secret, an API token), in a dialog with
 * a copy button (`IClientAccessor.ShowSecret`, `docs/identity_provider_platform_PRD.md` D5). The server
 * keeps only a hash. The value is never written to the page's state, the URL or browser storage, and
 * closing the dialog discards it.
 */
export interface ShowSecretOperation {
    type: 'showSecret';
    /** The dialog's title, in the request's language. */
    title: string;
    /** The explanation shown above the value, in the request's language. */
    message: string;
    /** The value itself. */
    value: string;
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
    | ShowSecretOperation
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
