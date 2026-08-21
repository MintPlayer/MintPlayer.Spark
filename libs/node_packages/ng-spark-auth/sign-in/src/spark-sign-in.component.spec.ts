import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkSignInComponent } from './spark-sign-in.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  SparkAuthCapabilities,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';
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
async function setup(capabilitiesImpl: () => Promise<SparkAuthCapabilities>) {
  const auth: any = {
    capabilities: vi.fn(capabilitiesImpl),
    loginWithProvider: vi.fn().mockResolvedValue(undefined),
  };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      // bs-alert binds a synthetic animation property, so rendering either alert branch throws
      // without an animations provider. Noop rather than real animations: the assertions are about
      // which branch rendered, not about how it got there.
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });

  const harness = await RouterTestingHarness.create();
  const component = await harness.navigateByUrl('/sign-in', SparkSignInComponent);
  harness.detectChanges();
  return { harness, component, auth };
}

const text = (harness: RouterTestingHarness) => harness.routeNativeElement!.textContent ?? '';
const buttons = (harness: RouterTestingHarness) =>
  Array.from(harness.routeNativeElement!.querySelectorAll('button'));

describe('SparkSignInComponent', () => {
  it('renders a button per provider reported by the server', async () => {
    const { harness } = await setup(async () => capabilities({ externalProviders: [github, google] }));

    expect(buttons(harness).map((b) => b.textContent!.trim())).toEqual(['GitHub', 'Google']);
  });

  it('signs in with the scheme the server reported, not a hard-coded string', async () => {
    // The reason the component exists: every consumer that hand-rolled this wrote the scheme as a
    // literal, which silently mismatches the moment the server's registration changes.
    const { harness, auth } = await setup(async () => capabilities({ externalProviders: [google] }));

    buttons(harness)[0].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google');
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
});
