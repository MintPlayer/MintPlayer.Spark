import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkCommerceCheckoutService } from './spark-commerce-checkout.service';

// M17: "More time" (GA10) and the account area's order calls (GB10).

describe('SparkCommerceCheckoutService (M17)', () => {
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

  it('asks for more time with one POST and an empty body', async () => {
    await service.extendHold();
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/checkout/basket/extend-hold', {});
  });

  it('pages the account orders through the query string', async () => {
    const page = service.accountOrders(20, 10);
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/spark/commerce/checkout/account/orders');
    expect(req.request.params.get('skip')).toBe('20');
    expect(req.request.params.get('take')).toBe('10');
    req.flush({ result: { orders: [], total: 0, skip: 20, take: 10 } });
    expect((await page)?.skip).toBe(20);
  });

  it('answers null when signed out instead of throwing', async () => {
    const page = service.accountOrders();
    http.expectOne('/spark/commerce/checkout/account/orders?skip=0&take=20').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await page).toBeNull();

    const guests = service.guestOrders();
    http.expectOne('/spark/commerce/checkout/account/guest-orders').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await guests).toBeNull();
  });

  it('attaches guest orders with one POST', async () => {
    postEnvelope.mockResolvedValue({ numbers: ['2026-0001'] });
    expect((await service.attachGuestOrders()).numbers).toEqual(['2026-0001']);
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/checkout/account/guest-orders/attach', {});
  });
});
