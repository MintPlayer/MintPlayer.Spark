import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkShopOrder } from './generated/contracts';
import { SparkCommerceShopService } from './spark-commerce-shop.service';
import { emptyShopAddress, shopAddressLine } from './spark-shop-address-book.component';
import { trackableParcels } from './spark-shop-order.component';

// M17 / GB10: the address book and the buyer order page's shop side.

describe('SparkCommerceShopService (account area)', () => {
  let http: HttpTestingController;
  let service: SparkCommerceShopService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    postEnvelope.mockResolvedValue({ entries: [] });
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: { postEnvelope } }],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommerceShopService);
  });

  afterEach(() => http.verify());

  it('reads the address book, null when signed out', async () => {
    const book = service.addressBook();
    http.expectOne('/spark/commerce/shop/account/addresses').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await book).toBeNull();
  });

  it('saves and deletes through POST bodies', async () => {
    const address = { ...emptyShopAddress(), name: 'A', street: 'Kerkstraat', postalCode: '9000', city: 'Gent' };
    await service.saveAddress({ address, isDefault: true });
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/shop/account/addresses', { address, isDefault: true });
    await service.deleteAddress('e1');
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/shop/account/addresses/delete', { entryId: 'e1' });
  });
});

describe('address and parcel helpers', () => {
  it('renders an address on one line without empty parts', () => {
    expect(shopAddressLine({ ...emptyShopAddress('BE'), name: 'A', street: 'Kerkstraat', houseNumber: '12', postalCode: '9770', city: 'Kruisem' }))
      .toBe('A, Kerkstraat 12, 9770 Kruisem, BE');
  });

  it('lists trackable parcels, outbound before returns', () => {
    const orders = [{
      orderNumber: '1', state: 'Shipped', lines: [],
      shipments: [
        { return: true, status: 'Labelled', parcels: [{ number: 1, trackingUrl: 'https://t/r', status: 'Labelled' }] },
        { return: false, status: 'InTransit', parcels: [{ number: 1, trackingUrl: 'https://t/o', status: 'InTransit' }, { number: 2, status: 'Pending' }] },
      ],
    }] as SparkShopOrder[];
    expect(trackableParcels(orders).map((p) => p.parcel.trackingUrl)).toEqual(['https://t/o', 'https://t/r']);
  });
});
