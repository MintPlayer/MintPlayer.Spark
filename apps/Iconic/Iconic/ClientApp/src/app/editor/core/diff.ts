/**
 * The autosave's patch (D21, S5): what changed between the last saved scene and the current one, as set/remove
 * operations on paths. Objects are walked key by key; an array that changed is set whole (layer orders and point
 * lists are short, and an index-wise diff would race with the server's own array semantics).
 */
export type PatchOp = { op: 'set'; path: string[]; value: unknown } | { op: 'remove'; path: string[] };

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

export function diff(before: unknown, after: unknown, path: string[] = [], out: PatchOp[] = []): PatchOp[] {
  if (isObject(before) && isObject(after)) {
    for (const key of Object.keys(before)) {
      if (!(key in after) || after[key] === undefined) {
        if (before[key] !== undefined) out.push({ op: 'remove', path: [...path, key] });
      }
    }
    for (const key of Object.keys(after)) {
      if (after[key] === undefined) continue;
      if (!(key in before) || before[key] === undefined) out.push({ op: 'set', path: [...path, key], value: after[key] });
      else diff(before[key], after[key], [...path, key], out);
    }
    return out;
  }
  if (!deepEqual(before, after)) out.push({ op: 'set', path, value: after });
  return out;
}

export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((v, i) => deepEqual(v, b[i]));
  if (isObject(a) && isObject(b)) {
    const ka = Object.keys(a).filter(k => a[k] !== undefined), kb = Object.keys(b).filter(k => b[k] !== undefined);
    return ka.length === kb.length && ka.every(k => deepEqual(a[k], b[k]));
  }
  return false;
}

/** Applies operations the way the server's patch script does; the tests hold the two to the same result. */
export function applyPatch<T>(doc: T, ops: PatchOp[]): T {
  let root = structuredClone(doc) as unknown;
  for (const op of ops) {
    if (op.path.length === 0) {
      if (op.op === 'set') root = structuredClone(op.value);
      continue;
    }
    let target = root as Record<string, unknown>;
    for (const key of op.path.slice(0, -1)) {
      if (target[key] === null || typeof target[key] !== 'object') target[key] = {};
      target = target[key] as Record<string, unknown>;
    }
    const last = op.path[op.path.length - 1];
    if (op.op === 'set') target[last] = structuredClone(op.value);
    else if (Array.isArray(target)) target.splice(Number(last), 1);
    else delete target[last];
  }
  return root as T;
}
