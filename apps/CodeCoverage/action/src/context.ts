import { context } from '@actions/github';

export interface UploadContext {
  repository: string;
  commitSha: string;
  branch: string;
  pullRequestNumber?: number;
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

  return {
    repository: `${context.repo.owner}/${context.repo.repo}`,
    commitSha,
    branch,
    pullRequestNumber: isPullRequest && pr?.number ? (pr.number as number) : undefined,
    runId: context.runId,
    runAttempt: parseInt(process.env['GITHUB_RUN_ATTEMPT'] || '1', 10),
    jobName: process.env['GITHUB_JOB'] || '',
    workflow: context.workflow,
    eventName: context.eventName,
    rootDir: process.env['GITHUB_WORKSPACE'] || process.cwd(),
  };
}
