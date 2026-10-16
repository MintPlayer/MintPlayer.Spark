import { promises as fs } from 'node:fs';
import type { Blocker, PlanItem } from '../../contracts/types';
import { escapeRegExp, replaceUniqueToken } from '../../core/lineEdit';
import { combined } from '../../util/exec';
import { OK, dirOf, exists, jsonBlock, notPinned, outcome, readDoc, rel2abs, writeDoc, type Adapter, type AdapterContext, type StepOutcome } from '../adapter';
import { parseNpm, parsePnpmNdjson, parseYarnJson } from '../blockers';

/**
 * npm, pnpm and yarn berry (S13 recommended commands). Our text edit owns package.json; the package
 * manager only ever writes the lockfile. A manager that rewrites package.json anyway (npm install
 * name@ver, yarn normalising) gets its manifest put back to our bytes before the relock, so the
 * manifest diff is always the planned token alone (gap A).
 */

const DEP_BLOCKS = ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies', 'resolutions', 'overrides'];

export async function editPackageJson(ctx: AdapterContext, item: PlanItem): Promise<void> {
  const doc = await readDoc(ctx, item.packageFile);
  const find = new RegExp(`"${escapeRegExp(item.depName)}"\\s*:\\s*"(?<v>${escapeRegExp(item.currentValue)})"`);
  let range: [number, number] | undefined;
  if (item.depType && DEP_BLOCKS.includes(item.depType)) {
    range = jsonBlock(doc, new RegExp(`^\\s*"${escapeRegExp(item.depType)}"\\s*:\\s*\\{`)) ?? undefined;
  }
  replaceUniqueToken(doc, { find, to: item.newValue, range }, item.packageFile);
  await writeDoc(ctx, item.packageFile, doc);
}

function lockOf(item: PlanItem, names: string[]): string | null {
  return item.lockFiles.find((l) => names.some((n) => l === n || l.endsWith(`/${n}`))) ?? null;
}

/** Path of the workspace member relative to the lockfile's directory ('.' = the root project). */
function memberOf(item: PlanItem, lock: string): string {
  const lockDir = dirOf(lock);
  const pkgDir = dirOf(item.packageFile);
  if (pkgDir === lockDir) return '.';
  if (lockDir === '.') return pkgDir;
  return pkgDir.startsWith(`${lockDir}/`) ? pkgDir.slice(lockDir.length + 1) : pkgDir;
}

function groupByLock(items: PlanItem[], names: string[]): Map<string, PlanItem[]> {
  const map = new Map<string, PlanItem[]>();
  for (const item of items) {
    const lock = lockOf(item, names);
    if (!lock) continue;
    map.set(lock, [...(map.get(lock) ?? []), item]);
  }
  return map;
}

async function saveManifests(ctx: AdapterContext, items: PlanItem[], locks: string[]): Promise<Map<string, Buffer>> {
  const files = new Set<string>(items.map((i) => i.packageFile));
  for (const l of locks) files.add(dirOf(l) === '.' ? 'package.json' : `${dirOf(l)}/package.json`);
  const saved = new Map<string, Buffer>();
  for (const f of files) {
    const bytes = await fs.readFile(rel2abs(ctx, f)).catch(() => null);
    if (bytes) saved.set(f, bytes);
  }
  return saved;
}

async function putBack(ctx: AdapterContext, saved: Map<string, Buffer>): Promise<void> {
  for (const [f, bytes] of saved) await fs.writeFile(rel2abs(ctx, f), bytes);
}

const NPM_FLAGS = ['--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund', '--strict-peer-deps'];
const NPM_LOCKS = ['package-lock.json', 'npm-shrinkwrap.json'];

export const npmAdapter: Adapter = {
  id: 'npm',
  matches: (item) => item.datasource === 'npm' && !lockOf(item, ['pnpm-lock.yaml', 'yarn.lock']),
  files: async (_ctx, item) => [item.packageFile, ...item.lockFiles],
  edit: editPackageJson,
  async pin(ctx, items) {
    const byLock = groupByLock(items, NPM_LOCKS);
    for (const [lock, list] of byLock) {
      const saved = await saveManifests(ctx, list, [lock]);
      const byMember = new Map<string, PlanItem[]>();
      for (const i of list) byMember.set(memberOf(i, lock), [...(byMember.get(memberOf(i, lock)) ?? []), i]);
      for (const [member, members] of byMember) {
        const specs = members.map((i) => `${i.depName}@${i.newVersion}`);
        const args = ['install', ...specs, ...NPM_FLAGS, ...(member === '.' ? [] : [`--workspace=${member}`])];
        const r = await ctx.exec('npm', args, { cwd: rel2abs(ctx, dirOf(lock)), timeoutMs: 15 * 60_000 });
        if (r.code !== 0) {
          await putBack(ctx, saved);
          return outcome(r);
        }
      }
      await putBack(ctx, saved);
    }
    return OK;
  },
  async relock(ctx, items) {
    for (const lock of groupByLock(items, NPM_LOCKS).keys()) {
      const r = await ctx.exec('npm', ['install', ...NPM_FLAGS], { cwd: rel2abs(ctx, dirOf(lock)), timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
      // D-C2: a peer "overriding" warning is a blocker even when npm exits 0.
      if (/ERESOLVE overriding peer dependency/.test(combined(r))) return { ok: false, output: combined(r), kind: 'resolver' };
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const [lock, list] of groupByLock(items, NPM_LOCKS)) {
      const json = JSON.parse(await fs.readFile(rel2abs(ctx, lock), 'utf8')) as { packages?: Record<string, { version?: string }> };
      for (const i of list) {
        const member = memberOf(i, lock);
        const keys = [`node_modules/${i.depName}`, member === '.' ? '' : `${member}/node_modules/${i.depName}`].filter(Boolean);
        const hit = keys.some((k) => json.packages?.[k]?.version === i.newVersion);
        if (!hit) misses.push(`${i.depName}@${i.newVersion} in ${lock}`);
      }
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output, _ctx, items) => parseNpm(output, items.map((i) => i.depName)),
  consistencyDir: (item) => dirOf(lockOf(item, NPM_LOCKS) ?? item.packageFile),
  async consistency(ctx, dir) {
    if (!(await exists(ctx, dir === '.' ? 'package-lock.json' : `${dir}/package-lock.json`))) return OK;
    return outcome(await ctx.exec('npm', ['ci', '--ignore-scripts', '--dry-run', '--no-audit', '--no-fund'], { cwd: rel2abs(ctx, dir), timeoutMs: 15 * 60_000 }));
  },
  tools: () => ['npm'],
};

const PNPM_ENV = { npm_config_strict_peer_dependencies: 'true', npm_config_ignore_scripts: 'true' };

export const pnpmAdapter: Adapter = {
  id: 'pnpm',
  matches: (item) => item.datasource === 'npm' && !!lockOf(item, ['pnpm-lock.yaml']),
  files: async (_ctx, item) => [item.packageFile, ...item.lockFiles],
  edit: editPackageJson,
  async pin(ctx, items) {
    for (const [lock, list] of groupByLock(items, ['pnpm-lock.yaml'])) {
      const saved = await saveManifests(ctx, list, [lock]);
      const byMember = new Map<string, PlanItem[]>();
      for (const i of list) byMember.set(memberOf(i, lock), [...(byMember.get(memberOf(i, lock)) ?? []), i]);
      for (const [member, members] of byMember) {
        const args = ['update', ...members.map((i) => `${i.depName}@${i.newVersion}`), '--lockfile-only', '--ignore-scripts', '--reporter=ndjson', ...(member === '.' ? [] : ['--filter', `./${member}`])];
        const r = await ctx.exec('pnpm', args, { cwd: rel2abs(ctx, dirOf(lock)), env: PNPM_ENV, timeoutMs: 15 * 60_000 });
        if (r.code !== 0) {
          await putBack(ctx, saved);
          return outcome(r);
        }
      }
      await putBack(ctx, saved);
    }
    return OK;
  },
  async relock(ctx, items) {
    for (const lock of groupByLock(items, ['pnpm-lock.yaml']).keys()) {
      const r = await ctx.exec('pnpm', ['install', '--lockfile-only', '--ignore-scripts', '--reporter=ndjson'], { cwd: rel2abs(ctx, dirOf(lock)), env: PNPM_ENV, timeoutMs: 15 * 60_000 });
      if (r.code !== 0) return outcome(r);
      if (parsePnpmNdjson(combined(r)).length > 0) return { ok: false, output: combined(r), kind: 'resolver' };
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const [lock, list] of groupByLock(items, ['pnpm-lock.yaml'])) {
      const text = await fs.readFile(rel2abs(ctx, lock), 'utf8');
      for (const i of list) {
        const re = new RegExp(`(^|[\\s'"/])${escapeRegExp(i.depName)}@${escapeRegExp(i.newVersion)}(['"(:]|$)`, 'm');
        if (!re.test(text)) misses.push(`${i.depName}@${i.newVersion} in ${lock}`);
      }
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output, _ctx, items) => parsePnpmNdjson(output, items.map((i) => i.depName)),
  consistencyDir: (item) => dirOf(lockOf(item, ['pnpm-lock.yaml']) ?? item.packageFile),
  consistency: async (ctx, dir) =>
    outcome(await ctx.exec('pnpm', ['install', '--frozen-lockfile', '--ignore-scripts', '--lockfile-only'], { cwd: rel2abs(ctx, dir), env: PNPM_ENV, timeoutMs: 15 * 60_000 })),
  tools: () => ['pnpm'],
};

const YARN_ENV = { YARN_ENABLE_SCRIPTS: '0', YARN_ENABLE_TELEMETRY: '0', YARN_ENABLE_IMMUTABLE_INSTALLS: '0', YARN_ENABLE_GLOBAL_CACHE: '0' };

async function isBerry(ctx: AdapterContext, lock: string): Promise<boolean> {
  const head = (await fs.readFile(rel2abs(ctx, lock), 'utf8').catch(() => '')).slice(0, 2000);
  return /__metadata:/.test(head);
}

function yarn(ctx: AdapterContext, lock: string, args: string[]) {
  // corepack honours the repo's packageManager pin (S13 ran `corepack yarn`).
  return ctx.exec('corepack', ['yarn', ...args], { cwd: rel2abs(ctx, dirOf(lock)), env: YARN_ENV, timeoutMs: 15 * 60_000 });
}

function yarnPeerFailure(output: string): StepOutcome | null {
  const blockers = parseYarnJson(output).filter((b) => b.code === 'YN0060' || b.code === 'YN0002');
  return blockers.length ? { ok: false, output, kind: 'resolver' } : null;
}

export const yarnAdapter: Adapter = {
  id: 'yarn',
  matches: (item) => item.datasource === 'npm' && !!lockOf(item, ['yarn.lock']),
  files: async (_ctx, item) => [item.packageFile, ...item.lockFiles],
  edit: editPackageJson,
  async pin(ctx, items) {
    for (const [lock, list] of groupByLock(items, ['yarn.lock'])) {
      if (!(await isBerry(ctx, lock))) return { ok: false, output: `${lock} is a yarn classic (v1) lockfile; only yarn berry is supported`, kind: 'tool' };
      const saved = await saveManifests(ctx, list, [lock]);
      for (const i of list) {
        // S13: `yarn set resolution <descriptor> npm:<version>` pins exactly; the descriptor is the NEW range.
        const r = await yarn(ctx, lock, ['set', 'resolution', `${i.depName}@npm:${i.newValue}`, `npm:${i.newVersion}`]);
        if (r.code !== 0) {
          await putBack(ctx, saved);
          return outcome(r);
        }
      }
      await putBack(ctx, saved);
    }
    return OK;
  },
  async relock(ctx, items) {
    for (const lock of groupByLock(items, ['yarn.lock']).keys()) {
      const r = await yarn(ctx, lock, ['install', '--mode=update-lockfile', '--json']);
      if (r.code !== 0) return outcome(r);
      // D-C2: yarn reports peer mismatches as warnings (YN0060, exit 0); they are blockers here.
      const peer = yarnPeerFailure(combined(r));
      if (peer) return peer;
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const [lock, list] of groupByLock(items, ['yarn.lock'])) {
      const text = await fs.readFile(rel2abs(ctx, lock), 'utf8');
      for (const i of list) {
        if (!text.includes(`resolution: "${i.depName}@npm:${i.newVersion}"`)) misses.push(`${i.depName}@${i.newVersion} in ${lock}`);
      }
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output, _ctx, items): Blocker[] => parseYarnJson(output, items.map((i) => i.depName)),
  consistencyDir: (item) => dirOf(lockOf(item, ['yarn.lock']) ?? item.packageFile),
  consistency: async (ctx, dir) =>
    outcome(await ctx.exec('corepack', ['yarn', 'install', '--immutable', '--mode=skip-build'], { cwd: rel2abs(ctx, dir), env: YARN_ENV, timeoutMs: 15 * 60_000 })),
  tools: () => ['corepack'],
};
