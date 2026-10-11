import type { CommitGraphLane, CommitGraphRow } from '../../services/browse.service';

/**
 * The pure half of the commit-graph panel (#504, PRD §4.5): turns the wire rows into the line
 * segments of one small SVG per row. No Angular and no DOM, so the geometry is unit-tested on its own.
 *
 * The wire shape (D27) stores per row only what the server's lane layout decided:
 * - `lane`/`colour`: the node's column and colour;
 * - `up`: the columns whose lines converge into the node from above (the node's own column among them
 *   when a child expected it; empty for a tip);
 * - `down`: one `[column, colour]` per parent edge leaving the node downwards.
 *
 * Pass-throughs are not stored. A page carries `top`, the lanes active above its first row, and every
 * further row's top is the previous row's bottom: `(top − up) ∪ down`. Lanes never change column (the
 * JoinLeft layout bends only in a node's own bottom half), so a line that passes through is straight.
 */

/** Horizontal distance between two lane centres, px. */
export const LANE_WIDTH = 14;
/** Fixed row height of the graph table, px. The SVG spans it exactly so lines meet across rows. */
export const ROW_HEIGHT = 32;

/** One drawable line segment of a row. `key` names it for the hover-path highlight. */
export interface GraphSegment {
  /** `t{col}` pass-through, `u{col}` converging from above, `d{i}` the i-th parent edge. */
  key: string;
  d: string;
  /** Palette index, already reduced modulo the palette size. */
  colour: number;
}

/** A wire row plus its derived geometry. */
export interface GraphRowView {
  row: CommitGraphRow;
  /** Absolute row index in the walk. */
  index: number;
  segments: GraphSegment[];
  /** Node centre. */
  cx: number;
  cy: number;
  /** Palette index of the node. */
  colour: number;
  /** One past the highest column this row touches; drives the overflow marker. */
  width: number;
  isMerge: boolean;
}

export const PALETTE_SIZE = 8;

export function laneX(col: number): number {
  return col * LANE_WIDTH + LANE_WIDTH / 2;
}

/** The bottom-edge lanes of a row from its top-edge lanes: `(top − up) ∪ down`, as a column → colour map. */
export function lanesBelow(top: ReadonlyMap<number, number>, row: Pick<CommitGraphRow, 'up' | 'down'>): Map<number, number> {
  const below = new Map(top);
  for (const u of row.up) below.delete(u);
  for (const [col, colour] of row.down) below.set(col, colour);
  return below;
}

/** The segments of one row, given the lanes active at its top edge. */
export function rowSegments(top: ReadonlyMap<number, number>, row: Pick<CommitGraphRow, 'lane' | 'colour' | 'up' | 'down'>): GraphSegment[] {
  const h = ROW_HEIGHT;
  const mid = h / 2;
  const xn = laneX(row.lane);
  const segments: GraphSegment[] = [];
  const ups = new Set(row.up);

  // Pass-throughs: active above, not converging here. Drawn first so the curves sit on top.
  for (const [col, colour] of [...top].sort((a, b) => a[0] - b[0])) {
    if (ups.has(col)) continue;
    const x = laneX(col);
    segments.push({ key: `t${col}`, d: `M${x} 0V${h}`, colour: colour % PALETTE_SIZE });
  }

  // Top half: every converging line ends in the node, curving in from its own column.
  for (const col of row.up) {
    const x = laneX(col);
    // A converging column always has a colour above; fall back to the node's for a malformed page.
    const colour = (top.get(col) ?? row.colour) % PALETTE_SIZE;
    const d = col === row.lane
      ? `M${x} 0V${mid}`
      : `M${x} 0C${x} ${mid / 2} ${xn} ${mid / 2} ${xn} ${mid}`;
    segments.push({ key: `u${col}`, d, colour });
  }

  // Bottom half: one edge per parent, from the node into the lane that carries it on.
  row.down.forEach(([col, colour], i) => {
    const x = laneX(col);
    const d = col === row.lane
      ? `M${xn} ${mid}V${h}`
      : `M${xn} ${mid}C${xn} ${mid + mid / 2} ${x} ${mid + mid / 2} ${x} ${h}`;
    segments.push({ key: `d${i}`, d, colour: colour % PALETTE_SIZE });
  });

  return segments;
}

/**
 * Derives a whole page: `top` is the page header's lanes above its first row, `from` the absolute index
 * of that row. Pages are independent, so a page fetched out of order (virtual scroll) draws correctly.
 */
export function derivePage(top: readonly CommitGraphLane[], rows: readonly CommitGraphRow[], from: number): GraphRowView[] {
  let lanes = new Map<number, number>(top.map(([col, colour]) => [col, colour]));
  return rows.map((row, i) => {
    const segments = rowSegments(lanes, row);
    const below = lanesBelow(lanes, row);
    let width = row.lane + 1;
    for (const col of lanes.keys()) width = Math.max(width, col + 1);
    for (const col of below.keys()) width = Math.max(width, col + 1);
    lanes = below;
    return {
      row,
      index: from + i,
      segments,
      cx: laneX(row.lane),
      cy: ROW_HEIGHT / 2,
      colour: row.colour % PALETTE_SIZE,
      width,
      isMerge: row.parents.length > 1,
    };
  });
}

/** Highlight keys per absolute row index; the node itself is `n`. */
export type PathHighlight = Map<number, Set<string>>;

/**
 * The first-parent ancestry of the row at `start`, within the rows loaded so far: the node, its first
 * parent edge, the straight run through the rows in between, the convergence into the parent's node,
 * and so on down. Stops at a root or at the first parent that is not loaded (or not in this view, e.g. a
 * search list).
 */
export function firstParentPath(
  start: number,
  rowAt: (index: number) => GraphRowView | undefined,
  indexOf: (sha: string) => number | undefined,
  maxSteps = 5000,
): PathHighlight {
  const path: PathHighlight = new Map();
  const mark = (index: number, key: string) => {
    let set = path.get(index);
    if (!set) path.set(index, set = new Set());
    set.add(key);
  };

  let current = rowAt(start);
  if (!current) return path;
  mark(current.index, 'n');
  for (let step = 0; step < maxSteps; step++) {
    const parentSha = current.row.parents[0];
    const edge = current.row.down[0];
    if (!parentSha || !edge) break;
    const parentIndex = indexOf(parentSha);
    if (parentIndex === undefined || parentIndex <= current.index) break;
    const parent = rowAt(parentIndex);
    if (!parent) break;
    const col = edge[0];
    // Every row in between must be loaded for the run to be drawn; stop at the first gap.
    let complete = true;
    for (let j = current.index + 1; j < parentIndex; j++) {
      if (!rowAt(j)) { complete = false; break; }
    }
    if (!complete) break;
    mark(current.index, 'd0');
    for (let j = current.index + 1; j < parentIndex; j++) mark(j, `t${col}`);
    mark(parentIndex, `u${col}`);
    mark(parentIndex, 'n');
    current = parent;
  }
  return path;
}

/**
 * The coverage sparkline along a branch (D17): the percentages of the first-parent chain from the
 * branch's tip, oldest first, within the rows loaded so far. Commits with no complete report are
 * skipped rather than drawn as zero; the walk stops at the first parent that is not loaded.
 */
export function branchCoverageSeries(
  tipSha: string,
  rowAt: (index: number) => GraphRowView | undefined,
  indexOf: (sha: string) => number | undefined,
  maxPoints = 200,
): number[] {
  const series: number[] = [];
  let index = indexOf(tipSha);
  for (let step = 0; index !== undefined && step < 100_000 && series.length < maxPoints; step++) {
    const view = rowAt(index);
    if (!view) break;
    const c = view.row.coverage;
    if (c && c.percent != null && c.completeness !== 'Partial') series.push(c.percent);
    const parent = view.row.parents[0];
    const next = parent ? indexOf(parent) : undefined;
    if (next === undefined || next <= index) break;
    index = next;
  }
  return series.reverse();
}

/** A pie wedge from 12 o'clock, clockwise, for a coverage fraction in (0, 1). */
export function pieWedge(cx: number, cy: number, r: number, fraction: number): string {
  const f = Math.min(Math.max(fraction, 0), 0.9999);
  const angle = f * 2 * Math.PI;
  const x = cx + r * Math.sin(angle);
  const y = cy - r * Math.cos(angle);
  const large = f > 0.5 ? 1 : 0;
  return `M${cx} ${cy}L${cx} ${cy - r}A${r} ${r} 0 ${large} 1 ${round(x)} ${round(y)}Z`;
}

function round(v: number): number {
  return Math.round(v * 100) / 100;
}
