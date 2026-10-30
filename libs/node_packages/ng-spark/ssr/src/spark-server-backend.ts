import { Injectable, PendingTasks, inject } from '@angular/core';
import { HttpBackend, HttpErrorResponse, HttpEvent, HttpHeaders, HttpRequest, HttpResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { SPARK_SERVER_DATA, SparkSuppliedResponse } from './spark-server-data';
import { sparkServerRequestKey } from './spark-server-request-key';

/**
 * The `HttpBackend` of a server render: it answers from the responses the .NET supplier produced
 * in-process, and never touches the network.
 *
 * It sits at the backend, below every interceptor, on purpose. Angular runs the app's interceptors
 * first and its transfer-cache interceptor last, so only a response that comes out of the backend is
 * recorded into TransferState — and only a recorded response spares the browser its second fetch.
 *
 * A request the supplier did not predict (a miss, PRD D3) is answered with an error rather than left
 * pending: a pending request holds the app unstable and the render would wait for its timeout. Errors
 * are not recorded, so the browser makes that one request itself after hydration.
 *
 * Every answer holds the render open until the next macrotask (see {@link hold}). The apps are zoneless, so
 * the render serializes as soon as no task is pending, and a supplied answer is synchronous: without the
 * hold, a component awaiting one request before sending the next (`await getQuery(); ... executeQuery()`)
 * leaves a gap with nothing pending, and the page was serialized mid-load, the grid still showing its
 * spinner (2026-10-30, `/query/songs`).
 */
@Injectable()
export class SparkServerBackend implements HttpBackend {
  private readonly responses = new Map<string, SparkSuppliedResponse>(
    (inject(SPARK_SERVER_DATA, { optional: true })?.responses ?? []).map(r => [r.key, r]),
  );

  private readonly pendingTasks = inject(PendingTasks);

  handle(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    return this.hold(this.answer(req));
  }

  /**
   * The answer, with a pending task until the macrotask after it is delivered. The caller's continuation
   * (`await`, `.then`) runs as microtasks before that, so the request it sends next is pending before
   * this one is released.
   */
  private hold(answer: Observable<HttpEvent<unknown>>): Observable<HttpEvent<unknown>> {
    return new Observable(subscriber => {
      const done = this.pendingTasks.add();
      const subscription = answer.subscribe(subscriber);
      setTimeout(done);
      return subscription;
    });
  }

  private answer(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    const key = sparkServerRequestKey(req.method, req.urlWithParams, req.body);
    const supplied = this.responses.get(key);
    if (!supplied) {
      console.warn(`[spark-ssr] miss: ${key}`);
      return throwError(() => new HttpErrorResponse({
        url: req.urlWithParams,
        status: 0,
        statusText: 'Not supplied for server rendering',
      }));
    }

    const headers = new HttpHeaders({ 'Content-Type': 'application/json' });
    const body = parseBody(supplied.body, req.responseType);
    if (supplied.status >= 200 && supplied.status < 300) {
      return of(new HttpResponse({ url: req.urlWithParams, status: supplied.status, headers, body }));
    }
    return throwError(() => new HttpErrorResponse({
      url: req.urlWithParams,
      status: supplied.status,
      headers,
      error: body,
    }));
  }
}

function parseBody(text: string | null, responseType: HttpRequest<unknown>['responseType']): unknown {
  if (text === null || text === '') return null;
  if (responseType === 'text') return text;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}
