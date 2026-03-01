import { PersistentObject } from './persistent-object';

export interface RetryActionPayload {
  type: 'retry-action';
  step: number;
  title: string;
  message?: string;
  options: string[];
  defaultOption?: string;
  persistentObject?: PersistentObject;
}

export interface RetryActionResult {
  step: number;
  option: string;
  persistentObject?: PersistentObject;
}
