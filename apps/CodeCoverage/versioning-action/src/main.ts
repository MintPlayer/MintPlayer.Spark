import * as core from '@actions/core';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parse as parseYaml } from 'yaml';
import { collectManifest, defaultEngineProject, runLocal, type EngineOptions, type Release, type RunResponse } from './engine';
import { realExec, type Exec } from './exec';
import { checkBranchName, prepareHistory, showOnDefaultBranch, taggedVersions } from './git';
import { discoverPackages, writeVersions } from './npm';
import { distTag, highest } from './semver';
import { runOnServer } from './server';

export interface Inputs {
  mode: string;
  url: string;
  configuration: string;
  workspace: string;
  token: string;
  registryFallback: boolean;
  runnerTemp: string;
  ref: string;
  eventName: string;
  eventPath: string;
}

export function readInputs(env: NodeJS.ProcessEnv = process.env): Inputs {
  const get = (name: string) => (env[`INPUT_${name.replace(/ /g, '_').toUpperCase()}`] ?? '').trim();
  return {
    mode: get('mode'),
    url: get('url') || 'https://coverage.mintplayer.com',
    configuration: get('configuration') || 'Release',
    workspace: path.resolve(get('working-directory') || env.GITHUB_WORKSPACE || process.cwd()),
    token: get('token'),
    registryFallback: get('registry-fallback') !== 'false',
    runnerTemp: env.RUNNER_TEMP || os.tmpdir(),
    ref: env.GITHUB_REF ?? '',
    eventName: env.GITHUB_EVENT_NAME ?? '',
    eventPath: env.GITHUB_EVENT_PATH ?? '',
  };
}

export interface Outputs {
  outcome: 'versioned' | 'skipped';
  mode?: 'server' | 'local';
  released: string[];
  versions: Record<string, string>;
  distTags: Record<string, string>;
}

export interface Dependencies {
  exec: Exec;
  engineProject: string;
  idToken: (audience: string) => Promise<string>;
  fetch?: typeof fetch;
  log: (message: string) => void;
  warn: (message: string) => void;
}

/** The versioning section of coverage.yml on the default branch, the configuration the server reads too (PRD D2). */
export function versioningSection(yml: string | null, defaultBranch: string): Record<string, unknown> {
  if (yml === null) throw new Error(`coverage.yml on ${defaultBranch} does not exist; add a versioning: section to it.`);
  const root = parseYaml(yml, { maxAliasCount: 0 }) as Record<string, unknown> | null;
  const section = root?.versioning;
  if (!section || typeof section !== 'object' || Array.isArray(section))
    throw new Error(`coverage.yml on ${defaultBranch} has no versioning: section.`);
  return section as Record<string, unknown>;
}

function defaultBranchOf(eventPath: string): string {
  try {
    const event = JSON.parse(readFileSync(eventPath, 'utf8'));
    if (typeof event?.repository?.default_branch === 'string') return checkBranchName(event.repository.default_branch);
  } catch {
    // Reported below.
  }
  throw new Error('The workflow event does not name the repository\'s default branch.');
}

/** Why a response is not a success, or null when every release is tagged (created, already there, or tagged by hand). */
export function failureOf(response: RunResponse): string | null {
  const lines: string[] = [];
  if (response.outcome !== 'versioned') lines.push(`${response.outcome}${response.reason ? ` (${response.reason})` : ''}${response.detail ? `: ${response.detail}` : ''}`);
  for (const error of response.errors ?? []) lines.push(`  ${error}`);
  for (const refused of response.refused ?? []) lines.push(`  ${refused.id}: ${refused.reason}`);
  for (const release of response.releases ?? [])
    if (!release.taggedByHand && release.tagOutcome !== 'created' && release.tagOutcome !== 'already-present')
      lines.push(`  ${release.tag}: ${release.tagOutcome ?? 'not written'}`);
  return lines.length === 0 ? null : lines.join('\n');
}

export async function version(inputs: Inputs, deps: Dependencies): Promise<Outputs> {
  // Only a branch run versions (PRD D5): a pull request or a tag build packs MinVer's prerelease and publishes nothing.
  if (!inputs.ref.startsWith('refs/heads/') || inputs.eventName.startsWith('pull_request')) {
    deps.log(`Not a branch run (${inputs.eventName} on ${inputs.ref || 'no ref'}): nothing is versioned.`);
    return { outcome: 'skipped', released: [], versions: {}, distTags: {} };
  }
  const branch = checkBranchName(inputs.ref.slice('refs/heads/'.length));
  const defaultBranch = defaultBranchOf(inputs.eventPath);
  const cwd = inputs.workspace;

  await prepareHistory(deps.exec, cwd, branch, defaultBranch);
  const section = versioningSection(await showOnDefaultBranch(deps.exec, cwd, defaultBranch, 'coverage.yml'), defaultBranch);
  const configured = typeof section.mode === 'string' ? section.mode : 'server';
  const requested = inputs.mode || configured;
  if (requested !== 'server' && requested !== 'local') throw new Error(`mode must be server or local, not '${requested}'.`);

  const work = mkdtempSync(path.join(inputs.runnerTemp, 'versioning-'));
  const config = path.join(work, 'config.json');
  writeFileSync(config, JSON.stringify(section));
  const engine: EngineOptions = { exec: deps.exec, project: deps.engineProject, cwd: inputs.runnerTemp, token: inputs.token || undefined };

  const manifestFile = path.join(work, 'manifest.json');
  const collected = await collectManifest(engine, {
    repo: cwd, out: path.join(work, 'collected.json'), config, configuration: inputs.configuration, branch, defaultBranch,
    registry: inputs.registryFallback,
  });
  for (const note of collected.notes) deps.log(note);
  writeFileSync(manifestFile, JSON.stringify(collected.manifest));

  let mode: 'server' | 'local' = requested;
  let response: RunResponse | null = null;
  if (mode === 'server') {
    const answer = await runOnServer({ url: inputs.url, idToken: deps.idToken, fetch: deps.fetch }, collected.manifest);
    if (answer.kind === 'answer') {
      response = answer.body;
      if (answer.status >= 400 && response.outcome === 'versioned') response = { ...response, outcome: `http-${answer.status}` };
    } else {
      deps.warn(`${inputs.url} is unavailable (${answer.reason}); versioning with the local engine instead.`);
      mode = 'local';
    }
  }
  response ??= await runLocal(engine, { repo: cwd, manifest: manifestFile, config, branch, defaultBranch, out: path.join(work, 'result.json') });

  for (const warning of response.warnings ?? []) deps.warn(warning);
  const failure = failureOf(response);
  if (failure) throw new Error(`Versioning (${mode}) did not tag the commit:\n${failure}`);

  const released = response.releases;
  for (const r of released) deps.log(`${r.id} ${r.version}${r.taggedByHand ? ' (tagged by hand)' : ''}: ${r.reasons.join('; ')}`);
  const distTags = await writeNpm(deps.exec, cwd, released, deps.log);
  return {
    outcome: 'versioned',
    mode,
    released: released.map((r) => r.id),
    versions: Object.fromEntries(released.map((r) => [r.id, r.version])),
    distTags,
  };
}

/** npm takes its version from package.json, so the action writes it there before pack (PRD D3); MinVer reads the tags. */
async function writeNpm(exec: Exec, cwd: string, released: Release[], log: (m: string) => void): Promise<Record<string, string>> {
  const npm = released.filter((r) => r.kind === 'npm');
  if (npm.length === 0) return {};
  const tags = await taggedVersions(exec, cwd);
  const packages = await discoverPackages(exec, cwd);
  const releasedVersions = new Map(npm.map((r) => [r.id, r.version]));
  const internal = new Map<string, string>();
  for (const name of packages.keys()) {
    const v = releasedVersions.get(name) ?? highest(tags.get(name) ?? [])?.text;
    if (v) internal.set(name, v);
  }
  for (const file of writeVersions(packages, releasedVersions, internal)) log(`wrote ${path.relative(cwd, file)}`);
  return Object.fromEntries(npm.map((r) => [r.id, distTag(r.version, tags.get(r.id) ?? [])]));
}

export async function run(): Promise<void> {
  try {
    const inputs = readInputs();
    if (inputs.token) core.setSecret(inputs.token);
    const outputs = await version(inputs, {
      exec: realExec,
      engineProject: defaultEngineProject(),
      idToken: (audience) => core.getIDToken(audience),
      log: (m) => core.info(m),
      warn: (m) => core.warning(m),
    });
    core.setOutput('outcome', outputs.outcome);
    core.setOutput('mode', outputs.mode ?? '');
    core.setOutput('released', JSON.stringify(outputs.released));
    core.setOutput('versions', JSON.stringify(outputs.versions));
    core.setOutput('dist-tags', JSON.stringify(outputs.distTags));
  } catch (e) {
    core.setFailed((e as Error).message);
  }
}
