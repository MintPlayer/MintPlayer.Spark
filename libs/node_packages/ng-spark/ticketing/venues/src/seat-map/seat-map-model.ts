import type { SeatMapPayload, SeatStateFrame } from '../generated/contracts';

// The seat map's in-memory model (S7): the payload's parallel arrays as typed arrays, a uniform grid over the seats
// for culling and hit testing, and the live seat states. Framework-free and written in erasable TypeScript only (no
// enums, no parameter properties), so the S7 perf harness can run it after plain type stripping.

/** Seat states in {@link SeatMapModel.state}: the 2-bit values of a state frame, plus "not for sale here". */
export const SEAT_FREE = 0;
export const SEAT_HELD = 1;
export const SEAT_SOLD = 2;
export const SEAT_UNAVAILABLE = 3;

/** The seat tags (default 3), as the server's `SeatTags` flags. */
export const TAG_ACCESSIBLE = 1;
export const TAG_COMPANION = 2;
export const TAG_RESTRICTED_VIEW = 4;

/** A uniform grid in compressed-row form: the seats of cell `c` are `items[starts[c] .. starts[c + 1])`. */
export interface SeatGrid {
  cell: number;
  cols: number;
  rows: number;
  minX: number;
  minY: number;
  starts: Int32Array;
  items: Int32Array;
}

export interface SeatMapModel {
  payload: SeatMapPayload;
  count: number;
  x: Float32Array;
  y: Float32Array;
  tags: Uint8Array;
  category: Int16Array;
  /** The block index of each seat. */
  block: Int32Array;
  /** {@link SEAT_FREE} … {@link SEAT_UNAVAILABLE} per seat. */
  state: Uint8Array;
  /** Bumped by every state change (renderers cache per-block aggregates on it). */
  stateVersion: number;
  /** The typical distance between neighbouring seats in a row: sets the drawn seat size. */
  pitch: number;
  /** Per block: minX, minY, maxX, maxY of its polygon and seats. */
  blockBounds: Float32Array;
  grid: SeatGrid;
  keyIndex: Map<string, number>;
}

/** Builds the model of a payload: O(n) plus a counting sort into the grid. */
export function buildSeatMapModel(payload: SeatMapPayload): SeatMapModel {
  const seats = payload.seats;
  const count = seats.keys.length;
  const x = Float32Array.from(seats.x);
  const y = Float32Array.from(seats.y);
  const tags = Uint8Array.from(seats.tags);
  const category = Int16Array.from(seats.category);
  const block = new Int32Array(count);
  payload.blocks.forEach((b, i) => block.fill(i, b.seatStart, b.seatStart + b.seatCount));
  const keyIndex = new Map<string, number>();
  for (let i = 0; i < count; i++) keyIndex.set(seats.keys[i], i);

  const pitch = estimatePitch(payload, x, y);
  const blockBounds = new Float32Array(payload.blocks.length * 4);
  payload.blocks.forEach((b, i) => {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let p = 0; p + 1 < b.polygon.length; p += 2) {
      minX = Math.min(minX, b.polygon[p]); maxX = Math.max(maxX, b.polygon[p]);
      minY = Math.min(minY, b.polygon[p + 1]); maxY = Math.max(maxY, b.polygon[p + 1]);
    }
    for (let s = b.seatStart; s < b.seatStart + b.seatCount; s++) {
      minX = Math.min(minX, x[s]); maxX = Math.max(maxX, x[s]);
      minY = Math.min(minY, y[s]); maxY = Math.max(maxY, y[s]);
    }
    blockBounds.set(Number.isFinite(minX) ? [minX, minY, maxX, maxY] : [0, 0, 0, 0], i * 4);
  });

  return {
    payload, count, x, y, tags, category, block,
    state: new Uint8Array(count),
    stateVersion: 0,
    pitch,
    blockBounds,
    grid: buildGrid(x, y, Math.max(pitch * 8, 1)),
    keyIndex,
  };
}

/** The median distance between neighbouring seats of a row (sampled), or 1 for a map without rows. */
export function estimatePitch(payload: SeatMapPayload, x: Float32Array, y: Float32Array): number {
  const samples: number[] = [];
  const rows = payload.rows;
  const step = Math.max(1, Math.floor(rows.seatStart.length / 400));
  for (let r = 0; r < rows.seatStart.length; r += step) {
    const start = rows.seatStart[r];
    if (rows.seatCount[r] < 2) continue;
    samples.push(Math.hypot(x[start + 1] - x[start], y[start + 1] - y[start]));
  }
  if (samples.length === 0) return 1;
  samples.sort((a, b) => a - b);
  return samples[samples.length >> 1] || 1;
}

/** A uniform grid of `cell`-sized cells over the points. */
export function buildGrid(x: Float32Array, y: Float32Array, cell: number): SeatGrid {
  const count = x.length;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let i = 0; i < count; i++) {
    if (x[i] < minX) minX = x[i];
    if (x[i] > maxX) maxX = x[i];
    if (y[i] < minY) minY = y[i];
    if (y[i] > maxY) maxY = y[i];
  }
  if (count === 0) { minX = minY = 0; maxX = maxY = 0; }
  const cols = Math.max(1, Math.floor((maxX - minX) / cell) + 1);
  const rows = Math.max(1, Math.floor((maxY - minY) / cell) + 1);
  const cellOf = new Int32Array(count);
  const starts = new Int32Array(cols * rows + 1);
  for (let i = 0; i < count; i++) {
    const c = Math.floor((y[i] - minY) / cell) * cols + Math.floor((x[i] - minX) / cell);
    cellOf[i] = c;
    starts[c + 1]++;
  }
  for (let c = 0; c < cols * rows; c++) starts[c + 1] += starts[c];
  const fill = starts.slice(0, cols * rows);
  const items = new Int32Array(count);
  for (let i = 0; i < count; i++) items[fill[cellOf[i]]++] = i;
  return { cell, cols, rows, minX, minY, starts, items };
}

/** Calls `visit` for every seat whose centre lies in the rectangle (plan units), via the grid. */
export function forEachSeatIn(model: SeatMapModel, x0: number, y0: number, x1: number, y1: number, visit: (i: number) => void): void {
  const g = model.grid;
  const c0 = Math.max(0, Math.floor((x0 - g.minX) / g.cell));
  const c1 = Math.min(g.cols - 1, Math.floor((x1 - g.minX) / g.cell));
  const r0 = Math.max(0, Math.floor((y0 - g.minY) / g.cell));
  const r1 = Math.min(g.rows - 1, Math.floor((y1 - g.minY) / g.cell));
  for (let r = r0; r <= r1; r++) {
    for (let c = c0; c <= c1; c++) {
      const cell = r * g.cols + c;
      for (let k = g.starts[cell]; k < g.starts[cell + 1]; k++) {
        const i = g.items[k];
        const sx = model.x[i], sy = model.y[i];
        if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1) visit(i);
      }
    }
  }
}

/** The seat nearest to (x, y) within `radius` plan units, or -1. */
export function hitTest(model: SeatMapModel, x: number, y: number, radius: number): number {
  let best = -1;
  let bestDistance = radius * radius;
  forEachSeatIn(model, x - radius, y - radius, x + radius, y + radius, (i) => {
    const d = (model.x[i] - x) ** 2 + (model.y[i] - y) ** 2;
    if (d <= bestDistance) { bestDistance = d; best = i; }
  });
  return best;
}

/** Whether the point lies inside the flat polygon `x0, y0, x1, y1, …` (even-odd rule). */
export function pointInPolygon(polygon: readonly number[], x: number, y: number): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 2; i < polygon.length; j = i, i += 2) {
    const xi = polygon[i], yi = polygon[i + 1], xj = polygon[j], yj = polygon[j + 1];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

/** The block under (x, y), the topmost (last drawn) first, or -1. */
export function blockAt(model: SeatMapModel, x: number, y: number): number {
  const blocks = model.payload.blocks;
  for (let b = blocks.length - 1; b >= 0; b--) {
    if (pointInPolygon(blocks[b].polygon, x, y)) return b;
  }
  return -1;
}

/**
 * Applies a state frame (base64 of 2 bits per seat, map order). Answers whether anything changed. A frame of another
 * size (another snapshot) is ignored.
 */
export function applyStateFrame(model: SeatMapModel, frame: Pick<SeatStateFrame, 'seatCount' | 'states'>): boolean {
  if (frame.seatCount !== model.count) return false;
  const binary = atob(frame.states);
  let changed = false;
  for (let i = 0; i < model.count; i++) {
    const value = (binary.charCodeAt(i >> 2) >> ((i & 3) * 2)) & 3;
    if (model.state[i] !== value) { model.state[i] = value; changed = true; }
  }
  if (changed) model.stateVersion++;
  return changed;
}

/** Encodes states the way the server does (tests, the harness and the editor's preview use it). */
export function encodeStates(states: ArrayLike<number>): string {
  const bytes = new Uint8Array((states.length + 3) >> 2);
  for (let i = 0; i < states.length; i++) bytes[i >> 2] |= (states[i] & 3) << ((i & 3) * 2);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

/** Free seats per block (numbered blocks only), for block-level drawing and the list alternative. */
export function freeSeatsPerBlock(model: SeatMapModel): Int32Array {
  const free = new Int32Array(model.payload.blocks.length);
  for (let i = 0; i < model.count; i++) if (model.state[i] === SEAT_FREE) free[model.block[i]]++;
  return free;
}
