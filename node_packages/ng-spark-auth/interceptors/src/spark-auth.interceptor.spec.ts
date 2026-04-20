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
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { sparkAuthInterceptor } from './spark-auth.interceptor';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const routes: Routes = [
  { path: '', pathMatch: 'full', component: StubComponent },
  { path: 'login', component: StubComponent },
  { path: 'protected/page', component: StubComponent },
  { path: 'current/page', component: StubComponent },
];

describe('sparkAuthInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([sparkAuthInterceptor])),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    harness = await RouterTestingHarness.create();
    // Drive the harness to a known starting URL so router.url has the value
    // the interceptor will store as returnUrl.
    await harness.navigateByUrl('/current/page');

    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  it('passes successful responses through untouched (no navigation)', async () => {
    const promise = new Promise<unknown>((resolve, reject) => {
      http.get('/some/data').subscribe({ next: resolve, error: reject });
    });

    httpTesting.expectOne('/some/data').flush({ ok: true });

    await expect(promise).resolves.toEqual({ ok: true });
    expect(TestBed.inject(Router).url).toBe('/current/page');
  });

  it('navigates to the login URL on a 401 from a non-api endpoint', async () => {
    const navigated = nextNavigationEnd();
    http.get('/some/protected/page').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/protected/page')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    await navigated;

    // returnUrl is the URL the user was on when the 401 fired
    expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fcurrent%2Fpage');
  });

  it('does NOT navigate on a 401 from an api endpoint (no redirect loop)', () => {
    http.get('/spark/auth/me').subscribe({ error: () => undefined });

    httpTesting.expectOne('/spark/auth/me')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(TestBed.inject(Router).url).toBe('/current/page');
  });

  it('does not navigate on non-401 errors (e.g. 500)', () => {
    http.get('/some/data').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/data')
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(TestBed.inject(Router).url).toBe('/current/page');
  });

  it('does not navigate on 403 (forbidden, not auth-required)', () => {
    http.get('/some/data').subscribe({ error: () => undefined });

    httpTesting.expectOne('/some/data')
      .flush(null, { status: 403, statusText: 'Forbidden' });

    expect(TestBed.inject(Router).url).toBe('/current/page');
  });
});
