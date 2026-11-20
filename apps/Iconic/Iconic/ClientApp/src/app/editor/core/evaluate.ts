import { analyze, solve, type Analysis, type Constraint, type Problem, type SolverPoint } from '@mintplayer/constraint-solver';
import { intersectPolylines, flatten, pointAlong, vec, type Vec } from './geometry';
import type { Easing, Id, Scene, SceneConstraint } from './model';
import { measureShape, shapePath, type Positions, type TextOutliner } from './shapes';

/** The scene's constraints in the solver's terms. Ids stay the scene's, so conflicts name scene constraints. */
export function toSolverConstraint(id: Id, c: SceneConstraint): Constraint {
  switch (c.type) {
    case 'pointOnCircle': return { id, type: 'pointOnCircle', p: c.p, circle: { center: c.center, rim: c.rim } };
    case 'tangent': return { id, type: 'tangentLineCircle', line: c.line, circle: { center: c.center, rim: c.rim } };
    case 'tangentCircles': return { id, type: 'tangentCircles', c1: { center: c.c1[0], rim: c.c1[1] }, c2: { center: c.c2[0], rim: c.c2[1] }, kind: c.kind };
    case 'equalRadius': return { id, type: 'equalLength', l1: c.c1, l2: c.c2 };
    default: return { id, ...c } as Constraint;
  }
}

export interface Evaluated {
  positions: Positions;
  status: 'solved' | 'conflict';
  conflicts: Id[];
}

export interface EvaluateOptions {
  /** The frame to evaluate, or null for the rest pose (no animation applied). */
  frame?: number | null;
  /** A point being dragged and where the pointer is. */
  drag?: { point: Id; target: Vec };
  /** Extra points to hold still (the rest of a multi-selection during a drag). */
  hold?: Iterable<Id>;
  text?: TextOutliner;
}

/**
 * Positions for a frame (D19): start from the rest pose, pin the animated and derived points, solve the rest, then
 * derive again from the result. Every frame starts from the same rest pose, so scrubbing, playing and exporting give
 * the same picture (S7).
 */
export function evaluate(scene: Scene, options: EvaluateOptions = {}): Evaluated {
  const rest: Positions = {};
  for (const [id, p] of Object.entries(scene.points)) rest[id] = vec(p.x, p.y);

  const pinned = new Set<Id>(options.hold ?? []);
  const start: Positions = { ...rest };
  if (options.frame != null) {
    for (const [id, pos] of Object.entries(animatedPositions(scene, options.frame, rest, options.text))) {
      start[id] = pos;
      pinned.add(id);
    }
  }
  deriveInto(scene, start, options.text);

  const points: Record<Id, SolverPoint> = {};
  for (const [id, p] of Object.entries(scene.points)) {
    const at = start[id];
    points[id] = { x: at.x, y: at.y, fixed: !!p.fixed || !!p.derive || pinned.has(id) };
  }
  const constraints = Object.entries(scene.constraints).map(([id, c]) => toSolverConstraint(id, c));
  const problem: Problem = { points, constraints };

  let positions: Positions = start;
  let status: Evaluated['status'] = 'solved';
  let conflicts: Id[] = [];
  if (constraints.length > 0 || options.drag) {
    const drag = options.drag && points[options.drag.point] && !points[options.drag.point].fixed ? options.drag : undefined;
    const result = solve(problem, { drag });
    positions = { ...start };
    for (const [id, p] of Object.entries(result.points)) positions[id] = vec(p.x, p.y);
    status = result.status;
    conflicts = result.conflicts;
    // Without constraints the solver leaves a free point where it is; a drag moves it outright.
    if (drag && constraints.length === 0) positions[drag.point] = drag.target;
  }
  deriveInto(scene, positions, options.text);
  return { positions, status, conflicts };
}

/** Degrees of freedom per point and the conflicting/redundant constraints, for the editor's point colours (D14). */
export function analyzeScene(scene: Scene): Analysis {
  const points: Record<Id, SolverPoint> = {};
  for (const [id, p] of Object.entries(scene.points)) points[id] = { x: p.x, y: p.y, fixed: !!p.fixed || !!p.derive };
  return analyze({ points, constraints: Object.entries(scene.constraints).map(([id, c]) => toSolverConstraint(id, c)) });
}

/** Computes derived points (D15) in place, in dependency order: a derived point may sit on a shape using another one. */
export function deriveInto(scene: Scene, positions: Positions, text?: TextOutliner): void {
  const derived = Object.entries(scene.points).filter(([, p]) => p.derive);
  // Two passes settle one level of dependency between derived points, which covers what the editor lets you build.
  for (let pass = 0; pass < 2; pass++) {
    for (const [id, p] of derived) {
      const d = p.derive!;
      switch (d.type) {
        case 'midpoint': {
          const a = positions[d.a], b = positions[d.b];
          if (a && b) positions[id] = vec((a.x + b.x) / 2, (a.y + b.y) / 2);
          break;
        }
        case 'onPath': {
          const shape = scene.shapes[d.shape];
          if (shape) positions[id] = pointAlong(measureShape(shape, positions, text), d.t);
          break;
        }
        case 'intersect': {
          const s1 = scene.shapes[d.s1], s2 = scene.shapes[d.s2];
          if (!s1 || !s2) break;
          const hits = intersections(shapePath(s1, positions, text), shapePath(s2, positions, text), s1.type === 'line', s2.type === 'line');
          // When the shapes stop crossing, the point stays where it was last seen.
          if (hits[d.index]) positions[id] = hits[d.index];
          break;
        }
      }
    }
  }
}

/** Intersections of two outlines. A line counts as infinite, as in a construction drawing. */
export function intersections(a: ReturnType<typeof shapePath>, b: ReturnType<typeof shapePath>, extendA = false, extendB = false): Vec[] {
  const polyA = flatten(a, 0.05).flatMap(s => [s.closed ? [...s.points, s.points[0]] : s.points]);
  const polyB = flatten(b, 0.05).flatMap(s => [s.closed ? [...s.points, s.points[0]] : s.points]);
  const extend = (poly: Vec[]): Vec[] => {
    if (poly.length !== 2) return poly;
    const [p, q] = poly;
    const dx = q.x - p.x, dy = q.y - p.y;
    const k = 1e4 / Math.max(1e-9, Math.hypot(dx, dy));
    return [vec(p.x - dx * k, p.y - dy * k), vec(q.x + dx * k, q.y + dy * k)];
  };
  const out: Vec[] = [];
  for (const pa of polyA) for (const pb of polyB) out.push(...intersectPolylines(extendA ? extend(pa) : pa, extendB ? extend(pb) : pb));
  return out;
}

export function ease(easing: Easing, t: number): number {
  switch (easing) {
    case 'linear': return t;
    case 'easeIn': return t * t * t;
    case 'easeOut': return 1 - (1 - t) ** 3;
    case 'easeInOut': return t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2;
    case 'step': return t < 1 ? 0 : 1;
  }
}

/** Interpolates keyed values; the easing of the key a segment starts at shapes that segment. Null without keys. */
export function interpolate<K extends { frame: number; easing: Easing }>(keys: readonly K[], frame: number, value: (k: K) => number[]): number[] | null {
  if (keys.length === 0) return null;
  const sorted = [...keys].sort((a, b) => a.frame - b.frame);
  if (frame <= sorted[0].frame) return value(sorted[0]);
  const last = sorted[sorted.length - 1];
  if (frame >= last.frame) return value(last);
  for (let i = 1; i < sorted.length; i++) {
    const a = sorted[i - 1], b = sorted[i];
    if (frame <= b.frame) {
      const t = ease(a.easing, (frame - a.frame) / (b.frame - a.frame));
      const va = value(a), vb = value(b);
      return va.map((v, j) => v + (vb[j] - v) * t);
    }
  }
  return value(last);
}

/** Where the animated points are at a frame. Motion paths are measured in the rest pose. */
export function animatedPositions(scene: Scene, frame: number, rest: Positions, text?: TextOutliner): Positions {
  const out: Positions = {};
  for (const [id, track] of Object.entries(scene.animation.tracks)) {
    if (!scene.points[id]) continue;
    if (track.path && track.path.keys.length > 0) {
      const shape = scene.shapes[track.path.shape];
      const t = interpolate(track.path.keys, frame, k => [k.t]);
      if (shape && t) out[id] = pointAlong(measureShape(shape, rest, text), t[0]);
      continue;
    }
    const xy = interpolate(track.keys, frame, k => [k.x, k.y]);
    if (xy) out[id] = vec(xy[0], xy[1]);
  }
  return out;
}

/** A layer's opacity at a frame: its own, times its track's value when it has one. */
export function layerOpacity(scene: Scene, layer: Id, frame: number | null): number {
  const base = scene.layers[layer]?.opacity ?? 1;
  if (frame == null) return base;
  const track = scene.animation.layerTracks[layer];
  const v = track ? interpolate(track.keys, frame, k => [k.value]) : null;
  return v ? base * v[0] : base;
}
