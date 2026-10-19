import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkCommerceShippingService } from './spark-commerce-shipping.service';
import { SparkServicePointPickerComponent } from './spark-service-point-picker.component';
import { SparkServicePoint } from './generated/contracts';

// Commerce #521 M11: the ng-spark client of MintPlayer.Spark.Commerce.Shipping.

const point = (id: string, kind: 'pickupPoint' | 'parcelLocker' = 'pickupPoint'): SparkServicePoint => ({
  id,
  carrier: 'test',
  name: `Point ${id}`,
  kind,
  address: { street: 'Teststraat', houseNumber: '1', postalCode: '1000', city: 'Brussel', country: 'BE' },
  distanceMetres: 350,
  openingHours: {},
});

describe('SparkCommerceShippingService', () => {
  let http: HttpTestingController;
  let service: SparkCommerceShippingService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommerceShippingService);
  });

  afterEach(() => http.verify());

  it('searches service points with the address in the query string (GB5)', async () => {
    const pending = service.servicePoints({ country: 'BE', postalCode: '1000', carrier: 'bpost', kind: 'parcelLocker' });
    const request = http.expectOne((r) => r.url === '/spark/commerce/shipping/service-points');
    expect(request.request.params.get('country')).toBe('BE');
    expect(request.request.params.get('postalCode')).toBe('1000');
    expect(request.request.params.get('carrier')).toBe('bpost');
    expect(request.request.params.get('kind')).toBe('parcelLocker');
    request.flush({ result: [point('a', 'parcelLocker')] });
    expect((await pending).map((p) => p.id)).toEqual(['a']);
  });

  it('asks rates by delivery country and subtotal, never by the buyer\'s nationality (GA11)', async () => {
    const pending = service.rates({ country: 'NL', subtotal: 42.5, weightGrams: 1200.4 });
    const request = http.expectOne((r) => r.url === '/spark/commerce/shipping/rates');
    expect(request.request.params.get('country')).toBe('NL');
    expect(request.request.params.get('subtotal')).toBe('42.5');
    expect(request.request.params.get('weight')).toBe('1200');
    expect(request.request.params.keys().some((k) => /nationality|residence/i.test(k))).toBe(false);
    request.flush({ result: [] });
    expect(await pending).toEqual([]);
  });

  it('puts ids in the query string, never in the path', () => {
    expect(service.labelUrl('Shipments/1-A', 2)).toBe('/spark/commerce/shipping/label?id=Shipments%2F1-A&parcel=2');
    expect(service.customsDocumentUrl('Shipments/1-A')).toBe('/spark/commerce/shipping/customs-document?id=Shipments%2F1-A');
  });
});

describe('SparkServicePointPickerComponent', () => {
  const servicePoints = vi.fn();

  beforeEach(() => {
    servicePoints.mockReset();
    TestBed.configureTestingModule({
      imports: [SparkServicePointPickerComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkCommerceShippingService, useValue: { servicePoints } },
      ],
    });
  });

  it('lists the points and selects the one the buyer picks', async () => {
    servicePoints.mockResolvedValue([point('a'), point('b', 'parcelLocker')]);
    const fixture = TestBed.createComponent(SparkServicePointPickerComponent);
    fixture.componentRef.setInput('country', 'BE');
    fixture.componentRef.setInput('postalCode', '1000');
    fixture.componentRef.setInput('carrier', 'bpost');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(servicePoints).toHaveBeenCalledWith(expect.objectContaining({ country: 'BE', postalCode: '1000', carrier: 'bpost' }));
    const radios = fixture.nativeElement.querySelectorAll('input[type=radio]') as NodeListOf<HTMLInputElement>;
    expect(radios.length).toBe(2);
    radios[1].dispatchEvent(new Event('change'));
    expect(fixture.componentInstance.selected()?.id).toBe('b');
  });

  it('searches nothing without a postal code', async () => {
    const fixture = TestBed.createComponent(SparkServicePointPickerComponent);
    fixture.componentRef.setInput('country', 'BE');
    fixture.componentRef.setInput('postalCode', '  ');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(servicePoints).not.toHaveBeenCalled();
  });

  it('drops a selection the new search no longer lists', async () => {
    servicePoints.mockResolvedValue([point('a')]);
    const fixture = TestBed.createComponent(SparkServicePointPickerComponent);
    fixture.componentRef.setInput('country', 'BE');
    fixture.componentRef.setInput('postalCode', '1000');
    fixture.componentInstance.selected.set(point('gone'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.selected()).toBeNull();
  });
});
