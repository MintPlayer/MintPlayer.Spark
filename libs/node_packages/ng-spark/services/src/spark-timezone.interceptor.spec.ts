import { DOCUMENT } from '@angular/common';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  HttpClient,
  HttpFeature,
  HttpFeatureKind,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { firstValueFrom } from 'rxjs';
import {
  SPARK_TIMEZONE_COOKIE,
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

  const configure = (features: HttpFeature<HttpFeatureKind>[]) => {
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

/** A document whose cookie writes are recorded (jsdom refuses `Secure` on its http origin). */
class FakeDocument {
  writes: string[] = [];
  private jar = new Map<string, string>();
  constructor(readonly location: { protocol: string }) {}
  get cookie(): string {
    return [...this.jar].map(([k, v]) => `${k}=${v}`).join('; ');
  }
  set cookie(value: string) {
    this.writes.push(value);
    const [pair] = value.split(';');
    const eq = pair.indexOf('=');
    this.jar.set(pair.slice(0, eq), pair.slice(eq + 1));
  }
}

/** The cookie lets a server-side render (no script, no header) know the viewer's zone (#460, item 7). */
describe('withSparkTimezone cookie', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let doc: FakeDocument;

  const configure = (options: {
    features?: ReturnType<typeof withSparkTimezone>;
    protocol?: string;
    platform?: string;
  } = {}) => {
    doc = new FakeDocument({ protocol: options.protocol ?? 'http:' });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: DOCUMENT, useValue: doc },
        { provide: PLATFORM_ID, useValue: options.platform ?? 'browser' },
        provideHttpClient(...(options.features ?? withSparkTimezone())),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  };

  const send = (url = '/spark/po/load') => {
    void firstValueFrom(http.get(url)).catch(() => undefined);
    httpTesting.expectOne(url).flush({});
  };

  const zone = (id: string) =>
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(
      () => ({ resolvedOptions: () => ({ timeZone: id }) }) as unknown as Intl.DateTimeFormat,
    );

  beforeEach(() => configure());

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it('stays in lockstep with the server cookie name', () => {
    expect(SPARK_TIMEZONE_COOKIE).toBe('spark-timezone');
  });

  it('writes the zone on the first request, with the PRD attributes', () => {
    configure();
    zone('Europe/Brussels');
    send();
    expect(doc.writes).toEqual(['spark-timezone=Europe/Brussels; Path=/; Max-Age=31536000; SameSite=Lax']);
  });

  it('adds Secure on https', () => {
    configure({ protocol: 'https:' });
    zone('Europe/Brussels');
    send();
    expect(doc.writes).toEqual(['spark-timezone=Europe/Brussels; Path=/; Max-Age=31536000; SameSite=Lax; Secure']);
  });

  it('writes only when the zone changed', () => {
    configure();
    zone('Europe/Brussels');
    send();
    send();
    vi.restoreAllMocks();
    zone('Asia/Tokyo');
    send();
    expect(doc.writes.map((w) => w.split(';')[0])).toEqual([
      'spark-timezone=Europe/Brussels',
      'spark-timezone=Asia/Tokyo',
    ]);
  });

  it('writes even when the request is cross-origin, without sending that host the header', () => {
    configure();
    zone('Europe/Brussels');
    void firstValueFrom(http.get('https://third-party.example/api')).catch(() => undefined);
    const req = httpTesting.expectOne('https://third-party.example/api');
    expect(req.request.headers.get(SPARK_TIMEZONE_HEADER)).toBeNull();
    req.flush({});
    expect(doc.writes.length).toBe(1);
  });

  it('uses a custom cookie name', () => {
    configure({ features: withSparkTimezone({ cookieName: 'tz' }) });
    zone('Europe/Brussels');
    send();
    expect(doc.writes[0].startsWith('tz=Europe/Brussels;')).toBe(true);
  });

  it('cookieName: false writes no cookie but still sends the header', () => {
    configure({ features: withSparkTimezone({ cookieName: false }) });
    zone('Europe/Brussels');
    void firstValueFrom(http.get('/spark/po/load')).catch(() => undefined);
    const req = httpTesting.expectOne('/spark/po/load');
    expect(req.request.headers.get(SPARK_TIMEZONE_HEADER)).toBe('Europe/Brussels');
    req.flush({});
    expect(doc.writes).toEqual([]);
  });

  it('never writes a zone the server would refuse', () => {
    configure();
    zone('Not a zone; Path=/evil');
    send();
    expect(doc.writes).toEqual([]);
  });

  it('never writes on the server platform', () => {
    configure({ platform: 'server' });
    zone('UTC');
    send();
    expect(doc.writes).toEqual([]);
  });
});

/**
 * S-TZ3 (#460): during a server-side render the "browser" zone is the Node process's zone (UTC in a
 * container). Sent as the header, it would win over the viewer's cookie on the server.
 */
describe('sparkTimezoneInterceptor on the server platform', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: PLATFORM_ID, useValue: 'server' },
        provideHttpClient(withInterceptors([sparkTimezoneInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('Node can name a zone, so an unguarded interceptor would send it', () => {
    // The measurement half of the spike: this is what would ride on the header.
    const nodeZone = resolveBrowserTimeZone();
    console.log(`S-TZ3 process zone: ${nodeZone} (TZ=${process.env['TZ'] ?? '<unset>'})`);
    expect(nodeZone).toBeTruthy();
  });

  it('does not send the header', () => {
    void firstValueFrom(http.get('/spark/po/load')).catch(() => undefined);
    const req = httpTesting.expectOne('/spark/po/load');
    expect(req.request.headers.get(SPARK_TIMEZONE_HEADER)).toBeNull();
    req.flush({});
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
