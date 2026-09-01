import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { collectContext } from './context';

// `@actions/github` exports a `context` singleton built from the environment at
// import time, so resetting modules cannot rebuild it — the singleton becomes a
// getter that constructs a fresh Context per access, which is what makes
// per-case environments possible at all.
vi.mock('@actions/github', async () => {
  const { Context } = await vi.importActual<typeof import('@actions/github/lib/context')>(
    '@actions/github/lib/context',
  );
  return {
    get context() {
      return new Context();
    },
  };
});

let tempDir: string;

function collectWith(env: Record<string, string>, payload?: Record<string, unknown>) {
  const base: Record<string, string> = {
    GITHUB_REPOSITORY: 'MintPlayer/CodeCoverage',
    GITHUB_SHA: 'merge-commit-sha',
    GITHUB_RUN_ID: '42',
    GITHUB_WORKFLOW: 'CI',
    GITHUB_WORKSPACE: '/workspace',
    ...env,
  };

  if (payload) {
    const eventPath = path.join(tempDir, 'event.json');
    fs.writeFileSync(eventPath, JSON.stringify(payload));
    base['GITHUB_EVENT_PATH'] = eventPath;
  }

  for (const [key, value] of Object.entries(base)) vi.stubEnv(key, value);

  return collectContext();
}

beforeEach(() => {
  tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'coverage-context-'));
  // GITHUB_EVENT_PATH leaks in from the real runner when these tests run in CI.
  vi.stubEnv('GITHUB_EVENT_PATH', '');
  for (const key of ['GITHUB_HEAD_REF', 'GITHUB_REF_NAME', 'GITHUB_JOB', 'GITHUB_RUN_ATTEMPT']) {
    vi.stubEnv(key, '');
  }
});

afterEach(() => {
  vi.unstubAllEnvs();
  fs.rmSync(tempDir, { recursive: true, force: true });
});

describe('collectContext', () => {
  it('reports the pushed commit and branch on a push', () => {
    const ctx = collectWith({
      GITHUB_EVENT_NAME: 'push',
      GITHUB_REF_NAME: 'master',
      GITHUB_JOB: 'test',
    });

    expect(ctx.repository).toBe('MintPlayer/CodeCoverage');
    expect(ctx.commitSha).toBe('merge-commit-sha');
    expect(ctx.branch).toBe('master');
    expect(ctx.pullRequestNumber).toBeUndefined();
    expect(ctx.parentSha).toBeUndefined();
    expect(ctx.jobName).toBe('test');
  });

  // The trap this function exists to avoid: on pull_request, GITHUB_SHA is an
  // ephemeral merge commit that exists in no branch. Reports attached to it would
  // never line up with anything the badge or a check run can point at.
  it('uses the PR head sha, not the ephemeral merge commit', () => {
    const ctx = collectWith(
      {
        GITHUB_EVENT_NAME: 'pull_request',
        GITHUB_HEAD_REF: 'feature/coverage',
      },
      { pull_request: { number: 7, head: { sha: 'head-sha' }, base: { sha: 'base-sha' } } },
    );

    expect(ctx.commitSha).toBe('head-sha');
    expect(ctx.branch).toBe('feature/coverage');
    expect(ctx.pullRequestNumber).toBe(7);
    expect(ctx.parentSha).toBe('base-sha');
  });

  it('handles pull_request_target the same way', () => {
    const ctx = collectWith(
      { GITHUB_EVENT_NAME: 'pull_request_target', GITHUB_HEAD_REF: 'feature/x' },
      { pull_request: { number: 9, head: { sha: 'head-sha' }, base: { sha: 'base-sha' } } },
    );

    expect(ctx.commitSha).toBe('head-sha');
    expect(ctx.pullRequestNumber).toBe(9);
  });

  // A pull_request event whose payload is unreadable must still upload against
  // *something* rather than crash the step.
  it('falls back to GITHUB_SHA when the PR payload is missing', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'pull_request' });

    expect(ctx.commitSha).toBe('merge-commit-sha');
    expect(ctx.pullRequestNumber).toBeUndefined();
  });

  it('defaults the run attempt to the first', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'push' });

    expect(ctx.runAttempt).toBe(1);
  });

  // The build key is (repository, sha, runId, runAttempt) — a re-run must not
  // merge its sessions into the original attempt's build.
  it('carries the run attempt through when a run is retried', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'push', GITHUB_RUN_ATTEMPT: '3' });

    expect(ctx.runId).toBe(42);
    expect(ctx.runAttempt).toBe(3);
  });

  it('uses the workspace as the root directory', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'push' });

    expect(ctx.rootDir).toBe('/workspace');
  });
});
