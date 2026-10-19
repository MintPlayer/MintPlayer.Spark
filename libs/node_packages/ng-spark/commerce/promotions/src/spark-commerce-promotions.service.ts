import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkBackInStockRequest,
  SparkBackInStockResult,
  SparkBackInStockView,
  SparkPromotionCodeInfo,
  SparkPromotionCodeRequest,
  SparkPromotionCodeView,
  SparkPromotionRefusal,
  SparkSalePriceDisplay,
  SparkSalePriceRequest,
  SparkSalePriceView,
  SparkTrackingLinkRequest,
  SparkTrackingReport,
  SparkTrackRequest,
  SparkTrackResult,
  SparkWaitingListEntryView,
  SparkWaitingListRequest,
  SparkWaitingListView,
} from './generated/contracts';

/** The refusal a promotions endpoint answered (HTTP 409 with a {@link SparkPromotionRefusal}), or `null`. */
export function promotionRefusalOf(error: unknown): SparkPromotionRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const body = error.error as { result?: unknown } | null | undefined;
  const candidate = body && typeof body === 'object' && 'result' in body ? body.result : body;
  return candidate && typeof candidate === 'object' && typeof (candidate as SparkPromotionRefusal).code === 'string'
    ? (candidate as SparkPromotionRefusal)
    : null;
}

/** The translation key of a promotions refusal code (also used for checkout refusals a promotions step raised). */
export function promotionRefusalKey(code: string): string {
  return 'commercePromotions.refusal.' + code;
}

/** The basket choice the code pricing step reads (`ISparkCheckout.SetChoicesAsync`). */
export const SPARK_PROMOTION_CODE_CHOICE = 'promotionCode';

/** The item a buyer adds to buy `itemId` with the capacity-blocking code `code` (a waiting-list offer, a partner allocation). */
export function blockedItemId(code: string, itemId: string): string {
  return `promotions:${code}|${itemId}`;
}

/**
 * Client for `MintPlayer.Spark.Commerce.Promotions`' endpoints (`/spark/commerce/promotions/*`, commerce #521 M8).
 * Reads are GETs answered in the add-on envelope; POSTs go through {@link SparkService.postEnvelope} (XSRF header).
 * Ids travel in bodies and query strings, never in paths.
 */
@Injectable({ providedIn: 'root' })
export class SparkCommercePromotionsService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  private async get<T>(path: string, params: Record<string, string>): Promise<T | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(`${this.baseUrl}/commerce/promotions${path}`, { params }));
      return envelope?.result ?? null;
    } catch (error) {
      if (error instanceof HttpErrorResponse && (error.status === 404 || error.status === 401)) return null;
      throw error;
    }
  }

  private post<T>(path: string, body: object): Promise<T> {
    return this.spark.postEnvelope<T>(`/commerce/promotions${path}`, { ...body });
  }

  /** `GET …/code`: what a typed code does (null when it is not valid for the caller). */
  code(code: string): Promise<SparkPromotionCodeInfo | null> {
    return this.get<SparkPromotionCodeInfo>('/code', { code });
  }

  /** `GET …/sale-price`: the price during a sale with its Omnibus prior price (GA3). */
  salePrice(itemId: string): Promise<SparkSalePriceDisplay | null> {
    return this.get<SparkSalePriceDisplay>('/sale-price', { itemId });
  }

  /** `POST …/track`: attributes the visit to a tracking link (GC2); stored only with the consent the host requires. */
  track(request: SparkTrackRequest): Promise<SparkTrackResult> {
    return this.post<SparkTrackResult>('/track', request);
  }

  /**
   * Reads `ref` and the UTM parameters from a URL's query string and attributes the visit. Call it once on landing
   * (after the consent banner had its chance); does nothing without `ref`.
   */
  async trackFromUrl(url: string): Promise<boolean> {
    const query = new URL(url, 'http://localhost').searchParams;
    const ref = query.get('ref');
    if (!ref) return false;
    const result = await this.track({
      ref,
      utmSource: query.get('utm_source'),
      utmMedium: query.get('utm_medium'),
      utmCampaign: query.get('utm_campaign'),
      utmContent: query.get('utm_content'),
    });
    return result.attributed;
  }

  /** `GET …/waiting-lists/get`. */
  waitingList(id: string): Promise<SparkWaitingListView | null> {
    return this.get<SparkWaitingListView>('/waiting-lists/get', { id });
  }

  /** `GET …/waiting-lists/mine`: the signed-in buyer's entry. */
  myWaitingListEntry(listId: string): Promise<SparkWaitingListEntryView | null> {
    return this.get<SparkWaitingListEntryView>('/waiting-lists/mine', { id: listId });
  }

  /** `POST …/waiting-lists/join` (signed in). */
  joinWaitingList(listId: string, quantity: number): Promise<SparkWaitingListEntryView> {
    return this.post<SparkWaitingListEntryView>('/waiting-lists/join', { listId, quantity });
  }

  /** `POST …/waiting-lists/leave`. */
  leaveWaitingList(listId: string): Promise<unknown> {
    return this.post('/waiting-lists/leave', { id: listId });
  }

  /** `POST …/back-in-stock/subscribe` (GB11); a guest passes the address and confirms it by mail. */
  subscribeBackInStock(request: SparkBackInStockRequest): Promise<SparkBackInStockResult> {
    return this.post<SparkBackInStockResult>('/back-in-stock/subscribe', request);
  }

  /** `POST …/back-in-stock/confirm`: the double opt-in link. */
  confirmBackInStock(id: string, token: string): Promise<unknown> {
    return this.post('/back-in-stock/confirm', { id, token });
  }

  /** `POST …/back-in-stock/unsubscribe`: from the mail's link. */
  unsubscribeBackInStock(id: string, token: string): Promise<unknown> {
    return this.post('/back-in-stock/unsubscribe', { id, token });
  }

  /** `GET …/back-in-stock/mine`: the account area's list (GB10). */
  async myBackInStock(): Promise<SparkBackInStockView[]> {
    return (await this.get<SparkBackInStockView[]>('/back-in-stock/mine', {})) ?? [];
  }

  /** `POST …/back-in-stock/delete`. */
  deleteBackInStock(id: string): Promise<unknown> {
    return this.post('/back-in-stock/delete', { id });
  }

  // ---- back office ----

  /** `POST …/admin/codes/save`. */
  saveCode(request: SparkPromotionCodeRequest): Promise<{ code: string }> {
    return this.post<{ code: string }>('/admin/codes/save', request);
  }

  /** `POST …/admin/codes/deactivate`. */
  deactivateCode(code: string): Promise<unknown> {
    return this.post('/admin/codes/deactivate', { code });
  }

  /** `GET …/admin/codes/get`: uses and block. */
  codeAdmin(code: string): Promise<SparkPromotionCodeView | null> {
    return this.get<SparkPromotionCodeView>('/admin/codes/get', { code });
  }

  /** `POST …/admin/sale-prices/schedule` (GA3). */
  scheduleSalePrice(request: SparkSalePriceRequest): Promise<SparkSalePriceView> {
    return this.post<SparkSalePriceView>('/admin/sale-prices/schedule', request);
  }

  /** `POST …/admin/sale-prices/cancel`. */
  cancelSalePrice(id: string): Promise<SparkSalePriceView> {
    return this.post<SparkSalePriceView>('/admin/sale-prices/cancel', { id });
  }

  /** `POST …/admin/waiting-lists/create`. */
  createWaitingList(request: SparkWaitingListRequest): Promise<SparkWaitingListView> {
    return this.post<SparkWaitingListView>('/admin/waiting-lists/create', request);
  }

  /** `POST …/admin/waiting-lists/close-queue`. */
  closeWaitingListQueue(id: string): Promise<unknown> {
    return this.post('/admin/waiting-lists/close-queue', { id });
  }

  /** `POST …/admin/waiting-lists/close`. */
  closeWaitingList(id: string): Promise<unknown> {
    return this.post('/admin/waiting-lists/close', { id });
  }

  /** `POST …/admin/links/save` (GC2). */
  saveTrackingLink(request: SparkTrackingLinkRequest): Promise<{ ref: string }> {
    return this.post<{ ref: string }>('/admin/links/save', request);
  }

  /** `GET …/admin/links/report`. */
  trackingReport(ref: string): Promise<SparkTrackingReport | null> {
    return this.get<SparkTrackingReport>('/admin/links/report', { ref });
  }
}
