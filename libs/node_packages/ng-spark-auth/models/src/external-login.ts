/** How the user is sent to the external provider, and how the result comes back. */
export type SparkExternalLoginMode = 'popup' | 'redirect';

export interface SparkExternalLoginOptions {
  /** Where to land after a successful sign-in. Server-side sanitized to an in-app path. */
  returnUrl?: string;
  /** Defaults to `'popup'`. */
  mode?: SparkExternalLoginMode;
}

/**
 * Why an external login did not sign anyone in. The three `no_login_info` /
 * `email_not_verified` / `account_creation_failed` codes come from the server; the two
 * `popup_*` codes are raised here, because the browser is the only place that can observe
 * them. Deliberately coarse — never enough to tell "no such account" from anything else.
 */
export type SparkExternalLoginError =
  | 'no_login_info'
  | 'email_not_verified'
  | 'account_creation_failed'
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
