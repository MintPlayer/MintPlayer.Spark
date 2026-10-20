import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { CommercePriceDisplay } from './generated/contracts';
import { SparkCommerceService } from './spark-commerce.service';
import { SparkCommercePriceComponent } from './spark-commerce-price.component';
import { SparkCommerceTraderLabelComponent } from './spark-commerce-trader-label.component';
import { sparkCommerceRoutes } from './provide-spark-commerce';

// Commerce #521 M4: the ng-spark client of MintPlayer.Spark.Commerce.

const eur = (amount: number) => ({ Amount: amount, Currency: 'EUR' });

const price = (overrides: Partial<CommercePriceDisplay> = {}): CommercePriceDisplay => ({
  unitTotal: eur(26.5),
  gross: eur(25),
  vat: eur(4.34),
  mandatoryFees: [{ code: 'service', kind: 'Service', amount: eur(1.5), labelKey: 'commerce.fee.Service' }],
  optionalFees: [],
  priorPrice: null,
  ...overrides,
});

describe('commerce/core: the service', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    spark = { postEnvelope: vi.fn().mockResolvedValue({ id: 'DsaNotices/1', receivedAt: '2026-10-19T10:00:00Z' }) };
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: spark }],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('quotes a raw price with GET and unwraps the envelope', async () => {
    const service = TestBed.inject(SparkCommerceService);
    const http = TestBed.inject(HttpTestingController);

    const quoted = service.quote({ channel: 'tickets', amount: 25, currency: 'EUR', vat: 2100 });
    const request = http.expectOne(r => r.url === '/spark/commerce/price');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('channel')).toBe('tickets');
    expect(request.request.params.get('amount')).toBe('25');
    expect(request.request.params.get('vat')).toBe('2100');
    request.flush({ result: price() });
    expect((await quoted)?.unitTotal).toEqual(eur(26.5));
  });

  it('answers null for a refused trader page (404)', async () => {
    const service = TestBed.inject(SparkCommerceService);
    const http = TestBed.inject(HttpTestingController);

    const page = service.traderPage('Organizations/x');
    const request = http.expectOne(r => r.url === '/spark/commerce/traders');
    expect(request.request.params.get('organizationId')).toBe('Organizations/x');
    request.flush(null, { status: 404, statusText: 'Not Found' });
    expect(await page).toBeNull();
  });

  it('posts a DSA notice through the envelope helper (XSRF, operations)', async () => {
    const service = TestBed.inject(SparkCommerceService);
    const receipt = await service.submitNotice({
      targetKind: 'listing', targetId: 'Products/1', reason: 'scams-and-fraud', explanation: 'Fake', goodFaith: true,
    });
    expect(spark.postEnvelope).toHaveBeenCalledWith('/commerce/notices', expect.objectContaining({ targetKind: 'listing', goodFaith: true }));
    expect(receipt.id).toBe('DsaNotices/1');
  });

  it('builds the export and PDF URLs with encoded ids', () => {
    const service = TestBed.inject(SparkCommerceService);
    expect(service.exportUrl('Organizations/a b', 'buyers')).toBe('/spark/commerce/sellers/export?organizationId=Organizations%2Fa%20b&kind=buyers');
    expect(service.legalDocumentPdfUrl('LegalDocuments/1')).toBe('/spark/commerce/legal/pdf?id=LegalDocuments%2F1');
  });
});

describe('commerce/core: components', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SparkLanguageService, useValue: { t: (k: string) => k } }],
    });
  });

  it('shows the final price and never hides the prior price the server sent (Omnibus)', () => {
    const fixture = TestBed.createComponent(SparkCommercePriceComponent);
    fixture.componentRef.setInput('price', price({ priorPrice: eur(30) }));
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.spark-commerce-price-total')?.textContent).toContain('26');
    expect(el.querySelector('.spark-commerce-price-prior')).not.toBeNull();
  });

  it('lists the mandatory fees only with breakdown', () => {
    const fixture = TestBed.createComponent(SparkCommercePriceComponent);
    fixture.componentRef.setInput('price', price());
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-commerce-price-fee')).toBeNull();
    fixture.componentRef.setInput('breakdown', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-commerce-price-fee')?.textContent).toContain('commerce.fee.Service');
  });

  it('labels a private seller as such and links the trader page', () => {
    const fixture = TestBed.createComponent(SparkCommerceTraderLabelComponent);
    fixture.componentRef.setInput('seller', { organizationId: 'Organizations/1', name: null, traderStatus: 'NonTrader' });
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent).toContain('commerce.trader.NonTrader');
    expect(el.querySelector('.spark-commerce-trader-label-private')).not.toBeNull();
    expect(el.querySelector('a')?.getAttribute('href')).toContain('/commerce/traders/Organizations');
  });

  it('exposes the public routes', () => {
    expect(sparkCommerceRoutes().map(r => r.path)).toEqual(['traders/:organizationId', 'ranking', 'legal', 'legal/:organizationId']);
  });
});
