import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import {
  HttpClient,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { Router } from '@angular/router';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import { sparkAuthInterceptor } from './spark-auth.interceptor';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

describe('sparkAuthInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let router: { navigate: ReturnType<typeof vi.fn>; url: string };

  beforeEach(() => {
    router = { navigate: vi.fn(), url: '/current/page' };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sparkAuthInterceptor])),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
        { provide: Router, useValue: router },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  it('passes successful responses through untouched', async () => {
    const promise = new Promise<unknown>((resolve, reject) => {
      http.get('/some/data').subscribe({ next: resolve, error: reject });
    });

    httpTesting.expectOne('/some/data').flush({ ok: true });

    await expect(promise).resolves.toEqual({ ok: true });
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('navigates to the login URL on a 401 from a non-api endpoint', () => {
    http.get('/some/protected/page').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/protected/page')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(router.navigate).toHaveBeenCalledTimes(1);
    expect(router.navigate).toHaveBeenCalledWith(
      ['/login'],
      { queryParams: { returnUrl: '/current/page' } },
    );
  });

  it('does NOT navigate on a 401 from an api endpoint (no redirect loop)', () => {
    http.get('/spark/auth/me').subscribe({ error: () => undefined });

    httpTesting.expectOne('/spark/auth/me')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('does not navigate on non-401 errors (e.g. 500)', () => {
    http.get('/some/data').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/data')
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('does not navigate on 403 (forbidden, not auth-required)', () => {
    http.get('/some/data').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/data')
      .flush(null, { status: 403, statusText: 'Forbidden' });

    expect(router.navigate).not.toHaveBeenCalled();
  });
});
