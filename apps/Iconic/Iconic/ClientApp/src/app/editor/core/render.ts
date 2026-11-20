import { boundsOf, flatten, scalePath, tracePath } from './geometry';
import { layerOpacity } from './evaluate';
import { isClosed, type FillStyle, type Id, type Output, type Scene, type Shape, type StrokeStyle } from './model';
import { shapePath, shapesInOrder, type Positions, type TextOutliner } from './shapes';

/** Bitmaps for paint layers (D2): the one for a layer at an output width, if painted. */
export interface PaintSource {
  get(layer: Id, width: number): CanvasImageSource | null;
}

export interface RenderOptions {
  /** Target size in pixels. */
  width: number;
  height: number;
  /** The output being rendered decides the stroke mode (D17). Absent: strokes scale. */
  output?: Output;
  positions: Positions;
  frame?: number | null;
  paint?: PaintSource;
  text?: TextOutliner;
  /** The editor shows guides (dashed, faint); exports never do. */
  showGuides?: boolean;
  /** Which paint size the editor shows for paint layers. Defaults to the closest one at or above the target. */
  paintWidth?: number;
}

type Ctx = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

/** The outline width in output pixels for a design width (D17). */
export function strokeWidthPx(designWidth: number, scale: number, output?: Output): number {
  if (designWidth <= 0) return 0;
  switch (output?.strokeMode ?? 'scale') {
    case 'scale': return designWidth * scale;
    case 'min': return Math.max(designWidth * scale, output?.minStroke ?? 1);
    case 'fixed': return designWidth;
  }
}

/**
 * Draws the scene into a context of the target size: shapes anti-aliased (D16), paint layers pixel for pixel. The
 * same function draws the editor's canvas, every export and the thumbnail, so they cannot disagree.
 */
export function renderScene(ctx: Ctx, scene: Scene, o: RenderOptions): void {
  const sx = o.width / scene.width, sy = o.height / scene.height;
  ctx.save();
  ctx.clearRect(0, 0, o.width, o.height);
  for (const layerId of scene.layerOrder) {
    const layer = scene.layers[layerId];
    if (!layer?.visible) continue;
    const opacity = layerOpacity(scene, layerId, o.frame ?? null);
    if (opacity <= 0) continue;
    // A translucent layer is composited as a whole, so its own shapes don't show through each other.
    const target = opacity < 1 ? offscreen(o.width, o.height) : null;
    const c: Ctx = target?.ctx ?? ctx;
    if (layer.kind === 'paint') {
      const width = o.paintWidth ?? pickPaintWidth(scene, layer.bitmaps, o.width);
      const img = width != null ? o.paint?.get(layerId, width) : null;
      if (img) {
        c.imageSmoothingEnabled = width! < o.width ? false : true;
        c.drawImage(img, 0, 0, o.width, o.height);
        c.imageSmoothingEnabled = true;
      }
    } else {
      for (const id of layer.shapes) {
        const shape = scene.shapes[id];
        if (!shape || shape.hidden) continue;
        if (shape.guide && !o.showGuides) continue;
        drawShape(c, shape, o.positions, sx, sy, o.output, o.text);
      }
    }
    if (target) {
      ctx.globalAlpha = opacity;
      ctx.drawImage(target.canvas, 0, 0);
      ctx.globalAlpha = 1;
    }
  }
  ctx.restore();
}

/** The paint size for a target width: the exact one, else the smallest larger one, else the largest. */
export function pickPaintWidth(scene: Scene, bitmaps: Record<string, string>, width: number): number | null {
  const sizes = Object.keys(bitmaps).map(Number).filter(n => n > 0).sort((a, b) => a - b);
  if (sizes.length === 0) return null;
  return sizes.find(s => s >= width) ?? sizes[sizes.length - 1];
}

function drawShape(c: Ctx, shape: Shape, pos: Positions, sx: number, sy: number, output: Output | undefined, text?: TextOutliner): void {
  const design = shapePath(shape, pos, text);
  if (design.length === 0) return;
  const path = scalePath(design, sx, sy);
  const scale = Math.min(sx, sy);
  c.beginPath();
  tracePath(c as CanvasRenderingContext2D, path);
  if (shape.guide) {
    c.save();
    c.setLineDash([6, 4]);
    c.lineWidth = 1;
    c.strokeStyle = 'rgba(13,110,253,0.6)';
    c.stroke();
    c.restore();
    return;
  }
  if (isClosed(shape) && shape.fill.type !== 'none') {
    c.fillStyle = fillPaint(c, shape.fill, path) ?? 'transparent';
    c.globalAlpha = shape.fill.opacity;
    c.fill('evenodd');
    c.globalAlpha = 1;
  }
  const w = strokeWidthPx(shape.stroke.width, scale, output);
  if (w > 0) applyStroke(c, shape.stroke, w, scale, output);
}

function applyStroke(c: Ctx, s: StrokeStyle, widthPx: number, scale: number, output?: Output): void {
  c.lineWidth = widthPx;
  c.lineCap = s.cap;
  c.lineJoin = s.join;
  // Dashes keep their proportion to the outline's width, so a thickened hairline keeps its pattern readable.
  const k = s.width > 0 ? widthPx / s.width : scale;
  c.setLineDash(s.dash.map(d => d * (output?.strokeMode === 'fixed' ? 1 : k)));
  c.strokeStyle = s.color;
  c.globalAlpha = s.opacity;
  c.stroke();
  c.globalAlpha = 1;
  c.setLineDash([]);
}

/** A gradient spans the shape's own box, so it moves and scales with the shape. */
function fillPaint(c: Ctx, fill: FillStyle, path: ReturnType<typeof scalePath>): string | CanvasGradient | null {
  if (fill.type === 'solid') return fill.color1;
  const b = boundsOf(flatten(path, 1).flatMap(s => s.points));
  if (!b) return null;
  const cx = (b.minX + b.maxX) / 2, cy = (b.minY + b.maxY) / 2;
  if (fill.type === 'linear') {
    const a = (fill.angle * Math.PI) / 180;
    const dx = Math.cos(a), dy = Math.sin(a);
    // Half the box's extent along the direction, so the gradient runs edge to edge.
    const half = (Math.abs(dx) * (b.maxX - b.minX) + Math.abs(dy) * (b.maxY - b.minY)) / 2;
    const g = c.createLinearGradient(cx - dx * half, cy - dy * half, cx + dx * half, cy + dy * half);
    g.addColorStop(0, fill.color1);
    g.addColorStop(1, fill.color2);
    return g;
  }
  const r = Math.max(b.maxX - b.minX, b.maxY - b.minY) / 2;
  const g = c.createRadialGradient(cx, cy, 0, cx, cy, Math.max(r, 0.001));
  g.addColorStop(0, fill.color1);
  g.addColorStop(1, fill.color2);
  return g;
}

function offscreen(width: number, height: number): { canvas: OffscreenCanvas | HTMLCanvasElement; ctx: Ctx } {
  if (typeof OffscreenCanvas !== 'undefined') {
    const canvas = new OffscreenCanvas(width, height);
    return { canvas, ctx: canvas.getContext('2d')! };
  }
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  return { canvas, ctx: canvas.getContext('2d')! };
}

/** Renders to a fresh canvas and returns its RGBA pixels: what every bitmap writer encodes. */
export function renderToImageData(scene: Scene, o: RenderOptions): ImageData {
  const { ctx } = offscreen(o.width, o.height);
  renderScene(ctx, scene, o);
  return ctx.getImageData(0, 0, o.width, o.height);
}

/** The enabled outputs, in order. */
export function enabledOutputs(scene: Scene): { id: Id; output: Output }[] {
  return scene.outputOrder.map(id => ({ id, output: scene.outputs[id] })).filter(x => x.output?.enabled);
}

/** Every shape id in the scene in drawing order, for hit testing from the top. */
export function topDownShapes(scene: Scene): Id[] {
  return [...shapesInOrder(scene)].map(s => s.id).reverse();
}
