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

  /**
   * Whether an anonymous visitor can sign in with a passkey — i.e. whether the server mounted the
   * passkey sign-in endpoint.
   *
   * Necessary but not sufficient for showing the button: the browser must also support the
   * ceremony. Check `passkeysSupported()` too, or the page offers a flow that cannot start.
   *
   * Optional because a server older than this client omits the field entirely, and an absent
   * capability must read as "no" rather than as `undefined` leaking into a template.
   */
  passkeys?: boolean;
}
