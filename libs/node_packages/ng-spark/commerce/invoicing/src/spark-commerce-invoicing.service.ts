import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkBillingDetailsOutcome,
  SparkBillingDetailsRequest,
  SparkBillingDetailsResponse,
  SparkInvoiceSummary,
  SparkVatRateSuggestion,
} from './generated/contracts';

/**
 * Client for `MintPlayer.Spark.Commerce.Invoicing`' endpoints (`/spark/commerce/invoicing/*`, commerce #521 M10).
 *
 * POSTs go through {@link SparkService.postEnvelope}, so the app's XSRF header goes with them. Ids travel in bodies and
 * query strings, never in paths (RavenDB ids contain slashes). Exports are plain downloads: link to {@link exportUrl}.
 */
@Injectable({ providedIn: 'root' })
export class SparkCommerceInvoicingService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** `GET /spark/commerce/invoicing/documents?number=`: an order's receipts, invoices and credit notes; `[]` when the caller may not see them. */
  async documents(orderNumber: string): Promise<SparkInvoiceSummary[]> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkInvoiceSummary[] } | null>(
        `${this.baseUrl}/commerce/invoicing/documents`, { params: { number: orderNumber } }));
      return envelope?.result ?? [];
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return [];
      throw e;
    }
  }

  /** `POST /spark/commerce/invoicing/billing`: the buyer's billing details, before the order is invoiced. */
  async saveBillingDetails(request: SparkBillingDetailsRequest): Promise<SparkBillingDetailsOutcome> {
    const response = await this.spark.postEnvelope<SparkBillingDetailsResponse>('/commerce/invoicing/billing', { ...request });
    return response.outcome;
  }

  /** `GET /spark/commerce/invoicing/vat-rate-suggestions` (GC10): TEDB rates beside the configured ones. Suggestions only. */
  async vatRateSuggestions(country?: string): Promise<SparkVatRateSuggestion[]> {
    const params: Record<string, string> = {};
    if (country) params['country'] = country;
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkVatRateSuggestion[] } | null>(
      `${this.baseUrl}/commerce/invoicing/vat-rate-suggestions`, { params }));
    return envelope?.result ?? [];
  }

  /** The download URL of an export (`oss`, `dac7`, `accounting`) with its query. */
  exportUrl(kind: 'oss' | 'dac7' | 'accounting', query: Record<string, string | number | boolean>): string {
    const search = new URLSearchParams(Object.entries(query).map(([k, v]) => [k, String(v)]));
    return `${this.baseUrl}/commerce/invoicing/exports/${kind}?${search.toString()}`;
  }
}
