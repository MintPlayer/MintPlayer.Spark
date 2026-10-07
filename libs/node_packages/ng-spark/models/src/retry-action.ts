import { PersistentObject } from './persistent-object';

/**
 * The option a cancelled retry step answers with (`RetryResult.CancelOption` on the server): the
 * modal's own Cancel button or its dismissal on a `cancellable` prompt, and a client method that did
 * not answer. An identifier, never a label: the button shows `common.cancel` in the user's language.
 */
export const SPARK_RETRY_CANCEL = 'Cancel';

export interface RetryActionPayload {
  type: 'retry-action';
  step: number;
  title: string;
  message?: string;
  /** The action's own options, shown as given. Never `'Cancel'`: see {@link cancellable}. */
  options: string[];
  defaultOption?: string;
  persistentObject?: PersistentObject;
  /**
   * Whether the modal adds its own Cancel button, labelled `common.cancel`, which, like closing the
   * modal, answers {@link SPARK_RETRY_CANCEL}. Without it a closed modal abandons the request.
   */
  cancellable?: boolean;
  /**
   * Set when the server asks for a client method (`IRetryAccessor.Invoke`) instead of a choice: the
   * name a method was registered under with `provideSparkClientMethods`. No modal is shown then.
   */
  clientMethod?: string;
  /** The client method's argument, as the server serialized it. */
  arguments?: unknown;
}

export interface RetryActionResult {
  step: number;
  /** The option chosen. For a client-method step `'OK'` when it answered, `'Cancel'` otherwise. */
  option: string;
  persistentObject?: PersistentObject;
  /** What the client method resolved with; absent for an ordinary prompt and for a cancelled step. */
  value?: unknown;
}
