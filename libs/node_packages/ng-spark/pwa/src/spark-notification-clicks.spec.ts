import { APP_BASE_HREF, Location } from '@angular/common';
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
        // The app is served under /app/: the router works below the base href (Angular's own Location over it).
        { provide: APP_BASE_HREF, useValue: '/app/' },
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

  // A same-origin link no route matches is the server's (SeoTester's report PDF, M11): loaded as a page. An app path is
  // below the base href for the page load too, as it was for the router: /app/seotester/…, never the host's /seotester/….
  it('loads a same-origin link the router cannot match as a page, below the base href', async () => {
    router.navigateByUrl.mockRejectedValue(new Error('NG04002'));
    start();
    const clicksService = TestBed.inject(SparkNotificationClicks);
    const assign = vi.spyOn(clicksService as unknown as { assign(href: string): void }, 'assign').mockImplementation(() => undefined);
    click('/seotester/report.pdf?run=1');
    await Promise.resolve();
    await Promise.resolve();
    expect(assign).toHaveBeenCalledWith(`${location.origin}/app/seotester/report.pdf?run=1`);
  });

  it('navigates in-app to an app-relative URL', () => {
    start();
    click('/runs/7');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/runs/7');
  });

  // The origin alone used to decide: a same-origin path outside /app/ (another application on the host) was handed
  // to this application's router.
  it('leaves a same-origin URL outside the base href to the page load', () => {
    start();
    click(`${location.origin}/other/runs/42`);
    click(`${location.origin}/application/runs/42`);
    expect(router.navigateByUrl).not.toHaveBeenCalled();
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

  it('is null for a same-origin URL outside the base href', () => {
    TestBed.configureTestingModule({ providers: [{ provide: APP_BASE_HREF, useValue: '/app/' }] });
    const appLocation = TestBed.inject(Location);
    expect(sparkInAppUrl(`${location.origin}/app/runs/42`, document, appLocation)).toBe('/runs/42');
    expect(sparkInAppUrl(`${location.origin}/other/runs/42`, document, appLocation)).toBeNull();
    TestBed.resetTestingModule();
  });
});
