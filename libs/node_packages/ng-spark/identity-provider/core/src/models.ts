// Wire types of the identity provider's portal API, `/spark/identity-provider/*`
// (MintPlayer.Spark.IdentityProvider, Endpoints/Portal). The response and request records are generated from the C#
// (./generated/contracts, docs/guide-typescript-contracts.md): SparkConnectedApplication, SparkConnectedScope,
// SparkWithdrawAccessRequest, SparkDeveloperStatus, SparkRegistrationToken, SparkInvitationOutcome, SparkSigningKey,
// SparkKeyRotation. What stays here is client-side only.

/** The developer statuses the server stores (`OidcDeveloperStatuses`); `SparkDeveloperStatus.status` holds one, or null. */
export type SparkDeveloperStatusName = 'Requested' | 'Approved' | 'Rejected' | 'Revoked';

/** The outcome of a portal call: the value, or the status and the server's problem text. */
export type SparkPortalResult<T> =
  | { ok: true; value: T }
  | { ok: false; status: number; problem: string | null };
