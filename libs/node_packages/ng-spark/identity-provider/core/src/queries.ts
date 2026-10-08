/**
 * The identity-provider library's queries, by the aliases in its `App_Data/Model/*.json`
 * (`queries[].alias`). They open in the generic query page of `sparkRoutes()`, `/query/:queryId`,
 * which resolves an alias as well as an id. Each lists only what the caller may see.
 */
export const SPARK_IDENTITY_PROVIDER_QUERIES = {
  /** `OidcApplication`: the caller's applications (members-only filter), or all of them for an administrator. */
  applications: 'oidc-applications',
  resources: 'oidc-resources',
  developerRequests: 'oidc-developer-requests',
  scopeApprovals: 'oidc-scope-approvals',
  goLiveReviews: 'oidc-golive-reviews',
  grants: 'oidc-grants',
  auditEvents: 'oidc-audit-events',
} as const;

/** The router commands that open one of {@link SPARK_IDENTITY_PROVIDER_QUERIES}. */
export function identityProviderQueryLink(alias: string): string[] {
  return ['/query', alias];
}
