import { DOCUMENT, Location, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, inject } from '@angular/core';
import { Router } from '@angular/router';
import { SwPush } from '@angular/service-worker';
import { sparkInAppUrl } from './spark-in-app-url';

/**
 * Notification clicks while the application is open (`docs/seo_analyzer_PRD.md` X10, D30): a click on
 * a push whose `notification.data.url` is on this origin becomes an in-app router navigation, so the
 * page keeps its state instead of loading again.
 *
 * What it does NOT do, because Angular's service worker (ngsw) already does it: show the push, and
 * open the page when the application is closed. The server sends Angular's payload,
 * `{ notification: { title, body, tag, data: { url, notificationId, onActionClick: { default: {
 * operation, url } } } } }`, and ngsw performs `onActionClick.default` itself on the click, then
 * broadcasts the click to the open pages, which is what {@link SwPush.notificationClicks} reports here.
 *
 * ⚠️ The operation decides whether this handler matters. ngsw runs it BEFORE the broadcast:
 * `navigateLastFocusedOrOpen` makes the focused page load the URL in full, so this handler then
 * finds the router already there and does nothing; `focusLastFocusedOrOpen` focuses the open page
 * without loading it (and opens the URL when no page is open), leaving the navigation to this handler.
 *
 * Active only in a browser with the service worker enabled; started by `provideSparkServiceWorker()`.
 */
@Injectable({ providedIn: 'root' })
export class SparkNotificationClicks {
  private readonly swPush = inject(SwPush);
  private readonly router = inject(Router, { optional: true });
  private readonly location = inject(Location, { optional: true });
  private readonly document = inject(DOCUMENT);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);

  private started = false;

  /** Starts listening. Idempotent; called by the app initializer `provideSparkServiceWorker()` adds. */
  start(): void {
    if (this.started) return;
    this.started = true;
    if (!isPlatformBrowser(this.platformId) || !this.swPush.isEnabled || !this.router) return;

    const subscription = this.swPush.notificationClicks.subscribe(({ notification }) => {
      const data = notification?.data as { url?: unknown } | undefined;
      this.navigate(typeof data?.url === 'string' ? data.url : null);
    });
    this.destroyRef.onDestroy(() => subscription.unsubscribe());
  }

  private navigate(url: string | null): void {
    const target = sparkInAppUrl(url, this.document, this.location);
    // Another origin is ngsw's to open (onActionClick); the router cannot reach it.
    if (!target || !this.router || this.router.url === target) return;
    void this.router.navigateByUrl(target);
  }
}
