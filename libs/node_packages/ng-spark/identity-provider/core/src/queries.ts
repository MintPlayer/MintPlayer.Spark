/**
 * The identity-provider library's queries, by the aliases in its `App_Data/Model/*.json`
 * (`queries[].alias`). They open in the generic query page of `sparkRoutes()`, `/query/:queryId`,
 * which resolves an alias as well as an id. Each lists only what the caller may see.
 */
export const SPARK_IDENTITY_PROVIDER_QUERIES = {
  /** `OidcApplication`: the caller's applications (members-only filter), or all of them for an administrator. */
  applications: 'oidc-applications',
  resources: 'oidc-resources',
  /**
   * The Approvals library's inbox (`ApprovalRequest`, MintPlayer.Spark.Approvals): the pending requests the caller may
   * decide. Developer requests, cross-owner scope requests and go-live reviews are approval requests (commerce D26).
   */
  approvals: 'pending-approvals',
  /** The Approvals library's list of the caller's own requests, which the requester may withdraw. */
  myApprovalRequests: 'my-approval-requests',
  grants: 'oidc-grants',
  auditEvents: 'oidc-audit-events',
} as const;

/** The router commands that open one of {@link SPARK_IDENTITY_PROVIDER_QUERIES}. */
export function identityProviderQueryLink(alias: string): string[] {
  return ['/query', alias];
}
