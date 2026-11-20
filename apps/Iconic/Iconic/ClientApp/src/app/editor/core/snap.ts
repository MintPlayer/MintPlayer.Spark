import { dist, distanceToSegment, flatten, nearestAlong, vec, type Vec } from './geometry';
import type { Id, Scene } from './model';
import { measureShape, shapePath, type Positions, type TextOutliner } from './shapes';

/**
 * Snapping (D4, as in DrawIt): a point, a shape's midpoint or an outline wins in that order; otherwise the grid.
 * Snapping onto a point merges the two (the point is shared); onto a midpoint or outline makes a derived point (D15).
 */
export type Snap =
  | { kind: 'point'; id: Id; at: Vec }
  | { kind: 'midpoint'; a: Id; b: Id; at: Vec }
  | { kind: 'onPath'; shape: Id; t: number; at: Vec }
  | { kind: 'grid'; at: Vec }
  | { kind: 'none'; at: Vec };

export interface SnapOptions {
  /** In design pixels: the screen radius divided by the zoom. */
  radius: number;
  grid: number;
  gridEnabled: boolean;
  /** Points never to snap to (the one being dragged and those moving with it). */
  exclude?: Set<Id>;
  /** Shapes never to snap to (the one being drawn). */
  excludeShapes?: Set<Id>;
  text?: TextOutliner;
  /** Midpoints and outlines too (off while dragging an existing point, where only a merge makes sense). */
  derived?: boolean;
}

export function snap(scene: Scene, positions: Positions, at: Vec, o: SnapOptions): Snap {
  let best: Snap | null = null;
  let bestD = o.radius;
  for (const [id, p] of Object.entries(positions)) {
    if (o.exclude?.has(id) || !scene.points[id]) continue;
    const d = dist(p, at);
    if (d <= bestD) { bestD = d; best = { kind: 'point', id, at: p }; }
  }
  if (best) return best;

  if (o.derived !== false) {
    for (const [id, shape] of Object.entries(scene.shapes)) {
      if (o.excludeShapes?.has(id) || shape.hidden) continue;
      if (shape.type === 'line') {
        const a = positions[shape.a], b = positions[shape.b];
        if (!a || !b) continue;
        const m = vec((a.x + b.x) / 2, (a.y + b.y) / 2);
        const d = dist(m, at);
        if (d <= bestD) { bestD = d; best = { kind: 'midpoint', a: shape.a, b: shape.b, at: m }; }
      }
    }
    if (best) return best;

    for (const [id, shape] of Object.entries(scene.shapes)) {
      if (o.excludeShapes?.has(id) || shape.hidden || shape.type === 'text') continue;
      const m = measureShape(shape, positions, o.text);
      if (m.points.length < 2) continue;
      const hit = nearestAlong(m, at);
      if (hit.distance <= bestD) {
        bestD = hit.distance;
        const t = hit.t;
        best = { kind: 'onPath', shape: id, t, at: pointOn(m, t) };
      }
    }
    if (best) return best;
  }

  if (o.gridEnabled && o.grid > 0) return { kind: 'grid', at: vec(Math.round(at.x / o.grid) * o.grid, Math.round(at.y / o.grid) * o.grid) };
  return { kind: 'none', at };
}

function pointOn(m: ReturnType<typeof measureShape>, t: number): Vec {
  const target = t * m.total;
  for (let i = 1; i < m.points.length; i++) {
    if (m.lengths[i] >= target) {
      const span = m.lengths[i] - m.lengths[i - 1];
      const u = span === 0 ? 0 : (target - m.lengths[i - 1]) / span;
      return vec(m.points[i - 1].x + (m.points[i].x - m.points[i - 1].x) * u, m.points[i - 1].y + (m.points[i].y - m.points[i - 1].y) * u);
    }
  }
  return m.points[m.points.length - 1];
}

/** The topmost shape under a point: inside a filled plane, or within `radius` of an outline. */
export function hitShape(scene: Scene, positions: Positions, at: Vec, radius: number, order: Id[], text?: TextOutliner): Id | null {
  for (const id of order) {
    const shape = scene.shapes[id];
    if (!shape || shape.hidden) continue;
    const layer = scene.layerOrder.find(l => scene.layers[l].shapes.includes(id));
    if (layer && (!scene.layers[layer].visible || scene.layers[layer].locked)) continue;
    const polys = flatten(shapePath(shape, positions, text), 0.5);
    const tolerance = radius + Math.max(0, shape.stroke.width / 2);
    for (const { points, closed } of polys) {
      const pts = closed ? [...points, points[0]] : points;
      for (let i = 1; i < pts.length; i++) if (distanceToSegment(at, pts[i - 1], pts[i]) <= tolerance) return id;
    }
    const filled = shape.fill.type !== 'none' || shape.type === 'text';
    if (filled && polys.some(p => p.closed) && insideAny(polys.filter(p => p.closed), at)) return id;
  }
  return null;
}

function insideAny(polys: { points: Vec[] }[], p: Vec): boolean {
  let inside = false;
  for (const { points } of polys) {
    for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
      const a = points[i], b = points[j];
      if ((a.y > p.y) !== (b.y > p.y) && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
    }
  }
  return inside;
}

/** The nearest point within `radius`, among the given ids. */
export function hitPoint(positions: Positions, at: Vec, radius: number, ids: Iterable<Id>): Id | null {
  let best: Id | null = null;
  let bestD = radius;
  for (const id of ids) {
    const p = positions[id];
    if (!p) continue;
    const d = dist(p, at);
    if (d <= bestD) { bestD = d; best = id; }
  }
  return best;
}
