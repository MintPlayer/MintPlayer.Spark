import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { collectContext } from './context';
import { anonymousCredential, authHeaders, staticCredential } from './credential';

// Same reasoning as context.test.ts: `@actions/github` builds its context
// singleton at import time, so it has to become a per-access getter.
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

function pullRequestPayload(headRepoId: unknown, baseRepoId: unknown) {
  return {
    pull_request: {
      number: 7,
      head: { sha: 'head-sha', ref: 'feature/x', repo: { id: headRepoId } },
      base: { sha: 'base-sha', ref: 'main', repo: { id: baseRepoId } },
    },
  };
}

beforeEach(() => {
  tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'coverage-fork-'));
  vi.stubEnv('GITHUB_EVENT_PATH', '');
  for (const key of ['GITHUB_HEAD_REF', 'GITHUB_BASE_REF', 'GITHUB_REF_NAME', 'GITHUB_JOB', 'GITHUB_RUN_ATTEMPT']) {
    vi.stubEnv(key, '');
  }
});

afterEach(() => {
  vi.unstubAllEnvs();
  fs.rmSync(tempDir, { recursive: true, force: true });
});

describe('fork detection', () => {
  it('reports a fork when the head repository differs from the base', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'pull_request' }, pullRequestPayload(222, 111));
    expect(ctx.isFork).toBe(true);
  });

  it('reports NOT a fork when both sides are the same repository', () => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'pull_request' }, pullRequestPayload(111, 111));
    expect(ctx.isFork).toBe(false);
  });

  // The safety direction: a payload we cannot read must NOT be treated as
  // first-party, because that is the reading that hands out a credential.
  it.each([
    ['head repo missing', undefined, 111],
    ['base repo missing', 222, undefined],
    ['a string id instead of a number', '222', 111],
  ])('reports a fork when it cannot tell (%s)', (_label, head, base) => {
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'pull_request' }, pullRequestPayload(head, base));
    expect(ctx.isFork).toBe(true);
  });

  it('is undefined outside a pull request, rather than false', () => {
    // Not merely cosmetic: `false` would assert "this is a same-repo PR" about a
    // push, which is a claim the event cannot support.
    const ctx = collectWith({ GITHUB_EVENT_NAME: 'push', GITHUB_REF_NAME: 'master' });
    expect(ctx.isFork).toBeUndefined();
  });

  // Names are contributor-controlled; ids are not. A fork renamed to look like
  // the base must still be detected.
  it('compares ids, not names', () => {
    const ctx = collectWith(
      { GITHUB_EVENT_NAME: 'pull_request' },
      {
        pull_request: {
          number: 7,
          head: { sha: 'head-sha', ref: 'main', repo: { id: 999, full_name: 'MintPlayer/CodeCoverage' } },
          base: { sha: 'base-sha', ref: 'main', repo: { id: 111, full_name: 'MintPlayer/CodeCoverage' } },
        },
      },
    );
    expect(ctx.isFork).toBe(true);
  });
});

describe('anonymous credential', () => {
  it('produces NO Authorization header at all', async () => {
    // The distinction that matters: an absent header, never `Bearer undefined`.
    await expect(authHeaders(anonymousCredential())).resolves.toEqual({});
  });

  it('still produces one for a real token', async () => {
    await expect(authHeaders(staticCredential('covt_abc'))).resolves.toEqual({
      Authorization: 'Bearer covt_abc',
    });
  });

  it('throws rather than yielding a token, so a caller cannot interpolate one by accident', async () => {
    await expect(anonymousCredential().get()).rejects.toThrow(/anonymous credential has no token/i);
  });
});
