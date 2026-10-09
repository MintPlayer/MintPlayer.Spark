import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkLoginComponent } from './spark-login.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark/auth/models';
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const routePaths = {
  login: '/login',
  twoFactor: '/login/two-factor',
  register: '/register',
  forgotPassword: '/forgot-password',
  resetPassword: '/reset-password',
};

const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'login', component: SparkLoginComponent },
  { path: 'login/two-factor', component: StubComponent },
  { path: 'protected', component: StubComponent },
];

/**
 * What the hosted `<spark-external-login-buttons>` reads from the service: no providers, no popup,
 * nothing in the URL. Tests about the providers override `capabilities` and friends.
 */
function externalLoginDefaults() {
  return {
    capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [] }),
    loginWithProvider: vi.fn().mockResolvedValue({ success: false, error: 'popup_closed' }),
    externalLoginPending: signal(false),
    takeExternalLoginResult: vi.fn(() => null),
  };
}

async function setup(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { ...externalLoginDefaults(), login: vi.fn().mockResolvedValue(undefined), ...authOverrides };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });

  const harness = await RouterTestingHarness.create();
  return { harness, auth };
}

describe('SparkLoginComponent', () => {
  it('starts with an invalid form (required fields)', async () => {
    const { harness } = await setup();
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);

    expect(component.form.invalid).toBe(true);
  });

  it('does not call auth.login when the form is invalid', async () => {
    const { harness, auth } = await setup();
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);

    await component.onSubmit();

    expect(auth.login).not.toHaveBeenCalled();
  });

  it('logs in and navigates to the configured default redirect URL on success', async () => {
    // Override defaultRedirectUrl to a non-root path so the navigation outcome is
    // unambiguous to assert against. (defaultSparkAuthConfig.defaultRedirectUrl is '/'.)
    TestBed.resetTestingModule();
    const auth: any = { ...externalLoginDefaults(), login: vi.fn().mockResolvedValue(undefined) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', pathMatch: 'full', component: StubComponent },
          { path: 'login', component: SparkLoginComponent },
          { path: 'dashboard', component: StubComponent },
        ]),
        { provide: SparkAuthService, useValue: auth },
        { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
        { provide: SPARK_AUTH_CONFIG, useValue: { ...defaultSparkAuthConfig, defaultRedirectUrl: '/dashboard' } },
        { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    const navigated = nextNavigationEnd();
    await component.onSubmit();
    await navigated;

    expect(auth.login).toHaveBeenCalledWith('a@b.c', 'pw', false);
    expect(TestBed.inject(Router).url).toBe('/dashboard');
  });

  it('passes a checked "Remember me" to auth.login', async () => {
    const { auth, harness } = await setup();
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: true });

    await component.onSubmit();

    expect(auth.login).toHaveBeenCalledWith('a@b.c', 'pw', true);
  });

  it('navigates to the returnUrl query param when present', async () => {
    const { harness } = await setup();
    const component = await harness.navigateByUrl('/login?returnUrl=%2Fprotected', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    const navigated = nextNavigationEnd();
    await component.onSubmit();
    await navigated;

    expect(TestBed.inject(Router).url).toBe('/protected');
  });

  it('redirects to the two-factor route on 401 with RequiresTwoFactor detail', async () => {
    const error = new HttpErrorResponse({ status: 401, error: { detail: 'RequiresTwoFactor' } });
    const { harness } = await setup({ login: vi.fn().mockRejectedValue(error) });
    const component = await harness.navigateByUrl('/login?returnUrl=%2Fprotected', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    const navigated = nextNavigationEnd();
    await component.onSubmit();
    await navigated;

    expect(TestBed.inject(Router).url).toBe('/login/two-factor?returnUrl=%2Fprotected');
  });

  it('shows the invalid-credentials error on a generic 401 (no navigation)', async () => {
    const { harness } = await setup({
      login: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 401 })),
    });
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'wrong', rememberMe: false });

    await component.onSubmit();

    expect(component.errorMessage()).toBe('auth.invalidCredentials');
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it('shows the invalid-credentials error on a non-HTTP failure too', async () => {
    const { harness } = await setup({ login: vi.fn().mockRejectedValue(new Error('network')) });
    const component = await harness.navigateByUrl('/login', SparkLoginComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    await component.onSubmit();

    expect(component.errorMessage()).toBe('auth.invalidCredentials');
    expect(component.loading()).toBe(false);
  });
});

/**
 * jsdom implements none of WebAuthn; the button is gated on browser support as well as on the
 * server's capability, so the browser half has to be faked for the button to appear at all.
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

describe('SparkLoginComponent passkeys', () => {
  afterEach(() => removeWebAuthn());

  const passkeyButton = (harness: RouterTestingHarness) =>
    Array.from(harness.routeNativeElement!.querySelectorAll('button'))
      .find((b) => b.textContent!.includes('auth.signInWithPasskey'));

  async function open(url: string, auth: Record<string, unknown>) {
    const { harness } = await setup({
      capabilities: vi.fn().mockResolvedValue({ passkeys: true, localCredentials: 'Full', externalProviders: [] }),
      signInWithPasskey: vi.fn().mockResolvedValue({ success: true }),
      ...auth,
    } as any);
    const component = await harness.navigateByUrl(url, SparkLoginComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();
    return { harness, component };
  }

  it('offers the passkey button when the server has passkeys and the browser supports them', async () => {
    installWebAuthn();
    const { harness, component } = await open('/login', {});

    expect(component.passkeysAvailable()).toBe(true);
    expect(passkeyButton(harness)).toBeDefined();
  });

  it('hides the passkey button when the browser cannot run the ceremony', async () => {
    const { harness, component } = await open('/login', {});

    expect(component.passkeysAvailable()).toBe(false);
    expect(passkeyButton(harness)).toBeUndefined();
  });

  it('hides the passkey button when the server has not enabled passkeys', async () => {
    installWebAuthn();
    const { harness } = await open('/login', {
      capabilities: vi.fn().mockResolvedValue({ passkeys: false, localCredentials: 'Full', externalProviders: [] }),
    });

    expect(passkeyButton(harness)).toBeUndefined();
  });

  it('keeps the password form usable when the capability lookup fails', async () => {
    installWebAuthn();
    const { harness, component } = await open('/login', {
      capabilities: vi.fn().mockRejectedValue(new Error('network')),
    });

    expect(component.passkeysAvailable()).toBe(false);
    expect(passkeyButton(harness)).toBeUndefined();
    expect(harness.routeNativeElement!.querySelector('button[type="submit"]')).not.toBeNull();
  });

  it('signs in with a passkey from the button and lands on the sanitized returnUrl', async () => {
    installWebAuthn();
    const signInWithPasskey = vi.fn().mockResolvedValue({ success: true });
    const { harness } = await open('/login?returnUrl=%2Fprotected', { signInWithPasskey });

    const navigated = nextNavigationEnd();
    passkeyButton(harness)!.click();
    await navigated;

    expect(signInWithPasskey).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(Router).url).toBe('/protected');
  });

  it('drops an off-site returnUrl after a passkey sign-in and falls back to the default', async () => {
    const { component } = await open('/login?returnUrl=%2F%2Fevil.example', {});
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    await component.signInWithPasskey();

    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('treats a dismissed passkey prompt as "not now": no error, no navigation', async () => {
    const { component } = await open('/login', {
      signInWithPasskey: vi.fn().mockResolvedValue({ success: false, error: 'cancelled' }),
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    await component.signInWithPasskey();

    expect(component.errorMessage()).toBe('');
    expect(navigate).not.toHaveBeenCalled();
    expect(component.passkeyBusy()).toBe(false);
  });

  it.each([
    ['locked_out', 'auth.lockedOut'],
    ['failed', 'auth.passkeyFailed'],
    ['no_credential', 'auth.passkeyFailed'],
  ])('maps a %s passkey failure to %s and stays on the page', async (error, key) => {
    const { component } = await open('/login', {
      signInWithPasskey: vi.fn().mockResolvedValue({ success: false, error }),
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    await component.signInWithPasskey();

    expect(component.errorMessage()).toBe(key);
    expect(navigate).not.toHaveBeenCalled();
    expect(component.passkeyBusy()).toBe(false);
  });

  it('clears a previous error and marks itself busy while the ceremony runs', async () => {
    let resolve!: (value: unknown) => void;
    const { component } = await open('/login', {
      signInWithPasskey: vi.fn(() => new Promise((r) => (resolve = r))),
    });
    component.errorMessage.set('auth.invalidCredentials');

    const running = component.signInWithPasskey();

    expect(component.errorMessage()).toBe('');
    expect(component.passkeyBusy()).toBe(true);
    resolve({ success: false, error: 'cancelled' });
    await running;
    expect(component.passkeyBusy()).toBe(false);
  });
});

describe('SparkLoginComponent identifier field', () => {
  async function open(signInIdentifiers: string[] | undefined) {
    const { harness } = await setup({
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [], signInIdentifiers }),
    } as any);
    await harness.navigateByUrl('/login', SparkLoginComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();
    const root = harness.routeNativeElement!;
    return {
      label: root.querySelector('label[for="email"]')!.textContent!.trim(),
      input: root.querySelector('input#email') as HTMLInputElement,
    };
  }

  it.each([
    [['email'], 'auth.email', 'email', 'email'],
    [['userName'], 'auth.userName', 'text', 'username'],
    [['email', 'userName'], 'auth.emailOrUserName', 'text', 'username'],
    // A server older than the option omits it, and accepts both.
    [undefined, 'auth.emailOrUserName', 'text', 'username'],
  ])('labels the field from the server\'s sign-in identifiers %j', async (identifiers, label, type, autocomplete) => {
    const field = await open(identifiers);

    expect(field.label).toBe(label);
    expect(field.input.type).toBe(type);
    expect(field.input.getAttribute('autocomplete')).toBe(autocomplete);
  });
});

/** The login page offers the same external providers as the sign-in landing page (#490). */
describe('SparkLoginComponent external providers', () => {
  const github = { scheme: 'GitHub', displayName: 'GitHub' };
  const google = { scheme: 'Google', displayName: 'Google' };

  async function open(auth: Record<string, unknown>, url = '/login') {
    const { harness, auth: service } = await setup(auth as any);
    const component = await harness.navigateByUrl(url, SparkLoginComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();
    return { harness, component, auth: service };
  }

  const providerButtons = (harness: RouterTestingHarness) =>
    Array.from(harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('.spark-provider-button'));

  it('shows a button per reported provider, above the password form, with an "or" divider', async () => {
    const { harness } = await open({
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [github, google] }),
    });

    expect(providerButtons(harness).map((b) => b.textContent!.trim())).toEqual(['GitHub', 'Google']);
    const root = harness.routeNativeElement!;
    const divider = root.querySelector('.spark-login-or');
    expect(divider?.textContent).toContain('auth.or');
    // Buttons, then the divider, then the form.
    const form = root.querySelector('form')!;
    expect(providerButtons(harness)[1].compareDocumentPosition(divider!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(divider!.compareDocumentPosition(form) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('shows no provider button and no divider when the server reports none', async () => {
    const { harness } = await open({});

    expect(providerButtons(harness)).toHaveLength(0);
    expect(harness.routeNativeElement!.querySelector('.spark-login-or')).toBeNull();
    expect(harness.routeNativeElement!.querySelector('button[type="submit"]')).not.toBeNull();
  });

  it('signs in with the clicked provider and the sanitized returnUrl', async () => {
    const { harness, auth } = await open({
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [github, google] }),
    }, '/login?returnUrl=%2Fprotected');

    providerButtons(harness)[1].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google', { returnUrl: '/protected' });
  });

  it('shows the error of a failed provider sign-in, and keeps the password form', async () => {
    const { harness, auth } = await open({
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [github] }),
    });
    auth.loginWithProvider.mockResolvedValue({ success: false, error: 'email_not_verified' });

    providerButtons(harness)[0].click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(harness.routeNativeElement!.querySelector('.spark-external-error')?.textContent)
      .toContain('auth.externalLoginError.email_not_verified');
    expect(harness.routeNativeElement!.querySelector('button[type="submit"]')).not.toBeNull();
  });

  it('reads the redirect result (?sparkExternalLogin) once', async () => {
    // Only the buttons read it; the page itself does not, so a failure cannot show twice.
    const { auth } = await open({
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [github] }),
    });

    expect(auth.takeExternalLoginResult).toHaveBeenCalledTimes(1);
  });
});
