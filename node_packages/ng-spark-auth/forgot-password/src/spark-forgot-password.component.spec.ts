import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';

import { SparkForgotPasswordComponent } from './spark-forgot-password.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

function configure(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { forgotPassword: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  TestBed.configureTestingModule({
    imports: [SparkForgotPasswordComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  return { fixture: TestBed.createComponent(SparkForgotPasswordComponent), auth };
}

describe('SparkForgotPasswordComponent', () => {
  it('does not call forgotPassword when the email is invalid', async () => {
    const { fixture, auth } = configure();
    fixture.componentInstance.form.setValue({ email: 'not-an-email' });

    await fixture.componentInstance.onSubmit();

    expect(auth.forgotPassword).not.toHaveBeenCalled();
  });

  it('calls forgotPassword and shows the success translation on success', async () => {
    const { fixture, auth } = configure();
    fixture.componentInstance.form.setValue({ email: 'a@b.c' });

    await fixture.componentInstance.onSubmit();

    expect(auth.forgotPassword).toHaveBeenCalledWith('a@b.c');
    expect(fixture.componentInstance.successMessage()).toBe('auth.forgotPasswordSuccess');
  });

  it('shows the success message even when the server returns an error (no email enumeration)', async () => {
    const { fixture } = configure({ forgotPassword: vi.fn().mockRejectedValue(new Error('no such email')) });
    fixture.componentInstance.form.setValue({ email: 'a@b.c' });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.successMessage()).toBe('auth.forgotPasswordSuccess');
    expect(fixture.componentInstance.errorMessage()).toBe('');
  });
});
