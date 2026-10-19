import { forEachSeatIn, freeSeatsPerBlock, SEAT_FREE, SEAT_HELD, SEAT_SOLD, TAG_ACCESSIBLE, TAG_RESTRICTED_VIEW } from './seat-map-model';
import type { SeatMapModel } from './seat-map-model';
import { visiblePlanRect } from './viewport';
import type { Viewport } from './viewport';

// The seat-map renderer (S7, PRD §4.11: "renders to canvas with level of detail"). Canvas 2D only (K11: a standard
// browser API, no WebGL library). Three levels of detail, chosen by how many pixels a seat pitch covers:
//
//   blocks  (< LOD_SEATS px)  each block is one polygon, shaded by how much of it is left: no per-seat work at all;
//   dots    (< LOD_CIRCLES)   seats are squares, batched into one path per colour, culled through the grid;
//   circles (≥ LOD_CIRCLES)   seats are circles, with seat labels from LOD_LABELS and row labels from LOD_ROW_LABELS.
//
// Batching by colour keeps the canvas state changes to a handful per frame however many seats are drawn, and the
// grid keeps a zoomed-in frame proportional to what is on screen, not to the map's 50k seats. Erasable TypeScript
// only, for the perf harness.

export const LOD_SEATS = 3;
export const LOD_CIRCLES = 8;
export const LOD_ROW_LABELS = 11;
export const LOD_LABELS = 18;

export type SeatMapLod = 'blocks' | 'dots' | 'circles';

/** The subset of the 2D context the renderer uses (a test passes a recording fake). */
export type SeatMapCanvas = Pick<CanvasRenderingContext2D,
  'fillStyle' | 'strokeStyle' | 'lineWidth' | 'globalAlpha' | 'font' | 'textAlign' | 'textBaseline'
  | 'beginPath' | 'moveTo' | 'lineTo' | 'closePath' | 'fill' | 'stroke' | 'rect' | 'arc' | 'fillRect' | 'fillText'
  | 'save' | 'restore' | 'setTransform' | 'clearRect' | 'drawImage'>;

export interface SeatMapColors {
  background: string;
  free: string;
  held: string;
  sold: string;
  unavailable: string;
  accessible: string;
  selected: string;
  highlight: string;
  blockFill: string;
  blockStroke: string;
  decor: string;
  stage: string;
  text: string;
}

/** Light defaults; the component reads the theme's CSS variables (`--spark-seat-*`) over them. */
export const DEFAULT_SEAT_MAP_COLORS: SeatMapColors = {
  background: '#ffffff',
  free: '#16a34a',
  held: '#f59e0b',
  sold: '#9ca3af',
  unavailable: '#d1d5db',
  accessible: '#2563eb',
  selected: '#7c3aed',
  highlight: '#dc2626',
  blockFill: '#f3f4f6',
  blockStroke: '#9ca3af',
  decor: '#e5e7eb',
  stage: '#374151',
  text: '#111827',
};

export interface SeatMapRenderOptions {
  /** CSS pixels of the canvas. */
  width: number;
  height: number;
  /** Device pixel ratio (the canvas is width × dpr wide). */
  dpr: number;
  colors: SeatMapColors;
  /** Seats drawn as selected (by index); null for none. */
  selected?: Uint8Array | null;
  /** Seats outlined in the highlight colour (the layout diff); null for none. */
  highlight?: Uint8Array | null;
  /** Colour free seats by price category instead of the free colour (the editor, a category legend). */
  categoryColors?: readonly string[] | null;
  /** The background image, already loaded, drawn at the plan's background rectangle. */
  backgroundImage?: CanvasImageSource | null;
  /** Force a level of detail (tests, the editor's "seats always" mode). */
  lod?: SeatMapLod | null;
}

export interface SeatMapRenderStats {
  lod: SeatMapLod;
  seatsDrawn: number;
  blocksDrawn: number;
  pathFills: number;
}

/** Which level of detail a viewport gets. */
export function lodFor(model: SeatMapModel, viewport: Viewport): SeatMapLod {
  const px = model.pitch * viewport.scale;
  return px < LOD_SEATS ? 'blocks' : px < LOD_CIRCLES ? 'dots' : 'circles';
}

// Colour buckets: 0 free, 1 held, 2 sold, 3 unavailable, 4 accessible-free, 5+ per category (free).
const BUCKET_ACCESSIBLE = 4;
const BUCKET_CATEGORY = 5;

/** Reused between frames: per-bucket seat lists. */
let bucketLists: Int32Array[] = [];
let bucketCounts: Int32Array = new Int32Array(0);

/** Per-model cache of the per-block free counts, keyed on the state version. */
const blockFree = new WeakMap<SeatMapModel, { version: number; free: Int32Array }>();

/** Draws one frame. Returns what it drew (the render-budget tests read it). */
export function renderSeatMap(ctx: SeatMapCanvas, model: SeatMapModel, viewport: Viewport, options: SeatMapRenderOptions): SeatMapRenderStats {
  const { colors, dpr } = options;
  const lod = options.lod ?? lodFor(model, viewport);
  const stats: SeatMapRenderStats = { lod, seatsDrawn: 0, blocksDrawn: 0, pathFills: 0 };
  const payload = model.payload;

  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = colors.background;
  ctx.fillRect(0, 0, options.width, options.height);
  ctx.setTransform(dpr * viewport.scale, 0, 0, dpr * viewport.scale, dpr * viewport.tx, dpr * viewport.ty);
  const [vx0, vy0, vx1, vy1] = visiblePlanRect(viewport, options.width, options.height);
  const onePx = 1 / viewport.scale;

  if (options.backgroundImage && payload.background) {
    const bg = payload.background;
    ctx.globalAlpha = bg.opacity;
    ctx.drawImage(options.backgroundImage, bg.x, bg.y, bg.width, bg.height);
    ctx.globalAlpha = 1;
  }

  // Decor.
  for (const decor of payload.decor) {
    if (decor.kind === 'text') {
      if (decor.points.length >= 2 && decor.label) {
        ctx.fillStyle = decor.color || colors.text;
        ctx.font = `${12 * onePx}px sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(decor.label, decor.points[0], decor.points[1]);
      }
      continue;
    }
    tracePolygon(ctx, decor.points);
    ctx.fillStyle = decor.color || (decor.kind === 'stage' ? colors.stage : colors.decor);
    ctx.fill();
    stats.pathFills++;
    if (decor.label && decor.points.length >= 2) {
      const [cx, cy] = centroid(decor.points);
      ctx.fillStyle = decor.kind === 'stage' ? colors.background : colors.text;
      ctx.font = `${14 * onePx}px sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(decor.label, cx, cy);
    }
  }

  // Blocks: at block level shaded by what is left; otherwise a light outline under the seats.
  const free = lod === 'blocks' ? freeCounts(model) : null;
  ctx.lineWidth = onePx;
  for (let b = 0; b < payload.blocks.length; b++) {
    const bounds = model.blockBounds;
    if (bounds[b * 4 + 2] < vx0 || bounds[b * 4] > vx1 || bounds[b * 4 + 3] < vy0 || bounds[b * 4 + 1] > vy1) continue;
    const block = payload.blocks[b];
    tracePolygon(ctx, block.polygon);
    if (free) {
      const total = block.kind === 'seatedNumbered' ? block.seatCount : 0;
      ctx.fillStyle = total === 0 ? colors.blockFill : free[b] === 0 ? colors.sold : colors.free;
      ctx.globalAlpha = total === 0 ? 1 : 0.25 + 0.75 * (free[b] / total);
      ctx.fill();
      ctx.globalAlpha = 1;
      stats.pathFills++;
    } else {
      ctx.fillStyle = colors.blockFill;
      ctx.fill();
      stats.pathFills++;
    }
    ctx.strokeStyle = colors.blockStroke;
    ctx.stroke();
    stats.blocksDrawn++;
    const screenWidth = (bounds[b * 4 + 2] - bounds[b * 4]) * viewport.scale;
    if (lod === 'blocks' && screenWidth > 60) {
      const [cx, cy] = centroid(block.polygon);
      ctx.fillStyle = colors.text;
      ctx.font = `${11 * onePx}px sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(block.name, cx, cy);
    }
  }
  if (lod === 'blocks') return stats;

  // Seats: bucket the visible ones by colour, then one path per bucket.
  const categories = options.categoryColors ?? null;
  const bucketTotal = BUCKET_CATEGORY + (categories?.length ?? 0);
  ensureBuckets(bucketTotal, model.count);
  bucketCounts.fill(0);
  const margin = model.pitch;
  forEachSeatIn(model, vx0 - margin, vy0 - margin, vx1 + margin, vy1 + margin, (i) => {
    const state = model.state[i];
    let bucket = state;
    if (state === SEAT_FREE) {
      if (model.tags[i] & TAG_ACCESSIBLE) bucket = BUCKET_ACCESSIBLE;
      else if (categories && model.category[i] >= 0 && model.category[i] < categories.length) bucket = BUCKET_CATEGORY + model.category[i];
    }
    bucketLists[bucket][bucketCounts[bucket]++] = i;
  });

  // Squares are one path per colour (measured faster than per-seat fillRect, S7); circles of 12 device pixels and more
  // are one pre-rendered sprite per colour stamped with drawImage, smaller ones arcs in one path per colour.
  const half = model.pitch * (lod === 'dots' ? 0.4 : 0.42);
  const spriteSize = Math.ceil(half * 2 * viewport.scale * dpr);
  for (let bucket = 0; bucket < bucketTotal; bucket++) {
    const n = bucketCounts[bucket];
    if (n === 0) continue;
    const list = bucketLists[bucket];
    const color = bucketColor(bucket, colors, categories);
    const sprite = lod === 'circles' && spriteSize >= 12 ? circleSprite(color, spriteSize) : null;
    if (lod === 'dots') {
      ctx.beginPath();
      for (let k = 0; k < n; k++) ctx.rect(model.x[list[k]] - half, model.y[list[k]] - half, half * 2, half * 2);
      ctx.fillStyle = color;
      ctx.fill();
    } else if (sprite) {
      for (let k = 0; k < n; k++) ctx.drawImage(sprite, model.x[list[k]] - half, model.y[list[k]] - half, half * 2, half * 2);
    } else {
      ctx.beginPath();
      for (let k = 0; k < n; k++) {
        const i = list[k];
        ctx.moveTo(model.x[i] + half, model.y[i]);
        ctx.arc(model.x[i], model.y[i], half, 0, Math.PI * 2);
      }
      ctx.fillStyle = color;
      ctx.fill();
    }
    stats.pathFills++;
    stats.seatsDrawn += n;
  }

  // Selection and highlight rings.
  strokeMarked(ctx, model, options.selected, colors.selected, half * 1.25, Math.max(onePx * 2, half * 0.25), vx0 - margin, vy0 - margin, vx1 + margin, vy1 + margin);
  strokeMarked(ctx, model, options.highlight, colors.highlight, half * 1.45, Math.max(onePx * 2, half * 0.2), vx0 - margin, vy0 - margin, vx1 + margin, vy1 + margin);

  if (lod === 'circles') {
    const px = model.pitch * viewport.scale;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    if (px >= LOD_LABELS) {
      ctx.fillStyle = colors.background;
      ctx.font = `${half * 0.95}px sans-serif`;
      const labels = model.payload.seats.labels;
      forEachSeatIn(model, vx0, vy0, vx1, vy1, (i) => ctx.fillText(labels[i], model.x[i], model.y[i]));
    }
    if (px >= LOD_ROW_LABELS) {
      // The row label left of each row's first seat.
      ctx.fillStyle = colors.text;
      ctx.font = `${half * 1.1}px sans-serif`;
      const rows = model.payload.rows;
      for (let r = 0; r < rows.seatStart.length; r++) {
        const first = rows.seatStart[r];
        if (rows.seatCount[r] === 0) continue;
        const fx = model.x[first], fy = model.y[first];
        if (fx < vx0 - margin * 2 || fx > vx1 + margin * 2 || fy < vy0 || fy > vy1) continue;
        const second = rows.seatCount[r] > 1 ? first + 1 : first;
        const dx = model.x[second] - fx || model.pitch, dy = model.y[second] - fy;
        const d = Math.hypot(dx, dy) || 1;
        ctx.fillText(rows.labels[r], fx - (dx / d) * model.pitch, fy - (dy / d) * model.pitch);
      }
    }
    // Restricted view: a small bar under the seat.
    ctx.fillStyle = colors.text;
    forEachSeatIn(model, vx0, vy0, vx1, vy1, (i) => {
      if (model.tags[i] & TAG_RESTRICTED_VIEW) ctx.fillRect(model.x[i] - half * 0.6, model.y[i] + half * 0.7, half * 1.2, half * 0.25);
    });
  }
  return stats;
}

/** Circle sprites per colour and pixel size (OffscreenCanvas, a standard API; absent in jsdom, where arcs are drawn). */
const sprites = new Map<string, CanvasImageSource>();

function circleSprite(color: string, size: number): CanvasImageSource | null {
  if (typeof OffscreenCanvas === 'undefined' || size < 2 || size > 256) return null;
  const key = color + '|' + size;
  const hit = sprites.get(key);
  if (hit) return hit;
  const canvas = new OffscreenCanvas(size, size);
  const c = canvas.getContext('2d');
  if (!c) return null;
  c.fillStyle = color;
  c.beginPath();
  c.arc(size / 2, size / 2, size / 2, 0, Math.PI * 2);
  c.fill();
  if (sprites.size > 256) sprites.clear();
  sprites.set(key, canvas);
  return canvas;
}

function bucketColor(bucket: number, colors: SeatMapColors, categories: readonly string[] | null): string {
  switch (bucket) {
    case SEAT_FREE: return colors.free;
    case SEAT_HELD: return colors.held;
    case SEAT_SOLD: return colors.sold;
    case BUCKET_ACCESSIBLE: return colors.accessible;
    case 3: return colors.unavailable;
    default: return categories?.[bucket - BUCKET_CATEGORY] ?? colors.free;
  }
}

function ensureBuckets(total: number, count: number): void {
  if (bucketLists.length < total) bucketLists = bucketLists.concat(Array.from({ length: total - bucketLists.length }, () => new Int32Array(0)));
  if (bucketCounts.length < total) bucketCounts = new Int32Array(total);
  for (let b = 0; b < total; b++) if (bucketLists[b].length < count) bucketLists[b] = new Int32Array(count);
}

function freeCounts(model: SeatMapModel): Int32Array {
  const cached = blockFree.get(model);
  if (cached && cached.version === model.stateVersion) return cached.free;
  const free = freeSeatsPerBlock(model);
  blockFree.set(model, { version: model.stateVersion, free });
  return free;
}

function strokeMarked(ctx: SeatMapCanvas, model: SeatMapModel, marks: Uint8Array | null | undefined, color: string, radius: number, width: number,
  x0: number, y0: number, x1: number, y1: number): void {
  if (!marks) return;
  let any = false;
  ctx.beginPath();
  forEachSeatIn(model, x0, y0, x1, y1, (i) => {
    if (!marks[i]) return;
    any = true;
    ctx.moveTo(model.x[i] + radius, model.y[i]);
    ctx.arc(model.x[i], model.y[i], radius, 0, Math.PI * 2);
  });
  if (!any) return;
  ctx.strokeStyle = color;
  ctx.lineWidth = width;
  ctx.stroke();
}

function tracePolygon(ctx: SeatMapCanvas, points: readonly number[]): void {
  ctx.beginPath();
  if (points.length < 4) return;
  ctx.moveTo(points[0], points[1]);
  for (let p = 2; p + 1 < points.length; p += 2) ctx.lineTo(points[p], points[p + 1]);
  ctx.closePath();
}

/** The vertex average of a flat polygon (good enough for a label). */
export function centroid(points: readonly number[]): [number, number] {
  let x = 0, y = 0;
  const n = Math.max(1, points.length >> 1);
  for (let p = 0; p + 1 < points.length; p += 2) { x += points[p]; y += points[p + 1]; }
  return [x / n, y / n];
}
