import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import type { AdapterContext } from '../apply/adapter';
import type { PlanItem } from '../contracts/types';
import { realExec, type Exec, type ExecResult } from '../util/exec';

/** Test-only helpers (excluded from coverage). */

export const FIXTURES = path.resolve(__dirname, '../../test/fixtures');
export const CONTRACTS = path.resolve(__dirname, '../../../contracts/dependency-update');

export function fixture(rel: string): string {
  return path.join(FIXTURES, ...rel.split('/'));
}

export function readFixture(rel: string): string {
  return fs.readFileSync(fixture(rel), 'utf8');
}

export function readJsonFixture<T = unknown>(rel: string): T {
  return JSON.parse(readFixture(rel)) as T;
}

export function tempDir(prefix = 'dep-action-'): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

export function copyTree(src: string, dst: string): void {
  fs.cpSync(src, dst, { recursive: true });
}

export function writeTree(root: string, files: Record<string, string | Buffer>): void {
  for (const [rel, content] of Object.entries(files)) {
    const abs = path.join(root, ...rel.split('/'));
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content);
  }
}

export function gitSync(cwd: string, ...args: string[]): string {
  return execFileSync('git', args, { cwd, stdio: ['ignore', 'pipe', 'pipe'] }).toString();
}

/** A real git repository with one commit holding `files`. Returns its dir and HEAD sha. */
export function gitRepo(files: Record<string, string | Buffer> = {}, from?: string): { dir: string; sha: string } {
  const dir = tempDir();
  if (from) copyTree(from, dir);
  writeTree(dir, files);
  gitSync(dir, 'init', '-q', '-b', 'main');
  gitSync(dir, 'config', 'user.email', 'test@example.invalid');
  gitSync(dir, 'config', 'user.name', 'test');
  gitSync(dir, 'config', 'core.autocrlf', 'false');
  gitSync(dir, 'config', 'commit.gpgsign', 'false');
  gitSync(dir, 'add', '-A');
  gitSync(dir, 'commit', '-q', '--allow-empty', '-m', 'base');
  return { dir, sha: gitSync(dir, 'rev-parse', 'HEAD').trim() };
}

export function commitAll(dir: string, message: string): string {
  gitSync(dir, 'add', '-A');
  gitSync(dir, 'commit', '-q', '--allow-empty', '-m', message);
  return gitSync(dir, 'rev-parse', 'HEAD').trim();
}

export function ctxFor(root: string, exec: Exec = realExec, group: PlanItem[] = []): AdapterContext {
  return { root, exec, feedSecrets: false, log: () => undefined, group, memo: new Set() };
}

export interface LinePair {
  minus: string;
  plus: string;
}

/** The `-`/`+` line pairs of one file in a unified diff (pairs in hunk order; equal counts assumed). */
export function diffPairs(diff: string, file: string): LinePair[] {
  const sections = diff.split(/^diff --git /m).filter(Boolean);
  const section = sections.find((s) => s.startsWith(`a/${file} b/${file}`));
  if (!section) throw new Error(`no section for ${file} in diff`);
  const minus: string[] = [];
  const plus: string[] = [];
  for (const line of section.split('\n')) {
    if (line.startsWith('---') || line.startsWith('+++')) continue;
    if (line.startsWith('-')) minus.push(line.slice(1).replace(/\r$/, ''));
    else if (line.startsWith('+')) plus.push(line.slice(1).replace(/\r$/, ''));
  }
  return minus.map((m, i) => ({ minus: m, plus: plus[i] }));
}

/** Line pairs that differ between two versions of a file with the same line count. */
export function changedPairs(before: string, after: string): LinePair[] {
  const a = before.split(/\r\n|\n|\r/);
  const b = after.split(/\r\n|\n|\r/);
  if (a.length !== b.length) throw new Error(`line count changed: ${a.length} -> ${b.length}`);
  const out: LinePair[] = [];
  a.forEach((line, i) => {
    if (line !== b[i]) out.push({ minus: line, plus: b[i] });
  });
  return out;
}

/** Every byte outside the changed lines is identical (EOLs, BOM, final newline). */
export function onlyLinesChanged(before: Buffer, after: Buffer): boolean {
  const eols = (s: string) => (s.match(/\r\n|\n|\r/g) ?? []).join('|');
  const bom = (b: Buffer) => b.subarray(0, 3).toString('hex') === 'efbbbf';
  return eols(before.toString('utf8')) === eols(after.toString('utf8')) && bom(before) === bom(after);
}

export function item(partial: Partial<PlanItem> & Pick<PlanItem, 'depName' | 'datasource' | 'packageFile' | 'currentValue' | 'newValue'>): PlanItem {
  return {
    manager: partial.manager ?? partial.datasource,
    lockFiles: [],
    newVersion: partial.newVersion ?? partial.newValue.replace(/^[\^~=<>]+/, ''),
    updateType: 'minor',
    ...partial,
  } as PlanItem;
}

type Handler = (cmd: string, args: readonly string[], cwd: string) => Partial<ExecResult> | Promise<Partial<ExecResult>> | undefined;

/**
 * An Exec that answers from `handler` and runs git for real (the orchestrator needs a real tree).
 * Every call is recorded.
 */
export function fakeExec(handler: Handler): { exec: Exec; calls: Array<{ cmd: string; args: string[]; cwd: string }> } {
  const calls: Array<{ cmd: string; args: string[]; cwd: string }> = [];
  const exec: Exec = async (cmd, args, options) => {
    calls.push({ cmd, args: [...args], cwd: options.cwd });
    if (cmd === 'git') return realExec(cmd, args, options);
    const r = (await handler(cmd, args, options.cwd)) ?? {};
    return { code: r.code ?? 0, stdout: r.stdout ?? '', stderr: r.stderr ?? '' };
  };
  return { exec, calls };
}

export const hasCommand = (cmd: string, args: string[] = ['--version']): boolean => {
  try {
    execFileSync(cmd, args, { stdio: 'ignore', shell: process.platform === 'win32' });
    return true;
  } catch {
    return false;
  }
};
