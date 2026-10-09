/** How the user is sent to the external provider, and how the result comes back. */
export type SparkExternalLoginMode = 'popup' | 'redirect';

/**
 * The app-wide default for {@link SparkExternalLoginMode}, set through
 * `provideSparkAuth({ externalLoginMode })`.
 *
 * `'auto'` (the default) uses `'redirect'` inside an installed web app (`display-mode: standalone`,
 * or iOS's `navigator.standalone`) and `'popup'` in a browser tab. Inside an installed app a popup is
 * captured into the app, or lands in a custom tab with no usable opener, so only a full-page
 * redirect is reliable there.
 */
export type SparkExternalLoginModeSetting = 'auto' | SparkExternalLoginMode;

export interface SparkExternalLoginOptions {
  /**
   * Where to land after a successful provider round trip. Server-side sanitized to an in-app path.
   */
  returnUrl?: string;
  /**
   * `'redirect'` mode only: where a failure lands, with `?sparkExternalLogin=<code>` added.
   * Server-side sanitized like `returnUrl`. Defaults to the page the attempt started from (the
   * router URL with any earlier `sparkExternalLogin` removed), which is the page that reads and
   * shows the code. Ignored in popup mode, where the outcome comes back to the opener instead.
   */
  errorUrl?: string;
  /** Overrides the configured `externalLoginMode` (default `'auto'`) for this one call. */
  mode?: SparkExternalLoginMode;
}

/** Whether this document runs as an installed web app rather than in a browser tab. */
export function sparkIsStandaloneDisplay(): boolean {
  if (typeof window === 'undefined') return false;
  const standaloneQuery = typeof window.matchMedia === 'function'
    && window.matchMedia('(display-mode: standalone)')?.matches === true;
  return standaloneQuery || (window.navigator as { standalone?: boolean } | undefined)?.standalone === true;
}

/**
 * The value of `externalLoginMode` resolved against this document: `'auto'` becomes `'redirect'` in
 * an installed web app and `'popup'` everywhere else.
 */
export function resolveSparkExternalLoginMode(setting: SparkExternalLoginModeSetting | undefined): SparkExternalLoginMode {
  if (setting === 'popup' || setting === 'redirect') return setting;
  return sparkIsStandaloneDisplay() ? 'redirect' : 'popup';
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
  /** The provider reported an error, or the correlation of its answer failed. */
  | 'remote_failure'
  /** The challenge refused the nonce (HTTP 400). It never travels in a popup payload. */
  | 'invalid_nonce'
  | 'popup_blocked'
  | 'popup_closed';

/** Every {@link SparkExternalLoginError}; a code read from a URL is checked against it. */
export const SPARK_EXTERNAL_LOGIN_ERRORS: readonly SparkExternalLoginError[] = [
  'no_login_info', 'email_not_verified', 'account_creation_failed', 'email_already_registered',
  'sign_in_to_link', 'link_confirmation_sent', 'login_already_associated', 'link_failed',
  'remote_failure', 'invalid_nonce', 'popup_blocked', 'popup_closed',
];

export interface SparkExternalLoginResult {
  success: boolean;
  error?: SparkExternalLoginError;
}

/**
 * The payload the callback page hands back: posted to the opener, posted on the
 * {@link SPARK_EXTERNAL_LOGIN_CHANNEL} BroadcastChannel, and written to `localStorage` under
 * {@link sparkExternalLoginStorageKey} (with an `at` timestamp). Any of the three may be the one
 * that arrives: COOP on the provider's pages cuts the opener, and a frozen background tab may only
 * see the storage entry once it is shown again.
 */
export interface SparkExternalLoginMessage {
  type: typeof SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE;
  success: boolean;
  error?: SparkExternalLoginError | null;
  /** The nonce the opener sent on the challenge; `null` from a challenge that carried none. */
  nonce?: string | null;
}

/** The opener's answer on the BroadcastChannel: the callback page sets its done-marker and closes. */
export interface SparkExternalLoginAck {
  type: typeof SPARK_EXTERNAL_LOGIN_ACK_TYPE;
  nonce: string;
}

export const SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE = 'spark:external-login';
export const SPARK_EXTERNAL_LOGIN_ACK_TYPE = 'spark:external-login-ack';

/** The BroadcastChannel the callback page and the opener share. */
export const SPARK_EXTERNAL_LOGIN_CHANNEL = 'spark:external-login';

/** The query parameter a redirect-mode failure comes back with. */
export const SPARK_EXTERNAL_LOGIN_QUERY_PARAM = 'sparkExternalLogin';

/** How long a popup attempt waits for its result before it settles `popup_closed`. */
export const SPARK_EXTERNAL_LOGIN_TIMEOUT_MS = 10 * 60 * 1000;

/** The `localStorage` key the callback page writes its payload to. */
export function sparkExternalLoginStorageKey(nonce: string): string {
  return `spark:external-login:${nonce}`;
}

/**
 * The `localStorage` done-marker: set once the opener has the result. The callback page reads it to
 * restore itself to the app when an installed web app shows it again.
 */
export function sparkExternalLoginDoneKey(nonce: string): string {
  return `spark:external-login-done:${nonce}`;
}

/**
 * A fresh nonce for one popup attempt: 32 base64url characters from `crypto.getRandomValues`
 * (24 bytes, so no padding). The server accepts `^[A-Za-z0-9_-]{16,64}$`.
 */
export function sparkExternalLoginNonce(): string {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_');
}

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
