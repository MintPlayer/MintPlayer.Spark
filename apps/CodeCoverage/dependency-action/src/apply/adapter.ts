import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { Blocker, PlanItem } from '../contracts/types';
import { EditRefused, escapeRegExp, parseText, replaceUniqueToken, serializeText, type TextDoc, type TokenEdit } from '../core/lineEdit';
import { combined, type Exec, type ExecResult } from '../util/exec';

export interface AdapterContext {
  root: string;
  exec: Exec;
  /** D-S14b: feed secrets are present in this job (uv then runs with --no-build). */
  feedSecrets: boolean;
  log: (message: string) => void;
  /** The current group's items (set by the orchestrator per group). */
  group: PlanItem[];
  /** Per-group scratch: lets an adapter apply several identical items in one edit. */
  memo: Set<string>;
}

export interface StepOutcome {
  ok: boolean;
  /** Tool output, for the blocker parser and the group message. */
  output: string;
  /** 'resolver' = the resolver refused the set (R2 unresolvable); 'tool' = anything else (failed). */
  kind?: 'resolver' | 'tool';
}

export const OK: StepOutcome = { ok: true, output: '' };

/**
 * One ecosystem's apply logic (D-S13a): edit() -> pin() -> relock() -> verify(), then parseBlockers()
 * on failure. The orchestrator owns snapshot/reset; an adapter only edits and runs its tool.
 * D-S14a: no adapter ever builds.
 */
export interface Adapter {
  id: string;
  /** D-S12d: keyed on datasource + lockFiles (+ the manifest's file name), never on the manager name. */
  matches(item: PlanItem): boolean;
  /** Every file this item may change (manifest, lockfiles, sibling lockfiles). Repo-relative. */
  files(ctx: AdapterContext, item: PlanItem): Promise<string[]>;
  /** Text edit of the manifest(s). Throws EditRefused on 0 or >1 matches. */
  edit(ctx: AdapterContext, item: PlanItem): Promise<void>;
  /** Cargo pins before the edit (S13: `cargo update --precise` must fit the OLD requirement). */
  pinFirst?: boolean;
  pin?(ctx: AdapterContext, items: PlanItem[]): Promise<StepOutcome>;
  relock?(ctx: AdapterContext, items: PlanItem[]): Promise<StepOutcome>;
  /** Exactness check: the lockfile holds exactly the planned version. Reads files; never builds. */
  verify?(ctx: AdapterContext, items: PlanItem[]): Promise<StepOutcome>;
  parseBlockers(output: string, ctx: AdapterContext, items: PlanItem[]): Promise<Blocker[]> | Blocker[];
  /** D-C3 whole-tree check for one directory after the last group. */
  consistency?(ctx: AdapterContext, directory: string): Promise<StepOutcome>;
  /** The directory a consistency check runs in, for an item. */
  consistencyDir?(item: PlanItem): string;
  /** Executables the adapter needs; missing -> the group fails with "tool missing" (never a crash). */
  tools(item: PlanItem): string[];
}

export function rel2abs(ctx: AdapterContext, rel: string): string {
  return path.join(ctx.root, ...rel.split('/'));
}

export function dirOf(rel: string): string {
  const i = rel.lastIndexOf('/');
  return i < 0 ? '.' : rel.slice(0, i);
}

export async function readDoc(ctx: AdapterContext, rel: string): Promise<TextDoc> {
  return parseText(await fs.readFile(rel2abs(ctx, rel)), rel);
}

export async function writeDoc(ctx: AdapterContext, rel: string, doc: TextDoc): Promise<void> {
  await fs.writeFile(rel2abs(ctx, rel), serializeText(doc));
}

export async function exists(ctx: AdapterContext, rel: string): Promise<boolean> {
  return fs
    .stat(rel2abs(ctx, rel))
    .then((s) => s.isFile())
    .catch(() => false);
}

/** One unique token edit on one file. */
export async function editFile(ctx: AdapterContext, rel: string, edit: TokenEdit): Promise<void> {
  const doc = await readDoc(ctx, rel);
  replaceUniqueToken(doc, edit, rel);
  await writeDoc(ctx, rel, doc);
}

/**
 * The generic "line names the dependency and holds the current token" edit: on lines containing
 * `name` (as a whole word-ish token), replace the exact `currentValue` token with `newValue`.
 */
export function tokenOnNamedLine(name: RegExp, currentValue: string, newValue: string, range?: [number, number]): TokenEdit {
  return {
    find: tokenRegExp(currentValue),
    to: newValue,
    lineFilter: (t) => name.test(t),
    range,
  };
}

/**
 * The exact token, not part of a longer version: '4.18.2' does not match inside '^4.18.2' or
 * '14.18.2' or '4.18.21'. Guards apply only on the sides where the token itself is a word character,
 * so '==4.1.0' (pip, operator included) still matches after 'django'.
 */
export function tokenRegExp(token: string): RegExp {
  const before = /^\w/.test(token) ? '(?<![\\w.^~=<>!+])' : '';
  const after = /\w$/.test(token) ? '(?![\\w.+-])' : '';
  return new RegExp(`${before}(?<v>${escapeRegExp(token)})${after}`);
}

export function outcome(r: ExecResult, kind: 'resolver' | 'tool' = 'resolver'): StepOutcome {
  return r.code === 0 ? { ok: true, output: combined(r) } : { ok: false, output: combined(r) || `exit ${r.code}`, kind: r.code === 127 ? 'tool' : kind };
}

export function notPinned(details: string[]): StepOutcome {
  return { ok: false, output: `lockfile does not hold the planned version: ${details.join('; ')}`, kind: 'resolver' };
}

/** JSON-object block [start, end) that opens on the line matching `opener` (brace counting). */
export function jsonBlock(doc: TextDoc, opener: RegExp): [number, number] | null {
  const start = doc.lines.findIndex((l) => opener.test(l.text));
  if (start < 0) return null;
  let depth = 0;
  for (let i = start; i < doc.lines.length; i++) {
    for (const ch of doc.lines[i].text) {
      if (ch === '{' || ch === '[') depth++;
      else if (ch === '}' || ch === ']') depth--;
    }
    if (depth <= 0 && i >= start) return [start, i + 1];
  }
  return [start, doc.lines.length];
}

export { EditRefused };
