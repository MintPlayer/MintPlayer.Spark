import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkAuthBarComponent } from './spark-auth-bar.component';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'somewhere', component: SparkAuthBarComponent },
];

async function setup() {
  const auth: any = {
    logout: vi.fn().mockResolvedValue(undefined),
    isAuthenticated: () => true,
    user: () => ({ isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [] }),
    // The bar asks the server whether passkeys are offered, so the double has to answer. Passkeys
    // off by default here: these tests are about the user/logout branch, and a double that silently
    // enabled an unrelated feature would make them assert more than they name.
    capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [] }),
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SparkAuthService, useValue: auth },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, auth };
}

describe('SparkAuthBarComponent', () => {
  it('exposes the SparkAuthService for template use', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);

    expect(c.authService).toBe(auth);
  });

  it('onLogout calls authService.logout and navigates back to root', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);

    const navigated = nextNavigationEnd();
    await c.onLogout();
    await navigated;

    expect(auth.logout).toHaveBeenCalledOnce();
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('still navigates back to root when authService.logout rejects', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    c.authService.logout = vi.fn().mockRejectedValue(new Error('network'));

    const navigated = nextNavigationEnd();
    await expect(c.onLogout()).rejects.toThrow('network');
    await navigated;

    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('does not offer passkeys when the server does not report them', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    await auth.capabilities.mock.results[0]?.value;

    expect(c.showPasskeys()).toBe(false);
  });

  it('does not offer passkeys when the capabilities call fails', async () => {
    // A refused or unreachable capabilities endpoint must leave the link hidden rather than
    // guessed: offering a credential page the server will not serve is worse than omitting it.
    const { harness, auth } = await setup();
    auth.capabilities = vi.fn().mockRejectedValue(new Error('offline'));
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);

    expect(c.showPasskeys()).toBe(false);
  });

  // ⚠️ The *positive* case is deliberately not asserted here. showPasskeys() also requires
  // passkeysSupported(), which needs a secure context and the real PublicKeyCredential JSON helpers
  // — jsdom has none of them, so a passing test would have to fake the browser API it is meant to be
  // checking. That path is covered against a real browser instead.
});
