import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { ApplyResult, Blocker, ConsistencyCheck, GroupResult, Plan, PlanGroup, PlanItem } from '../contracts/types';
import { EditRefused } from '../core/lineEdit';
import { applyPlatform } from '../platform/applyPlatform';
import { scanLocations } from '../platform/locations';
import { hasTool, type Exec } from '../util/exec';
import { git, restore, snapshot, statusPaths, type Snapshot } from '../util/git';
import { directoryFiles } from '../util/repoFiles';
import type { Adapter, AdapterContext, StepOutcome } from './adapter';
import { ADAPTERS, adapterFor } from './registry';

export interface OrchestratorOptions {
  root: string;
  exec: Exec;
  feedSecrets: boolean;
  consistencyCheck: boolean;
  adapters?: readonly Adapter[];
  log?: (message: string) => void;
  /** Overridable in tests; defaults to "can the executable start". */
  toolAvailable?: (tool: string) => Promise<boolean>;
}

class GroupFailure extends Error {
  constructor(
    readonly status: 'unresolvable' | 'failed',
    message: string,
    readonly blockers: Blocker[] = [],
  ) {
    super(message);
  }
}

const MAX_MESSAGE = 3800;

function tail(text: string): string {
  const t = text.trim();
  return t.length > MAX_MESSAGE ? `…${t.slice(-(MAX_MESSAGE - 1))}` : t;
}

function partition(items: PlanItem[], adapters: readonly Adapter[]): { parts: Array<[Adapter, PlanItem[]]>; missing: PlanItem[] } {
  const parts = new Map<Adapter, PlanItem[]>();
  const missing: PlanItem[] = [];
  for (const item of items) {
    const a = adapterFor(item, adapters);
    if (!a) missing.push(item);
    else parts.set(a, [...(parts.get(a) ?? []), item]);
  }
  return { parts: [...parts.entries()], missing };
}

async function tracked(o: OrchestratorOptions, rel: string): Promise<boolean> {
  const r = await o.exec('git', ['ls-files', '--error-unmatch', '--', rel], { cwd: o.root });
  return r.code === 0;
}

/**
 * Puts the tree back to `snap` (D-S13a, R2): every file the group could touch is restored byte for
 * byte, and anything else a tool changed or created since `before` is reverted to HEAD or deleted.
 * S13 measured NuGet rewriting packages.lock.json on a FAILED restore, so "the tool failed" never
 * means "nothing changed".
 */
async function resetTo(o: OrchestratorOptions, snap: Snapshot, before: Set<string>): Promise<void> {
  const after = await statusPaths(o.exec, o.root);
  for (const p of after) {
    if (snap.has(p) || before.has(p)) continue;
    if (await tracked(o, p)) await git(o.exec, o.root, ['checkout', 'HEAD', '--', p]);
    else await fs.rm(path.join(o.root, p), { force: true, recursive: true });
  }
  await restore(o.root, snap);
}

/** Every file a group may touch, for the snapshot. */
async function groupFiles(o: OrchestratorOptions, ctx: AdapterContext, group: PlanGroup, parts: Array<[Adapter, PlanItem[]]>): Promise<string[]> {
  const files = new Set<string>();
  for (const [a, items] of parts) for (const i of items) for (const f of await a.files(ctx, i)) files.add(f);
  if (group.platform) for (const f of await scanLocations(directoryFiles(o.root), [group.platform.sdk])) files.add(f.file);
  return [...files];
}

async function failureFrom(adapter: Adapter, out: StepOutcome, ctx: AdapterContext, items: PlanItem[]): Promise<GroupFailure> {
  const blockers = await adapter.parseBlockers(out.output, ctx, items);
  return new GroupFailure(out.kind === 'tool' ? 'failed' : 'unresolvable', `${adapter.id}: ${tail(out.output)}`, blockers);
}

export async function applyGroup(o: OrchestratorOptions, group: PlanGroup): Promise<GroupResult> {
  const adapters = o.adapters ?? ADAPTERS;
  const ctx: AdapterContext = { root: o.root, exec: o.exec, feedSecrets: o.feedSecrets, log: o.log ?? (() => undefined), group: group.items, memo: new Set() };
  const { parts, missing } = partition(group.items, adapters);
  const itemsOut = group.items.map((i) => ({
    depName: i.depName,
    packageFile: i.packageFile,
    newVersion: i.newVersion,
    adapter: adapterFor(i, adapters)?.id ?? null,
  }));
  const base: Omit<GroupResult, 'status' | 'message' | 'blockers' | 'files'> = { group: group.group, items: itemsOut };

  if (missing.length > 0) {
    return { ...base, status: 'failed', files: [], blockers: [], message: `no apply adapter for ${missing.map((m) => `${m.depName} (datasource ${m.datasource}, ${m.packageFile})`).join(', ')}` };
  }
  const toolOk = o.toolAvailable ?? ((t: string) => hasTool(o.exec, t, o.root));
  for (const [a, items] of parts) {
    for (const tool of new Set(items.flatMap((i) => a.tools(i)))) {
      if (!(await toolOk(tool))) return { ...base, status: 'failed', files: [], blockers: [], message: `${a.id}: tool '${tool}' is not available on the runner` };
    }
  }

  const before = await statusPaths(o.exec, o.root);
  const files = await groupFiles(o, ctx, group, parts);
  const snap = await snapshot(o.root, [...files, ...before]);
  let platform: GroupResult['platform'];
  try {
    if (group.platform) {
      platform = await applyPlatform(directoryFiles(o.root), group.platform);
      if (platform.edited.length === 0) {
        throw new GroupFailure('failed', `no editable ${group.platform.sdk} location on channel ${group.platform.fromChannel}`);
      }
    }
    for (const [a, items] of parts) {
      if (a.pinFirst && a.pin) {
        const r = await a.pin(ctx, items);
        if (!r.ok) throw await failureFrom(a, r, ctx, items);
      }
      for (const item of items) await a.edit(ctx, item);
      if (!a.pinFirst && a.pin) {
        const r = await a.pin(ctx, items);
        if (!r.ok) throw await failureFrom(a, r, ctx, items);
      }
      for (const step of [a.relock, a.verify]) {
        if (!step) continue;
        const r = await step.call(a, ctx, items);
        if (!r.ok) throw await failureFrom(a, r, ctx, items);
      }
    }
  } catch (err) {
    await resetTo(o, snap, before);
    if (err instanceof GroupFailure) {
      return { ...base, status: err.status, files: [], blockers: err.blockers, message: tail(err.message), ...(platform ? { platform } : {}) };
    }
    if (err instanceof EditRefused) {
      return { ...base, status: 'failed', files: [], blockers: [], message: `edit refused: ${err.message}` };
    }
    return { ...base, status: 'failed', files: [], blockers: [], message: tail(`unexpected error: ${(err as Error).stack ?? String(err)}`) };
  }

  const after = await statusPaths(o.exec, o.root);
  const changed: string[] = [];
  for (const p of new Set([...after, ...snap.keys()])) {
    const now = await fs.readFile(path.join(o.root, p)).catch(() => null);
    const then = snap.has(p) ? snap.get(p)! : undefined;
    // Not snapshotted: a path the tool changed outside the group's files (still part of this group).
    const same = then === undefined ? false : then === null ? now === null : now !== null && now.equals(then);
    if (!same) changed.push(p);
  }
  return { ...base, status: 'applied', files: changed.sort(), blockers: [], message: null, ...(platform ? { platform } : {}) };
}

/** Back to HEAD (= expected_sha + the plan commit) for every changed path. */
export async function resetAll(o: OrchestratorOptions): Promise<void> {
  for (const p of await statusPaths(o.exec, o.root)) {
    if (await tracked(o, p)) await git(o.exec, o.root, ['checkout', 'HEAD', '--', p]);
    else await fs.rm(path.join(o.root, p), { force: true, recursive: true });
  }
}

async function runAll(o: OrchestratorOptions, groups: PlanGroup[]): Promise<GroupResult[]> {
  const out: GroupResult[] = [];
  for (const g of groups) out.push(await applyGroup(o, g));
  return out;
}

/** D-C3: one check per (adapter, directory) the applied groups touched. */
export async function consistency(o: OrchestratorOptions, groups: PlanGroup[], results: GroupResult[]): Promise<{ ok: boolean; checks: ConsistencyCheck[] }> {
  const adapters = o.adapters ?? ADAPTERS;
  const ctx: AdapterContext = { root: o.root, exec: o.exec, feedSecrets: o.feedSecrets, log: o.log ?? (() => undefined), group: [], memo: new Set() };
  const seen = new Set<string>();
  const checks: ConsistencyCheck[] = [];
  for (const g of groups) {
    if (results.find((r) => r.group === g.group)?.status !== 'applied') continue;
    for (const item of g.items) {
      const a = adapterFor(item, adapters);
      if (!a?.consistency || !a.consistencyDir) continue;
      const dir = a.consistencyDir(item);
      const key = `${a.id}\0${dir}`;
      if (seen.has(key)) continue;
      seen.add(key);
      const r = await a.consistency(ctx, dir);
      checks.push({ adapter: a.id, directory: dir, ok: r.ok, message: r.ok ? null : tail(r.output) });
    }
  }
  return { ok: checks.every((c) => c.ok), checks };
}

/**
 * Applies the plan's groups in order, each on top of the previous ones (D-C2), resetting a failing
 * group (R2). Then the whole-tree consistency check (D-C3): on failure, bisect over the applied groups
 * for the first prefix that fails, drop that group, re-apply the rest, and repeat.
 */
export async function applyPlan(plan: Plan, o: OrchestratorOptions): Promise<{ groups: GroupResult[]; consistency: ApplyResult['consistency'] }> {
  let order = [...plan.groups];
  let results = await runAll(o, order);
  const doCheck = o.consistencyCheck && plan.options?.consistencyCheck !== false;
  if (!doCheck) return { groups: results, consistency: { status: 'skipped', checks: [], bisected: [] } };

  const dropped = new Map<string, string>();
  let checks: ConsistencyCheck[] = [];
  for (let round = 0; round <= plan.groups.length; round++) {
    const applied = order.filter((g) => results.find((r) => r.group === g.group)?.status === 'applied');
    const check = await consistency(o, order, results);
    checks = check.checks;
    if (check.ok || applied.length === 0) break;
    let lo = 1;
    let hi = applied.length;
    let culpritMessage = check.checks.filter((c) => !c.ok).map((c) => `${c.adapter} ${c.directory}: ${c.message}`).join('\n');
    while (lo < hi) {
      const mid = Math.floor((lo + hi) / 2);
      await resetAll(o);
      const prefix = applied.slice(0, mid);
      const r = await runAll(o, prefix);
      const c = await consistency(o, prefix, r);
      if (!c.ok) {
        hi = mid;
        culpritMessage = c.checks.filter((x) => !x.ok).map((x) => `${x.adapter} ${x.directory}: ${x.message}`).join('\n');
      } else lo = mid + 1;
    }
    const culprit = applied[lo - 1];
    dropped.set(culprit.group, culpritMessage);
    await resetAll(o);
    order = order.filter((g) => g.group !== culprit.group);
    results = await runAll(o, order);
  }
  const final = plan.groups.map((g): GroupResult => {
    const r = results.find((x) => x.group === g.group);
    if (r && !dropped.has(g.group)) return r;
    return {
      group: g.group,
      status: 'failed',
      items: g.items.map((i) => ({ depName: i.depName, packageFile: i.packageFile, newVersion: i.newVersion, adapter: adapterFor(i, o.adapters ?? ADAPTERS)?.id ?? null })),
      files: [],
      blockers: [],
      message: tail(`dropped by the whole-tree consistency check (D-C3 bisect): ${dropped.get(g.group) ?? ''}`),
    };
  });
  return {
    groups: final,
    // The status describes the FINAL tree; `bisected` names what it took to get there.
    consistency: { status: checks.some((c) => !c.ok) ? 'failed' : checks.length ? 'passed' : 'skipped', checks, bisected: [...dropped.keys()] },
  };
}
