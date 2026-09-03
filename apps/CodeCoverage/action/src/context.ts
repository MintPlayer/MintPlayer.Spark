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
