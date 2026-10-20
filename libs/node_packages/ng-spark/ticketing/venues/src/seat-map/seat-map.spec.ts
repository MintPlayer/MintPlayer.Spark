import { describe, expect, it } from 'vitest';
import { SeatMapLayer } from './seat-map-layer';
import {
  applyStateFrame, blockAt, buildSeatMapModel, encodeStates, forEachSeatIn, freeSeatsPerBlock, hitTest, SEAT_FREE, SEAT_HELD, SEAT_SOLD,
} from './seat-map-model';
import { DEFAULT_SEAT_MAP_COLORS, lodFor, renderSeatMap, SeatMapCanvas, SeatMapRenderOptions } from './seat-map-renderer';
import { syntheticStadium, syntheticStates } from './test-utils';
import { centreOn, fitViewport, toPlan, toScreen, zoomAt } from './viewport';

// Commerce #521 M14 / spike S7: the seat-map model, viewport and renderer. jsdom has no canvas, so the renderer draws
// on a recording fake and the tests budget what it issues (the frame-rate measurement in a real browser is the perf
// harness, tools/seat-map-perf/build-harness.mjs).

/** A 2D context that records calls: enough for the renderer, and countable. */
function recordingCanvas() {
  const calls: Record<string, number> = {};
  const count = (name: string) => () => { calls[name] = (calls[name] ?? 0) + 1; };
  const ctx = {
    fillStyle: '', strokeStyle: '', lineWidth: 1, globalAlpha: 1, font: '', textAlign: 'left', textBaseline: 'alphabetic',
    beginPath: count('beginPath'), moveTo: count('moveTo'), lineTo: count('lineTo'), closePath: count('closePath'),
    fill: count('fill'), stroke: count('stroke'), rect: count('rect'), arc: count('arc'), fillRect: count('fillRect'),
    fillText: count('fillText'), save: count('save'), restore: count('restore'), setTransform: count('setTransform'),
    clearRect: count('clearRect'), drawImage: count('drawImage'),
  };
  return { ctx: ctx as unknown as SeatMapCanvas, calls };
}

const W = 1280, H = 800;
const options = (extra: Partial<SeatMapRenderOptions> = {}): SeatMapRenderOptions => ({ width: W, height: H, dpr: 1, colors: DEFAULT_SEAT_MAP_COLORS, ...extra });

describe('seat-map model (S7)', () => {
  const payload = syntheticStadium();
  const model = buildSeatMapModel(payload);

  it('holds 50,000 seats, every one in exactly one grid cell', () => {
    expect(model.count).toBe(50_000);
    expect(model.grid.items.length).toBe(50_000);
    expect(new Set(model.grid.items).size).toBe(50_000);
    expect(model.pitch).toBeCloseTo(10, 5);
  });

  it('finds seats through the grid exactly like a full scan', () => {
    const [x0, y0, x1, y1] = [300, 200, 900, 500];
    const viaGrid: number[] = [];
    forEachSeatIn(model, x0, y0, x1, y1, (i) => viaGrid.push(i));
    const viaScan: number[] = [];
    for (let i = 0; i < model.count; i++) if (model.x[i] >= x0 && model.x[i] <= x1 && model.y[i] >= y0 && model.y[i] <= y1) viaScan.push(i);
    expect(viaGrid.sort((a, b) => a - b)).toEqual(viaScan);
  });

  it('hits the seat under the pointer, and nothing between seats of a gap', () => {
    const i = 1234;
    expect(hitTest(model, model.x[i] + 1, model.y[i] - 1, model.pitch * 0.6)).toBe(i);
    expect(hitTest(model, 1, 1, model.pitch * 0.6)).toBe(-1);
    expect(blockAt(model, model.x[i], model.y[i])).toBe(model.block[i]);
  });

  it('round-trips a state frame (2 bits per seat) and counts free seats per block', () => {
    const states = syntheticStates(model.count);
    expect(applyStateFrame(model, { seatCount: model.count, states: encodeStates(states) })).toBe(true);
    expect(Array.from(model.state.subarray(0, 64))).toEqual(Array.from(states.subarray(0, 64)));
    expect([SEAT_FREE, SEAT_HELD, SEAT_SOLD]).toContain(model.state[49_999]);
    const free = freeSeatsPerBlock(model);
    expect(free.reduce((a, b) => a + b, 0)).toBe(states.filter((s) => s === SEAT_FREE).length);
    // The same frame again changes nothing; a frame of another snapshot (size) is ignored.
    expect(applyStateFrame(model, { seatCount: model.count, states: encodeStates(states) })).toBe(false);
    expect(applyStateFrame(model, { seatCount: 3, states: encodeStates([1, 1, 1]) })).toBe(false);
  });
});

describe('viewport', () => {
  it('fits the plan, zooms around the cursor and converts both ways', () => {
    const fit = fitViewport(2000, 1000, 1000, 600, 0);
    expect(fit.scale).toBe(0.5);
    const zoomed = zoomAt(fit, 2, 300, 200, fit.scale);
    const before = toPlan(fit, 300, 200), after = toPlan(zoomed, 300, 200);
    expect(after.x).toBeCloseTo(before.x, 9);
    expect(after.y).toBeCloseTo(before.y, 9);
    const p = toScreen(zoomed, 123, 456);
    expect(toPlan(zoomed, p.x, p.y).x).toBeCloseTo(123, 9);
    // Never further out than half the fit.
    expect(zoomAt(fit, 0.01, 0, 0, fit.scale).scale).toBe(fit.scale * 0.5);
  });
});

describe('seat-map renderer: level of detail and render budget (S7)', () => {
  const payload = syntheticStadium();
  const model = buildSeatMapModel(payload);
  applyStateFrame(model, { seatCount: model.count, states: encodeStates(syntheticStates(model.count)) });
  const fit = fitViewport(payload.width, payload.height, W, H);

  it('draws only blocks when the whole stadium is on screen', () => {
    const { ctx, calls } = recordingCanvas();
    const stats = renderSeatMap(ctx, model, fit, options());
    expect(lodFor(model, fit)).toBe('blocks');
    expect(stats.seatsDrawn).toBe(0);
    expect(stats.blocksDrawn).toBe(40);
    expect(calls['rect'] ?? 0).toBe(0);
    expect(calls['arc'] ?? 0).toBe(0);
  });

  it('batches seats into one fill per colour at dot level', () => {
    const { ctx, calls } = recordingCanvas();
    const vp = centreOn(payload.width / 2, payload.height / 2, 4 / model.pitch, W, H);
    const stats = renderSeatMap(ctx, model, vp, options());
    expect(stats.lod).toBe('dots');
    expect(stats.seatsDrawn).toBeGreaterThan(10_000);
    expect(calls['rect']).toBe(stats.seatsDrawn);
    // Free, held, sold, accessible: a handful of seat fills, never one per seat.
    expect(stats.pathFills - stats.blocksDrawn - payload.decor.length).toBeLessThanOrEqual(5);
  });

  it('culls to what is on screen when zoomed in', () => {
    const { ctx } = recordingCanvas();
    const vp = centreOn(payload.width / 2, payload.height / 2, 24 / model.pitch, W, H);
    const stats = renderSeatMap(ctx, model, vp, options());
    expect(stats.lod).toBe('circles');
    // 1280 × 800 at 24 px a seat shows about 53 × 33 seats; the margin adds a ring.
    expect(stats.seatsDrawn).toBeLessThan(3_000);
  });

  it('builds the model and issues a full dot-level frame within a budget (jsdom, generous bounds)', () => {
    const t0 = performance.now();
    const fresh = buildSeatMapModel(payload);
    const built = performance.now() - t0;
    const { ctx } = recordingCanvas();
    const vp = centreOn(payload.width / 2, payload.height / 2, Math.max(4 / model.pitch, fit.scale), W, H);
    const t1 = performance.now();
    renderSeatMap(ctx, fresh, vp, options());
    const frame = performance.now() - t1;
    // Failure bounds, not measurements: the real numbers are the harness's (S7, recorded in the guide).
    expect(built).toBeLessThan(2_000);
    expect(frame).toBeLessThan(1_000);
  });

  it('stamps the cached bitmap while panning and redraws it only when needed', () => {
    const layer = new SeatMapLayer();
    layer.createCanvas = (width, height) => {
      const { ctx } = recordingCanvas();
      return { width, height, getContext: () => ctx };
    };
    const { ctx, calls } = recordingCanvas();
    const vp = centreOn(payload.width / 2, payload.height / 2, 6 / model.pitch, W, H);
    expect(layer.draw(ctx, model, vp, options()).rebuilt).toBe(true);
    expect(layer.draw(ctx, model, { ...vp, tx: vp.tx + 30 }, options()).rebuilt).toBe(false);
    expect(calls['drawImage']).toBe(2);
    // New content (a state change) redraws.
    model.stateVersion++;
    expect(layer.draw(ctx, model, { ...vp, tx: vp.tx + 30 }, options()).rebuilt).toBe(true);
    // Block level is drawn directly.
    expect(layer.draw(ctx, model, fit, options()).direct).toBe(true);
  });
});
