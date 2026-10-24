import { readFileSync } from 'node:fs';
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
}

export interface Release {
  id: string;
  kind: 'nuget' | 'npm';
  version: string;
  tag: string;
  reasons: string[];
  apiHash?: string;
  taggedByHand: boolean;
  tagOutcome?: 'created' | 'already-present' | 'conflict' | 'unreachable';
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
  /** Outside the checkout (`RUNNER_TEMP`), so its global.json does not pin the SDK the engine builds with (S4). */
  cwd: string;
  /** Pushes the tags instead of the checkout's credentials; only ever in the environment. */
  token?: string;
}

export const defaultEngineProject = () => path.resolve(__dirname, '..', '..', 'CodeCoverage.Versioning.Cli');

/** Runs the engine from source (PRD D2): `dotnet run` builds it the first time, about 19 s cold and 2 s warm. */
async function cli(options: EngineOptions, args: string[], env: NodeJS.ProcessEnv = {}): Promise<void> {
  const result = await options.exec(
    'dotnet',
    ['run', '--project', options.project, '--configuration', 'Release', '--', ...args],
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
}

export async function runLocal(options: EngineOptions, request: LocalRunRequest): Promise<RunResponse> {
  await cli(
    options,
    ['run', '--repo', request.repo, '--manifest', request.manifest, '--config', request.config, '--branch', request.branch,
      '--default-branch', request.defaultBranch, '--out', request.out],
    options.token ? { CODECOVERAGE_VERSIONING_TOKEN: options.token } : {},
  );
  return JSON.parse(readFileSync(request.out, 'utf8'));
}
