import * as fs from 'node:fs';
import * as path from 'node:path';
import { runApply } from '../apply/apply';
import type { ApplyResult, PlanGroup } from '../contracts/types';
import { commitAll, gitRepo, hasCommand, tempDir, writeTree } from '../testing/helpers';
import { realExec, type Exec } from '../util/exec';
import { PLAN_PATH } from '../util/git';

/** DEP_ACTION_IT_GROUP picks a CI slice: one group or a comma-separated list; unset runs every group (a local run). */
export type ItGroup = 'node+dotnet' | 'jvm' | 'python+go+rust+php' | 'docker+renovate';

export function enabled(group: ItGroup, ...tools: string[]): boolean {
  const wanted = process.env.DEP_ACTION_IT_GROUP;
  if (wanted && !wanted.split(',').includes(group)) return false;
  const missing = tools.filter((t) => !hasCommand(t, t === 'go' ? ['version'] : ['--version']));
  if (missing.length > 0) {
    // Loud: a skipped integration test must say why in the log.
    console.warn(`[integration ${group}] SKIPPED: tool(s) not installed: ${missing.join(', ')}`);
    return false;
  }
  return true;
}

export interface ApplyRun {
  dir: string;
  sha: string;
  result: ApplyResult;
  patch: string;
  read(rel: string): string;
}

/**
 * The longest one tool command may run in an integration test. The adapters allow 15-30 minutes, which suits a real
 * repository, but vitest stops a test at 300 s (vitest.integration.config.ts), and a command still running then never
 * returns its output: PR #694's run 44337506945 and PR #704's run 45091276261 both hung in Maven with an empty log.
 * Below the test timeout, the command is killed, its output tail is logged, and the test fails on its own assertion.
 */
export const IT_COMMAND_TIMEOUT_MS = 120_000;

function cappedExec(env: Record<string, string> | undefined): Exec {
  return async (cmd, args, o) => {
    const timeoutMs = Math.min(o.timeoutMs ?? IT_COMMAND_TIMEOUT_MS, IT_COMMAND_TIMEOUT_MS);
    const started = Date.now();
    const r = await realExec(cmd, args, { ...o, env: { ...env, ...o.env }, timeoutMs });
    if (Date.now() - started >= timeoutMs) {
      const tail = (text: string) => text.split('\n').slice(-40).join('\n');
      console.error(`[integration] ${cmd} ${args.join(' ')} was killed after ${timeoutMs / 1000} s in ${o.cwd}\n--- stdout (tail)\n${tail(r.stdout)}\n--- stderr (tail)\n${tail(r.stderr)}`);
    }
    return r;
  };
}

/** expected_sha (the given tree, committed) + the plan commit, then a real runApply. */
export async function applyOnRepo(
  files: Record<string, string | Buffer>,
  groups: PlanGroup[],
  opts: { prepare?: (dir: string) => void | Promise<void>; exec?: Exec; env?: Record<string, string>; feedSecrets?: boolean } = {},
): Promise<ApplyRun> {
  const { dir } = gitRepo(files);
  if (opts.prepare) {
    await opts.prepare(dir);
  }
  const sha = commitAll(dir, 'base (locked)');
  writeTree(dir, { [PLAN_PATH]: JSON.stringify({ contractVersion: 1, kind: 'plan', planId: 'it', expectedSha: sha, groups }, null, 2) });
  commitAll(dir, 'plan');
  const out = tempDir();
  const exec: Exec = opts.exec ?? cappedExec(opts.env);
  const { result, patchFile } = await runApply({ exec, repoDir: dir, outDir: out, expectedSha: sha, feedSecrets: opts.feedSecrets ?? false });
  return { dir, sha, result, patch: fs.readFileSync(patchFile, 'utf8'), read: (rel) => fs.readFileSync(path.join(dir, rel), 'utf8') };
}

/** Run a command for test setup (base lockfile); throws with its output on failure. */
export async function must(cmd: string, args: string[], cwd: string, env?: Record<string, string>): Promise<string> {
  const r = await realExec(cmd, args, { cwd, env, timeoutMs: 10 * 60_000 });
  if (r.code !== 0) throw new Error(`${cmd} ${args.join(' ')} exited ${r.code}\n${r.stdout}\n${r.stderr}`);
  return r.stdout;
}
