import { ViewportScroller, isPlatformBrowser } from '@angular/common';
import { ApplicationRef, DestroyRef, Injectable, PLATFORM_ID, inject, isDevMode } from '@angular/core';
import { NavigationEnd, NavigationStart, Router } from '@angular/router';

import { SparkViewportScroller } from './spark-viewport-scroller';

/** How long after a fragment navigation the router must have asked for its anchor before the dev warning. */
const ASK_WITHIN_MS = 1_000;

/**
 * What `withFragmentScrolling()` starts beside its scroller (#721, Part E, Dec11):
 * - **the next navigation cancels** the wait for, or the holding of, the last anchor;
 * - **the first anchor after a hydration**: the router skips the first navigation's scroll while the app hydrates, so
 *   once the app is stable, if the router has not asked for the URL's anchor by then, this asks;
 * - **a dev-mode warning** when a later fragment navigation ends and the router never asks: the app has
 *   `withFragmentScrolling()` without `withInMemoryScrolling({ anchorScrolling: 'enabled' })`. A back or forward
 *   navigation is not counted: the router restores its position instead.
 */
@Injectable({ providedIn: 'root' })
export class SparkFragmentScrolling {
  private readonly router = inject(Router);
  private readonly scroller = inject(ViewportScroller);
  private readonly appRef = inject(ApplicationRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private started = false;
  private warned = false;
  private navigations = 0;
  private lastNavigationId = 0;
  private lastTrigger: NavigationStart['navigationTrigger'];

  start(): void {
    const scroller = this.scroller;
    if (this.started || !this.browser || !(scroller instanceof SparkViewportScroller)) return;
    this.started = true;

    const subscription = this.router.events.subscribe(event => {
      if (event instanceof NavigationStart) {
        this.lastNavigationId = event.id;
        this.lastTrigger = event.navigationTrigger;
        scroller.cancel();
      } else if (event instanceof NavigationEnd) {
        const first = this.navigations++ === 0;
        const fragment = this.router.parseUrl(event.urlAfterRedirects).fragment;
        if (!fragment) return;
        const asked = scroller.anchorRequests;
        if (first) void this.askForFirstAnchor(scroller, fragment, asked, event.id);
        else if (isDevMode() && !this.warned && this.lastTrigger !== 'popstate') this.warnIfNeverAsked(scroller, asked, event.id);
      }
    });
    this.destroyRef.onDestroy(() => subscription.unsubscribe());
  }

  private async askForFirstAnchor(scroller: SparkViewportScroller, fragment: string, asked: number, navigationId: number): Promise<void> {
    await this.appRef.whenStable();
    // Past the router's own scroll, which follows NavigationEnd by a timeout or a frame.
    await new Promise(resolve => setTimeout(resolve));
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    if (scroller.anchorRequests !== asked || this.lastNavigationId !== navigationId) return;
    scroller.scrollToAnchor(fragment);
  }

  private warnIfNeverAsked(scroller: SparkViewportScroller, asked: number, navigationId: number): void {
    setTimeout(() => {
      if (this.warned || scroller.anchorRequests !== asked || this.lastNavigationId !== navigationId) return;
      this.warned = true;
      console.warn(
        'Spark: the URL has a #fragment, but the router never asked to scroll to it. withFragmentScrolling() needs '
        + "withInMemoryScrolling({ anchorScrolling: 'enabled' }) in provideRouter (docs/guide-object-urls.md).",
      );
    }, ASK_WITHIN_MS);
  }
}
