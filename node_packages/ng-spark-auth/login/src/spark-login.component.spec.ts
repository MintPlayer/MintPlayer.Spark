import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkLoginComponent } from './spark-login.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';
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

async function setup(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { login: vi.fn().mockResolvedValue(undefined), ...authOverrides };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
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
    const auth: any = { login: vi.fn().mockResolvedValue(undefined) };
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

    expect(auth.login).toHaveBeenCalledWith('a@b.c', 'pw');
    expect(TestBed.inject(Router).url).toBe('/dashboard');
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
});
