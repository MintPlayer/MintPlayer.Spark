// S1's performance bar (docs/iconic_PRD.md): a drag frame must solve within 16 ms (60 fps).
// Run from this package's folder: npm run bench. It prints timings and asserts nothing about time, so it can never
// fail on a slow machine; the numbers go into the PRD.
import { solve } from '../src/solver';
import type { Constraint, Problem, SolverPoint } from '../src/types';

/** 200 points in ONE connected group with mixed constraints: the worst case, since groups solve separately. */
function linkage(): Problem {
  const points: Record<string, SolverPoint> = {};
  const constraints: Constraint[] = [];
  const n = 200;
  for (let i = 0; i < n; i++) points[`p${i}`] = { x: (i % 20) * 10 + Math.sin(i) * 0.3, y: Math.floor(i / 20) * 10 + Math.cos(i) * 0.3, fixed: i === 0 };
  // A zig-zag through every point keeps it one group, alternating constraint kinds.
  for (let i = 1; i < n; i++) {
    const a = `p${i - 1}`, b = `p${i}`;
    if (i % 20 === 0) constraints.push({ id: `v${i}`, type: 'vertical', a: `p${i - 20}`, b });
    else if (i % 3 === 0) constraints.push({ id: `h${i}`, type: 'horizontal', a, b });
    else if (i % 3 === 1) constraints.push({ id: `d${i}`, type: 'distance', a, b, value: 10 });
    else constraints.push({ id: `e${i}`, type: 'equalLength', l1: [`p${i - 2}`, a], l2: [a, b] });
  }
  return { points, constraints };
}

/** 40 independent rectangles (160 points, 160 constraints): what a typical icon looks like. */
function rectangles(): Problem {
  const points: Record<string, SolverPoint> = {};
  const constraints: Constraint[] = [];
  for (let r = 0; r < 40; r++) {
    const ox = (r % 8) * 30, oy = Math.floor(r / 8) * 30;
    const id = (k: string) => `r${r}${k}`;
    points[id('a')] = { x: ox, y: oy + 0.2 };
    points[id('b')] = { x: ox + 20.3, y: oy };
    points[id('c')] = { x: ox + 20, y: oy + 15.4 };
    points[id('d')] = { x: ox - 0.1, y: oy + 15 };
    constraints.push(
      { id: id('h1'), type: 'horizontal', a: id('a'), b: id('b') },
      { id: id('h2'), type: 'horizontal', a: id('d'), b: id('c') },
      { id: id('v1'), type: 'vertical', a: id('a'), b: id('d') },
      { id: id('v2'), type: 'vertical', a: id('b'), b: id('c') },
    );
  }
  return { points, constraints };
}

function measure(name: string, problem: Problem, point: string, frames = 200) {
  const base = problem.points[point];
  const times: number[] = [];
  let worstStatus = 'solved';
  for (let f = 0; f < frames; f++) {
    const t = f * 0.05;
    const start = performance.now();
    const r = solve(problem, { drag: { point, target: { x: base.x + Math.sin(t) * 5, y: base.y + Math.cos(t) * 5 } } });
    times.push(performance.now() - start);
    if (r.status !== 'solved') worstStatus = r.status;
  }
  times.sort((a, b) => a - b);
  const pct = (p: number) => times[Math.min(times.length - 1, Math.floor(p * times.length))].toFixed(2);
  console.log(`${name}: ${problem.constraints.length} constraints, median ${pct(0.5)} ms, p95 ${pct(0.95)} ms, max ${pct(1)} ms, status ${worstStatus}`);
}

it('drag frame timings', () => {
  measure('linkage (200 points, one group)', linkage(), 'p199');
  measure('rectangles (160 points, 40 groups)', rectangles(), 'r39c');
});
