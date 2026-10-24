import { type BranchCut, type Release } from './engine';

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
  const outcomes = new Map<string, ReleaseOutcome>();
  const spaced = spacer(options);
  for (const release of releases) {
    if (!release.release) continue;
    await spaced();
    const outcome = await post(options, `Release ${release.tag}`, '/releases', {
      tag_name: release.tag,
      name: release.release.name,
      body: release.release.body,
      prerelease: release.release.prerelease,
      // One Release per package: the newest is not the repository's latest (as GitHubForgeVersioning).
      make_latest: 'false',
    }, (status, text) => status === 422 && text.includes('already_exists'));
    outcomes.set(release.tag, outcome);
  }
  return outcomes;
}

/**
 * Local mode's release-branch cuts (PRD D12): each branch the engine asks for, created at the release commit's first
 * parent unless it exists, and the merged pull request told so. Like the Releases, a cut that fails is a warning.
 */
export async function cutReleaseBranches(options: ReleaseOptions, cuts: BranchCut[]): Promise<Map<string, ReleaseOutcome>> {
  const outcomes = new Map<string, ReleaseOutcome>();
  const spaced = spacer(options);
  for (const cut of cuts) {
    await spaced();
    // "Reference already exists" is a plain 422 without an error code (as GitHubForgeVersioning).
    const outcome = await post(options, `Release branch ${cut.branch}`, '/git/refs', { ref: `refs/heads/${cut.branch}`, sha: cut.commit },
      (status, text) => status === 422 && /reference already exists/i.test(text));
    outcomes.set(cut.branch, outcome);
    if (outcome === 'created' && cut.pullRequest) {
      await spaced();
      await post(options, `Comment on #${cut.pullRequest}`, `/issues/${cut.pullRequest}/comments`, { body: cutComment(cut) }, () => false);
    }
  }
  return outcomes;
}

/** The pull-request comment of a cut; the same text as the server's ReleaseBranchCutRecipient. */
export function cutComment(cut: BranchCut): string {
  return `${cut.packages.map((p) => `\`${p}\``).join(', ')} left major ${cut.major}, so \`${cut.branch}\` was cut at ${cut.commit.slice(0, 12)}: `
    + `patches for the ${cut.major}.x line merge there and are versioned as ${cut.major}.x.\n\n`
    + `To backport a merged pull request, label it \`backport: ${cut.major}.x\` or comment \`/backport ${cut.major}.x\`. `
    + 'The branch is protected only when a ruleset matches it.';
}

function spacer(options: ReleaseOptions): () => Promise<void> {
  const sleep = options.sleep ?? defaultSleep;
  let first = true;
  return async () => {
    if (!first) await sleep(SPACING_MS);
    first = false;
  };
}

function defaultSleep(ms: number): Promise<void> {
  return new Promise<void>((resolve) => setTimeout(resolve, ms));
}

/** One content-creating POST below the repository, retried only on a rate limit. */
async function post(options: ReleaseOptions, label: string, path: string, body: unknown, exists: (status: number, text: string) => boolean): Promise<ReleaseOutcome> {
  const doFetch = options.fetch ?? fetch;
  const sleep = options.sleep ?? defaultSleep;
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    const reply = await doFetch(`${options.apiUrl}/repos/${options.repository}${path}`, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${options.token}`,
        accept: 'application/vnd.github+json',
        'x-github-api-version': '2022-11-28',
        'content-type': 'application/json',
      },
      body: JSON.stringify(body),
    });
    if (reply.ok) { options.log(`${label}: created`); return 'created'; }
    const text = await reply.text();
    if (exists(reply.status, text)) { options.log(`${label}: exists`); return 'exists'; }
    const wait = retryAfterMs(reply, text);
    if (wait === null || attempt === ATTEMPTS) {
      options.warn(`${label}: HTTP ${reply.status} ${text.slice(0, 200)}`);
      return 'failed';
    }
    options.log(`${label}: rate limited, waiting ${Math.round(wait / 1000)} s.`);
    await sleep(wait);
  }
  return 'failed';
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
