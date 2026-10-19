import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkConnectStartResponse,
  SparkEvidenceSubmission,
  SparkEvidenceSubmissionResponse,
  SparkPaymentMethod,
  SparkSellerPaymentStatus,
} from './generated/contracts';

/**
 * Client for `MintPlayer.Spark.Commerce.Payments`' endpoints (`/spark/commerce/payments/*`, commerce #521 M6).
 *
 * POSTs go through {@link SparkService.postEnvelope}, so the app's XSRF header goes with them (the endpoints require
 * the antiforgery token). Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes).
 */
@Injectable({ providedIn: 'root' })
export class SparkCommercePaymentsService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /**
   * `GET /spark/commerce/payments/methods`: the methods a basket of `channel` may offer (the server applies D3's cap
   * for held channels). Never filtered by the buyer's country (GA11).
   */
  async methods(channel?: string, provider?: string): Promise<SparkPaymentMethod[]> {
    const params: Record<string, string> = {};
    if (channel) params['channel'] = channel;
    if (provider) params['provider'] = provider;
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkPaymentMethod[] } | null>(
      `${this.baseUrl}/commerce/payments/methods`, { params }));
    return envelope?.result ?? [];
  }

  /** `GET /spark/commerce/payments/connect/status`: a seller's linked account; `null` when the caller may not see it. */
  async status(organizationId: string): Promise<SparkSellerPaymentStatus | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkSellerPaymentStatus } | null>(
        `${this.baseUrl}/commerce/payments/connect/status`, { params: { organizationId } }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }

  /** `POST /spark/commerce/payments/connect/start`: where to send the seller's Owner or Admin to link the account. */
  async startOnboarding(organizationId: string, provider?: string): Promise<string> {
    const response = await this.spark.postEnvelope<SparkConnectStartResponse>('/commerce/payments/connect/start',
      { organizationId, ...(provider ? { provider } : {}) });
    return response.redirectUrl;
  }

  /** `POST /spark/commerce/payments/disputes/submit-evidence` (GB1). `manualOnly`: download the pack instead. */
  async submitEvidence(disputeId: string): Promise<SparkEvidenceSubmission> {
    const response = await this.spark.postEnvelope<SparkEvidenceSubmissionResponse>('/commerce/payments/disputes/submit-evidence', { id: disputeId });
    return response.result;
  }

  /** The evidence pack's download URL (`GET /spark/commerce/payments/disputes/evidence?id=…`). */
  evidenceUrl(disputeId: string): string {
    return `${this.baseUrl}/commerce/payments/disputes/evidence?id=${encodeURIComponent(disputeId)}`;
  }
}
