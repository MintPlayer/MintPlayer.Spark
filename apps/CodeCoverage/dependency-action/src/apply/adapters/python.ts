import { promises as fs } from 'node:fs';
import type { PlanItem } from '../../contracts/types';
import { escapeRegExp, findTokenHits } from '../../core/lineEdit';
import { basename } from '../../util/repoFiles';
import { OK, dirOf, editFile, exists, notPinned, outcome, readDoc, rel2abs, tokenOnNamedLine, tokenRegExp, type Adapter, type AdapterContext } from '../adapter';
import { parsePip, parsePoetry, parseUv } from '../blockers';

/**
 * PyPI: plain requirements / setup.py (no lock), pip-tools (requirements.in -> .txt), uv and Poetry.
 * S13: text edit + the tool's own lock step was pure everywhere; uv pins ranges with
 * `--upgrade-package n==v`. D-S14b: with feed secrets present uv runs `--no-build` (sdists run setup.py).
 */

/** PEP 503: '-', '_' and '.' are interchangeable, case-insensitive. */
export function pypiName(name: string): RegExp {
  const body = name.split(/[-_.]+/).map(escapeRegExp).join('[-_.]+');
  return new RegExp(`(?<![\\w.-])${body}(?![\\w-])`, 'i');
}

export function normalizePypi(name: string): string {
  return name.toLowerCase().replace(/[-_.]+/g, '-');
}

function lockOf(item: PlanItem, names: string[]): string | null {
  return item.lockFiles.find((l) => names.includes(basename(l))) ?? null;
}

async function editManifest(ctx: AdapterContext, item: PlanItem): Promise<void> {
  await editFile(ctx, item.packageFile, tokenOnNamedLine(pypiName(item.depName), item.currentValue, item.newValue));
}

/** `name = "x"` followed by `version = "v"` (uv.lock and poetry.lock share the TOML layout). */
export function lockHasVersion(text: string, name: string, version: string): boolean {
  const n = normalizePypi(name);
  const re = /^name = "([^"]+)"\r?\nversion = "([^"]+)"/gm;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) if (normalizePypi(m[1]) === n && m[2] === version) return true;
  return false;
}

export const uvAdapter: Adapter = {
  id: 'uv',
  matches: (item) => item.datasource === 'pypi' && !!lockOf(item, ['uv.lock']),
  files: async (_ctx, item) => [item.packageFile, ...item.lockFiles],
  edit: editManifest,
  async relock(ctx, items) {
    const lock = lockOf(items[0], ['uv.lock'])!;
    const args = ['lock', ...items.flatMap((i) => ['--upgrade-package', `${i.depName}==${i.newVersion}`]), ...(ctx.feedSecrets ? ['--no-build'] : [])];
    return outcome(await ctx.exec('uv', args, { cwd: rel2abs(ctx, dirOf(lock)), timeoutMs: 15 * 60_000 }));
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const lock = lockOf(i, ['uv.lock'])!;
      if (!lockHasVersion(await fs.readFile(rel2abs(ctx, lock), 'utf8'), i.depName, i.newVersion)) misses.push(`${i.depName}==${i.newVersion} in ${lock}`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parseUv(output),
  consistencyDir: (item) => dirOf(lockOf(item, ['uv.lock'])!),
  consistency: async (ctx, dir) => outcome(await ctx.exec('uv', ['lock', '--check', ...(ctx.feedSecrets ? ['--no-build'] : [])], { cwd: rel2abs(ctx, dir), timeoutMs: 15 * 60_000 })),
  tools: () => ['uv'],
};

export const poetryAdapter: Adapter = {
  id: 'poetry',
  matches: (item) => item.datasource === 'pypi' && !!lockOf(item, ['poetry.lock']),
  files: async (_ctx, item) => [item.packageFile, ...item.lockFiles],
  edit: editManifest,
  async relock(ctx, items) {
    const lock = lockOf(items[0], ['poetry.lock'])!;
    return outcome(await ctx.exec('poetry', ['lock', '--no-interaction'], { cwd: rel2abs(ctx, dirOf(lock)), timeoutMs: 15 * 60_000 }));
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const lock = lockOf(i, ['poetry.lock'])!;
      if (!lockHasVersion(await fs.readFile(rel2abs(ctx, lock), 'utf8'), i.depName, i.newVersion)) misses.push(`${i.depName}==${i.newVersion} in ${lock}`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parsePoetry(output),
  consistencyDir: (item) => dirOf(lockOf(item, ['poetry.lock'])!),
  consistency: async (ctx, dir) => outcome(await ctx.exec('poetry', ['check', '--lock', '--no-interaction'], { cwd: rel2abs(ctx, dir), timeoutMs: 10 * 60_000 })),
  tools: () => ['poetry'],
};

/** The compiled lockfile of a requirements.in: the record's lockFiles, else the sibling .txt. */
function compiledOf(item: PlanItem): string {
  return item.lockFiles.find((l) => l.endsWith('.txt')) ?? item.packageFile.replace(/\.in$/, '.txt');
}

export const pipCompileAdapter: Adapter = {
  id: 'pip-compile',
  matches: (item) => item.datasource === 'pypi' && (item.manager === 'pip-compile' || item.packageFile.endsWith('.in')),
  files: async (_ctx, item) => [item.packageFile, compiledOf(item), ...item.lockFiles],
  async edit(ctx, item) {
    // An unpinned `requests` in the .in is a lock-only update; a pinned one is edited like any manifest.
    const doc = await readDoc(ctx, item.packageFile);
    const hits = findTokenHits(doc, { find: tokenRegExp(item.currentValue), lineFilter: (t) => pypiName(item.depName).test(t) });
    if (hits.length === 0 && (item.updateType === 'lockfileUpdate' || item.packageFile.endsWith('.in'))) return;
    await editManifest(ctx, item);
  },
  async relock(ctx, items) {
    for (const i of items) {
      const out = compiledOf(i);
      const existing = (await fs.readFile(rel2abs(ctx, out), 'utf8').catch(() => '')) as string;
      const header = /autogenerated by pip-compile/.test(existing) ? [] : ['--no-header'];
      const args = ['--quiet', ...header, '--upgrade-package', `${i.depName}==${i.newVersion}`, '--output-file', basename(out), basename(i.packageFile)];
      const r = await ctx.exec('pip-compile', args, { cwd: rel2abs(ctx, dirOf(i.packageFile)), timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const text = await fs.readFile(rel2abs(ctx, compiledOf(i)), 'utf8');
      const re = new RegExp(`^${pypiName(i.depName).source}==${escapeRegExp(i.newVersion)}(\\s|$)`, 'im');
      if (!re.test(text)) misses.push(`${i.depName}==${i.newVersion} in ${compiledOf(i)}`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parsePip(output),
  tools: () => ['pip-compile'],
};

/**
 * requirements.txt / setup.py / setup.cfg / a pyproject without a lockfile: text edit only, then a
 * dry-run resolve of a requirements file (S13 verify) so a conflict still yields blockers.
 */
export const pipAdapter: Adapter = {
  id: 'pip',
  matches: (item) => item.datasource === 'pypi' && !lockOf(item, ['uv.lock', 'poetry.lock', 'pdm.lock']) && !pipCompileAdapter.matches(item),
  files: async (_ctx, item) => [item.packageFile],
  edit: editManifest,
  async verify(ctx, items) {
    for (const i of items.filter((x) => /requirements[^/]*\.txt$/.test(x.packageFile))) {
      if (!(await exists(ctx, i.packageFile))) continue;
      const args = ['-m', 'pip', 'install', '--dry-run', '--ignore-installed', '--quiet', '--disable-pip-version-check', ...(ctx.feedSecrets ? ['--only-binary=:all:'] : []), '-r', basename(i.packageFile)];
      const r = await ctx.exec('python3', args, { cwd: rel2abs(ctx, dirOf(i.packageFile)), timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  parseBlockers: (output) => parsePip(output),
  tools: (item) => (/requirements[^/]*\.txt$/.test(item.packageFile) ? ['python3'] : []),
};
