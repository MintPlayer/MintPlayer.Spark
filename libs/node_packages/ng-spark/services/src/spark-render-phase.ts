import { DOCUMENT, isPlatformServer } from '@angular/common';
import { ApplicationRef, Injectable, PLATFORM_ID, inject, signal } from '@angular/core';

/**
 * Whether the page is in its first, server-matching render: true on the server, and in the browser from
 * the start of a server-rendered page until it is first stable (hydrated). Whatever a component renders
 * differently in that phase must be the same on both sides, or hydration fails (docs/spark_ssr_PRD.md D14).
 *
 * A page the browser renders on its own (no `ng-server-context` on the root, e.g. the service worker's
 * shell) is never in the phase, and neither is a later navigation.
 */
@Injectable({ providedIn: 'root' })
export class SparkRenderPhase {
  private readonly phase = signal(this.initial());
  readonly serverMatching = this.phase.asReadonly();

  constructor() {
    if (this.phase() && !isPlatformServer(inject(PLATFORM_ID)))
      inject(ApplicationRef).whenStable().then(() => this.phase.set(false));
  }

  private initial(): boolean {
    if (isPlatformServer(inject(PLATFORM_ID))) return true;
    // Angular's server render marks the root component's host with ng-server-context.
    return inject(DOCUMENT).querySelector('[ng-server-context]') !== null;
  }
}
