import { describe, expect, it } from 'vitest';
import { type Release } from './engine';
import { createReleases, MAX_WAIT_MS, retryAfterMs, SPACING_MS } from './releases';

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

describe('retryAfterMs', () => {
  it('reads retry-after, then the reset time, then waits a minute; caps the wait', () => {
    expect(retryAfterMs(new Response('', { status: 429, headers: { 'retry-after': '600' } }), '')).toBe(MAX_WAIT_MS);
    expect(retryAfterMs(new Response('', { status: 403, headers: { 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '1010' } }), '', 1_000_000)).toBe(10_000);
    expect(retryAfterMs(new Response('', { status: 429 }), '')).toBe(MAX_WAIT_MS);
    expect(retryAfterMs(new Response('', { status: 403 }), 'Must have admin rights')).toBeNull();
    expect(retryAfterMs(new Response('', { status: 500 }), 'rate limit')).toBeNull();
  });
});
