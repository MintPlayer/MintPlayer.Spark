import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkApprovalDecisionResult, SparkApprovalProposal } from './spark-approvals.models';

/**
 * Client for `MintPlayer.Spark.Approvals`' endpoints (`/spark/approvals/*`, commerce #521 M3).
 *
 * The decisions are POSTs with the id in the body (RavenDB ids contain slashes), through
 * {@link SparkService.postEnvelope} like every add-on's: the app's XSRF header goes with them (the
 * endpoints require the antiforgery token) and envelope operations are dispatched.
 *
 * A request the caller may not see or decide is the standard refusal (401 / 404) and rejects. A refusal
 * by rule — a missing reason (400), not pending or stale (409), a proposal the caller may not apply in
 * full (422) — resolves to its {@link SparkApprovalDecisionResult}, whose `outcome` says which.
 */
@Injectable({ providedIn: 'root' })
export class SparkApprovalsService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** `POST /spark/approvals/decide`: approve, or reject with a reason (required). */
  decide(id: string, decision: 'approve' | 'reject', reason?: string): Promise<SparkApprovalDecisionResult> {
    return this.decision('/approvals/decide', { id, decision, ...(reason ? { reason } : {}) });
  }

  /** `POST /spark/approvals/withdraw`: the requester takes a pending request back. */
  withdraw(id: string): Promise<SparkApprovalDecisionResult> {
    return this.decision('/approvals/withdraw', { id });
  }

  /** `POST /spark/approvals/revoke`: a decider takes back an approval, with a reason. */
  revoke(id: string, reason: string): Promise<SparkApprovalDecisionResult> {
    return this.decision('/approvals/revoke', { id, reason });
  }

  /**
   * `GET /spark/approvals/proposal?id=…`: the document proposal behind a request and its target, for the
   * diff. `null` when the request is not a document proposal, or not one the caller may see (the refusal).
   */
  async proposal(id: string): Promise<SparkApprovalProposal | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkApprovalProposal } | null>(
        `${this.baseUrl}/approvals/proposal`, { params: { id } }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }

  private async decision(path: string, body: Record<string, unknown>): Promise<SparkApprovalDecisionResult> {
    try {
      return await this.spark.postEnvelope<SparkApprovalDecisionResult>(path, body);
    } catch (e) {
      const error = e as HttpErrorResponse;
      const result = error?.error?.result as SparkApprovalDecisionResult | undefined;
      if ((error?.status === 400 || error?.status === 409 || error?.status === 422) && result?.outcome) return result;
      throw e;
    }
  }
}
