import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkRegisterComponent } from './spark-register.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

function configure(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { register: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  TestBed.configureTestingModule({
    imports: [SparkRegisterComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  const fixture = TestBed.createComponent(SparkRegisterComponent);
  const router = TestBed.inject(Router);
  const navigate = vi.spyOn(router, 'navigate').mockReturnValue(Promise.resolve(true));
  return { fixture, auth, navigate };
}

describe('SparkRegisterComponent', () => {
  it('detects password mismatch as a form-level validation error', () => {
    const { fixture } = configure();
    fixture.componentInstance.form.setValue({
      email: 'a@b.c', password: 'pw1', confirmPassword: 'pw2',
    });
    expect(fixture.componentInstance.form.errors).toEqual({ passwordMismatch: true });
  });

  it('does not call auth.register when the form is invalid', async () => {
    const { fixture, auth } = configure();
    await fixture.componentInstance.onSubmit();
    expect(auth.register).not.toHaveBeenCalled();
  });

  it('registers and navigates to login with ?registered=true on success', async () => {
    const { fixture, auth, navigate } = configure();
    fixture.componentInstance.form.setValue({
      email: 'a@b.c', password: 'pw', confirmPassword: 'pw',
    });

    await fixture.componentInstance.onSubmit();

    expect(auth.register).toHaveBeenCalledWith('a@b.c', 'pw');
    expect(navigate).toHaveBeenCalledWith([routePaths.login], {
      queryParams: { registered: 'true' },
    });
  });

  it('flattens server validation errors into a single message', async () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { errors: { Password: ['Too short', 'Needs digit'] } },
    });
    const { fixture } = configure({ register: vi.fn().mockRejectedValue(error) });
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'x', confirmPassword: 'x' });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toContain('Too short');
    expect(fixture.componentInstance.errorMessage()).toContain('Needs digit');
  });

  it('falls back to the registration-failed translation on unknown errors', async () => {
    const { fixture } = configure({ register: vi.fn().mockRejectedValue(new Error('boom')) });
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'pw', confirmPassword: 'pw' });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toBe('auth.registrationFailed');
  });
});
