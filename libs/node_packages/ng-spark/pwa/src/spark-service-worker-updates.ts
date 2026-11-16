import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import {
  ApplicationRef,
  DestroyRef,
  Injectable,
  InjectionToken,
  PLATFORM_ID,
  inject,
  signal,
} from '@angular/core';
import { SwUpdate, VersionReadyEvent } from '@angular/service-worker';
import { Subscription, filter, first } from 'rxjs';

/** A service-worker version as `ngsw.json` describes it. `appData` is the config's `appData`. */
export interface SparkAppVersion {
  hash: string;
  appData?: Record<string, unknown>;
}

/** The one page load the update policy performs, on `unrecoverable`. Replaceable for tests. */
export interface SparkPageLoader {
  reload(): void;
}

/** How the update policy reloads the page: `window.location` by default. */
export const SPARK_SW_PAGE_LOADER = new InjectionToken<SparkPageLoader>('SPARK_SW_PAGE_LOADER', {
  providedIn: 'root',
  factory: () => {
    const location = inject(DOCUMENT).defaultView?.location;
    return { reload: () => location?.reload() };
  },
});

/** Update-check timing. Defaults: every 6 h, and on becoming visible at most once a minute. */
export interface SparkServiceWorkerSchedule {
  checkIntervalMs: number;
  visibleCheckThrottleMs: number;
}

export const SPARK_SW_SCHEDULE = new InjectionToken<SparkServiceWorkerSchedule>('SPARK_SW_SCHEDULE', {
  providedIn: 'root',
  factory: () => ({ checkIntervalMs: 6 * 60 * 60 * 1000, visibleCheckThrottleMs: 60 * 1000 }),
});

/**
 * The update policy of `withServiceWorker()` (#464, Q1b; revised 2026-11-16, Q1c).
 *
 * - `VERSION_READY`: **no page load.** The new version is downloaded and recorded (`updateReady`,
 *   `latestVersion`); the open tab keeps running the version it started with, which the worker keeps
 *   serving it, and the new version runs from the next full page load (a refresh, a new tab, or an
 *   app restart). An app that wants to tell the user can read `updateReady()`.
 * - `unrecoverable`: the running version can no longer be served, so the page reloads at once.
 * - Checks: once the application is stable, every 6 h, and when the tab becomes visible again
 *   (at most once a minute).
 * - **A hidden tab never reloads.** Spark has no unsaved-changes tracking to consult, so a reload
 *   nobody is looking at could throw away an open edit. An `unrecoverable` in a hidden tab waits
 *   until the tab is visible.
 *
 * Until 2026-11-16 a ready version turned the next router navigation into a full page load. That
 * made one link click per deploy per tab reload the whole page, which the owner asked to remove
 * (docs/pwa_external_login_PRD.md §6, Q1c).
 *
 * Nothing happens when the service worker is disabled (development builds, unsupported browsers)
 * or on the server.
 */
@Injectable({ providedIn: 'root' })
export class SparkServiceWorkerUpdates {
  private readonly swUpdate = inject(SwUpdate);
  private readonly appRef = inject(ApplicationRef);
  private readonly document = inject(DOCUMENT);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageLoader = inject(SPARK_SW_PAGE_LOADER);
  private readonly schedule = inject(SPARK_SW_SCHEDULE);

  private readonly _updateReady = signal(false);
  private readonly _currentVersion = signal<SparkAppVersion | null>(null);
  private readonly _latestVersion = signal<SparkAppVersion | null>(null);

  /** True once a new version is downloaded; the next full page load runs it. */
  readonly updateReady = this._updateReady.asReadonly();
  /** The running version, known once an update was reported. */
  readonly currentVersion = this._currentVersion.asReadonly();
  /** The version the next full page load runs (`appData.build` is the ng-spark version). */
  readonly latestVersion = this._latestVersion.asReadonly();

  private started = false;
  private reloading = false;
  private reloadWhenVisible = false;
  private lastCheck = 0;

  /** Starts the policy. Idempotent; called by the app initializer `withServiceWorker()` adds. */
  start(): void {
    if (this.started) return;
    this.started = true;

    if (!isPlatformBrowser(this.platformId) || !this.swUpdate.isEnabled) return;

    const subscriptions = new Subscription();
    this.destroyRef.onDestroy(() => subscriptions.unsubscribe());

    subscriptions.add(
      this.swUpdate.versionUpdates
        .pipe(filter((event): event is VersionReadyEvent => event.type === 'VERSION_READY'))
        .subscribe((event) => this.onVersionReady(event)),
    );

    subscriptions.add(this.swUpdate.unrecoverable.subscribe(() => this.onUnrecoverable()));

    // The first check waits for stability, so it never competes with the initial render.
    subscriptions.add(
      this.appRef.isStable.pipe(first((stable) => stable)).subscribe(() => {
        this.check();
        const interval = setInterval(() => this.check(), this.schedule.checkIntervalMs);
        subscriptions.add(() => clearInterval(interval));
      }),
    );

    const onVisibilityChange = () => {
      if (this.isHidden()) return;
      if (this.reloadWhenVisible) {
        this.reloadWhenVisible = false;
        this.reload();
        return;
      }
      if (Date.now() - this.lastCheck >= this.schedule.visibleCheckThrottleMs) this.check();
    };
    this.document.addEventListener('visibilitychange', onVisibilityChange);
    subscriptions.add(() => this.document.removeEventListener('visibilitychange', onVisibilityChange));
  }

  private onVersionReady(event: VersionReadyEvent): void {
    this._currentVersion.set(toVersion(event.currentVersion));
    this._latestVersion.set(toVersion(event.latestVersion));
    this._updateReady.set(true);
  }

  private onUnrecoverable(): void {
    if (this.isHidden()) {
      this.reloadWhenVisible = true;
      return;
    }
    this.reload();
  }

  private check(): void {
    this.lastCheck = Date.now();
    this.swUpdate.checkForUpdate().catch(() => {
      // Offline, or the worker is gone. The next scheduled check tries again.
    });
  }

  private reload(): void {
    if (this.reloading) return;
    this.reloading = true;
    this.pageLoader.reload();
  }

  private isHidden(): boolean {
    return this.document.visibilityState === 'hidden';
  }
}

function toVersion(version: { hash: string; appData?: object }): SparkAppVersion {
  return { hash: version.hash, appData: version.appData as Record<string, unknown> | undefined };
}
