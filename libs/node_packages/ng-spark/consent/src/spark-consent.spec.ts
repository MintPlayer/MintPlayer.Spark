import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkConsentState } from './generated/contracts';
import { SparkConsentPurposes, SparkConsentService } from './spark-consent.service';
import { SparkConsentBannerComponent } from './spark-consent-banner.component';
import { SparkConsentSettingsComponent } from './spark-consent-settings.component';
import { sparkConsentRoutes } from './provide-spark-consent';

// Commerce #521 X21 (GA9): the client of the consent primitive.

const text = (en: string) => ({ entries: [{ value: en, tag: 'en' }] }) as never;

const state = (granted: Record<string, boolean | null> = {}, needsPrompt = true): SparkConsentState => ({
  policyVersion: '1',
  needsPrompt,
  policyUrl: '/legal/privacy',
  purposes: ['analytics', 'third-party-challenge'].map(name => ({
    name,
    title: text(name),
    description: text(`${name} description`),
    vendors: [],
    necessary: false,
    granted: granted[name] ?? null,
    decidedAt: null,
  })),
});

describe('consent: the service', () => {
  let http: HttpTestingController;
  let consent: SparkConsentService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    consent = TestBed.inject(SparkConsentService);
  });

  afterEach(() => http.verify());

  it('treats an undecided purpose as not granted, and necessary as always granted', async () => {
    const loaded = consent.load();
    http.expectOne('/spark/consent/').flush(state());
    await loaded;

    expect(consent.has(SparkConsentPurposes.analytics)).toBe(false);
    expect(consent.has(SparkConsentPurposes.necessary)).toBe(true);
    expect(consent.needsPrompt()).toBe(true);
  });

  it('rejects all as easily as it accepts all: one POST with every purpose false', async () => {
    const loaded = consent.load();
    http.expectOne('/spark/consent/').flush(state());
    await loaded;

    const rejected = consent.rejectAll();
    const request = http.expectOne(r => r.method === 'POST' && r.url === '/spark/consent/');
    expect(request.request.body.decisions).toEqual({ analytics: false, 'third-party-challenge': false });
    expect(request.request.body.source).toBe('banner');
    request.flush(state({ analytics: false, 'third-party-challenge': false }, false));
    await rejected;

    expect(consent.has('analytics')).toBe(false);
    expect(consent.needsPrompt()).toBe(false);
  });

  it('whenGranted waits until the purpose is granted, never on a refusal', async () => {
    const loaded = consent.load();
    http.expectOne('/spark/consent/').flush(state({ analytics: false }, false));
    await loaded;

    let resolved = false;
    void consent.whenGranted('analytics').then(() => (resolved = true));
    await Promise.resolve();
    expect(resolved).toBe(false);

    const saved = consent.save({ analytics: true });
    http.expectOne(r => r.method === 'POST').flush(state({ analytics: true }, false));
    await saved;
    await Promise.resolve();
    expect(resolved).toBe(true);
  });

  it('reads a missing primitive (404) as no state, so nothing is ever granted', async () => {
    const loaded = consent.load();
    http.expectOne('/spark/consent/').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await loaded).toBeNull();
    expect(consent.has('analytics')).toBe(false);
    expect(consent.needsPrompt()).toBe(false);
  });

  it('unsubscribes with the token, no sign-in', async () => {
    const done = consent.unsubscribe('tok');
    const request = http.expectOne('/spark/consent/unsubscribe');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(await done).toBe(true);
  });
});

describe('consent: the banner and settings', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (v: { entries: { value: string }[] }) => v?.entries?.[0]?.value ?? '' } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows reject and accept with the same class, side by side (no dark pattern)', async () => {
    const fixture = TestBed.createComponent(SparkConsentBannerComponent);
    fixture.detectChanges();
    http.expectOne('/spark/consent/').flush(state());
    await fixture.whenStable();
    fixture.detectChanges();

    const reject = fixture.nativeElement.querySelector('[data-choice="reject"]') as HTMLButtonElement;
    const accept = fixture.nativeElement.querySelector('[data-choice="accept"]') as HTMLButtonElement;
    expect(reject).toBeTruthy();
    expect(accept).toBeTruthy();
    expect(reject.className).toBe(accept.className);
    expect(reject.parentElement).toBe(accept.parentElement);
    expect(fixture.nativeElement.querySelector('section[role="region"]')).toBeTruthy();
  });

  it('is hidden once every purpose is decided', async () => {
    const fixture = TestBed.createComponent(SparkConsentBannerComponent);
    fixture.detectChanges();
    http.expectOne('/spark/consent/').flush(state({ analytics: true, 'third-party-challenge': false }, false));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.spark-consent-banner')).toBeNull();
  });

  it('pre-ticks no purpose the visitor did not grant', async () => {
    const fixture = TestBed.createComponent(SparkConsentSettingsComponent);
    fixture.detectChanges();
    http.expectOne('/spark/consent/').flush(state({ 'third-party-challenge': true }));
    await fixture.whenStable();
    fixture.detectChanges();

    const box = (name: string) => fixture.nativeElement.querySelector(`input[data-purpose="${name}"]`) as HTMLInputElement;
    expect(box('analytics').checked).toBe(false);
    expect(box('third-party-challenge').checked).toBe(true);
  });

  it('routes the settings and unsubscribe pages', () => {
    expect(sparkConsentRoutes().map(r => r.path)).toEqual(['settings', 'unsubscribe']);
  });
});
