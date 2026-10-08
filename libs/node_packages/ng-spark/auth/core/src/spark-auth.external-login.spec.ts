import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import {
  SPARK_AUTH_CONFIG,
  SparkAuthConfig,
  defaultSparkAuthConfig,
  sparkExternalLoginDoneKey,
  sparkExternalLoginStorageKey,
} from '@mintplayer/ng-spark/auth/models';

/** Microtask flush — let pending awaited Promises resolve before the next expectation. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * Stands in for the popup window. `window.open` is not implemented in jsdom, and the flow
 * only ever touches `closed` and `close()` anyway.
 */
function fakePopup() {
  return { closed: false, close: vi.fn(function (this: { closed: boolean }) { this.closed = true; }) };
}

/**
 * A BroadcastChannel the test can drive. Node's real one delivers asynchronously and keeps the
 * process alive until closed; this one records what the flow posts and whether it was closed.
 */
class FakeBroadcastChannel {
  static instances: FakeBroadcastChannel[] = [];
  readonly posted: unknown[] = [];
  readonly listeners = new Set<(event: MessageEvent) => void>();
  closed = false;

  constructor(readonly name: string) {
    FakeBroadcastChannel.instances.push(this);
  }

  addEventListener(_type: string, listener: (event: MessageEvent) => void) { this.listeners.add(listener); }
  removeEventListener(_type: string, listener: (event: MessageEvent) => void) { this.listeners.delete(listener); }
  postMessage(data: unknown) { this.posted.push(data); }
  close() { this.closed = true; }

  /** What another document posting on this channel looks like from here. */
  emit(data: unknown) {
    for (const listener of [...this.listeners]) listener(new MessageEvent('message', { data }));
  }
}

function postFromCallback(data: unknown, origin = window.location.origin) {
  window.dispatchEvent(new MessageEvent('message', { data, origin }));
}

function setVisibility(state: DocumentVisibilityState) {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
}

function stubStandalone(matches: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: vi.fn((query: string) => ({ matches: matches && query === '(display-mode: standalone)', media: query })),
  });
}

/**
 * `loginWithProvider` is the popup handshake. Under COOP on the provider's pages the opener loses
 * its handle and reads `closed` while the user is still typing (F6), so the result can arrive on any
 * of four sources, a closed popup is a UI hint only (D2 final), and every ending — result, a newer
 * attempt, the 10-minute timeout — tears everything down.
 */
describe('SparkAuthService.loginWithProvider', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;
  let open: ReturnType<typeof vi.spyOn>;

  function configure(config: SparkAuthConfig = defaultSparkAuthConfig) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: config },
      ],
    });
    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);

    // Service constructor calls checkAuth().
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
  }

  /** The nonce the flow put on the challenge URL it opened. */
  function openedNonce(call = 0): string {
    const url = new URL(open.mock.calls[call][0] as string, window.location.origin);
    return url.searchParams.get('nonce')!;
  }

  function payload(nonce: string, overrides: Record<string, unknown> = {}) {
    return { type: 'spark:external-login', success: false, error: 'no_login_info', nonce, ...overrides };
  }

  /** The channel the current attempt opened. */
  const channel = () => FakeBroadcastChannel.instances[FakeBroadcastChannel.instances.length - 1];

  beforeEach(() => {
    FakeBroadcastChannel.instances = [];
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
    stubStandalone(false);
    setVisibility('visible');
    localStorage.clear();
    open = vi.spyOn(window, 'open');
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    localStorage.clear();
  });

  describe('popup mode', () => {
    beforeEach(() => configure());

    it('opens the challenge with the popup flag, a nonce and the service-worker bypass', () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      service.loginWithProvider('GitHub', { returnUrl: '/github-projects' });

      const url = new URL(open.mock.calls[0][0] as string, window.location.origin);
      expect(url.pathname).toBe('/spark/auth/external-login');
      expect(url.searchParams.get('provider')).toBe('GitHub');
      expect(url.searchParams.get('returnUrl')).toBe('/github-projects');
      // Without this the callback redirects the popup instead of posting back.
      expect(url.searchParams.get('popup')).toBe('1');
      expect(url.searchParams.get('ngsw-bypass')).toBe('true');
      // The server's pattern, and 32 characters per the contract.
      expect(url.searchParams.get('nonce')).toMatch(/^[A-Za-z0-9_-]{32}$/);
      // The outcome comes back to the opener; there is no page for a failure to land on.
      expect(url.searchParams.has('errorUrl')).toBe(false);
    });

    it('uses a fresh nonce per attempt', () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      service.loginWithProvider('GitHub');
      service.loginWithProvider('GitHub');

      expect(openedNonce(0)).not.toBe(openedNonce(1));
    });

    it('re-reads the session and resolves on a success message', async () => {
      const popup = fakePopup();
      open.mockReturnValue(popup as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      postFromCallback(payload(openedNonce(), { success: true, error: null }));
      await flush();

      // The identity changed, so the token is refreshed before the session is re-read.
      http.expectOne('/spark/auth/csrf-refresh').flush(null);
      await flush();

      http.expectOne('/spark/auth/me').flush({
        isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [],
      });

      await expect(promise).resolves.toEqual({ success: true });
      expect(service.isAuthenticated()).toBe(true);
      expect(popup.close).toHaveBeenCalled();
    });

    it('still resolves the sign-in when the token refresh fails', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      postFromCallback(payload(openedNonce(), { success: true, error: null }));
      await flush();

      // The sign-in already happened in the popup; a failed refresh must not leave the flow hanging.
      http.expectOne('/spark/auth/csrf-refresh').flush(null, { status: 500, statusText: 'Server Error' });
      await flush();

      http.expectOne('/spark/auth/me').flush({
        isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [],
      });

      await expect(promise).resolves.toEqual({ success: true });
      expect(service.isAuthenticated()).toBe(true);
    });

    it('surfaces the server-side refusal code without signing anyone in', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      postFromCallback(payload(openedNonce(), { error: 'email_not_verified' }));

      await expect(promise).resolves.toEqual({ success: false, error: 'email_not_verified' });
      expect(service.isAuthenticated()).toBe(false);
      // A refusal must not re-read the session — a stray /me here would be the difference between
      // "refused" and "silently still signed in".
      http.expectNone('/spark/auth/me');
    });

    it('delivers through the BroadcastChannel when the opener was cut off', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      expect(channel().name).toBe('spark:external-login');
      channel().emit(payload(openedNonce(), { error: 'remote_failure' }));

      await expect(promise).resolves.toEqual({ success: false, error: 'remote_failure' });
    });

    it('delivers through the storage event for its own key', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      const key = sparkExternalLoginStorageKey(openedNonce());
      const value = JSON.stringify({ ...payload(openedNonce(), { error: 'link_failed' }), at: Date.now() });
      localStorage.setItem(key, value);
      window.dispatchEvent(new StorageEvent('storage', { key, newValue: value }));

      await expect(promise).resolves.toEqual({ success: false, error: 'link_failed' });
    });

    it('delivers by re-reading the storage key when the tab becomes visible again', async () => {
      // A frozen background tab may miss both the channel message and the storage event.
      open.mockReturnValue(fakePopup() as unknown as Window);

      let result: unknown;
      service.loginWithProvider('GitHub').then((r) => (result = r));
      setVisibility('hidden');
      localStorage.setItem(
        sparkExternalLoginStorageKey(openedNonce()),
        JSON.stringify({ ...payload(openedNonce()), at: Date.now() }));

      document.dispatchEvent(new Event('visibilitychange'));
      await flush();
      expect(result).toBeUndefined();

      setVisibility('visible');
      document.dispatchEvent(new Event('visibilitychange'));
      await flush();
      expect(result).toEqual({ success: false, error: 'no_login_info' });
    });

    it('delivers by re-reading the storage key on focus', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      localStorage.setItem(
        sparkExternalLoginStorageKey(openedNonce()),
        JSON.stringify({ ...payload(openedNonce(), { error: 'sign_in_to_link' }), at: Date.now() }));
      window.dispatchEvent(new Event('focus'));

      await expect(promise).resolves.toEqual({ success: false, error: 'sign_in_to_link' });
    });

    it('ignores another origin, a foreign type, and a payload for another nonce on every source', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      let settled = false;
      service.loginWithProvider('GitHub').then(() => { settled = true; });
      const nonce = openedNonce();

      postFromCallback(payload(nonce, { success: true }), 'https://evil.example');
      postFromCallback({ ...payload(nonce, { success: true }), type: 'something-else' });
      postFromCallback(payload('another-attempt-nonce-0000000000', { success: true }));
      postFromCallback(payload(null as unknown as string, { success: true }));
      channel().emit(payload('another-attempt-nonce-0000000000', { success: true }));
      const otherKey = sparkExternalLoginStorageKey('another-attempt-nonce-0000000000');
      const otherValue = JSON.stringify(payload('another-attempt-nonce-0000000000', { success: true }));
      localStorage.setItem(otherKey, otherValue);
      window.dispatchEvent(new StorageEvent('storage', { key: otherKey, newValue: otherValue }));
      // Our key holding someone else's nonce is not ours either.
      localStorage.setItem(sparkExternalLoginStorageKey(nonce), otherValue);
      window.dispatchEvent(new Event('focus'));
      await flush();

      expect(settled).toBe(false);
      expect(channel().posted).toEqual([]);
      http.expectNone('/spark/auth/csrf-refresh');
      http.expectNone('/spark/auth/me');
    });

    it('acknowledges on the channel, writes the done-marker and removes the payload', async () => {
      const popup = fakePopup();
      open.mockReturnValue(popup as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      const nonce = openedNonce();
      localStorage.setItem(sparkExternalLoginStorageKey(nonce), JSON.stringify(payload(nonce)));
      const ch = channel();
      ch.emit(payload(nonce));
      await promise;

      expect(ch.posted).toEqual([{ type: 'spark:external-login-ack', nonce }]);
      expect(localStorage.getItem(sparkExternalLoginDoneKey(nonce))).toBe('1');
      expect(localStorage.getItem(sparkExternalLoginStorageKey(nonce))).toBeNull();
      expect(popup.close).toHaveBeenCalled();
    });

    it('settles once even when every source delivers', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const promise = service.loginWithProvider('GitHub');
      const nonce = openedNonce();
      const ch = channel();
      ch.emit(payload(nonce, { error: 'link_failed' }));
      postFromCallback(payload(nonce, { error: 'no_login_info' }));

      await expect(promise).resolves.toEqual({ success: false, error: 'link_failed' });
      expect(ch.posted).toHaveLength(1);
    });

    it('reports a blocked popup instead of hanging, and is not pending', async () => {
      open.mockReturnValue(null);

      await expect(service.loginWithProvider('GitHub'))
        .resolves.toEqual({ success: false, error: 'popup_blocked' });
      expect(service.externalLoginPending()).toBe(false);
    });

    it('is pending while the attempt is open, and not after its result', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      expect(service.externalLoginPending()).toBe(false);
      const promise = service.loginWithProvider('GitHub');
      expect(service.externalLoginPending()).toBe(true);

      postFromCallback(payload(openedNonce()));
      await promise;
      expect(service.externalLoginPending()).toBe(false);
    });

    it('a closed popup only clears pending: the attempt keeps listening and a late result settles it', async () => {
      // D2 final: under COOP the popup reads closed while the user is still signing in (F6).
      vi.useFakeTimers();
      const popup = fakePopup();
      open.mockReturnValue(popup as unknown as Window);
      const removeListener = vi.spyOn(window, 'removeEventListener');

      let result: unknown;
      service.loginWithProvider('GitHub').then((r) => (result = r));
      popup.closed = true;
      await vi.advanceTimersByTimeAsync(5000);

      expect(service.externalLoginPending()).toBe(false);
      expect(result).toBeUndefined();
      expect(removeListener).not.toHaveBeenCalledWith('message', expect.any(Function));
      expect(channel().closed).toBe(false);

      channel().emit(payload(openedNonce(), { error: 'email_already_registered' }));
      await vi.advanceTimersByTimeAsync(0);
      expect(result).toEqual({ success: false, error: 'email_already_registered' });
    });

    it('a new attempt settles the open one popup_closed and tears it down', async () => {
      open.mockReturnValue(fakePopup() as unknown as Window);

      const first = service.loginWithProvider('GitHub');
      const firstNonce = openedNonce(0);
      const firstChannel = channel();
      const second = service.loginWithProvider('Google');

      await expect(first).resolves.toEqual({ success: false, error: 'popup_closed' });
      expect(firstChannel.closed).toBe(true);
      expect(service.externalLoginPending()).toBe(true);

      // The old nonce is dead; only the new attempt's result counts.
      let secondResult: unknown;
      second.then((r) => (secondResult = r));
      postFromCallback(payload(firstNonce, { success: true }));
      await flush();
      expect(secondResult).toBeUndefined();

      postFromCallback(payload(openedNonce(1), { error: 'link_failed' }));
      await expect(second).resolves.toEqual({ success: false, error: 'link_failed' });
    });

    it('settles popup_closed after ten minutes without a result', async () => {
      vi.useFakeTimers();
      open.mockReturnValue(fakePopup() as unknown as Window);

      let result: unknown;
      service.loginWithProvider('GitHub').then((r) => (result = r));

      await vi.advanceTimersByTimeAsync(10 * 60 * 1000 - 1000);
      expect(result).toBeUndefined();

      await vi.advanceTimersByTimeAsync(1000);
      expect(result).toEqual({ success: false, error: 'popup_closed' });
      expect(service.externalLoginPending()).toBe(false);
    });

    it('tears down every listener, the channel and the timers on settle', async () => {
      vi.useFakeTimers();
      open.mockReturnValue(fakePopup() as unknown as Window);
      const windowRemove = vi.spyOn(window, 'removeEventListener');
      const documentRemove = vi.spyOn(document, 'removeEventListener');

      const promise = service.loginWithProvider('GitHub');
      const ch = channel();
      ch.emit(payload(openedNonce()));
      await vi.advanceTimersByTimeAsync(0);
      await promise;

      expect(windowRemove).toHaveBeenCalledWith('message', expect.any(Function));
      expect(windowRemove).toHaveBeenCalledWith('storage', expect.any(Function));
      expect(windowRemove).toHaveBeenCalledWith('focus', expect.any(Function));
      expect(documentRemove).toHaveBeenCalledWith('visibilitychange', expect.any(Function));
      expect(ch.closed).toBe(true);
      expect(ch.listeners.size).toBe(0);
      // Neither the poll nor the 10-minute timeout survives.
      expect(vi.getTimerCount()).toBe(0);
    });
  });

  describe('mode', () => {
    it('opens no popup in redirect mode', () => {
      configure();
      // The navigation itself is deliberately not asserted. jsdom locks both `window.location` and
      // `Location.prototype.assign` against redefinition, so there is no portable seam to observe it
      // from a unit test. What matters here is the branch: redirect mode must not open a window.
      service.loginWithProvider('GitHub', { returnUrl: '/after', mode: 'redirect' });

      expect(open).not.toHaveBeenCalled();
    });

    it("'auto' redirects inside an installed web app", () => {
      stubStandalone(true);
      configure();

      service.loginWithProvider('GitHub');

      expect(open).not.toHaveBeenCalled();
    });

    it("'auto' redirects when iOS reports navigator.standalone", () => {
      Object.defineProperty(window.navigator, 'standalone', { configurable: true, value: true });
      try {
        configure();
        service.loginWithProvider('GitHub');
        expect(open).not.toHaveBeenCalled();
      } finally {
        delete (window.navigator as { standalone?: boolean }).standalone;
      }
    });

    it("'auto' opens a popup in a browser tab", () => {
      configure();
      open.mockReturnValue(fakePopup() as unknown as Window);

      service.loginWithProvider('GitHub');

      expect(open).toHaveBeenCalledTimes(1);
    });

    it("an explicit 'popup' setting keeps the popup inside an installed web app", () => {
      stubStandalone(true);
      configure({ ...defaultSparkAuthConfig, externalLoginMode: 'popup' });
      open.mockReturnValue(fakePopup() as unknown as Window);

      service.loginWithProvider('GitHub');

      expect(open).toHaveBeenCalledTimes(1);
    });

    it("a configured 'redirect' opens no popup in a browser tab", () => {
      configure({ ...defaultSparkAuthConfig, externalLoginMode: 'redirect' });

      service.loginWithProvider('GitHub');

      expect(open).not.toHaveBeenCalled();
    });

    it("the call's own mode overrides the configured one", () => {
      configure({ ...defaultSparkAuthConfig, externalLoginMode: 'redirect' });
      open.mockReturnValue(fakePopup() as unknown as Window);

      service.loginWithProvider('GitHub', { mode: 'popup' });

      expect(open).toHaveBeenCalledTimes(1);
    });

    it('a redirect attempt settles the open popup attempt popup_closed', async () => {
      configure();
      open.mockReturnValue(fakePopup() as unknown as Window);

      const first = service.loginWithProvider('GitHub');
      service.loginWithProvider('Google', { mode: 'redirect' });

      await expect(first).resolves.toEqual({ success: false, error: 'popup_closed' });
      expect(service.externalLoginPending()).toBe(false);
    });
  });
});

@Component({ standalone: true, template: '' })
class BlankPage {}

/** The login page and its provider buttons ask at the same moment: one request, not two. */
describe('SparkAuthService.capabilities', () => {
  function configure() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    const service = TestBed.inject(SparkAuthService);
    const http = TestBed.inject(HttpTestingController);
    http.match('/spark/auth/me').forEach((r) => r.flush(null, { status: 401, statusText: 'Unauthorized' }));
    return { service, http };
  }

  it('joins concurrent callers into one request, and asks again once it settled', async () => {
    const { service, http } = configure();
    const answer = { localCredentials: 'Full', externalProviders: [] };

    const first = service.capabilities();
    const second = service.capabilities();
    http.expectOne('/spark/auth/capabilities').flush(answer);

    await expect(first).resolves.toEqual(answer);
    await expect(second).resolves.toEqual(answer);

    // Not a cache: a later caller reads the server's current answer.
    const third = service.capabilities();
    http.expectOne('/spark/auth/capabilities').flush(answer);
    await expect(third).resolves.toEqual(answer);
    http.verify();
  });

  it('lets a caller after a failure try again', async () => {
    const { service, http } = configure();

    const failed = service.capabilities();
    http.expectOne('/spark/auth/capabilities').flush(null, { status: 500, statusText: 'Server Error' });
    await expect(failed).rejects.toBeTruthy();

    const retried = service.capabilities();
    http.expectOne('/spark/auth/capabilities').flush({ localCredentials: 'Full', externalProviders: [] });
    await expect(retried).resolves.toBeTruthy();
  });
});

/** Redirect mode's outcome is a query parameter on the next page load (D3). */
describe('SparkAuthService.takeExternalLoginResult', () => {
  async function at(url: string) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: BlankPage }]),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    const service = TestBed.inject(SparkAuthService);
    TestBed.inject(HttpTestingController).expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    const router = TestBed.inject(Router);
    await router.navigateByUrl(url);
    return { service, router };
  }

  it('answers the code and strips it, keeping the other parameters', async () => {
    const { service, router } = await at('/account/logins?tab=2&sparkExternalLogin=remote_failure');
    const navigate = vi.spyOn(router, 'navigateByUrl');

    expect(service.takeExternalLoginResult()).toBe('remote_failure');
    await new Promise<void>((resolve) => setTimeout(resolve, 0));

    expect(router.url).toBe('/account/logins?tab=2');
    // replaceUrl: Back must not bring the error back.
    expect(navigate).toHaveBeenCalledWith(expect.anything(), { replaceUrl: true });
  });

  it('answers null and navigates nowhere without the parameter', async () => {
    const { service, router } = await at('/sign-in?returnUrl=%2Fx');
    const navigate = vi.spyOn(router, 'navigateByUrl');

    expect(service.takeExternalLoginResult()).toBeNull();
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    expect(navigate).not.toHaveBeenCalled();
  });

  it('answers a failure once, even to a second caller before the strip lands', async () => {
    // Two components on one page (e.g. a login page and the provider buttons it hosts) must not
    // both show the same failure.
    const { service, router } = await at('/login?sparkExternalLogin=remote_failure');

    expect(service.takeExternalLoginResult()).toBe('remote_failure');
    expect(service.takeExternalLoginResult()).toBeNull();
    await new Promise<void>((resolve) => setTimeout(resolve, 0));

    expect(router.url).toBe('/login');
    // A later failure on the same page is news again.
    await router.navigateByUrl('/login?sparkExternalLogin=email_not_verified');
    expect(service.takeExternalLoginResult()).toBe('email_not_verified');
  });

  it('does not echo an unknown code', async () => {
    // Otherwise a crafted link chooses the text the page shows.
    const { service } = await at('/sign-in?sparkExternalLogin=Your%20account%20is%20suspended');

    expect(service.takeExternalLoginResult()).toBe('no_login_info');
  });
});

/**
 * D9/M6: in redirect mode a failure must land back on the page that started the attempt (which reads
 * `?sparkExternalLogin`), not on `returnUrl`. The challenge URL carries that page as `errorUrl`.
 */
describe('SparkAuthService redirect-mode challenge URL', () => {
  async function at(url: string) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: BlankPage }]),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    const service = TestBed.inject(SparkAuthService);
    TestBed.inject(HttpTestingController).expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await TestBed.inject(Router).navigateByUrl(url);
    // jsdom cannot observe window.location.assign; the service routes the navigation through here.
    const leave = vi.spyOn(service as unknown as { leaveForProvider(url: string): void }, 'leaveForProvider')
      .mockImplementation(() => undefined);
    const left = () => new URL(leave.mock.calls[0][0], window.location.origin);
    return { service, leave, left };
  }

  afterEach(() => vi.restoreAllMocks());

  it('sends the current page, query included, as errorUrl', async () => {
    const { service, left } = await at('/login?returnUrl=%2Fx');

    service.loginWithProvider('GitHub', { returnUrl: '/x', mode: 'redirect' });

    const url = left();
    expect(url.pathname).toBe('/spark/auth/external-login');
    expect(url.searchParams.get('provider')).toBe('GitHub');
    expect(url.searchParams.get('returnUrl')).toBe('/x');
    expect(url.searchParams.get('errorUrl')).toBe('/login?returnUrl=%2Fx');
    expect(url.searchParams.has('popup')).toBe(false);
  });

  it('encodes errorUrl as one parameter, so its own query does not leak into the challenge', async () => {
    const { service, leave } = await at('/login?returnUrl=%2Fx&tab=a');

    service.loginWithProvider('GitHub', { returnUrl: '/x', mode: 'redirect' });

    expect(leave.mock.calls[0][0]).toContain(`&errorUrl=${encodeURIComponent('/login?returnUrl=%2Fx&tab=a')}`);
  });

  it('drops an earlier sparkExternalLogin, so failures do not accumulate', async () => {
    const { service, left } = await at('/login?returnUrl=%2Fx&sparkExternalLogin=remote_failure');

    service.loginWithProvider('Google', { mode: 'redirect' });

    expect(left().searchParams.get('errorUrl')).toBe('/login?returnUrl=%2Fx');
  });

  it('applies to linking as well', async () => {
    const { service, left } = await at('/account/logins?sparkExternalLogin=no_login_info');

    service.linkProvider('GitHub', { returnUrl: '/account/logins', mode: 'redirect' });

    const url = left();
    expect(url.pathname).toBe('/spark/auth/external-logins/link');
    expect(url.searchParams.get('errorUrl')).toBe('/account/logins');
  });

  it("honours the caller's own errorUrl", async () => {
    const { service, left } = await at('/login');

    service.loginWithProvider('GitHub', { mode: 'redirect', errorUrl: '/oops?a=1' });

    expect(left().searchParams.get('errorUrl')).toBe('/oops?a=1');
  });
});
