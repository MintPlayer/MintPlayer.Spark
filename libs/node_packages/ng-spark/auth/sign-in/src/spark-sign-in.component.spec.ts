import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, Routes } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- see the provider below
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkSignInComponent } from './spark-sign-in.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  SPARK_EXTERNAL_PROVIDERS,
  SparkAuthCapabilities,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark/auth/models';
import { StubComponent } from '../../src/test-utils';

const routePaths = {
  login: '/login',
  twoFactor: '/login/two-factor',
  register: '/register',
  forgotPassword: '/forgot-password',
  resetPassword: '/reset-password',
  signIn: '/sign-in',
};

const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'sign-in', component: SparkSignInComponent },
  { path: 'login', component: StubComponent },
];

const github = { scheme: 'GitHub', displayName: 'GitHub' };
const google = { scheme: 'Google', displayName: 'Google' };

function capabilities(overrides: Partial<SparkAuthCapabilities> = {}): SparkAuthCapabilities {
  return { localCredentials: 'Disabled', externalProviders: [github], ...overrides } as SparkAuthCapabilities;
}

/**
 * `capabilities` is a promise the component awaits in its constructor, so a test that wants to
 * observe the *loading* state must hand over a promise it controls rather than a resolved one.
 */
async function setup(
  capabilitiesImpl: () => Promise<SparkAuthCapabilities>,
  authOverrides: Record<string, unknown> = {},
  url = '/sign-in',
) {
  const auth: any = {
    capabilities: vi.fn(capabilitiesImpl),
    loginWithProvider: vi.fn().mockResolvedValue({ success: false, error: 'popup_closed' }),
    externalLoginPending: signal(false),
    // The real implementation against the harness's real Router: reading and stripping
    // ?sparkExternalLogin is the part under test, not a stub of it.
    takeExternalLoginResult: vi.fn(() =>
      (SparkAuthService.prototype.takeExternalLoginResult as () => unknown).call({ injector: TestBed.inject(Injector) })),
    ...authOverrides,
  };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      // bs-alert binds a synthetic animation property, so rendering either alert branch throws
      // ("Found the synthetic property @…") without an animations provider. Noop rather than real
      // animations: the assertions are about which branch rendered, not how it got there.
      //
      // Deprecated, unavoidably. The whole provider family is deprecated as of Angular 20.2 in
      // favour of animate.enter/animate.leave, with intent to remove in v23 — but that migration
      // belongs to @mintplayer/ng-bootstrap, which still emits synthetic props. Every demo app in
      // this repo carries the same deprecated call for the same reason.
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });

  const harness = await RouterTestingHarness.create();
  const component = await harness.navigateByUrl(url, SparkSignInComponent);
  harness.detectChanges();
  return { harness, component, auth };
}

const text = (harness: RouterTestingHarness) => harness.routeNativeElement!.textContent ?? '';
const buttons = (harness: RouterTestingHarness) =>
  Array.from(harness.routeNativeElement!.querySelectorAll('button'));

/**
 * The provider buttons' own behaviour (pending state, errors, "Continue in this tab", returnUrl
 * validation, declared presentation) is specified by `<spark-external-login-buttons>`'s spec; these
 * cover what this page adds around it, and that the buttons are wired in.
 */
describe('SparkSignInComponent', () => {
  it('renders a button per provider reported by the server', async () => {
    const { harness } = await setup(async () => capabilities({ externalProviders: [github, google] }));

    expect(buttons(harness).map((b) => b.textContent!.trim())).toEqual(['GitHub', 'Google']);
  });

  it('reads ?sparkExternalLogin on load, shows it, and strips it from the URL', async () => {
    // Redirect mode (inside an installed web app, or "Continue in this tab") brings the failure back
    // as a query parameter (D3).
    const { harness, auth } = await setup(
      async () => capabilities({ externalProviders: [google] }), {}, '/sign-in?returnUrl=%2Fprojects&sparkExternalLogin=no_login_info');
    // The strip is a deferred replaceUrl navigation.
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(auth.takeExternalLoginResult).toHaveBeenCalledTimes(1);
    expect(harness.routeNativeElement!.querySelector('.spark-external-error')?.textContent)
      .toContain('auth.externalLoginError.no_login_info');
    // Stripped with replaceUrl, so a reload or Back does not show it again; returnUrl survives.
    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Fprojects');
  });

  it('reports that sign-in is unavailable when capabilities cannot be loaded', async () => {
    // An empty page and a failed fetch look identical to a user, so the two must be distinguishable.
    const { harness } = await setup(async () => {
      throw new Error('network');
    });

    expect(text(harness)).toContain('auth.signInUnavailable');
    expect(buttons(harness)).toHaveLength(0);
  });

  it('reports that there are no sign-in methods when the provider list is empty', async () => {
    const { harness } = await setup(async () => capabilities({ externalProviders: [] }));

    expect(text(harness)).toContain('auth.noSignInMethods');
    expect(text(harness)).not.toContain('auth.signInUnavailable');
  });

  it('shows a spinner while capabilities are in flight, and no alert', async () => {
    let resolve!: (value: SparkAuthCapabilities) => void;
    const pending = new Promise<SparkAuthCapabilities>((r) => (resolve = r));
    const { harness, component } = await setup(() => pending);

    expect(component.loading()).toBe(true);
    expect(harness.routeNativeElement!.querySelector('bs-spinner')).not.toBeNull();
    expect(text(harness)).not.toContain('auth.noSignInMethods');
    expect(text(harness)).not.toContain('auth.signInUnavailable');

    resolve(capabilities());
    await pending;
  });

  it('offers the password form when local credentials are only limited, not disabled', async () => {
    const { harness } = await setup(async () => capabilities({ localCredentials: 'SignInOnly' }));

    const link = harness.routeNativeElement!.querySelector('a');
    expect(link).not.toBeNull();
    expect(link!.getAttribute('href')).toBe('/login');
  });

  it('offers no password form when local credentials are disabled', async () => {
    // A link to a route the app did not mount is the failure this guards: sparkAuthRoutes omits the
    // login page entirely in disabled mode.
    const { harness } = await setup(async () => capabilities({ localCredentials: 'Disabled' }));

    expect(harness.routeNativeElement!.querySelector('a')).toBeNull();
  });

  it('warns when the app routes this page but the server still allows full local credentials', async () => {
    // Routes are a build-time decision, the server's mode a deployment-time one; they can disagree
    // with nothing failing.
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

    await setup(async () => capabilities({ localCredentials: 'Full' }));

    expect(warn).toHaveBeenCalledWith(expect.stringContaining('localCredentials = Full'));
    warn.mockRestore();
  });

  it('signs in with the safe returnUrl from the query string', async () => {
    const { harness, auth } = await setup(
      async () => capabilities({ externalProviders: [google] }), {}, '/sign-in?returnUrl=%2Fprojects');
    auth.loginWithProvider.mockResolvedValue({ success: true });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    buttons(harness)[0].click();
    await harness.fixture.whenStable();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google', { returnUrl: '/projects' });
    expect(navigate).toHaveBeenCalledWith('/projects');
  });

});

/**
 * jsdom implements none of WebAuthn; `passkeysAvailable` needs browser support as well as the
 * server's capability, so the browser half is faked for the button to appear at all.
 */
function installWebAuthn() {
  (globalThis as Record<string, unknown>)['PublicKeyCredential'] = Object.assign(function () { }, {
    parseCreationOptionsFromJSON: vi.fn(),
    parseRequestOptionsFromJSON: vi.fn(),
  });
  Object.defineProperty(globalThis.navigator, 'credentials', {
    value: { create: vi.fn(), get: vi.fn() },
    configurable: true,
  });
  Object.defineProperty(globalThis.window, 'isSecureContext', { value: true, configurable: true });
}

function removeWebAuthn() {
  delete (globalThis as Record<string, unknown>)['PublicKeyCredential'];
  Object.defineProperty(globalThis.navigator, 'credentials', { value: undefined, configurable: true });
  Object.defineProperty(globalThis.window, 'isSecureContext', { value: false, configurable: true });
}

describe('SparkSignInComponent passkeys', () => {
  afterEach(() => removeWebAuthn());

  const passkeyButton = (harness: RouterTestingHarness) =>
    buttons(harness).find((b) => b.textContent!.includes('auth.signInWithPasskey'));

  async function open(auth: Record<string, unknown> = {}, url = '/sign-in', externalProviders = [github]) {
    installWebAuthn();
    return setup(
      async () => capabilities({ passkeys: true, externalProviders } as Partial<SparkAuthCapabilities>),
      { signInWithPasskey: vi.fn().mockResolvedValue({ success: true }), ...auth },
      url,
    );
  }

  it('offers the passkey button, and does not claim there are no sign-in methods without providers', async () => {
    // A passkey is a sign-in method: with it available, an empty provider list is not "nothing".
    const { harness, component } = await open({}, '/sign-in', []);

    expect(component.passkeysAvailable()).toBe(true);
    expect(passkeyButton(harness)).toBeDefined();
    expect(text(harness)).not.toContain('auth.noSignInMethods');
  });

  it('offers no passkey button when the browser cannot run the ceremony', async () => {
    const { harness, component } = await setup(
      async () => capabilities({ passkeys: true } as Partial<SparkAuthCapabilities>));

    expect(component.passkeysAvailable()).toBe(false);
    expect(passkeyButton(harness)).toBeUndefined();
  });

  it('signs in with a passkey and navigates to the returnUrl from the query string', async () => {
    const { harness, auth } = await open({}, '/sign-in?returnUrl=%2Fprojects');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    passkeyButton(harness)!.click();
    await harness.fixture.whenStable();

    expect(auth.signInWithPasskey).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/projects');
  });

  it('treats a dismissed passkey prompt as "not now": no error, no navigation', async () => {
    const { harness, component } = await open({
      signInWithPasskey: vi.fn().mockResolvedValue({ success: false, error: 'cancelled' }),
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    await component.signInWithPasskey();
    harness.detectChanges();

    expect(component.passkeyError()).toBe('');
    expect(navigate).not.toHaveBeenCalled();
    expect(passkeyButton(harness)!.disabled).toBe(false);
  });

  it.each([
    ['locked_out', 'auth.lockedOut'],
    ['failed', 'auth.passkeyFailed'],
    ['no_credential', 'auth.passkeyFailed'],
  ])('renders a %s passkey failure as %s and stays on the page', async (error, key) => {
    const { harness, component } = await open({
      signInWithPasskey: vi.fn().mockResolvedValue({ success: false, error }),
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    await component.signInWithPasskey();
    harness.detectChanges();

    expect(component.passkeyError()).toBe(key);
    expect(text(harness)).toContain(key);
    expect(navigate).not.toHaveBeenCalled();
    expect(component.passkeyBusy()).toBe(false);
  });

  it('disables the passkey button while the ceremony runs, and clears the previous error', async () => {
    let resolve!: (value: unknown) => void;
    const { harness, component } = await open({
      signInWithPasskey: vi.fn(() => new Promise((r) => (resolve = r))),
    });
    component.passkeyError.set('auth.passkeyFailed');

    const running = component.signInWithPasskey();
    harness.detectChanges();

    expect(component.passkeyError()).toBe('');
    expect(passkeyButton(harness)!.disabled).toBe(true);
    resolve({ success: false, error: 'cancelled' });
    await running;
    harness.detectChanges();
    expect(passkeyButton(harness)!.disabled).toBe(false);
  });
});
