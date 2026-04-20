import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { filter, firstValueFrom } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { SparkRegisterComponent } from './spark-register.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';

@Component({ standalone: true, template: '' })
class StubComponent {}

const routePaths = {
  login: '/login', twoFactor: '/login/two-factor', register: '/register',
  forgotPassword: '/forgot-password', resetPassword: '/reset-password',
};

const routes: Routes = [
  { path: 'login', component: StubComponent },
  { path: 'register', component: SparkRegisterComponent },
];

function nextNavigationEnd(): Promise<NavigationEnd> {
  const router = TestBed.inject(Router);
  return firstValueFrom(router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd)));
}

async function setup(authOverrides: Partial<SparkAuthService> = {}) {
  const auth: any = { register: vi.fn().mockResolvedValue(undefined), ...authOverrides };
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: routePaths },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, auth };
}

describe('SparkRegisterComponent', () => {
  it('detects password mismatch as a form-level validation error', async () => {
    const { harness } = await setup();
    const component = await harness.navigateByUrl('/register', SparkRegisterComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw1', confirmPassword: 'pw2' });

    expect(component.form.errors).toEqual({ passwordMismatch: true });
  });

  it('does not call auth.register when the form is invalid', async () => {
    const { harness, auth } = await setup();
    const component = await harness.navigateByUrl('/register', SparkRegisterComponent);

    await component.onSubmit();

    expect(auth.register).not.toHaveBeenCalled();
  });

  it('registers and navigates to login with ?registered=true on success', async () => {
    const { harness, auth } = await setup();
    const component = await harness.navigateByUrl('/register', SparkRegisterComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', confirmPassword: 'pw' });

    const navigated = nextNavigationEnd();
    await component.onSubmit();
    await navigated;

    expect(auth.register).toHaveBeenCalledWith('a@b.c', 'pw');
    expect(TestBed.inject(Router).url).toBe('/login?registered=true');
  });

  it('flattens server validation errors into a single message', async () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { errors: { Password: ['Too short', 'Needs digit'] } },
    });
    const { harness } = await setup({ register: vi.fn().mockRejectedValue(error) });
    const component = await harness.navigateByUrl('/register', SparkRegisterComponent);
    component.form.setValue({ email: 'a@b.c', password: 'x', confirmPassword: 'x' });

    await component.onSubmit();

    expect(component.errorMessage()).toContain('Too short');
    expect(component.errorMessage()).toContain('Needs digit');
  });

  it('falls back to the registration-failed translation on unknown errors', async () => {
    const { harness } = await setup({ register: vi.fn().mockRejectedValue(new Error('boom')) });
    const component = await harness.navigateByUrl('/register', SparkRegisterComponent);
    component.form.setValue({ email: 'a@b.c', password: 'pw', confirmPassword: 'pw' });

    await component.onSubmit();

    expect(component.errorMessage()).toBe('auth.registrationFailed');
  });
});
