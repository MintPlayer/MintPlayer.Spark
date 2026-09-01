import * as core from '@actions/core';
import * as exec from '@actions/exec';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { fetchCapabilities, warnAboutUnsupportedInputs } from './capabilities';
import { collectContext } from './context';
import { Credential, oidcCredential, staticCredential } from './credential';
import { findCoverageFiles } from './files';
import { UploadStatus, rate, waitForFinalize } from './status';

const MAX_RETRIES = 3;

// getBooleanInput throws on empty input; action.yml defaults cover GitHub
// runs, but be robust for local/other runners: absent means false.
function getBool(name: string): boolean {
  return core.getInput(name).trim().toLowerCase() === 'true';
}

export async function run(): Promise<void> {
  const failCiIfError = getBool('fail-ci-if-error');

  try {
    const url = core.getInput('url', { required: true }).replace(/\/+$/, '');
    const credential = resolveCredential(url);
    const ctx = collectContext();

    // Probed before anything is sent, so an input this server will ignore is
    // reported next to the upload it affects rather than after it.
    const capabilities = await fetchCapabilities(url, credential);
    core.setOutput('server-contract', capabilities.contract);
    warnAboutUnsupportedInputs(capabilities, { partial: getBool('partial') });

    const files = await findCoverageFiles(
      core.getInput('files') || undefined,
      core.getInput('directory') || undefined,
      ctx.rootDir,
      getBool('disable-search'),
    );

    if (files.length === 0) {
      throw new Error(
        'No coverage report files found. Pass `files:` explicitly or check that your test step writes lcov/cobertura output.',
      );
    }
    core.info(`Uploading ${files.length} coverage file(s) for ${ctx.repository}@${ctx.commitSha}:`);
    for (const file of files) {
      core.info(`  ${path.relative(ctx.rootDir, file)}`);
    }

    const fileList = await gitLsFiles(ctx.rootDir);

    const form = new FormData();
    form.set('repository', ctx.repository);
    form.set('commitSha', ctx.commitSha);
    if (ctx.branch) form.set('branch', ctx.branch);
    if (ctx.pullRequestNumber) form.set('pullRequestNumber', String(ctx.pullRequestNumber));
    if (ctx.parentSha) form.set('parentSha', ctx.parentSha);
    form.set('runId', String(ctx.runId));
    form.set('runAttempt', String(ctx.runAttempt));
    form.set('jobName', core.getInput('name') || ctx.jobName);
    form.set('workflow', ctx.workflow);
    form.set('eventName', ctx.eventName);
    const flags = core.getInput('flags');
    if (flags) form.set('flags', flags);
    if (getBool('partial')) form.set('partial', 'true');
    const baseSha = core.getInput('base-sha');
    if (baseSha) form.set('baseSha', baseSha);
    form.set('rootDir', ctx.rootDir);
    if (fileList) form.set('fileList', fileList);

    for (const file of files) {
      // Server ungzips by magic bytes; gzip keeps multi-MB lcov payloads small.
      const gzipped = zlib.gzipSync(fs.readFileSync(file));
      form.append('files', new Blob([new Uint8Array(gzipped)]), path.basename(file) + '.gz');
    }

    const response = await postWithRetry(`${url}/api/uploads`, credential, form);
    const body = (await response.json()) as { buildId: string; sessionId: string };
    core.info(`Upload accepted: build ${body.buildId}, session ${body.sessionId}`);
    core.setOutput('build-id', body.buildId);
    core.setOutput('session-id', body.sessionId);

    if (getBool('finish')) {
      const finish = await postWithRetry(
        `${url}/api/uploads/finish`,
        credential,
        JSON.stringify({
          repository: ctx.repository,
          commitSha: ctx.commitSha,
          runId: ctx.runId,
          runAttempt: ctx.runAttempt,
        }),
        'application/json',
      );
      core.info(`Finish requested (${finish.status}) — the build finalizes once parsing completes.`);
    }

    if (getBool('wait-for-finalize')) {
      await waitAndReport(url, credential, ctx);
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (failCiIfError) {
      core.setFailed(message);
    } else {
      core.warning(`Coverage upload failed (not failing CI): ${message}`);
    }
  }
}

/**
 * Prefers OIDC when asked (use-oidc) or when no token is configured and the
 * runtime offers it. The id-token's audience must equal the server's base URL —
 * that's what the server validates.
 *
 * Returns a credential rather than a token because an OIDC id-token lives five
 * minutes and `wait-for-finalize` can run for thirty; see credential.ts.
 */
function resolveCredential(url: string): Credential {
  const token = core.getInput('token');
  const oidcAvailable = !!process.env['ACTIONS_ID_TOKEN_REQUEST_URL'];

  if (getBool('use-oidc') || (!token && oidcAvailable)) {
    if (!oidcAvailable) {
      throw new Error(
        'use-oidc requires `permissions: id-token: write` (and OIDC is never available to pull requests from forks).',
      );
    }
    core.info('Authenticating with GitHub Actions OIDC.');
    return oidcCredential(url);
  }

  if (!token) {
    throw new Error(
      'No `token` given and OIDC is unavailable. Pass an upload token, or grant `permissions: id-token: write` for tokenless uploads.',
    );
  }
  return staticCredential(token);
}

/**
 * Waits for the build to finalize and publishes the result as step outputs, so
 * a workflow can gate on `steps.<id>.outputs.line-rate` without writing any
 * polling of its own.
 *
 * A timeout or a `CompleteWithErrors` is reported through the existing
 * `fail-ci-if-error` input rather than a second knob: both mean "you may not
 * have the number you think you have", which is the same judgement that input
 * already encodes. Outputs are still set from whatever the server did say.
 */
async function waitAndReport(url: string, credential: Credential, ctx: ReturnType<typeof collectContext>): Promise<void> {
  const timeoutSeconds = numberInput('wait-timeout', 1800);
  const pollIntervalSeconds = numberInput('wait-poll-interval', 5);

  core.info(`Waiting up to ${timeoutSeconds}s for the build to finalize...`);

  // A timeout propagates to run()'s single catch, so fail-ci-if-error decides
  // what it means in one place rather than two.
  const status = await waitForFinalize(credential, {
    url,
    repository: ctx.repository,
    commitSha: ctx.commitSha,
    runId: ctx.runId,
    runAttempt: ctx.runAttempt,
    timeoutSeconds,
    pollIntervalSeconds,
  });

  setResultOutputs(status);

  const summary = status.coverage
    ? `${status.coverage.linesCovered}/${status.coverage.linesCoverable} lines (${rate(status.coverage.linesCovered, status.coverage.linesCoverable)}%)`
    : 'no coverage data';
  core.info(`Build ${status.state}: ${summary}`);
  if (status.commitUrl) core.info(status.commitUrl);

  if (status.state === 'CompleteWithErrors') {
    const failures = (status.sessions ?? [])
      .filter((s) => s.parseStatus !== 'Parsed')
      .map((s) => `${s.jobName || s.sessionId}: ${s.error ?? s.parseStatus}`);
    throw new Error(
      `The build finalized with errors, so the coverage number under-counts. ${failures.join('; ')}`.trim(),
    );
  }
}

function setResultOutputs(status: UploadStatus): void {
  core.setOutput('state', status.state);
  core.setOutput('build-status', status.status);
  core.setOutput('finalize-reason', status.finalizeReason ?? '');
  core.setOutput('commit-url', status.commitUrl ?? '');

  const coverage = status.coverage;
  core.setOutput('lines-covered', coverage?.linesCovered ?? '');
  core.setOutput('lines-coverable', coverage?.linesCoverable ?? '');
  core.setOutput('line-rate', rate(coverage?.linesCovered, coverage?.linesCoverable));
  core.setOutput('branches-covered', coverage?.branchesCovered ?? '');
  core.setOutput('branches-total', coverage?.branchesTotal ?? '');
  core.setOutput('branch-rate', rate(coverage?.branchesCovered, coverage?.branchesTotal));
  core.setOutput('files-count', coverage?.filesCount ?? '');

  // Empty on a first upload, where a ratchet has nothing to compare against and
  // must pass by definition.
  const baseline = status.baseline;
  core.setOutput('baseline-sha', baseline?.sha ?? '');
  core.setOutput('baseline-lines-covered', baseline?.coverage?.linesCovered ?? '');
  core.setOutput('baseline-lines-coverable', baseline?.coverage?.linesCoverable ?? '');
  core.setOutput('baseline-line-rate', rate(baseline?.coverage?.linesCovered, baseline?.coverage?.linesCoverable));

  // Partial-upload surfaces. All empty on whole uploads.
  const scope = status.baselineScope;
  core.setOutput('base-resolution', scope?.baseResolution ?? '');
  core.setOutput('resolved-base-sha', scope?.resolvedBaseSha ?? '');

  const projection = status.projection;
  core.setOutput('projection-line-rate', rate(projection?.coverage?.linesCovered, projection?.coverage?.linesCoverable));
  core.setOutput('projection-complete', projection ? String(projection.complete) : '');
  core.setOutput('projection-incomplete-reasons', projection?.incompleteReasons?.join(',') ?? '');

  // Patch coverage. Empty when no diff base was available; a gate that wants
  // to require it should treat empty as "abstain", not as zero.
  const patch = status.patch;
  core.setOutput('patch-lines-covered', patch?.linesCovered ?? '');
  core.setOutput('patch-lines-coverable', patch?.linesCoverable ?? '');
  core.setOutput('patch-rate', rate(patch?.linesCovered, patch?.linesCoverable));
  core.setOutput('patch-diff-truncated', patch ? String(patch.diffTruncated) : '');
}

function numberInput(name: string, fallback: number): number {
  const raw = core.getInput(name).trim();
  if (!raw) return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error(`\`${name}\` must be a positive number of seconds, got "${raw}".`);
  }
  return value;
}

async function gitLsFiles(cwd: string): Promise<string | null> {
  try {
    const output = await exec.getExecOutput('git', ['ls-files'], { cwd, silent: true });
    return output.exitCode === 0 ? output.stdout : null;
  } catch {
    core.warning('git ls-files failed — path matching on the server will be best-effort.');
    return null;
  }
}

async function postWithRetry(
  url: string,
  credential: Credential,
  body: FormData | string,
  contentType?: string,
): Promise<Response> {
  let lastError: unknown;
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    try {
      const headers: Record<string, string> = { Authorization: `Bearer ${await credential.get()}` };
      if (contentType) headers['Content-Type'] = contentType;
      const response = await fetch(url, { method: 'POST', headers, body });
      if (response.ok) return response;

      // 4xx (except 429) won't get better by retrying.
      if (response.status < 500 && response.status !== 429) {
        throw new Error(`${url} responded ${response.status}: ${await safeText(response)}`);
      }
      lastError = new Error(`${url} responded ${response.status}`);
    } catch (error) {
      if (error instanceof Error && error.message.includes('responded 4')) throw error;
      lastError = error;
    }
    if (attempt < MAX_RETRIES) {
      const delaySeconds = attempt * 5;
      core.info(`Upload attempt ${attempt} failed, retrying in ${delaySeconds}s...`);
      await new Promise((resolve) => setTimeout(resolve, delaySeconds * 1000));
    }
  }
  throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

async function safeText(response: Response): Promise<string> {
  try {
    return (await response.text()).slice(0, 500);
  } catch {
    return '';
  }
}
