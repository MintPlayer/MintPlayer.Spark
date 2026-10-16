import { parse } from 'yaml';
import type { InterfaceDiff, LookupRecord } from '../contracts/types';

/**
 * R15: the action.yml interface diff for an Actions major. S12 showed the real v3->v4/v4->v5 pairs
 * remove nothing and only move the node runtime, so this is necessary but not sufficient; majors stay
 * over-threshold whatever it says. The C# GitHubWorkflowValidator reads the same fixture pairs and must
 * reach the same verdict.
 */
export interface ActionInterface {
  inputs: Map<string, { required: boolean; hasDefault: boolean }>;
  outputs: Set<string>;
  using: string | null;
}

export function parseActionYml(text: string): ActionInterface {
  const doc = (parse(text) ?? {}) as {
    inputs?: Record<string, { required?: boolean | string; default?: unknown } | null>;
    outputs?: Record<string, unknown>;
    runs?: { using?: string };
  };
  const inputs = new Map<string, { required: boolean; hasDefault: boolean }>();
  for (const [name, spec] of Object.entries(doc.inputs ?? {})) {
    const required = spec?.required === true || spec?.required === 'true';
    inputs.set(name, { required, hasDefault: spec?.default !== undefined && spec?.default !== null && spec?.default !== '' });
  }
  return { inputs, outputs: new Set(Object.keys(doc.outputs ?? {})), using: doc.runs?.using ?? null };
}

export interface ActionUsage {
  file: string;
  action: string;
  ref: string;
  withKeys: string[];
  outputsRead: string[];
}

interface Step {
  uses?: unknown;
  with?: Record<string, unknown>;
  id?: unknown;
}

function usagesInSteps(file: string, steps: Step[], scopeText: string): ActionUsage[] {
  const out: ActionUsage[] = [];
  for (const step of steps ?? []) {
    if (typeof step?.uses !== 'string') continue;
    const m = step.uses.match(/^([^@\s]+)@(\S+)$/);
    if (!m || m[1].startsWith('./') || m[1].startsWith('docker://')) continue;
    const id = typeof step.id === 'string' ? step.id : null;
    const outputsRead = new Set<string>();
    if (id) {
      const re = new RegExp(`steps\\.${id.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&')}\\.outputs\\.([A-Za-z0-9_-]+)`, 'g');
      let o: RegExpExecArray | null;
      while ((o = re.exec(scopeText)) !== null) outputsRead.add(o[1]);
    }
    out.push({ file, action: m[1], ref: m[2], withKeys: Object.keys(step.with ?? {}), outputsRead: [...outputsRead].sort() });
  }
  return out;
}

/** Every remote `uses:` step in a workflow (jobs.*.steps) or a composite action (runs.steps). */
export function findUsages(file: string, text: string): ActionUsage[] {
  let doc: { jobs?: Record<string, { steps?: Step[] }>; runs?: { steps?: Step[] } };
  try {
    doc = (parse(text) ?? {}) as typeof doc;
  } catch {
    return [];
  }
  const out: ActionUsage[] = [];
  for (const job of Object.values(doc.jobs ?? {})) {
    if (job?.steps) out.push(...usagesInSteps(file, job.steps, JSON.stringify(job)));
  }
  if (doc.runs?.steps) out.push(...usagesInSteps(file, doc.runs.steps, JSON.stringify(doc)));
  return out;
}

export function diffInterface(action: string, from: string, to: string, before: ActionInterface | null, after: ActionInterface | null, usages: ActionUsage[]): InterfaceDiff {
  const files = [...new Set(usages.map((u) => u.file))].sort();
  if (!before || !after) {
    return {
      action, from, to, packageFiles: files,
      runtime: { from: before?.using ?? null, to: after?.using ?? null },
      addedInputs: [], removedInputs: [], newRequiredInputs: [], removedOutputs: [], blockers: [], verdict: 'unknown',
    };
  }
  const addedInputs = [...after.inputs.keys()].filter((k) => !before.inputs.has(k)).sort();
  const removedInputs = [...before.inputs.keys()].filter((k) => !after.inputs.has(k)).sort();
  const newRequiredInputs = [...after.inputs.entries()]
    .filter(([k, v]) => v.required && !v.hasDefault && !(before.inputs.get(k)?.required && !before.inputs.get(k)?.hasDefault))
    .map(([k]) => k)
    .sort();
  const removedOutputs = [...before.outputs].filter((k) => !after.outputs.has(k)).sort();
  const blockers: InterfaceDiff['blockers'] = [];
  for (const u of usages) {
    for (const key of u.withKeys) if (!after.inputs.has(key)) blockers.push({ kind: 'removed-input-used', name: key, file: u.file });
    for (const key of newRequiredInputs) if (!u.withKeys.includes(key)) blockers.push({ kind: 'new-required-input', name: key, file: u.file });
    for (const out of u.outputsRead) if (!after.outputs.has(out)) blockers.push({ kind: 'missing-output-read', name: out, file: u.file });
  }
  if (before.using !== after.using) {
    for (const file of files) blockers.push({ kind: 'runtime-changed', name: `${before.using ?? '?'} -> ${after.using ?? '?'}`, file });
  }
  const incompatible = blockers.some((b) => b.kind !== 'runtime-changed');
  return {
    action, from, to, packageFiles: files,
    runtime: { from: before.using, to: after.using },
    addedInputs, removedInputs, newRequiredInputs, removedOutputs, blockers,
    verdict: incompatible ? 'incompatible' : 'compatible',
  };
}

export type ActionYmlFetcher = (action: string, ref: string) => Promise<string | null>;

/** `owner/repo[/path]` at `ref`, via the contents API with the raw media type (S12), action.yml then action.yaml. */
export function githubActionYmlFetcher(token: string | undefined, apiUrl = 'https://api.github.com'): ActionYmlFetcher {
  return async (action, ref) => {
    const [owner, repo, ...rest] = action.split('/');
    const dir = rest.length ? `${rest.join('/')}/` : '';
    for (const name of ['action.yml', 'action.yaml']) {
      const url = `${apiUrl}/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/contents/${dir}${name}?ref=${encodeURIComponent(ref)}`;
      const res = await fetch(url, {
        headers: {
          accept: 'application/vnd.github.raw',
          'user-agent': 'mintplayer-dependency-update-action',
          ...(token ? { authorization: `Bearer ${token}` } : {}),
        },
      });
      if (res.ok) return res.text();
    }
    return null;
  };
}

/** Interface diffs for every Actions record with a major candidate. */
export async function interfaceDiffsFor(records: LookupRecord[], usagesByFile: Map<string, ActionUsage[]>, fetchYml: ActionYmlFetcher): Promise<InterfaceDiff[]> {
  const out: InterfaceDiff[] = [];
  const seen = new Set<string>();
  for (const r of records) {
    if (r.ecosystem !== 'github-actions') continue;
    const from = r.currentDigest ?? r.currentValue;
    if (!from) continue;
    for (const c of r.candidates) {
      if (c.updateType !== 'major') continue;
      const to = c.digest ?? c.value;
      if (!to) continue;
      const key = `${r.depName}@${from}->${to}`;
      if (seen.has(key)) continue;
      seen.add(key);
      const usages = [...usagesByFile.values()].flat().filter((u) => u.action.toLowerCase() === r.depName.toLowerCase());
      const [beforeText, afterText] = await Promise.all([fetchYml(r.depName, from).catch(() => null), fetchYml(r.depName, to).catch(() => null)]);
      out.push(
        diffInterface(
          r.depName,
          r.currentValue ?? from,
          c.value ?? to,
          beforeText ? parseActionYml(beforeText) : null,
          afterText ? parseActionYml(afterText) : null,
          usages,
        ),
      );
    }
  }
  return out;
}
