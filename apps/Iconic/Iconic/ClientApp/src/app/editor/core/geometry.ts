/** Plain 2D geometry: vectors, path commands, Bézier evaluation, intersections and sampling. */

export interface Vec { x: number; y: number }

export const vec = (x: number, y: number): Vec => ({ x, y });
export const add = (a: Vec, b: Vec): Vec => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
export const mul = (a: Vec, k: number): Vec => ({ x: a.x * k, y: a.y * k });
export const dot = (a: Vec, b: Vec): number => a.x * b.x + a.y * b.y;
export const cross = (a: Vec, b: Vec): number => a.x * b.y - a.y * b.x;
export const len = (a: Vec): number => Math.hypot(a.x, a.y);
export const dist = (a: Vec, b: Vec): number => Math.hypot(a.x - b.x, a.y - b.y);
export const lerp = (a: Vec, b: Vec, t: number): Vec => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });

/**
 * A path in the four commands every target understands: canvas, SVG, hit testing and sampling. Arcs and ellipses
 * become cubic Béziers (at most 90° each, error under 0.03 % of the radius), so there is one curve type to handle.
 */
export type PathCommand =
  | { c: 'M'; p: Vec }
  | { c: 'L'; p: Vec }
  | { c: 'C'; c1: Vec; c2: Vec; p: Vec }
  | { c: 'Z' };

/** Cubic segments approximating the arc from angle a0 to a1 (radians, either direction) on an ellipse. */
export function arcToCubics(center: Vec, rx: number, ry: number, a0: number, a1: number): PathCommand[] {
  const out: PathCommand[] = [];
  const sweep = a1 - a0;
  const n = Math.max(1, Math.ceil(Math.abs(sweep) / (Math.PI / 2) - 1e-9));
  const step = sweep / n;
  const k = (4 / 3) * Math.tan(step / 4);
  let a = a0;
  for (let i = 0; i < n; i++) {
    const b = a + step;
    const p0 = vec(center.x + rx * Math.cos(a), center.y + ry * Math.sin(a));
    const p3 = vec(center.x + rx * Math.cos(b), center.y + ry * Math.sin(b));
    const c1 = vec(p0.x - k * rx * Math.sin(a), p0.y + k * ry * Math.cos(a));
    const c2 = vec(p3.x + k * rx * Math.sin(b), p3.y - k * ry * Math.cos(b));
    out.push({ c: 'C', c1, c2, p: p3 });
    a = b;
  }
  return out;
}

export function ellipsePath(center: Vec, rx: number, ry: number): PathCommand[] {
  return [{ c: 'M', p: vec(center.x + rx, center.y) }, ...arcToCubics(center, rx, ry, 0, Math.PI * 2), { c: 'Z' }];
}

export function cubicAt(p0: Vec, c1: Vec, c2: Vec, p3: Vec, t: number): Vec {
  const u = 1 - t;
  const a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
  return vec(a * p0.x + b * c1.x + c * c2.x + d * p3.x, a * p0.y + b * c1.y + c * c2.y + d * p3.y);
}

/** Flattens a path into polylines (one per subpath) with about `tolerance` design pixels of error. */
export function flatten(path: PathCommand[], tolerance = 0.25): { points: Vec[]; closed: boolean }[] {
  const out: { points: Vec[]; closed: boolean }[] = [];
  let current: Vec[] | null = null;
  let last = vec(0, 0);
  let start = vec(0, 0);
  for (const cmd of path) {
    switch (cmd.c) {
      case 'M':
        current = [cmd.p];
        out.push({ points: current, closed: false });
        last = start = cmd.p;
        break;
      case 'L':
        if (!current) { current = [last]; out.push({ points: current, closed: false }); }
        current.push(cmd.p);
        last = cmd.p;
        break;
      case 'C': {
        if (!current) { current = [last]; out.push({ points: current, closed: false }); }
        const estimate = dist(last, cmd.c1) + dist(cmd.c1, cmd.c2) + dist(cmd.c2, cmd.p);
        const n = Math.min(256, Math.max(2, Math.ceil(Math.sqrt(estimate / tolerance) * 1.5)));
        for (let i = 1; i <= n; i++) current.push(cubicAt(last, cmd.c1, cmd.c2, cmd.p, i / n));
        last = cmd.p;
        break;
      }
      case 'Z':
        if (current) {
          out[out.length - 1].closed = true;
          current = null;
          last = start;
        }
        break;
    }
  }
  return out;
}

/** A polyline measured for walking it by arc length. */
export interface Measured {
  points: Vec[];
  lengths: number[];
  total: number;
}

export function measure(points: Vec[], closed: boolean): Measured {
  const pts = closed && points.length > 1 ? [...points, points[0]] : points;
  const lengths = [0];
  for (let i = 1; i < pts.length; i++) lengths.push(lengths[i - 1] + dist(pts[i - 1], pts[i]));
  return { points: pts, lengths, total: lengths[lengths.length - 1] ?? 0 };
}

/** The point at fraction `t` (0..1) of the length. */
export function pointAlong(m: Measured, t: number): Vec {
  if (m.points.length === 0) return vec(0, 0);
  if (m.points.length === 1 || m.total === 0) return m.points[0];
  const target = Math.min(1, Math.max(0, t)) * m.total;
  let lo = 0, hi = m.lengths.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (m.lengths[mid] <= target) lo = mid; else hi = mid;
  }
  const span = m.lengths[hi] - m.lengths[lo];
  return lerp(m.points[lo], m.points[hi], span === 0 ? 0 : (target - m.lengths[lo]) / span);
}

/** The fraction of the length closest to `p`, for dropping a point onto a path. */
export function nearestAlong(m: Measured, p: Vec): { t: number; distance: number } {
  let best = { t: 0, distance: Infinity };
  for (let i = 1; i < m.points.length; i++) {
    const a = m.points[i - 1], b = m.points[i];
    const ab = sub(b, a);
    const l2 = dot(ab, ab);
    const u = l2 === 0 ? 0 : Math.min(1, Math.max(0, dot(sub(p, a), ab) / l2));
    const q = add(a, mul(ab, u));
    const d = dist(p, q);
    if (d < best.distance) best = { t: m.total === 0 ? 0 : (m.lengths[i - 1] + u * Math.sqrt(l2)) / m.total, distance: d };
  }
  return best;
}

export function distanceToSegment(p: Vec, a: Vec, b: Vec): number {
  const ab = sub(b, a);
  const l2 = dot(ab, ab);
  const u = l2 === 0 ? 0 : Math.min(1, Math.max(0, dot(sub(p, a), ab) / l2));
  return dist(p, add(a, mul(ab, u)));
}

/** Even-odd containment of a point in flattened subpaths. */
export function contains(polys: { points: Vec[] }[], p: Vec): boolean {
  let inside = false;
  for (const { points } of polys) {
    for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
      const a = points[i], b = points[j];
      if ((a.y > p.y) !== (b.y > p.y) && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
    }
  }
  return inside;
}

/** Intersections of two polylines, in order along the first. */
export function intersectPolylines(a: Vec[], b: Vec[]): Vec[] {
  const hits: { t: number; p: Vec }[] = [];
  for (let i = 1; i < a.length; i++) {
    const p = a[i - 1], r = sub(a[i], p);
    for (let j = 1; j < b.length; j++) {
      const q = b[j - 1], s = sub(b[j], q);
      const denom = cross(r, s);
      if (Math.abs(denom) < 1e-12) continue;
      const qp = sub(q, p);
      const t = cross(qp, s) / denom;
      const u = cross(qp, r) / denom;
      if (t >= 0 && t <= 1 && u >= 0 && u <= 1) hits.push({ t: i - 1 + t, p: add(p, mul(r, t)) });
    }
  }
  hits.sort((x, y) => x.t - y.t);
  // Neighbouring segments meet at their shared vertex: drop the duplicate.
  const out: Vec[] = [];
  for (const h of hits) if (!out.some(o => dist(o, h.p) < 1e-6)) out.push(h.p);
  return out;
}

export interface Bounds { minX: number; minY: number; maxX: number; maxY: number }

export function boundsOf(points: Vec[]): Bounds | null {
  if (points.length === 0) return null;
  const b = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity };
  for (const p of points) {
    b.minX = Math.min(b.minX, p.x); b.minY = Math.min(b.minY, p.y);
    b.maxX = Math.max(b.maxX, p.x); b.maxY = Math.max(b.maxY, p.y);
  }
  return b;
}

/** Applies a scale to every coordinate of a path (design pixels to output pixels). */
export function scalePath(path: PathCommand[], sx: number, sy: number): PathCommand[] {
  const s = (p: Vec) => vec(p.x * sx, p.y * sy);
  return path.map(cmd => {
    switch (cmd.c) {
      case 'M': return { c: 'M', p: s(cmd.p) };
      case 'L': return { c: 'L', p: s(cmd.p) };
      case 'C': return { c: 'C', c1: s(cmd.c1), c2: s(cmd.c2), p: s(cmd.p) };
      case 'Z': return cmd;
    }
  });
}

export function tracePath(ctx: CanvasRenderingContext2D | Path2D, path: PathCommand[]): void {
  for (const cmd of path) {
    switch (cmd.c) {
      case 'M': ctx.moveTo(cmd.p.x, cmd.p.y); break;
      case 'L': ctx.lineTo(cmd.p.x, cmd.p.y); break;
      case 'C': ctx.bezierCurveTo(cmd.c1.x, cmd.c1.y, cmd.c2.x, cmd.c2.y, cmd.p.x, cmd.p.y); break;
      case 'Z': ctx.closePath(); break;
    }
  }
}

const n = (v: number) => (Math.round(v * 1000) / 1000).toString();

export function svgPathData(path: PathCommand[]): string {
  return path.map(cmd => {
    switch (cmd.c) {
      case 'M': return `M${n(cmd.p.x)} ${n(cmd.p.y)}`;
      case 'L': return `L${n(cmd.p.x)} ${n(cmd.p.y)}`;
      case 'C': return `C${n(cmd.c1.x)} ${n(cmd.c1.y)} ${n(cmd.c2.x)} ${n(cmd.c2.y)} ${n(cmd.p.x)} ${n(cmd.p.y)}`;
      case 'Z': return 'Z';
    }
  }).join('');
}
