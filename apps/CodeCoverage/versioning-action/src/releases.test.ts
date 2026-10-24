import { describe, expect, it } from 'vitest';
import { type BranchCut, type Release } from './engine';
import { createReleases, cutComment, cutReleaseBranches, MAX_WAIT_MS, retryAfterMs, SPACING_MS } from './releases';

const release = (tag: string, withNotes = true): Release => ({
  id: tag.split('/v')[0], kind: 'nuget', version: tag.split('/v')[1], tag, reasons: [], taggedByHand: false, tagOutcome: 'created',
  release: withNotes ? { name: tag.replace('/v', ' '), body: '### Fixes\n- fix: x (#1)', prerelease: false } : undefined,
});

function harness(replies: Response[]) {
  const requests: { url: string; body: any }[] = [];
  const sleeps: number[] = [];
  const fetch = (async (url: string, init: RequestInit) => {
    requests.push({ url, body: JSON.parse(init.body as string) });
    return replies.shift()!;
  }) as unknown as typeof globalThis.fetch;
  const warnings: string[] = [];
  const options = {
    apiUrl: 'https://api.github.com', repository: 'acme/repo', token: 't', fetch,
    sleep: async (ms: number) => { sleeps.push(ms); }, log: () => {}, warn: (m: string) => { warnings.push(m); },
  };
  return { options, requests, sleeps, warnings };
}

describe('createReleases', () => {
  it('creates one Release per tag with notes, spaced, and counts an existing one as done', async () => {
    const h = harness([new Response('{}', { status: 201 }), new Response('{"errors":[{"code":"already_exists"}]}', { status: 422 })]);
    const outcomes = await createReleases(h.options, [release('A/v1.0.0'), release('B/v2.0.0', false), release('C/v1.1.0')]);
    expect([...outcomes]).toEqual([['A/v1.0.0', 'created'], ['C/v1.1.0', 'exists']]);
    expect(h.requests.map((r) => r.body.tag_name)).toEqual(['A/v1.0.0', 'C/v1.1.0']);
    expect(h.requests[0].body).toMatchObject({ name: 'A 1.0.0', prerelease: false, make_latest: 'false' });
    expect(h.sleeps).toEqual([SPACING_MS]);
  });

  it('waits out a rate limit and retries, but not a missing permission', async () => {
    const h = harness([
      new Response('secondary rate limit', { status: 403, headers: { 'retry-after': '5' } }),
      new Response('{}', { status: 201 }),
      new Response('Resource not accessible by integration', { status: 403, headers: { 'x-ratelimit-remaining': '4000' } }),
    ]);
    const outcomes = await createReleases(h.options, [release('A/v1.0.0'), release('B/v1.0.0')]);
    expect([...outcomes]).toEqual([['A/v1.0.0', 'created'], ['B/v1.0.0', 'failed']]);
    expect(h.sleeps).toEqual([5000, SPACING_MS]);
    expect(h.warnings).toEqual([expect.stringContaining('B/v1.0.0: HTTP 403')]);
  });
});

describe('cutReleaseBranches', () => {
  const sha = 'a'.repeat(40);
  const cut = (major: number, pullRequest?: number): BranchCut => ({ branch: `release/${major}.x`, major, commit: sha, packages: ['Acme.Core'], pullRequest });

  it('creates the branch at the first parent and comments on the pull request only when it created it', async () => {
    const h = harness([
      new Response('{}', { status: 201 }),
      new Response('{}', { status: 201 }),
      new Response('{"message":"Reference already exists"}', { status: 422 }),
    ]);
    const outcomes = await cutReleaseBranches(h.options, [cut(10, 42), cut(8, 43)]);
    expect([...outcomes]).toEqual([['release/10.x', 'created'], ['release/8.x', 'exists']]);
    expect(h.requests.map((r) => r.url)).toEqual([
      'https://api.github.com/repos/acme/repo/git/refs',
      'https://api.github.com/repos/acme/repo/issues/42/comments',
      'https://api.github.com/repos/acme/repo/git/refs',
    ]);
    expect(h.requests[0].body).toEqual({ ref: 'refs/heads/release/10.x', sha });
    expect(h.requests[1].body.body).toContain('`backport: 10.x`');
    expect(h.sleeps).toEqual([SPACING_MS, SPACING_MS]);
    expect(h.warnings).toEqual([]);
  });

  it('warns on a refused cut and does not comment', async () => {
    const h = harness([new Response('Resource not accessible by integration', { status: 403 })]);
    const outcomes = await cutReleaseBranches(h.options, [cut(10, 42)]);
    expect([...outcomes]).toEqual([['release/10.x', 'failed']]);
    expect(h.requests).toHaveLength(1);
    expect(h.warnings).toEqual([expect.stringContaining('release/10.x: HTTP 403')]);
  });

  it('writes the same comment as the server', () => {
    expect(cutComment(cut(10, 42))).toBe(
      '`Acme.Core` left major 10, so `release/10.x` was cut at aaaaaaaaaaaa: patches for the 10.x line merge there and are versioned as 10.x.\n\n'
      + 'To backport a merged pull request, label it `backport: 10.x` or comment `/backport 10.x`. The branch is protected only when a ruleset matches it.');
  });
});

describe('retryAfterMs', () => {
  it('reads retry-after, then the reset time, then waits a minute; caps the wait', () => {
    expect(retryAfterMs(new Response('', { status: 429, headers: { 'retry-after': '600' } }), '')).toBe(MAX_WAIT_MS);
    expect(retryAfterMs(new Response('', { status: 403, headers: { 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '1010' } }), '', 1_000_000)).toBe(10_000);
    expect(retryAfterMs(new Response('', { status: 429 }), '')).toBe(MAX_WAIT_MS);
    expect(retryAfterMs(new Response('', { status: 403 }), 'Must have admin rights')).toBeNull();
    expect(retryAfterMs(new Response('', { status: 500 }), 'rate limit')).toBeNull();
  });
});
