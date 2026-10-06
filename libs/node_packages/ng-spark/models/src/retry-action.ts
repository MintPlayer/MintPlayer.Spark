import { PersistentObject } from './persistent-object';

export interface RetryActionPayload {
  type: 'retry-action';
  step: number;
  title: string;
  message?: string;
  options: string[];
  defaultOption?: string;
  persistentObject?: PersistentObject;
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
