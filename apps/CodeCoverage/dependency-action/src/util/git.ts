import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { Exec } from './exec';

export const PLAN_PATH = '.mintplayer/dependency-plan.json';

/**
 * Never in a patch: the plan file (R14, PatchValidator refuses it anyway) and install/cache output a
 * repo may have forgotten to ignore (migrate runs `npm ci`).
 */
export const PATCH_EXCLUDES = [
  `:(exclude)${PLAN_PATH}`,
  ':(exclude,glob)**/node_modules/**',
  ':(exclude,glob).nx/cache/**',
  ':(exclude,glob).nx/workspace-data/**',
  ':(exclude,glob).angular/**',
  ':(exclude,glob)**/obj/**',
  ':(exclude,glob)**/bin/**',
];

export async function git(exec: Exec, cwd: string, args: string[]): Promise<string> {
  const r = await exec('git', args, { cwd, timeoutMs: 10 * 60_000 });
  if (r.code !== 0) throw new Error(`git ${args.join(' ')} exited ${r.code}: ${r.stderr.trim()}`);
  return r.stdout;
}

export async function revParse(exec: Exec, cwd: string, rev: string): Promise<string> {
  return (await git(exec, cwd, ['rev-parse', '--verify', `${rev}^{commit}`])).trim();
}

/**
 * `git diff --binary <base>` of the working tree INCLUDING untracked files (a new lockfile), with the
 * plan file and install output excluded. Uses intent-to-add so untracked files show up as additions
 * without staging their content.
 */
export async function diffBinary(exec: Exec, cwd: string, base: string, outFile: string): Promise<Buffer> {
  await git(exec, cwd, ['add', '--intent-to-add', '--all', '--', '.', ...PATCH_EXCLUDES]);
  // --output: the patch bytes go straight to disk, never through a string decode.
  await fs.mkdir(path.dirname(outFile), { recursive: true });
  await git(exec, cwd, [
    '-c', 'core.quotepath=false', 'diff', '--binary', '--no-color', '--no-ext-diff', '--no-textconv', `--output=${outFile}`, base, '--', '.', ...PATCH_EXCLUDES,
  ]);
  return fs.readFile(outFile);
}

/** Paths changed against `base` (tracked edits plus untracked additions), plan/install output excluded. */
export async function changedFiles(exec: Exec, cwd: string, base: string): Promise<string[]> {
  await git(exec, cwd, ['add', '--intent-to-add', '--all', '--', '.', ...PATCH_EXCLUDES]);
  const out = await git(exec, cwd, ['-c', 'core.quotepath=false', 'diff', '--name-only', base, '--', '.', ...PATCH_EXCLUDES]);
  return out.split('\n').map((s) => s.trim()).filter(Boolean).sort();
}

export function sha256(bytes: Buffer): string {
  return createHash('sha256').update(bytes).digest('hex');
}

/** Bytes of a set of files (null = absent), so a failed group can be put back exactly (D-S13a). */
export type Snapshot = Map<string, Buffer | null>;

export async function snapshot(root: string, files: Iterable<string>): Promise<Snapshot> {
  const snap: Snapshot = new Map();
  for (const rel of files) {
    if (snap.has(rel)) continue;
    snap.set(rel, await fs.readFile(path.join(root, rel)).catch(() => null));
  }
  return snap;
}

export async function restore(root: string, snap: Snapshot): Promise<void> {
  for (const [rel, bytes] of snap) {
    const abs = path.join(root, rel);
    if (bytes === null) await fs.rm(abs, { force: true });
    else {
      await fs.mkdir(path.dirname(abs), { recursive: true });
      await fs.writeFile(abs, bytes);
    }
  }
}

/** Files a tool changed or created anywhere in the tree, relative to the given snapshot of `git status`. */
export async function statusPaths(exec: Exec, cwd: string): Promise<Set<string>> {
  const out = await git(exec, cwd, ['-c', 'core.quotepath=false', 'status', '--porcelain=v1', '--untracked-files=all', '--', '.', ...PATCH_EXCLUDES]);
  const set = new Set<string>();
  for (const line of out.split('\n')) {
    if (line.length < 4) continue;
    let p = line.slice(3);
    const arrow = p.indexOf(' -> ');
    if (arrow >= 0) p = p.slice(arrow + 4);
    set.add(p.replace(/^"|"$/g, ''));
  }
  return set;
}
