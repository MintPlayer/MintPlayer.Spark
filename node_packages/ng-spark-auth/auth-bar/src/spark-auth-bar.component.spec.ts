import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { filter, firstValueFrom } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { SparkAuthBarComponent } from './spark-auth-bar.component';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

@Component({ standalone: true, template: '' })
class StubComponent {}

const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'somewhere', component: SparkAuthBarComponent },
];

function nextNavigationEnd(): Promise<NavigationEnd> {
  const router = TestBed.inject(Router);
  return firstValueFrom(router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd)));
}

async function setup() {
  const auth: any = {
    logout: vi.fn().mockResolvedValue(undefined),
    isAuthenticated: () => true,
    user: () => ({ isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [] }),
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

  it('does not navigate when logout rejects (current implementation has no try/catch)', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/somewhere', SparkAuthBarComponent);
    c.authService.logout = vi.fn().mockRejectedValue(new Error('network'));

    await expect(c.onLogout()).rejects.toThrow();
    expect(TestBed.inject(Router).url).toBe('/somewhere');
  });
});
