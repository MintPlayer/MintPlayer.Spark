import { oidcCredential, staticCredential } from './credential';

const mockGetIDToken = vi.fn();
vi.mock('@actions/core', () => ({ getIDToken: (audience: string) => mockGetIDToken(audience) }));

/** A JWT whose only readable claim is `exp`; the signature is never checked here. */
function jwtExpiringIn(seconds: number): string {
  const exp = Math.floor(Date.now() / 1000) + seconds;
  const payload = Buffer.from(JSON.stringify({ exp }), 'utf8').toString('base64url');
  return `header.${payload}.signature`;
}

beforeEach(() => {
  mockGetIDToken.mockReset();
});

describe('staticCredential', () => {
  it('hands back the same upload token every time', async () => {
    const credential = staticCredential('covt_abc');

    expect(await credential.get()).toBe('covt_abc');
    expect(await credential.get()).toBe('covt_abc');
  });

  // An upload token never expires, so a 401 refresh has nothing to re-mint.
  // invalidate() must still be safe to call — status.ts calls it blind.
  it('survives invalidate() without changing', async () => {
    const credential = staticCredential('covt_abc');

    credential.invalidate();

    expect(await credential.get()).toBe('covt_abc');
  });
});

describe('oidcCredential', () => {
  it('mints against the server URL as the audience', async () => {
    mockGetIDToken.mockResolvedValue(jwtExpiringIn(300));
    const credential = oidcCredential('https://coverage.example.com');

    await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledWith('https://coverage.example.com');
  });

  it('reuses a token that is still comfortably valid', async () => {
    mockGetIDToken.mockResolvedValue(jwtExpiringIn(300));
    const credential = oidcCredential('https://coverage.example.com');

    await credential.get();
    await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledTimes(1);
  });

  // The whole reason this type exists: a five-minute token carried through a
  // thirty-minute wait would 401 partway, on slow builds only.
  it('re-mints within the refresh margin of expiry', async () => {
    mockGetIDToken.mockResolvedValueOnce(jwtExpiringIn(30)).mockResolvedValueOnce(jwtExpiringIn(300));
    const credential = oidcCredential('https://coverage.example.com');

    const first = await credential.get();
    const second = await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledTimes(2);
    expect(second).not.toBe(first);
  });

  it('re-mints after invalidate(), even on an unexpired token', async () => {
    mockGetIDToken.mockResolvedValueOnce(jwtExpiringIn(300)).mockResolvedValueOnce(jwtExpiringIn(300));
    const credential = oidcCredential('https://coverage.example.com');

    await credential.get();
    credential.invalidate();
    await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledTimes(2);
  });

  // An unreadable token is assumed to live the documented five minutes rather
  // than treated as already expired — which would re-mint on every single call.
  it('falls back to a five-minute lifetime when the token is not a readable JWT', async () => {
    mockGetIDToken.mockResolvedValue('not-a-jwt');
    const credential = oidcCredential('https://coverage.example.com');

    await credential.get();
    await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledTimes(1);
  });

  it('falls back when the payload carries no numeric exp', async () => {
    const payload = Buffer.from(JSON.stringify({ sub: 'repo:x' }), 'utf8').toString('base64url');
    mockGetIDToken.mockResolvedValue(`header.${payload}.signature`);
    const credential = oidcCredential('https://coverage.example.com');

    await credential.get();
    await credential.get();

    expect(mockGetIDToken).toHaveBeenCalledTimes(1);
  });
});
