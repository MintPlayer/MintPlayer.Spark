import { analyze, components, solve } from './solver';
import type { Constraint, Problem, SolverPoint, Vec2 } from './types';

const P = (x: number, y: number, fixed = false): SolverPoint => ({ x, y, fixed });
const dist = (a: Vec2, b: Vec2) => Math.hypot(a.x - b.x, a.y - b.y);
const cross = (a: Vec2, b: Vec2, c: Vec2, d: Vec2) => (b.x - a.x) * (d.y - c.y) - (b.y - a.y) * (d.x - c.x);
const dot = (a: Vec2, b: Vec2, c: Vec2, d: Vec2) => (b.x - a.x) * (d.x - c.x) + (b.y - a.y) * (d.y - c.y);

describe('solve', () => {
  it('leaves a problem without constraints untouched', () => {
    const r = solve({ points: { a: P(1, 2) }, constraints: [] });
    expect(r).toMatchObject({ status: 'solved', points: { a: { x: 1, y: 2 } }, conflicts: [] });
  });

  it('makes a nearly horizontal line horizontal, moving both ends half way', () => {
    const r = solve({ points: { a: P(0, 0), b: P(10, 1) }, constraints: [{ id: 'h', type: 'horizontal', a: 'a', b: 'b' }] });
    expect(r.status).toBe('solved');
    // Minimum-norm: each end moves 0.5, rather than one end moving 1.
    expect(r.points['a'].y).toBeCloseTo(0.5, 6);
    expect(r.points['b'].y).toBeCloseTo(0.5, 6);
  });

  it('moves only free points when the other end is fixed', () => {
    const r = solve({ points: { a: P(0, 0, true), b: P(10, 1) }, constraints: [{ id: 'h', type: 'horizontal', a: 'a', b: 'b' }] });
    expect(r.points['a']).toEqual({ x: 0, y: 0 });
    expect(r.points['b'].y).toBeCloseTo(0, 6);
  });

  it('solves a rectangle: four corners, perpendicular and parallel sides', () => {
    const problem: Problem = {
      points: { a: P(0, 0, true), b: P(10, 0.5), c: P(10.4, 6), d: P(-0.3, 5.7) },
      constraints: [
        { id: 'h1', type: 'horizontal', a: 'a', b: 'b' },
        { id: 'h2', type: 'horizontal', a: 'd', b: 'c' },
        { id: 'v1', type: 'vertical', a: 'a', b: 'd' },
        { id: 'v2', type: 'vertical', a: 'b', b: 'c' },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('solved');
    const { a, b, c, d } = r.points;
    expect(b.y).toBeCloseTo(a.y, 7);
    expect(c.y).toBeCloseTo(d.y, 7);
    expect(d.x).toBeCloseTo(a.x, 7);
    expect(c.x).toBeCloseTo(b.x, 7);
  });

  it('keeps the solution nearest to the start, so a triangle never flips to its mirror image', () => {
    // A triangle with fixed side lengths has two solutions mirrored about a-b; the start sits above the base.
    const problem: Problem = {
      points: { a: P(0, 0, true), b: P(10, 0, true), c: P(4, 3) },
      constraints: [
        { id: 'ac', type: 'distance', a: 'a', b: 'c', value: 6 },
        { id: 'bc', type: 'distance', a: 'b', b: 'c', value: 8 },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('solved');
    expect(r.points['c'].y).toBeGreaterThan(0);
    expect(dist(r.points['a'], r.points['c'])).toBeCloseTo(6, 7);
    expect(dist(r.points['b'], r.points['c'])).toBeCloseTo(8, 7);
  });

  it('drags a point: it lands on the target and the constrained neighbour follows', () => {
    const problem: Problem = {
      points: { a: P(0, 0), b: P(10, 0) },
      constraints: [{ id: 'len', type: 'distance', a: 'a', b: 'b', value: 10 }],
    };
    const r = solve(problem, { drag: { point: 'b', target: { x: 10, y: 5 } } });
    expect(r.status).toBe('solved');
    expect(r.points['b'].x).toBeCloseTo(10, 2);
    expect(r.points['b'].y).toBeCloseTo(5, 2);
    expect(dist(r.points['a'], r.points['b'])).toBeCloseTo(10, 7);
  });

  it('drags a point without constraints straight to the target', () => {
    const r = solve({ points: { a: P(0, 0) }, constraints: [] }, { drag: { point: 'a', target: { x: 3, y: 4 } } });
    expect(r.points['a']).toEqual({ x: 3, y: 4 });
  });

  it('never moves a fixed point that is dragged', () => {
    const r = solve({ points: { a: P(0, 0, true) }, constraints: [] }, { drag: { point: 'a', target: { x: 3, y: 4 } } });
    expect(r.points['a']).toEqual({ x: 0, y: 0 });
  });

  it('reports conflicting constraints by id, and still returns in bounded time', () => {
    const problem: Problem = {
      points: { a: P(0, 0, true), b: P(5, 0) },
      constraints: [
        { id: 'd5', type: 'distance', a: 'a', b: 'b', value: 5 },
        { id: 'd7', type: 'distance', a: 'a', b: 'b', value: 7 },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('conflict');
    expect(r.conflicts.sort()).toEqual(['d5', 'd7']);
  });

  it('reports a constraint over fixed points only that is violated', () => {
    const r = solve({
      points: { a: P(0, 0, true), b: P(5, 1, true) },
      constraints: [{ id: 'h', type: 'horizontal', a: 'a', b: 'b' }],
    });
    expect(r.status).toBe('conflict');
    expect(r.conflicts).toEqual(['h']);
  });

  it('solves tangent, on-circle, midpoint and symmetric together', () => {
    const problem: Problem = {
      points: {
        c: P(0, 0, true), r: P(5, 0, true), // circle radius 5
        l1: P(-10, 5.5), l2: P(10, 6.2), // a line just above the circle
        p: P(3, 3.9), // a point near the circle
        m: P(0.4, 6.4), // the line's midpoint
        s1: P(-3, -6), s2: P(3.4, -6.3), // symmetric about the y axis
        y1: P(0, -10, true), y2: P(0, 10, true),
      },
      constraints: [
        { id: 'tan', type: 'tangentLineCircle', line: ['l1', 'l2'], circle: { center: 'c', rim: 'r' } },
        { id: 'on', type: 'pointOnCircle', p: 'p', circle: { center: 'c', rim: 'r' } },
        { id: 'mid', type: 'midpoint', m: 'm', line: ['l1', 'l2'] },
        { id: 'sym', type: 'symmetric', a: 's1', b: 's2', axis: ['y1', 'y2'] },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('solved');
    const { c, l1, l2, p, m, s1, s2 } = r.points;
    const lineDist = Math.abs(cross(l1, l2, l1, c)) / dist(l1, l2);
    expect(lineDist).toBeCloseTo(5, 6);
    expect(dist(c, p)).toBeCloseTo(5, 6);
    expect(m.x).toBeCloseTo((l1.x + l2.x) / 2, 6);
    expect(s1.x).toBeCloseTo(-s2.x, 6);
    expect(s1.y).toBeCloseTo(s2.y, 6);
  });

  it('solves parallel, perpendicular, angle and equal length', () => {
    const problem: Problem = {
      points: { a: P(0, 0, true), b: P(10, 0, true), c: P(0, 3), d: P(9, 4), e: P(1, 1), f: P(1.5, 8), g: P(5, 5), h: P(8, 9) },
      constraints: [
        { id: 'par', type: 'parallel', l1: ['a', 'b'], l2: ['c', 'd'] },
        { id: 'perp', type: 'perpendicular', l1: ['a', 'b'], l2: ['e', 'f'] },
        { id: 'ang', type: 'angle', l1: ['a', 'b'], l2: ['g', 'h'], value: Math.PI / 4 },
        { id: 'eq', type: 'equalLength', l1: ['a', 'b'], l2: ['c', 'd'] },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('solved');
    const { a, b, c, d, e, f, g, h } = r.points;
    expect(cross(a, b, c, d)).toBeCloseTo(0, 6);
    expect(dot(a, b, e, f)).toBeCloseTo(0, 6);
    expect(Math.atan2(h.y - g.y, h.x - g.x)).toBeCloseTo(Math.PI / 4, 6);
    expect(dist(c, d)).toBeCloseTo(10, 6);
  });

  it('solves external and internal circle tangency', () => {
    const problem: Problem = {
      points: { c1: P(0, 0, true), r1: P(5, 0, true), c2: P(9, 0), r2: P(9, 3), c3: P(1, 0.5), r3: P(1, 2.5) },
      constraints: [
        { id: 'ext', type: 'tangentCircles', c1: { center: 'c1', rim: 'r1' }, c2: { center: 'c2', rim: 'r2' }, kind: 'external' },
        { id: 'int', type: 'tangentCircles', c1: { center: 'c1', rim: 'r1' }, c2: { center: 'c3', rim: 'r3' }, kind: 'internal' },
      ],
    };
    const r = solve(problem);
    expect(r.status).toBe('solved');
    const { c1, r1, c2, r2, c3, r3 } = r.points;
    expect(dist(c1, c2)).toBeCloseTo(dist(c1, r1) + dist(c2, r2), 6);
    expect(dist(c1, c3)).toBeCloseTo(dist(c1, r1) - dist(c3, r3), 6);
  });

  it('is deterministic', () => {
    const problem = chain(30);
    const one = solve(problem, { drag: { point: 'p29', target: { x: 50, y: 40 } } });
    const two = solve(problem, { drag: { point: 'p29', target: { x: 50, y: 40 } } });
    expect(two).toEqual(one);
  });

  it('throws on a constraint over an unknown point', () => {
    expect(() => solve({ points: {}, constraints: [{ id: 'x', type: 'horizontal', a: 'a', b: 'b' }] })).toThrow(/unknown point 'a'/);
    expect(() => solve({ points: {}, constraints: [] }, { drag: { point: 'z', target: { x: 0, y: 0 } } })).toThrow(/unknown point 'z'/);
  });
});

describe('degenerate cases', () => {
  // Each must end in a defined state, solved or reported, and never hang or produce NaN.
  const cases: Array<[string, Problem]> = [
    ['parallel lines that also coincide', {
      points: { a: P(0, 0, true), b: P(10, 0, true), c: P(2, 0), d: P(8, 0.0001) },
      constraints: [
        { id: 'par', type: 'parallel', l1: ['a', 'b'], l2: ['c', 'd'] },
        { id: 'on', type: 'pointOnLine', p: 'c', line: ['a', 'b'] },
      ],
    }],
    ['a zero-length segment in an angle', {
      points: { a: P(0, 0, true), b: P(0, 0), c: P(1, 1), d: P(3, 2) },
      constraints: [{ id: 'ang', type: 'perpendicular', l1: ['a', 'b'], l2: ['c', 'd'] }],
    }],
    ['a distance between coincident points', {
      points: { a: P(1, 1), b: P(1, 1) },
      constraints: [{ id: 'd', type: 'distance', a: 'a', b: 'b', value: 3 }],
    }],
    ['a tangent to a circle of radius 0', {
      points: { a: P(-5, 1), b: P(5, 1), c: P(0, 0, true), r: P(0, 0) },
      constraints: [{ id: 'tan', type: 'tangentLineCircle', line: ['a', 'b'], circle: { center: 'c', rim: 'r' } }],
    }],
    ['a near-singular Jacobian (three nearly collinear constraints)', {
      points: { a: P(0, 0, true), b: P(10, 1e-9), c: P(20, -1e-9) },
      constraints: [
        { id: 'h1', type: 'horizontal', a: 'a', b: 'b' },
        { id: 'h2', type: 'horizontal', a: 'b', b: 'c' },
        { id: 'h3', type: 'horizontal', a: 'a', b: 'c' },
      ],
    }],
    ['a zero-length symmetry axis', {
      points: { a: P(-1, 0), b: P(1, 0), p: P(0, 0, true), q: P(0, 0, true) },
      constraints: [{ id: 'sym', type: 'symmetric', a: 'a', b: 'b', axis: ['p', 'q'] }],
    }],
  ];

  it.each(cases)('%s', (_, problem) => {
    const r = solve(problem);
    for (const p of Object.values(r.points)) {
      expect(Number.isFinite(p.x)).toBe(true);
      expect(Number.isFinite(p.y)).toBe(true);
    }
    expect(['solved', 'conflict']).toContain(r.status);
    if (r.status === 'conflict') expect(r.conflicts.length).toBeGreaterThan(0);
  });

  it('solves the redundant horizontal triangle and reports it as redundant, not as a conflict', () => {
    const [, problem] = cases[4];
    expect(solve(problem).status).toBe('solved');
    const a = analyze(problem);
    expect(a.conflicts).toEqual([]);
    expect(a.redundant).toContain('h3');
  });
});

describe('analyze', () => {
  it('counts the freedom of each point', () => {
    const problem: Problem = {
      points: { a: P(0, 0, true), b: P(10, 0), c: P(3, 3), lone: P(7, 7) },
      constraints: [
        { id: 'len', type: 'distance', a: 'a', b: 'b', value: 10 }, // b moves on a circle: 1
        { id: 'cb', type: 'coincident', a: 'c', b: 'c' }, // c is unconstrained in effect: 2
      ],
    };
    const a = analyze(problem);
    expect(a.pointDof).toEqual({ a: 0, b: 1, c: 2, lone: 2 });
    expect(a.dof).toBe(1 + 2 + 2);
  });

  it('finds a fully determined point', () => {
    const problem: Problem = {
      points: { a: P(0, 0, true), x: P(10, 0, true), b: P(4, 3) },
      constraints: [
        { id: 'len', type: 'distance', a: 'a', b: 'b', value: 5 },
        { id: 'h', type: 'horizontal', a: 'a', b: 'b' },
        // Implied by the horizontal: redundant, and blamed because it comes later.
        { id: 'par', type: 'parallel', l1: ['a', 'b'], l2: ['a', 'x'] },
      ],
    };
    // Put b where all hold, then ask.
    const solved = solve(problem);
    expect(solved.status).toBe('solved');
    const a = analyze({ ...problem, points: { ...problem.points, b: { ...solved.points['b'] } } });
    expect(a.pointDof['b']).toBe(0);
    expect(a.redundant).toEqual(['par']);
    expect(a.conflicts).toEqual([]);
  });

  it('names conflicts at positions that violate them', () => {
    const a = analyze({
      points: { a: P(0, 0, true), b: P(5, 0) },
      constraints: [
        { id: 'd5', type: 'distance', a: 'a', b: 'b', value: 5 },
        { id: 'd7', type: 'distance', a: 'a', b: 'b', value: 7 },
      ],
    });
    expect(a.conflicts.sort()).toEqual(['d5', 'd7']);
  });
});

describe('components', () => {
  it('splits independent groups and never joins two through a fixed point', () => {
    const problem: Problem = {
      points: { f: P(0, 0, true), a: P(1, 0), b: P(2, 0), c: P(3, 0), d: P(4, 0) },
      constraints: [
        { id: 'fa', type: 'horizontal', a: 'f', b: 'a' },
        { id: 'fc', type: 'horizontal', a: 'f', b: 'c' },
        { id: 'ab', type: 'horizontal', a: 'a', b: 'b' },
        { id: 'cd', type: 'horizontal', a: 'c', b: 'd' },
      ],
    };
    expect(components(problem).map((c) => c.constraints.map((k) => k.id))).toEqual([['fa', 'ab'], ['fc', 'cd']]);
  });
});

/** A chain of n points joined by fixed-length links, the first one fixed: a typical drag benchmark shape. */
export function chain(n: number): Problem {
  const points: Record<string, SolverPoint> = {};
  const constraints: Constraint[] = [];
  for (let i = 0; i < n; i++) points[`p${i}`] = P(i * 2, Math.sin(i) * 0.5, i === 0);
  for (let i = 1; i < n; i++) {
    constraints.push({ id: `len${i}`, type: 'distance', a: `p${i - 1}`, b: `p${i}`, value: 2 });
  }
  return { points, constraints };
}
