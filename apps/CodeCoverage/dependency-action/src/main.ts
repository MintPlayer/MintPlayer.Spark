import * as core from '@actions/core';
import * as os from 'node:os';
import * as path from 'node:path';
import { runApply } from './apply/apply';
import { githubActionYmlFetcher } from './lookup/interfaceDiff';
import { runLookup } from './lookup/lookup';
import { runMigrate, type MigratePackage } from './migrate/migrate';
import { realFetcher } from './platform/feeds';
import { realExec, type Exec } from './util/exec';

export interface Inputs {
  mode: string;
  expectedSha: string;
  baseSha: string;
  outputDir: string;
  workspace: string;
  feedSecrets: boolean;
  migrateTool: string;
  migratePackages: string;
}

const SHA = /^[0-9a-f]{40}$/;

export function readInputs(env: NodeJS.ProcessEnv = process.env): Inputs {
  const get = (name: string) => (env[`INPUT_${name.replace(/ /g, '_').toUpperCase()}`] ?? '').trim();
  const runnerTemp = env.RUNNER_TEMP || os.tmpdir();
  return {
    mode: get('mode'),
    expectedSha: get('expected-sha'),
    baseSha: get('base-sha'),
    outputDir: get('output-dir') || path.join(runnerTemp, 'dependency-update'),
    workspace: env.GITHUB_WORKSPACE || process.cwd(),
    feedSecrets: get('feed-secrets') === 'true',
    migrateTool: get('migrate-tool'),
    migratePackages: get('migrate-packages'),
  };
}

function requireSha(name: string, value: string): string {
  if (!SHA.test(value)) throw new Error(`input ${name} must be a full 40-hex commit sha (got '${value}')`);
  return value;
}

export function parseMigratePackages(raw: string): MigratePackage[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new Error('input migrate-packages must be a JSON array of {name, from, to}');
  }
  if (!Array.isArray(parsed) || parsed.length === 0) throw new Error('input migrate-packages must be a non-empty JSON array');
  return parsed.map((p) => {
    const { name, from, to } = (p ?? {}) as Record<string, unknown>;
    if (typeof name !== 'string' || typeof from !== 'string' || typeof to !== 'string') throw new Error('each migrate package needs string name, from and to');
    return { name, from, to };
  });
}

/** Dispatches on `mode`. Returns the output directory (uploaded by the template `if: success()`). */
export async function runMode(inputs: Inputs, exec: Exec = realExec, log: (m: string) => void = (m) => core.info(m)): Promise<string> {
  const outDir = inputs.outputDir;
  const workDir = path.join(path.dirname(outDir), 'dependency-update-work');
  switch (inputs.mode) {
    case 'lookup': {
      const { file, document } = await runLookup({
        exec,
        repoDir: inputs.workspace,
        outDir,
        workDir,
        expectedSha: requireSha('expected-sha', inputs.expectedSha),
        fetcher: realFetcher,
        fetchActionYml: githubActionYmlFetcher(process.env['INPUT_GITHUB-TOKEN'] || undefined),
        githubTokenEnv: process.env['INPUT_GITHUB-TOKEN'] ? 'INPUT_GITHUB-TOKEN' : undefined,
        hostRulesEnv: process.env['INPUT_HOST-RULES'] ? 'INPUT_HOST-RULES' : undefined,
      });
      log(`lookup: ${document.records.length} records, ${document.platforms.length} platforms, ${document.interfaceDiffs.length} interface diffs -> ${file}`);
      return outDir;
    }
    case 'apply': {
      const { result, resultFile } = await runApply({
        exec,
        repoDir: inputs.workspace,
        outDir,
        expectedSha: requireSha('expected-sha', inputs.expectedSha),
        feedSecrets: inputs.feedSecrets,
        log,
      });
      for (const g of result.groups) log(`group ${g.group}: ${g.status}${g.message ? ` (${g.message.split('\n')[0]})` : ''}`);
      log(`consistency: ${result.consistency.status}; patch ${result.patch.bytes} bytes -> ${resultFile}`);
      return outDir;
    }
    case 'migrate': {
      const tool = inputs.migrateTool;
      if (tool !== 'angular' && tool !== 'nx') throw new Error(`input migrate-tool must be angular or nx (got '${tool}')`);
      const { result, resultFile } = await runMigrate({
        exec,
        repoDir: inputs.workspace,
        outDir,
        tool,
        packages: parseMigratePackages(inputs.migratePackages),
        baseSha: requireSha('base-sha', inputs.baseSha),
        log,
      });
      log(`migrate: ${result.steps.length} steps, ${result.extraBumps.length} extra bumps -> ${resultFile}`);
      return outDir;
    }
    default:
      throw new Error(`input mode must be lookup, apply or migrate (got '${inputs.mode}')`);
  }
}

export async function run(): Promise<void> {
  try {
    const outDir = await runMode(readInputs());
    core.setOutput('output-dir', outDir);
  } catch (err) {
    core.setFailed((err as Error).message ?? String(err));
  }
}
