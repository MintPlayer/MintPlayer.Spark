import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  CommerceDsaTransparencyReport,
  CommerceLegalDocumentView,
  CommerceNoticeReceipt,
  CommercePriceDisplay,
  CommerceRankingParameter,
  CommerceSellerProfileView,
  CommerceTraderPage,
} from './generated/contracts';

/** A raw price to quote in a channel (`GET /spark/commerce/price`). */
export interface SparkCommerceQuote {
  channel: string;
  amount: number;
  currency?: string;
  basis?: 'gross' | 'net';
  /** VAT rate in basis points (2100 = 21 %). */
  vat?: number;
  quantity?: number;
}

/** A DSA notice (Art. 16), as the notice form sends it. */
export interface SparkCommerceNotice {
  targetKind: string;
  targetId: string;
  reason: string;
  explanation: string;
  location?: string | null;
  contactName?: string | null;
  contactEmail?: string | null;
  goodFaith: boolean;
}

/**
 * Client for `MintPlayer.Spark.Commerce`'s endpoints (`/spark/commerce/*`, commerce #521 M4).
 *
 * Reads are GETs whose answer is the add-on envelope (`{ result }`); a refusal (401/404) resolves to
 * `null`. Writes go through {@link SparkService.postEnvelope}, so the app's XSRF header goes with them
 * and envelope operations are dispatched; a validation failure rejects with the server's errors.
 */
@Injectable({ providedIn: 'root' })
export class SparkCommerceService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** The final price of a raw price (default 4): fees included, broken down. */
  quote(quote: SparkCommerceQuote): Promise<CommercePriceDisplay | null> {
    let params = new HttpParams().set('channel', quote.channel).set('amount', String(quote.amount));
    if (quote.currency) params = params.set('currency', quote.currency);
    if (quote.basis) params = params.set('basis', quote.basis);
    if (quote.vat != null) params = params.set('vat', String(quote.vat));
    if (quote.quantity != null) params = params.set('quantity', String(quote.quantity));
    return this.get<CommercePriceDisplay>('/commerce/price', params);
  }

  /** The public trader page of a seller (DSA Art. 30(7)). */
  traderPage(organizationId: string): Promise<CommerceTraderPage | null> {
    return this.get<CommerceTraderPage>('/commerce/traders', new HttpParams().set('organizationId', organizationId));
  }

  /** The main ranking parameters of every storefront list (GA5). */
  async rankingParameters(): Promise<CommerceRankingParameter[]> {
    return (await this.get<CommerceRankingParameter[]>('/commerce/ranking-parameters')) ?? [];
  }

  /** The legal documents in force for a seller (its own, then the platform's), or the platform's. */
  async legalDocuments(organizationId?: string | null): Promise<CommerceLegalDocumentView[]> {
    const params = organizationId ? new HttpParams().set('organizationId', organizationId) : undefined;
    return (await this.get<CommerceLegalDocumentView[]>('/commerce/legal', params)) ?? [];
  }

  /** One published legal-document version (current or past). */
  legalDocument(id: string): Promise<CommerceLegalDocumentView | null> {
    return this.get<CommerceLegalDocumentView>('/commerce/legal', new HttpParams().set('id', id));
  }

  /** The URL of a published version's PDF. */
  legalDocumentPdfUrl(id: string): string {
    return `${this.baseUrl}/commerce/legal/pdf?id=${encodeURIComponent(id)}`;
  }

  /** Files a DSA notice (Art. 16); visitors may too. */
  submitNotice(notice: SparkCommerceNotice): Promise<CommerceNoticeReceipt> {
    return this.spark.postEnvelope<CommerceNoticeReceipt>('/commerce/notices', { ...notice });
  }

  /** The seller's own profile (organization Admin/Owner). */
  sellerProfile(organizationId: string): Promise<CommerceSellerProfileView | null> {
    return this.get<CommerceSellerProfileView>('/commerce/sellers/profile', new HttpParams().set('organizationId', organizationId));
  }

  /** Saves the seller profile; parts left out stay unchanged. */
  saveSellerProfile(body: { organizationId: string; traderStatus?: string; trader?: unknown; dac7?: unknown }): Promise<CommerceSellerProfileView> {
    return this.spark.postEnvelope<CommerceSellerProfileView>('/commerce/sellers/profile', { ...body });
  }

  /** Submits the registration, accepting the current platform terms and DPA by id. */
  submitRegistration(organizationId: string, acceptedDocumentIds: string[]): Promise<CommerceSellerProfileView> {
    return this.spark.postEnvelope<CommerceSellerProfileView>('/commerce/sellers/submit', { organizationId, acceptedDocumentIds });
  }

  /** Uploads the identity document (PDF, JPEG or PNG). */
  async uploadIdentityDocument(organizationId: string, file: Blob): Promise<void> {
    const contentBase64 = await toBase64(file);
    await this.spark.postEnvelope<unknown>('/commerce/sellers/identity-document', { organizationId, contentType: file.type, contentBase64 });
  }

  /** Files a seller complaint (P2B Art. 11); answers the approval request id. */
  complain(organizationId: string, subject: string, details: string): Promise<string> {
    return this.spark.postEnvelope<string>('/commerce/sellers/complaint', { organizationId, subject, details });
  }

  /** The URL of a seller's CSV export (`buyers`, `attendees`). */
  exportUrl(organizationId: string, kind: string): string {
    return `${this.baseUrl}/commerce/sellers/export?organizationId=${encodeURIComponent(organizationId)}&kind=${encodeURIComponent(kind)}`;
  }

  /** Decides a DSA notice; an action issues a statement of reasons. */
  decideNotice(body: {
    noticeId: string; takeAction: boolean; restriction?: string; restrictionEndsAt?: string | null;
    territories?: string[]; facts?: string; ground?: 'Illegal' | 'Contractual'; groundExplanation?: string; automated?: boolean;
  }): Promise<{ id: string; status: string; statementId?: string | null }> {
    return this.spark.postEnvelope('/commerce/admin/notices/decide', { ...body });
  }

  /** The DSA transparency figures for a period (default: the last year). */
  transparencyReport(from?: string, to?: string): Promise<CommerceDsaTransparencyReport | null> {
    let params = new HttpParams();
    if (from) params = params.set('from', from);
    if (to) params = params.set('to', to);
    return this.get<CommerceDsaTransparencyReport>('/commerce/admin/notices/report', params);
  }

  private async get<T>(path: string, params?: HttpParams): Promise<T | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(`${this.baseUrl}${path}`, { params }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }
}

function toBase64(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}
