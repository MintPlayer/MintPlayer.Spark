import { existsSync, readFileSync } from 'node:fs';
import * as path from 'node:path';
import { type Exec } from './exec';

/** The run response both hosts give (`VersioningController.VersioningRunResponse`, the CLI's `LocalRunResponse`). */
export interface RunResponse {
  outcome: string;
  reason?: string;
  detail?: string;
  releases: Release[];
  refused: { id: string; reason: string }[];
  skipped: number;
  warnings: string[];
  errors?: string[];
  /** The release branches a platform major move asks for (PRD D12); the server cuts them itself. */
  branchCuts?: BranchCut[];
  /** #552 D1: a plan-only run's tags, written by the release half. */
  plan?: TagPlan | null;
  /** #552 D6: the server's seal over its plan; the server's release takes only this. */
  seal?: string | null;
}

/** The tags a plan decided (`ReleaseRun.TagPlan`), and the tag history it decided them on (#552 D3). */
export interface TagPlan {
  commit: string;
  fingerprint: string;
  run?: string | null;
  tags: { name: string; apiHash?: string | null }[];
}

/** `release/<major>.x` at `commit`, the release commit's first parent. */
export interface BranchCut {
  branch: string;
  major: number;
  commit: string;
  packages: string[];
  pullRequest?: number | null;
}

export interface Release {
  id: string;
  kind: 'nuget' | 'npm';
  version: string;
  tag: string;
  reasons: string[];
  apiHash?: string;
  taggedByHand: boolean;
  tagOutcome?: 'created' | 'already-present' | 'conflict' | 'unreachable' | 'planned';
  /** The GitHub Release for the tag (PRD D15); absent when the tag is not in place. */
  release?: { name: string; body: string; prerelease: boolean };
}

export interface ManifestFile {
  manifest: { commit: string; packages: unknown[] };
  notes: string[];
}

export interface EngineOptions {
  exec: Exec;
  /** CodeCoverage.Versioning.Cli's project directory: next to the action in the repository the action runs from. */
  project: string;
  /** The published engine's dll, run in place of the project when present (#552 D8). */
  prebuilt?: string;
  /** Outside the checkout (`RUNNER_TEMP`), so its global.json does not pin the SDK the engine builds with (S4). */
  cwd: string;
  /** Pushes the tags instead of the checkout's credentials; only ever in the environment. */
  token?: string;
}

export const defaultEngineProject = () => path.resolve(__dirname, '..', '..', 'CodeCoverage.Versioning.Cli');

/**
 * #552 D8: the engine the release workflow published next to the action, on the `versioning-v1.x.y` tag commit only.
 * A checkout of a branch has none, and runs the engine from source.
 */
export const prebuiltEngine = (dir = path.resolve(__dirname, '..', 'engine')) => {
  const dll = path.join(dir, 'codecoverage-versioning.dll');
  return existsSync(dll) ? dll : undefined;
};

/**
 * Runs the engine: the prebuilt one when the action carries it (#552 D8), else from source (PRD D2), where
 * `dotnet run` builds it the first time, about 19 s cold and 2 s warm.
 */
async function cli(options: EngineOptions, args: string[], env: NodeJS.ProcessEnv = {}): Promise<void> {
  const result = await options.exec(
    'dotnet',
    options.prebuilt ? [options.prebuilt, ...args] : ['run', '--project', options.project, '--configuration', 'Release', '--', ...args],
    { cwd: options.cwd, env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1', ...env } },
  );
  if (result.code !== 0)
    throw new Error(`The versioning engine failed (exit ${result.code}): ${(result.stderr || result.stdout).trim().slice(-4000)}`);
}

export interface ManifestRequest {
  repo: string;
  out: string;
  config: string;
  configuration: string;
  branch: string;
  defaultBranch: string;
  registry: boolean;
}

export async function collectManifest(options: EngineOptions, request: ManifestRequest): Promise<ManifestFile> {
  await cli(options, [
    'manifest', '--repo', request.repo, '--out', request.out, '--config', request.config, '--configuration', request.configuration,
    '--branch', request.branch, '--default-branch', request.defaultBranch, ...(request.registry ? [] : ['--no-registry']),
  ]);
  return JSON.parse(readFileSync(request.out, 'utf8'));
}

export interface LocalRunRequest {
  repo: string;
  manifest: string;
  config: string;
  branch: string;
  defaultBranch: string;
  out: string;
  /** #552 D1: decide the tags and write none; the answer carries the plan. */
  planOnly?: boolean;
}

export async function runLocal(options: EngineOptions, request: LocalRunRequest): Promise<RunResponse> {
  await cli(
    options,
    ['run', '--repo', request.repo, '--manifest', request.manifest, '--config', request.config, '--branch', request.branch,
      '--default-branch', request.defaultBranch, '--out', request.out, ...(request.planOnly ? ['--plan-only'] : [])],
    options.token && !request.planOnly ? { CODECOVERAGE_VERSIONING_TOKEN: options.token } : {},
  );
  return JSON.parse(readFileSync(request.out, 'utf8'));
}

export interface PushTagsRequest {
  repo: string;
  /** A file holding a plan-only answer (`plan` set). */
  plan: string;
  branch: string;
  out: string;
}

/** #552 D2, D3: writes a plan's tags, or answers `stale-plan` when HEAD or the tag history moved since it was made. */
export async function pushTags(options: EngineOptions, request: PushTagsRequest): Promise<RunResponse> {
  await cli(
    options,
    ['push-tags', '--repo', request.repo, '--plan', request.plan, '--branch', request.branch, '--out', request.out],
    options.token ? { CODECOVERAGE_VERSIONING_TOKEN: options.token } : {},
  );
  return JSON.parse(readFileSync(request.out, 'utf8'));
}
