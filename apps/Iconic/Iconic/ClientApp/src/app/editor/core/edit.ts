import type { Vec } from './geometry';
import {
  constraintPointIds, newId, orphanPoints, replaceConstraintPoint, replacePoint, shapePoints,
  type Derivation, type Easing, type Id, type Layer, type LayerKind, type Scene, type SceneConstraint, type Shape,
} from './model';
import type { Positions } from './shapes';

/**
 * Edits on a scene draft. Each function mutates the draft it is given; the editor clones the scene, applies one or
 * more edits and commits the result as one undo step (history.ts).
 */

export function addPoint(scene: Scene, at: Vec, derive?: Derivation): Id {
  const id = newId('p');
  scene.points[id] = { x: round(at.x), y: round(at.y), ...(derive ? { derive } : {}) };
  return id;
}

export function addShape(scene: Scene, layer: Id, shape: Shape): Id {
  const id = newId('s');
  scene.shapes[id] = shape;
  scene.layers[layer].shapes.push(id);
  return id;
}

/** Writes solved positions back to the free points (rounded to 1/1000 px so saves stay small). */
export function applyPositions(scene: Scene, positions: Positions): void {
  for (const [id, p] of Object.entries(scene.points)) {
    const at = positions[id];
    if (!at || p.derive) continue;
    p.x = round(at.x);
    p.y = round(at.y);
  }
}

/**
 * Snapping two points together (D4): `from` disappears and everything that named it names `into`. A constraint that
 * becomes trivial (both ends the same point) goes.
 */
export function mergePoints(scene: Scene, from: Id, into: Id): void {
  if (from === into || !scene.points[from] || !scene.points[into]) return;
  for (const [id, shape] of Object.entries(scene.shapes)) scene.shapes[id] = replacePoint(shape, from, into);
  for (const [id, c] of Object.entries(scene.constraints)) {
    const next = replaceConstraintPoint(c, from, into);
    const ids = constraintPointIds(next);
    if ((next.type === 'horizontal' || next.type === 'vertical' || next.type === 'distance') && ids[0] === ids[1]) delete scene.constraints[id];
    else scene.constraints[id] = next;
  }
  for (const p of Object.values(scene.points)) {
    const d = p.derive;
    if (d?.type === 'midpoint') { if (d.a === from) d.a = into; if (d.b === from) d.b = into; }
  }
  const track = scene.animation.tracks[from];
  if (track && !scene.animation.tracks[into]) scene.animation.tracks[into] = track;
  delete scene.animation.tracks[from];
  if (scene.points[from].fixed) scene.points[into].fixed = true;
  delete scene.points[from];
}

/** Split (D15): the shape gets its own copy of a shared point, so it no longer moves with the others. */
export function splitPoint(scene: Scene, point: Id, shapeId: Id): Id | null {
  const shape = scene.shapes[shapeId];
  const p = scene.points[point];
  if (!shape || !p || !shapePoints(shape).includes(point)) return null;
  const copy = newId('p');
  scene.points[copy] = { x: p.x, y: p.y };
  scene.shapes[shapeId] = replacePoint(shape, point, copy);
  return copy;
}

/** Shapes sharing a point, for the Split menu. */
export function shapesUsing(scene: Scene, point: Id): Id[] {
  return Object.entries(scene.shapes).filter(([, s]) => shapePoints(s).includes(point)).map(([id]) => id);
}

export function deleteShapes(scene: Scene, ids: Iterable<Id>): void {
  const set = new Set(ids);
  for (const id of set) delete scene.shapes[id];
  for (const layer of Object.values(scene.layers)) layer.shapes = layer.shapes.filter(s => !set.has(s));
  // Derived points on a deleted shape become plain points where they are.
  for (const p of Object.values(scene.points)) {
    const d = p.derive;
    if ((d?.type === 'onPath' && set.has(d.shape)) || (d?.type === 'intersect' && (set.has(d.s1) || set.has(d.s2)))) delete p.derive;
  }
  for (const [id, track] of Object.entries(scene.animation.tracks)) if (track.path && set.has(track.path.shape)) delete scene.animation.tracks[id].path;
  removeOrphans(scene);
}

/** Deleting points deletes the shapes that need them, and the constraints that name them. */
export function deletePoints(scene: Scene, ids: Iterable<Id>): void {
  const set = new Set(ids);
  const shapes = Object.entries(scene.shapes).filter(([, s]) => shapePoints(s).some(p => set.has(p))).map(([id]) => id);
  for (const id of set) delete scene.points[id];
  for (const [id, c] of Object.entries(scene.constraints)) if (constraintPointIds(c).some(p => set.has(p))) delete scene.constraints[id];
  for (const id of set) delete scene.animation.tracks[id];
  for (const [id, p] of Object.entries(scene.points)) {
    if (p.derive?.type === 'midpoint' && (set.has(p.derive.a) || set.has(p.derive.b))) delete scene.points[id].derive;
  }
  deleteShapes(scene, shapes);
}

/** Points and constraints nothing needs any more. */
export function removeOrphans(scene: Scene): void {
  for (const [id, c] of Object.entries(scene.constraints)) if (constraintPointIds(c).some(p => !scene.points[p])) delete scene.constraints[id];
  for (const id of orphanPoints(scene)) {
    delete scene.points[id];
    delete scene.animation.tracks[id];
  }
}

export function addConstraint(scene: Scene, c: SceneConstraint): Id {
  const id = newId('c');
  scene.constraints[id] = c;
  return id;
}

export function addLayer(scene: Scene, kind: LayerKind, name?: string, above?: Id): Id {
  const id = newId('l');
  const count = scene.layerOrder.length + 1;
  const layer: Layer = { name: name ?? (kind === 'paint' ? `Paint ${count}` : `Layer ${count}`), kind, visible: true, locked: false, opacity: 1, shapes: [], bitmaps: {} };
  scene.layers[id] = layer;
  const at = above ? scene.layerOrder.indexOf(above) + 1 : scene.layerOrder.length;
  scene.layerOrder.splice(at <= 0 ? scene.layerOrder.length : at, 0, id);
  return id;
}

export function removeLayer(scene: Scene, id: Id): void {
  const layer = scene.layers[id];
  if (!layer || scene.layerOrder.length <= 1) return;
  deleteShapes(scene, layer.shapes);
  delete scene.layers[id];
  delete scene.animation.layerTracks[id];
  scene.layerOrder = scene.layerOrder.filter(l => l !== id);
}

export function moveLayer(scene: Scene, id: Id, delta: number): void {
  const i = scene.layerOrder.indexOf(id);
  const j = Math.max(0, Math.min(scene.layerOrder.length - 1, i + delta));
  if (i < 0 || i === j) return;
  scene.layerOrder.splice(i, 1);
  scene.layerOrder.splice(j, 0, id);
}

/** Moves shapes within their layer: +1 is up (drawn later). */
export function reorderShape(scene: Scene, shape: Id, delta: number): void {
  for (const layer of Object.values(scene.layers)) {
    const i = layer.shapes.indexOf(shape);
    if (i < 0) continue;
    const j = Math.max(0, Math.min(layer.shapes.length - 1, i + delta));
    layer.shapes.splice(i, 1);
    layer.shapes.splice(j, 0, shape);
  }
}

/** Copies shapes with their own new points, offset a little, into the same layers. Returns the new shape ids. */
export function duplicateShapes(scene: Scene, ids: Id[], offset: Vec = { x: 8, y: 8 }): Id[] {
  const map = new Map<Id, Id>();
  const out: Id[] = [];
  for (const id of ids) {
    const shape = scene.shapes[id];
    if (!shape) continue;
    let copy: Shape = structuredClone(shape);
    for (const p of shapePoints(shape)) {
      if (!map.has(p)) {
        const src = scene.points[p];
        map.set(p, addPoint(scene, { x: src.x + offset.x, y: src.y + offset.y }));
      }
      copy = replacePoint(copy, p, map.get(p)!);
    }
    const layer = Object.entries(scene.layers).find(([, l]) => l.shapes.includes(id))?.[0];
    if (layer) out.push(addShape(scene, layer, copy));
  }
  return out;
}

/** Translates points (a whole-shape drag). Fixed and derived points stay. */
export function translatePoints(scene: Scene, ids: Iterable<Id>, delta: Vec): void {
  for (const id of new Set(ids)) {
    const p = scene.points[id];
    if (!p || p.fixed || p.derive) continue;
    p.x = round(p.x + delta.x);
    p.y = round(p.y + delta.y);
  }
}

/** Sets (or replaces) a point's keyframe at a frame. */
export function setPointKey(scene: Scene, point: Id, frame: number, at: Vec, easing: Easing = 'easeInOut'): void {
  const track = (scene.animation.tracks[point] ??= { keys: [] });
  track.keys = track.keys.filter(k => k.frame !== frame);
  track.keys.push({ frame, x: round(at.x), y: round(at.y), easing });
  track.keys.sort((a, b) => a.frame - b.frame);
}

export function removePointKey(scene: Scene, point: Id, frame: number): void {
  const track = scene.animation.tracks[point];
  if (!track) return;
  track.keys = track.keys.filter(k => k.frame !== frame);
  if (track.path) track.path.keys = track.path.keys.filter(k => k.frame !== frame);
  if (track.keys.length === 0 && !track.path?.keys.length) delete scene.animation.tracks[point];
}

/** Puts a point on a guide shape (a motion path, D5), moving from `from` to `to` over the whole animation. */
export function setMotionPath(scene: Scene, point: Id, shape: Id, from = 0, to = 1): void {
  const track = (scene.animation.tracks[point] ??= { keys: [] });
  track.path = { shape, keys: [{ frame: 0, t: from, easing: 'linear' }, { frame: Math.max(1, scene.animation.frames - 1), t: to, easing: 'linear' }] };
  if (scene.shapes[shape]) scene.shapes[shape].guide = true;
}

/** Frames that have a key anywhere, for the timeline's ticks. */
export function keyedFrames(scene: Scene): number[] {
  const set = new Set<number>();
  for (const t of Object.values(scene.animation.tracks)) {
    for (const k of t.keys) set.add(k.frame);
    for (const k of t.path?.keys ?? []) set.add(k.frame);
  }
  for (const t of Object.values(scene.animation.layerTracks)) for (const k of t.keys) set.add(k.frame);
  return [...set].sort((a, b) => a - b);
}

export const round = (v: number) => Math.round(v * 1000) / 1000;
