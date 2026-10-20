// Pan and zoom of a seat map (S7): screen = plan × scale + translation. Pure and framework-free (erasable TypeScript
// only, for the perf harness).

export interface Viewport {
  scale: number;
  tx: number;
  ty: number;
}

export const MIN_ZOOM_FACTOR = 0.5;
export const MAX_SCALE = 40;

/** The viewport that shows the whole `width × height` plan centred in a `viewWidth × viewHeight` canvas. */
export function fitViewport(width: number, height: number, viewWidth: number, viewHeight: number, padding = 16): Viewport {
  const scale = Math.max(1e-6, Math.min((viewWidth - 2 * padding) / Math.max(width, 1), (viewHeight - 2 * padding) / Math.max(height, 1)));
  return { scale, tx: (viewWidth - width * scale) / 2, ty: (viewHeight - height * scale) / 2 };
}

/** Zooms by `factor` around the screen point (sx, sy), clamped between half the fit scale and {@link MAX_SCALE}. */
export function zoomAt(viewport: Viewport, factor: number, sx: number, sy: number, fitScale: number): Viewport {
  const scale = Math.min(MAX_SCALE, Math.max(fitScale * MIN_ZOOM_FACTOR, viewport.scale * factor));
  const applied = scale / viewport.scale;
  return { scale, tx: sx - (sx - viewport.tx) * applied, ty: sy - (sy - viewport.ty) * applied };
}

export function panBy(viewport: Viewport, dx: number, dy: number): Viewport {
  return { scale: viewport.scale, tx: viewport.tx + dx, ty: viewport.ty + dy };
}

export function toPlan(viewport: Viewport, sx: number, sy: number): { x: number; y: number } {
  return { x: (sx - viewport.tx) / viewport.scale, y: (sy - viewport.ty) / viewport.scale };
}

export function toScreen(viewport: Viewport, x: number, y: number): { x: number; y: number } {
  return { x: x * viewport.scale + viewport.tx, y: y * viewport.scale + viewport.ty };
}

/** The plan rectangle the canvas shows: x0, y0, x1, y1. */
export function visiblePlanRect(viewport: Viewport, viewWidth: number, viewHeight: number): [number, number, number, number] {
  const a = toPlan(viewport, 0, 0);
  const b = toPlan(viewport, viewWidth, viewHeight);
  return [a.x, a.y, b.x, b.y];
}

/** The viewport centred on the plan point (x, y) at `scale`. */
export function centreOn(x: number, y: number, scale: number, viewWidth: number, viewHeight: number): Viewport {
  return { scale, tx: viewWidth / 2 - x * scale, ty: viewHeight / 2 - y * scale };
}
