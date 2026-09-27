import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as zlib from 'zlib';
import { run } from './main';

/**
 * `run()` end to end: the real context, credential, capability, file-search, rebase and
 * status modules, with only the process boundaries faked — `@actions/core` (inputs and
 * outputs), `@actions/exec` (git), `@actions/github` (the event) and `fetch` (the server).
 * Faking any of the modules in between would test the wiring against a guess of what
 * those modules do, which is the part most worth checking.
 */

const mocks = vi.hoisted(() => ({
  inputs: new Map<string, string>(),
  outputs: new Map<string, unknown>(),
  info: [] as string[],
  warnings: [] as string[],
  failed: [] as string[],
  getIDToken: vi.fn(async (_audience: string) => 'oidc-id-token'),
  getExecOutput: vi.fn(),
}));

vi.mock('@actions/core', () => ({
  // Honours `required` like the real one, so a missing url takes the real exit.
  getInput: (name: string, options?: { required?: boolean }) => {
    const value = mocks.inputs.get(name) ?? '';
    if (options?.required && !value) throw new Error(`Input required and not supplied: ${name}`);
    return value;
  },
  setOutput: (name: string, value: unknown) => mocks.outputs.set(name, value),
  setFailed: (message: string) => mocks.failed.push(message),
  info: (message: string) => mocks.info.push(message),
  warning: (message: string) => mocks.warnings.push(message),
  debug: () => {},
  getIDToken: (audience: string) => mocks.getIDToken(audience),
}));

vi.mock('@actions/exec', () => ({
  getExecOutput: (...args: unknown[]) => mocks.getExecOutput(...args),
}));

// Same reasoning as context.test.ts: the `context` singleton is built at import time,
// so it becomes a getter to let each case set its own environment.
vi.mock('@actions/github', async () => {
  // v7+ no longer exports lib/context; the class is reachable through the singleton.
  const actual = await vi.importActual<typeof import('@actions/github')>('@actions/github');
  const Context = actual.context.constructor as new () => typeof actual.context;
  return {
    get context() {
      return new Context();
    },
  };
});

const SERVER = 'https://coverage.example.test';
const OID = 'a'.repeat(40);
const PARENT = 'b'.repeat(40);

interface Call {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

let tempDir: string;
let calls: Call[];
// One queue per endpoint; the last entry repeats once the queue is drained.
let routes: Record<'capabilities' | 'upload' | 'finish' | 'status', (() => Response | Promise<Response>)[]>;

const json = (body: unknown, status = 200) => () =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
const text = (body: string, status: number) => () => new Response(body, { status });

function routeOf(url: string): keyof typeof routes {
  if (url.includes('/api/uploads/capabilities')) return 'capabilities';
  if (url.includes('/api/uploads/finish')) return 'finish';
  if (url.includes('/api/uploads/status')) return 'status';
  return 'upload';
}

function callsTo(route: keyof typeof routes): Call[] {
  return calls.filter((c) => routeOf(c.url) === route);
}

function uploadForm(index = 0): FormData {
  return callsTo('upload')[index].body as FormData;
}

async function gunzipped(file: FormDataEntryValue): Promise<string> {
  return zlib.gunzipSync(Buffer.from(await (file as Blob).arrayBuffer())).toString('utf8');
}

function setInputs(values: Record<string, string>) {
  for (const [key, value] of Object.entries(values)) mocks.inputs.set(key, value);
}

function setEvent(env: Record<string, string>, payload?: Record<string, unknown>) {
  for (const [key, value] of Object.entries(env)) vi.stubEnv(key, value);
  if (payload) {
    const eventPath = path.join(tempDir, 'event.json');
    fs.writeFileSync(eventPath, JSON.stringify(payload));
    vi.stubEnv('GITHUB_EVENT_PATH', eventPath);
  }
}

function pullRequest(headRepoId: number, baseRepoId: number) {
  return {
    pull_request: {
      number: 17,
      head: { sha: 'c'.repeat(40), ref: 'feature/x', repo: { id: headRepoId } },
      base: { sha: 'd'.repeat(40), ref: 'master', repo: { id: baseRepoId } },
    },
  };
}

function writeReport(relative: string, content: string): string {
  const full = path.join(tempDir, relative);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, content);
  return full;
}

/** Replaces the retry/poll sleeps with an immediate callback, recording each delay. */
function instantTimers(): number[] {
  const delays: number[] = [];
  vi.spyOn(globalThis, 'setTimeout').mockImplementation(((fn: () => void, ms?: number) => {
    delays.push(ms ?? 0);
    fn();
    return 0 as unknown as NodeJS.Timeout;
  }) as typeof setTimeout);
  return delays;
}

beforeEach(() => {
  tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'coverage-main-'));
  mocks.inputs.clear();
  mocks.outputs.clear();
  mocks.info.length = 0;
  mocks.warnings.length = 0;
  mocks.failed.length = 0;
  mocks.getIDToken.mockReset();
  mocks.getIDToken.mockImplementation(async () => 'oidc-id-token');
  mocks.getExecOutput.mockReset();
  mocks.getExecOutput.mockImplementation(async (_cmd: string, args: string[]) => {
    if (args[0] === 'ls-files') return { exitCode: 0, stdout: `100644 ${OID} 0\tsrc/app.ts\n`, stderr: '' };
    return { exitCode: 0, stdout: `${PARENT}\n`, stderr: '' };
  });

  calls = [];
  routes = {
    capabilities: [json({ contract: 1, features: ['partial-uploads', 'carry-forward'] })],
    upload: [json({ buildId: 'build-1', sessionId: 'session-1' }, 202)],
    finish: [json({}, 200)],
    status: [json({ buildId: 'build-1', state: 'InFlight', status: 'Pending' })],
  };
  vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
    const url = String(input);
    calls.push({
      url,
      method: init?.method ?? 'GET',
      headers: { ...((init?.headers as Record<string, string>) ?? {}) },
      body: init?.body,
    });
    const queue = routes[routeOf(url)];
    const next = queue.length > 1 ? queue.shift()! : queue[0];
    return next();
  }));

  // A push to master with an upload token: the plainest run there is. The empty
  // values keep whatever the real runner exported out of each case.
  setEvent({
    GITHUB_REPOSITORY: 'MintPlayer/Example',
    GITHUB_SHA: 'e'.repeat(40),
    GITHUB_RUN_ID: '42',
    GITHUB_RUN_ATTEMPT: '2',
    GITHUB_WORKFLOW: 'CI',
    GITHUB_JOB: 'test',
    GITHUB_EVENT_NAME: 'push',
    GITHUB_REF_NAME: 'master',
    GITHUB_WORKSPACE: tempDir,
    GITHUB_EVENT_PATH: '',
    GITHUB_HEAD_REF: '',
    GITHUB_BASE_REF: '',
    ACTIONS_ID_TOKEN_REQUEST_URL: '',
  });
  setInputs({ url: `${SERVER}/`, token: 'covt_test', 'fail-ci-if-error': 'true' });
  writeReport('coverage/lcov.info', `TN:\nSF:${path.join(tempDir, 'src', 'app.ts')}\nDA:1,1\nend_of_record\n`);
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  fs.rmSync(tempDir, { recursive: true, force: true });
});

describe('run: a push upload', () => {
  it('probes, uploads the rebased report with the run identity, and publishes the ids', async () => {
    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toEqual([]);

    // The trailing slash on `url` is stripped before any endpoint is built.
    const probe = callsTo('capabilities')[0];
    expect(probe.url).toBe(`${SERVER}/api/uploads/capabilities`);
    expect(probe.headers).toEqual({ Authorization: 'Bearer covt_test' });
    expect(mocks.outputs.get('server-contract')).toBe(1);

    const upload = callsTo('upload');
    expect(upload).toHaveLength(1);
    expect(upload[0].url).toBe(`${SERVER}/api/uploads`);
    expect(upload[0].method).toBe('POST');
    expect(upload[0].headers).toEqual({ Authorization: 'Bearer covt_test' });
    // No explicit Content-Type: fetch must write the multipart boundary itself.
    expect(upload[0].headers['Content-Type']).toBeUndefined();

    const form = uploadForm();
    expect(form.get('repository')).toBe('MintPlayer/Example');
    expect(form.get('commitSha')).toBe('e'.repeat(40));
    expect(form.get('branch')).toBe('master');
    expect(form.get('parentSha')).toBe(PARENT);
    expect(form.get('runId')).toBe('42');
    expect(form.get('runAttempt')).toBe('2');
    expect(form.get('jobName')).toBe('test');
    expect(form.get('workflow')).toBe('CI');
    expect(form.get('eventName')).toBe('push');
    expect(form.get('rootDir')).toBe(tempDir.replace(/\\/g, '/'));
    expect(form.get('fileList')).toBe(`${OID} src/app.ts`);
    // A push is not a pull request, and nothing optional was asked for.
    for (const absent of ['pullRequestNumber', 'baseRef', 'prBaseSha', 'flags', 'partial', 'carryForward', 'baseSha']) {
      expect(form.has(absent)).toBe(false);
    }

    const files = form.getAll('files');
    expect(files).toHaveLength(1);
    expect((files[0] as File).name).toBe('lcov.info.gz');
    // Rebased on the way out: the absolute native path became repository-relative.
    expect(await gunzipped(files[0])).toBe('TN:\nSF:src/app.ts\nDA:1,1\nend_of_record\n');
    expect(mocks.info).toContain('Rebased 1 report path(s) to repository-relative form.');
    expect(mocks.info).toContain('  coverage/lcov.info');

    expect(mocks.outputs.get('build-id')).toBe('build-1');
    expect(mocks.outputs.get('session-id')).toBe('session-1');

    // Not waiting still takes one look, with no sleep behind it.
    const peek = callsTo('status');
    expect(peek).toHaveLength(1);
    expect(peek[0].url).toBe(
      `${SERVER}/api/uploads/status?repository=MintPlayer%2FExample&commitSha=${'e'.repeat(40)}&runId=42&runAttempt=2`,
    );
    expect(callsTo('finish')).toHaveLength(0);
  });

  it('asks git for the tracked tree and the first parent from the workspace root', async () => {
    await run();

    const [lsFiles, revParse] = mocks.getExecOutput.mock.calls;
    expect(lsFiles.slice(0, 2)).toEqual(['git', ['ls-files', '-s']]);
    expect(lsFiles[2]).toMatchObject({ cwd: tempDir, silent: true });
    expect(revParse.slice(0, 2)).toEqual(['git', ['rev-parse', '--verify', '--quiet', `${'e'.repeat(40)}^1`]]);
    expect(revParse[2]).toMatchObject({ cwd: tempDir, ignoreReturnCode: true });
  });

  it('sends every optional input it was given', async () => {
    setInputs({ name: 'unit-tests', flags: 'frontend', partial: 'true', 'carry-forward': 'false', 'base-sha': 'f'.repeat(40) });

    await run();

    const form = uploadForm();
    expect(form.get('jobName')).toBe('unit-tests');
    expect(form.get('flags')).toBe('frontend');
    expect(form.get('partial')).toBe('true');
    // Only the explicit false travels; true is the server's default.
    expect(form.get('carryForward')).toBe('false');
    expect(form.get('baseSha')).toBe('f'.repeat(40));
    expect(mocks.info.some((m) => m.startsWith('carry-forward is off'))).toBe(true);
    expect(mocks.failed).toEqual([]);
  });

  it('leaves carryForward off the wire for anything but an explicit false', async () => {
    setInputs({ 'carry-forward': 'TRUE' });

    await run();

    expect(uploadForm().has('carryForward')).toBe(false);
  });

  it('uploads a format it cannot rebase byte for byte', async () => {
    fs.rmSync(path.join(tempDir, 'coverage'), { recursive: true });
    const raw = JSON.stringify({ [path.join(tempDir, 'src', 'app.ts')]: { s: { 0: 1 } } });
    writeReport('coverage/coverage-final.json', raw);

    await run();

    const [file] = uploadForm().getAll('files');
    expect((file as File).name).toBe('coverage-final.json.gz');
    expect(await gunzipped(file)).toBe(raw);
    expect(mocks.info.some((m) => m.startsWith('Rebased'))).toBe(false);
  });

  it('uses the explicit `files` input rather than searching', async () => {
    writeReport('other/custom.lcov', 'SF:src/b.ts\nend_of_record\n');
    setInputs({ files: path.join(tempDir, 'other', '*.lcov') });

    await run();

    const files = uploadForm().getAll('files');
    expect(files.map((f) => (f as File).name)).toEqual(['custom.lcov.gz']);
    // Already repository-relative: nothing to rewrite, and it says nothing about it.
    expect(await gunzipped(files[0])).toBe('SF:src/b.ts\nend_of_record\n');
    expect(mocks.info.some((m) => m.startsWith('Rebased'))).toBe(false);
  });
});

describe('run: pull request and fork context', () => {
  it('attaches a same-repository PR to its head, with its target branch and tip', async () => {
    setEvent({ GITHUB_EVENT_NAME: 'pull_request', GITHUB_HEAD_REF: 'feature/x', GITHUB_BASE_REF: 'master' }, pullRequest(1, 1));

    await run();

    const form = uploadForm();
    // Never the ephemeral merge commit in GITHUB_SHA.
    expect(form.get('commitSha')).toBe('c'.repeat(40));
    expect(form.get('branch')).toBe('feature/x');
    expect(form.get('pullRequestNumber')).toBe('17');
    expect(form.get('baseRef')).toBe('master');
    expect(form.get('prBaseSha')).toBe('d'.repeat(40));
    expect(form.get('eventName')).toBe('pull_request');
    expect(callsTo('upload')[0].headers).toEqual({ Authorization: 'Bearer covt_test' });
  });

  it('uploads a fork PR anonymously: no Authorization header on any request', async () => {
    mocks.inputs.delete('token');
    setEvent({ GITHUB_EVENT_NAME: 'pull_request', GITHUB_HEAD_REF: 'feature/x' }, pullRequest(2, 1));

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.info).toContain('Pull request from a fork: uploading anonymously against the pull request.');
    expect(calls.length).toBeGreaterThanOrEqual(3);
    for (const call of calls) expect(call.headers).not.toHaveProperty('Authorization');
    expect(mocks.outputs.get('build-id')).toBe('build-1');
  });

  it('lets an explicitly configured token win on a fork PR', async () => {
    setEvent({ GITHUB_EVENT_NAME: 'pull_request_target' }, pullRequest(2, 1));

    await run();

    expect(mocks.info.some((m) => m.startsWith('Pull request from a fork'))).toBe(false);
    expect(callsTo('upload')[0].headers).toEqual({ Authorization: 'Bearer covt_test' });
  });
});

describe('run: credentials', () => {
  it('mints an OIDC token for the server url when no token is configured and OIDC is offered', async () => {
    mocks.inputs.delete('token');
    vi.stubEnv('ACTIONS_ID_TOKEN_REQUEST_URL', 'https://token.example.test');

    await run();

    expect(mocks.info).toContain('Authenticating with GitHub Actions OIDC.');
    // The audience is the base url without its trailing slash: that is what the server validates.
    expect(mocks.getIDToken).toHaveBeenCalledWith(SERVER);
    expect(callsTo('upload')[0].headers).toEqual({ Authorization: 'Bearer oidc-id-token' });
  });

  it('fails before contacting the server when use-oidc is set but OIDC is unavailable', async () => {
    setInputs({ 'use-oidc': 'true' });

    await run();

    expect(mocks.failed).toHaveLength(1);
    expect(mocks.failed[0]).toMatch(/use-oidc requires `permissions: id-token: write`/);
    expect(calls).toEqual([]);
  });

  it('only warns, without failing CI, when there is no credential and fail-ci-if-error is off', async () => {
    mocks.inputs.delete('token');
    mocks.inputs.delete('fail-ci-if-error');

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toHaveLength(1);
    expect(mocks.warnings[0]).toMatch(/^Coverage upload failed \(not failing CI\): No `token` given and OIDC is unavailable/);
    expect(calls).toEqual([]);
  });

  it('fails when the url input is missing', async () => {
    mocks.inputs.delete('url');

    await run();

    expect(mocks.failed).toEqual(['Input required and not supplied: url']);
    expect(calls).toEqual([]);
  });
});

describe('run: capabilities', () => {
  it('uploads anyway against a server that predates the probe, and reports contract 0', async () => {
    routes.capabilities = [text('not found', 404)];

    await run();

    expect(mocks.outputs.get('server-contract')).toBe(0);
    expect(mocks.info).toContain('Server does not advertise an upload contract (pre-capabilities image); uploading anyway.');
    expect(callsTo('upload')).toHaveLength(1);
    expect(mocks.failed).toEqual([]);
  });

  it('warns when partial is asked of a server that cannot do it, and still uploads', async () => {
    routes.capabilities = [json({ contract: 1, features: [] })];
    setInputs({ partial: 'true' });

    await run();

    expect(mocks.warnings.some((w) => w.includes('does not support partial uploads'))).toBe(true);
    expect(uploadForm().get('partial')).toBe('true');
  });
});

describe('run: no coverage files', () => {
  beforeEach(() => fs.rmSync(path.join(tempDir, 'coverage'), { recursive: true }));

  it('fails a whole upload that found nothing, without uploading', async () => {
    await run();

    expect(mocks.failed).toHaveLength(1);
    expect(mocks.failed[0]).toMatch(/^No coverage report files found\. Pass `files:` explicitly/);
    expect(callsTo('upload')).toHaveLength(0);
  });

  it('does not search at all when disable-search is set and no files were named', async () => {
    // The report is there, but only a search would find it.
    writeReport('coverage/lcov.info', 'SF:src/app.ts\nend_of_record\n');
    setInputs({ 'disable-search': 'true' });

    await run();

    expect(mocks.failed[0]).toMatch(/^No coverage report files found/);
  });

  it('uploads the file list alone for a partial run, so the server can carry coverage forward', async () => {
    setInputs({ partial: 'true' });

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.info.some((m) => m.startsWith('No coverage report files found; uploading the file list only'))).toBe(true);
    const form = uploadForm();
    expect(form.getAll('files')).toEqual([]);
    expect(form.get('fileList')).toBe(`${OID} src/app.ts`);
    expect(form.get('partial')).toBe('true');
  });

  it('fails a partial run with neither reports nor a file list, since there is nothing to carry', async () => {
    setInputs({ partial: 'true' });
    mocks.getExecOutput.mockImplementation(async () => ({ exitCode: 128, stdout: '', stderr: 'not a git repository' }));

    await run();

    expect(mocks.failed).toEqual([
      'No coverage report files found and `git ls-files` failed, so there is nothing the server could carry forward.',
    ]);
    expect(callsTo('upload')).toHaveLength(0);
  });
});

describe('run: git is best effort when there are reports', () => {
  it('uploads without a file list or parent when git cannot run at all', async () => {
    mocks.getExecOutput.mockImplementation(async () => {
      throw new Error('spawn git ENOENT');
    });

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toContain('git ls-files failed — path matching on the server will be best-effort.');
    const form = uploadForm();
    expect(form.has('fileList')).toBe(false);
    expect(form.has('parentSha')).toBe(false);
  });

  it.each([
    ['a shallow checkout (non-zero exit)', { exitCode: 1, stdout: '' }],
    ['output that is not a sha', { exitCode: 0, stdout: 'HEAD^1\n' }],
  ])('sends no parentSha for %s', async (_label, revParse) => {
    mocks.getExecOutput.mockImplementation(async (_cmd: string, args: string[]) =>
      args[0] === 'ls-files' ? { exitCode: 0, stdout: `100644 ${OID} 0\tsrc/app.ts\n` } : { ...revParse, stderr: '' },
    );

    await run();

    expect(uploadForm().has('parentSha')).toBe(false);
    expect(uploadForm().get('fileList')).toBe(`${OID} src/app.ts`);
  });
});

describe('run: upload failures and retries', () => {
  it('does not retry a 4xx, and reports the server body', async () => {
    const delays = instantTimers();
    routes.upload = [text('repository not registered', 403)];

    await run();

    expect(callsTo('upload')).toHaveLength(1);
    expect(delays).toEqual([]);
    expect(mocks.failed).toEqual([`${SERVER}/api/uploads responded 403: repository not registered`]);
    expect(mocks.outputs.has('build-id')).toBe(false);
  });

  it('retries a 5xx with a growing delay and succeeds on the third attempt', async () => {
    const delays = instantTimers();
    routes.upload = [text('', 502), text('', 503), json({ buildId: 'build-3', sessionId: 'session-3' }, 202)];

    await run();

    expect(callsTo('upload')).toHaveLength(3);
    expect(delays).toEqual([5000, 10000]);
    expect(mocks.info).toContain('Upload attempt 1 failed, retrying in 5s...');
    expect(mocks.info).toContain('Upload attempt 2 failed, retrying in 10s...');
    expect(mocks.outputs.get('build-id')).toBe('build-3');
    expect(mocks.failed).toEqual([]);
  });

  it('retries a 429 like a 5xx rather than giving up', async () => {
    instantTimers();
    routes.upload = [text('slow down', 429), json({ buildId: 'build-2', sessionId: 'session-2' }, 202)];

    await run();

    expect(callsTo('upload')).toHaveLength(2);
    expect(mocks.outputs.get('build-id')).toBe('build-2');
  });

  it('fails with the last status after three 5xx attempts', async () => {
    const delays = instantTimers();
    routes.upload = [text('', 500)];

    await run();

    expect(callsTo('upload')).toHaveLength(3);
    // No sleep after the final attempt.
    expect(delays).toEqual([5000, 10000]);
    expect(mocks.failed).toEqual([`${SERVER}/api/uploads responded 500`]);
  });

  it('retries a network failure and reports its message', async () => {
    instantTimers();
    routes.upload = [() => Promise.reject(new TypeError('fetch failed'))];

    await run();

    expect(callsTo('upload')).toHaveLength(3);
    expect(mocks.failed).toEqual(['fetch failed']);
  });

  it('reports a failed upload as a warning when fail-ci-if-error is off', async () => {
    mocks.inputs.set('fail-ci-if-error', 'false');
    routes.upload = [text('bad form', 400)];

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toEqual([
      `Coverage upload failed (not failing CI): ${SERVER}/api/uploads responded 400: bad form`,
    ]);
  });
});

describe('run: finish', () => {
  it('posts the run identity as JSON to the finish endpoint', async () => {
    setInputs({ finish: 'true' });

    await run();

    const [finish] = callsTo('finish');
    expect(finish.url).toBe(`${SERVER}/api/uploads/finish`);
    expect(finish.method).toBe('POST');
    expect(finish.headers).toEqual({ Authorization: 'Bearer covt_test', 'Content-Type': 'application/json' });
    expect(JSON.parse(finish.body as string)).toEqual({
      repository: 'MintPlayer/Example',
      commitSha: 'e'.repeat(40),
      runId: 42,
      runAttempt: 2,
    });
    expect(mocks.info).toContain('Finish requested (200) — the build finalizes once parsing completes.');
  });

  it('fails on a finish the server refuses', async () => {
    setInputs({ finish: 'true' });
    routes.finish = [text('no such run', 404)];

    await run();

    expect(mocks.failed).toEqual([`${SERVER}/api/uploads/finish responded 404: no such run`]);
  });
});

describe('run: the single status peek when not waiting', () => {
  it('warns, but never fails, when the build already finalized with errors', async () => {
    routes.status = [json({
      buildId: 'build-1',
      state: 'CompleteWithErrors',
      status: 'Failed',
      ingest: { reportsAccepted: 0, reportsRejected: 1, rejected: [{ fileName: 'lcov.info', reason: 'malformed', detail: 'BOM' }] },
    })];

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toContain("Coverage report 'lcov.info' was rejected (malformed): BOM");
    const verdict = mocks.warnings.find((w) => w.startsWith('The coverage build finalized with errors'));
    expect(verdict).toContain('lcov.info: malformed (BOM)');
    expect(verdict).toContain('Set `wait-for-finalize: true`');
  });

  it('swallows a status endpoint that errors', async () => {
    routes.status = [text('boom', 500)];

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toEqual([]);
    expect(mocks.outputs.get('build-id')).toBe('build-1');
  });
});

describe('run: wait-for-finalize', () => {
  const complete = {
    buildId: 'build-1',
    state: 'Complete',
    status: 'Passed',
    commitUrl: `${SERVER}/r/MintPlayer/Example/commit/eee`,
    coverage: { linesCovered: 50, linesCoverable: 200, branchesCovered: 1, branchesTotal: 4, filesCount: 3 },
    assembly: {
      coverage: { linesCovered: 150, linesCoverable: 200, branchesCovered: 0, branchesTotal: 0, filesCount: 9 },
      completeness: 'Complete',
      incompleteReasons: [],
      measuredFiles: 3,
      carriedFiles: 6,
      unmeasuredFiles: 0,
      builds: ['build-1'],
    },
  };

  it('polls until terminal and publishes the result as outputs', async () => {
    const delays = instantTimers();
    setInputs({ 'wait-for-finalize': 'true', 'wait-poll-interval': '2' });
    routes.status = [json({ buildId: 'build-1', state: 'InFlight', status: 'Pending' }), json(complete)];

    await run();

    expect(mocks.failed).toEqual([]);
    expect(callsTo('status')).toHaveLength(2);
    expect(delays).toEqual([2000]);
    expect(mocks.info).toContain('Waiting up to 1800s for the build to finalize...');
    expect(mocks.info).toContain('Build Complete: 50/200 lines (25.0%)');
    expect(mocks.info).toContain('Commit eeeeeee assembled: 150/200 lines (75.0%) — 3 measured, 6 carried, 0 unmeasured.');
    expect(mocks.info).toContain(complete.commitUrl);
    expect(mocks.outputs.get('state')).toBe('Complete');
    expect(mocks.outputs.get('line-rate')).toBe('25.0');
    expect(mocks.outputs.get('assembly-line-rate')).toBe('75.0');
    expect(mocks.warnings).toEqual([]);
  });

  it('warns that an incomplete assembly under-counts, rather than reading as a drop', async () => {
    setInputs({ 'wait-for-finalize': 'true' });
    routes.status = [json({ ...complete, assembly: { ...complete.assembly, completeness: 'Partial', incompleteReasons: ['noBase'] } })];

    await run();

    expect(mocks.failed).toEqual([]);
    expect(mocks.warnings).toEqual([
      'The assembled coverage for this commit is Partial (noBase), so the reported percentage under-counts. It is not a coverage drop.',
    ]);
  });

  it('says "no coverage data" when the server sent none', async () => {
    setInputs({ 'wait-for-finalize': 'true' });
    routes.status = [json({ buildId: 'build-1', state: 'Complete', status: 'Passed' })];

    await run();

    expect(mocks.info).toContain('Build Complete: no coverage data');
    expect(mocks.outputs.get('files-count')).toBe(0);
  });

  it('fails a build that finalized with errors, naming the failed sessions', async () => {
    setInputs({ 'wait-for-finalize': 'true' });
    routes.status = [json({
      buildId: 'build-1',
      state: 'CompleteWithErrors',
      status: 'Failed',
      sessions: [
        { sessionId: 's1', jobName: 'unit', parseStatus: 'Parsed' },
        { sessionId: 's2', jobName: null, parseStatus: 'Failed', error: 'unreadable' },
      ],
    })];

    await run();

    // Outputs are still set from what the server did say.
    expect(mocks.outputs.get('state')).toBe('CompleteWithErrors');
    expect(mocks.failed).toEqual(['The build finalized with errors, so the coverage number under-counts. s2: unreadable']);
  });

  it('fails when none of the uploaded coverage matched the repository', async () => {
    setInputs({ 'wait-for-finalize': 'true' });
    routes.status = [json({ ...complete, unmatched: { files: 3, totalFiles: 3, sample: ['/abs/a.ts'] } })];

    await run();

    expect(mocks.outputs.get('files-unmatched')).toBe(3);
    expect(mocks.failed).toHaveLength(1);
    expect(mocks.failed[0]).toMatch(/^None of the uploaded coverage resolved to files in this repository \(3 of 3 file\(s\)\)/);
  });

  it('rejects a wait-timeout that is not a positive number, after the upload', async () => {
    setInputs({ 'wait-for-finalize': 'true', 'wait-timeout': 'soon' });

    await run();

    expect(callsTo('upload')).toHaveLength(1);
    expect(callsTo('status')).toHaveLength(0);
    expect(mocks.failed).toEqual(['`wait-timeout` must be a positive number of seconds, got "soon".']);
  });
});
