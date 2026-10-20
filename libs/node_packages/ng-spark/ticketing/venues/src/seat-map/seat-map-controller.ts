import { SeatMapLayer } from './seat-map-layer';
import { DEFAULT_SEAT_MAP_COLORS, SeatMapColors, SeatMapRenderOptions, SeatMapRenderStats } from './seat-map-renderer';
import type { SeatMapModel } from './seat-map-model';
import { fitViewport, panBy, toPlan, Viewport, zoomAt } from './viewport';

/** What a host of the controller plugs in: content options, taps, and its own pointer handling (the editor's tools). */
export interface SeatMapControllerHooks {
  /** Selection, highlight, categories, background image; `revision` changes whenever any of them does. */
  options?: () => Partial<SeatMapRenderOptions> & { revision?: number };
  /** A tap (pointer down and up without moving), in plan units. */
  onTap?: (x: number, y: number, event: PointerEvent) => void;
  /** A primary-button press the host takes over (drawing, dragging seats); return true to stop panning. */
  onPointerDown?: (x: number, y: number, event: PointerEvent) => boolean;
  onPointerMove?: (x: number, y: number, event: PointerEvent) => void;
  onPointerUp?: (x: number, y: number, event: PointerEvent) => void;
  /** Drawn over the map in plan coordinates (the editor's handles), after every frame. */
  overlay?: (ctx: CanvasRenderingContext2D, viewport: Viewport) => void;
  /** A key on the focused canvas the host takes over (the editor moves seats with the arrows); return true to consume it. */
  onKey?: (event: KeyboardEvent) => boolean;
  /** After every frame (tests and the perf readout). */
  rendered?: (stats: SeatMapRenderStats) => void;
}

const TAP_SLOP = 6;

/**
 * Drives one seat-map canvas (S7): size and device-pixel ratio (ResizeObserver), pan with a pointer drag, pinch zoom
 * with two pointers (Pointer Events, so mouse, pen and touch share one path), wheel zoom around the cursor, keyboard
 * pan and zoom, and frames scheduled with requestAnimationFrame only when something changed. Framework-free.
 */
export class SeatMapController {
  private model: SeatMapModel | null = null;
  private viewport: Viewport = { scale: 1, tx: 0, ty: 0 };
  private fitScale = 1;
  private width = 0;
  private height = 0;
  private dpr = 1;
  private frame = 0;
  private readonly layer = new SeatMapLayer();
  private readonly pointers = new Map<number, { x: number; y: number }>();
  private pinchDistance = 0;
  private dragStart: { x: number; y: number } | null = null;
  private moved = false;
  private hostOwnsDrag = false;
  private readonly resize: ResizeObserver | null;
  colors: SeatMapColors = DEFAULT_SEAT_MAP_COLORS;

  constructor(private readonly canvas: HTMLCanvasElement, private readonly hooks: SeatMapControllerHooks = {}) {
    canvas.style.touchAction = 'none';
    canvas.addEventListener('pointerdown', this.down);
    canvas.addEventListener('pointermove', this.move);
    canvas.addEventListener('pointerup', this.up);
    canvas.addEventListener('pointercancel', this.up);
    canvas.addEventListener('wheel', this.wheel, { passive: false });
    canvas.addEventListener('keydown', this.key);
    this.resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => this.measure());
    this.resize?.observe(canvas);
    this.measure();
  }

  get currentViewport(): Viewport {
    return this.viewport;
  }

  get currentModel(): SeatMapModel | null {
    return this.model;
  }

  setModel(model: SeatMapModel | null, fit = true): void {
    const first = !this.model;
    this.model = model;
    this.layer.invalidate();
    if (model && (fit || first)) this.fit();
    this.requestRender();
  }

  /** Content changed outside the revision (the editor moved seats in place). */
  invalidate(): void {
    this.layer.invalidate();
    this.requestRender();
  }

  fit(): void {
    if (!this.model) return;
    this.viewport = fitViewport(this.model.payload.width, this.model.payload.height, this.width, this.height);
    this.fitScale = this.viewport.scale;
    this.requestRender();
  }

  zoomBy(factor: number, sx = this.width / 2, sy = this.height / 2): void {
    this.viewport = zoomAt(this.viewport, factor, sx, sy, this.fitScale);
    this.requestRender();
  }

  panBy(dx: number, dy: number): void {
    this.viewport = panBy(this.viewport, dx, dy);
    this.requestRender();
  }

  setViewport(viewport: Viewport): void {
    this.viewport = viewport;
    this.requestRender();
  }

  requestRender(): void {
    if (this.frame || typeof requestAnimationFrame === 'undefined') return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.render();
    });
  }

  /** Draws now (tests call it directly). */
  render(): void {
    const ctx = this.canvas.getContext('2d');
    if (!ctx || !this.model || this.width === 0) return;
    const extra = this.hooks.options?.() ?? {};
    const options: SeatMapRenderOptions = {
      width: this.width,
      height: this.height,
      dpr: this.dpr,
      colors: this.colors,
      ...extra,
    };
    const stats = this.layer.draw(ctx, this.model, this.viewport, options, extra.revision ?? 0);
    if (this.hooks.overlay) {
      ctx.setTransform(this.dpr * this.viewport.scale, 0, 0, this.dpr * this.viewport.scale, this.dpr * this.viewport.tx, this.dpr * this.viewport.ty);
      this.hooks.overlay(ctx, this.viewport);
    }
    this.hooks.rendered?.(stats);
  }

  destroy(): void {
    this.resize?.disconnect();
    if (this.frame) cancelAnimationFrame(this.frame);
    this.canvas.removeEventListener('pointerdown', this.down);
    this.canvas.removeEventListener('pointermove', this.move);
    this.canvas.removeEventListener('pointerup', this.up);
    this.canvas.removeEventListener('pointercancel', this.up);
    this.canvas.removeEventListener('wheel', this.wheel);
    this.canvas.removeEventListener('keydown', this.key);
  }

  /** Canvas-relative CSS pixels of an event. */
  private local(event: { clientX: number; clientY: number }): { x: number; y: number } {
    const rect = this.canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  private measure(): void {
    const rect = this.canvas.getBoundingClientRect();
    const dpr = typeof devicePixelRatio === 'number' ? devicePixelRatio : 1;
    if (rect.width === this.width && rect.height === this.height && dpr === this.dpr) return;
    const firstSize = this.width === 0;
    this.width = rect.width;
    this.height = rect.height;
    this.dpr = dpr;
    this.canvas.width = Math.round(rect.width * dpr);
    this.canvas.height = Math.round(rect.height * dpr);
    this.layer.invalidate();
    if (firstSize) this.fit();
    this.requestRender();
  }

  private readonly down = (event: PointerEvent): void => {
    const p = this.local(event);
    this.pointers.set(event.pointerId, p);
    this.canvas.setPointerCapture?.(event.pointerId);
    if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      this.pinchDistance = Math.hypot(a.x - b.x, a.y - b.y);
      this.hostOwnsDrag = false;
      return;
    }
    this.dragStart = p;
    this.moved = false;
    const plan = toPlan(this.viewport, p.x, p.y);
    this.hostOwnsDrag = event.button === 0 && !!this.hooks.onPointerDown?.(plan.x, plan.y, event);
  };

  private readonly move = (event: PointerEvent): void => {
    const previous = this.pointers.get(event.pointerId);
    const p = this.local(event);
    const plan = toPlan(this.viewport, p.x, p.y);
    if (!previous) {
      this.hooks.onPointerMove?.(plan.x, plan.y, event);
      return;
    }
    this.pointers.set(event.pointerId, p);
    if (this.dragStart && Math.hypot(p.x - this.dragStart.x, p.y - this.dragStart.y) > TAP_SLOP) this.moved = true;

    if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      const distance = Math.hypot(a.x - b.x, a.y - b.y);
      if (this.pinchDistance > 0) {
        this.viewport = zoomAt(this.viewport, distance / this.pinchDistance, (a.x + b.x) / 2, (a.y + b.y) / 2, this.fitScale);
        // Two fingers also pan: half of this pointer's movement (the pair's midpoint moves by half).
        this.viewport = panBy(this.viewport, (p.x - previous.x) / 2, (p.y - previous.y) / 2);
      }
      this.pinchDistance = distance;
      this.moved = true;
      this.requestRender();
      return;
    }
    if (this.hostOwnsDrag) {
      this.hooks.onPointerMove?.(plan.x, plan.y, event);
      return;
    }
    this.viewport = panBy(this.viewport, p.x - previous.x, p.y - previous.y);
    this.requestRender();
  };

  private readonly up = (event: PointerEvent): void => {
    const p = this.local(event);
    const plan = toPlan(this.viewport, p.x, p.y);
    const wasTap = this.pointers.size === 1 && !this.moved && event.type === 'pointerup';
    this.pointers.delete(event.pointerId);
    if (this.pointers.size < 2) this.pinchDistance = 0;
    if (this.hostOwnsDrag) this.hooks.onPointerUp?.(plan.x, plan.y, event);
    else if (wasTap) this.hooks.onTap?.(plan.x, plan.y, event);
    if (this.pointers.size === 0) {
      this.dragStart = null;
      this.hostOwnsDrag = false;
    }
  };

  private readonly wheel = (event: WheelEvent): void => {
    event.preventDefault();
    const p = this.local(event);
    this.zoomBy(Math.exp(-event.deltaY * (event.deltaMode === 1 ? 0.05 : 0.0015)), p.x, p.y);
  };

  private readonly key = (event: KeyboardEvent): void => {
    if (this.hooks.onKey?.(event)) {
      event.preventDefault();
      return;
    }
    const step = 60;
    switch (event.key) {
      case 'ArrowLeft': this.panBy(step, 0); break;
      case 'ArrowRight': this.panBy(-step, 0); break;
      case 'ArrowUp': this.panBy(0, step); break;
      case 'ArrowDown': this.panBy(0, -step); break;
      case '+': case '=': this.zoomBy(1.25); break;
      case '-': case '_': this.zoomBy(0.8); break;
      case '0': this.fit(); break;
      default: return;
    }
    event.preventDefault();
  };
}
