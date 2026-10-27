import * as core from '@actions/core';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { parse as parseYaml } from 'yaml';
import {
  collectManifest, defaultEngineProject, prebuiltEngine, pushTags, runLocal, type EngineOptions, type Release, type RunResponse,
} from './engine';
import { realExec, type Exec } from './exec';
import { checkBranchName, fetchTags, prepareHistory, showOnDefaultBranch, taggedVersions } from './git';
import { discoverPackages, PLACEHOLDER, writeVersions, type Stamp } from './npm';
import { distTag, highest } from './semver';
import { createReleases, cutReleaseBranches } from './releases';
import { planOnServer, releaseOnServer, runOnServer } from './server';

export interface Inputs {
  /** #552 D4: all (plan and tag in one step), plan (decide, write nothing) or release (write the plan's tags). */
  phase: string;
  /** phase: release: the plan phase's `plan` output. */
  plan: string;
  /** The commit the workflow runs for, which a release's plan must have been made for. */
  sha: string;
  mode: string;
  url: string;
  configuration: string;
  workspace: string;
  token: string;
  registryFallback: boolean;
  releases: boolean;
  cutReleaseBranches: boolean;
  githubToken: string;
  repository: string;
  apiUrl: string;
  runnerTemp: string;
  ref: string;
  eventName: string;
  eventPath: string;
}

export function readInputs(env: NodeJS.ProcessEnv = process.env): Inputs {
  const get = (name: string) => (env[`INPUT_${name.replace(/ /g, '_').toUpperCase()}`] ?? '').trim();
  return {
    phase: get('phase'),
    plan: get('plan'),
    sha: env.GITHUB_SHA ?? '',
    mode: get('mode'),
    url: get('url') || 'https://coverage.mintplayer.com',
    configuration: get('configuration') || 'Release',
    workspace: path.resolve(get('working-directory') || env.GITHUB_WORKSPACE || process.cwd()),
    token: get('token'),
    registryFallback: get('registry-fallback') !== 'false',
    releases: get('releases') !== 'false',
    cutReleaseBranches: get('cut-release-branches') !== 'false',
    githubToken: get('github-token'),
    repository: env.GITHUB_REPOSITORY ?? '',
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    runnerTemp: env.RUNNER_TEMP || os.tmpdir(),
    ref: env.GITHUB_REF ?? '',
    eventName: env.GITHUB_EVENT_NAME ?? '',
    eventPath: env.GITHUB_EVENT_PATH ?? '',
  };
}

export interface Outputs {
  outcome: 'versioned' | 'planned' | 'skipped';
  mode?: 'server' | 'local';
  released: string[];
  versions: Record<string, string>;
  distTags: Record<string, string>;
  /** #552 D10: every package's version, released or not (absent when skipped). */
  allVersions?: Record<string, string>;
  /** phase: plan: the {@link PlanOutput} as JSON, for the release phase's `plan` input. */
  plan?: string;
}

export interface Dependencies {
  exec: Exec;
  engineProject: string;
  /** The prebuilt engine, run in place of {@link engineProject} when present (#552 D8). */
  engineDll?: string;
  idToken: (audience: string) => Promise<string>;
  fetch?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
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

/**
 * Why a response is not a success, or null when every release is tagged (created, already there, or tagged by hand);
 * for a plan, when every release is planned and the plan is there.
 */
export function failureOf(response: RunResponse, planned = false): string | null {
  const lines: string[] = [];
  if (response.outcome !== 'versioned') lines.push(`${response.outcome}${response.reason ? ` (${response.reason})` : ''}${response.detail ? `: ${response.detail}` : ''}`);
  else if (planned && !response.plan) lines.push('the answer holds no plan');
  for (const error of response.errors ?? []) lines.push(`  ${error}`);
  for (const refused of response.refused ?? []) lines.push(`  ${refused.id}: ${refused.reason}`);
  const written = planned ? ['planned'] : ['created', 'already-present'];
  for (const release of response.releases ?? [])
    if (!release.taggedByHand && !written.includes(release.tagOutcome ?? ''))
      lines.push(`  ${release.tag}: ${release.tagOutcome ?? 'not written'}`);
  return lines.length === 0 ? null : lines.join('\n');
}

/**
 * What `phase: plan` hands `phase: release` through a job output (#552 D5): the plan-only answer, the host that made
 * it, and the commit and branch it was made for.
 */
export interface PlanOutput {
  commit: string;
  branch: string;
  mode: 'server' | 'local';
  response: RunResponse;
  /** The manifest's package ids: the release phase has no manifest, and reports `all-versions` over the same set. */
  packages: string[];
}

export async function version(inputs: Inputs, deps: Dependencies): Promise<Outputs> {
  const phase = inputs.phase || 'all';
  if (phase !== 'all' && phase !== 'plan' && phase !== 'release') throw new Error(`phase must be all, plan or release, not '${phase}'.`);
  // Only a branch run versions (PRD D5): a pull request or a tag build versions nothing, and its pack
  // re-packs the last tagged versions, which a publish with skip-duplicate skips.
  if (!inputs.ref.startsWith('refs/heads/') || inputs.eventName.startsWith('pull_request')) {
    deps.log(`Not a branch run (${inputs.eventName} on ${inputs.ref || 'no ref'}): nothing is versioned.`);
    return { outcome: 'skipped', released: [], versions: {}, distTags: {} };
  }
  const branch = checkBranchName(inputs.ref.slice('refs/heads/'.length));
  const defaultBranch = defaultBranchOf(inputs.eventPath);
  const cwd = inputs.workspace;
  if (phase === 'release') return release(inputs, deps, branch, defaultBranch);

  await prepareHistory(deps.exec, cwd, branch, defaultBranch);
  const section = versioningSection(await showOnDefaultBranch(deps.exec, cwd, defaultBranch, 'coverage.yml'), defaultBranch);
  const configured = typeof section.mode === 'string' ? section.mode : 'server';
  const requested = inputs.mode || configured;
  if (requested !== 'server' && requested !== 'local') throw new Error(`mode must be server or local, not '${requested}'.`);

  const work = mkdtempSync(path.join(inputs.runnerTemp, 'versioning-'));
  const config = path.join(work, 'config.json');
  writeFileSync(config, JSON.stringify(section));
  const engine = engineOptions(inputs, deps);

  const manifestFile = path.join(work, 'manifest.json');
  const collected = await collectManifest(engine, {
    repo: cwd, out: path.join(work, 'collected.json'), config, configuration: inputs.configuration, branch, defaultBranch,
    registry: inputs.registryFallback,
  });
  for (const note of collected.notes) deps.log(note);
  writeFileSync(manifestFile, JSON.stringify(collected.manifest));

  // #552 D1: the plan phase asks the same host the same question, with the tags left unwritten.
  const planOnly = phase === 'plan';
  let mode: 'server' | 'local' = requested;
  let response: RunResponse | null = null;
  if (mode === 'server') {
    const server = { url: inputs.url, idToken: deps.idToken, fetch: deps.fetch };
    const answer = await (planOnly ? planOnServer(server, collected.manifest) : runOnServer(server, collected.manifest));
    if (answer.kind === 'answer') response = answered(answer.status, answer.body);
    else {
      deps.warn(`${inputs.url} is unavailable (${answer.reason}); versioning with the local engine instead.`);
      mode = 'local';
    }
  }
  response ??= await runLocal(engine, {
    repo: cwd, manifest: manifestFile, config, branch, defaultBranch, out: path.join(work, 'result.json'), planOnly,
  });

  const packages = (collected.manifest.packages as { id: string }[]).map((p) => p.id);
  if (!planOnly) return finish(inputs, deps, mode, response, packages);

  for (const warning of response.warnings ?? []) deps.warn(warning);
  const failure = failureOf(response, true);
  if (failure) throw new Error(`Versioning (${mode}) could not plan the commit:\n${failure}`);
  for (const r of response.releases) deps.log(`${r.id} ${r.version}${r.taggedByHand ? ' (tagged by hand)' : ''} (planned): ${r.reasons.join('; ')}`);
  // #552 D10: the stamps need no new tag (a release at its planned version, any other package at its last tag), so the
  // plan writes them, and whatever builds between the plan and the tests (a demo, a docs site) reads the right version.
  const distTags = await writeNpm(deps.exec, cwd, response.releases, deps.log, deps.warn);
  const plan: PlanOutput = { commit: collected.manifest.commit, branch, mode, response, packages };
  return {
    ...released(response.releases), outcome: 'planned', mode, distTags,
    allVersions: await allVersionsOf(deps.exec, cwd, packages, response.releases), plan: JSON.stringify(plan),
  };
}

/**
 * #552 D10: every package's version, released or not: a release's own, else the last tag reachable from HEAD, which is
 * what MintPlayer.Versioning.MSBuild and the npm stamps give an unreleased package. A package with neither has none.
 */
export async function allVersionsOf(exec: Exec, cwd: string, packages: string[], releases: Release[]): Promise<Record<string, string>> {
  const reachable = await taggedVersions(exec, cwd, 'HEAD');
  const versions: Record<string, string> = {};
  for (const id of new Set([...packages, ...releases.map((r) => r.id)])) {
    const version = releases.find((r) => r.id === id)?.version ?? highest(reachable.get(id) ?? [])?.text;
    if (version) versions[id] = version;
  }
  return versions;
}

/** A 4xx keeps the app's own outcome only when it names a failure. */
function answered(status: number, body: RunResponse): RunResponse {
  return status >= 400 && body.outcome === 'versioned' ? { ...body, outcome: `http-${status}` } : body;
}

function engineOptions(inputs: Inputs, deps: Dependencies): EngineOptions {
  return { exec: deps.exec, project: deps.engineProject, prebuilt: deps.engineDll, cwd: inputs.runnerTemp, token: inputs.token || undefined };
}

function released(releases: Release[]): Pick<Outputs, 'released' | 'versions'> {
  return { released: releases.map((r) => r.id), versions: Object.fromEntries(releases.map((r) => [r.id, r.version])) };
}

/** Reads the plan phase's output, and refuses one made for another commit or branch (#552 D3). */
export function readPlan(text: string, sha: string, branch: string): PlanOutput {
  if (!text) throw new Error('phase: release needs the plan phase\'s `plan` output as its `plan` input.');
  let plan: PlanOutput;
  try {
    plan = JSON.parse(text);
  } catch {
    throw new Error('The plan input is not the plan phase\'s `plan` output.');
  }
  if (!plan?.response || (plan.mode !== 'server' && plan.mode !== 'local') || typeof plan.commit !== 'string')
    throw new Error('The plan input is not the plan phase\'s `plan` output.');
  if (plan.commit !== sha || plan.branch !== branch)
    throw new Error(`The plan was made for ${plan.commit} on ${plan.branch}, not ${sha} on ${branch}: use "Re-run all jobs" to plan again.`);
  return plan;
}

/**
 * #552 D2: writes the tags the plan phase decided, deciding nothing again: through the app with its seal, or with the
 * local engine, which the server's plan falls back to on a 5xx as a run does. Either refuses a plan the tag history
 * moved past (D3).
 */
async function release(inputs: Inputs, deps: Dependencies, branch: string, defaultBranch: string): Promise<Outputs> {
  const plan = readPlan(inputs.plan, inputs.sha, branch);
  const cwd = inputs.workspace;
  await prepareHistory(deps.exec, cwd, branch, defaultBranch);

  let mode = plan.mode;
  let response: RunResponse | null = null;
  if (mode === 'server') {
    const answer = plan.response.seal
      ? await releaseOnServer({ url: inputs.url, idToken: deps.idToken, fetch: deps.fetch }, plan.response.seal)
      : { kind: 'unavailable' as const, reason: 'the plan carries no seal' };
    if (answer.kind === 'answer') response = answered(answer.status, answer.body);
    else {
      deps.warn(`${inputs.url} is unavailable (${answer.reason}); writing the plan's tags with the local engine instead.`);
      mode = 'local';
    }
  }
  if (!response) {
    const work = mkdtempSync(path.join(inputs.runnerTemp, 'versioning-'));
    const planFile = path.join(work, 'plan.json');
    writeFileSync(planFile, JSON.stringify(plan.response));
    response = await pushTags(engineOptions(inputs, deps), { repo: cwd, plan: planFile, branch, out: path.join(work, 'result.json') });
  }
  return finish(inputs, deps, mode, response, plan.packages ?? []);
}

/**
 * After the tags are written, by a run or a release: npm's versions, and in local mode the Releases and the cuts.
 * A release stamps again (the same stamps as its plan), so a job that did not inherit the plan's files publishes right.
 */
async function finish(inputs: Inputs, deps: Dependencies, mode: 'server' | 'local', response: RunResponse, packages: string[]): Promise<Outputs> {
  const cwd = inputs.workspace;
  for (const warning of response.warnings ?? []) deps.warn(warning);
  const failure = failureOf(response);
  if (failure) throw new Error(`Versioning (${mode}) did not tag the commit:\n${failure}`);

  const releases = response.releases;
  for (const r of releases) deps.log(`${r.id} ${r.version}${r.taggedByHand ? ' (tagged by hand)' : ''}: ${r.reasons.join('; ')}`);
  // The server pushed the tags to the remote; the pack step's build reads them from this checkout (D9).
  if (mode === 'server') await fetchTags(deps.exec, cwd);
  const distTags = await writeNpm(deps.exec, cwd, releases, deps.log, deps.warn);

  // PRD D15: the server creates its Releases itself, behind the app's release-on-tag policy. The local host cannot read
  // that policy, so the `releases` input stands in for it, default on like the policy's own default.
  if (mode === 'local' && inputs.releases) {
    if (!inputs.githubToken || !inputs.repository) deps.warn('No github-token or GITHUB_REPOSITORY: the Releases are not created.');
    else await createReleases({ apiUrl: inputs.apiUrl, repository: inputs.repository, token: inputs.githubToken, fetch: deps.fetch, sleep: deps.sleep,
      log: deps.log, warn: deps.warn }, releases);
  }
  // PRD D12: likewise for the release-branch cuts, behind `cut-release-branches` in place of the auto-cut policy.
  const cuts = response.branchCuts ?? [];
  if (mode === 'local' && inputs.cutReleaseBranches && cuts.length > 0) {
    if (!inputs.githubToken || !inputs.repository) deps.warn('No github-token or GITHUB_REPOSITORY: the release branches are not cut.');
    else await cutReleaseBranches({ apiUrl: inputs.apiUrl, repository: inputs.repository, token: inputs.githubToken, fetch: deps.fetch, sleep: deps.sleep,
      log: deps.log, warn: deps.warn }, cuts);
  }
  return { ...released(releases), outcome: 'versioned', mode, distTags, allVersions: await allVersionsOf(deps.exec, cwd, packages, releases) };
}

/**
 * npm takes its version from package.json, so the action stamps EVERY publishable package there before publish (PRD
 * D3, D9): a release at its new version under its dist-tag, any other package at its last reachable tag, which the
 * publisher skips as already published. The .NET side reads the same tags in MintPlayer.Versioning.MSBuild.
 */
async function writeNpm(exec: Exec, cwd: string, released: Release[], log: (m: string) => void, warn: (m: string) => void): Promise<Record<string, string>> {
  const npm = released.filter((r) => r.kind === 'npm');
  const packages = await discoverPackages(exec, cwd);
  if (npm.length === 0 && ![...packages.values()].some((p) => !p.private)) return {};
  const all = await taggedVersions(exec, cwd);
  const reachable = await taggedVersions(exec, cwd, 'HEAD');
  const distTags = Object.fromEntries(npm.map((r) => [r.id, distTag(r.version, all.get(r.id) ?? [])]));

  const stamps = new Map<string, Stamp>(npm.map((r) => [r.id, { version: r.version, distTag: distTags[r.id] }]));
  for (const pkg of packages.values()) {
    if (pkg.private || stamps.has(pkg.name)) continue;
    const last = highest(reachable.get(pkg.name) ?? [])?.text;
    if (last) stamps.set(pkg.name, { version: last, distTag: null });
    else if (pkg.version === PLACEHOLDER) {
      stamps.set(pkg.name, 'withhold');
      warn(`${pkg.name} has no tag reachable from HEAD: it stays at ${PLACEHOLDER} and is marked private, so it is not published.`);
    }
  }
  const internal = new Map<string, string>();
  for (const [name, stamp] of stamps) if (stamp !== 'withhold') internal.set(name, stamp.version);
  for (const file of writeVersions(packages, stamps, internal)) log(`wrote ${path.relative(cwd, file)}`);
  return distTags;
}

export async function run(): Promise<void> {
  try {
    const inputs = readInputs();
    if (inputs.token) core.setSecret(inputs.token);
    if (inputs.githubToken) core.setSecret(inputs.githubToken);
    const outputs = await version(inputs, {
      exec: realExec,
      engineProject: defaultEngineProject(),
      engineDll: prebuiltEngine(),
      idToken: (audience) => core.getIDToken(audience),
      log: (m) => core.info(m),
      warn: (m) => core.warning(m),
    });
    core.setOutput('outcome', outputs.outcome);
    core.setOutput('mode', outputs.mode ?? '');
    core.setOutput('released', JSON.stringify(outputs.released));
    core.setOutput('versions', JSON.stringify(outputs.versions));
    core.setOutput('dist-tags', JSON.stringify(outputs.distTags));
    core.setOutput('plan', outputs.plan ?? '');
    core.setOutput('all-versions', JSON.stringify(outputs.allVersions ?? {}));
  } catch (e) {
    core.setFailed((e as Error).message);
  }
}
