import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { blockedItemId, promotionRefusalKey, promotionRefusalOf, SparkCommercePromotionsService } from './spark-commerce-promotions.service';
import { SparkSalePriceComponent } from './spark-sale-price.component';
import { SparkSalePriceDisplay } from './generated/contracts';

// Commerce #521 M8: the ng-spark client of MintPlayer.Spark.Commerce.Promotions.

const eur = (amount: number) => ({ amount, currency: 'EUR' });

const onSale = (overrides: Partial<SparkSalePriceDisplay> = {}): SparkSalePriceDisplay => ({
  itemId: 'items/1',
  finalPrice: eur(80),
  onSale: true,
  announceReduction: true,
  saleEndsAt: '2026-10-26T00:00:00Z',
  regularFinalPrice: eur(100),
  priorPrice: eur(95),
  priorPriceExempt: false,
  reductionPercent: 15,
  reductionSince: '2026-10-19T00:00:00Z',
  ...overrides,
});

describe('SparkCommercePromotionsService', () => {
  let http: HttpTestingController;
  let service: SparkCommercePromotionsService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommercePromotionsService);
  });

  afterEach(() => http.verify());

  it('reads the sale price with the item id in the query string (GA3)', async () => {
    const pending = service.salePrice('products/1-A');
    const request = http.expectOne((r) => r.url === '/spark/commerce/promotions/sale-price');
    expect(request.request.params.get('itemId')).toBe('products/1-A');
    request.flush({ result: onSale() });
    expect((await pending)?.priorPrice).toEqual(eur(95));
  });

  it('answers null for a code the server refuses', async () => {
    const pending = service.code('NOPE');
    http.expectOne((r) => r.url === '/spark/commerce/promotions/code').flush({}, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('wraps an item for a capacity-blocking code', () => {
    expect(blockedItemId('WLABC', 'items/1')).toBe('promotions:WLABC|items/1');
  });

  it('reads a refusal from a 409 envelope', () => {
    const error = new HttpErrorResponse({ status: 409, error: { result: { code: 'promotionCodeExhausted', subject: 'SUMMER' } } });
    expect(promotionRefusalOf(error)?.code).toBe('promotionCodeExhausted');
    expect(promotionRefusalKey('promotionCodeExhausted')).toBe('commercePromotions.refusal.promotionCodeExhausted');
    expect(promotionRefusalOf(new HttpErrorResponse({ status: 500 }))).toBeNull();
  });
});

describe('SparkSalePriceComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  });

  it('shows the was price, the percentage and the prior price of an announced reduction', () => {
    const fixture = TestBed.createComponent(SparkSalePriceComponent);
    fixture.componentRef.setInput('display', onSale());
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.spark-sale-price-was')).not.toBeNull();
    expect(element.querySelector('.spark-sale-price-badge')).not.toBeNull();
    expect(element.querySelector('.spark-sale-price-prior')).not.toBeNull();
  });

  it('never shows a reduction the server may not announce (no prior price)', () => {
    const fixture = TestBed.createComponent(SparkSalePriceComponent);
    fixture.componentRef.setInput('display', onSale({ announceReduction: false, priorPrice: null, regularFinalPrice: null, reductionPercent: null }));
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.spark-sale-price-now')).not.toBeNull();
    expect(element.querySelector('.spark-sale-price-was')).toBeNull();
    expect(element.querySelector('.spark-sale-price-badge')).toBeNull();
    expect(element.querySelector('.spark-sale-price-prior')).toBeNull();
  });
});
