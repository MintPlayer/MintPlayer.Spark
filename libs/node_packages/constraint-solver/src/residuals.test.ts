import { residualRows } from './residuals';
import type { Constraint, Vec2 } from './types';

// A generic position for every point the constraints below use, chosen so no line is degenerate.
const positions: Record<string, Vec2> = {
  a: { x: 0.3, y: -0.2 },
  b: { x: 4.1, y: 1.7 },
  c: { x: -1.2, y: 2.9 },
  d: { x: 2.4, y: 5.3 },
  e: { x: 6.2, y: -3.1 },
  f: { x: -2.5, y: -4.4 },
};

const all: Constraint[] = [
  { id: 'coincident', type: 'coincident', a: 'a', b: 'b' },
  { id: 'horizontal', type: 'horizontal', a: 'a', b: 'b' },
  { id: 'vertical', type: 'vertical', a: 'a', b: 'b' },
  { id: 'distance', type: 'distance', a: 'a', b: 'b', value: 2 },
  { id: 'equalLength', type: 'equalLength', l1: ['a', 'b'], l2: ['c', 'd'] },
  { id: 'parallel', type: 'parallel', l1: ['a', 'b'], l2: ['c', 'd'] },
  { id: 'perpendicular', type: 'perpendicular', l1: ['a', 'b'], l2: ['c', 'd'] },
  { id: 'angle', type: 'angle', l1: ['a', 'b'], l2: ['c', 'd'], value: 0.7 },
  { id: 'pointOnLine', type: 'pointOnLine', p: 'e', line: ['a', 'b'] },
  { id: 'pointOnCircle', type: 'pointOnCircle', p: 'e', circle: { center: 'a', rim: 'b' } },
  { id: 'midpoint', type: 'midpoint', m: 'e', line: ['a', 'b'] },
  { id: 'tangentLineCircle', type: 'tangentLineCircle', line: ['a', 'b'], circle: { center: 'c', rim: 'd' } },
  { id: 'tangentExternal', type: 'tangentCircles', c1: { center: 'a', rim: 'b' }, c2: { center: 'c', rim: 'd' }, kind: 'external' },
  { id: 'tangentInternal', type: 'tangentCircles', c1: { center: 'a', rim: 'b' }, c2: { center: 'e', rim: 'f' }, kind: 'internal' },
  { id: 'symmetric', type: 'symmetric', a: 'e', b: 'f', axis: ['a', 'b'] },
];

describe('residual gradients', () => {
  // The solver's Jacobian is only as good as these: every analytic gradient must match a central difference.
  it.each(all.map((c) => [c.id, c] as const))('%s matches central differences', (_, constraint) => {
    const start = (id: string) => positions[id];
    const rows = residualRows(constraint, start);
    const h = 1e-6;
    for (const row of rows) {
      const { grads } = row(start);
      for (const id of Object.keys(positions)) {
        for (const axis of ['x', 'y'] as const) {
          const shifted = (delta: number) => (pid: string) =>
            pid === id ? { ...positions[pid], [axis]: positions[pid][axis] + delta } : positions[pid];
          const numeric = (row(shifted(h)).value - row(shifted(-h)).value) / (2 * h);
          const analytic = grads.filter((gr) => gr.point === id).reduce((s, gr) => s + (axis === 'x' ? gr.gx : gr.gy), 0);
          expect(analytic).toBeCloseTo(numeric, 5);
        }
      }
    }
  });
});
