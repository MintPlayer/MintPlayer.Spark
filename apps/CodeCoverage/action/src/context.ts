import { context } from '@actions/github';

export interface UploadContext {
  repository: string;
  commitSha: string;
  branch: string;
  pullRequestNumber?: number;
  /**
   * The branch the pull request TARGETS (`main`), not its head. Absent on
   * non-PR events. Distinct from `branch`, which is the head — the server had
   * no way to know a PR's target before this.
   */
  baseRef?: string;
  /**
   * Tip of the target branch when the PR was last synchronised. Not the same
   * thing as the `base-sha` input, which is nx's affected-computation base and
   * is not guaranteed to be the merge-base.
   */
  prBaseSha?: string;
  /**
   * True when the pull request's head branch lives in a different repository
   * than its base — i.e. a contribution from a fork.
   *
   * Load-bearing, because a fork run has no credential at all: GitHub refuses
   * to mint an OIDC token for it and withholds secrets, so the ordinary upload
   * cannot authenticate and must not try. Undefined on non-PR events.
   *
   * WARNING compared by repository ID, never by full name. A name can be
   * changed to match the base repository's, and on a `pull_request_target` run
   * the names are easy to confuse; the numeric IDs cannot be spoofed by the
   * contributor. When either ID is missing we report `true`, because the safe
   * reading of "cannot tell" is the one that withholds the credential.
   */
  isFork?: boolean;
  runId: number;
  runAttempt: number;
  jobName: string;
  workflow: string;
  eventName: string;
  rootDir: string;
}

/**
 * Collects run identity from the Actions context. The one trap: on
 * pull_request events GITHUB_SHA is the ephemeral merge commit that exists in
 * no branch — reports must attach to the PR's head SHA instead.
 */
export function collectContext(): UploadContext {
  const isPullRequest = context.eventName.startsWith('pull_request');
  const pr = (context.payload as Record<string, any>)['pull_request'];

  const commitSha = isPullRequest && pr?.head?.sha ? (pr.head.sha as string) : context.sha;
  const branch = isPullRequest
    ? process.env['GITHUB_HEAD_REF'] || ''
    : process.env['GITHUB_REF_NAME'] || '';

  // GITHUB_BASE_REF first (set for every PR event), payload second so a
  // hand-built event file without the env var still works.
  const baseRef = isPullRequest
    ? process.env['GITHUB_BASE_REF'] || (pr?.base?.ref as string | undefined) || undefined
    : undefined;

  return {
    repository: `${context.repo.owner}/${context.repo.repo}`,
    commitSha,
    branch,
    pullRequestNumber: isPullRequest && pr?.number ? (pr.number as number) : undefined,
    isFork: isPullRequest ? isForkPullRequest(pr) : undefined,
    baseRef,
    prBaseSha: isPullRequest ? ((pr?.base?.sha as string | undefined) || undefined) : undefined,
    runId: context.runId,
    runAttempt: parseInt(process.env['GITHUB_RUN_ATTEMPT'] || '1', 10),
    jobName: process.env['GITHUB_JOB'] || '',
    workflow: context.workflow,
    eventName: context.eventName,
    rootDir: process.env['GITHUB_WORKSPACE'] || process.cwd(),
  };
}

/**
 * Whether a pull-request payload describes a fork contribution.
 *
 * Deliberately defaults to `true` for a payload it cannot read. Being wrong in
 * that direction costs a fork-path upload for a same-repository PR, which
 * simply gets less feedback; being wrong in the other direction sends a
 * credential to a workflow a stranger can modify.
 */
function isForkPullRequest(pr: Record<string, any> | undefined): boolean {
  const headId = pr?.head?.repo?.id;
  const baseId = pr?.base?.repo?.id;
  if (typeof headId !== 'number' || typeof baseId !== 'number') return true;
  return headId !== baseId;
}
