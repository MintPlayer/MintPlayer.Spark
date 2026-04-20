import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkResetPasswordComponent } from './spark-reset-password.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

function configure(
  authOverrides: Partial<SparkAuthService> = {},
  queryParams: Record<string, string> = { email: 'a@b.c', code: 'CODE-123' },
) {
  const auth: any = { resetPassword: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  const route = { snapshot: { queryParamMap: convertToParamMap(queryParams) } };

  TestBed.configureTestingModule({
    imports: [SparkResetPasswordComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: ActivatedRoute, useValue: route },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  return { fixture: TestBed.createComponent(SparkResetPasswordComponent), auth };
}

describe('SparkResetPasswordComponent', () => {
  it('shows invalidResetLink error when query params are missing', () => {
    const { fixture } = configure({}, {});
    fixture.componentInstance.ngOnInit();

    expect(fixture.componentInstance.errorMessage()).toBe('auth.invalidResetLink');
  });

  it('does not call resetPassword when passwords mismatch', async () => {
    const { fixture, auth } = configure();
    fixture.componentInstance.ngOnInit();
    fixture.componentInstance.form.setValue({ newPassword: 'a', confirmPassword: 'b' });

    await fixture.componentInstance.onSubmit();

    expect(auth.resetPassword).not.toHaveBeenCalled();
  });

  it('calls resetPassword with email + code from query params and shows success', async () => {
    const { fixture, auth } = configure();
    fixture.componentInstance.ngOnInit();
    fixture.componentInstance.form.setValue({ newPassword: 'newPw', confirmPassword: 'newPw' });

    await fixture.componentInstance.onSubmit();

    expect(auth.resetPassword).toHaveBeenCalledWith('a@b.c', 'CODE-123', 'newPw');
    expect(fixture.componentInstance.successMessage()).toBe('auth.resetSuccess');
  });

  it('surfaces the server error.detail when reset fails with a 400', async () => {
    const error = new HttpErrorResponse({ status: 400, error: { detail: 'Reset code expired' } });
    const { fixture } = configure({ resetPassword: vi.fn().mockRejectedValue(error) });
    fixture.componentInstance.ngOnInit();
    fixture.componentInstance.form.setValue({ newPassword: 'newPw', confirmPassword: 'newPw' });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toBe('Reset code expired');
  });
});
