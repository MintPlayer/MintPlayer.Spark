/** How the user is sent to the external provider, and how the result comes back. */
export type SparkExternalLoginMode = 'popup' | 'redirect';

export interface SparkExternalLoginOptions {
  /** Where to land after a successful sign-in. Server-side sanitized to an in-app path. */
  returnUrl?: string;
  /** Defaults to `'popup'`. */
  mode?: SparkExternalLoginMode;
}

/**
 * Why an external login did not sign anyone in. Everything but the two `popup_*` codes comes
 * from the server; those two are raised here, because the browser is the only place that can
 * observe them.
 *
 * ⚠️ `link_confirmation_sent` is **not a failure**. It travels on this channel because no session
 * was created, which is what `success: false` means to the opener — but the right response is to
 * tell the user to check their mail, not to show an error.
 *
 * ⚠️ `email_already_registered` admits that an account exists, which the older codes deliberately
 * avoid doing. That trade is made on the server (the alternative was a generic failure that reads
 * as "this application is broken"); a client must not widen it by pairing the code with the address
 * it was asked about.
 */
export type SparkExternalLoginError =
  | 'no_login_info'
  | 'email_not_verified'
  | 'account_creation_failed'
  | 'email_already_registered'
  | 'sign_in_to_link'
  | 'link_confirmation_sent'
  | 'login_already_associated'
  | 'link_failed'
  | 'popup_blocked'
  | 'popup_closed';

export interface SparkExternalLoginResult {
  success: boolean;
  error?: SparkExternalLoginError;
}

/** The message the callback page posts back to the window that opened it. */
export interface SparkExternalLoginMessage {
  type: 'spark:external-login';
  success: boolean;
  error?: SparkExternalLoginError;
}

export const SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE = 'spark:external-login';

/** One external login attached to the signed-in account. */
export interface SparkLinkedExternalLogin {
  provider: string;
  providerKey: string;
  displayName: string;
  /**
   * ⚠️ `false` when removing this login would leave the account with no way to sign in.
   *
   * Served by the server so the UI can disable the control rather than offer an action that will
   * be refused. It is **not** the enforcement — the server refuses the request too — and a client
   * that treats it as advisory only is behaving correctly.
   */
  canUnlink: boolean;
}

/** A provider that is configured but not yet attached to the signed-in account. */
export interface SparkAvailableExternalProvider {
  provider: string;
  displayName: string;
}

export interface SparkExternalLogins {
  linked: SparkLinkedExternalLogin[];
  available: SparkAvailableExternalProvider[];
}

/** Why a login was not detached. */
export type SparkUnlinkError =
  /** ⚠️ It was the account's last way in, so removing it would have locked the owner out for good. */
  | 'last_credential'
  /** Not attached to this account. */
  | 'login_not_found'
  /** The store refused, for a reason that is not either of the above. */
  | 'unlink_failed';

export interface SparkUnlinkResult {
  success: boolean;
  error?: SparkUnlinkError;
}
