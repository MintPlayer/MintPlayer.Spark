import { lodFor, renderSeatMap } from './seat-map-renderer';
import type { SeatMapCanvas, SeatMapRenderOptions, SeatMapRenderStats } from './seat-map-renderer';
import type { SeatMapModel } from './seat-map-model';
import { visiblePlanRect } from './viewport';
import type { Viewport } from './viewport';

// The seat-map layer cache (S7). Pan and zoom must stay smooth with 50k seats, but rasterising tens of thousands of
// seats costs the GPU more than the JavaScript that issues them. So the frame is rendered once into an offscreen
// bitmap at a quantised scale (steps of √2) over the visible area plus a margin, and every following frame of a pan
// or zoom only stamps that bitmap with one drawImage. The bitmap is redrawn when the scale leaves its step, the view
// leaves the margin, or the content changes (`revision`: seat states, selection, colours). At block level the direct
// renderer is cheap enough and is used as is. Erasable TypeScript only, for the perf harness.

/** The largest bitmap side, in device pixels (well under every browser's canvas limit). */
export const LAYER_MAX_SIDE = 4096;

/** How far beyond the visible area the bitmap reaches, as a fraction of the view per side. */
export const LAYER_MARGIN = 0.35;

export interface SeatMapLayerStats extends SeatMapRenderStats {
  /** The bitmap was redrawn for this frame. */
  rebuilt: boolean;
  /** The frame was drawn straight to the canvas (block level, or no OffscreenCanvas). */
  direct: boolean;
}

type LayerCanvas = { width: number; height: number; getContext(type: '2d'): unknown };

export class SeatMapLayer {
  private canvas: LayerCanvas | null = null;
  private key = '';
  private scale = 0;
  private rect: [number, number, number, number] = [0, 0, 0, 0];
  private lastStats: SeatMapRenderStats | null = null;

  /** Creates the bitmap; null makes every frame direct. The default uses OffscreenCanvas when the browser has it. */
  createCanvas: (width: number, height: number) => LayerCanvas | null = (width, height) =>
    typeof OffscreenCanvas === 'undefined' ? null : (new OffscreenCanvas(width, height) as unknown as LayerCanvas);

  /** Drops the bitmap (the next frame redraws it). */
  invalidate(): void {
    this.key = '';
  }

  /**
   * Draws a frame. `revision` changes whenever the content changes (states, selection, highlight, colours); the
   * model's own `stateVersion` is included already.
   */
  draw(ctx: SeatMapCanvas, model: SeatMapModel, viewport: Viewport, options: SeatMapRenderOptions, revision = 0): SeatMapLayerStats {
    const lod = options.lod ?? lodFor(model, viewport);
    if (lod === 'blocks') return { ...renderSeatMap(ctx, model, viewport, options), rebuilt: false, direct: true };

    const quantised = Math.pow(2, Math.round(Math.log2(viewport.scale) * 2) / 2);
    const [vx0, vy0, vx1, vy1] = visiblePlanRect(viewport, options.width, options.height);
    const key = `${revision}|${model.stateVersion}|${options.dpr}|${lod}|${options.colors.free}|${options.colors.background}`;
    const inside = vx0 >= this.rect[0] && vy0 >= this.rect[1] && vx1 <= this.rect[2] && vy1 <= this.rect[3];
    let rebuilt = false;

    if (key !== this.key || quantised !== this.scale || !inside || !this.canvas) {
      const marginX = (vx1 - vx0) * LAYER_MARGIN, marginY = (vy1 - vy0) * LAYER_MARGIN;
      const rect: [number, number, number, number] = [vx0 - marginX, vy0 - marginY, vx1 + marginX, vy1 + marginY];
      const width = Math.ceil((rect[2] - rect[0]) * quantised * options.dpr);
      const height = Math.ceil((rect[3] - rect[1]) * quantised * options.dpr);
      if (width > LAYER_MAX_SIDE || height > LAYER_MAX_SIDE || width < 1 || height < 1) {
        return { ...renderSeatMap(ctx, model, viewport, options), rebuilt: false, direct: true };
      }
      if (!this.canvas || this.canvas.width < width || this.canvas.height < height) {
        this.canvas = this.createCanvas(Math.max(width, this.canvas?.width ?? 0), Math.max(height, this.canvas?.height ?? 0));
      }
      const layer = this.canvas?.getContext('2d') as SeatMapCanvas | null | undefined;
      if (!this.canvas || !layer) {
        return { ...renderSeatMap(ctx, model, viewport, options), rebuilt: false, direct: true };
      }
      layer.setTransform(1, 0, 0, 1, 0, 0);
      layer.clearRect(0, 0, this.canvas.width, this.canvas.height);
      this.lastStats = renderSeatMap(layer, model, { scale: quantised, tx: -rect[0] * quantised, ty: -rect[1] * quantised }, {
        ...options,
        width: width / options.dpr,
        height: height / options.dpr,
        lod,
      });
      this.key = key;
      this.scale = quantised;
      this.rect = rect;
      rebuilt = true;
    }

    // Stamp the bitmap: plan rect → screen.
    const r = this.rect;
    const pixelsWide = Math.ceil((r[2] - r[0]) * this.scale * options.dpr);
    const pixelsHigh = Math.ceil((r[3] - r[1]) * this.scale * options.dpr);
    ctx.setTransform(options.dpr, 0, 0, options.dpr, 0, 0);
    ctx.fillStyle = options.colors.background;
    ctx.fillRect(0, 0, options.width, options.height);
    ctx.drawImage(this.canvas as unknown as CanvasImageSource, 0, 0, pixelsWide, pixelsHigh,
      r[0] * viewport.scale + viewport.tx, r[1] * viewport.scale + viewport.ty,
      (r[2] - r[0]) * viewport.scale, (r[3] - r[1]) * viewport.scale);
    const last = this.lastStats ?? { lod, seatsDrawn: 0, blocksDrawn: 0, pathFills: 0 };
    return { ...last, lod, rebuilt, direct: false };
  }
}
