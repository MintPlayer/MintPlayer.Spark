import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';

import { SparkTwoFactorComponent } from './spark-two-factor.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

function configure(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { loginTwoFactor: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  const route = { snapshot: { queryParamMap: convertToParamMap({}) } };

  TestBed.configureTestingModule({
    imports: [SparkTwoFactorComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: ActivatedRoute, useValue: route },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  const fixture = TestBed.createComponent(SparkTwoFactorComponent);
  const router = TestBed.inject(Router);
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockReturnValue(Promise.resolve(true));
  return { fixture, auth, navigateByUrl };
}

describe('SparkTwoFactorComponent', () => {
  it('toggleRecoveryCode flips the useRecoveryCode signal and clears errorMessage', () => {
    const { fixture } = configure();
    const c = fixture.componentInstance;
    c.errorMessage.set('boom');

    c.toggleRecoveryCode();

    expect(c.useRecoveryCode()).toBe(true);
    expect(c.errorMessage()).toBe('');
  });

  it('shows enterCodeError when submitting an empty TOTP code', async () => {
    const { fixture, auth } = configure();
    await fixture.componentInstance.onSubmit();

    expect(auth.loginTwoFactor).not.toHaveBeenCalled();
    expect(fixture.componentInstance.errorMessage()).toBe('auth.enterCodeError');
  });

  it('shows enterRecoveryCodeError when submitting empty recovery code', async () => {
    const { fixture } = configure();
    fixture.componentInstance.toggleRecoveryCode();

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toBe('auth.enterRecoveryCodeError');
  });

  it('submits the TOTP code and navigates to defaultRedirectUrl on success', async () => {
    const { fixture, auth, navigateByUrl } = configure();
    fixture.componentInstance.form.setValue({ code: '123456', recoveryCode: '' });

    await fixture.componentInstance.onSubmit();

    expect(auth.loginTwoFactor).toHaveBeenCalledWith('123456', undefined);
    expect(navigateByUrl).toHaveBeenCalledWith(defaultSparkAuthConfig.defaultRedirectUrl);
  });

  it('submits the recovery code when in recovery mode', async () => {
    const { fixture, auth } = configure();
    fixture.componentInstance.toggleRecoveryCode();
    fixture.componentInstance.form.setValue({ code: '', recoveryCode: 'RECOVERY-CODE' });

    await fixture.componentInstance.onSubmit();

    expect(auth.loginTwoFactor).toHaveBeenCalledWith('', 'RECOVERY-CODE');
  });

  it('shows invalidCode error when loginTwoFactor rejects', async () => {
    const { fixture } = configure({ loginTwoFactor: vi.fn().mockRejectedValue(new Error('bad')) });
    fixture.componentInstance.form.setValue({ code: '000000', recoveryCode: '' });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toBe('auth.invalidCode');
  });
});
