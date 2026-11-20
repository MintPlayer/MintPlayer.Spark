import { arcToCubics, ellipsePath, flatten, measure, type Measured, type PathCommand, vec, type Vec } from './geometry';
import type { Id, Scene, Shape, TextShape } from './model';

/** Resolved positions of every point in a frame. */
export type Positions = Record<Id, Vec>;

/** Turns text into outlines (text.ts, D23). Absent or still loading: the text has no path yet. */
export interface TextOutliner {
  outline(shape: TextShape, anchor: Vec): PathCommand[] | null;
}

const at = (pos: Positions, id: Id): Vec => pos[id] ?? vec(0, 0);

/** The shape's outline in design pixels, the one source for drawing, SVG, hit testing and motion paths. */
export function shapePath(shape: Shape, pos: Positions, text?: TextOutliner): PathCommand[] {
  switch (shape.type) {
    case 'line':
      return [{ c: 'M', p: at(pos, shape.a) }, { c: 'L', p: at(pos, shape.b) }];
    case 'polyline': {
      if (shape.points.length === 0) return [];
      const out: PathCommand[] = [{ c: 'M', p: at(pos, shape.points[0]) }];
      for (const id of shape.points.slice(1)) out.push({ c: 'L', p: at(pos, id) });
      if (shape.closed) out.push({ c: 'Z' });
      return out;
    }
    case 'curve': {
      const nodes = shape.nodes;
      if (nodes.length === 0) return [];
      const out: PathCommand[] = [{ c: 'M', p: at(pos, nodes[0].p) }];
      const count = shape.closed ? nodes.length : nodes.length - 1;
      for (let i = 0; i < count; i++) {
        const a = nodes[i], b = nodes[(i + 1) % nodes.length];
        const pa = at(pos, a.p), pb = at(pos, b.p);
        if (!a.out && !b.in) out.push({ c: 'L', p: pb });
        else out.push({ c: 'C', c1: a.out ? at(pos, a.out) : pa, c2: b.in ? at(pos, b.in) : pb, p: pb });
      }
      if (shape.closed) out.push({ c: 'Z' });
      return out;
    }
    case 'arc': {
      const c = at(pos, shape.center), s = at(pos, shape.start), e = at(pos, shape.end);
      const r = Math.hypot(s.x - c.x, s.y - c.y);
      const a0 = Math.atan2(s.y - c.y, s.x - c.x);
      let a1 = Math.atan2(e.y - c.y, e.x - c.x);
      // Canvas y points down, so "clockwise on screen" is increasing angle.
      if (shape.ccw) { while (a1 > a0) a1 -= Math.PI * 2; } else { while (a1 < a0) a1 += Math.PI * 2; }
      if (r === 0) return [];
      const out: PathCommand[] = shape.pie ? [{ c: 'M', p: c }, { c: 'L', p: s }] : [{ c: 'M', p: s }];
      out.push(...arcToCubics(c, r, r, a0, a1));
      if (shape.pie) out.push({ c: 'Z' });
      return out;
    }
    case 'circle': {
      const c = at(pos, shape.center), rim = at(pos, shape.rim);
      const r = Math.hypot(rim.x - c.x, rim.y - c.y);
      return r === 0 ? [] : ellipsePath(c, r, r);
    }
    case 'ellipse': {
      const a = at(pos, shape.a), b = at(pos, shape.b);
      const rx = Math.abs(b.x - a.x) / 2, ry = Math.abs(b.y - a.y) / 2;
      return rx === 0 || ry === 0 ? [] : ellipsePath(vec((a.x + b.x) / 2, (a.y + b.y) / 2), rx, ry);
    }
    case 'rect': {
      const a = at(pos, shape.a), b = at(pos, shape.b);
      const x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y);
      const r = Math.max(0, Math.min(shape.radius, (x1 - x0) / 2, (y1 - y0) / 2));
      if (r === 0) {
        return [{ c: 'M', p: vec(x0, y0) }, { c: 'L', p: vec(x1, y0) }, { c: 'L', p: vec(x1, y1) }, { c: 'L', p: vec(x0, y1) }, { c: 'Z' }];
      }
      const H = Math.PI / 2;
      return [
        { c: 'M', p: vec(x0 + r, y0) },
        { c: 'L', p: vec(x1 - r, y0) }, ...arcToCubics(vec(x1 - r, y0 + r), r, r, -H, 0),
        { c: 'L', p: vec(x1, y1 - r) }, ...arcToCubics(vec(x1 - r, y1 - r), r, r, 0, H),
        { c: 'L', p: vec(x0 + r, y1) }, ...arcToCubics(vec(x0 + r, y1 - r), r, r, H, 2 * H),
        { c: 'L', p: vec(x0, y0 + r) }, ...arcToCubics(vec(x0 + r, y0 + r), r, r, 2 * H, 3 * H),
        { c: 'Z' },
      ];
    }
    case 'text':
      return text?.outline(shape, at(pos, shape.anchor)) ?? [];
  }
}

/** A shape as one measured polyline, for derived points on it and motion paths (its first subpath). */
export function measureShape(shape: Shape, pos: Positions, text?: TextOutliner): Measured {
  const sub = flatten(shapePath(shape, pos, text))[0];
  return measure(sub?.points ?? [], sub?.closed ?? false);
}

/** The shapes in drawing order, bottom to top, with their layer. */
export function* shapesInOrder(scene: Scene): Generator<{ id: Id; shape: Shape; layer: Id }> {
  for (const layer of scene.layerOrder) {
    for (const id of scene.layers[layer]?.shapes ?? []) {
      const shape = scene.shapes[id];
      if (shape) yield { id, shape, layer };
    }
  }
}
