import { spawn } from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as os from 'os';
import * as path from 'path';
import * as zlib from 'zlib';

/**
 * Runs the **committed bundle** — `dist/index.js`, the thing `runs.main` actually
 * executes — against a real HTTP server speaking the documented upload contract.
 *
 * The unit tests import `src/`, so every one of them passes against a bundle that
 * was never rebuilt, or built from the wrong entry point. Bundling `main.ts`
 * instead of `index.ts` produces a file that defines `run` and never calls it: an
 * action that exits 0 having uploaded nothing, green in CI, silent in production.
 * That failure is invisible to every other test in this folder.
 *
 * Requires `npm run build` first, which is why it lives behind its own config
 * rather than in the default `npm test` run.
 */

interface Capture {
  method: string;
  url: string;
  headers: http.IncomingHttpHeaders;
  body: Buffer;
}

interface Stub {
  port: number;
  captures: Capture[];
  close: () => Promise<void>;
}

/** @param capabilities `null` serves 404 — the pre-capabilities image. */
async function startStub(capabilities: { contract: number; features: string[] } | null): Promise<Stub> {
  const captures: Capture[] = [];

  const server = http.createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on('data', (chunk: Buffer) => chunks.push(chunk));
    request.on('end', () => {
      captures.push({
        method: request.method ?? '',
        url: request.url ?? '',
        headers: request.headers,
        body: Buffer.concat(chunks),
      });

      const url = (request.url ?? '').split('?')[0];

      if (url === '/api/uploads/capabilities') {
        if (!capabilities) {
          response.writeHead(404).end();
          return;
        }
        response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(capabilities));
        return;
      }
      if (url === '/api/uploads' && request.method === 'POST') {
        response
          .writeHead(202, { 'content-type': 'application/json' })
          .end(JSON.stringify({ buildId: 'build-99', sessionId: 'session-99' }));
        return;
      }
      if (url === '/api/uploads/finish' && request.method === 'POST') {
        response.writeHead(202, { 'content-type': 'application/json' }).end(JSON.stringify({ status: 'Finalizing' }));
        return;
      }
      response.writeHead(404).end();
    });
  });

  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (address === null || typeof address === 'string') throw new Error('stub server did not bind a port');

  return {
    port: address.port,
    captures,
    close: () => new Promise<void>((resolve, reject) => server.close((error) => (error ? reject(error) : resolve()))),
  };
}

interface RunResult {
  exitCode: number | null;
  stdout: string;
  /** Whatever the action wrote to $GITHUB_OUTPUT, verbatim. */
  outputs: string;
}

/** Runs the bundle the way a runner does: inputs and context through the environment. */
async function runBundle(port: number, inputs: Record<string, string>): Promise<RunResult> {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'coverage-bundle-'));
  fs.mkdirSync(path.join(workspace, 'coverage'), { recursive: true });
  fs.writeFileSync(path.join(workspace, 'coverage', 'lcov.info'), 'TN:\nSF:src/app.ts\nDA:1,1\nend_of_record\n');
  // @actions/core appends to $GITHUB_OUTPUT and requires it to already exist —
  // without this the very first setOutput throws "Missing file at path", before
  // anything is uploaded.
  const outputFile = path.join(workspace, 'outputs.txt');
  fs.writeFileSync(outputFile, '');

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    GITHUB_WORKSPACE: workspace,
    GITHUB_REPOSITORY: 'MintPlayer/MintPlayer.Spark',
    GITHUB_SHA: '1111111111111111111111111111111111111111',
    GITHUB_RUN_ID: '4242',
    GITHUB_RUN_ATTEMPT: '2',
    GITHUB_WORKFLOW: 'pull-request',
    GITHUB_JOB: 'bundle-smoke',
    GITHUB_EVENT_NAME: 'push',
    GITHUB_REF_NAME: 'master',
    GITHUB_OUTPUT: outputFile,
    GITHUB_EVENT_PATH: '',
    GITHUB_HEAD_REF: '',
    // Must not leak in from the real runner: it would send the action down the
    // OIDC path, where `getIDToken` fails against a URL that isn't GitHub's.
    ACTIONS_ID_TOKEN_REQUEST_URL: '',
    INPUT_URL: `http://127.0.0.1:${port}`,
    ...Object.fromEntries(Object.entries(inputs).map(([key, value]) => [`INPUT_${key.toUpperCase()}`, value])),
  };

  return await new Promise<RunResult>((resolve, reject) => {
    const child = spawn(process.execPath, [path.join(__dirname, '..', 'dist', 'index.js')], { env });
    let stdout = '';
    child.stdout.on('data', (chunk: Buffer) => (stdout += chunk.toString()));
    child.stderr.on('data', (chunk: Buffer) => (stdout += chunk.toString()));
    child.on('error', reject);
    child.on('close', (exitCode) => {
      const outputs = fs.readFileSync(outputFile, 'utf8');
      fs.rmSync(workspace, { recursive: true, force: true });
      resolve({ exitCode, stdout, outputs });
    });
  });
}

/**
 * The raw bytes of the uploaded report part.
 *
 * Scanning for gzip magic bytes finds a byte pair, not necessarily a member
 * start, so the part is located the way a server does it: by its own headers and
 * the closing boundary.
 */
function filePart(body: Buffer, boundary: string): Buffer {
  const marker = Buffer.from(`filename="lcov.info.gz"`);
  const markerAt = body.indexOf(marker);
  if (markerAt < 0) throw new Error('no report part in the upload');

  const headerEnd = body.indexOf(Buffer.from('\r\n\r\n'), markerAt);
  const start = headerEnd + 4;
  const end = body.indexOf(Buffer.from(`\r\n--${boundary}`), start);
  return body.subarray(start, end < 0 ? undefined : end);
}

describe('the committed bundle', () => {
  let stub: Stub;

  afterEach(async () => {
    await stub.close();
  });

  // The regression that matters most: a bundle built from the wrong entry point
  // exits 0 and makes no requests at all, which every src-level test misses.
  it('actually runs, and uploads to the documented endpoint', async () => {
    stub = await startStub({ contract: 1, features: ['partial-uploads'] });

    const result = await runBundle(stub.port, {
      token: 'covt_smoke',
      'disable-search': 'false',
      'fail-ci-if-error': 'true',
    });

    const upload = stub.captures.find((c) => c.method === 'POST' && c.url === '/api/uploads');
    expect(upload, `no upload arrived. Output:\n${result.stdout}`).toBeDefined();
    expect(result.exitCode, result.stdout).toBe(0);
    expect(upload!.headers.authorization).toBe('Bearer covt_smoke');
    expect(upload!.headers['content-type']).toMatch(/^multipart\/form-data; boundary=/);
  });

  it('probes capabilities before uploading', async () => {
    stub = await startStub({ contract: 1, features: ['partial-uploads'] });

    await runBundle(stub.port, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    expect(stub.captures[0].method).toBe('GET');
    expect(stub.captures[0].url).toBe('/api/uploads/capabilities');
  });

  // A server that predates the endpoint must be a normal, quiet success.
  it('uploads anyway when the server has no capabilities endpoint', async () => {
    stub = await startStub(null);

    const result = await runBundle(stub.port, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    expect(result.exitCode).toBe(0);
    expect(stub.captures.some((c) => c.method === 'POST' && c.url === '/api/uploads')).toBe(true);
    expect(result.stdout).toMatch(/does not advertise an upload contract/);
  });

  // An input the server cannot honour is dropped by model binding, not rejected —
  // so the only way a caller learns is if the action says so.
  it('warns that partial will be ignored by a server that cannot do it', async () => {
    stub = await startStub({ contract: 1, features: [] });

    const result = await runBundle(stub.port, {
      token: 'covt_smoke',
      partial: 'true',
      'base-sha': '2222222222222222222222222222222222222222',
      'fail-ci-if-error': 'true',
    });

    expect(result.exitCode).toBe(0);
    expect(result.stdout).toMatch(/::warning::.*partial uploads/i);
  });

  it('gzips each report and carries the run identity', async () => {
    stub = await startStub({ contract: 1, features: ['partial-uploads'] });

    await runBundle(stub.port, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    const upload = stub.captures.find((c) => c.method === 'POST' && c.url === '/api/uploads')!;
    const body = upload.body;
    // The gzip magic bytes must appear inside the multipart payload; the server
    // ungzips by sniffing them.
    expect(body.includes(Buffer.from([0x1f, 0x8b]))).toBe(true);
    expect(body.toString('latin1')).toContain('filename="lcov.info.gz"');

    const text = body.toString('latin1');
    expect(text).toContain('MintPlayer/MintPlayer.Spark');
    expect(text).toContain('4242');
    // A re-run must not merge into the first attempt's build.
    expect(text).toMatch(/name="runAttempt"\r\n\r\n2/);
  });

  it('calls finish when asked to', async () => {
    stub = await startStub({ contract: 1, features: ['partial-uploads'] });

    await runBundle(stub.port, { token: 'covt_smoke', finish: 'true', 'fail-ci-if-error': 'true' });

    const finish = stub.captures.find((c) => c.url === '/api/uploads/finish');
    expect(finish).toBeDefined();
    expect(finish!.headers['content-type']).toBe('application/json');
    expect(JSON.parse(finish!.body.toString())).toMatchObject({
      repository: 'MintPlayer/MintPlayer.Spark',
      runId: 4242,
      runAttempt: 2,
    });
  });

  // fail-ci-if-error is the only knob that decides what an upload failure means,
  // and the default must not turn a coverage hiccup into a red build.
  it('does not fail the step when the server rejects and fail-ci-if-error is false', async () => {
    stub = await startStub({ contract: 1, features: [] });
    await stub.close();
    // A port with nothing listening: the upload cannot succeed.
    const dead = stub.port;
    stub = { port: dead, captures: [], close: async () => {} };

    const result = await runBundle(dead, { token: 'covt_smoke', 'fail-ci-if-error': 'false' });

    expect(result.exitCode).toBe(0);
    expect(result.stdout).toMatch(/::warning::/);
  });

  it('fails the step on an unreachable server when told to', async () => {
    stub = await startStub({ contract: 1, features: [] });
    await stub.close();
    const dead = stub.port;
    stub = { port: dead, captures: [], close: async () => {} };

    const result = await runBundle(dead, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    expect(result.exitCode).not.toBe(0);
    expect(result.stdout).toMatch(/::error::/);
  });

  it('reports the server contract as an output', async () => {
    stub = await startStub({ contract: 1, features: ['partial-uploads'] });

    const result = await runBundle(stub.port, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    expect(result.exitCode, result.stdout).toBe(0);
    // @actions/core writes `name<<delimiter`, the value, then the delimiter again.
    expect(result.outputs).toMatch(/server-contract<<.*\r?\n1\r?\n/);
    expect(result.outputs).toMatch(/build-id<<.*\r?\nbuild-99\r?\n/);
  });

  it('gzip round-trips to the original report', async () => {
    stub = await startStub({ contract: 1, features: [] });

    await runBundle(stub.port, { token: 'covt_smoke', 'fail-ci-if-error': 'true' });

    const upload = stub.captures.find((c) => c.method === 'POST' && c.url === '/api/uploads')!;
    const boundary = /boundary=(.+)$/.exec(String(upload.headers['content-type']))![1];
    const inflated = zlib.gunzipSync(filePart(upload.body, boundary));

    expect(inflated.toString()).toContain('SF:src/app.ts');
  });
});
