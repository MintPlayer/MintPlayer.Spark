import * as core from '@actions/core';

/**
 * A bearer credential that stays valid for as long as the step needs it.
 *
 * Uploading takes seconds, so the original code minted once and was right to.
 * Waiting for a build to finalize can take half an hour, and a GitHub Actions
 * OIDC token lives for **five minutes** (ten is GitHub's maximum, and it is not
 * configurable) — so a wait carrying the token it uploaded with would 401
 * partway through, on slow builds only, which is the worst shape of bug. This
 * hands out a token that is still valid instead.
 */
export interface Credential {
  get(): Promise<string>;
  /** Forces the next `get()` to re-mint. No-op for credentials that don't expire. */
  invalidate(): void;
  /**
   * True when there is no credential at all and requests must carry NO
   * `Authorization` header.
   *
   * WARNING this exists so that "no credential" is a state callers must handle
   * rather than an empty string they can interpolate. A fork run genuinely has
   * nothing to send, and `Authorization: Bearer undefined` is not an absent
   * header — it is a malformed one, which the server rejects differently and
   * which would make the fork path look broken rather than anonymous.
   */
  readonly anonymous?: boolean;
}

/**
 * No credential at all, for coverage contributed from a fork's pull request.
 *
 * GitHub withholds both halves of the usual answer from a fork run — secrets
 * are not exposed and `id-token: write` is downgraded to read — so there is
 * nothing to authenticate with and nothing to wait for. The server accepts
 * these against the pull request itself: it reads the PR back from GitHub and
 * requires the uploaded sha to equal its current head, which only the person
 * who opened it can arrange.
 */
export function anonymousCredential(): Credential {
  return {
    get: async () => {
      throw new Error('An anonymous credential has no token; check `credential.anonymous` before reading one.');
    },
    invalidate: () => {},
    anonymous: true,
  };
}

/** An upload token (`covt_…`). Never expires, so there is nothing to refresh. */
export function staticCredential(token: string): Credential {
  return { get: async () => token, invalidate: () => {} };
}

/**
 * A GitHub Actions OIDC id-token, re-minted as it approaches expiry. The
 * runtime request token behind `getIDToken` is valid for the whole job, so
 * re-minting mid-job is free; refreshing on expiry rather than per request
 * keeps it to a handful of calls across a full wait.
 */
export function oidcCredential(audience: string): Credential {
  const REFRESH_MARGIN_SECONDS = 60;
  let token: string | undefined;
  let expiresAt = 0;

  return {
    async get() {
      const now = Date.now() / 1000;
      if (token && now < expiresAt - REFRESH_MARGIN_SECONDS) return token;

      token = await core.getIDToken(audience);
      expiresAt = expiryOf(token) ?? now + 300;
      return token;
    },
    invalidate() {
      token = undefined;
    },
  };
}

/**
 * The `exp` claim, or undefined if the token isn't a readable JWT. Only the
 * expiry is read — the token is never validated here; the server does that.
 */
function expiryOf(jwt: string): number | undefined {
  try {
    const payload = jwt.split('.')[1];
    if (!payload) return undefined;
    const claims = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as { exp?: number };
    return typeof claims.exp === 'number' ? claims.exp : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The `Authorization` header for a credential, or no header at all.
 *
 * WARNING an anonymous credential must produce an ABSENT header, never an empty
 * or malformed one. All three request sites go through this so the distinction
 * cannot be re-lost in one of them.
 */
export async function authHeaders(credential: Credential): Promise<Record<string, string>> {
  if (credential.anonymous) return {};
  return { Authorization: `Bearer ${await credential.get()}` };
}
