/**
 * The scene: what a project stores (docs/iconic_PRD.md §3). Plain JSON, keyed by id, so a save is a short list of
 * set/remove operations on paths (diff.ts, S5) and the server never has to understand it.
 *
 * Coordinates are design pixels (D16): the canvas is `width` × `height`, and every output size scales from it.
 */

export type Id = string;

export interface Scene {
  format: 1;
  width: number;
  height: number;
  /** Points are shared: two shapes that use the same id move together (D4). */
  points: Record<Id, ScenePoint>;
  /** Constraints between points and shapes, solved live (D14). */
  constraints: Record<Id, SceneConstraint>;
  layers: Record<Id, Layer>;
  /** Bottom to top. */
  layerOrder: Id[];
  shapes: Record<Id, Shape>;
  /** The images an export writes, one per size (D1, D17). */
  outputs: Record<Id, Output>;
  outputOrder: Id[];
  /** Fonts uploaded for text shapes (D23); the file is the attachment `fonts/<id>.<ext>`. */
  fonts: Record<Id, FontRef>;
  animation: Animation;
  /** The cursor's hotspot in design pixels; a .cur scales it per size. */
  hotspot: { x: number; y: number };
}

export interface ScenePoint {
  x: number;
  y: number;
  /** A fixed point never moves while solving. */
  fixed?: boolean;
  /** Set for a derived point (D15): its position follows from other things and is never dragged. */
  derive?: Derivation;
}

export type Derivation =
  | { type: 'midpoint'; a: Id; b: Id }
  | { type: 'intersect'; s1: Id; s2: Id; index: number }
  | { type: 'onPath'; shape: Id; t: number };

export type SceneConstraint =
  | { type: 'horizontal'; a: Id; b: Id }
  | { type: 'vertical'; a: Id; b: Id }
  | { type: 'distance'; a: Id; b: Id; value: number }
  | { type: 'equalLength'; l1: [Id, Id]; l2: [Id, Id] }
  | { type: 'parallel'; l1: [Id, Id]; l2: [Id, Id] }
  | { type: 'perpendicular'; l1: [Id, Id]; l2: [Id, Id] }
  | { type: 'angle'; l1: [Id, Id]; l2: [Id, Id]; value: number }
  | { type: 'pointOnLine'; p: Id; line: [Id, Id] }
  | { type: 'pointOnCircle'; p: Id; center: Id; rim: Id }
  | { type: 'midpoint'; m: Id; line: [Id, Id] }
  | { type: 'tangent'; line: [Id, Id]; center: Id; rim: Id }
  | { type: 'symmetric'; a: Id; b: Id; axis: [Id, Id] }
  | { type: 'equalRadius'; c1: [Id, Id]; c2: [Id, Id] }
  | { type: 'tangentCircles'; c1: [Id, Id]; c2: [Id, Id]; kind: 'external' | 'internal' };

export type LayerKind = 'shape' | 'paint';

export interface Layer {
  name: string;
  kind: LayerKind;
  visible: boolean;
  locked: boolean;
  opacity: number;
  /** Shape layers: the shapes, bottom to top. */
  shapes: Id[];
  /**
   * Paint layers (D2): one bitmap per output size, keyed by the size's pixel width. The value is the attachment
   * name (`paint/<layer>/<size>.png`) once saved, or a data URL in a local draft.
   */
  bitmaps: Record<string, string>;
}

export type Shape = LineShape | PolylineShape | CurveShape | ArcShape | CircleShape | EllipseShape | RectShape | TextShape;

interface ShapeBase {
  name?: string;
  stroke: StrokeStyle;
  fill: FillStyle;
  /** A guide is drawn in the editor only: construction lines and motion paths (D5). */
  guide?: boolean;
  hidden?: boolean;
}

export interface LineShape extends ShapeBase { type: 'line'; a: Id; b: Id }
export interface PolylineShape extends ShapeBase { type: 'polyline'; points: Id[]; closed: boolean }
/** A cubic Bézier path: each node is a point with optional handles (their own points). */
export interface CurveShape extends ShapeBase { type: 'curve'; nodes: CurveNode[]; closed: boolean }
export interface CurveNode { p: Id; in?: Id; out?: Id }
/** From `start` around `center` to the angle of `end`; the radius is |start - center|. */
export interface ArcShape extends ShapeBase { type: 'arc'; center: Id; start: Id; end: Id; ccw: boolean; pie?: boolean }
export interface CircleShape extends ShapeBase { type: 'circle'; center: Id; rim: Id }
/** Axis-aligned, inside the box from `a` to `b`. */
export interface EllipseShape extends ShapeBase { type: 'ellipse'; a: Id; b: Id }
export interface RectShape extends ShapeBase { type: 'rect'; a: Id; b: Id; radius: number }
/** Live text (D23), drawn from the font's outlines so every output matches. */
export interface TextShape extends ShapeBase {
  type: 'text';
  anchor: Id;
  text: string;
  font: Id | null;
  size: number;
  align: 'left' | 'center' | 'right';
}

export type ShapeType = Shape['type'];

export interface StrokeStyle {
  color: string;
  /** In design pixels; 0 draws no outline. */
  width: number;
  dash: number[];
  cap: CanvasLineCap;
  join: CanvasLineJoin;
  opacity: number;
}

export type FillType = 'none' | 'solid' | 'linear' | 'radial';

export interface FillStyle {
  type: FillType;
  color1: string;
  color2: string;
  /** Linear gradients: degrees, 0 = left to right. */
  angle: number;
  opacity: number;
}

/** How an outline's width follows the output size (D17). */
export type StrokeMode = 'scale' | 'min' | 'fixed';

export interface Output {
  width: number;
  height: number;
  strokeMode: StrokeMode;
  /** For `min`: the thinnest outline in output pixels (default 1). */
  minStroke: number;
  /** For an ICO or CUR entry: PNG, or a classic BMP with an AND mask (older readers). */
  encoding: 'png' | 'bmp';
  /** Left out of exports without deleting it. */
  enabled: boolean;
}

export interface FontRef {
  name: string;
  file: string;
}

/**
 * Animation (D5, D19): keyframes on points. Every frame is solved from the rest pose with the keyed points held,
 * so the constraints keep the drawing together while it moves.
 */
export interface Animation {
  fps: number;
  frames: number;
  loop: boolean;
  /** GIF transparency: pixels below this alpha (0–255) become transparent, the rest are blended onto `matte`. */
  alphaThreshold: number;
  matte: string;
  tracks: Record<Id, PointTrack>;
  /** Layer opacity over time. */
  layerTracks: Record<Id, OpacityTrack>;
}

export type Easing = 'linear' | 'easeIn' | 'easeOut' | 'easeInOut' | 'step';

export interface PointKey {
  frame: number;
  x: number;
  y: number;
  easing: Easing;
}

/** A point's motion: free keyframes, or a position along a guide shape (a motion path) keyed by `t` in 0..1. */
export interface PointTrack {
  keys: PointKey[];
  path?: { shape: Id; keys: { frame: number; t: number; easing: Easing }[] };
}

export interface OpacityTrack {
  keys: { frame: number; value: number; easing: Easing }[];
}

let counter = 0;

/** A short, collision-safe id: random plus a counter, so ids made in the same millisecond differ. */
export function newId(prefix = ''): Id {
  counter = (counter + 1) % 1296;
  const random = Math.floor(Math.random() * 2 ** 40).toString(36);
  return `${prefix}${random}${counter.toString(36).padStart(2, '0')}`;
}

export const defaultStroke = (): StrokeStyle => ({ color: '#212529', width: 8, dash: [], cap: 'round', join: 'round', opacity: 1 });
export const defaultFill = (): FillStyle => ({ type: 'none', color1: '#0d6efd', color2: '#6f42c1', angle: 90, opacity: 1 });
export const defaultPlaneFill = (): FillStyle => ({ ...defaultFill(), type: 'solid' });

export const STANDARD_SIZES = [16, 24, 32, 48, 64, 128, 256] as const;

export function newOutput(size: number): Output {
  return {
    width: size,
    height: size,
    // Small sizes keep a visible outline; 256 just scales (D17).
    strokeMode: size <= 48 ? 'min' : 'scale',
    minStroke: 1,
    encoding: size >= 256 ? 'png' : 'png',
    enabled: true,
  };
}

export function emptyScene(width = 256, height = 256, sizes: readonly number[] = [16, 32, 48, 256]): Scene {
  const layerId = newId('l');
  const outputs: Record<Id, Output> = {};
  const outputOrder: Id[] = [];
  for (const size of sizes) {
    const id = newId('o');
    outputs[id] = newOutput(size);
    outputOrder.push(id);
  }
  return {
    format: 1,
    width,
    height,
    points: {},
    constraints: {},
    layers: { [layerId]: { name: 'Layer 1', kind: 'shape', visible: true, locked: false, opacity: 1, shapes: [], bitmaps: {} } },
    layerOrder: [layerId],
    shapes: {},
    outputs,
    outputOrder,
    fonts: {},
    animation: { fps: 12, frames: 24, loop: true, alphaThreshold: 128, matte: '#ffffff', tracks: {}, layerTracks: {} },
    hotspot: { x: 0, y: 0 },
  };
}

/** Fills the fields an older or hand-written scene lacks, so the editor can rely on them. */
export function normalizeScene(raw: unknown): Scene {
  const base = emptyScene();
  const s = (raw && typeof raw === 'object' ? raw : {}) as Partial<Scene>;
  if (!s.layerOrder || s.layerOrder.length === 0) return { ...base, ...pick(s, ['width', 'height']) };
  return {
    format: 1,
    width: s.width ?? 256,
    height: s.height ?? 256,
    points: s.points ?? {},
    constraints: s.constraints ?? {},
    layers: s.layers ?? {},
    layerOrder: s.layerOrder,
    shapes: s.shapes ?? {},
    outputs: s.outputs ?? base.outputs,
    outputOrder: s.outputOrder ?? base.outputOrder,
    fonts: s.fonts ?? {},
    animation: { ...base.animation, ...(s.animation ?? {}) },
    hotspot: s.hotspot ?? { x: 0, y: 0 },
  };
}

function pick<T extends object, K extends keyof T>(o: T, keys: K[]): Partial<T> {
  const out: Partial<T> = {};
  for (const k of keys) if (o[k] !== undefined) out[k] = o[k];
  return out;
}

/** Every point id a shape uses, in order (handles included). */
export function shapePoints(shape: Shape): Id[] {
  switch (shape.type) {
    case 'line': return [shape.a, shape.b];
    case 'polyline': return [...shape.points];
    case 'curve': return shape.nodes.flatMap(n => [n.in, n.p, n.out].filter((x): x is Id => !!x));
    case 'arc': return [shape.center, shape.start, shape.end];
    case 'circle': return [shape.center, shape.rim];
    case 'ellipse':
    case 'rect': return [shape.a, shape.b];
    case 'text': return [shape.anchor];
  }
}

/** Replaces point `from` by `to` everywhere a shape names it. */
export function replacePoint(shape: Shape, from: Id, to: Id): Shape {
  const r = (id: Id) => (id === from ? to : id);
  const ro = (id: Id | undefined) => (id === undefined ? undefined : r(id));
  switch (shape.type) {
    case 'line': return { ...shape, a: r(shape.a), b: r(shape.b) };
    case 'polyline': return { ...shape, points: shape.points.map(r) };
    case 'curve': return { ...shape, nodes: shape.nodes.map(n => ({ p: r(n.p), ...(n.in ? { in: ro(n.in) } : {}), ...(n.out ? { out: ro(n.out) } : {}) })) };
    case 'arc': return { ...shape, center: r(shape.center), start: r(shape.start), end: r(shape.end) };
    case 'circle': return { ...shape, center: r(shape.center), rim: r(shape.rim) };
    case 'ellipse':
    case 'rect': return { ...shape, a: r(shape.a), b: r(shape.b) };
    case 'text': return { ...shape, anchor: r(shape.anchor) };
  }
}

/** Every point a constraint names. */
export function constraintPointIds(c: SceneConstraint): Id[] {
  switch (c.type) {
    case 'horizontal':
    case 'vertical':
    case 'distance': return [c.a, c.b];
    case 'equalLength':
    case 'parallel':
    case 'perpendicular':
    case 'angle': return [...c.l1, ...c.l2];
    case 'pointOnLine': return [c.p, ...c.line];
    case 'pointOnCircle': return [c.p, c.center, c.rim];
    case 'midpoint': return [c.m, ...c.line];
    case 'tangent': return [...c.line, c.center, c.rim];
    case 'symmetric': return [c.a, c.b, ...c.axis];
    case 'equalRadius':
    case 'tangentCircles': return [...c.c1, ...c.c2];
  }
}

export function replaceConstraintPoint(c: SceneConstraint, from: Id, to: Id): SceneConstraint {
  return JSON.parse(JSON.stringify(c), (key, value) => (key !== 'type' && value === from ? to : value)) as SceneConstraint;
}

/** Shapes that close: a plane gets a fill (the brief's "planes"). */
export function isClosed(shape: Shape): boolean {
  switch (shape.type) {
    case 'circle':
    case 'ellipse':
    case 'rect': return true;
    case 'polyline':
    case 'curve': return shape.closed;
    case 'arc': return !!shape.pie;
    case 'text': return true;
    case 'line': return false;
  }
}

/** The layer holding a shape. */
export function layerOf(scene: Scene, shapeId: Id): Id | undefined {
  return scene.layerOrder.find(l => scene.layers[l]?.shapes.includes(shapeId));
}

/** Points no shape, constraint, derivation or track uses any more. */
export function orphanPoints(scene: Scene): Id[] {
  const used = new Set<Id>();
  for (const shape of Object.values(scene.shapes)) for (const p of shapePoints(shape)) used.add(p);
  for (const c of Object.values(scene.constraints)) for (const p of constraintPointIds(c)) used.add(p);
  for (const [id, p] of Object.entries(scene.points)) {
    if (p.derive?.type === 'midpoint') { used.add(p.derive.a); used.add(p.derive.b); }
    if (p.derive) used.add(id);
  }
  return Object.keys(scene.points).filter(id => !used.has(id));
}
