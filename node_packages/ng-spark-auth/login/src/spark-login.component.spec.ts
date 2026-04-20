import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkLoginComponent } from './spark-login.component';
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

function configure(authOverrides: Partial<SparkAuthService> = {}, returnUrl: string | null = null) {
  const auth: any = { login: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  const route = { snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) } };

  TestBed.configureTestingModule({
    imports: [SparkLoginComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: ActivatedRoute, useValue: route },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });

  const fixture = TestBed.createComponent(SparkLoginComponent);
  const router = TestBed.inject(Router);
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockReturnValue(Promise.resolve(true));
  const navigate = vi.spyOn(router, 'navigate').mockReturnValue(Promise.resolve(true));

  return { fixture, auth, navigateByUrl, navigate };
}

describe('SparkLoginComponent', () => {
  it('starts with an invalid form (required fields)', () => {
    const { fixture } = configure();
    expect(fixture.componentInstance.form.invalid).toBe(true);
  });

  it('does not call auth.login when the form is invalid', async () => {
    const { fixture, auth } = configure();
    await fixture.componentInstance.onSubmit();
    expect(auth.login).not.toHaveBeenCalled();
  });

  it('logs in and navigates to defaultRedirectUrl on success', async () => {
    const { fixture, auth, navigateByUrl } = configure();
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    await fixture.componentInstance.onSubmit();

    expect(auth.login).toHaveBeenCalledWith('a@b.c', 'pw');
    expect(navigateByUrl).toHaveBeenCalledWith(defaultSparkAuthConfig.defaultRedirectUrl);
  });

  it('navigates to the returnUrl query param when present', async () => {
    const { fixture, navigateByUrl } = configure({}, '/protected');
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    await fixture.componentInstance.onSubmit();

    expect(navigateByUrl).toHaveBeenCalledWith('/protected');
  });

  it('redirects to the two-factor route on 401 with RequiresTwoFactor detail', async () => {
    const error = new HttpErrorResponse({ status: 401, error: { detail: 'RequiresTwoFactor' } });
    const { fixture, navigate } = configure({ login: vi.fn().mockRejectedValue(error) }, '/protected');
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'pw', rememberMe: false });

    await fixture.componentInstance.onSubmit();

    expect(navigate).toHaveBeenCalledWith([routePaths.twoFactor], {
      queryParams: { returnUrl: '/protected' },
    });
  });

  it('shows the invalid-credentials error on a generic 401', async () => {
    const { fixture } = configure({
      login: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 401 })),
    });
    fixture.componentInstance.form.setValue({ email: 'a@b.c', password: 'wrong', rememberMe: false });

    await fixture.componentInstance.onSubmit();

    expect(fixture.componentInstance.errorMessage()).toBe('auth.invalidCredentials');
  });
});
