import { Location } from '@angular/common';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { SwPush } from '@angular/service-worker';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SparkNotificationClicks } from './spark-notification-clicks';
import { sparkInAppUrl } from './spark-in-app-url';

type Click = { action: string; notification: NotificationOptions & { title: string } };

describe('SparkNotificationClicks', () => {
  let clicks: Subject<Click>;
  let router: { url: string; navigateByUrl: ReturnType<typeof vi.fn> };

  function start(options: { enabled?: boolean; platform?: string } = {}): void {
    TestBed.configureTestingModule({
      providers: [
        { provide: SwPush, useValue: { isEnabled: options.enabled ?? true, notificationClicks: clicks } },
        { provide: Router, useValue: router },
        // The app is served under /app/: the router works below the base href.
        { provide: Location, useValue: { normalize: (url: string) => url.replace(/^\/app(?=\/|$)/, '') } },
        ...(options.platform ? [{ provide: PLATFORM_ID, useValue: options.platform }] : []),
      ],
    });
    TestBed.inject(SparkNotificationClicks).start();
  }

  function click(url: unknown): void {
    clicks.next({ action: '', notification: { title: 'Score dropped', data: { url, notificationId: 'Notifications/1' } } });
  }

  beforeEach(() => {
    clicks = new Subject();
    router = { url: '/', navigateByUrl: vi.fn().mockResolvedValue(true) };
  });

  afterEach(() => TestBed.resetTestingModule());

  it('navigates in-app to a same-origin absolute URL, below the base href', () => {
    start();
    click(`${location.origin}/app/runs/42?tab=issues#top`);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/runs/42?tab=issues#top');
  });

  it('navigates in-app to an app-relative URL', () => {
    start();
    click('/runs/7');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/runs/7');
  });

  it('leaves another origin, a script URL and a missing URL alone', () => {
    start();
    click('https://evil.example/runs/1');
    click('javascript:alert(1)');
    click(undefined);
    click(42);
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does nothing when the router is already there (ngsw navigated the page itself)', () => {
    router.url = '/runs/42';
    start();
    click('/runs/42');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does nothing when the service worker is disabled', () => {
    start({ enabled: false });
    click('/runs/42');
    expect(clicks.observed).toBe(false);
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does nothing on the server', () => {
    start({ platform: 'server' });
    click('/runs/42');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('stops listening when the application is destroyed', () => {
    start();
    TestBed.resetTestingModule();
    expect(clicks.observed).toBe(false);
  });
});

describe('sparkInAppUrl', () => {
  it('keeps the root of the application', () => {
    expect(sparkInAppUrl(`${location.origin}/`, document, null)).toBe('/');
  });

  it('is null for an unparseable value', () => {
    expect(sparkInAppUrl('http://[', document, null)).toBeNull();
  });
});
