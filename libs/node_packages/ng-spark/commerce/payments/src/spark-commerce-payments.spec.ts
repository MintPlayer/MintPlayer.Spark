import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkCommercePaymentsService } from './spark-commerce-payments.service';
import { SparkSellerPaymentAccountComponent } from './spark-seller-payment-account.component';

// Commerce #521 M6: the ng-spark client of MintPlayer.Spark.Commerce.Payments.

describe('SparkCommercePaymentsService', () => {
  let http: HttpTestingController;
  let service: SparkCommercePaymentsService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkService, useValue: { postEnvelope } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommercePaymentsService);
  });

  afterEach(() => http.verify());

  it('asks for the methods of a channel and never sends a country (GA11)', async () => {
    const pending = service.methods('tickets');
    const request = http.expectOne((r) => r.url === '/spark/commerce/payments/methods');
    expect(request.request.params.get('channel')).toBe('tickets');
    expect(request.request.params.keys().some((k) => /country/i.test(k))).toBe(false);
    request.flush({ result: [{ id: 'ideal', provider: 'mollie', maxExpiryMinutes: 15, supportsAuthorization: false, supportsSplit: false }] });
    expect((await pending).map((m) => m.id)).toEqual(['ideal']);
  });

  it('answers null for a seller account the caller may not see', async () => {
    const pending = service.status('Organizations/1');
    http.expectOne((r) => r.url === '/spark/commerce/payments/connect/status').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('posts the onboarding start with the id in the body', async () => {
    postEnvelope.mockResolvedValue({ redirectUrl: 'https://connect.example/onboard' });
    expect(await service.startOnboarding('Organizations/1')).toBe('https://connect.example/onboard');
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/payments/connect/start', { organizationId: 'Organizations/1' });
  });

  it('builds the evidence download URL with the id escaped', () => {
    expect(service.evidenceUrl('PaymentDisputes/stripe/dp_1')).toBe('/spark/commerce/payments/disputes/evidence?id=PaymentDisputes%2Fstripe%2Fdp_1');
  });
});

describe('SparkSellerPaymentAccountComponent', () => {
  it('shows the state and sends an unverified seller to the provider', async () => {
    const status = vi.fn().mockResolvedValue({ provider: 'stripe', linked: true, kyc: 'pending', chargesEnabled: false, payoutsEnabled: false, requirements: ['external_account'] });
    const startOnboarding = vi.fn().mockResolvedValue('https://connect.example/onboard');
    TestBed.configureTestingModule({
      imports: [SparkSellerPaymentAccountComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkCommercePaymentsService, useValue: { status, startOnboarding } },
      ],
    });
    const fixture = TestBed.createComponent(SparkSellerPaymentAccountComponent);
    const navigate = vi.fn();
    fixture.componentRef.setInput('organizationId', 'Organizations/1');
    fixture.componentRef.setInput('navigate', navigate);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-kyc="pending"]')).not.toBeNull();
    expect(element.textContent).toContain('external_account');
    element.querySelector('button')!.click();
    await fixture.whenStable();
    expect(startOnboarding).toHaveBeenCalledWith('Organizations/1', undefined);
    expect(navigate).toHaveBeenCalledWith('https://connect.example/onboard');
  });
});
