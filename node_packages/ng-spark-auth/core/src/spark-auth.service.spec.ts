import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

/** Microtask flush — let pending awaited Promises resolve before the next HTTP expectation. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('SparkAuthService', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);

    // Service constructor calls checkAuth() — flush the initial /me call as 401
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
  });

  it('starts unauthenticated', () => {
    expect(service.isAuthenticated()).toBe(false);
    expect(service.user()).toBeNull();
  });

  it('login posts credentials, refreshes csrf, then re-checks auth', async () => {
    const promise = service.login('user@example.com', 'password123');

    const loginReq = http.expectOne('/spark/auth/login?useCookies=true');
    expect(loginReq.request.method).toBe('POST');
    expect(loginReq.request.body).toEqual({ email: 'user@example.com', password: 'password123' });
    loginReq.flush(null);
    await flush();

    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    await flush();

    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true,
      userName: 'user',
      email: 'user@example.com',
      roles: [],
    });

    await promise;

    expect(service.isAuthenticated()).toBe(true);
    expect(service.user()?.email).toBe('user@example.com');
  });

  it('login propagates server errors', async () => {
    const promise = service.login('user@example.com', 'wrong');

    http.expectOne('/spark/auth/login?useCookies=true')
      .flush({ detail: 'Invalid credentials' }, { status: 401, statusText: 'Unauthorized' });

    await expect(promise).rejects.toBeDefined();
  });

  it('loginTwoFactor posts the 2FA code', async () => {
    const promise = service.loginTwoFactor('123456');

    const req = http.expectOne('/spark/auth/login?useCookies=true');
    expect(req.request.body).toEqual({ twoFactorCode: '123456', twoFactorRecoveryCode: undefined });
    req.flush(null);
    await flush();

    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    await flush();

    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true, userName: 'u', email: 'u@x', roles: [],
    });

    await promise;
    expect(service.isAuthenticated()).toBe(true);
  });

  it('loginTwoFactor accepts a recovery code', async () => {
    const promise = service.loginTwoFactor('', 'RECOVERY-CODE');

    const req = http.expectOne('/spark/auth/login?useCookies=true');
    expect(req.request.body.twoFactorRecoveryCode).toBe('RECOVERY-CODE');
    req.flush(null);
    await flush();

    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    await flush();

    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true, userName: 'u', email: 'u@x', roles: [],
    });

    await promise;
  });

  it('register posts email and password', async () => {
    const promise = service.register('new@example.com', 'pw');

    const req = http.expectOne('/spark/auth/register');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'new@example.com', password: 'pw' });
    req.flush(null);

    await promise;
  });

  it('logout clears the user signal after csrf refresh', async () => {
    // arrange: authenticate first
    const loginPromise = service.login('user@x', 'pw');
    http.expectOne('/spark/auth/login?useCookies=true').flush(null);
    await flush();
    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    await flush();
    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true, userName: 'u', email: 'u@x', roles: [],
    });
    await loginPromise;
    expect(service.isAuthenticated()).toBe(true);

    // act
    const logoutPromise = service.logout();
    http.expectOne('/spark/auth/logout').flush(null);
    await flush();
    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    await logoutPromise;

    expect(service.isAuthenticated()).toBe(false);
    expect(service.user()).toBeNull();
  });

  it('checkAuth sets the user on a successful /me response', async () => {
    const promise = service.checkAuth();
    http.expectOne('/spark/auth/me').flush({
      isAuthenticated: true,
      userName: 'jane',
      email: 'jane@example.com',
      roles: ['admin'],
    });

    const result = await promise;

    expect(result?.userName).toBe('jane');
    expect(service.user()?.roles).toEqual(['admin']);
  });

  it('checkAuth sets user to null on a 401 response', async () => {
    const promise = service.checkAuth();
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await promise;

    expect(result).toBeNull();
    expect(service.user()).toBeNull();
  });

  it('forgotPassword posts the email', async () => {
    const promise = service.forgotPassword('forgot@example.com');

    const req = http.expectOne('/spark/auth/forgotPassword');
    expect(req.request.body).toEqual({ email: 'forgot@example.com' });
    req.flush(null);

    await promise;
  });

  it('resetPassword posts email, code and new password', async () => {
    const promise = service.resetPassword('user@example.com', 'CODE-123', 'newPw');

    const req = http.expectOne('/spark/auth/resetPassword');
    expect(req.request.body).toEqual({
      email: 'user@example.com',
      resetCode: 'CODE-123',
      newPassword: 'newPw',
    });
    req.flush(null);

    await promise;
  });

  it('uses configured apiBasePath when overridden', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: { ...defaultSparkAuthConfig, apiBasePath: '/custom/auth' } },
      ],
    });
    const customService = TestBed.inject(SparkAuthService);
    const customHttp = TestBed.inject(HttpTestingController);

    customHttp.expectOne('/custom/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const promise = customService.forgotPassword('a@b.c');
    customHttp.expectOne('/custom/auth/forgotPassword').flush(null);
    await promise;
  });
});
