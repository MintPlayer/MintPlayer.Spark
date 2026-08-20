/** An external provider a user can actually click, as reported by the server. */
export interface SparkExternalProvider {
  scheme: string;
  displayName: string;
}

/**
 * What `GET /spark/auth/capabilities` reports: how much of the local-credential surface this
 * application mounts, and which external providers are registered.
 *
 * This exists so the two tiers cannot silently disagree. The route config is a build-time choice
 * and the server's mode is a deployment-time one; without a channel between them, a mismatch shows
 * up as a form that posts into a 404, or a sign-in page with no way to sign in.
 */
export interface SparkAuthCapabilities {
  localCredentials: 'Full' | 'SignInOnly' | 'Disabled';
  externalProviders: SparkExternalProvider[];
}
