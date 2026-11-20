import { shapesUsing } from './core/edit';
import type { Vec } from './core/geometry';
import type { Id, Scene, SceneConstraint } from './core/model';
import type { Positions } from './core/shapes';

/** What a selection offers the constraints panel: the segments (two points) and circles (centre and rim) in it. */
export interface SelectionParts {
  points: Id[];
  segments: [Id, Id][];
  circles: [Id, Id][];
}

export function selectionParts(scene: Scene, shapes: Id[], points: Id[]): SelectionParts {
  const segments: [Id, Id][] = [];
  const circles: [Id, Id][] = [];
  for (const id of shapes) {
    const s = scene.shapes[id];
    if (!s) continue;
    if (s.type === 'line') segments.push([s.a, s.b]);
    else if (s.type === 'polyline' && s.points.length === 2) segments.push([s.points[0], s.points[1]]);
    else if (s.type === 'circle') circles.push([s.center, s.rim]);
    else if (s.type === 'arc') circles.push([s.center, s.start]);
  }
  return { points: points.filter(p => scene.points[p]), segments, circles };
}

export interface ConstraintOption {
  label: string;
  make: () => SceneConstraint;
}

const length = (pos: Positions, [a, b]: [Id, Id]) => Math.hypot(pos[b].x - pos[a].x, pos[b].y - pos[a].y);
const direction = (pos: Positions, [a, b]: [Id, Id]): Vec => ({ x: pos[b].x - pos[a].x, y: pos[b].y - pos[a].y });

/** Signed angle from l1 to l2 in radians, as the solver measures it. */
function angleBetween(pos: Positions, l1: [Id, Id], l2: [Id, Id]): number {
  const u = direction(pos, l1), v = direction(pos, l2);
  return Math.atan2(u.x * v.y - u.y * v.x, u.x * v.x + u.y * v.y);
}

/**
 * The constraints that make sense for what is selected (D14). Values (a distance, an angle) start at what the drawing
 * has now, so adding one never jumps the drawing; the panel edits them afterwards.
 */
export function applicableConstraints(parts: SelectionParts, pos: Positions): ConstraintOption[] {
  const { points: pts, segments: segs, circles } = parts;
  const out: ConstraintOption[] = [];
  const pair: [Id, Id] | null = pts.length === 2 && segs.length === 0 ? [pts[0], pts[1]] : segs.length === 1 && pts.length === 0 && circles.length === 0 ? segs[0] : null;
  if (pair && pos[pair[0]] && pos[pair[1]]) {
    const [a, b] = pair;
    out.push({ label: 'Horizontal', make: () => ({ type: 'horizontal', a, b }) });
    out.push({ label: 'Vertical', make: () => ({ type: 'vertical', a, b }) });
    out.push({ label: 'Distance', make: () => ({ type: 'distance', a, b, value: round(length(pos, pair)) }) });
  }
  if (segs.length === 2 && pts.length === 0) {
    const [l1, l2] = segs;
    out.push({ label: 'Equal length', make: () => ({ type: 'equalLength', l1, l2 }) });
    out.push({ label: 'Parallel', make: () => ({ type: 'parallel', l1, l2 }) });
    out.push({ label: 'Perpendicular', make: () => ({ type: 'perpendicular', l1, l2 }) });
    out.push({ label: 'Angle', make: () => ({ type: 'angle', l1, l2, value: angleBetween(pos, l1, l2) }) });
  }
  if (pts.length === 1 && segs.length === 1) {
    const [p] = pts, [line] = segs;
    if (!line.includes(p)) {
      out.push({ label: 'Point on line', make: () => ({ type: 'pointOnLine', p, line }) });
      out.push({ label: 'Midpoint', make: () => ({ type: 'midpoint', m: p, line }) });
    }
  }
  if (pts.length === 2 && segs.length === 1) {
    const [a, b] = pts, [axis] = segs;
    out.push({ label: 'Symmetric', make: () => ({ type: 'symmetric', a, b, axis }) });
  }
  if (pts.length === 1 && circles.length === 1 && segs.length === 0) {
    const [p] = pts, [[center, rim]] = circles;
    if (p !== center && p !== rim) out.push({ label: 'Point on circle', make: () => ({ type: 'pointOnCircle', p, center, rim }) });
  }
  if (segs.length === 1 && circles.length === 1 && pts.length === 0) {
    const [line] = segs, [[center, rim]] = circles;
    out.push({ label: 'Tangent', make: () => ({ type: 'tangent', line, center, rim }) });
  }
  if (circles.length === 2 && segs.length === 0 && pts.length === 0) {
    const [c1, c2] = circles;
    out.push({ label: 'Equal radius', make: () => ({ type: 'equalRadius', c1, c2 }) });
    out.push({ label: 'Tangent outside', make: () => ({ type: 'tangentCircles', c1, c2, kind: 'external' }) });
    out.push({ label: 'Tangent inside', make: () => ({ type: 'tangentCircles', c1, c2, kind: 'internal' }) });
  }
  return out;
}

export const CONSTRAINT_LABELS: Record<SceneConstraint['type'], string> = {
  horizontal: 'Horizontal', vertical: 'Vertical', distance: 'Distance', equalLength: 'Equal length', parallel: 'Parallel',
  perpendicular: 'Perpendicular', angle: 'Angle', pointOnLine: 'Point on line', pointOnCircle: 'Point on circle', midpoint: 'Midpoint',
  tangent: 'Tangent', symmetric: 'Symmetric', equalRadius: 'Equal radius', tangentCircles: 'Tangent circles',
};

/** Points shared by more than one shape, among the selected ones: what Split can act on (D15). */
export function sharedPoints(scene: Scene, points: Id[]): Id[] {
  return points.filter(p => shapesUsing(scene, p).length > 1);
}

const round = (v: number) => Math.round(v * 100) / 100;
