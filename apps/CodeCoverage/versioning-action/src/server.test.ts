import { planOnServer, releaseOnServer, runOnServer } from './server';

const versioned = { outcome: 'versioned', releases: [], refused: [], skipped: 0, warnings: [] };

function fakeFetch(status: number, body: unknown, seen: { url?: string; auth?: string } = {}): typeof fetch {
  return (async (url: string, init: RequestInit) => {
    seen.url = url;
    seen.auth = (init.headers as Record<string, string>).authorization;
    return new Response(typeof body === 'string' ? body : JSON.stringify(body), { status });
  }) as unknown as typeof fetch;
}

describe('runOnServer', () => {
  it('posts the manifest with an OIDC token for the app\'s own URL', async () => {
    const seen: { url?: string; auth?: string } = {};
    let audience = '';
    const answer = await runOnServer(
      { url: 'https://coverage.example/', idToken: async (a) => ((audience = a), 'jwt'), fetch: fakeFetch(200, versioned, seen) },
      { commit: 'c' },
    );
    expect(audience).toBe('https://coverage.example');
    expect(seen).toEqual({ url: 'https://coverage.example/api/versioning/run', auth: 'Bearer jwt' });
    expect(answer).toEqual({ kind: 'answer', status: 200, body: versioned });
  });

  it.each([400, 403, 409, 422])('%i is the app\'s answer, which the action fails on', async (status) => {
    const answer = await runOnServer({ url: 'https://x', idToken: async () => 't', fetch: fakeFetch(status, { ...versioned, outcome: 'refused' }) }, {});
    expect(answer.kind).toBe('answer');
  });

  it.each([502, 503])('%i falls back to the local engine', async (status) => {
    const answer = await runOnServer({ url: 'https://x', idToken: async () => 't', fetch: fakeFetch(status, 'down') }, {});
    expect(answer.kind).toBe('unavailable');
  });

  it('plans and releases on their own routes; the release sends only the seal (#552 D6)', async () => {
    const seen: { url?: string } = {};
    let body = '';
    const recording = (async (url: string, init: RequestInit) => {
      seen.url = url;
      body = String(init.body);
      return new Response(JSON.stringify(versioned), { status: 200 });
    }) as unknown as typeof fetch;

    await planOnServer({ url: 'https://x', idToken: async () => 't', fetch: recording }, { commit: 'c' });
    expect(seen.url).toBe('https://x/api/versioning/plan');
    await releaseOnServer({ url: 'https://x', idToken: async () => 't', fetch: recording }, 'sealed');
    expect(seen.url).toBe('https://x/api/versioning/release');
    expect(JSON.parse(body)).toEqual({ seal: 'sealed' });
  });

  it('a stale release (409) is an answer, so the action does not fall back', async () => {
    const answer = await releaseOnServer({ url: 'https://x', idToken: async () => 't', fetch: fakeFetch(409, { ...versioned, outcome: 'refused', reason: 'stale-plan' }) }, 's');
    expect(answer).toMatchObject({ kind: 'answer', status: 409 });
  });

  it('falls back when the job has no OIDC token or the app is unreachable', async () => {
    const noToken = await runOnServer({ url: 'https://x', idToken: async () => { throw new Error('no id-token permission'); } }, {});
    expect(noToken).toMatchObject({ kind: 'unavailable', reason: expect.stringContaining('id-token') });

    const unreachable = await runOnServer({
      url: 'https://x', idToken: async () => 't', fetch: (async () => { throw new TypeError('fetch failed'); }) as unknown as typeof fetch,
    }, {});
    expect(unreachable.kind).toBe('unavailable');
  });
});
