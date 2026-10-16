import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { Exec } from '../util/exec';
import type { RenovateReport } from './mapReport';

/**
 * Renovate pinned by digest (R13, S12). A digest bump is a reviewed change: re-run the S12 sentinel
 * test (R17 guard 2) before moving it.
 */
export const RENOVATE_IMAGE =
  'renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77';

/**
 * The repository config handed to Renovate. D-C1: group:monorepos + group:recommended so related
 * packages carry one groupName. D-S12b: the separate* options so holds and cooldown have fallback
 * candidates (without them Renovate reports one candidate per bucket).
 */
export function renovateConfig(): Record<string, unknown> {
  return {
    extends: ['group:monorepos', 'group:recommended'],
    separateMajorMinor: true,
    separateMultipleMajor: true,
    separateMinorPatch: true,
    separateMultipleMinor: true,
    // The local platform cannot read presets from the repo; a repo renovate.json is still merged.
    onboarding: false,
    requireConfig: 'optional',
    dependencyDashboard: false,
  };
}

export interface RenovateRun {
  report: RenovateReport;
  seconds: number;
  log: string;
}

export interface RenovateOptions {
  exec: Exec;
  repoDir: string;
  workDir: string;
  /** GitHub token for github-tags/releases lookups (read-only; github.token is enough). Passed by NAME. */
  githubTokenEnv?: string;
  /** Env var NAME holding a RENOVATE_HOST_RULES JSON array (private feeds, §13.3). Passed by NAME. */
  hostRulesEnv?: string;
  image?: string;
}

/**
 * `docker run` arguments. Secrets travel as `-e NAME` (value inherited from this process's env),
 * never as `-e NAME=value`, so they never appear in a command line or a log.
 */
export function dockerArgs(o: { repoDir: string; outDir: string; configFile: string; image: string; githubTokenEnv?: string; hostRulesEnv?: string }): string[] {
  const args = [
    'run', '--rm',
    '-v', `${o.repoDir}:/usr/src/app`,
    '-v', `${o.outDir}:/out`,
    '-v', `${o.configFile}:/config/renovate.json:ro`,
    '-w', '/usr/src/app',
    '-e', 'LOG_LEVEL=info',
    '-e', 'RENOVATE_CONFIG_FILE=/config/renovate.json',
    '-e', 'GIT_CONFIG_COUNT=1', '-e', 'GIT_CONFIG_KEY_0=safe.directory', '-e', 'GIT_CONFIG_VALUE_0=*',
  ];
  if (o.githubTokenEnv) args.push('-e', `RENOVATE_GITHUB_COM_TOKEN`);
  if (o.hostRulesEnv) args.push('-e', 'RENOVATE_HOST_RULES');
  args.push(
    o.image,
    '--platform=local',
    '--dry-run=lookup',
    '--binary-source=install',
    '--report-type=file',
    '--report-path=/out/report.json',
  );
  return args;
}

export async function runRenovate(o: RenovateOptions): Promise<RenovateRun> {
  const outDir = path.join(o.workDir, 'renovate-out');
  await fs.mkdir(outDir, { recursive: true });
  await fs.chmod(outDir, 0o777).catch(() => undefined);
  const configFile = path.join(o.workDir, 'renovate.json');
  await fs.writeFile(configFile, JSON.stringify(renovateConfig(), null, 2));
  const image = o.image ?? RENOVATE_IMAGE;
  const env: Record<string, string | undefined> = {};
  if (o.githubTokenEnv) env.RENOVATE_GITHUB_COM_TOKEN = process.env[o.githubTokenEnv];
  if (o.hostRulesEnv) env.RENOVATE_HOST_RULES = process.env[o.hostRulesEnv];
  const started = Date.now();
  const result = await o.exec('docker', dockerArgs({ repoDir: o.repoDir, outDir, configFile, image, githubTokenEnv: o.githubTokenEnv, hostRulesEnv: o.hostRulesEnv }), {
    cwd: o.repoDir,
    env,
    timeoutMs: 30 * 60_000,
  });
  const seconds = Math.round((Date.now() - started) / 100) / 10;
  const log = `${result.stdout}\n${result.stderr}`;
  if (result.code !== 0) {
    throw new Error(`renovate exited ${result.code}:\n${log.slice(-4000)}`);
  }
  const report = JSON.parse(await fs.readFile(path.join(outDir, 'report.json'), 'utf8')) as RenovateReport;
  return { report, seconds, log };
}
