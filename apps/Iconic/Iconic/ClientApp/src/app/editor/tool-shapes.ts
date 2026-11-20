import { addPoint } from './core/edit';
import { svgPathData, vec, type Vec } from './core/geometry';
import { defaultFill, type FillStyle, type Id, type Scene, type Shape, type StrokeStyle } from './core/model';
import { shapePath, type Positions, type TextOutliner } from './core/shapes';
import type { Snap } from './core/snap';
import type { ToolId } from './editor-store';

/** A click of a drawing tool: where it landed and what it snapped to; a curve node may carry a dragged handle. */
export interface Anchor {
  at: Vec;
  snap: Snap;
  handle?: Vec;
}

/** How many anchors finish a tool's shape; `null` for the tools you finish yourself (Enter, double-click, closing). */
export function anchorsNeeded(tool: ToolId): number | null {
  switch (tool) {
    case 'point':
    case 'text':
    case 'hotspot': return 1;
    case 'line':
    case 'circle':
    case 'ellipse':
    case 'rect':
    case 'regular':
    case 'star': return 2;
    case 'arc': return 3;
    case 'polyline':
    case 'polygon':
    case 'curve':
    case 'closedCurve': return null;
    default: return 0;
  }
}

export const DRAWING_TOOLS: ReadonlySet<ToolId> = new Set<ToolId>(['point', 'line', 'polyline', 'curve', 'arc', 'polygon', 'closedCurve', 'circle', 'ellipse', 'rect', 'regular', 'star', 'text']);
export const PAINT_TOOLS: ReadonlySet<ToolId> = new Set<ToolId>(['pencil', 'eraser', 'bucket', 'picker']);

/**
 * The point an anchor stands for (D4, D15): the existing point it snapped to (shared), a derived midpoint or
 * on-outline point, or a new free point.
 */
export function resolveAnchor(scene: Scene, a: Anchor): Id {
  switch (a.snap.kind) {
    case 'point': if (scene.points[a.snap.id]) return a.snap.id; break;
    case 'midpoint': return addPoint(scene, a.at, { type: 'midpoint', a: a.snap.a, b: a.snap.b });
    case 'onPath': return addPoint(scene, a.at, { type: 'onPath', shape: a.snap.shape, t: a.snap.t });
  }
  return addPoint(scene, a.at);
}

export function regularVertices(center: Vec, rim: Vec, sides: number): Vec[] {
  const r = Math.hypot(rim.x - center.x, rim.y - center.y);
  const a0 = Math.atan2(rim.y - center.y, rim.x - center.x);
  return Array.from({ length: sides }, (_, i) => vec(center.x + r * Math.cos(a0 + (2 * Math.PI * i) / sides), center.y + r * Math.sin(a0 + (2 * Math.PI * i) / sides)));
}

export function starVertices(center: Vec, rim: Vec, tips: number, inner = 0.45): Vec[] {
  const r = Math.hypot(rim.x - center.x, rim.y - center.y);
  const a0 = Math.atan2(rim.y - center.y, rim.x - center.x);
  return Array.from({ length: tips * 2 }, (_, i) => {
    const rr = i % 2 === 0 ? r : r * inner;
    const a = a0 + (Math.PI * i) / tips;
    return vec(center.x + rr * Math.cos(a), center.y + rr * Math.sin(a));
  });
}

const mirror = (p: Vec, h: Vec): Vec => vec(2 * p.x - h.x, 2 * p.y - h.y);

/** Shapes the tool makes, with point ids supplied by `id(i)` (anchor i) and `extra(v)` (a point of its own). */
function shapeFor(tool: ToolId, anchors: Anchor[], closed: boolean, id: (i: number) => Id, extra: (v: Vec) => Id, stroke: StrokeStyle, fill: FillStyle, sides: number): Shape | null {
  const lineFill = defaultFill();
  switch (tool) {
    case 'line': return { type: 'line', a: id(0), b: id(1), stroke, fill: lineFill };
    case 'polyline':
    case 'polygon': {
      const close = tool === 'polygon' || closed;
      return { type: 'polyline', points: anchors.map((_, i) => id(i)), closed: close, stroke, fill: close ? fill : lineFill };
    }
    case 'curve':
    case 'closedCurve': {
      const close = tool === 'closedCurve' || closed;
      const nodes = anchors.map((a, i) => (a.handle ? { p: id(i), in: extra(mirror(a.at, a.handle)), out: extra(a.handle) } : { p: id(i) }));
      return { type: 'curve', nodes, closed: close, stroke, fill: close ? fill : lineFill };
    }
    case 'arc': return { type: 'arc', center: id(0), start: id(1), end: id(2), ccw: false, stroke, fill: lineFill };
    case 'circle': return { type: 'circle', center: id(0), rim: id(1), stroke, fill };
    case 'ellipse': return { type: 'ellipse', a: id(0), b: id(1), stroke, fill };
    case 'rect': return { type: 'rect', a: id(0), b: id(1), radius: 0, stroke, fill };
    case 'regular':
    case 'star': {
      const vertices = tool === 'regular' ? regularVertices(anchors[0].at, anchors[1].at, sides) : starVertices(anchors[0].at, anchors[1].at, sides);
      return { type: 'polyline', points: vertices.map(extra), closed: true, stroke, fill };
    }
    case 'text': return { type: 'text', anchor: id(0), text: 'Text', font: null, size: 48, align: 'left', stroke: { ...stroke, width: 0 }, fill: fill.type === 'none' ? { ...fill, type: 'solid', color1: stroke.color } : fill };
    default: return null;
  }
}

/** Builds the tool's shape into the scene, creating or sharing its points. */
export function buildShape(scene: Scene, tool: ToolId, anchors: Anchor[], closed: boolean, stroke: StrokeStyle, fill: FillStyle, sides: number): Shape | null {
  const ids = new Map<number, Id>();
  const id = (i: number) => {
    let v = ids.get(i);
    if (!v) { v = resolveAnchor(scene, anchors[i]); ids.set(i, v); }
    return v;
  };
  return shapeFor(tool, anchors, closed, id, v => addPoint(scene, v), stroke, fill, sides);
}

/** The outline of the shape being drawn, with the pointer as its next anchor, for the overlay. */
export function previewPath(tool: ToolId, anchors: Anchor[], cursor: Vec | null, stroke: StrokeStyle, fill: FillStyle, sides: number, text?: TextOutliner): string {
  const all = cursor ? [...anchors, { at: cursor, snap: { kind: 'none', at: cursor } as Snap }] : anchors;
  const needed = anchorsNeeded(tool);
  if (all.length < 2 && tool !== 'text') return '';
  const used = needed ? all.slice(0, needed) : all;
  if (tool === 'arc' && used.length === 2) {
    return svgPathData([{ c: 'M', p: used[0].at }, { c: 'L', p: used[1].at }]);
  }
  const positions: Positions = {};
  let n = 0;
  const id = (i: number) => { const k = `a${i}`; positions[k] = used[i].at; return k; };
  const extra = (v: Vec) => { const k = `x${n++}`; positions[k] = v; return k; };
  const shape = shapeFor(tool, used, false, id, extra, stroke, fill, sides);
  return shape ? svgPathData(shapePath(shape, positions, text)) : '';
}
