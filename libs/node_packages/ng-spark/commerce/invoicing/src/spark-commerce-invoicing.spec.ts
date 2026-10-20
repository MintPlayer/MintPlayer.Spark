import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkCommerceInvoicingService } from './spark-commerce-invoicing.service';
import { SparkOrderInvoicesComponent } from './spark-order-invoices.component';

// Commerce #521 M10: the ng-spark client of MintPlayer.Spark.Commerce.Invoicing.

describe('SparkCommerceInvoicingService', () => {
  let http: HttpTestingController;
  let service: SparkCommerceInvoicingService;
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
    service = TestBed.inject(SparkCommerceInvoicingService);
  });

  afterEach(() => http.verify());

  it('lists an order\'s documents by number in the query string', async () => {
    const pending = service.documents('A1B2');
    const request = http.expectOne((r) => r.url === '/spark/commerce/invoicing/documents');
    expect(request.request.params.get('number')).toBe('A1B2');
    request.flush({ result: [{ id: 'Invoices/r-1', number: 'R2026-000001', kind: 'receipt', issueDate: '2026-10-19', total: 12.1, currency: 'EUR', pdfUrl: '/spark/commerce/invoicing/pdf?token=x' }] });
    expect((await pending).map((d) => d.number)).toEqual(['R2026-000001']);
  });

  it('answers no documents for an order the caller may not see', async () => {
    const pending = service.documents('A1B2');
    http.expectOne((r) => r.url === '/spark/commerce/invoicing/documents').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toEqual([]);
  });

  it('posts billing details with the order number in the body', async () => {
    postEnvelope.mockResolvedValue({ outcome: 'saved' });
    const request = { orderNumber: 'A1B2', invoiceRequested: true, name: 'Klant BV', vatId: 'BE0123456749' };
    expect(await service.saveBillingDetails(request)).toBe('saved');
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/invoicing/billing', request);
  });

  it('builds export links', () => {
    expect(service.exportUrl('oss', { issuer: 'platform', year: 2026, quarter: 4 }))
      .toBe('/spark/commerce/invoicing/exports/oss?issuer=platform&year=2026&quarter=4');
  });
});

describe('SparkOrderInvoicesComponent', () => {
  it('shows each document with its PDF link', async () => {
    const documents = vi.fn().mockResolvedValue([
      { id: 'Invoices/r-1', number: 'R2026-000001', kind: 'receipt', issueDate: '2026-10-19', total: 12.1, currency: 'EUR', pdfUrl: '/pdf?token=a' },
      { id: 'Invoices/c-1', number: 'C2026-000001', kind: 'creditNote', issueDate: '2026-10-20', total: 12.1, currency: 'EUR', creditedNumber: 'R2026-000001', pdfUrl: '/pdf?token=b' },
    ]);
    TestBed.configureTestingModule({
      imports: [SparkOrderInvoicesComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkCommerceInvoicingService, useValue: { documents } },
      ],
    });
    const fixture = TestBed.createComponent(SparkOrderInvoicesComponent);
    fixture.componentRef.setInput('orderNumber', 'A1B2');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(documents).toHaveBeenCalledWith('A1B2');
    expect(element.querySelectorAll('li')).toHaveLength(2);
    expect(element.querySelector('[data-kind="creditNote"] a')?.getAttribute('href')).toBe('/pdf?token=b');
    expect(element.textContent).toContain('R2026-000001');
  });
});
