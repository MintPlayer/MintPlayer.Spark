import type { Constraint, Vec2 } from './types';

/** The gradient of one residual with respect to one point: d r / d (x, y). */
export interface PointGradient {
  point: string;
  gx: number;
  gy: number;
}

/** One scalar equation `value(x) = 0` with its gradient. */
export interface ResidualValue {
  value: number;
  grads: PointGradient[];
}

/** One residual row of a constraint, with its constants already captured. */
export type ResidualRow = (pos: (id: string) => Vec2) => ResidualValue;

/** Lengths below this count as zero, so a degenerate segment never divides by zero. */
const EPS = 1e-9;

const len = (x: number, y: number) => Math.hypot(x, y);
const safe = (l: number) => (l < EPS ? EPS : l);

/** The unit vector of (x, y); a zero vector gets (1, 0), so a degenerate distance still has a gradient. */
function unit(x: number, y: number): [number, number] {
  const l = len(x, y);
  return l < EPS ? [1, 0] : [x / l, y / l];
}

/** The ids of every point a constraint reads. */
export function constraintPoints(c: Constraint): string[] {
  switch (c.type) {
    case 'coincident':
    case 'horizontal':
    case 'vertical':
    case 'distance':
      return [c.a, c.b];
    case 'equalLength':
    case 'parallel':
    case 'perpendicular':
    case 'angle':
      return [...c.l1, ...c.l2];
    case 'pointOnLine':
      return [c.p, ...c.line];
    case 'pointOnCircle':
      return [c.p, c.circle.center, c.circle.rim];
    case 'midpoint':
      return [c.m, ...c.line];
    case 'tangentLineCircle':
      return [...c.line, c.circle.center, c.circle.rim];
    case 'tangentCircles':
      return [c.c1.center, c.c1.rim, c.c2.center, c.c2.rim];
    case 'symmetric':
      return [c.a, c.b, ...c.axis];
  }
}

/**
 * Turns a constraint into residual rows. Scale factors (line lengths, which side of a line a circle sits on) are
 * read once, at the positions `start`, and stay constant during the solve. Two things follow from that: every row is
 * an exact function with an exact gradient, and at a solution the rows equal the normalized forms (sin of an angle,
 * distance to a line), so Newton converges quadratically there.
 */
export function residualRows(c: Constraint, start: (id: string) => Vec2): ResidualRow[] {
  switch (c.type) {
    case 'coincident':
      return [
        (p) => ({ value: p(c.a).x - p(c.b).x, grads: [g(c.a, 1, 0), g(c.b, -1, 0)] }),
        (p) => ({ value: p(c.a).y - p(c.b).y, grads: [g(c.a, 0, 1), g(c.b, 0, -1)] }),
      ];
    case 'horizontal':
      return [(p) => ({ value: p(c.a).y - p(c.b).y, grads: [g(c.a, 0, 1), g(c.b, 0, -1)] })];
    case 'vertical':
      return [(p) => ({ value: p(c.a).x - p(c.b).x, grads: [g(c.a, 1, 0), g(c.b, -1, 0)] })];
    case 'distance':
      return [
        (p) => {
          const a = p(c.a), b = p(c.b);
          const [ux, uy] = unit(a.x - b.x, a.y - b.y);
          return { value: len(a.x - b.x, a.y - b.y) - c.value, grads: [g(c.a, ux, uy), g(c.b, -ux, -uy)] };
        },
      ];
    case 'equalLength':
      return [
        (p) => {
          const [a, b] = c.l1, [d, e] = c.l2;
          const pa = p(a), pb = p(b), pd = p(d), pe = p(e);
          const [u1x, u1y] = unit(pa.x - pb.x, pa.y - pb.y);
          const [u2x, u2y] = unit(pd.x - pe.x, pd.y - pe.y);
          return {
            value: len(pa.x - pb.x, pa.y - pb.y) - len(pd.x - pe.x, pd.y - pe.y),
            grads: [g(a, u1x, u1y), g(b, -u1x, -u1y), g(d, -u2x, -u2y), g(e, u2x, u2y)],
          };
        },
      ];
    case 'parallel':
      return [angleRow(c.l1, c.l2, 0, start)];
    case 'perpendicular':
      return [angleRow(c.l1, c.l2, Math.PI / 2, start)];
    case 'angle':
      return [angleRow(c.l1, c.l2, c.value, start)];
    case 'pointOnLine':
      return [lineDistanceRow(c.p, c.line, start, 1)];
    case 'pointOnCircle':
      return [
        (p) => {
          const pp = p(c.p), pc = p(c.circle.center), pr = p(c.circle.rim);
          const [e1x, e1y] = unit(pp.x - pc.x, pp.y - pc.y);
          const [e2x, e2y] = unit(pr.x - pc.x, pr.y - pc.y);
          return {
            value: len(pp.x - pc.x, pp.y - pc.y) - len(pr.x - pc.x, pr.y - pc.y),
            grads: [g(c.p, e1x, e1y), g(c.circle.center, -e1x + e2x, -e1y + e2y), g(c.circle.rim, -e2x, -e2y)],
          };
        },
      ];
    case 'midpoint': {
      const [a, b] = c.line;
      return [
        (p) => ({ value: p(c.m).x - (p(a).x + p(b).x) / 2, grads: [g(c.m, 1, 0), g(a, -0.5, 0), g(b, -0.5, 0)] }),
        (p) => ({ value: p(c.m).y - (p(a).y + p(b).y) / 2, grads: [g(c.m, 0, 1), g(a, 0, -0.5), g(b, 0, -0.5)] }),
      ];
    }
    case 'tangentLineCircle': {
      // The distance from the center to the line equals the radius, on the side the center starts on.
      const [a, b] = c.line;
      const sa = start(a), sb = start(b), sc = start(c.circle.center);
      const side = (sb.x - sa.x) * (sc.y - sa.y) - (sb.y - sa.y) * (sc.x - sa.x) < 0 ? -1 : 1;
      const dist = lineDistanceRow(c.circle.center, c.line, start, side);
      return [
        (p) => {
          const d = dist(p);
          const pc = p(c.circle.center), pr = p(c.circle.rim);
          const [ex, ey] = unit(pr.x - pc.x, pr.y - pc.y);
          return {
            value: d.value - len(pr.x - pc.x, pr.y - pc.y),
            grads: [...d.grads, g(c.circle.center, ex, ey), g(c.circle.rim, -ex, -ey)],
          };
        },
      ];
    }
    case 'tangentCircles': {
      // External: |c1 - c2| = r1 + r2. Internal: |c1 - c2| = |r1 - r2|, with the sign of r1 - r2 at the start.
      const r1s = len(start(c.c1.rim).x - start(c.c1.center).x, start(c.c1.rim).y - start(c.c1.center).y);
      const r2s = len(start(c.c2.rim).x - start(c.c2.center).x, start(c.c2.rim).y - start(c.c2.center).y);
      const s1 = c.kind === 'external' ? 1 : r1s >= r2s ? 1 : -1;
      const s2 = c.kind === 'external' ? 1 : -s1;
      return [
        (p) => {
          const c1 = p(c.c1.center), q1 = p(c.c1.rim), c2 = p(c.c2.center), q2 = p(c.c2.rim);
          const [dx, dy] = unit(c1.x - c2.x, c1.y - c2.y);
          const [e1x, e1y] = unit(q1.x - c1.x, q1.y - c1.y);
          const [e2x, e2y] = unit(q2.x - c2.x, q2.y - c2.y);
          return {
            value: len(c1.x - c2.x, c1.y - c2.y) - s1 * len(q1.x - c1.x, q1.y - c1.y) - s2 * len(q2.x - c2.x, q2.y - c2.y),
            grads: [
              g(c.c1.center, dx + s1 * e1x, dy + s1 * e1y),
              g(c.c1.rim, -s1 * e1x, -s1 * e1y),
              g(c.c2.center, -dx + s2 * e2x, -dy + s2 * e2y),
              g(c.c2.rim, -s2 * e2x, -s2 * e2y),
            ],
          };
        },
      ];
    }
    case 'symmetric': {
      // The midpoint of a and b lies on the axis, and b - a is perpendicular to it.
      const [p0, q0] = c.axis;
      const L = safe(len(start(q0).x - start(p0).x, start(q0).y - start(p0).y));
      return [
        (p) => {
          const pp = p(p0), pq = p(q0), pa = p(c.a), pb = p(c.b);
          const ux = pq.x - pp.x, uy = pq.y - pp.y;
          const wx = (pa.x + pb.x) / 2 - pp.x, wy = (pa.y + pb.y) / 2 - pp.y;
          // r = cross(u, w) / L; d/du = (wy, -wx) / L, d/dw = (-uy, ux) / L.
          const dux = wy / L, duy = -wx / L, dwx = -uy / L, dwy = ux / L;
          return {
            value: (ux * wy - uy * wx) / L,
            grads: [g(q0, dux, duy), g(p0, -dux - dwx, -duy - dwy), g(c.a, dwx / 2, dwy / 2), g(c.b, dwx / 2, dwy / 2)],
          };
        },
        (p) => {
          const pp = p(p0), pq = p(q0), pa = p(c.a), pb = p(c.b);
          const ux = pq.x - pp.x, uy = pq.y - pp.y, vx = pb.x - pa.x, vy = pb.y - pa.y;
          return {
            value: (ux * vx + uy * vy) / L,
            grads: [g(q0, vx / L, vy / L), g(p0, -vx / L, -vy / L), g(c.b, ux / L, uy / L), g(c.a, -ux / L, -uy / L)],
          };
        },
      ];
    }
  }
}

function g(point: string, gx: number, gy: number): PointGradient {
  return { point, gx, gy };
}

/**
 * sin(θ − θ0), where θ is the angle from l1 to l2, scaled by the lengths at the start:
 * (cross(u, v)·cos θ0 − dot(u, v)·sin θ0) / (|u₀|·|v₀|).
 */
function angleRow(l1: readonly [string, string], l2: readonly [string, string], theta0: number, start: (id: string) => Vec2): ResidualRow {
  const [a, b] = l1, [d, e] = l2;
  const s = 1 / (safe(len(start(b).x - start(a).x, start(b).y - start(a).y)) * safe(len(start(e).x - start(d).x, start(e).y - start(d).y)));
  const cos = Math.cos(theta0), sin = Math.sin(theta0);
  return (p) => {
    const pa = p(a), pb = p(b), pd = p(d), pe = p(e);
    const ux = pb.x - pa.x, uy = pb.y - pa.y, vx = pe.x - pd.x, vy = pe.y - pd.y;
    const cross = ux * vy - uy * vx, dot = ux * vx + uy * vy;
    const gux = s * (cos * vy - sin * vx), guy = s * (-cos * vx - sin * vy);
    const gvx = s * (-cos * uy - sin * ux), gvy = s * (cos * ux - sin * uy);
    return {
      value: s * (cross * cos - dot * sin),
      grads: [g(b, gux, guy), g(a, -gux, -guy), g(e, gvx, gvy), g(d, -gvx, -gvy)],
    };
  };
}

/**
 * side · cross(b − a, p − a) / |b − a|: the exact signed distance from p to the line through a and b. Unlike the
 * angle rows, this one cannot keep the start length as its scale: the tangent row compares it with a radius, so it
 * must be a true distance at the solution, not just zero there.
 */
function lineDistanceRow(pt: string, line: readonly [string, string], _start: (id: string) => Vec2, side: number): ResidualRow {
  const [a, b] = line;
  return (p) => {
    const pa = p(a), pb = p(b), pp = p(pt);
    const ux = pb.x - pa.x, uy = pb.y - pa.y, wx = pp.x - pa.x, wy = pp.y - pa.y;
    const L = safe(len(ux, uy));
    const cross = ux * wy - uy * wx;
    // d/dw = (−uy, ux)/L; d/du = (wy, −wx)/L − cross·u/L³.
    const gpx = (side * -uy) / L, gpy = (side * ux) / L;
    const k = cross / (L * L * L);
    const gbx = side * (wy / L - k * ux), gby = side * (-wx / L - k * uy);
    return {
      value: (side * cross) / L,
      grads: [g(pt, gpx, gpy), g(b, gbx, gby), g(a, -gpx - gbx, -gpy - gby)],
    };
  };
}
