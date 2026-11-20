import { constraintPoints, residualRows, type ResidualRow } from './residuals';
import type { Analysis, Constraint, Problem, SolveOptions, SolveResult, Vec2 } from './types';

/** How willing a point is to move, relative to the others. The dragged point barely moves: the rest follow it. */
const MOBILITY_FREE = 1;
const MOBILITY_DRAGGED = 1e-4;

/** One independent group: the free points and constraints that touch each other, and nothing else. */
interface Component {
  points: string[];
  constraints: Constraint[];
}

/**
 * Splits a problem into groups that share no free point. Fixed points are inputs, so they never join two groups.
 * A constraint that touches only fixed points goes into its own group, so it is still checked.
 * The order is deterministic: by first appearance in `problem.constraints`.
 */
export function components(problem: Problem): Component[] {
  const parent = new Map<string, string>();
  const find = (id: string): string => {
    let root = id;
    while (parent.get(root) !== root) root = parent.get(root)!;
    // Path compression.
    let cur = id;
    while (cur !== root) {
      const next = parent.get(cur)!;
      parent.set(cur, root);
      cur = next;
    }
    return root;
  };
  const free = (id: string) => {
    const p = problem.points[id];
    if (!p) throw new Error(`Constraint refers to unknown point '${id}'.`);
    return !p.fixed;
  };

  for (const id of Object.keys(problem.points)) if (free(id)) parent.set(id, id);
  for (const c of problem.constraints) {
    const ids = constraintPoints(c).filter(free);
    for (let i = 1; i < ids.length; i++) {
      const ra = find(ids[0]), rb = find(ids[i]);
      if (ra !== rb) parent.set(rb, ra);
    }
  }

  const byRoot = new Map<string, Component>();
  const result: Component[] = [];
  for (const c of problem.constraints) {
    const ids = constraintPoints(c).filter(free);
    if (ids.length === 0) {
      result.push({ points: [], constraints: [c] });
      continue;
    }
    const root = find(ids[0]);
    let comp = byRoot.get(root);
    if (!comp) {
      comp = { points: [], constraints: [] };
      byRoot.set(root, comp);
      result.push(comp);
    }
    comp.constraints.push(c);
  }
  // Points in constraint-touched groups; free points touched by nothing are left alone.
  const seen = new Set<string>();
  for (const comp of byRoot.values()) {
    for (const c of comp.constraints) {
      for (const id of constraintPoints(c)) {
        if (free(id) && !seen.has(id)) {
          seen.add(id);
          comp.points.push(id);
        }
      }
    }
  }
  return result;
}

/** A dense row-major Jacobian as sparse rows: each row lists (variable index, derivative). */
interface Linearization {
  values: Float64Array;
  rows: Array<Array<[number, number]>>;
  /** The constraint each row came from. */
  owners: string[];
}

class Group {
  readonly index = new Map<string, number>();
  readonly x: Float64Array;
  readonly rows: ResidualRow[] = [];
  readonly owners: string[] = [];

  constructor(
    readonly comp: Component,
    private readonly fixedPos: (id: string) => Vec2,
    initial: (id: string) => Vec2,
  ) {
    this.x = new Float64Array(comp.points.length * 2);
    comp.points.forEach((id, i) => {
      this.index.set(id, i);
      const p = initial(id);
      this.x[2 * i] = p.x;
      this.x[2 * i + 1] = p.y;
    });
    const at = this.reader(this.x);
    for (const c of comp.constraints) {
      for (const row of residualRows(c, at)) {
        this.rows.push(row);
        this.owners.push(c.id);
      }
    }
  }

  reader(x: Float64Array): (id: string) => Vec2 {
    return (id) => {
      const i = this.index.get(id);
      return i === undefined ? this.fixedPos(id) : { x: x[2 * i], y: x[2 * i + 1] };
    };
  }

  linearize(x: Float64Array): Linearization {
    const at = this.reader(x);
    const values = new Float64Array(this.rows.length);
    const rows: Array<Array<[number, number]>> = [];
    this.rows.forEach((row, r) => {
      const { value, grads } = row(at);
      values[r] = value;
      const entries = new Map<number, number>();
      for (const gr of grads) {
        const i = this.index.get(gr.point);
        if (i === undefined) continue; // A fixed point has no variable.
        entries.set(2 * i, (entries.get(2 * i) ?? 0) + gr.gx);
        entries.set(2 * i + 1, (entries.get(2 * i + 1) ?? 0) + gr.gy);
      }
      rows.push([...entries]);
    });
    return { values, rows, owners: this.owners };
  }

  residuals(x: Float64Array): Float64Array {
    const at = this.reader(x);
    return Float64Array.from(this.rows, (row) => row(at).value);
  }
}

const maxAbs = (v: Float64Array) => v.reduce((m, x) => Math.max(m, Math.abs(x)), 0);
const sumSq = (v: Float64Array) => v.reduce((s, x) => s + x * x, 0);

/**
 * Solves the constraints by projecting the current positions onto them: damped, weighted minimum-norm Gauss–Newton
 * steps, `dx = M Jᵀ (J M Jᵀ + λI)⁻¹ (−r)`. A minimum-norm step moves the points as little as possible, so the solver
 * keeps the solution nearest to what is on screen and never flips a shape to its mirror image. With a drag, the
 * dragged point is first put on its target and given a tiny mobility, so the other points do the moving.
 *
 * Deterministic: the same problem and options always give the same result.
 */
export function solve(problem: Problem, options: SolveOptions = {}): SolveResult {
  const tolerance = options.tolerance ?? 1e-7;
  const maxIterations = options.maxIterations ?? 60;
  const drag = options.drag;
  if (drag && !problem.points[drag.point]) throw new Error(`Drag refers to unknown point '${drag.point}'.`);

  const initial = (id: string): Vec2 => {
    if (drag && drag.point === id && !problem.points[id].fixed) return drag.target;
    const p = problem.points[id];
    return { x: p.x, y: p.y };
  };
  const out: Record<string, Vec2> = {};
  for (const id of Object.keys(problem.points)) out[id] = initial(id);

  let status: SolveResult['status'] = 'solved';
  let residual = 0;
  let iterations = 0;
  const conflicts: string[] = [];

  for (const comp of components(problem)) {
    const group = new Group(comp, initial, initial);
    const mobility = new Float64Array(group.x.length).fill(MOBILITY_FREE);
    const dragged = drag ? group.index.get(drag.point) : undefined;
    if (dragged !== undefined) mobility[2 * dragged] = mobility[2 * dragged + 1] = MOBILITY_DRAGGED;

    const r = project(group, mobility, tolerance, maxIterations);
    iterations = Math.max(iterations, r.iterations);
    if (r.residual > tolerance) {
      status = 'conflict';
      conflicts.push(...analyzeGroup(group, group.linearize(group.x), r.residual > tolerance).conflicts);
    }
    residual = Math.max(residual, r.residual);
    comp.points.forEach((id, i) => (out[id] = { x: group.x[2 * i], y: group.x[2 * i + 1] }));
  }

  return { status, points: out, conflicts: [...new Set(conflicts)], residual, iterations };
}

function project(group: Group, mobility: Float64Array, tolerance: number, maxIterations: number) {
  const x = group.x;
  let lambda = 1e-9;
  let lin = group.linearize(x);
  let cost = sumSq(lin.values);
  let iterations = 0;

  while (iterations < maxIterations && maxAbs(lin.values) > tolerance) {
    iterations++;
    const m = lin.rows.length;
    const A = normalMatrix(lin.rows, mobility, m);
    let accepted = false;
    // Raise λ until a step lowers the cost; each failed try costs one residual evaluation, not a new Jacobian.
    for (let attempt = 0; attempt < 12 && !accepted; attempt++) {
      const damped = A.slice();
      // λ relative to the matrix's own scale, so the damping means the same at any zoom level.
      const scale = Math.max(1e-12, maxDiag(A, m));
      for (let i = 0; i < m; i++) damped[i * m + i] += lambda * scale;
      const y = choleskySolve(damped, Float64Array.from(lin.values, (v) => -v), m);
      if (!y) {
        lambda = Math.max(lambda * 10, 1e-6);
        continue;
      }
      const trial = x.slice();
      for (let r = 0; r < m; r++) {
        for (const [j, d] of lin.rows[r]) trial[j] += mobility[j] * d * y[r];
      }
      const trialValues = group.residuals(trial);
      const trialCost = sumSq(trialValues);
      if (trialCost < cost || maxAbs(trialValues) <= tolerance) {
        x.set(trial);
        cost = trialCost;
        lambda = Math.max(lambda / 10, 1e-12);
        accepted = true;
      } else {
        lambda *= 10;
      }
    }
    if (!accepted) break; // Stuck: no step lowers the cost, so the constraints contradict each other here.
    lin = group.linearize(x);
  }
  return { iterations, residual: maxAbs(lin.values) };
}

/** J M Jᵀ, built from the sparse rows: only pairs of rows that share a variable contribute. */
function normalMatrix(rows: Array<Array<[number, number]>>, mobility: Float64Array, m: number): Float64Array {
  const A = new Float64Array(m * m);
  const byVar = new Map<number, Array<[number, number]>>();
  rows.forEach((row, r) => {
    for (const [j, d] of row) {
      let list = byVar.get(j);
      if (!list) byVar.set(j, (list = []));
      list.push([r, d]);
    }
  });
  for (const [j, list] of byVar) {
    const w = mobility[j];
    for (const [r1, d1] of list) {
      for (const [r2, d2] of list) A[r1 * m + r2] += d1 * w * d2;
    }
  }
  return A;
}

function maxDiag(A: Float64Array, m: number): number {
  let best = 0;
  for (let i = 0; i < m; i++) best = Math.max(best, A[i * m + i]);
  return best;
}

/** Solves A y = b for a symmetric positive definite A. Returns undefined when A is not positive definite. */
function choleskySolve(A: Float64Array, b: Float64Array, m: number): Float64Array | undefined {
  const L = new Float64Array(m * m);
  for (let i = 0; i < m; i++) {
    for (let j = 0; j <= i; j++) {
      let s = A[i * m + j];
      for (let k = 0; k < j; k++) s -= L[i * m + k] * L[j * m + k];
      if (i === j) {
        if (s <= 0) return undefined;
        L[i * m + i] = Math.sqrt(s);
      } else {
        L[i * m + j] = s / L[j * m + j];
      }
    }
  }
  const y = new Float64Array(m);
  for (let i = 0; i < m; i++) {
    let s = b[i];
    for (let k = 0; k < i; k++) s -= L[i * m + k] * y[k];
    y[i] = s / L[i * m + i];
  }
  for (let i = m - 1; i >= 0; i--) {
    let s = y[i];
    for (let k = i + 1; k < m; k++) s -= L[k * m + i] * y[k];
    y[i] = s / L[i * m + i];
  }
  return y;
}

/** Relative size below which a Jacobian row counts as a combination of the rows before it. */
const RANK_EPS = 1e-8;

interface GroupAnalysis {
  rank: number;
  /** Orthonormal basis of the row space, one Float64Array of length n per row. */
  basis: Float64Array[];
  redundant: string[];
  conflicts: string[];
}

/**
 * Gram–Schmidt over the Jacobian rows, in constraint order, tracking how each basis vector combines the original rows.
 * A row that reduces to nothing depends on earlier rows: its constraint and the ones it combines are reported, as a
 * conflict when the system is unsolved, or as redundant when it is solved. Later constraints are the ones blamed,
 * which matches the editor: the constraint just added is the one that conflicts.
 */
function analyzeGroup(group: Group, lin: Linearization, unsolved: boolean): GroupAnalysis {
  const n = group.x.length;
  const basis: Float64Array[] = [];
  const combos: Float64Array[] = [];
  const m = lin.rows.length;
  const redundant: string[] = [];
  const conflicts: string[] = [];

  for (let r = 0; r < m; r++) {
    const v = new Float64Array(n);
    for (const [j, d] of lin.rows[r]) v[j] = d;
    const norm0 = Math.sqrt(sumSq(v));
    const combo = new Float64Array(m);
    combo[r] = 1;
    if (norm0 === 0) {
      // A row with no free variable: it reads only fixed points.
      if (Math.abs(lin.values[r]) > RANK_EPS) conflicts.push(lin.owners[r]);
      continue;
    }
    // Two passes of modified Gram–Schmidt keep the basis orthogonal in floating point.
    for (let pass = 0; pass < 2; pass++) {
      basis.forEach((q, k) => {
        let dot = 0;
        for (let j = 0; j < n; j++) dot += q[j] * v[j];
        if (dot === 0) return;
        for (let j = 0; j < n; j++) v[j] -= dot * q[j];
        const ck = combos[k];
        for (let i = 0; i < m; i++) combo[i] -= dot * ck[i];
      });
    }
    const norm = Math.sqrt(sumSq(v));
    if (norm <= RANK_EPS * norm0) {
      if (unsolved) {
        // A conflict names every constraint in the contradiction, so the user sees which ones to choose between.
        conflicts.push(lin.owners[r]);
        for (let i = 0; i < r; i++) if (Math.abs(combo[i]) > 1e-6) conflicts.push(lin.owners[i]);
      } else {
        // Redundancy blames only the later constraint: removing it changes nothing.
        redundant.push(lin.owners[r]);
      }
      continue;
    }
    for (let j = 0; j < n; j++) v[j] /= norm;
    for (let i = 0; i < m; i++) combo[i] /= norm;
    basis.push(v);
    combos.push(combo);
  }
  return {
    rank: basis.length,
    basis,
    redundant: [...new Set(redundant)].filter((id) => !conflicts.includes(id)),
    conflicts: [...new Set(conflicts)],
  };
}

/**
 * Degrees of freedom and constraint health at the current positions, without moving anything.
 * A point's degrees of freedom are how many independent directions it can still move in (0, 1 or 2), from the
 * projection of its two coordinates onto the Jacobian's null space.
 */
export function analyze(problem: Problem): Analysis {
  const pos = (id: string): Vec2 => problem.points[id];
  const pointDof: Analysis['pointDof'] = {};
  let dof = 0;
  const redundant: string[] = [];
  const conflicts: string[] = [];
  const grouped = new Set<string>();

  for (const comp of components(problem)) {
    const group = new Group(comp, pos, pos);
    const lin = group.linearize(group.x);
    const unsolved = maxAbs(lin.values) > 1e-6;
    const a = analyzeGroup(group, lin, unsolved);
    redundant.push(...a.redundant);
    conflicts.push(...a.conflicts);
    dof += group.x.length - a.rank;
    comp.points.forEach((id, i) => {
      grouped.add(id);
      // B = I₂ − Qᵀ Q restricted to this point's two columns; its rank is the point's freedom.
      let bxx = 1, bxy = 0, byy = 1;
      for (const q of a.basis) {
        const qx = q[2 * i], qy = q[2 * i + 1];
        bxx -= qx * qx;
        bxy -= qx * qy;
        byy -= qy * qy;
      }
      const tr = bxx + byy, det = bxx * byy - bxy * bxy;
      const disc = Math.sqrt(Math.max(0, (tr * tr) / 4 - det));
      const big = tr / 2 + disc, small = tr / 2 - disc;
      const eps = 1e-6;
      pointDof[id] = (big <= eps ? 0 : small <= eps ? 1 : 2) as 0 | 1 | 2;
    });
  }
  for (const [id, p] of Object.entries(problem.points)) {
    if (p.fixed) pointDof[id] = 0;
    else if (!grouped.has(id)) {
      pointDof[id] = 2;
      dof += 2;
    }
  }
  return { dof, pointDof, redundant: [...new Set(redundant)], conflicts: [...new Set(conflicts)] };
}
