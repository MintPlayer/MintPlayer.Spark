/** A 2D point position. */
export interface Vec2 {
  x: number;
  y: number;
}

/** A point the solver may move, unless it is fixed. */
export interface SolverPoint {
  x: number;
  y: number;
  /** A fixed point is an input: the solver reads it and never moves it. */
  fixed?: boolean;
}

/** Two point ids: a segment, or the direction of a line through them. */
export type Line = readonly [string, string];

/** A circle as its center point and one point on its rim, so the radius is `|rim - center|`. */
export interface Circle {
  center: string;
  rim: string;
}

/**
 * A geometric constraint between points. Every constraint carries an `id`, which conflict reports name.
 * Angles are in radians, measured counter-clockwise from the first line to the second.
 */
export type Constraint =
  | { id: string; type: 'coincident'; a: string; b: string }
  | { id: string; type: 'horizontal'; a: string; b: string }
  | { id: string; type: 'vertical'; a: string; b: string }
  | { id: string; type: 'distance'; a: string; b: string; value: number }
  | { id: string; type: 'equalLength'; l1: Line; l2: Line }
  | { id: string; type: 'parallel'; l1: Line; l2: Line }
  | { id: string; type: 'perpendicular'; l1: Line; l2: Line }
  | { id: string; type: 'angle'; l1: Line; l2: Line; value: number }
  | { id: string; type: 'pointOnLine'; p: string; line: Line }
  | { id: string; type: 'pointOnCircle'; p: string; circle: Circle }
  | { id: string; type: 'midpoint'; m: string; line: Line }
  | { id: string; type: 'tangentLineCircle'; line: Line; circle: Circle }
  | { id: string; type: 'tangentCircles'; c1: Circle; c2: Circle; kind: 'external' | 'internal' }
  | { id: string; type: 'symmetric'; a: string; b: string; axis: Line };

export type ConstraintType = Constraint['type'];

/** The input of a solve: points by id and the constraints over them. */
export interface Problem {
  points: Readonly<Record<string, SolverPoint>>;
  constraints: readonly Constraint[];
}

/** Moves one point toward a target, as a mouse drag does. The hard constraints always win. */
export interface Drag {
  point: string;
  target: Vec2;
}

export interface SolveOptions {
  drag?: Drag;
  /** Largest absolute residual accepted as solved, in the problem's length units. Default 1e-7. */
  tolerance?: number;
  /** Iteration cap per independent group. Default 60. */
  maxIterations?: number;
}

export type SolveStatus =
  /** Every constraint is met. */
  | 'solved'
  /** Some constraints cannot all be met; `conflicts` names them. */
  | 'conflict';

export interface SolveResult {
  status: SolveStatus;
  /** Every point of the problem, moved. Fixed points are copied unchanged. */
  points: Record<string, Vec2>;
  /** Ids of constraints that contradict each other (empty when solved). */
  conflicts: string[];
  /** Largest absolute residual left. */
  residual: number;
  iterations: number;
}

export interface Analysis {
  /** Degrees of freedom the whole problem has left. */
  dof: number;
  /** Degrees of freedom per point: 0 (fully determined), 1 (moves along a curve) or 2 (free). */
  pointDof: Record<string, 0 | 1 | 2>;
  /** Constraints implied by earlier ones (consistent duplicates). Not an error, but worth showing. */
  redundant: string[];
  /** Constraints that contradict earlier ones at the current positions, with the constraints they contradict. */
  conflicts: string[];
}
