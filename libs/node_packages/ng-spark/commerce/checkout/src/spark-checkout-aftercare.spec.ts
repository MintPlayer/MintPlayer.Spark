import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SparkOrderAftercare } from './generated/contracts';
import { SparkCommerceCheckoutService } from './spark-commerce-checkout.service';
import { SparkCheckoutAftercareComponent } from './spark-checkout-aftercare.component';

// Commerce #521, M9: the withdrawal function (GA1), cancellation (D20), reschedule (D20), guarantee claims (GA8).

const aftercare = (available: boolean): SparkOrderAftercare => ({
  number: '2026-000001',
  state: 'Confirmed',
  currency: 'EUR',
  withdrawal: { available, deadline: null, buttonKey: 'commerceCheckout.withdrawal.button', confirmKey: 'commerceCheckout.withdrawal.confirm', sellerPaysReturnShipping: false },
  lines: [{
    lineId: 'l1', title: null, quantity: 2, returned: 0, free: 2, withdrawalRight: true, withdrawalLive: available,
    withdrawalExemption: null, cancellationRefusal: 'noCancellation', cancellationFeeBasisPoints: null, guaranteeLive: false, guaranteeUntil: null,
  }],
  returns: [],
  claims: [],
  refunds: [],
  rescheduleOffers: [],
});

describe('SparkCommerceCheckoutService after the sale', () => {
  let http: HttpTestingController;
  let service: SparkCommerceCheckoutService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    postEnvelope.mockResolvedValue({});
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: { postEnvelope } }],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommerceCheckoutService);
  });

  afterEach(() => http.verify());

  it('reads the after-sale state with the number and the signed guest token in the query string', async () => {
    const pending = service.aftercare('2026-000001', 'tok');
    const request = http.expectOne((r) => r.url === '/spark/commerce/checkout/refunds/order');
    expect(request.request.params.get('number')).toBe('2026-000001');
    expect(request.request.params.get('access')).toBe('tok');
    request.flush({ result: aftercare(true) });
    expect((await pending)?.withdrawal.available).toBe(true);
  });

  it('answers null for an order that is not the caller\'s', async () => {
    const pending = service.aftercare('x');
    http.expectOne((r) => r.url === '/spark/commerce/checkout/refunds/order').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('posts the withdrawal, cancellation, claim and reschedule choice through the envelope helper', async () => {
    await service.withdraw({ number: 'n', lines: [{ lineId: 'l1', quantity: 1 }], confirm: false });
    await service.cancel({ number: 'n', confirm: true });
    await service.claim({ number: 'n', lines: [{ lineId: 'l1', quantity: 1 }], description: 'broken' });
    await service.chooseReschedule({ number: 'n', windowId: 'w', choice: 'refund' });
    expect(postEnvelope.mock.calls.map((c) => c[0])).toEqual([
      '/commerce/checkout/refunds/withdraw',
      '/commerce/checkout/refunds/cancel',
      '/commerce/checkout/refunds/claim',
      '/commerce/checkout/refunds/reschedule',
    ]);
    expect(postEnvelope.mock.calls[0][1]).toMatchObject({ confirm: false, lines: [{ lineId: 'l1', quantity: 1 }] });
  });
});

describe('SparkCheckoutAftercareComponent', () => {
  const checkout = {
    aftercare: vi.fn(),
    withdraw: vi.fn(),
    cancel: vi.fn(),
    claim: vi.fn(),
    chooseReschedule: vi.fn(),
  };

  beforeEach(() => {
    Object.values(checkout).forEach((f) => f.mockReset());
    TestBed.configureTestingModule({
      imports: [SparkCheckoutAftercareComponent],
      providers: [
        { provide: SparkCommerceCheckoutService, useValue: checkout },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k, language: () => 'en', languages: () => ({}) } },
      ],
    });
  });

  async function render(available: boolean) {
    checkout.aftercare.mockResolvedValue(aftercare(available));
    const fixture = TestBed.createComponent(SparkCheckoutAftercareComponent);
    fixture.componentRef.setInput('number', '2026-000001');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('shows the labelled withdrawal button while a right is live, and not otherwise (GA1)', async () => {
    const live = await render(true);
    expect((live.nativeElement as HTMLElement).querySelector('.spark-checkout-withdrawal-button')?.textContent).toContain('commerceCheckout.withdrawal.button');
    const ended = await render(false);
    expect((ended.nativeElement as HTMLElement).querySelector('.spark-checkout-withdrawal-button')).toBeNull();
  });

  it('goes form, then a confirmation step (preview), then records and shows the receipt', async () => {
    const fixture = await render(true);
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('.spark-checkout-withdrawal-button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('.spark-checkout-withdrawal-form')).not.toBeNull();

    checkout.withdraw.mockResolvedValueOnce({ confirmed: false, lines: [{ lineId: 'l1', quantity: 2, eligible: true }], withheld: false });
    (element.querySelector('.spark-checkout-withdrawal-form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(checkout.withdraw).toHaveBeenLastCalledWith(expect.objectContaining({ confirm: false, lines: [{ lineId: 'l1', quantity: 2 }] }));
    expect(element.querySelector('.spark-checkout-withdrawal-confirm')?.textContent).toContain('commerceCheckout.withdrawal.confirm');

    checkout.withdraw.mockResolvedValueOnce({ confirmed: true, returnId: 'w-1', receivedAt: '2026-10-19T10:00:00Z', lines: [], withheld: true });
    (element.querySelector('.spark-checkout-withdrawal-confirm') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(checkout.withdraw).toHaveBeenLastCalledWith(expect.objectContaining({ confirm: true }));
    expect(element.querySelector('.spark-checkout-withdrawal-receipt')).not.toBeNull();
  });
});
