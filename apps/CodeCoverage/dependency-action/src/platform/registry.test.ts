import { registryTagExists, type FetchLike } from './registry';

interface Call {
  method: string;
  url: string;
  authorization: string | null;
}

/** A registry that wants an anonymous bearer token, as Docker Hub, ghcr.io and mcr.microsoft.com's peers do. */
function fakeRegistry(tags: Record<string, string[]>, opts: { anonymous?: boolean } = {}): { fetch: FetchLike; calls: Call[] } {
  const calls: Call[] = [];
  const fetch: FetchLike = async (url, init) => {
    const authorization = (init?.headers as Record<string, string> | undefined)?.authorization ?? null;
    calls.push({ method: init?.method ?? 'GET', url, authorization });
    const u = new URL(url);
    if (u.pathname === '/token') return new Response(JSON.stringify({ token: 'anon' }), { status: 200 });
    const m = /^\/v2\/(.+)\/manifests\/([^/]+)$/.exec(u.pathname);
    if (!m) return new Response('', { status: 404 });
    if (!opts.anonymous && authorization !== 'Bearer anon') {
      return new Response('', {
        status: 401,
        headers: { 'www-authenticate': `Bearer realm="https://${u.host}/token",service="${u.host}",scope="repository:${m[1]}:pull"` },
      });
    }
    return new Response('', { status: tags[`${u.host}/${m[1]}`]?.includes(m[2]) ? 200 : 404 });
  };
  return { fetch, calls };
}

describe('platform-locations-PRD P7: a Docker tag is checked against its registry before it is written', () => {
  it('a Docker Hub official image is library/<name> on registry-1.docker.io, behind an anonymous token', async () => {
    const { fetch, calls } = fakeRegistry({ 'registry-1.docker.io/library/node': ['26-alpine'] });
    const exists = registryTagExists(fetch);

    expect(await exists('node', '26-alpine')).toBe(true);
    expect(await exists('node', '26-bookworm-nope')).toBe(false);

    expect(calls[0]).toEqual({ method: 'HEAD', url: 'https://registry-1.docker.io/v2/library/node/manifests/26-alpine', authorization: null });
    expect(calls[1].url).toBe('https://registry-1.docker.io/token?service=registry-1.docker.io&scope=repository%3Alibrary%2Fnode%3Apull');
    expect(calls[2]).toMatchObject({ method: 'HEAD', authorization: 'Bearer anon' });
    expect(calls.every((c) => c.method !== 'GET' || c.url.includes('/token')), 'manifests are only HEADed: no pull is counted').toBe(true);
  });

  it('a registry host in the image name is asked directly, and an anonymous registry needs no token', async () => {
    const { fetch, calls } = fakeRegistry({ 'mcr.microsoft.com/dotnet/sdk': ['11.0'] }, { anonymous: true });
    const exists = registryTagExists(fetch);

    expect(await exists('mcr.microsoft.com/dotnet/sdk', '11.0')).toBe(true);
    expect(calls).toEqual([{ method: 'HEAD', url: 'https://mcr.microsoft.com/v2/dotnet/sdk/manifests/11.0', authorization: null }]);
  });

  it('a Docker Hub user image keeps its namespace', async () => {
    const { fetch } = fakeRegistry({ 'registry-1.docker.io/bitnami/node': ['26'] });
    expect(await registryTagExists(fetch)('bitnami/node', '26')).toBe(true);
  });

  it('a registry that cannot be reached, or answers otherwise, cannot say (null)', async () => {
    const offline: FetchLike = async () => {
      throw new Error('getaddrinfo ENOTFOUND');
    };
    expect(await registryTagExists(offline)('node', '26')).toBeNull();
    const broken: FetchLike = async () => new Response('', { status: 500 });
    expect(await registryTagExists(broken)('node', '26')).toBeNull();
    const noRealm: FetchLike = async () => new Response('', { status: 401, headers: { 'www-authenticate': 'Basic realm="x"' } });
    expect(await registryTagExists(noRealm)('ghcr.io/o/private', '1')).toBeNull();
  });
});
