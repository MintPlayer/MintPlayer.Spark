import { type Release } from './engine';

export interface ReleaseOptions {
  apiUrl: string;
  repository: string;
  token: string;
  fetch?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
  log: (message: string) => void;
  warn: (message: string) => void;
}

export type ReleaseOutcome = 'created' | 'exists' | 'failed';

/** GitHub asks for at least a second between content-creating requests (secondary rate limits). */
export const SPACING_MS = 1000;
/** The longest one rate-limit wait; past it the Release is reported as failed rather than holding the publish job. */
export const MAX_WAIT_MS = 60_000;
export const ATTEMPTS = 3;

/**
 * Local mode's Releases (PRD D15): one per tag the run put in place, created one at a time with backoff, the same
 * notes the server's recipient uses. A tag that already has a Release counts as done, so a rerun creates only what is
 * missing. A Release that cannot be made is a warning, not a failure: the tags, which the packages are built from, stand.
 */
export async function createReleases(options: ReleaseOptions, releases: Release[]): Promise<Map<string, ReleaseOutcome>> {
  const doFetch = options.fetch ?? fetch;
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const outcomes = new Map<string, ReleaseOutcome>();
  let first = true;
  for (const release of releases) {
    if (!release.release) continue;
    if (!first) await sleep(SPACING_MS);
    first = false;
    let outcome: ReleaseOutcome = 'failed';
    for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
      const reply = await doFetch(`${options.apiUrl}/repos/${options.repository}/releases`, {
        method: 'POST',
        headers: {
          authorization: `Bearer ${options.token}`,
          accept: 'application/vnd.github+json',
          'x-github-api-version': '2022-11-28',
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          tag_name: release.tag,
          name: release.release.name,
          body: release.release.body,
          prerelease: release.release.prerelease,
          // One Release per package: the newest is not the repository's latest (as GitHubForgeVersioning).
          make_latest: 'false',
        }),
      });
      if (reply.ok) { outcome = 'created'; break; }
      const text = await reply.text();
      if (reply.status === 422 && text.includes('already_exists')) { outcome = 'exists'; break; }
      const wait = retryAfterMs(reply, text);
      if (wait === null || attempt === ATTEMPTS) {
        options.warn(`Release ${release.tag}: HTTP ${reply.status} ${text.slice(0, 200)}`);
        break;
      }
      options.log(`Release ${release.tag}: rate limited, waiting ${Math.round(wait / 1000)} s.`);
      await sleep(wait);
    }
    outcomes.set(release.tag, outcome);
    if (outcome !== 'failed') options.log(`Release ${release.tag}: ${outcome}`);
  }
  return outcomes;
}

/** The wait a rate-limited answer asks for, capped; null when the answer is not a rate limit. */
export function retryAfterMs(reply: Response, text: string, now: number = Date.now()): number | null {
  const exhausted = reply.headers.get('x-ratelimit-remaining') === '0';
  // A 403 is a rate limit only when it says so; otherwise it is a missing permission, and waiting will not help.
  if (reply.status !== 429 && !(reply.status === 403 && (exhausted || /rate limit/i.test(text)))) return null;
  const after = Number(reply.headers.get('retry-after'));
  if (Number.isFinite(after) && after > 0) return Math.min(after * 1000, MAX_WAIT_MS);
  if (exhausted) {
    const reset = Number(reply.headers.get('x-ratelimit-reset')) * 1000;
    if (Number.isFinite(reset) && reset > now) return Math.min(reset - now, MAX_WAIT_MS);
  }
  // A secondary limit without a hint: GitHub's guidance is to wait at least a minute.
  return MAX_WAIT_MS;
}
