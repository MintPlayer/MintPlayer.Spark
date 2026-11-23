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
    // #693 D7: a package that moved only because a dependency did gets no Release of its own; the run's Release lists it.
    if (!release.release || release.release.dependencyOnly) continue;
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

export interface RunReleaseRequest {
  /** The commit the run tagged. */
  commit: string;
  /** The branch the run released from; a release line names the tag (`release/<line>/<date>`). */
  branch: string;
  defaultBranch: string;
  /** Today, as `yyyy-MM-dd` (UTC); a parameter so the tests are fixed. */
  date: string;
  /** The server's URL, for the links to the package Releases (`https://github.com`). */
  serverUrl: string;
  /** Changelog × release lines R2: false on an older release line, whose run Release must not take "Latest" ({@link isCurrentLine}). */
  latest: boolean;
}

/** `versioning.lines.pattern`'s default, as `ReleaseLinePattern.DefaultText`. */
const DEFAULT_LINE_PATTERN = 'release/{ecosystem}-{major}.x';

/** The pattern's regex, with the server's placeholders (`ReleaseLinePattern.TryParse`); null when it is not one. */
function lineRegex(pattern: string): RegExp | null {
  if (pattern.split('{ecosystem}').length !== 2 || pattern.split('{major}').length !== 2) return null;
  const escaped = pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    .replace('\\{ecosystem\\}', '(?<ecosystem>[a-z][a-z0-9]{0,31})')
    .replace('\\{major\\}', '(?<major>[1-9][0-9]{0,8})');
  return new RegExp(`^${escaped}$`);
}

/**
 * Changelog × release lines R2: whether a run on `branch` may mark its run Release latest. A release line is current when it
 * is the highest major of its ecosystem among the line branches, the rule `/release` uses (`ReleaseLinePattern.CurrentLines`);
 * the default branch, lines off and a branch that is no line stay latest. When the refs cannot be read the answer is false:
 * GitHub then keeps the current Latest, which is never wrong.
 */
export async function isCurrentLine(options: ReleaseOptions, lines: unknown, branch: string, defaultBranch: string): Promise<boolean> {
  if (branch === defaultBranch || !lines || typeof lines !== 'object') return true;
  const configured = (lines as Record<string, unknown>).pattern;
  const pattern = typeof configured === 'string' ? configured : DEFAULT_LINE_PATTERN;
  const regex = lineRegex(pattern);
  const mine = regex?.exec(branch)?.groups;
  if (!regex || !mine) return true;
  const prefix = pattern.slice(0, pattern.indexOf('{'));
  const doFetch = options.fetch ?? fetch;
  const reply = await doFetch(`${options.apiUrl}/repos/${options.repository}/git/matching-refs/heads/${prefix}`, {
    headers: { authorization: `Bearer ${options.token}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28' },
  });
  if (!reply.ok) {
    options.warn(`Run Release: listing the release lines answered HTTP ${reply.status}; the run Release is not marked latest.`);
    return false;
  }
  const refs = (await reply.json()) as { ref?: unknown }[];
  return !refs.some((r) => {
    const other = typeof r.ref === 'string' ? regex.exec(r.ref.replace(/^refs\/heads\//, ''))?.groups : undefined;
    return other?.ecosystem === mine.ecosystem && Number(other.major) > Number(mine.major);
  });
}

/** The most `.n` suffixes tried for a run's tag on one day. */
export const MAX_RUN_TAGS_PER_DAY = 20;

/**
 * #693 D7: one Release per run, created last and marked latest (unless its line is an older one, R2), so the repository's "Latest release" is the overview. Its
 * tag is `release/<line>/<yyyy-MM-dd>[.<n>]` on a release line (the branch without its `release/`), else
 * `release/<yyyy-MM-dd>[.<n>]`; it holds no `/v`, so the engine never reads it as a version tag (S2). It lists every
 * package the run released, linking each one's own Release, and the dependency-only ones by name.
 */
export async function createRunRelease(options: ReleaseOptions, releases: Release[], request: RunReleaseRequest): Promise<ReleaseOutcome | null> {
  const listed = releases.filter((r) => r.release);
  if (listed.length === 0) return null;
  await (options.sleep ?? defaultSleep)(SPACING_MS); // after the package Releases, as GitHub's secondary limits ask
  const line = request.branch !== request.defaultBranch ? request.branch.replace(/^release\//, '') : null;
  const base = line ? `release/${line}/${request.date}` : `release/${request.date}`;
  // R5: the lookup skips taken tags; a 422 already_exists from the create (another run took the tag in between) is
  // "taken" too, so the loop moves to the next suffix rather than failing.
  for (let n = 1; n <= MAX_RUN_TAGS_PER_DAY; n++) {
    const tag = n === 1 ? base : `${base}.${n}`;
    const free = await isFreeTag(options, tag);
    if (free === null) return 'failed';
    if (!free) continue;
    const outcome = await post(options, `Release ${tag}`, '/releases', {
      tag_name: tag,
      target_commitish: request.commit,
      name: line ? `${line} ${request.date}` : request.date,
      body: runBody(listed, options.repository, request.serverUrl),
      prerelease: false,
      make_latest: request.latest ? 'true' : 'false',
    }, (status, text) => status === 422 && text.includes('already_exists'));
    if (outcome !== 'exists') return outcome;
  }
  options.warn(`Run Release: every tag from ${base} to ${base}.${MAX_RUN_TAGS_PER_DAY} exists; none was created.`);
  return 'failed';
}

/** The run Release's body: a line per package, linked to its own Release unless it moved only with a dependency. */
export function runBody(releases: Release[], repository: string, serverUrl: string): string {
  const own = releases.filter((r) => !r.release!.dependencyOnly);
  const dependents = releases.filter((r) => r.release!.dependencyOnly);
  const lines: string[] = [];
  if (own.length > 0) {
    lines.push('### Packages');
    for (const r of own) lines.push(`- [${r.id} ${r.version}](${serverUrl.replace(/\/$/, '')}/${repository}/releases/tag/${encodeURIComponent(r.tag).replace(/%2F/g, '/')})`);
  }
  if (dependents.length > 0) {
    if (lines.length > 0) lines.push('');
    lines.push('### Dependency updates only');
    for (const r of dependents) lines.push(`- ${r.id} ${r.version}`);
  }
  return lines.join('\n');
}

/** Whether `name` is not a tag yet; null (with a warning) when the lookup fails. */
async function isFreeTag(options: ReleaseOptions, name: string): Promise<boolean | null> {
  const doFetch = options.fetch ?? fetch;
  const reply = await doFetch(`${options.apiUrl}/repos/${options.repository}/git/ref/tags/${name}`, {
    headers: { authorization: `Bearer ${options.token}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28' },
  });
  if (reply.status === 404) return true;
  if (!reply.ok) {
    options.warn(`Run Release: looking up the tag ${name} answered HTTP ${reply.status}.`);
    return null;
  }
  return false;
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
  if (cut.ecosystem) {
    return `${cut.packages.map((p) => `\`${p}\``).join(', ')} now target ${cut.ecosystem} ${cut.major}, so the release line \`${cut.branch}\` was cut at ${cut.commit.slice(0, 12)}. `
      + 'Packages release only from release lines: a change ships once its pull request is backported there.\n\n'
      + 'The branch is protected only when a ruleset matches it.';
  }
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
