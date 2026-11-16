import { ApplicationRef, PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationStart, Router } from '@angular/router';
import { SwUpdate, UnrecoverableStateEvent, VersionEvent } from '@angular/service-worker';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  SPARK_SW_PAGE_LOADER,
  SPARK_SW_SCHEDULE,
  SparkServiceWorkerUpdates,
} from './spark-service-worker-updates';

const HOUR = 60 * 60 * 1000;

function versionReady(latestHash = 'new', build = '22.31.0'): VersionEvent {
  return {
    type: 'VERSION_READY',
    currentVersion: { hash: 'old', appData: { build: '22.30.0' } },
    latestVersion: { hash: latestHash, appData: { build } },
  };
}

describe('SparkServiceWorkerUpdates', () => {
  let versionUpdates: Subject<VersionEvent>;
  let unrecoverable: Subject<UnrecoverableStateEvent>;
  let routerEvents: Subject<unknown>;
  let swUpdate: { isEnabled: boolean; versionUpdates: Subject<VersionEvent>; unrecoverable: Subject<UnrecoverableStateEvent>; checkForUpdate: ReturnType<typeof vi.fn> };
  let abort: ReturnType<typeof vi.fn>;
  let loader: { reload: ReturnType<typeof vi.fn> };
  let visibility: DocumentVisibilityState;

  function setVisibility(state: DocumentVisibilityState, dispatch = true): void {
    visibility = state;
    if (dispatch) document.dispatchEvent(new Event('visibilitychange'));
  }

  function navigate(url: string, trigger: 'imperative' | 'popstate' = 'imperative'): void {
    routerEvents.next(new NavigationStart(1, url, trigger));
  }

  async function start(options: { enabled?: boolean; platform?: string } = {}): Promise<SparkServiceWorkerUpdates> {
    swUpdate.isEnabled = options.enabled ?? true;
    TestBed.configureTestingModule({
      providers: [
        { provide: SwUpdate, useValue: swUpdate },
        // A router is present, as in every app: the policy must leave its navigations alone.
        { provide: Router, useValue: { events: routerEvents, currentNavigation: () => ({ abort }) } },
        { provide: SPARK_SW_PAGE_LOADER, useValue: loader },
        { provide: SPARK_SW_SCHEDULE, useValue: { checkIntervalMs: 6 * HOUR, visibleCheckThrottleMs: 60_000 } },
        ...(options.platform ? [{ provide: PLATFORM_ID, useValue: options.platform }] : []),
      ],
    });
    const updates = TestBed.inject(SparkServiceWorkerUpdates);
    updates.start();
    await TestBed.inject(ApplicationRef).whenStable();
    return updates;
  }

  beforeEach(() => {
    // Only the clock and the interval are faked: whenStable() needs real microtasks and timeouts.
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    versionUpdates = new Subject();
    unrecoverable = new Subject();
    routerEvents = new Subject();
    swUpdate = { isEnabled: true, versionUpdates, unrecoverable, checkForUpdate: vi.fn().mockResolvedValue(false) };
    abort = vi.fn();
    loader = { reload: vi.fn() };
    visibility = 'visible';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    delete (document as unknown as Record<string, unknown>)['visibilityState'];
  });

  describe('VERSION_READY', () => {
    // Q1c (owner, 2026-11-16): a downloaded version never reloads the page; the next full load runs it.
    it('records the update and never loads a page, on navigations nor on becoming visible', async () => {
      const updates = await start();

      versionUpdates.next(versionReady());
      navigate('/po/car/123?tab=2');
      navigate('/previous', 'popstate');
      setVisibility('hidden');
      setVisibility('visible');

      expect(updates.updateReady()).toBe(true);
      expect(abort).not.toHaveBeenCalled();
      expect(loader.reload).not.toHaveBeenCalled();
    });

    it('exposes the versions and their appData', async () => {
      const updates = await start();

      versionUpdates.next(versionReady('v2', '22.32.0'));

      expect(updates.latestVersion()).toEqual({ hash: 'v2', appData: { build: '22.32.0' } });
      expect(updates.currentVersion()).toEqual({ hash: 'old', appData: { build: '22.30.0' } });
    });

    it('ignores other version events', async () => {
      const updates = await start();

      versionUpdates.next({ type: 'VERSION_DETECTED', version: { hash: 'v2' } });
      versionUpdates.next({ type: 'NO_NEW_VERSION_DETECTED', version: { hash: 'old' } });

      expect(updates.updateReady()).toBe(false);
      expect(updates.latestVersion()).toBeNull();
    });
  });

  describe('unrecoverable', () => {
    it('reloads at once in a visible tab', async () => {
      await start();

      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone' });

      expect(loader.reload).toHaveBeenCalledTimes(1);
    });

    it('reloads only once, however often it is reported', async () => {
      await start();

      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone' });
      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone again' });

      expect(loader.reload).toHaveBeenCalledTimes(1);
    });

    it('waits until a hidden tab becomes visible', async () => {
      await start();
      setVisibility('hidden');

      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone' });
      expect(loader.reload).not.toHaveBeenCalled();

      setVisibility('visible');
      expect(loader.reload).toHaveBeenCalledTimes(1);
    });
  });

  describe('check schedule', () => {
    it('checks once the application is stable', async () => {
      await start();

      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(1);
    });

    it('checks every 6 hours', async () => {
      await start();

      vi.advanceTimersByTime(6 * HOUR - 1);
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(1);
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(2);

      vi.advanceTimersByTime(6 * HOUR);
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(3);
    });

    it('checks when the tab becomes visible, at most once a minute', async () => {
      await start();
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(1);

      // Within a minute of the stable check: throttled.
      setVisibility('hidden');
      vi.setSystemTime(Date.now() + 30_000);
      setVisibility('visible');
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(1);

      setVisibility('hidden');
      vi.setSystemTime(Date.now() + 30_000);
      setVisibility('visible');
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(2);

      // Becoming hidden never checks.
      vi.setSystemTime(Date.now() + 120_000);
      setVisibility('hidden');
      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(2);
    });

    it('survives a failing check', async () => {
      swUpdate.checkForUpdate.mockRejectedValue(new Error('offline'));
      await start();

      vi.advanceTimersByTime(6 * HOUR);

      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(2);
    });

    it('stops checking when the application is destroyed', async () => {
      await start();

      TestBed.resetTestingModule();
      vi.advanceTimersByTime(12 * HOUR);
      visibility = 'visible';
      document.dispatchEvent(new Event('visibilitychange'));

      expect(swUpdate.checkForUpdate).toHaveBeenCalledTimes(1);
    });
  });

  describe('disabled', () => {
    it('does nothing when the service worker is disabled', async () => {
      const updates = await start({ enabled: false });

      versionUpdates.next(versionReady());
      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone' });
      vi.advanceTimersByTime(12 * HOUR);
      setVisibility('hidden');
      setVisibility('visible');

      expect(updates.updateReady()).toBe(false);
      expect(swUpdate.checkForUpdate).not.toHaveBeenCalled();
      expect(loader.reload).not.toHaveBeenCalled();
    });

    it('does nothing on the server', async () => {
      const updates = await start({ platform: 'server' });

      versionUpdates.next(versionReady());
      unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'gone' });

      expect(updates.updateReady()).toBe(false);
      expect(swUpdate.checkForUpdate).not.toHaveBeenCalled();
      expect(loader.reload).not.toHaveBeenCalled();
    });
  });
});
