import { DOCUMENT, Location, isPlatformBrowser } from '@angular/common';
import {
  ApplicationRef,
  DestroyRef,
  Injectable,
  InjectionToken,
  PLATFORM_ID,
  inject,
  signal,
} from '@angular/core';
import { NavigationStart, Router } from '@angular/router';
import { SwUpdate, VersionReadyEvent } from '@angular/service-worker';
import { Subscription, filter, first } from 'rxjs';

/** A service-worker version as `ngsw.json` describes it. `appData` is the config's `appData`. */
export interface SparkAppVersion {
  hash: string;
  appData?: Record<string, unknown>;
}

/** The two page loads the update policy performs. Replaceable for tests. */
export interface SparkPageLoader {
  assign(url: string): void;
  reload(): void;
}

/** How the update policy loads a page: `window.location` by default. */
export const SPARK_SW_PAGE_LOADER = new InjectionToken<SparkPageLoader>('SPARK_SW_PAGE_LOADER', {
  providedIn: 'root',
  factory: () => {
    const location = inject(DOCUMENT).defaultView?.location;
    return {
      assign: (url: string) => location?.assign(url),
      reload: () => location?.reload(),
    };
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
 * Remembers, per tab, the version a full load was made for. If the page that load produced still
 * reports the same version as ready (another tab keeps the old version alive, or the worker did not
 * hand this client the new one), the policy does not reload again: one attempt per version.
 */
const RELOADED_FOR_KEY = 'spark-sw-reloaded-for';

/**
 * The update policy of `provideSparkServiceWorker()` (#464, Q1b).
 *
 * - `VERSION_READY`: the **next** router navigation becomes a full page load of its target, so the
 *   user lands on the page they asked for, in the new version, without losing anything on the page
 *   they are leaving (the navigation was leaving it anyway).
 * - `unrecoverable`: the running version can no longer be served, so the page reloads at once.
 * - Checks: once the application is stable, every 6 h, and when the tab becomes visible again
 *   (at most once a minute).
 * - **A hidden tab never reloads.** Spark has no unsaved-changes tracking to consult, so a reload
 *   nobody is looking at could throw away an open edit. An `unrecoverable` in a hidden tab waits
 *   until the tab is visible; a navigation in a hidden tab stays a normal in-app navigation.
 *
 * Nothing happens when the service worker is disabled (development builds, unsupported browsers)
 * or on the server.
 */
@Injectable({ providedIn: 'root' })
export class SparkServiceWorkerUpdates {
  private readonly swUpdate = inject(SwUpdate);
  private readonly router = inject(Router, { optional: true });
  private readonly location = inject(Location, { optional: true });
  private readonly appRef = inject(ApplicationRef);
  private readonly document = inject(DOCUMENT);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageLoader = inject(SPARK_SW_PAGE_LOADER);
  private readonly schedule = inject(SPARK_SW_SCHEDULE);

  private readonly _updateReady = signal(false);
  private readonly _currentVersion = signal<SparkAppVersion | null>(null);
  private readonly _latestVersion = signal<SparkAppVersion | null>(null);

  /** True once a new version is downloaded and the next navigation will load it. */
  readonly updateReady = this._updateReady.asReadonly();
  /** The running version, known once an update was reported. */
  readonly currentVersion = this._currentVersion.asReadonly();
  /** The version the next navigation loads (`appData.build` is the ng-spark version). */
  readonly latestVersion = this._latestVersion.asReadonly();

  private started = false;
  private leaving = false;
  private reloadWhenVisible = false;
  private lastCheck = 0;

  /** Starts the policy. Idempotent; called by the app initializer `provideSparkServiceWorker()` adds. */
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

    if (this.router) {
      subscriptions.add(
        this.router.events
          .pipe(filter((event): event is NavigationStart => event instanceof NavigationStart))
          .subscribe((event) => this.onNavigationStart(event)),
      );
    }

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
    const latest = toVersion(event.latestVersion);
    this._currentVersion.set(toVersion(event.currentVersion));
    this._latestVersion.set(latest);

    if (this.readReloadedFor() === latest.hash) {
      // This page is the full load made for that version and it is still not the one served.
      // Reloading again would loop; the next deploy, or the next fresh tab, picks it up.
      return;
    }
    this._updateReady.set(true);
  }

  private onUnrecoverable(): void {
    if (this.isHidden()) {
      this.reloadWhenVisible = true;
      return;
    }
    this.reload();
  }

  private onNavigationStart(event: NavigationStart): void {
    if (!this._updateReady() || this.leaving || this.isHidden()) return;

    this.leaving = true;
    this.writeReloadedFor(this._latestVersion()?.hash);
    this.router?.currentNavigation()?.abort();

    if (event.navigationTrigger === 'popstate') {
      // Back/forward: the address bar already shows the target, and assign() would add a
      // duplicate history entry.
      this.pageLoader.reload();
      return;
    }

    // event.url is relative to the router's base; prepareExternalUrl adds the <base href>.
    const target = this.location ? this.location.prepareExternalUrl(event.url) : event.url;
    this.pageLoader.assign(target);
  }

  private check(): void {
    this.lastCheck = Date.now();
    this.swUpdate.checkForUpdate().catch(() => {
      // Offline, or the worker is gone. The next scheduled check tries again.
    });
  }

  private reload(): void {
    if (this.leaving) return;
    this.leaving = true;
    this.pageLoader.reload();
  }

  private isHidden(): boolean {
    return this.document.visibilityState === 'hidden';
  }

  private readReloadedFor(): string | null {
    try {
      return this.document.defaultView?.sessionStorage.getItem(RELOADED_FOR_KEY) ?? null;
    } catch {
      return null;
    }
  }

  private writeReloadedFor(hash: string | undefined): void {
    if (!hash) return;
    try {
      this.document.defaultView?.sessionStorage.setItem(RELOADED_FOR_KEY, hash);
    } catch {
      // Storage blocked: the guard is best effort; one reload per navigation is still bounded.
    }
  }
}

function toVersion(version: { hash: string; appData?: object }): SparkAppVersion {
  return { hash: version.hash, appData: version.appData as Record<string, unknown> | undefined };
}
