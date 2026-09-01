import { afterEach, describe, expect, it, vi } from 'vitest';
import { Credential } from './credential';
import { UploadStatus, WaitTimeout, rate, waitForFinalize } from './status';

vi.mock('@actions/core', () => ({
  info: vi.fn(),
  debug: vi.fn(),
}));

function credentialSpy(token = 'covt_test'): Credential & { invalidations: number } {
  const spy = {
    invalidations: 0,
    get: async () => token,
    invalidate: () => {
      spy.invalidations += 1;
    },
  };
  return spy;
}

/**
 * Sub-millisecond intervals rather than fake timers: waitForFinalize interleaves
 * awaited fetches with setTimeout, which fake timers cannot advance through
 * without hand-pumping the microtask queue between every tick.
 */
function options(overrides: Partial<Parameters<typeof waitForFinalize>[1]> = {}) {
  return {
    url: 'https://coverage.example.com',
    repository: 'MintPlayer/CodeCoverage',
    commitSha: 'abc123',
    runId: 42,
    runAttempt: 1,
    timeoutSeconds: 30,
    pollIntervalSeconds: 0.001,
    ...overrides,
  };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function statusBody(state: string): UploadStatus {
  return { buildId: 'build-1', state, status: state === 'InFlight' ? 'Open' : 'Finalized' };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('waitForFinalize', () => {
  it('polls until the build leaves InFlight', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(statusBody('InFlight')))
      .mockResolvedValueOnce(jsonResponse(statusBody('InFlight')))
      .mockResolvedValueOnce(jsonResponse(statusBody('Complete')));
    vi.stubGlobal('fetch', fetchMock);

    const status = await waitForFinalize(credentialSpy(), options());

    expect(status.state).toBe('Complete');
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  // A build that finished with a failed session has finished. Waiting for it to
  // become clean would burn the entire timeout on a state it can never reach.
  it('treats CompleteWithErrors as terminal', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(statusBody('CompleteWithErrors'))));

    const status = await waitForFinalize(credentialSpy(), options());

    expect(status.state).toBe('CompleteWithErrors');
  });

  it('sends the run identity as query parameters, with a bearer credential', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(statusBody('Complete')));
    vi.stubGlobal('fetch', fetchMock);

    await waitForFinalize(credentialSpy('covt_abc'), options());

    const [endpoint, init] = fetchMock.mock.calls[0];
    const url = new URL(endpoint as string);
    expect(url.pathname).toBe('/api/uploads/status');
    expect(url.searchParams.get('repository')).toBe('MintPlayer/CodeCoverage');
    expect(url.searchParams.get('commitSha')).toBe('abc123');
    expect(url.searchParams.get('runId')).toBe('42');
    expect(url.searchParams.get('runAttempt')).toBe('1');
    expect((init as RequestInit).headers).toEqual({ Authorization: 'Bearer covt_abc' });
  });

  // 429 means "wait longer", never "give up" — only the deadline ends a wait.
  it('keeps polling through a 429 instead of throwing', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429 }))
      .mockResolvedValueOnce(jsonResponse(statusBody('Complete')));
    vi.stubGlobal('fetch', fetchMock);

    const status = await waitForFinalize(credentialSpy(), options());

    expect(status.state).toBe('Complete');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('honours Retry-After, capped at the maximum interval', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429, headers: { 'retry-after': '9999' } }))
      .mockResolvedValueOnce(jsonResponse(statusBody('Complete')));
    vi.stubGlobal('fetch', fetchMock);

    // timeoutSeconds 5 bounds the sleep to the remaining deadline, so a capped
    // 60s interval cannot hang the test; the point is that it did not throw.
    const status = await waitForFinalize(credentialSpy(), options({ timeoutSeconds: 0.02 }));

    expect(status.state).toBe('Complete');
  });

  // An OIDC id-token lives five minutes; a wait can run thirty.
  it('re-mints the credential once on a 401, then retries', async () => {
    const credential = credentialSpy();
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(jsonResponse(statusBody('Complete')));
    vi.stubGlobal('fetch', fetchMock);

    const status = await waitForFinalize(credential, options());

    expect(credential.invalidations).toBe(1);
    expect(status.state).toBe('Complete');
  });

  // A second consecutive 401 is a real authorization failure, not an expiry.
  it('gives up on a repeated 401', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('nope', { status: 401 })));

    await expect(waitForFinalize(credentialSpy(), options())).rejects.toThrow(/401/);
  });

  it('throws on a non-retryable status', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('boom', { status: 500 })));

    await expect(waitForFinalize(credentialSpy(), options())).rejects.toThrow(/500/);
  });

  it('throws WaitTimeout once the deadline passes', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(statusBody('InFlight'))));

    await expect(waitForFinalize(credentialSpy(), options({ timeoutSeconds: 0 }))).rejects.toBeInstanceOf(
      WaitTimeout,
    );
  });
});

describe('rate', () => {
  it('reports a percentage to one decimal', () => {
    expect(rate(1, 2)).toBe('50.0');
    expect(rate(2, 3)).toBe('66.7');
    expect(rate(4, 4)).toBe('100.0');
  });

  // The contract is explicit that 0/0 is no data, not full coverage. Rendering it
  // as 100% would let an empty report read as a perfect one.
  it('returns nothing rather than 100% when there is nothing to cover', () => {
    expect(rate(0, 0)).toBe('');
    expect(rate(undefined, 10)).toBe('');
    expect(rate(5, undefined)).toBe('');
  });
});
