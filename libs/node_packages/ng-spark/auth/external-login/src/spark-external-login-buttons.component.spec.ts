import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, Routes } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkExternalLoginButtonsComponent } from './spark-external-login-buttons.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_EXTERNAL_PROVIDERS,
  SparkAuthCapabilities,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark/auth/models';
import { StubComponent } from '../../src/test-utils';

/**
 * Routed straight to the component: what it reads from the route (`?returnUrl`, `?sparkExternalLogin`)
 * is part of what is under test. Projection through a host is covered by the sign-in page's
 * projection spec, which passes its inputs through to this component.
 */
const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'sign-in', component: SparkExternalLoginButtonsComponent },
];

const github = { scheme: 'GitHub', displayName: 'GitHub' };
const google = { scheme: 'Google', displayName: 'Google' };

function capabilities(overrides: Partial<SparkAuthCapabilities> = {}): SparkAuthCapabilities {
  return { localCredentials: 'Disabled', externalProviders: [github], ...overrides } as SparkAuthCapabilities;
}

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
      // eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic props
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
    ],
  });

  const harness = await RouterTestingHarness.create();
  const component = await harness.navigateByUrl(url, SparkExternalLoginButtonsComponent);
  await harness.fixture.whenStable();
  harness.detectChanges();
  return { harness, component, auth };
}

const buttons = (harness: RouterTestingHarness) =>
  Array.from(harness.routeNativeElement!.querySelectorAll('button'));

describe('SparkExternalLoginButtonsComponent', () => {
  it('renders a button per provider reported by the server', async () => {
    const { harness, component } = await setup(async () => capabilities({ externalProviders: [github, google] }));

    expect(buttons(harness).map((b) => b.textContent!.trim())).toEqual(['GitHub', 'Google']);
    expect(component.hasProviders()).toBe(true);
  });

  it('renders nothing when the server reports no providers', async () => {
    const { harness, component } = await setup(async () => capabilities({ externalProviders: [] }));

    expect(component.hasProviders()).toBe(false);
    expect(harness.routeNativeElement!.children).toHaveLength(0);
  });

  it('renders nothing while the capabilities load, nor when they fail', async () => {
    let reject!: (reason: unknown) => void;
    const pending = new Promise<SparkAuthCapabilities>((_, r) => (reject = r));
    const { harness, component } = await setup(() => pending);

    expect(harness.routeNativeElement!.children).toHaveLength(0);

    reject(new Error('network'));
    await pending.catch(() => undefined);
    await harness.fixture.whenStable();
    harness.detectChanges();

    // The host owns that failure: the sign-in page says sign-in is unavailable, the login page keeps
    // its password form.
    expect(component.loaded()).toBe(false);
    expect(harness.routeNativeElement!.children).toHaveLength(0);
  });

  it('signs in with the scheme the server reported, not a hard-coded string', async () => {
    // Every consumer that hand-rolled this wrote the scheme as a literal, which silently mismatches
    // the moment the server's registration changes.
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [google] }));

    buttons(harness)[0].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google', { returnUrl: '/' });
  });

  it('leaves the page once sign-in succeeds', async () => {
    // The bug this pins: in popup mode the returnUrl is consumed by the POPUP — it tells the server
    // where to send that window before it closes — so the opener, which is the tab the user is
    // actually looking at, was never touched. Sign-in worked, the topbar flipped to the signed-in
    // state, and the user sat on the login page wondering whether it had.
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [google] }));
    auth.loginWithProvider.mockResolvedValue({ success: true });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    buttons(harness)[0].click();
    await harness.fixture.whenStable();

    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('stays put when sign-in fails, and shows the translated error', async () => {
    // Navigating away on failure would hide the very message this component just rendered.
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [google] }));
    auth.loginWithProvider.mockResolvedValue({ success: false, error: 'email_not_verified' });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    buttons(harness)[0].click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(navigate).not.toHaveBeenCalled();
    const alert = harness.routeNativeElement!.querySelector('.spark-external-error');
    expect(alert?.textContent).toContain('auth.externalLoginError.email_not_verified');
  });

  it("shows 'link_confirmation_sent' as news, not as an error", async () => {
    const { harness, auth, component } = await setup(async () => capabilities({ externalProviders: [google] }));
    auth.loginWithProvider.mockResolvedValue({ success: false, error: 'link_confirmation_sent' });

    buttons(harness)[0].click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    const alert = harness.routeNativeElement!.querySelector('bs-alert.spark-external-error');
    expect(alert?.textContent).toContain('auth.externalLoginError.link_confirmation_sent');
    expect(component.externalErrorIsNotice()).toBe(true);
  });

  it("shows nothing for 'popup_closed': it is \"not now\", not an error", async () => {
    const { harness, component } = await setup(async () => capabilities({ externalProviders: [google] }));

    await component.signInWith(component.providers()[0]);
    harness.detectChanges();

    expect(component.externalError()).toBe('');
    expect(harness.routeNativeElement!.querySelector('.spark-external-error')).toBeNull();
  });

  it('clears the previous error when a new attempt starts', async () => {
    const { harness, auth, component } = await setup(async () => capabilities({ externalProviders: [google] }));
    auth.loginWithProvider.mockResolvedValueOnce({ success: false, error: 'remote_failure' });
    await component.signInWith(component.providers()[0]);
    expect(component.externalError()).toBe('auth.externalLoginError.remote_failure');

    let resolve!: (value: unknown) => void;
    auth.loginWithProvider.mockReturnValueOnce(new Promise((r) => (resolve = r)));
    const running = component.signInWith(component.providers()[0]);
    harness.detectChanges();

    expect(component.externalError()).toBe('');
    resolve({ success: false, error: 'popup_closed' });
    await running;
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

  it('disables the provider buttons and shows a spinner while a popup is pending', async () => {
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [github, google] }));

    auth.externalLoginPending.set(true);
    harness.detectChanges();
    expect(buttons(harness).every((b) => b.disabled)).toBe(true);
    expect(harness.routeNativeElement!.querySelector('.spark-external-pending bs-spinner')).not.toBeNull();

    // D2: the popup was seen closed (or COOP made it look so); the buttons come back by themselves.
    auth.externalLoginPending.set(false);
    harness.detectChanges();
    expect(buttons(harness).some((b) => b.disabled)).toBe(false);
    expect(harness.routeNativeElement!.querySelector('.spark-external-pending')).toBeNull();
  });

  it("offers \"Continue in this tab\" on a blocked popup, which retries the same provider in redirect mode", async () => {
    // No automatic fallback (F10): the user chooses the redirect.
    const { harness, auth } = await setup(
      async () => capabilities({ externalProviders: [github, google] }), {}, '/sign-in?returnUrl=%2Fprojects');
    auth.loginWithProvider.mockResolvedValueOnce({ success: false, error: 'popup_blocked' });

    buttons(harness)[1].click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(harness.routeNativeElement!.querySelector('.spark-external-error')?.textContent)
      .toContain('auth.externalLoginError.popup_blocked');
    const retry = harness.routeNativeElement!.querySelector<HTMLButtonElement>('.spark-continue-in-tab');
    expect(retry).not.toBeNull();
    expect(retry!.textContent).toContain('auth.continueInThisTab');

    auth.loginWithProvider.mockReturnValueOnce(new Promise(() => { /* the page is going away */ }));
    retry!.click();

    expect(auth.loginWithProvider).toHaveBeenLastCalledWith('Google', { returnUrl: '/projects', mode: 'redirect' });
  });

  it('offers no "Continue in this tab" for any other failure', async () => {
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [google] }));
    auth.loginWithProvider.mockResolvedValue({ success: false, error: 'remote_failure' });

    buttons(harness)[0].click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(harness.routeNativeElement!.querySelector('.spark-continue-in-tab')).toBeNull();
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

  it('drops an off-site returnUrl from the query string rather than following it', async () => {
    const { harness, auth } = await setup(
      async () => capabilities({ externalProviders: [google] }), {}, '/sign-in?returnUrl=%2F%2Fevil.example');

    buttons(harness)[0].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google', { returnUrl: '/' });
  });

  it('puts the declared icon on the provider button', async () => {
    TestBed.overrideProvider(SPARK_EXTERNAL_PROVIDERS, { useValue: [{ scheme: 'github', iconClass: 'bi bi-github' }] });
    const { harness } = await setup(async () => capabilities({ externalProviders: [github] }));

    expect(harness.routeNativeElement!.querySelector('button i.bi-github')).not.toBeNull();
  });

  it('applies a declared display name and ordering, and drops a declared provider the server did not report', async () => {
    // A declaration decorates; it cannot conjure a provider.
    TestBed.overrideProvider(SPARK_EXTERNAL_PROVIDERS, {
      useValue: [
        { scheme: 'google', displayName: 'Google Workspace', order: 1 },
        { scheme: 'Facebook', displayName: 'Facebook' },
      ],
    });
    const { harness } = await setup(async () => capabilities({ externalProviders: [github, google] }));

    expect(buttons(harness).map((b) => b.textContent!.trim())).toEqual(['Google Workspace', 'GitHub']);
  });
});
