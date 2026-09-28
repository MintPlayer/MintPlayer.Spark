import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { beforeEach, describe, expect, it } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('SparkAuthService — account management (#460, D16)', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig }],
    });
    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
  });

  it('confirmEmail posts the link query, changedEmail only when present', async () => {
    const plain = service.confirmEmail('users/1', 'abc');
    const req = http.expectOne('/spark/auth/confirm-email');
    expect(req.request.body).toEqual({ userId: 'users/1', code: 'abc' });
    req.flush(null);
    expect((await plain).success).toBe(true);

    const change = service.confirmEmail('users/1', 'abc', 'new@example.test');
    http.expectOne('/spark/auth/confirm-email').flush(
      { errors: { InvalidToken: ['Invalid token.'] } }, { status: 400, statusText: 'Bad Request' });
    const result = await change;
    expect(result.success).toBe(false);
    expect(result.errors).toEqual({ InvalidToken: ['Invalid token.'] });
  });

  it('setPassword sends the current password only when given', async () => {
    const first = service.setPassword('N3w!pass');
    const req = http.expectOne('/spark/auth/manage/password');
    expect(req.request.body).toEqual({ newPassword: 'N3w!pass' });
    req.flush(null);
    await first;

    const second = service.setPassword('N3w!pass', 'old');
    expect(http.expectOne('/spark/auth/manage/password').request.body).toEqual({ newPassword: 'N3w!pass', currentPassword: 'old' });
  });

  it('profile read and update round-trip, with preferredCulture', async () => {
    const read = service.profile();
    http.expectOne('/spark/auth/manage/profile').flush({ userName: 'jane', email: 'jane@example.test', fields: {}, preferredCulture: 'nl-BE' });
    expect((await read).value?.preferredCulture).toBe('nl-BE');

    const update = service.updateProfile({ preferredCulture: null });
    const req = http.expectOne(r => r.method === 'POST' && r.url === '/spark/auth/manage/profile');
    expect(req.request.body).toEqual({ preferredCulture: null });
    req.flush({ userName: 'jane', email: 'jane@example.test', fields: {}, preferredCulture: null });
    expect((await update).value?.preferredCulture).toBeNull();
  });

  it('changeEmail posts newEmail to manage/info', async () => {
    const call = service.changeEmail('new@example.test');
    const req = http.expectOne('/spark/auth/manage/info');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ newEmail: 'new@example.test' });
    req.flush({ email: 'old@example.test', isEmailConfirmed: true });
    expect((await call).value?.email).toBe('old@example.test');
  });

  it('twoFactor and authenticatorUri map the 409 error code', async () => {
    const state = service.twoFactor({ enable: true, twoFactorCode: '123456' });
    expect(http.expectOne('/spark/auth/manage/2fa').request.body).toEqual({ enable: true, twoFactorCode: '123456' });

    const uri = service.authenticatorUri();
    http.expectOne('/spark/auth/manage/2fa/authenticator-uri').flush({ error: 'no_authenticator_key' }, { status: 409, statusText: 'Conflict' });
    const result = await uri;
    expect(result).toEqual({ success: false, status: 409, error: 'no_authenticator_key', errors: undefined });
    void state;
  });

  it('deleteAccount sends the password in the DELETE body, clears the user and refreshes csrf', async () => {
    const call = service.deleteAccount('secret');
    const req = http.expectOne('/spark/auth/manage/account');
    expect(req.request.method).toBe('DELETE');
    expect(req.request.body).toEqual({ password: 'secret' });
    req.flush(null, { status: 204, statusText: 'No Content' });
    await flush();
    http.expectOne('/spark/auth/csrf-refresh').flush(null);
    expect((await call).success).toBe(true);
    expect(service.user()).toBeNull();
  });

  it('deleteAccount surfaces reauthentication_required', async () => {
    const call = service.deleteAccount();
    const req = http.expectOne('/spark/auth/manage/account');
    expect(req.request.body).toEqual({});
    req.flush({ error: 'reauthentication_required' }, { status: 403, statusText: 'Forbidden' });
    const result = await call;
    expect(result.success).toBe(false);
    expect(result.error).toBe('reauthentication_required');
  });
});
