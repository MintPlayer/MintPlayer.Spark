import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { MigrateResult } from '../contracts/types';
import { CONTRACT_VERSION } from '../contracts/types';
import { assertValid } from '../contracts/validate';
import { combined, type Exec } from '../util/exec';
import { diffBinary, git, revParse, sha256 } from '../util/git';

/**
 * mode=migrate (P9/R9, S17): run the framework's own migrations after an approved major was applied.
 *
 * S17 conditions, all enforced here:
 * 1. install first (`npm ci --ignore-scripts`), or `ng update` exits 1 with ELSPROBLEMS;
 * 2. Nx needs `--from` (plain `nx migrate <new>` after our apply is a silent exit-0 no-op, nx#23505);
 * 3. Angular: NG_DISABLE_VERSION_CHECK=1 and the LOCAL binary; one package per run, --allow-dirty
 *    from the second on;
 * 4. job env: scripts off, no analytics, no Nx daemon or cloud;
 * 5. `set -euo pipefail` semantics: the first non-zero step aborts, and the completion marker
 *    (migrate-result.json) is written only as the very last step. R19: `--include=all`.
 */

export interface MigratePackage {
  name: string;
  from: string;
  to: string;
}

export interface MigrateOptions {
  exec: Exec;
  repoDir: string;
  outDir: string;
  tool: 'angular' | 'nx';
  packages: MigratePackage[];
  baseSha: string;
  log?: (message: string) => void;
}

export const MIGRATE_ENV = {
  npm_config_ignore_scripts: 'true',
  NG_CLI_ANALYTICS: 'false',
  NG_DISABLE_VERSION_CHECK: '1',
  NX_DAEMON: 'false',
  NX_NO_CLOUD: 'true',
  NX_SKIP_NX_CACHE: 'true',
  CI: 'true',
};

export class MigrateStepFailed extends Error {
  constructor(
    readonly step: string,
    readonly exitCode: number,
    output: string,
  ) {
    super(`migrate step '${step}' exited ${exitCode}; no completion marker written.\n${output.slice(-4000)}`);
    this.name = 'MigrateStepFailed';
  }
}

type PackageManager = 'npm' | 'pnpm' | 'yarn';

async function detectPm(repoDir: string): Promise<PackageManager> {
  const has = (f: string) => fs.stat(path.join(repoDir, f)).then(() => true, () => false);
  if (await has('pnpm-lock.yaml')) return 'pnpm';
  if (await has('yarn.lock')) return 'yarn';
  return 'npm';
}

const INSTALL: Record<PackageManager, { cmd: string; frozen: string[]; update: string[]; relock: string[] }> = {
  npm: { cmd: 'npm', frozen: ['ci', '--ignore-scripts', '--no-audit', '--no-fund'], update: ['install', '--ignore-scripts', '--no-audit', '--no-fund'], relock: ['install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund'] },
  pnpm: { cmd: 'pnpm', frozen: ['install', '--frozen-lockfile', '--ignore-scripts'], update: ['install', '--ignore-scripts'], relock: ['install', '--lockfile-only', '--ignore-scripts'] },
  yarn: { cmd: 'corepack', frozen: ['yarn', 'install', '--immutable', '--mode=skip-build'], update: ['yarn', 'install', '--mode=skip-build'], relock: ['yarn', 'install', '--mode=update-lockfile'] },
};

/** The commands, in order; exported so the S17 sequence is unit-tested without running npm. */
export function migrateSteps(tool: 'angular' | 'nx', packages: MigratePackage[], pm: PackageManager): Array<{ name: string; cmd: string; args: string[] }> {
  const i = INSTALL[pm];
  const npx = pm === 'npm' ? { cmd: 'npx', pre: ['--no-install'] } : pm === 'pnpm' ? { cmd: 'pnpm', pre: ['exec'] } : { cmd: 'corepack', pre: ['yarn', 'exec'] };
  const steps: Array<{ name: string; cmd: string; args: string[] }> = [{ name: 'install', cmd: i.cmd, args: i.frozen }];
  if (tool === 'angular') {
    packages.forEach((p, idx) => {
      steps.push({
        name: `ng update ${p.name}`,
        cmd: npx.cmd,
        args: [...npx.pre, 'ng', 'update', p.name, '--migrate-only', `--from=${p.from}`, `--to=${p.to}`, ...(idx > 0 ? ['--allow-dirty'] : [])],
      });
    });
  } else {
    const nx = packages.find((p) => p.name === 'nx') ?? packages[0];
    steps.push({
      name: 'nx migrate',
      cmd: npx.cmd,
      args: [...npx.pre, 'nx', 'migrate', nx.to, `--from=${packages.map((p) => `${p.name}@${p.from}`).join(',')}`, '--include=all', '--agentic=false'],
    });
    steps.push({ name: 'install (migrated package.json)', cmd: i.cmd, args: i.update });
    steps.push({ name: 'nx migrate --run-migrations', cmd: npx.cmd, args: [...npx.pre, 'nx', 'migrate', '--run-migrations', '--agentic=false'] });
  }
  steps.push({ name: 'relock', cmd: i.cmd, args: i.relock });
  return steps;
}

function depsOf(json: string): Map<string, string> {
  const map = new Map<string, string>();
  try {
    const pkg = JSON.parse(json) as Record<string, Record<string, string> | undefined>;
    for (const block of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
      for (const [k, v] of Object.entries(pkg[block] ?? {})) map.set(k, v);
    }
  } catch {
    /* not JSON: no deps */
  }
  return map;
}

/** R19: every dependency the migration moved that is not one of the group's own packages. */
export async function extraBumps(exec: Exec, repoDir: string, packages: MigratePackage[]): Promise<MigrateResult['extraBumps']> {
  const own = new Set(packages.map((p) => p.name));
  const changed = (await git(exec, repoDir, ['-c', 'core.quotepath=false', 'diff', '--name-only', 'HEAD', '--', ':(glob)**/package.json', 'package.json']))
    .split('\n').map((s) => s.trim()).filter((f) => f.endsWith('package.json') && !f.includes('node_modules/'));
  const out: MigrateResult['extraBumps'] = [];
  for (const file of [...new Set(changed)]) {
    const before = depsOf(await git(exec, repoDir, ['show', `HEAD:${file}`]).catch(() => '{}'));
    const after = depsOf(await fs.readFile(path.join(repoDir, file), 'utf8').catch(() => '{}'));
    for (const name of new Set([...before.keys(), ...after.keys()])) {
      if (own.has(name)) continue;
      const from = before.get(name) ?? null;
      const to = after.get(name) ?? null;
      if (from !== to) out.push({ depName: name, packageFile: file, from, to });
    }
  }
  return out.sort((a, b) => a.depName.localeCompare(b.depName));
}

export async function runMigrate(o: MigrateOptions): Promise<{ result: MigrateResult; resultFile: string; patchFile: string }> {
  const head = await revParse(o.exec, o.repoDir, 'HEAD');
  if (head !== o.baseSha) throw new Error(`checked-out HEAD ${head} is not base_sha ${o.baseSha}`);
  // Validate the inputs against the contract BEFORE anything runs (names/versions are argv, never shell).
  assertValid('migrate', {
    contractVersion: CONTRACT_VERSION, kind: 'migrate-result', tool: o.tool, baseSha: o.baseSha, packages: o.packages,
    completed: true, steps: [], extraBumps: [], patch: { file: 'migrate.patch', bytes: 0, sha256: '0'.repeat(64) },
  });
  const pm = await detectPm(o.repoDir);
  const steps: MigrateResult['steps'] = [];
  for (const step of migrateSteps(o.tool, o.packages, pm)) {
    o.log?.(`::group::${step.name}`);
    const started = Date.now();
    const r = await o.exec(step.cmd, step.args, { cwd: o.repoDir, env: MIGRATE_ENV, timeoutMs: 30 * 60_000 });
    o.log?.(combined(r));
    o.log?.('::endgroup::');
    if (r.code !== 0) throw new MigrateStepFailed(step.name, r.code, combined(r));
    steps.push({ name: step.name, exitCode: 0, seconds: Math.round((Date.now() - started) / 100) / 10 });
  }
  // Nx's migrations.json is a work file of the run that just completed.
  await fs.rm(path.join(o.repoDir, 'migrations.json'), { force: true });

  const bumps = await extraBumps(o.exec, o.repoDir, o.packages);
  await fs.mkdir(o.outDir, { recursive: true });
  const patchFile = path.join(o.outDir, 'migrate.patch');
  const patch = await diffBinary(o.exec, o.repoDir, o.baseSha, patchFile);
  const result: MigrateResult = {
    contractVersion: CONTRACT_VERSION,
    kind: 'migrate-result',
    tool: o.tool,
    baseSha: o.baseSha,
    packages: o.packages,
    completed: true,
    steps,
    extraBumps: bumps,
    patch: { file: 'migrate.patch', bytes: patch.length, sha256: sha256(patch) },
  };
  assertValid('migrate', result);
  // LAST: the completion marker. Anything that threw above left no marker (S17 partial detection).
  const resultFile = path.join(o.outDir, 'migrate-result.json');
  await fs.writeFile(resultFile, JSON.stringify(result, null, 2) + '\n');
  return { result, resultFile, patchFile };
}
