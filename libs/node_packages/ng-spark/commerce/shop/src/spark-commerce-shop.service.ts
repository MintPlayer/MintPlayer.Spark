import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkShopCatalogPage,
  SparkShopCategory,
  SparkShopDeliveryChoice,
  SparkShopDeliveryOptions,
  SparkShopDeliveryRequest,
  SparkShopOrder,
  SparkShopProduct,
  SparkShopRefusal,
  SparkShopReviewPage,
  SparkShopReviewRequest,
  SparkShopReviewResult,
} from './generated/contracts';

/** A catalogue question (GB13): words, a category, facet terms (`colour:red`), a price range index, a sort, a page. */
export interface SparkShopCatalogSearch {
  q?: string;
  category?: string;
  facets?: string[];
  price?: number | null;
  sort?: 'relevance' | 'newest' | 'price-asc' | 'price-desc' | 'rating' | '';
  seller?: string;
  skip?: number;
  take?: number;
}

/** The shop refusal carried by an HTTP 409, or null. */
export function shopRefusalOf(error: unknown): SparkShopRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const body = error.error as { result?: unknown } | null | undefined;
  const candidate = body && typeof body === 'object' && 'result' in body ? body.result : body;
  return candidate && typeof candidate === 'object' && typeof (candidate as SparkShopRefusal).code === 'string'
    ? (candidate as SparkShopRefusal)
    : null;
}

/** The translation key of a shop refusal: the shop's own, then Checkout's (a refusal raised through Checkout's seams). */
export function shopRefusalKey(code: string, lang?: { t(key: string): string }): string {
  const own = 'commerceShop.refusal.' + code;
  if (!lang || lang.t(own) !== own) return own;
  const checkout = 'commerceCheckout.refusal.' + code;
  return lang.t(checkout) !== checkout ? checkout : 'commerceCheckout.refusal.conflict';
}

/**
 * Client for `MintPlayer.Spark.Commerce.Shop`'s endpoints (`/spark/commerce/shop/*`, commerce #521 M12).
 *
 * Reads are GETs answering the add-on envelope (`{ result }`); a missing or unpublished product answers `null`.
 * POSTs go through {@link SparkService.postEnvelope} so the XSRF header goes with them. A refusal rejects with the HTTP
 * 409 error; read it with {@link shopRefusalOf}. Ids travel in query strings and bodies, never in paths.
 */
@Injectable({ providedIn: 'root' })
export class SparkCommerceShopService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** `GET /spark/commerce/shop/products?slug=`: a published product's page; null when there is none. */
  async product(slug: string): Promise<SparkShopProduct | null> {
    return this.getOrNull<SparkShopProduct>('/commerce/shop/products', { slug });
  }

  /** `GET /spark/commerce/shop/catalog`: a page of published products with facet counts. */
  async catalog(search: SparkShopCatalogSearch = {}): Promise<SparkShopCatalogPage> {
    const params: Record<string, string | string[]> = {};
    if (search.q?.trim()) params['q'] = search.q.trim();
    if (search.category) params['category'] = search.category;
    if (search.facets?.length) params['facet'] = search.facets;
    if (search.price !== undefined && search.price !== null && search.price >= 0) params['price'] = String(search.price);
    if (search.sort) params['sort'] = search.sort;
    if (search.seller) params['seller'] = search.seller;
    if (search.skip) params['skip'] = String(search.skip);
    if (search.take) params['take'] = String(search.take);
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkShopCatalogPage } | null>(`${this.baseUrl}/commerce/shop/catalog`, { params }));
    return envelope?.result ?? { items: [], total: 0, facets: [], priceRanges: [], typo: false };
  }

  /** `GET /spark/commerce/shop/categories`: every category in tree order. */
  async categories(): Promise<SparkShopCategory[]> {
    return (await this.getOrNull<SparkShopCategory[]>('/commerce/shop/categories', {})) ?? [];
  }

  /** `GET /spark/commerce/shop/reviews?productId=`: published reviews with the summary and the verification flag (GC5). */
  async reviews(productId: string, skip = 0, take = 20): Promise<SparkShopReviewPage | null> {
    return this.getOrNull<SparkShopReviewPage>('/commerce/shop/reviews', { productId, skip: String(skip), take: String(take) });
  }

  /** `POST /spark/commerce/shop/reviews`: a review of a delivered line of the caller's own order. */
  review(request: SparkShopReviewRequest): Promise<SparkShopReviewResult> {
    return this.spark.postEnvelope<SparkShopReviewResult>('/commerce/shop/reviews', { ...request });
  }

  /** `GET /spark/commerce/shop/delivery/options?country=&postalCode=`: the rates for the basket and the current choice. */
  async deliveryOptions(country: string, postalCode?: string): Promise<SparkShopDeliveryOptions> {
    const params: Record<string, string> = { country };
    if (postalCode?.trim()) params['postalCode'] = postalCode.trim();
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkShopDeliveryOptions } | null>(`${this.baseUrl}/commerce/shop/delivery/options`, { params }));
    return envelope?.result ?? { needsDelivery: false, rates: [], chosen: null };
  }

  /** `POST /spark/commerce/shop/delivery`: the buyer's delivery choice (address, method, service point). */
  chooseDelivery(request: SparkShopDeliveryRequest): Promise<SparkShopDeliveryChoice> {
    return this.spark.postEnvelope<SparkShopDeliveryChoice>('/commerce/shop/delivery', { ...request });
  }

  /** `GET /spark/commerce/shop/orders?number=`: the shop's side of the caller's own order (one entry per seller). */
  async order(number: string): Promise<SparkShopOrder[] | null> {
    return this.getOrNull<SparkShopOrder[]>('/commerce/shop/orders', { number });
  }

  /** The stable URL of a seller's product feed (GC6); omit `seller` for the platform's own products. */
  feedUrl(kind: 'google' | 'meta', seller?: string | null): string {
    const query = seller ? `?${new URLSearchParams({ seller })}` : '';
    return `${this.baseUrl}/commerce/shop/feeds/${kind}${query}`;
  }

  private async getOrNull<T>(path: string, params: Record<string, string>): Promise<T | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(`${this.baseUrl}${path}`, { params }));
      return envelope?.result ?? null;
    } catch (e) {
      if (e instanceof HttpErrorResponse && (e.status === 404 || e.status === 401)) return null;
      throw e;
    }
  }
}
