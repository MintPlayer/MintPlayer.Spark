import type { SparkClientMethodMap } from '@mintplayer/ng-spark/client-operations';
import { passkeysSupported, sparkPasskeyError } from '@mintplayer/ng-spark/auth/models';

/**
 * The browser steps the Authorization library's server actions invoke (`IRetryAccessor.Invoke`),
 * registered by {@link provideSparkAuth}.
 *
 * `webauthn.create` is the enrollment ceremony of the passkeys page's `AddPasskey` action: the
 * server hands it the creation options and receives the credential back in the same action, which
 * attests it against the ceremony cookie its first pass set. The server keeps the challenge; nothing
 * here chooses or carries one.
 *
 * - The user dismissing the prompt (`cancelled`, `no_credential`) rejects, which answers the server
 *   with `Cancel`: not an error, so no banner.
 * - Any other failure (an authenticator already holding a credential for this account, a security
 *   error) resolves as `{ error }`, so the server can say it did not work.
 * - An unsupported browser never runs it (`supported`), and the action shows disabled with
 *   `auth.passkeyUnsupported` as the reason.
 */
export const sparkAuthClientMethods: SparkClientMethodMap = {
  'webauthn.create': {
    supported: passkeysSupported,
    unsupportedReason: 'auth.passkeyUnsupported',
    async invoke(args: unknown): Promise<unknown> {
      try {
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(args as PublicKeyCredentialCreationOptionsJSON);
        const credential = await navigator.credentials.create({ publicKey: options });
        if (!credential) throw new DOMException('No credential was produced.', 'NotAllowedError');
        // The credential's own toJSON is WebAuthn's JSON encoding, which the server's attestation reads.
        return JSON.parse(JSON.stringify(credential));
      } catch (error) {
        const code = sparkPasskeyError(error);
        if (code === 'cancelled' || code === 'no_credential') throw error;
        return { error: code };
      }
    },
  },
};
