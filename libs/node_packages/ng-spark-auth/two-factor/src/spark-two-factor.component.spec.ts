import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkTwoFactorComponent } from './spark-two-factor.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

const routes: Routes = [
  { path: 'login/two-factor', component: SparkTwoFactorComponent },
  { path: 'dashboard', component: StubComponent },
];

async function setup(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { loginTwoFactor: vi.fn().mockResolvedValue(undefined), ...authOverrides };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: { ...defaultSparkAuthConfig, defaultRedirectUrl: '/dashboard' } },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, auth };
}

describe('SparkTwoFactorComponent', () => {
  it('toggleRecoveryCode flips the useRecoveryCode signal and clears errorMessage', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);
    c.errorMessage.set('boom');

    c.toggleRecoveryCode();

    expect(c.useRecoveryCode()).toBe(true);
    expect(c.errorMessage()).toBe('');
  });

  it('shows enterCodeError when submitting an empty TOTP code', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);

    await c.onSubmit();

    expect(auth.loginTwoFactor).not.toHaveBeenCalled();
    expect(c.errorMessage()).toBe('auth.enterCodeError');
  });

  it('shows enterRecoveryCodeError when submitting empty recovery code', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);
    c.toggleRecoveryCode();

    await c.onSubmit();

    expect(c.errorMessage()).toBe('auth.enterRecoveryCodeError');
  });

  it('submits the TOTP code and navigates to the configured default redirect URL on success', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);
    c.form.setValue({ code: '123456', recoveryCode: '' });

    const navigated = nextNavigationEnd();
    await c.onSubmit();
    await navigated;

    expect(auth.loginTwoFactor).toHaveBeenCalledWith('123456', undefined);
    expect(TestBed.inject(Router).url).toBe('/dashboard');
  });

  it('submits the recovery code when in recovery mode', async () => {
    const { harness, auth } = await setup();
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);
    c.toggleRecoveryCode();
    c.form.setValue({ code: '', recoveryCode: 'RECOVERY-CODE' });

    const navigated = nextNavigationEnd();
    await c.onSubmit();
    await navigated;

    expect(auth.loginTwoFactor).toHaveBeenCalledWith('', 'RECOVERY-CODE');
  });

  it('shows invalidCode error when loginTwoFactor rejects (no navigation)', async () => {
    const { harness } = await setup({ loginTwoFactor: vi.fn().mockRejectedValue(new Error('bad')) });
    const c = await harness.navigateByUrl('/login/two-factor', SparkTwoFactorComponent);
    c.form.setValue({ code: '000000', recoveryCode: '' });

    await c.onSubmit();

    expect(c.errorMessage()).toBe('auth.invalidCode');
    expect(TestBed.inject(Router).url).toBe('/login/two-factor');
  });
});
