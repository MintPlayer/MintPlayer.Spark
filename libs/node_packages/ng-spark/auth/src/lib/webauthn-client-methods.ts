import { inject } from '@angular/core';
import type { SparkClientMethodMap } from '@mintplayer/ng-spark/client-operations';
import { passkeysSupported, sparkPasskeyError } from '@mintplayer/ng-spark/auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';

/** What the server's `ISparkOneShotOAuth` hands `oauth.authorize`. */
interface SparkOneShotOAuthArgs {
  url: string;
  nonce: string;
  handle: string;
}

/**
 * The browser steps the Authorization library's server actions invoke (`IRetryAccessor.Invoke`),
 * registered by {@link withAuth}.
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
 *
 * `oauth.authorize` is `ISparkOneShotOAuth` (#554): it opens the provider's authorization page in a
 * popup and waits for `/spark/auth/oauth-callback` to report. The token stays on the server; the
 * answer is only the attempt's `handle`, which the server checks against the signed-in user.
 *
 * - A blocked popup resolves as `{ error: 'popup_blocked' }`, so the server can say so.
 * - A wait that runs out (the user never finished) rejects: `Cancel`.
 * - Any other report, success or not, answers with the handle: the server knows the outcome.
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
  'oauth.authorize': {
    async invoke(args: unknown): Promise<unknown> {
      // Synchronously, at the top: the injection context ends at the first await.
      const auth = inject(SparkAuthService);
      const { url, nonce, handle } = (args ?? {}) as Partial<SparkOneShotOAuthArgs>;
      // Only a web page is opened, whatever the server sent.
      if (typeof url !== 'string' || !/^https?:\/\//i.test(url) || typeof nonce !== 'string' || typeof handle !== 'string')
        throw new Error('oauth.authorize: malformed arguments');

      const result = await auth.authorizeInPopup(url, nonce);
      if (result.error === 'popup_blocked') return { error: 'popup_blocked' };
      if (result.error === 'popup_closed') throw new Error('oauth.authorize: no answer in time');
      return { handle };
    },
  },
};
