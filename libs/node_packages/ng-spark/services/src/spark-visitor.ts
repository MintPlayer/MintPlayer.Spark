import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpFeature, HttpFeatureKind, HttpInterceptorFn, withInterceptors } from '@angular/common/http';
import { Injectable, PLATFORM_ID, Signal, inject, signal } from '@angular/core';
import { finalize } from 'rxjs';
import { SparkLiveService } from './spark-live.service';

/** The readable companion of the HttpOnly `spark.visitor` cookie: present while the browser has a visitor id. */
export const SPARK_VISITOR_PRESENCE_COOKIE = 'spark.visitor.present';

/**
 * Whether this browser is an identified anonymous visitor (`ISparkCurrentPrincipal.VisitorId` on the server).
 *
 * The visitor id itself is HttpOnly; the server sets `spark.visitor.present=1` beside it, and this service reads that.
 * {@link check} runs after every HTTP response (see {@link withSparkVisitor}): when the cookie appears, the
 * `/spark/live` socket is replaced, because the server keeps the handshake's request for the socket's life and would
 * otherwise never see the new visitor (`docs/notification_bell_PRD.md` G7).
 */
@Injectable({ providedIn: 'root' })
export class SparkVisitorPresence {
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly live = inject(SparkLiveService);
  private readonly state = signal(this.read());

  /** Whether the presence cookie is set. Always false on the server. */
  readonly present: Signal<boolean> = this.state.asReadonly();

  /** Reads the cookie again; reconnects the live socket when a visitor appeared. */
  check(): void {
    const now = this.read();
    if (now === this.state()) return;
    this.state.set(now);
    if (now) this.live.reconnect();
  }

  private read(): boolean {
    if (!this.browser) return false;
    let cookie: string;
    try {
      cookie = this.document.cookie ?? '';
    } catch {
      return false;
    }
    return cookie.split(';').some(part => part.trim().startsWith(`${SPARK_VISITOR_PRESENCE_COOKIE}=`));
  }
}

/** Checks {@link SparkVisitorPresence} after each response, success or error alike. */
export const sparkVisitorInterceptor: HttpInterceptorFn = (req, next) => {
  const presence = inject(SparkVisitorPresence);
  return next(req).pipe(finalize(() => presence.check()));
};

/**
 * `provideHttpClient(...withSparkVisitor())`: registers {@link sparkVisitorInterceptor}, so a visitor id that a
 * response issues reaches the open `/spark/live` socket. Needed only by an app that addresses anonymous visitors.
 */
export function withSparkVisitor(): HttpFeature<HttpFeatureKind>[] {
  return [withInterceptors([sparkVisitorInterceptor])];
}
