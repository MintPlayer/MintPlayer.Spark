/** An external provider a user can actually click, as reported by the server. */
export interface SparkExternalProvider {
  scheme: string;
  displayName: string;
}

/** An identifier kind password sign-in can accept. */
export type SparkSignInIdentifier = 'email' | 'userName';

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
   * What password sign-in accepts as the identifier — the server's `SignInIdentifiers`. Empty when
   * `localCredentials` is `Disabled`. Optional: a server older than this client omits it, and absent
   * reads as both, which is what such a server accepts.
   */
  signInIdentifiers?: SparkSignInIdentifier[];

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

  /**
   * Whether the two-factor setup page is served — the server mapped `manage/2fa` and the
   * authenticator-URI endpoint. Optional: absent reads as "no".
   */
  twoFactor?: boolean;

  /**
   * Whether a signed-in user may change their email address — the server's opt-in
   * `SparkEmailChange.Enabled`, and a local-credential mode that maps `POST manage/info`. Optional for
   * the same reason as `passkeys`: absent reads as "no".
   */
  emailChange?: boolean;

  /**
   * Whether the connected-logins page is served — the server's `ExternalLoginLinking` is not
   * `Disabled`. External SIGN-IN can be on while this is off. Optional: absent reads as "no".
   */
  externalLogins?: boolean;
}
