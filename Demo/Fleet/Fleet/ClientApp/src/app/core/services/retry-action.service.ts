import { Injectable } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { RetryActionPayload, RetryActionResult } from '../models/retry-action';

@Injectable({ providedIn: 'root' })
export class RetryActionService {
  private payloadSubject = new Subject<RetryActionPayload>();
  private responseSubject = new Subject<RetryActionResult>();

  payload$ = this.payloadSubject.asObservable();

  show(payload: RetryActionPayload): Observable<RetryActionResult> {
    this.payloadSubject.next(payload);
    return new Observable<RetryActionResult>(subscriber => {
      const sub = this.responseSubject.subscribe(result => {
        subscriber.next(result);
        subscriber.complete();
        sub.unsubscribe();
      });
    });
  }

  respond(result: RetryActionResult): void {
    this.responseSubject.next(result);
  }
}
