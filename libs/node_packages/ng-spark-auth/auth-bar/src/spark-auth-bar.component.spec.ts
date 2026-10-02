import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkAuthBarComponent } from './spark-auth-bar.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
import { sparkAuthRoutes, withAccount, withLocalLogin } from '@mintplayer/ng-spark-auth/routes';
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const baseRoutes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'somewhere', component: SparkAuthBarComponent },
];

async function setup(extraRoutes: Routes = []) {
  const auth: any = {
    logout: vi.fn().mockResolvedValue(undefined),
    isAuthenticated: () => true,
    user: () => ({ isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [] }),
  };
  TestBed.configureTestingModule({
    providers: [
      // The bar sits in the shell, outside the sparkAuthRoutes() subtree — the shape these tests keep.
      provideRouter([...baseRoutes, ...extraRoutes]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, auth };
}

const buttons = (harness: RouterTestingHarness) =>
  [...(harness.routeNativeElement as HTMLElement).querySelectorAll('bs-button-group .btn')]
    .map(b => ({ text: b.textContent?.trim(), href: b.getAttribute('href') }));

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

  it('groups Account and Logout, Account first, when withAccount() is mounted', async () => {
    const { harness } = await setup(sparkAuthRoutes(withLocalLogin(), withAccount()));
    await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    harness.detectChanges();

    expect(buttons(harness)).toEqual([
      { text: 'auth.account', href: '/account' },
      { text: 'auth.logout', href: null },
    ]);
  });

  it('follows a custom account path, read from the router configuration', async () => {
    const { harness } = await setup(sparkAuthRoutes(withAccount({ account: 'me' })));
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);

    expect(c.accountUrl).toBe('/me');
  });

  it('offers logout alone when withAccount() is not mounted', async () => {
    const { harness } = await setup(sparkAuthRoutes(withLocalLogin()));
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    harness.detectChanges();

    expect(c.accountUrl).toBeUndefined();
    expect(buttons(harness)).toEqual([{ text: 'auth.logout', href: null }]);
  });

  it('no longer renders the user name in the bar', async () => {
    const { harness } = await setup(sparkAuthRoutes(withAccount()));
    await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    harness.detectChanges();

    expect((harness.routeNativeElement as HTMLElement).textContent).not.toContain('jane');
  });
});
