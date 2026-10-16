import { promises as fs } from 'node:fs';
import type { PlanItem } from '../../contracts/types';
import { blockFrom, escapeRegExp, findLines, replaceUniqueToken, type TextDoc } from '../../core/lineEdit';
import { basename } from '../../util/repoFiles';
import { OK, dirOf, editFile, exists, notPinned, outcome, readDoc, rel2abs, tokenOnNamedLine, tokenRegExp, writeDoc, type Adapter, type AdapterContext } from '../adapter';
import { parseCargo, parseComposer, parseGo } from '../blockers';

/** Cargo, Go modules and Composer: text edit + the S13 pin/lock command, scripts and plugins off. */

// ---------------------------------------------------------------- Cargo

/**
 * Cargo.toml: `name = "x"`, `name = { version = "x", ... }`, or a `[dependencies.name]` table with
 * `version = "x"`.
 */
export function editCargoToml(doc: TextDoc, file: string, item: PlanItem): void {
  const name = escapeRegExp(item.depName);
  const inline = findLines(doc, new RegExp(`^\\s*${name}\\s*=`));
  if (inline.length > 0) {
    replaceUniqueToken(doc, { find: tokenRegExp(item.currentValue), to: item.newValue, lineFilter: (t) => new RegExp(`^\\s*${name}\\s*=`).test(t) }, file);
    return;
  }
  const tables = findLines(doc, new RegExp(`^\\s*\\[(?:[\\w.-]+\\.)?(?:dev-|build-)?dependencies\\.${name}\\]`));
  const ranges = tables.map((t) => blockFrom(doc, t + 1, /^\s*\[/)).map(([s, e]) => [s, e - 1] as [number, number]);
  const hits = ranges.filter(([s, e]) => doc.lines.slice(s, e + 1).some((l) => /^\s*version\s*=/.test(l.text)));
  replaceUniqueToken(
    doc,
    { find: new RegExp(`^\\s*version\\s*=\\s*"(?<v>${escapeRegExp(item.currentValue)})"`), to: item.newValue, range: hits.length === 1 ? [hits[0][0], hits[0][1] + 1] : [0, 0] },
    file,
  );
}

function cargoLock(item: PlanItem): string {
  return item.lockFiles.find((l) => basename(l) === 'Cargo.lock') ?? (dirOf(item.packageFile) === '.' ? 'Cargo.lock' : `${dirOf(item.packageFile)}/Cargo.lock`);
}

export function cargoLockHas(text: string, name: string, version: string): boolean {
  return new RegExp(`^name = "${escapeRegExp(name)}"\\r?\\nversion = "${escapeRegExp(version)}"`, 'm').test(text);
}

export const cargoAdapter: Adapter = {
  id: 'cargo',
  matches: (item) => item.datasource === 'crate' && basename(item.packageFile) === 'Cargo.toml',
  files: async (_ctx, item) => [item.packageFile, cargoLock(item)],
  // S13 lockfirst-precise-then-textedit: `cargo update --precise` FIRST, while the new version still
  // fits the old requirement; editing first makes cargo re-resolve to the latest (1.0.229, not 1.0.200).
  pinFirst: true,
  async pin(ctx, items) {
    for (const i of items) {
      if (!(await exists(ctx, cargoLock(i)))) continue;
      const r = await ctx.exec('cargo', ['update', '-p', i.depName, '--precise', i.newVersion], { cwd: rel2abs(ctx, dirOf(i.packageFile)), timeoutMs: 15 * 60_000 });
      // A major (outside the old requirement) cannot be pinned first; the relock after the edit does it.
      if (r.code !== 0 && !/(?:requirement|did not match|candidate versions found which didn't match)/i.test(`${r.stdout}${r.stderr}`)) return outcome(r);
    }
    return OK;
  },
  edit: async (ctx, item) => {
    const doc = await readDoc(ctx, item.packageFile);
    editCargoToml(doc, item.packageFile, item);
    await writeDoc(ctx, item.packageFile, doc);
  },
  async relock(ctx, items) {
    for (const i of items) {
      const lockText = await fs.readFile(rel2abs(ctx, cargoLock(i)), 'utf8').catch(() => null);
      if (lockText === null || cargoLockHas(lockText, i.depName, i.newVersion)) continue;
      const r = await ctx.exec('cargo', ['update', '-p', i.depName, '--precise', i.newVersion], { cwd: rel2abs(ctx, dirOf(i.packageFile)), timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const text = await fs.readFile(rel2abs(ctx, cargoLock(i)), 'utf8').catch(() => null);
      if (text !== null && !cargoLockHas(text, i.depName, i.newVersion)) misses.push(`${i.depName} ${i.newVersion}`);
    }
    if (misses.length) return notPinned(misses);
    // `cargo metadata --locked` fails if the lock would change (S13 verify); resolve only, no build.rs.
    for (const dir of new Set(items.map((i) => dirOf(i.packageFile)))) {
      const r = await ctx.exec('cargo', ['metadata', '--locked', '--format-version', '1', '--no-deps'], { cwd: rel2abs(ctx, dir), timeoutMs: 10 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  parseBlockers: (output) => parseCargo(output),
  consistencyDir: (item) => dirOf(item.packageFile),
  consistency: async (ctx, dir) => outcome(await ctx.exec('cargo', ['metadata', '--locked', '--format-version', '1'], { cwd: rel2abs(ctx, dir), timeoutMs: 10 * 60_000 })),
  tools: () => ['cargo'],
};

// ---------------------------------------------------------------- Go

const GO_ENV = { GOTOOLCHAIN: 'local', GOFLAGS: '-mod=mod', GONOSUMDB: '', GOWORK: 'off' };

export const goAdapter: Adapter = {
  id: 'go',
  matches: (item) => item.datasource === 'go' && basename(item.packageFile) === 'go.mod',
  files: async (_ctx, item) => [item.packageFile, item.packageFile.replace(/go\.mod$/, 'go.sum')],
  async edit(ctx, item) {
    const mod = escapeRegExp(item.depName);
    await editFile(ctx, item.packageFile, tokenOnNamedLine(new RegExp(`(^|\\s)${mod}\\s`), item.currentValue, item.newValue));
  },
  async relock(ctx, items) {
    // S13 textedit-tidy: pure go.mod, go.sum swapped. GOTOOLCHAIN=local turns a newer-go requirement
    // into a blocker (conflict-toolchain.log) instead of a silent toolchain download.
    for (const dir of new Set(items.map((i) => dirOf(i.packageFile)))) {
      const r = await ctx.exec('go', ['mod', 'tidy'], { cwd: rel2abs(ctx, dir), env: GO_ENV, timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const text = await fs.readFile(rel2abs(ctx, i.packageFile), 'utf8');
      if (!new RegExp(`(^|\\s)${escapeRegExp(i.depName)}\\s+${escapeRegExp(i.newValue)}(\\s|$)`, 'm').test(text)) misses.push(`${i.depName} ${i.newValue} (tidy changed it)`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parseGo(output),
  consistencyDir: (item) => dirOf(item.packageFile),
  consistency: async (ctx, dir) => outcome(await ctx.exec('go', ['mod', 'verify'], { cwd: rel2abs(ctx, dir), env: GO_ENV, timeoutMs: 10 * 60_000 })),
  tools: () => ['go'],
};

// ---------------------------------------------------------------- Composer

/** D-S14c: always BOTH flags; `--no-scripts` alone still ran a plugin in S14. */
export const COMPOSER_FLAGS = ['--no-install', '--no-scripts', '--no-plugins', '--no-interaction', '--no-progress'];

function composerLock(item: PlanItem): string {
  return item.lockFiles.find((l) => basename(l) === 'composer.lock') ?? item.packageFile.replace(/composer\.json$/, 'composer.lock');
}

export const composerAdapter: Adapter = {
  id: 'composer',
  matches: (item) => item.datasource === 'packagist' && basename(item.packageFile) === 'composer.json',
  files: async (_ctx, item) => [item.packageFile, composerLock(item)],
  async edit(ctx, item) {
    const doc = await readDoc(ctx, item.packageFile);
    replaceUniqueToken(doc, { find: new RegExp(`"${escapeRegExp(item.depName)}"\\s*:\\s*"(?<v>${escapeRegExp(item.currentValue)})"`, 'i'), to: item.newValue }, item.packageFile);
    await writeDoc(ctx, item.packageFile, doc);
  },
  async relock(ctx, items) {
    for (const dir of new Set(items.map((i) => dirOf(i.packageFile)))) {
      const mine = items.filter((i) => dirOf(i.packageFile) === dir);
      if (!(await exists(ctx, composerLock(mine[0])))) continue;
      // S13 textedit-range-with: `--with name:version` pins the lock exactly for a range.
      const args = ['update', ...mine.map((i) => i.depName), ...mine.flatMap((i) => ['--with', `${i.depName}:${i.newVersion}`]), ...COMPOSER_FLAGS];
      const r = await ctx.exec('composer', args, { cwd: rel2abs(ctx, dir), env: { COMPOSER_NO_INTERACTION: '1', COMPOSER_ALLOW_SUPERUSER: '0' }, timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const text = await fs.readFile(rel2abs(ctx, composerLock(i)), 'utf8').catch(() => null);
      if (text === null) continue;
      const lock = JSON.parse(text) as { packages?: Array<{ name: string; version: string }>; 'packages-dev'?: Array<{ name: string; version: string }> };
      const all = [...(lock.packages ?? []), ...(lock['packages-dev'] ?? [])];
      const hit = all.find((p) => p.name.toLowerCase() === i.depName.toLowerCase());
      if (!hit || hit.version.replace(/^v/, '') !== i.newVersion.replace(/^v/, '')) misses.push(`${i.depName} ${i.newVersion} (lock has ${hit?.version ?? 'none'})`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parseComposer(output),
  consistencyDir: (item) => dirOf(item.packageFile),
  async consistency(ctx, dir) {
    const v = await ctx.exec('composer', ['validate', '--no-check-publish', '--no-check-all', '--no-interaction'], { cwd: rel2abs(ctx, dir), timeoutMs: 5 * 60_000 });
    if (v.code !== 0) return outcome(v);
    return outcome(await ctx.exec('composer', ['install', '--dry-run', '--no-scripts', '--no-plugins', '--no-interaction', '--no-progress'], { cwd: rel2abs(ctx, dir), timeoutMs: 10 * 60_000 }));
  },
  tools: () => ['composer'],
};

