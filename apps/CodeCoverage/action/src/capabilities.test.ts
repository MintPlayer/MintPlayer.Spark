import { BASELINE, CLIENT_CONTRACT, fetchCapabilities, warnAboutUnsupportedInputs } from './capabilities';
import { Credential } from './credential';

const mockInfo = vi.fn();
const mockDebug = vi.fn();
const mockWarning = vi.fn();
vi.mock('@actions/core', () => ({
  info: (message: string) => mockInfo(message),
  debug: (message: string) => mockDebug(message),
  warning: (message: string) => mockWarning(message),
}));

function credential(): Credential {
  return { get: async () => 'covt_test', invalidate: () => {} };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

beforeEach(() => {
  mockInfo.mockReset();
  mockDebug.mockReset();
  mockWarning.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('fetchCapabilities', () => {
  it('reads the contract and features the server advertises', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ contract: 1, features: ['partial-uploads'] })));

    const capabilities = await fetchCapabilities('https://coverage.example.com', credential());

    expect(capabilities).toEqual({ contract: 1, features: ['partial-uploads'] });
  });

  it('sends the bearer credential to the capabilities endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ contract: 1, features: [] }));
    vi.stubGlobal('fetch', fetchMock);

    await fetchCapabilities('https://coverage.example.com', credential());

    const [endpoint, init] = fetchMock.mock.calls[0];
    expect(endpoint).toBe('https://coverage.example.com/api/uploads/capabilities');
    expect((init as RequestInit).headers).toEqual({ Authorization: 'Bearer covt_test' });
  });

  // The whole point of the probe: the image currently deployed has no such
  // endpoint, and must come back as a usable answer rather than an error.
  it('treats 404 as the baseline contract', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 404 })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual(BASELINE);
  });

  it('degrades to the baseline on a server error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('boom', { status: 500 })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual(BASELINE);
  });

  // A probe that could fail the step would be worse than no probe at all: the
  // upload itself reports auth and connectivity failures far better.
  it('degrades to the baseline when the request throws', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('ECONNREFUSED')));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual(BASELINE);
  });

  it('degrades to the baseline on an unauthorized probe rather than reporting twice', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 401 })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual(BASELINE);
    expect(mockWarning).not.toHaveBeenCalled();
  });

  it('survives a body that is not readable JSON', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>maintenance</html>', { status: 200 })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual(BASELINE);
  });

  it('defaults each field independently when the payload is partial', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ features: ['partial-uploads'] })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual({
      contract: 0,
      features: ['partial-uploads'],
    });
  });

  it('discards a features value that is not an array of strings', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ contract: 2, features: [1, 'ok', null] })));

    expect(await fetchCapabilities('https://coverage.example.com', credential())).toEqual({
      contract: 2,
      features: ['ok'],
    });
  });
});

describe('warnAboutUnsupportedInputs', () => {
  // The silent-wrong-number case this exists to prevent: an old server accepts
  // `partial` (unknown multipart fields are dropped, not rejected) and then
  // compares a subset against a whole-workspace baseline.
  it('warns when partial was requested and the server cannot do it', () => {
    warnAboutUnsupportedInputs({ contract: 1, features: [] }, { partial: true });

    expect(mockWarning).toHaveBeenCalledTimes(1);
    expect(mockWarning.mock.calls[0][0]).toMatch(/partial/i);
  });

  it('stays quiet when the server supports what was asked for', () => {
    warnAboutUnsupportedInputs({ contract: 1, features: ['partial-uploads', 'carry-forward'] }, { partial: true });

    expect(mockWarning).not.toHaveBeenCalled();
  });

  it('warns when partial was requested and the server cannot carry forward', () => {
    warnAboutUnsupportedInputs({ contract: 1, features: ['partial-uploads'] }, { partial: true });
    expect(mockWarning).toHaveBeenCalledTimes(1);
    expect(mockWarning.mock.calls[0][0]).toMatch(/carry/i);
  });

  it('does not warn about partial when partial was not requested', () => {
    warnAboutUnsupportedInputs({ contract: 1, features: [] }, { partial: false });

    expect(mockWarning).not.toHaveBeenCalled();
  });

  // Nothing specific can be claimed about a server that advertises nothing, so
  // it gets one honest line rather than a warning per input.
  it('reports a pre-capabilities server once, without warning per input', () => {
    warnAboutUnsupportedInputs(BASELINE, { partial: true });

    expect(mockWarning).not.toHaveBeenCalled();
    expect(mockInfo).toHaveBeenCalledTimes(1);
    expect(mockInfo.mock.calls[0][0]).toMatch(/does not advertise/i);
  });

  it('says nothing when the server is level with the action', () => {
    warnAboutUnsupportedInputs({ contract: CLIENT_CONTRACT, features: ['partial-uploads', 'carry-forward'] }, { partial: true });

    expect(mockWarning).not.toHaveBeenCalled();
    expect(mockInfo).not.toHaveBeenCalled();
  });

  // A server that has been bumped ahead of this action is not a problem: every
  // contract change is additive from the client's side, so there is nothing to
  // say. The reverse case — action ahead of a server that still advertises — is
  // unreachable while CLIENT_CONTRACT is 1, because contract 0 is the
  // pre-capabilities path tested above; it becomes reachable at contract 2.
  it('says nothing when the server is ahead of the action', () => {
    warnAboutUnsupportedInputs({ contract: CLIENT_CONTRACT + 1, features: ['partial-uploads', 'carry-forward'] }, { partial: true });

    expect(mockWarning).not.toHaveBeenCalled();
    expect(mockInfo).not.toHaveBeenCalled();
  });
});
