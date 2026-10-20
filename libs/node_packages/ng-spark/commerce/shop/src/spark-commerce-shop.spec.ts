import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SparkCommerceCheckoutService } from '@mintplayer/ng-spark/commerce/checkout';
import { SparkShopProduct, SparkShopVariant } from './generated/contracts';
import { shopRefusalKey, shopRefusalOf, SparkCommerceShopService } from './spark-commerce-shop.service';
import { SparkShopProductComponent } from './spark-shop-product.component';

// Commerce #521 M12: the ng-spark client of MintPlayer.Spark.Commerce.Shop.

const ts = (en: string) => ({ Entries: [{ Tag: 'en', Value: en }] });
const eur = (amount: number) => ({ Amount: amount, Currency: 'EUR' });

const variant = (id: string, extra: Partial<SparkShopVariant> = {}): SparkShopVariant => ({
  id,
  itemId: `shop:${id}`,
  sku: id.toUpperCase(),
  gtin: null,
  options: [{ name: 'size', value: id }],
  finalPrice: eur(9.3),
  unitPrice: eur(12.4),
  netQuantity: 0.75,
  availability: 'InStock',
  available: null,
  ...extra,
});

const product = (extra: Partial<SparkShopProduct> = {}): SparkShopProduct => ({
  id: 'ShopProducts/1-A',
  slug: 'olive-oil',
  name: ts('Olive oil'),
  description: null,
  brand: 'Oliva',
  mpn: null,
  kind: 'Goods',
  organizationId: 'Organizations/seller',
  sellerName: 'Seller',
  channel: 'shop',
  currency: 'EUR',
  minimumAge: null,
  categoryIds: [],
  media: [
    { url: '/img/all.jpg', alt: ts('Bottle'), variantIds: [] },
    { url: '/img/b.jpg', alt: ts('Large bottle'), variantIds: ['b'] },
  ],
  attributes: [],
  variants: [variant('a'), variant('b', { availability: 'OutOfStock', available: 0 })],
  unitOfMeasure: 'Litre',
  unitSymbol: 'l',
  unitPriceReference: null,
  safety: {
    manufacturerName: 'Oliva SA', manufacturerAddress: 'Rue 1, Bruxelles', manufacturerContact: 'info@oliva.example',
    responsiblePersonName: null, responsiblePersonAddress: null, responsiblePersonContact: null,
    identifier: 'OL-75', gtins: ['4006381333931'], pictureUrl: '/img/all.jpg', warnings: ts('Keep away from heat.'),
  },
  legalGuaranteeMonths: 24,
  guaranteeNotice: null,
  durability: { months: 36, guarantor: 'Oliva SA', terms: null },
  withdrawalExemption: null,
  reviewsEnabled: false,
  reviewCount: 0,
  ratingAverage: null,
  ...extra,
});

describe('SparkCommerceShopService', () => {
  let http: HttpTestingController;
  let service: SparkCommerceShopService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    postEnvelope.mockResolvedValue({});
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: { postEnvelope } }],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkCommerceShopService);
  });

  afterEach(() => http.verify());

  it('sends every facet term as its own facet parameter and the price range by index (GB13)', async () => {
    const pending = service.catalog({ q: ' oil ', facets: ['colour:red', 'colour:blue', 'size:m'], price: 0, sort: 'price-asc' });
    const request = http.expectOne((r) => r.url === '/spark/commerce/shop/catalog');
    expect(request.request.params.get('q')).toBe('oil');
    expect(request.request.params.getAll('facet')).toEqual(['colour:red', 'colour:blue', 'size:m']);
    expect(request.request.params.get('price')).toBe('0');
    expect(request.request.params.get('sort')).toBe('price-asc');
    request.flush({ result: { items: [], total: 0, facets: [], priceRanges: [], typo: true } });
    expect((await pending).typo).toBe(true);
  });

  it('answers null for a product that is not published', async () => {
    const pending = service.product('gone');
    http.expectOne((r) => r.url === '/spark/commerce/shop/products' && r.params.get('slug') === 'gone')
      .flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('posts the delivery choice through the XSRF-aware envelope', async () => {
    await service.chooseDelivery({ shipTo: { name: 'A', street: 'S', postalCode: '1000', city: 'Brussel', country: 'BE' }, method: 'bpost-point', servicePointId: 'P1', servicePointCarrier: 'bpost' });
    expect(postEnvelope).toHaveBeenCalledWith('/commerce/shop/delivery', expect.objectContaining({ method: 'bpost-point', servicePointId: 'P1' }));
  });

  it('builds stable feed URLs per seller with the id in the query string (GC6)', () => {
    expect(service.feedUrl('google', 'Organizations/1-A')).toBe('/spark/commerce/shop/feeds/google?seller=Organizations%2F1-A');
    expect(service.feedUrl('meta')).toBe('/spark/commerce/shop/feeds/meta');
  });

  it('reads a shop refusal from a 409 and falls back to checkout\'s namespace', () => {
    const error = new HttpErrorResponse({ status: 409, error: { result: { code: 'deliveryRequired' } } });
    expect(shopRefusalOf(error)?.code).toBe('deliveryRequired');
    const lang = { t: (k: string) => (k === 'commerceCheckout.refusal.deliveryRequired' ? 'Choose a delivery' : k) };
    expect(shopRefusalKey('deliveryRequired', lang)).toBe('commerceCheckout.refusal.deliveryRequired');
    expect(shopRefusalKey('unknownCode', lang)).toBe('commerceCheckout.refusal.conflict');
  });
});

describe('SparkShopProductComponent', () => {
  const add = vi.fn();
  const lang = {
    t: (k: string) => ({
      'commerceShop.product.unitPrice': '{price} per {unit}',
      'commerceShop.guarantee.notice': 'Legal guarantee of {months} months',
      'commerceShop.guarantee.durability': 'Durability: {months} months',
      'commerceShop.availability.OutOfStock': 'Out of stock',
    } as Record<string, string>)[k] ?? k,
  };

  async function render(p: SparkShopProduct, variantId = ''): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SparkShopProductComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkLanguageService, useValue: lang },
        { provide: SparkCommerceShopService, useValue: { product: vi.fn().mockResolvedValue(p), reviews: vi.fn().mockResolvedValue(null) } },
        { provide: SparkCommerceCheckoutService, useValue: { add } },
      ],
    });
    const fixture = TestBed.createComponent(SparkShopProductComponent);
    fixture.componentRef.setInput('slug', p.slug);
    fixture.componentRef.setInput('variant', variantId);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    add.mockReset();
    add.mockResolvedValue({});
  });

  it('shows the computed unit price beside the FinalPrice (GA3)', async () => {
    const el = await render(product());
    expect(el.querySelector('.spark-shop-product-unit-price')?.textContent).toContain('per l');
  });

  it('shows the safety block and the guarantee notice for goods (GA7, GA8)', async () => {
    const el = await render(product());
    expect(el.querySelector('.spark-shop-product-safety')?.textContent).toContain('Oliva SA');
    expect(el.querySelector('.spark-shop-product-safety')?.textContent).toContain('4006381333931');
    const guarantee = el.querySelector('.spark-shop-product-guarantee')?.textContent ?? '';
    expect(guarantee).toContain('24 months');
    expect(guarantee).toContain('Durability: 36 months');
  });

  it('shows no guarantee notice for a service', async () => {
    const el = await render(product({ kind: 'Service', safety: null, durability: null }));
    expect(el.querySelector('.spark-shop-product-guarantee')).toBeNull();
  });

  it('shows the chosen variant\'s own image first (GB13) and refuses to add an out-of-stock variant', async () => {
    const el = await render(product(), 'b');
    const main = el.querySelector('.spark-shop-product-main') as HTMLImageElement;
    expect(main.getAttribute('src')).toBe('/img/b.jpg');
    const button = el.querySelector('.spark-shop-product-buy button') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it('adds the chosen variant to the basket by its checkout item id', async () => {
    const el = await render(product());
    (el.querySelector('.spark-shop-product-buy button') as HTMLButtonElement).click();
    await Promise.resolve();
    expect(add).toHaveBeenCalledWith({ itemId: 'shop:a', quantity: 1 });
  });
});
