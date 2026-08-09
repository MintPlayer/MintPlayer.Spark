import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

/** Microtask flush — let pending awaited Promises resolve before the next expectation. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * Stands in for the popup window. `window.open` is not implemented in jsdom, and the flow
 * only ever touches `closed` and `close()` anyway.
 */
function fakePopup() {
  return { closed: false, close: vi.fn(function (this: { closed: boolean }) { this.closed = true; }) };
}

function postFromCallback(data: unknown, origin = window.location.origin) {
  window.dispatchEvent(new MessageEvent('message', { data, origin }));
}

/**
 * `loginWithProvider` is the popup handshake. What matters is not only that a successful
 * message signs the user in, but that every *other* ending — a refusal, a foreign message,
 * a blocked popup, a window the user closed by hand — also settles and takes the listener
 * with it. The hand-rolled version this replaces leaked its listener on all four.
 */
describe('SparkAuthService.loginWithProvider', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;
  let open: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);

    // Service constructor calls checkAuth().
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    open = vi.spyOn(window, 'open');
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('opens the popup at the configured base path with the popup flag set', () => {
    open.mockReturnValue(fakePopup() as unknown as Window);

    service.loginWithProvider('GitHub', { returnUrl: '/github-projects' });

    const url = open.mock.calls[0][0] as string;
    expect(url).toContain('/spark/auth/external-login');
    expect(url).toContain('provider=GitHub');
    expect(url).toContain(`returnUrl=${encodeURIComponent('/github-projects')}`);
    // Without this the callback redirects the popup instead of posting back, which is
    // exactly the bug: the opener's listener would wait forever.
    expect(url).toContain('popup=1');
  });

  it('re-reads the session and resolves on a success message', async () => {
    const popup = fakePopup();
    open.mockReturnValue(popup as unknown as Window);

    const promise = service.loginWithProvider('GitHub');
    postFromCallback({ type: 'spark:external-login', success: true });
    await flush();

    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [],
    });

    await expect(promise).resolves.toEqual({ success: true });
    expect(service.isAuthenticated()).toBe(true);
    expect(popup.close).toHaveBeenCalled();
  });

  it('surfaces the server-side refusal code without signing anyone in', async () => {
    open.mockReturnValue(fakePopup() as unknown as Window);

    const promise = service.loginWithProvider('GitHub');
    postFromCallback({ type: 'spark:external-login', success: false, error: 'email_not_verified' });

    await expect(promise).resolves.toEqual({ success: false, error: 'email_not_verified' });
    expect(service.isAuthenticated()).toBe(false);
    // A refusal must not re-read the session — there is nothing to read, and a stray /me
    // here would be the difference between "refused" and "silently still signed in".
    http.expectNone('/spark/auth/me');
  });

  it('ignores messages from another origin and anything that is not ours', async () => {
    open.mockReturnValue(fakePopup() as unknown as Window);

    let settled = false;
    service.loginWithProvider('GitHub').then(() => { settled = true; });

    postFromCallback({ type: 'spark:external-login', success: true }, 'https://evil.example');
    postFromCallback({ type: 'something-else', success: true });
    await flush();

    // Same-origin is the only thing separating this handshake from any script that can
    // postMessage at this window, so a foreign success must not sign the user in.
    expect(settled).toBe(false);
    http.expectNone('/spark/auth/me');
  });

  it('reports a blocked popup instead of hanging', async () => {
    open.mockReturnValue(null);

    await expect(service.loginWithProvider('GitHub'))
      .resolves.toEqual({ success: false, error: 'popup_blocked' });
  });

  it('detects a popup the user closed by hand, and removes its listener', async () => {
    vi.useFakeTimers();
    const popup = fakePopup();
    open.mockReturnValue(popup as unknown as Window);
    const removeListener = vi.spyOn(window, 'removeEventListener');

    const promise = service.loginWithProvider('GitHub');
    popup.closed = true;
    await vi.advanceTimersByTimeAsync(1000);

    await expect(promise).resolves.toEqual({ success: false, error: 'popup_closed' });
    expect(removeListener).toHaveBeenCalledWith('message', expect.any(Function));
  });

  it('navigates the current page in redirect mode and opens nothing', () => {
    const originalLocation = window.location;
    const assign = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { origin: originalLocation.origin, href: originalLocation.href, assign },
    });

    try {
      service.loginWithProvider('GitHub', { returnUrl: '/after', mode: 'redirect' });

      expect(open).not.toHaveBeenCalled();
      expect(assign).toHaveBeenCalledOnce();
      const url = assign.mock.calls[0][0] as string;
      expect(url).toContain('provider=GitHub');
      expect(url).not.toContain('popup');
    } finally {
      Object.defineProperty(window, 'location', { configurable: true, value: originalLocation });
    }
  });
});
