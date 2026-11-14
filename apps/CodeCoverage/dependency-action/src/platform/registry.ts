/** The slice of `fetch` the registry check uses, injectable for tests. */
export type FetchLike = (url: string, init?: { method?: string; headers?: Record<string, string> }) => Promise<Response>;

const DOCKER_HUB = 'registry-1.docker.io';

/** Every manifest media type a tag may resolve to: a multi-arch index or a single image, OCI or Docker. */
const MANIFEST_TYPES = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');

/**
 * `node`, `bitnami/node`, `mcr.microsoft.com/dotnet/sdk` -> registry host and repository. The first path segment is a
 * host when it has a dot or a port, or is localhost (Docker's own rule); otherwise the image is on Docker Hub, where an
 * official image lives under `library/`.
 */
export function registryOf(image: string): { host: string; repository: string } {
  const slash = image.indexOf('/');
  const first = slash < 0 ? '' : image.slice(0, slash);
  if (first && (first.includes('.') || first.includes(':') || first === 'localhost')) {
    return { host: first === 'docker.io' ? DOCKER_HUB : first, repository: image.slice(slash + 1) };
  }
  return { host: DOCKER_HUB, repository: slash < 0 ? `library/${image}` : image };
}

/** `Bearer realm="…",service="…",scope="…"` -> its parameters; null for any other challenge. */
function bearerChallenge(header: string | null): Record<string, string> | null {
  if (!header || !/^Bearer\s/i.test(header)) return null;
  const params: Record<string, string> = {};
  for (const m of header.matchAll(/(\w+)="([^"]*)"/g)) params[m[1].toLowerCase()] = m[2];
  return params.realm ? params : null;
}

/**
 * platform-locations-PRD P7 (spike S5): does `image:tag` exist? A HEAD on the OCI distribution API's manifest
 * endpoint, which Docker Hub does not count against its pull limit. A 401 with a Bearer challenge is answered with an
 * anonymous token for the challenge's realm, service and scope, and the HEAD is retried once. True on 2xx, false on
 * 404; null when the registry cannot say (unreachable, a private image, any other status), which the apply reports
 * rather than guessing. No credentials are ever sent: the check is for public base images.
 */
export function registryTagExists(fetchImpl: FetchLike = fetch): (image: string, tag: string) => Promise<boolean | null> {
  return async (image, tag) => {
    const { host, repository } = registryOf(image);
    const url = `https://${host}/v2/${repository}/manifests/${encodeURIComponent(tag)}`;
    const head = (authorization?: string) =>
      fetchImpl(url, { method: 'HEAD', headers: { accept: MANIFEST_TYPES, ...(authorization ? { authorization } : {}) } });
    try {
      let res = await head();
      if (res.status === 401) {
        const challenge = bearerChallenge(res.headers.get('www-authenticate'));
        if (!challenge) return null;
        const tokenUrl = new URL(challenge.realm);
        if (tokenUrl.protocol !== 'https:') return null;
        if (challenge.service) tokenUrl.searchParams.set('service', challenge.service);
        if (challenge.scope) tokenUrl.searchParams.set('scope', challenge.scope);
        const tokenRes = await fetchImpl(tokenUrl.toString(), { method: 'GET' });
        if (!tokenRes.ok) return null;
        const body = (await tokenRes.json()) as { token?: string; access_token?: string };
        const token = body.token ?? body.access_token;
        if (!token) return null;
        res = await head(`Bearer ${token}`);
      }
      if (res.ok) return true;
      if (res.status === 404) return false;
      return null;
    } catch {
      return null;
    }
  };
}
