// Wire types of the identity provider's portal API, `/spark/identity-provider/*`
// (MintPlayer.Spark.IdentityProvider, Endpoints/Portal). camelCase JSON; dates are ISO strings.

/** One granted scope, with its text in the request's language (`spark-lang` cookie). */
export interface SparkConnectedScope {
  name: string;
  displayName: string;
  description?: string | null;
  /** `openid`, or a scope the resource or the application marks required: only withdrawable with the whole grant. */
  required: boolean;
}

/** One application the signed-in user granted access to (`GET /applications`, PRD D6). */
export interface SparkConnectedApplication {
  applicationId: string;
  displayName: string;
  logoUrl?: string | null;
  publisher?: string | null;
  homepageUrl?: string | null;
  scopes: SparkConnectedScope[];
  firstGrantedAt: string;
  lastUsedAt?: string | null;
  remembered: boolean;
  rememberedUntil?: string | null;
}

/** The body of `POST /applications/withdraw`. No `scopes` withdraws the whole grant. */
export interface SparkWithdrawAccessRequest {
  applicationId: string;
  scopes?: string[];
}

/** The developer statuses the server stores (`OidcDeveloperStatuses`). */
export type SparkDeveloperStatusName = 'Requested' | 'Approved' | 'Rejected' | 'Revoked';

/** `GET /developer` and `POST /developer` (`OidcDeveloperStatusResponse`). */
export interface SparkDeveloperStatus {
  /** Null when the user never asked. */
  status: SparkDeveloperStatusName | string | null;
  acceptedTermsVersion: number | null;
  currentTermsVersion: number;
  termsUrl: string | null;
  requireApproval: boolean;
  isActive: boolean;
  requestedAt: string | null;
  decidedAt: string | null;
  note: string | null;
}

/** `POST /developer/registration-token`: an RFC 7591 initial access token, shown once. */
export interface SparkRegistrationToken {
  initialAccessToken: string;
  /** Seconds. */
  expiresIn: number;
}

/** `POST /invitations/accept` (`OidcAcceptInvitationResponse`). */
export interface SparkInvitationOutcome {
  accepted: boolean;
  application: string | null;
  problem: string | null;
}

/** One signing key on the management page (`OidcKeySummary`); never its private material. */
export interface SparkSigningKey {
  kid: string;
  algorithm: string;
  state: string;
  createdAt: string;
  activatedAt: string | null;
  retiredAt: string | null;
}

/** `POST /admin/keys/rotate`. */
export interface SparkKeyRotation {
  changes: string[];
}

/** The outcome of a portal call: the value, or the status and the server's problem text. */
export type SparkPortalResult<T> =
  | { ok: true; value: T }
  | { ok: false; status: number; problem: string | null };
