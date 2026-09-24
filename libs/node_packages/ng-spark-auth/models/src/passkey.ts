/** One passkey attached to the signed-in account, as the server reports it. */
export interface SparkPasskey {
  /** Base64url of the credential id. Opaque to the client — it is only ever echoed back in a URL. */
  id: string;
  /** The user's own label, or `null` if they never set one. */
  name: string | null;
  createdAt: string;
  /** Whether the credential is currently synced to the authenticator's cloud backup. */
  isBackedUp: boolean;
  isBackupEligible: boolean;
  /** `usb`, `nfc`, `ble`, `internal`, `hybrid` — a hint for the browser, never a security control. */
  transports: string[];
}

/**
 * Why a passkey ceremony did not complete.
 *
 * ⚠️ Every server-side sign-in rejection arrives as `failed`, and that is deliberate rather than
 * lossy: distinguishing "unknown credential" from "bad signature" would tell an anonymous caller
 * whether a credential exists. Do not try to recover the detail — it is not sent.
 *
 * The three codes the server never sends are raised here, because the browser is the only place
 * that can observe them.
 */
export type SparkPasskeyError =
  /** The browser cannot run the ceremony at all. Offer another sign-in method. */
  | 'unsupported'
  /** The user dismissed the authenticator prompt. Not an error worth showing. */
  | 'cancelled'
  /** The authenticator produced nothing — no matching credential, or the user gave up. */
  | 'no_credential'
  /** The account is locked out. The one server-side outcome worth distinguishing. */
  | 'locked_out'
  /** ⚠️ It was the account's last way in, so removing it would have locked the owner out for good. */
  | 'last_credential'
  /** Everything else, server or client. */
  | 'failed';

export interface SparkPasskeyResult {
  success: boolean;
  error?: SparkPasskeyError;
}

export interface SparkPasskeyRegistrationResult extends SparkPasskeyResult {
  /** The newly enrolled passkey, when `success` is true. */
  passkey?: SparkPasskey;
}

/**
 * Whether this browser can run a passkey ceremony.
 *
 * Checks the native JSON helpers as well as `navigator.credentials`, because the ceremony is built
 * on them: the server speaks WebAuthn's JSON encoding, and without
 * `parseCreationOptionsFromJSON` / `parseRequestOptionsFromJSON` the client would have to hand-roll
 * base64url conversion over every field of the options — on the security-critical path, to support
 * browsers that a user holding a passkey is unlikely to be running.
 *
 * ⚠️ `window.isSecureContext` is part of the test rather than an afterthought: WebAuthn is
 * unavailable on an insecure origin, and without this the button would render and then fail at the
 * moment the user clicks it.
 */
export function passkeysSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext === true &&
    typeof PublicKeyCredential !== 'undefined' &&
    typeof navigator?.credentials?.create === 'function' &&
    typeof navigator?.credentials?.get === 'function' &&
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function' &&
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function'
  );
}
