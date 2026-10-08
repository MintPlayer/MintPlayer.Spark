import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, type Observable } from 'rxjs';
import type {
  SparkConnectedApplication,
  SparkDeveloperStatus,
  SparkInvitationOutcome,
  SparkKeyRotation,
  SparkPortalResult,
  SparkRegistrationToken,
  SparkSigningKey,
  SparkWithdrawAccessRequest,
} from './models';

/** The portal API's prefix (`IdentityProviderPortalGroup.Prefix` on the server). */
export const SPARK_IDENTITY_PROVIDER_API = '/spark/identity-provider';

/**
 * The identity provider's portal API for the SPA pages (PRD D7). Every call is for a signed-in user;
 * the POSTs require the antiforgery token, which Angular's XSRF support adds from the `XSRF-TOKEN`
 * cookie (`withSparkAuth()` configures it), so the calls are plain `HttpClient` requests.
 *
 * Calls never throw: each resolves to a {@link SparkPortalResult}, so a page branches on `ok` and on
 * the status (403 for a page the user may not see) instead of catching.
 */
@Injectable({ providedIn: 'root' })
export class SparkIdentityProviderPortalService {
  private readonly http = inject(HttpClient);

  connectedApplications(): Promise<SparkPortalResult<SparkConnectedApplication[]>> {
    return this.call(this.http.get<SparkConnectedApplication[]>(`${SPARK_IDENTITY_PROVIDER_API}/applications`));
  }

  /** Withdraws the whole grant, or only `scopes` when given. Answers 204, or 409 when it could not. */
  withdraw(request: SparkWithdrawAccessRequest): Promise<SparkPortalResult<null>> {
    const body: SparkWithdrawAccessRequest = request.scopes?.length
      ? { applicationId: request.applicationId, scopes: request.scopes }
      : { applicationId: request.applicationId };
    return this.call(this.http.post<null>(`${SPARK_IDENTITY_PROVIDER_API}/applications/withdraw`, body));
  }

  developerStatus(): Promise<SparkPortalResult<SparkDeveloperStatus>> {
    return this.call(this.http.get<SparkDeveloperStatus>(`${SPARK_IDENTITY_PROVIDER_API}/developer`));
  }

  /** Accepts the terms version the page showed; the server refuses (409) a version that is no longer current. */
  requestDeveloperStatus(termsVersion: number): Promise<SparkPortalResult<SparkDeveloperStatus>> {
    return this.call(this.http.post<SparkDeveloperStatus>(`${SPARK_IDENTITY_PROVIDER_API}/developer`, { termsVersion }));
  }

  issueRegistrationToken(): Promise<SparkPortalResult<SparkRegistrationToken>> {
    return this.call(this.http.post<SparkRegistrationToken>(`${SPARK_IDENTITY_PROVIDER_API}/developer/registration-token`, {}));
  }

  acceptInvitation(applicationId: string, token: string): Promise<SparkPortalResult<SparkInvitationOutcome>> {
    return this.call(this.http.post<SparkInvitationOutcome>(`${SPARK_IDENTITY_PROVIDER_API}/invitations/accept`, { applicationId, token }));
  }

  signingKeys(): Promise<SparkPortalResult<SparkSigningKey[]>> {
    return this.call(this.http.get<SparkSigningKey[]>(`${SPARK_IDENTITY_PROVIDER_API}/admin/keys`));
  }

  rotateSigningKeys(): Promise<SparkPortalResult<SparkKeyRotation>> {
    return this.call(this.http.post<SparkKeyRotation>(`${SPARK_IDENTITY_PROVIDER_API}/admin/keys/rotate`, {}));
  }

  private async call<T>(request: Observable<T>): Promise<SparkPortalResult<T>> {
    try {
      return { ok: true, value: await firstValueFrom(request) };
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        return { ok: false, status: error.status, problem: problemText(error.error) };
      }
      return { ok: false, status: 0, problem: null };
    }
  }
}

/** The text of an RFC 7807 problem (`TypedResults.Problem(detail)`), when the body is one. */
function problemText(body: unknown): string | null {
  if (body && typeof body === 'object') {
    const problem = body as { detail?: unknown; title?: unknown };
    if (typeof problem.detail === 'string' && problem.detail) return problem.detail;
  }
  return null;
}
