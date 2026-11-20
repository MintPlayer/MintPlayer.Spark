# @mintplayer/constraint-solver

A deterministic 2D geometric constraint solver in TypeScript, for sketch and drawing editors. It solves live while
you drag, finds conflicting constraints by name, and tells you how free every point still is. No dependencies, no
WebAssembly.

## Solve

```ts
import { solve } from '@mintplayer/constraint-solver';

const result = solve({
  points: {
    a: { x: 0, y: 0, fixed: true },
    b: { x: 10, y: 0.4 },
    c: { x: 4, y: 3 },
  },
  constraints: [
    { id: 'base', type: 'horizontal', a: 'a', b: 'b' },
    { id: 'ac', type: 'distance', a: 'a', b: 'c', value: 6 },
    { id: 'bc', type: 'distance', a: 'b', b: 'c', value: 8 },
  ],
});

result.status;   // 'solved' | 'conflict'
result.points;   // every point, moved as little as possible
result.conflicts; // ids of contradicting constraints
```

## Drag

```ts
solve(problem, { drag: { point: 'c', target: pointerPosition } });
```

The dragged point goes to the target, and the points constrained to it follow. The hard constraints always win.

## Constraints

| type | meaning |
|---|---|
| `coincident` | `a` and `b` are the same position |
| `horizontal`, `vertical` | the line `a`–`b` is horizontal or vertical |
| `distance` | `|a − b| = value` |
| `equalLength` | the lines `l1` and `l2` are equally long |
| `parallel`, `perpendicular`, `angle` | the angle from `l1` to `l2` is 0, 90° or `value` (radians) |
| `pointOnLine` | `p` lies on the (infinite) line through `line` |
| `pointOnCircle` | `p` lies on `circle` (its `center` and a `rim` point) |
| `midpoint` | `m` is the midpoint of `line` |
| `tangentLineCircle` | `line` touches `circle`, on the side the center starts on |
| `tangentCircles` | `c1` and `c2` touch, `external` or `internal` |
| `symmetric` | `a` and `b` mirror each other in `axis` |

## Analyze

```ts
import { analyze } from '@mintplayer/constraint-solver';

const { dof, pointDof, redundant, conflicts } = analyze(problem);
pointDof['c']; // 0: fully determined, 1: moves along a curve, 2: free
```

`redundant` lists constraints implied by earlier ones. `conflicts` lists constraints that contradict each other at
the current positions.

## How it works

- **Residuals.** Every constraint becomes one or two residual equations with analytic gradients.
- **Independent groups.** Groups of points that share no constraint are solved separately.
- **The step.** Each group takes damped, weighted, minimum-norm Gauss–Newton steps (Levenberg–Marquardt):
  `dx = M Jᵀ (J M Jᵀ + λI)⁻¹ (−r)`.
- **Moving as little as possible.** Minimum-norm steps keep the solution nearest to the current positions, so a
  shape never flips to its mirror image.
- **Dragging.** A dragged point gets almost no mobility, so the others do the moving.
- **Conflicts and redundancy.** These come from Gram–Schmidt over the Jacobian's rows, in constraint order. The
  constraint added later is the one blamed.

The same input always gives the same output.
