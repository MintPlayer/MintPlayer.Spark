import { applyPatch, diff } from './diff';
import { addConstraint, addPoint, addShape, deletePoints, mergePoints, setMotionPath, setPointKey, splitPoint } from './edit';
import { evaluate, interpolate } from './evaluate';
import { arcToCubics, cubicAt, measure, pointAlong } from './geometry';
import { emptyScene, defaultFill, defaultStroke, normalizeScene, type Scene } from './model';
import { strokeWidthPx } from './render';
import { snap } from './snap';
import { sceneToSvg } from './svg';

function sceneWithLine(): { scene: Scene; a: string; b: string; line: string; layer: string } {
  const scene = emptyScene();
  const layer = scene.layerOrder[0];
  const a = addPoint(scene, { x: 10, y: 10 });
  const b = addPoint(scene, { x: 100, y: 40 });
  const line = addShape(scene, layer, { type: 'line', a, b, stroke: defaultStroke(), fill: defaultFill() });
  return { scene, a, b, line, layer };
}

describe('geometry', () => {
  it('approximates a quarter circle with a cubic within 0.03 % of the radius', () => {
    const [c] = arcToCubics({ x: 0, y: 0 }, 100, 100, 0, Math.PI / 2);
    if (c.c !== 'C') throw new Error('expected a cubic');
    let worst = 0;
    for (let t = 0; t <= 1; t += 0.01) worst = Math.max(worst, Math.abs(Math.hypot(...Object.values(cubicAt({ x: 100, y: 0 }, c.c1, c.c2, c.p, t))) - 100));
    expect(worst).toBeLessThan(0.03);
  });

  it('walks a polyline by length', () => {
    const m = measure([{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }], false);
    expect(m.total).toBe(20);
    expect(pointAlong(m, 0.75)).toEqual({ x: 10, y: 5 });
  });
});

describe('diff and patch (S5)', () => {
  it('keeps a realistic project far below the 5 MB scene quota and a drag patch tiny', () => {
    // 500 shapes (closed hexagons whose neighbours share a point) and 30 point tracks with 10 keys each.
    const scene = emptyScene();
    const layer = scene.layerOrder[0];
    let shared = addPoint(scene, { x: 0, y: 0 });
    for (let i = 0; i < 500; i++) {
      const pts = [shared, ...Array.from({ length: 5 }, (_, k) => addPoint(scene, { x: i + k * 3.125, y: k * 7.5 }))];
      addShape(scene, layer, { type: 'polyline', points: pts, closed: true, stroke: defaultStroke(), fill: defaultFill() });
      shared = pts[5];
    }
    const ids = Object.keys(scene.points);
    for (let t = 0; t < 30; t++) {
      for (let f = 0; f < 10; f++) setPointKey(scene, ids[t], f * 3, { x: f * 1.5, y: t * 2.25 });
    }
    const bytes = new TextEncoder().encode(JSON.stringify(scene)).length;
    expect(bytes).toBeLessThan(1024 * 1024);

    const next = structuredClone(scene);
    next.points[ids[100]] = { ...next.points[ids[100]], x: 42.5, y: 17.25 };
    const patch = new TextEncoder().encode(JSON.stringify(diff(scene, next))).length;
    expect(patch).toBeLessThan(200);
  });

  it('produces operations that turn the old scene into the new one', () => {
    const { scene, a, line } = sceneWithLine();
    const next = structuredClone(scene);
    next.points[a].x = 50;
    next.shapes[line].stroke.color = '#ff0000';
    delete next.shapes[line].name;
    next.layerOrder.push('extra');
    next.layers['extra'] = { name: 'x', kind: 'paint', visible: true, locked: false, opacity: 1, shapes: [], bitmaps: {} };
    const ops = diff(scene, next);
    expect(ops.length).toBeLessThan(6);
    expect(applyPatch(scene, ops)).toEqual(next);
  });

  it('removes deleted keys and nothing else', () => {
    const { scene, b } = sceneWithLine();
    const next = structuredClone(scene);
    delete next.points[b];
    expect(diff(scene, next)).toEqual([{ op: 'remove', path: ['points', b] }]);
  });

  it('finds no changes in an equal scene', () => {
    const { scene } = sceneWithLine();
    expect(diff(scene, structuredClone(scene))).toEqual([]);
  });
});

describe('shared points, snapping and Split (D4, D15)', () => {
  it('merging makes two shapes share a point; Split gives one back its own', () => {
    const { scene, b, layer } = sceneWithLine();
    const c = addPoint(scene, { x: 101, y: 41 });
    const d = addPoint(scene, { x: 200, y: 200 });
    const second = addShape(scene, layer, { type: 'line', a: c, b: d, stroke: defaultStroke(), fill: defaultFill() });
    mergePoints(scene, c, b);
    expect(scene.points[c]).toBeUndefined();
    expect(scene.shapes[second]).toMatchObject({ a: b });

    const own = splitPoint(scene, b, second)!;
    expect(own).not.toBe(b);
    expect(scene.shapes[second]).toMatchObject({ a: own });
    expect(scene.points[own]).toMatchObject({ x: scene.points[b].x, y: scene.points[b].y });
  });

  it('snaps to a point first, then a midpoint, then an outline, then the grid', () => {
    const { scene, a, line } = sceneWithLine();
    const { positions } = evaluate(scene);
    const o = { radius: 4, grid: 8, gridEnabled: true };
    expect(snap(scene, positions, { x: 11, y: 11 }, o)).toMatchObject({ kind: 'point', id: a });
    expect(snap(scene, positions, { x: 56, y: 26 }, o)).toMatchObject({ kind: 'midpoint' });
    expect(snap(scene, positions, { x: 30, y: 17 }, o)).toMatchObject({ kind: 'onPath', shape: line });
    expect(snap(scene, positions, { x: 150, y: 150 }, o)).toEqual({ kind: 'grid', at: { x: 152, y: 152 } });
  });

  it('deleting a point deletes the shapes and constraints that need it', () => {
    const { scene, a, b, line } = sceneWithLine();
    addConstraint(scene, { type: 'horizontal', a, b });
    deletePoints(scene, [a]);
    expect(scene.shapes[line]).toBeUndefined();
    expect(Object.keys(scene.constraints)).toEqual([]);
    expect(scene.points[b]).toBeUndefined();
  });
});

describe('evaluation (D14, D15, D19)', () => {
  it('solves constraints and keeps derived points on their sources', () => {
    const { scene, a, b, line } = sceneWithLine();
    addConstraint(scene, { type: 'horizontal', a, b });
    scene.points[a].fixed = true;
    const mid = addPoint(scene, { x: 0, y: 0 }, { type: 'midpoint', a, b });
    const on = addPoint(scene, { x: 0, y: 0 }, { type: 'onPath', shape: line, t: 0.5 });
    const { positions, status } = evaluate(scene);
    expect(status).toBe('solved');
    expect(positions[b].y).toBeCloseTo(10, 5);
    expect(positions[mid].x).toBeCloseTo((positions[a].x + positions[b].x) / 2, 5);
    expect(positions[on].y).toBeCloseTo(10, 3);
  });

  it('drags a point with the constraints holding', () => {
    const { scene, a, b } = sceneWithLine();
    addConstraint(scene, { type: 'distance', a, b, value: 50 });
    scene.points[a].fixed = true;
    const { positions } = evaluate(scene, { drag: { point: b, target: { x: 10, y: 200 } } });
    expect(Math.hypot(positions[b].x - 10, positions[b].y - 10)).toBeCloseTo(50, 4);
    expect(positions[b].x).toBeCloseTo(10, 2);
  });

  it('evaluates every frame from the rest pose, so frame order does not matter (S7)', () => {
    const { scene, a, b } = sceneWithLine();
    addConstraint(scene, { type: 'distance', a, b, value: 90 });
    setPointKey(scene, a, 0, { x: 10, y: 10 }, 'linear');
    setPointKey(scene, a, 10, { x: 50, y: 10 }, 'linear');
    const forward = [0, 3, 7, 10].map(f => evaluate(scene, { frame: f }).positions);
    const backward = [10, 7, 3, 0].map(f => evaluate(scene, { frame: f }).positions).reverse();
    expect(backward).toEqual(forward);
    expect(forward[1][a].x).toBeCloseTo(22, 6);
  });

  it('moves a point along a motion path', () => {
    const { scene, line, layer } = sceneWithLine();
    const p = addPoint(scene, { x: 0, y: 0 });
    const q = addPoint(scene, { x: 5, y: 5 });
    addShape(scene, layer, { type: 'line', a: p, b: q, stroke: defaultStroke(), fill: defaultFill() });
    scene.animation.frames = 11;
    setMotionPath(scene, p, line);
    const end = evaluate(scene, { frame: 10 }).positions[p];
    expect(end.x).toBeCloseTo(100, 4);
    expect(end.y).toBeCloseTo(40, 4);
    expect(scene.shapes[line].guide).toBe(true);
  });

  it('interpolates with easing from the segment start key', () => {
    const keys = [{ frame: 0, easing: 'step' as const, v: 0 }, { frame: 10, easing: 'linear' as const, v: 10 }];
    expect(interpolate(keys, 5, k => [k.v])).toEqual([0]);
    expect(interpolate(keys, 10, k => [k.v])).toEqual([10]);
  });
});

describe('stroke modes (D17)', () => {
  it('scales, scales with a minimum, or stays fixed', () => {
    const out = (mode: 'scale' | 'min' | 'fixed') => ({ width: 16, height: 16, strokeMode: mode, minStroke: 1, encoding: 'png' as const, enabled: true });
    expect(strokeWidthPx(8, 16 / 256, out('scale'))).toBe(0.5);
    expect(strokeWidthPx(8, 16 / 256, out('min'))).toBe(1);
    expect(strokeWidthPx(2, 16 / 256, out('fixed'))).toBe(2);
    expect(strokeWidthPx(0, 1, out('min'))).toBe(0);
  });
});

describe('SVG', () => {
  it('writes shapes as paths and leaves guides out', () => {
    const { scene, line } = sceneWithLine();
    const svg1 = sceneToSvg(scene, evaluate(scene).positions);
    expect(svg1).toContain('<path d="M10 10L100 40"');
    scene.shapes[line].guide = true;
    expect(sceneToSvg(scene, evaluate(scene).positions)).not.toContain('<path');
  });
});

describe('normalizeScene', () => {
  it('turns an empty object into a usable scene', () => {
    const s = normalizeScene({});
    expect(s.layerOrder.length).toBe(1);
    expect(s.outputOrder.length).toBeGreaterThan(0);
  });
});
