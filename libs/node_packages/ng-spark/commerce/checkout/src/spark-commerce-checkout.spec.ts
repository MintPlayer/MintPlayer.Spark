import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SparkCommerceService } from '@mintplayer/ng-spark/commerce/core';
import { SparkCheckoutSummary } from './generated/contracts';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';
import { SparkCheckoutStepComponent } from './spark-checkout-step.component';

// Commerce #521: the ng-spark client of MintPlayer.Spark.Commerce.Checkout.

describe('SparkCommerceCheckoutService', () => {
  let http: HttpTestingController;
  let service: SparkCommerceCheckoutService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    postEnvelope.mockResolvedValue({});
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkService, useValue: { postEnvelope } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommerceCheckoutService);
  });

  afterEach(() => http.verify());

  it('reads the basket and the summary with GET and unwraps the envelope', async () => {
    const basket = service.basket();
    http.expectOne((r) => r.method === 'GET' && r.url === '/spark/commerce/checkout/basket')
      .flush({ result: { lines: [], mixedChannels: false, choices: {} } });
    expect((await basket)?.lines).toEqual([]);

    const summary = service.summary();
    http.expectOne((r) => r.method === 'GET' && r.url === '/spark/commerce/checkout/summary').flush({ result: { orderButtonKey: 'x' } });
    expect((await summary)?.orderButtonKey).toBe('x');
  });

  it('asks for an order by number in the query string and answers null for one that is not the caller\'s', async () => {
    const pending = service.order('2026-000001');
    const request = http.expectOne((r) => r.url === '/spark/commerce/checkout/order');
    expect(request.request.params.get('number')).toBe('2026-000001');
    request.flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('posts every write through the envelope helper with the documented body', async () => {
    await service.add({ itemId: 'Items/1', quantity: 2 });
    await service.setQuantity('l1', 0);
    await service.setChoices({ insurance: 'accept' });
    await service.place({ acceptedDocumentIds: ['LegalDocuments/1'], marketingOptIn: false });
    await service.cancelPayment('2026-000001');
    await service.createBackOfficeOrder({ organizationId: 'Organizations/1', origin: 'phone', lines: [] });
    await service.markPaid({ orderId: 'CheckoutOrders/1', paymentReference: 'ref' });
    await service.resend('CheckoutOrders/1');
    await service.changeLine({ orderId: 'CheckoutOrders/1', lineId: 'l1' });

    expect(postEnvelope.mock.calls).toEqual([
      ['/commerce/checkout/basket/add', { itemId: 'Items/1', quantity: 2 }],
      ['/commerce/checkout/basket/quantity', { lineId: 'l1', quantity: 0 }],
      ['/commerce/checkout/basket/choices', { choices: { insurance: 'accept' } }],
      ['/commerce/checkout/place', { acceptedDocumentIds: ['LegalDocuments/1'], marketingOptIn: false }],
      ['/commerce/checkout/order/cancel-payment', { number: '2026-000001' }],
      ['/commerce/checkout/box-office/orders', { organizationId: 'Organizations/1', origin: 'phone', lines: [] }],
      ['/commerce/checkout/box-office/mark-paid', { orderId: 'CheckoutOrders/1', paymentReference: 'ref' }],
      ['/commerce/checkout/box-office/resend', { orderId: 'CheckoutOrders/1' }],
      ['/commerce/checkout/box-office/change-line', { orderId: 'CheckoutOrders/1', lineId: 'l1' }],
    ]);
  });

  it('reads a refusal from a 409 and nothing from other errors', () => {
    const conflict = new HttpErrorResponse({ status: 409, error: { result: { code: 'soldOut', lineId: 'l1' } } });
    expect(refusalOf(conflict)).toEqual({ code: 'soldOut', lineId: 'l1' });
    expect(refusalKey('soldOut')).toBe('commerceCheckout.refusal.soldOut');
    expect(refusalOf(new HttpErrorResponse({ status: 500, error: { result: { code: 'x' } } }))).toBeNull();
    expect(refusalOf(new Error('boom'))).toBeNull();
  });
});

describe('SparkCheckoutStepComponent', () => {
  const money = (amount: number) => ({ Amount: amount, Currency: 'EUR' });
  const summary: SparkCheckoutSummary = {
    lines: [{
      lineId: 'l1', itemId: 'Items/1', title: null, description: null, channel: 'tickets', sellerOrganizationId: null,
      quantity: 1, units: [], unitPrice: money(20), net: money(16.53), vat: money(3.47), fees: [], adjustments: [],
      total: money(20), minimumAge: 18, serviceDate: null,
    }],
    extras: [],
    total: money(20),
    sellers: [null],
    documents: [
      { id: 'LegalDocuments/terms', kind: 'terms-of-sale', organizationId: null, version: 2, effectiveFrom: '2026-01-01T00:00:00Z', title: null, hasPdf: true, mustAccept: true },
      { id: 'LegalDocuments/privacy', kind: 'privacy', organizationId: null, version: 1, effectiveFrom: '2026-01-01T00:00:00Z', title: null, hasPdf: false, mustAccept: false },
    ],
    minimumAge: 18,
    requiresSignIn: false,
    signedIn: true,
    methods: [{ id: 'ideal', provider: 'mollie' }, { id: 'card', provider: 'mollie' }],
    paymentWindowMinutes: 15,
    earliestHoldUntil: null,
    orderButtonKey: 'commerceCheckout.orderWithObligationToPay',
    donation: { beneficiary: null, amounts: [1, 5], roundUp: true },
    insurance: { provider: 'acme', premium: money(2), insuredAmount: money(20), termsUrl: null, description: null },
    softOptInOffered: true,
    needsEmail: false,
  };

  async function render() {
    const place = vi.fn().mockResolvedValue({ number: '2026-000001', state: 'PaymentPending', redirectUrl: 'https://pay.example/1' });
    TestBed.configureTestingModule({
      imports: [SparkCheckoutStepComponent],
      providers: [
        provideRouter([]),
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
        { provide: SparkCommerceService, useValue: { traderPage: vi.fn().mockResolvedValue(null) } },
        {
          provide: SparkCommerceCheckoutService,
          useValue: { summary: vi.fn().mockResolvedValue(summary), place, setChoices: vi.fn(), legalDocumentPdfUrl: (id: string) => `/pdf?id=${id}` },
        },
      ],
    });
    const fixture = TestBed.createComponent(SparkCheckoutStepComponent);
    const navigate = vi.fn();
    fixture.componentRef.setInput('navigate', navigate);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement, place, navigate };
  }

  function tick(element: HTMLElement, selector: string): void {
    const box = element.querySelector<HTMLInputElement>(selector)!;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
  }

  it('never pre-ticks the terms, the age, the insurance, a donation or the opt-in, nor picks one of several methods', async () => {
    const { element } = await render();
    const checked = (selector: string) => [...element.querySelectorAll<HTMLInputElement>(selector)].map((i) => i.checked);
    expect(checked('.spark-checkout-accept')).toEqual([false]);
    expect(checked('.spark-checkout-age')).toEqual([false]);
    expect(checked('.spark-checkout-insurance')).toEqual([false]);
    expect(checked('.spark-checkout-opt-in')).toEqual([false]);
    expect(checked('input[name="spark-checkout-method"]')).toEqual([false, false]);
    // The only checked donation radio is "no donation".
    expect(checked('input[name="spark-checkout-donation"]')).toEqual([true, false, false, false]);
  });

  it('labels the pay button with orderButtonKey and keeps it disabled until every required box is ticked', async () => {
    const { fixture, element, place, navigate } = await render();
    const button = element.querySelector<HTMLButtonElement>('.spark-checkout-pay')!;
    expect(button.textContent?.trim()).toBe('commerceCheckout.orderWithObligationToPay');
    expect(button.disabled).toBe(true);

    tick(element, '.spark-checkout-accept');
    fixture.detectChanges();
    expect(button.disabled).toBe(true);

    tick(element, '.spark-checkout-age');
    fixture.detectChanges();
    expect(button.disabled).toBe(false);

    button.click();
    await fixture.whenStable();
    expect(place).toHaveBeenCalledWith(expect.objectContaining({
      acceptedDocumentIds: ['LegalDocuments/terms'],
      attestedMinimumAge: 18,
      marketingOptIn: false,
      method: null,
    }));
    expect(navigate).toHaveBeenCalledWith('https://pay.example/1');
  });
});
