import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { firstValueFrom } from 'rxjs';
import {
  SPARK_TIMEZONE_HEADER,
  resolveBrowserTimeZone,
  sparkTimezoneInterceptor,
  withSparkTimezone,
} from './spark-timezone.interceptor';

/**
 * The viewer's IANA zone rides on same-origin requests only: a third-party host has no business
 * receiving a fingerprinting signal.
 */
describe('sparkTimezoneInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  const configure = (features: ReturnType<typeof withInterceptors>[]) => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(...features), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  };

  /** Sends a GET and returns the header the server would have received. */
  const headerFor = (url: string): string | null => {
    void firstValueFrom(http.get(url)).catch(() => undefined);
    const req = httpTesting.expectOne(url);
    const header = req.request.headers.get(SPARK_TIMEZONE_HEADER);
    req.flush({});
    return header;
  };

  const zone = (id: string | undefined) =>
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(
      () => ({ resolvedOptions: () => ({ timeZone: id }) }) as unknown as Intl.DateTimeFormat,
    );

  beforeEach(() => configure([withInterceptors([sparkTimezoneInterceptor])]));

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it('stays in lockstep with the server header name', () => {
    expect(SPARK_TIMEZONE_HEADER).toBe('X-Spark-Timezone');
  });

  it('sets the zone id on a relative URL', () => {
    zone('Europe/Brussels');
    expect(headerFor('/spark/po/load')).toBe('Europe/Brussels');
  });

  it('sets it on an absolute URL of the same origin', () => {
    zone('Europe/Brussels');
    expect(headerFor(`${window.location.origin}/spark/types`)).toBe('Europe/Brussels');
  });

  it('never sets it on a cross-origin URL', () => {
    zone('Europe/Brussels');
    expect(headerFor('https://third-party.example/api')).toBeNull();
  });

  it('leaves a scheme-prefixed URL it cannot parse alone', () => {
    zone('Europe/Brussels');
    // Matches the scheme test but `new URL` throws on it; not annotating is the safe answer.
    expect(headerFor('http://[bad')).toBeNull();
  });

  it('sends no header when the browser cannot name a zone', () => {
    zone(undefined);
    expect(headerFor('/spark/po/load')).toBeNull();
  });

  it('withSparkTimezone() registers the same interceptor', () => {
    configure(withSparkTimezone());
    zone('America/New_York');
    expect(headerFor('/spark/queries')).toBe('America/New_York');
  });
});

describe('resolveBrowserTimeZone', () => {
  afterEach(() => vi.restoreAllMocks());

  it('returns the resolved zone id', () => {
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(
      () => ({ resolvedOptions: () => ({ timeZone: 'Asia/Tokyo' }) }) as unknown as Intl.DateTimeFormat,
    );
    expect(resolveBrowserTimeZone()).toBe('Asia/Tokyo');
  });

  it('returns null for an empty zone id', () => {
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(
      () => ({ resolvedOptions: () => ({ timeZone: '' }) }) as unknown as Intl.DateTimeFormat,
    );
    expect(resolveBrowserTimeZone()).toBeNull();
  });

  it('returns null when Intl throws', () => {
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(() => {
      throw new Error('no Intl');
    });
    expect(resolveBrowserTimeZone()).toBeNull();
  });
});
