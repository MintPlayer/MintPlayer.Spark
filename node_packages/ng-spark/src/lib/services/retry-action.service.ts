import { Injectable, signal } from '@angular/core';
import { RetryActionPayload, RetryActionResult } from '../models';

@Injectable({ providedIn: 'root' })
export class RetryActionService {
  private resolveRetry: ((result: RetryActionResult) => void) | null = null;

  payload = signal<RetryActionPayload | null>(null);

  show(payload: RetryActionPayload): Promise<RetryActionResult> {
    this.payload.set(payload);
    return new Promise(resolve => { this.resolveRetry = resolve; });
  }

  respond(result: RetryActionResult): void {
    this.payload.set(null);
    this.resolveRetry?.(result);
    this.resolveRetry = null;
  }
}
