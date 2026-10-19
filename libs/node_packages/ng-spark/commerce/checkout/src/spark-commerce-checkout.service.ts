import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkBackOfficeOrderRequest,
  SparkBackOfficeResult,
  SparkBasket,
  SparkBasketAddRequest,
  SparkCancellationRequest,
  SparkCheckoutRefusal,
  SparkGuaranteeClaimRequest,
  SparkOrderAftercare,
  SparkRefundResult,
  SparkRescheduleChoiceRequest,
  SparkWithdrawalRequest,
  SparkWithdrawalResult,
  SparkCheckoutSummary,
  SparkLineChangeRequest,
  SparkMarkPaidRequest,
  SparkOrderView,
  SparkPlaceOrderRequest,
  SparkPlaceOrderResult,
} from './generated/contracts';

/**
 * The refusal a checkout endpoint answered (HTTP 409 whose envelope result is a {@link SparkCheckoutRefusal}), or
 * `null` for any other error.
 */
export function refusalOf(error: unknown): SparkCheckoutRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const body = error.error as { result?: unknown } | null | undefined;
  const candidate = body && typeof body === 'object' && 'result' in body ? body.result : body;
  return candidate && typeof candidate === 'object' && typeof (candidate as SparkCheckoutRefusal).code === 'string'
    ? (candidate as SparkCheckoutRefusal)
    : null;
}

/**
 * Where refusal texts live, tried in order: Checkout's own, then the packages that refuse through Checkout's seams
 * (a promotion code in a pricing step, a sale phase or presale code at admission). A host may push its own namespace
 * (`'myShop.refusal'`) for the codes of its own steps.
 */
export const SPARK_CHECKOUT_REFUSAL_NAMESPACES: string[] = [
  'commerceCheckout.refusal',
  'commercePromotions.refusal',
  'ticketingEvents.refusal',
  'commerceWaitingRoom.refusal',
];

/** The generic text shown for a code no namespace translates. */
export const SPARK_CHECKOUT_REFUSAL_FALLBACK = 'commerceCheckout.refusal.conflict';

/**
 * The translation key of a refusal code. Without `lang` it is Checkout's own key. With `lang` it is the first key of
 * {@link SPARK_CHECKOUT_REFUSAL_NAMESPACES} that has a text, else {@link SPARK_CHECKOUT_REFUSAL_FALLBACK}, so a code
 * raised by another package (`promotionCodeInvalid`) never shows as a raw key.
 */
export function refusalKey(code: string, lang?: { t(key: string): string }): string {
  const own = 'commerceCheckout.refusal.' + code;
  if (!lang) return own;
  for (const ns of SPARK_CHECKOUT_REFUSAL_NAMESPACES) {
    const key = `${ns}.${code}`;
    if (lang.t(key) !== key) return key;
  }
  return SPARK_CHECKOUT_REFUSAL_FALLBACK;
}

/**
 * Client for `MintPlayer.Spark.Commerce.Checkout`'s endpoints (`/spark/commerce/checkout/*`, commerce #521).
 *
 * Reads are GETs whose answer is the add-on envelope (`{ result }`). POSTs go through {@link SparkService.postEnvelope},
 * so the app's XSRF header goes with them (the endpoints require the antiforgery token). A refusal rejects with the
 * HTTP 409 error; read it with {@link refusalOf}. Ids and numbers travel in bodies and query strings, never in paths.
 */
@Injectable({ providedIn: 'root' })
export class SparkCommerceCheckoutService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** `GET /spark/commerce/checkout/basket`: the current buyer's basket. */
  async basket(): Promise<SparkBasket | null> {
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkBasket } | null>(`${this.baseUrl}/commerce/checkout/basket`));
    return envelope?.result ?? null;
  }

  /** `POST /spark/commerce/checkout/basket/add`. */
  add(request: SparkBasketAddRequest): Promise<SparkBasket> {
    return this.spark.postEnvelope<SparkBasket>('/commerce/checkout/basket/add', { ...request });
  }

  /** `POST /spark/commerce/checkout/basket/quantity`: 0 removes the line. */
  setQuantity(lineId: string, quantity: number): Promise<SparkBasket> {
    return this.spark.postEnvelope<SparkBasket>('/commerce/checkout/basket/quantity', { lineId, quantity });
  }

  /** `POST /spark/commerce/checkout/basket/choices`: donation, insurance, promotion codes; null removes a choice. */
  setChoices(choices: Record<string, string | null>, email?: string | null): Promise<SparkBasket> {
    return this.spark.postEnvelope<SparkBasket>('/commerce/checkout/basket/choices', { choices, ...(email ? { email } : {}) });
  }

  /** `GET /spark/commerce/checkout/summary`: the last step (GA2, CRD Art. 6 and 8(2)). */
  async summary(): Promise<SparkCheckoutSummary | null> {
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkCheckoutSummary } | null>(`${this.baseUrl}/commerce/checkout/summary`));
    return envelope?.result ?? null;
  }

  /** `POST /spark/commerce/checkout/place`: "Order with obligation to pay". */
  place(request: SparkPlaceOrderRequest): Promise<SparkPlaceOrderResult> {
    return this.spark.postEnvelope<SparkPlaceOrderResult>('/commerce/checkout/place', { ...request });
  }

  /** `GET /spark/commerce/checkout/order?number=`: the buyer's own order; `null` when it is not theirs or absent. */
  async order(number: string): Promise<SparkOrderView | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkOrderView } | null>(
        `${this.baseUrl}/commerce/checkout/order`, { params: { number } }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }

  /** `POST /spark/commerce/checkout/order/cancel-payment`: the buyer abandons the payment (D3). */
  cancelPayment(number: string): Promise<SparkOrderView> {
    return this.spark.postEnvelope<SparkOrderView>('/commerce/checkout/order/cancel-payment', { number });
  }

  /** `POST /spark/commerce/checkout/box-office/orders` (GB7). */
  createBackOfficeOrder(request: SparkBackOfficeOrderRequest): Promise<SparkBackOfficeResult> {
    return this.spark.postEnvelope<SparkBackOfficeResult>('/commerce/checkout/box-office/orders', { ...request });
  }

  /** `POST /spark/commerce/checkout/box-office/mark-paid`: finance marks a manual order paid. */
  markPaid(request: SparkMarkPaidRequest): Promise<SparkBackOfficeResult> {
    return this.spark.postEnvelope<SparkBackOfficeResult>('/commerce/checkout/box-office/mark-paid', { ...request });
  }

  /** `POST /spark/commerce/checkout/box-office/resend`: mails the confirmation again. */
  resend(orderId: string): Promise<SparkBackOfficeResult> {
    return this.spark.postEnvelope<SparkBackOfficeResult>('/commerce/checkout/box-office/resend', { orderId });
  }

  /** `POST /spark/commerce/checkout/box-office/change-line`: a seat change or upgrade (GB7). */
  changeLine(request: SparkLineChangeRequest): Promise<SparkBackOfficeResult> {
    return this.spark.postEnvelope<SparkBackOfficeResult>('/commerce/checkout/box-office/change-line', { ...request });
  }

  // ---- after the sale (M9): withdrawal (GA1), cancellation (D20), guarantee claims (GA8), reschedule ----------------

  /**
   * `GET /spark/commerce/checkout/refunds/order?number=&access=`: the order's after-sale state with the withdrawal
   * function; `access` is a guest's signed order link token. `null` when the order is not the caller's.
   */
  async aftercare(number: string, access?: string | null): Promise<SparkOrderAftercare | null> {
    try {
      const params: Record<string, string> = access ? { number, access } : { number };
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkOrderAftercare } | null>(
        `${this.baseUrl}/commerce/checkout/refunds/order`, { params }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }

  /** `POST /spark/commerce/checkout/refunds/withdraw`: `confirm: false` previews, `true` records (no approval step, GA1). */
  withdraw(request: SparkWithdrawalRequest): Promise<SparkWithdrawalResult> {
    return this.spark.postEnvelope<SparkWithdrawalResult>('/commerce/checkout/refunds/withdraw', { ...request });
  }

  /** `POST /spark/commerce/checkout/refunds/cancel`: a cancellation within the seller's policy (D20). */
  cancel(request: SparkCancellationRequest): Promise<SparkRefundResult> {
    return this.spark.postEnvelope<SparkRefundResult>('/commerce/checkout/refunds/cancel', { ...request });
  }

  /** `POST /spark/commerce/checkout/refunds/claim`: a legal-guarantee claim (GA8). */
  claim(request: SparkGuaranteeClaimRequest): Promise<SparkOrderAftercare> {
    return this.spark.postEnvelope<SparkOrderAftercare>('/commerce/checkout/refunds/claim', { ...request });
  }

  /** `POST /spark/commerce/checkout/refunds/reschedule`: keep or refund after a reschedule (D20). */
  chooseReschedule(request: SparkRescheduleChoiceRequest): Promise<SparkOrderAftercare> {
    return this.spark.postEnvelope<SparkOrderAftercare>('/commerce/checkout/refunds/reschedule', { ...request });
  }

  /** The URL of a legal document version's PDF. */
  legalDocumentPdfUrl(id: string): string {
    return `${this.baseUrl}/commerce/legal/pdf?id=${encodeURIComponent(id)}`;
  }
}
