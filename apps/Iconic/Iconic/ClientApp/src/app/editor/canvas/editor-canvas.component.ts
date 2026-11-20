import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, HostListener, inject, signal, viewChild } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { addLayer, addShape, applyPositions, deletePoints, deleteShapes, duplicateShapes, mergePoints, setPointKey, translatePoints } from '../core/edit';
import { evaluate } from '../core/evaluate';
import { boundsOf, flatten, svgPathData, vec, type Vec } from '../core/geometry';
import { constraintPointIds, isClosed, layerOf, shapePoints, type Id, type Scene, type SceneConstraint } from '../core/model';
import { renderScene, renderToImageData, topDownShapes } from '../core/render';
import { shapePath } from '../core/shapes';
import { hitPoint, hitShape, snap, type Snap } from '../core/snap';
import { EditorStore, type ToolId } from '../editor-store';
import { floodFill, hexToRgba, plotLine, rgbToHex } from '../paint-store';
import { anchorsNeeded, buildShape, DRAWING_TOOLS, PAINT_TOOLS, previewPath, resolveAnchor, type Anchor } from '../tool-shapes';

type Gesture =
  | { kind: 'none' }
  | { kind: 'pan'; start: Vec; pan0: Vec }
  | { kind: 'pinch'; d0: number; mid0: Vec; zoom0: number; pan0: Vec }
  | { kind: 'point'; id: Id; start: Vec; moved: boolean }
  | { kind: 'shapes'; ids: Id[]; points: Id[]; start: Vec; base: Scene; moved: boolean }
  | { kind: 'marquee'; start: Vec; end: Vec; additive: boolean }
  | { kind: 'draw'; start: Vec; moved: boolean }
  | { kind: 'paint'; layer: Id; width: number; before: ImageData; last: { x: number; y: number } | null };

interface PointView { id: Id; x: number; y: number; cls: string; selected: boolean; derived: boolean; fixed: boolean }
interface GlyphView { id: Id; x: number; y: number; text: string; conflict: boolean; redundant: boolean }

const SYMBOL: Record<SceneConstraint['type'], string> = {
  horizontal: 'H', vertical: 'V', distance: '↔', equalLength: '=', parallel: '∥', perpendicular: '⊥', angle: '∠',
  pointOnLine: '•—', pointOnCircle: '•○', midpoint: 'M', tangent: 'T', symmetric: '⇋', equalRadius: '=r', tangentCircles: '○○',
};

/**
 * The drawing surface: a canvas that renders the scene with the same code as the exports, and an SVG overlay for
 * points, selection, constraint glyphs and the tool's preview. Pointer Events throughout, so mouse, pen and touch
 * share one path; two fingers pan and zoom, and a loupe shows what is under a finger (D18).
 */
@Component({
  selector: 'app-editor-canvas',
  templateUrl: './editor-canvas.component.html',
  styleUrl: './editor-canvas.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsButtonTypeDirective, SparkIconComponent],
})
export class EditorCanvasComponent {
  protected readonly colors = Color;
  protected readonly store = inject(EditorStore);

  private readonly stage = viewChild.required<ElementRef<HTMLDivElement>>('stage');
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly loupeCanvas = viewChild.required<ElementRef<HTMLCanvasElement>>('loupeCanvas');

  readonly size = signal({ width: 300, height: 300 });
  readonly zoom = signal(1);
  readonly pan = signal<Vec>({ x: 0, y: 0 });
  private fitted = false;

  /** The shape being drawn. */
  readonly anchors = signal<Anchor[]>([]);
  readonly cursor = signal<Vec | null>(null);
  readonly hoverSnap = signal<Snap | null>(null);
  readonly marquee = signal<{ x: number; y: number; w: number; h: number } | null>(null);
  readonly loupe = signal<{ x: number; y: number } | null>(null);
  readonly spaceDown = signal(false);

  private gesture: Gesture = { kind: 'none' };
  private readonly pointers = new Map<number, Vec>();
  private art: HTMLCanvasElement | OffscreenCanvas | null = null;

  readonly transform = computed(() => `translate(${this.pan().x} ${this.pan().y}) scale(${this.zoom()})`);
  /** One screen pixel in design units, for overlay sizes. */
  readonly px = computed(() => 1 / this.zoom());

  readonly cursorStyle = computed(() => {
    const tool = this.store.tool();
    if (this.spaceDown() || tool === 'pan') return 'grab';
    if (tool === 'select') return 'default';
    return 'crosshair';
  });

  readonly preview = computed(() => {
    const tool = this.store.tool();
    if (!DRAWING_TOOLS.has(tool)) return '';
    return previewPath(tool, this.anchors(), this.cursor(), this.store.stroke(), this.store.fill(), this.store.sides(), this.store.fonts);
  });

  readonly selectionOutlines = computed(() => {
    const scene = this.store.shown(), pos = this.store.evaluated().positions;
    this.store.fontsVersion();
    return this.store.selectedShapes().filter(id => scene.shapes[id]).map(id => ({ id, d: svgPathData(shapePath(scene.shapes[id], pos, this.store.fonts)) }));
  });

  readonly selectionBox = computed(() => {
    const scene = this.store.shown(), pos = this.store.evaluated().positions;
    const pts = this.store.selectedShapes().flatMap(id => (scene.shapes[id] ? flatten(shapePath(scene.shapes[id], pos, this.store.fonts), 1).flatMap(p => p.points) : []));
    const b = boundsOf(pts);
    return b ? { x: b.minX, y: b.minY, w: b.maxX - b.minX, h: b.maxY - b.minY } : null;
  });

  readonly pointViews = computed<PointView[]>(() => {
    if (this.store.readOnly() || PAINT_TOOLS.has(this.store.tool())) return [];
    const scene = this.store.shown(), pos = this.store.evaluated().positions;
    const analysis = this.store.analysis();
    const conflicts = new Set([...this.store.evaluated().conflicts, ...analysis.conflicts]);
    const conflicted = new Set<Id>();
    for (const id of conflicts) { const c = scene.constraints[id]; if (c) for (const p of constraintPointIds(c)) conflicted.add(p); }
    const visible = new Set<Id>();
    for (const layerId of scene.layerOrder) {
      const layer = scene.layers[layerId];
      if (!layer?.visible || layer.locked || layer.kind !== 'shape') continue;
      for (const s of layer.shapes) if (scene.shapes[s] && !scene.shapes[s].hidden) for (const p of shapePoints(scene.shapes[s])) visible.add(p);
    }
    for (const [id, p] of Object.entries(scene.points)) if (p.derive) visible.add(id);
    const selected = new Set(this.store.selectedPoints());
    const views: PointView[] = [];
    for (const id of visible) {
      const at = pos[id], p = scene.points[id];
      if (!at || !p) continue;
      const dof = analysis.pointDof[id] ?? 2;
      const cls = conflicted.has(id) ? 'conflict' : p.derive ? 'derived' : p.fixed ? 'fixed' : dof === 0 ? 'dof0' : dof === 1 ? 'dof1' : 'dof2';
      views.push({ id, x: at.x, y: at.y, cls, selected: selected.has(id), derived: !!p.derive, fixed: !!p.fixed });
    }
    return views;
  });

  readonly glyphs = computed<GlyphView[]>(() => {
    if (this.store.readOnly() || this.store.tool() !== 'select') return [];
    const scene = this.store.shown(), pos = this.store.evaluated().positions, analysis = this.store.analysis();
    const conflicts = new Set([...this.store.evaluated().conflicts, ...analysis.conflicts]);
    const redundant = new Set(analysis.redundant);
    const out: GlyphView[] = [];
    for (const [id, c] of Object.entries(scene.constraints)) {
      const pts = constraintPointIds(c).map(p => pos[p]).filter((p): p is Vec => !!p);
      if (pts.length < 2) continue;
      const a = pts[0], b = pts[1];
      out.push({ id, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, text: SYMBOL[c.type] ?? '?', conflict: conflicts.has(id), redundant: redundant.has(id) });
    }
    return out;
  });

  /** Where an animated point travels, for the selected points (D5). */
  readonly trails = computed(() => {
    const scene = this.store.scene();
    const ids = this.store.selectedPoints().filter(id => scene.animation.tracks[id]);
    if (ids.length === 0 || this.store.playing()) return [];
    const frames = Math.max(1, scene.animation.frames);
    const per = Array.from({ length: frames }, (_, f) => evaluate(scene, { frame: f, text: this.store.fonts }).positions);
    return ids.map(id => ({ id, d: per.map((p, i) => `${i ? 'L' : 'M'}${p[id]?.x ?? 0} ${p[id]?.y ?? 0}`).join('') }));
  });

  readonly hotspot = computed(() => (this.store.kind() === 'Cursor' ? this.store.shown().hotspot : null));

  constructor() {
    const destroyRef = inject(DestroyRef);
    const observer = new ResizeObserver(entries => {
      const r = entries[0].contentRect;
      this.size.set({ width: Math.max(1, Math.floor(r.width)), height: Math.max(1, Math.floor(r.height)) });
      if (!this.fitted) { this.fit(); this.fitted = true; }
    });
    queueMicrotask(() => observer.observe(this.stage().nativeElement));
    destroyRef.onDestroy(() => observer.disconnect());

    // Redraw whenever anything the picture depends on changes.
    effect(() => this.draw());
    // A different tool abandons the shape being drawn.
    effect(() => { this.store.tool(); this.anchors.set([]); this.hoverSnap.set(null); });
  }

  /** Fits the artboard into the view. */
  fit(): void {
    const { width, height } = this.size(), scene = this.store.scene();
    const z = Math.max(0.05, Math.min((width - 32) / scene.width, (height - 32) / scene.height));
    this.zoom.set(z);
    this.pan.set({ x: (width - scene.width * z) / 2, y: (height - scene.height * z) / 2 });
  }

  zoomBy(factor: number, at?: Vec): void {
    const { width, height } = this.size();
    const c = at ?? { x: width / 2, y: height / 2 };
    const z0 = this.zoom(), z = Math.min(64, Math.max(0.05, z0 * factor));
    const p = this.pan();
    this.pan.set({ x: c.x - ((c.x - p.x) * z) / z0, y: c.y - ((c.y - p.y) * z) / z0 });
    this.zoom.set(z);
  }

  // ------------------------------------------------------------------ drawing

  private draw(): void {
    const canvas = this.canvas().nativeElement;
    const { width, height } = this.size();
    const dpr = window.devicePixelRatio || 1;
    const scene = this.store.shown();
    const { positions } = this.store.evaluated();
    const z = this.zoom(), pan = this.pan();
    const preview = this.store.previewWidth();
    const frame = this.store.frame();
    this.store.paint.version();
    this.store.fontsVersion();
    const showGuides = this.store.showGuides();
    const gridOn = this.store.gridEnabled(), grid = this.store.grid();

    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
    }
    const ctx = canvas.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);

    const x = pan.x, y = pan.y, w = scene.width * z, h = scene.height * z;
    // The checkerboard says "transparent".
    ctx.save();
    ctx.beginPath();
    ctx.rect(x, y, w, h);
    ctx.clip();
    const cell = 8;
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(x, y, w, h);
    ctx.fillStyle = '#e9ecef';
    for (let cy = 0; cy * cell < h; cy++) for (let cx = cy % 2; cx * cell < w; cx += 2) ctx.fillRect(x + cx * cell, y + cy * cell, cell, cell);
    ctx.restore();

    if (preview) {
      const output = Object.values(scene.outputs).find(o => o.width === preview);
      const ph = output?.height ?? Math.round((preview * scene.height) / scene.width);
      const img = renderToImageData(scene, { width: preview, height: ph, output, positions, frame, paint: this.store.paint, text: this.store.fonts, paintWidth: preview });
      const small = this.scratch(preview, ph);
      (small.getContext('2d') as CanvasRenderingContext2D).putImageData(img, 0, 0);
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(small, x, y, w, h);
      ctx.imageSmoothingEnabled = true;
      // Pixel grid when the pixels are big enough to see.
      if (w / preview >= 6) {
        ctx.strokeStyle = 'rgba(0,0,0,0.08)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let i = 1; i < preview; i++) { const gx = Math.round(x + (i * w) / preview) + 0.5; ctx.moveTo(gx, y); ctx.lineTo(gx, y + h); }
        for (let i = 1; i < ph; i++) { const gy = Math.round(y + (i * h) / ph) + 0.5; ctx.moveTo(x, gy); ctx.lineTo(x + w, gy); }
        ctx.stroke();
      }
    } else {
      // Render at the on-screen resolution, capped so a deep zoom stays affordable.
      const scale = Math.min(dpr * z, 4096 / Math.max(scene.width, scene.height));
      const rw = Math.max(1, Math.round(scene.width * scale)), rh = Math.max(1, Math.round(scene.height * scale));
      const art = this.scratch(rw, rh);
      renderScene(art.getContext('2d') as CanvasRenderingContext2D, scene, { width: rw, height: rh, positions, frame, paint: this.store.paint, text: this.store.fonts, showGuides: showGuides && !this.store.readOnly(), paintWidth: this.store.paintWidth() });
      ctx.drawImage(art, x, y, w, h);
      if (gridOn && grid * z >= 8 && !this.store.readOnly()) {
        ctx.strokeStyle = 'rgba(13,110,253,0.10)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let gx = grid; gx < scene.width; gx += grid) { const sx = Math.round(x + gx * z) + 0.5; ctx.moveTo(sx, y); ctx.lineTo(sx, y + h); }
        for (let gy = grid; gy < scene.height; gy += grid) { const sy = Math.round(y + gy * z) + 0.5; ctx.moveTo(x, sy); ctx.lineTo(x + w, sy); }
        ctx.stroke();
      }
    }
    ctx.strokeStyle = 'rgba(0,0,0,0.35)';
    ctx.lineWidth = 1;
    ctx.strokeRect(Math.round(x) + 0.5, Math.round(y) + 0.5, Math.round(w), Math.round(h));
    this.drawLoupe();
  }

  private scratch(width: number, height: number): HTMLCanvasElement {
    let c = this.art as HTMLCanvasElement | null;
    if (!c) { c = document.createElement('canvas'); this.art = c; }
    if (c.width !== width || c.height !== height) { c.width = width; c.height = height; }
    return c;
  }

  private drawLoupe(): void {
    const at = this.loupe();
    const loupe = this.loupeCanvas().nativeElement;
    if (!at) return;
    const dpr = window.devicePixelRatio || 1, size = 120, k = 2;
    loupe.width = size * dpr;
    loupe.height = size * dpr;
    const ctx = loupe.getContext('2d')!;
    const src = this.canvas().nativeElement;
    const half = size / k / 2;
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(src, (at.x - half) * dpr, (at.y - half) * dpr, 2 * half * dpr, 2 * half * dpr, 0, 0, size * dpr, size * dpr);
    ctx.strokeStyle = 'rgba(220,53,69,0.9)';
    ctx.lineWidth = dpr;
    ctx.beginPath();
    ctx.moveTo((size / 2) * dpr, (size / 2 - 10) * dpr); ctx.lineTo((size / 2) * dpr, (size / 2 + 10) * dpr);
    ctx.moveTo((size / 2 - 10) * dpr, (size / 2) * dpr); ctx.lineTo((size / 2 + 10) * dpr, (size / 2) * dpr);
    ctx.stroke();
  }

  // ------------------------------------------------------------------ coordinates

  private screen(e: PointerEvent | WheelEvent | MouseEvent): Vec {
    const r = this.stage().nativeElement.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  private design(s: Vec): Vec {
    const p = this.pan(), z = this.zoom();
    return vec((s.x - p.x) / z, (s.y - p.y) / z);
  }

  /** Alt held during the current pointer event: place freely, no snap (S2). */
  private noSnap = false;

  private snapAt(at: Vec, opts: { exclude?: Set<Id>; derived?: boolean } = {}): Snap {
    if (!this.store.snapEnabled() || this.noSnap) return { kind: 'none', at };
    return snap(this.store.shown(), this.store.evaluated().positions, at, {
      radius: 10 / this.zoom(),
      grid: this.store.grid(),
      gridEnabled: this.store.gridEnabled(),
      exclude: opts.exclude,
      derived: opts.derived,
      text: this.store.fonts,
    });
  }

  // ------------------------------------------------------------------ pointer events

  onPointerDown(e: PointerEvent): void {
    this.noSnap = e.altKey;
    const s = this.screen(e);
    this.pointers.set(e.pointerId, s);
    this.stage().nativeElement.setPointerCapture(e.pointerId);

    if (this.pointers.size === 2) {
      // A second finger turns whatever started into a pinch.
      this.cancelGesture();
      const [a, b] = [...this.pointers.values()];
      this.gesture = { kind: 'pinch', d0: Math.hypot(a.x - b.x, a.y - b.y), mid0: vec((a.x + b.x) / 2, (a.y + b.y) / 2), zoom0: this.zoom(), pan0: this.pan() };
      return;
    }
    if (this.pointers.size > 2) return;

    const tool = this.store.tool();
    if (e.button === 1 || tool === 'pan' || this.spaceDown() || (this.store.readOnly() && e.button === 0)) {
      this.gesture = { kind: 'pan', start: s, pan0: this.pan() };
      e.preventDefault();
      return;
    }
    if (e.button !== 0) return;
    const at = this.design(s);
    if (e.pointerType === 'touch') this.loupe.set(s);

    if (tool === 'select') this.selectDown(at, e.shiftKey || e.ctrlKey || e.metaKey);
    else if (PAINT_TOOLS.has(tool)) this.paintDown(tool, at);
    else if (tool === 'hotspot') {
      const sn = this.snapAt(at, { derived: false });
      this.store.change(scene => { scene.hotspot = { x: Math.round(sn.at.x), y: Math.round(sn.at.y) }; });
    } else if (DRAWING_TOOLS.has(tool)) {
      const sn = this.snapAt(at);
      const anchors = this.anchors();
      // Closing a path by clicking its first point.
      if ((tool === 'polyline' || tool === 'polygon' || tool === 'curve' || tool === 'closedCurve') && anchors.length >= 2) {
        const first = anchors[0].at;
        if (Math.hypot(first.x - sn.at.x, first.y - sn.at.y) * this.zoom() < 10) { this.finish(true); return; }
      }
      this.anchors.set([...anchors, { at: sn.at, snap: sn }]);
      this.gesture = { kind: 'draw', start: s, moved: false };
    }
  }

  onPointerMove(e: PointerEvent): void {
    this.noSnap = e.altKey;
    const s = this.screen(e);
    if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, s);
    const at = this.design(s);
    const g = this.gesture;

    switch (g.kind) {
      case 'pinch': {
        if (this.pointers.size < 2) return;
        const [a, b] = [...this.pointers.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y), mid = vec((a.x + b.x) / 2, (a.y + b.y) / 2);
        const z = Math.min(64, Math.max(0.05, (g.zoom0 * d) / Math.max(1, g.d0)));
        // Keep the design point under the starting midpoint under the fingers' midpoint.
        const dx = (g.mid0.x - g.pan0.x) / g.zoom0, dy = (g.mid0.y - g.pan0.y) / g.zoom0;
        this.zoom.set(z);
        this.pan.set({ x: mid.x - dx * z, y: mid.y - dy * z });
        return;
      }
      case 'pan':
        this.pan.set({ x: g.pan0.x + s.x - g.start.x, y: g.pan0.y + s.y - g.start.y });
        return;
      case 'point': {
        const sn = this.snapAt(at, { exclude: new Set([g.id]), derived: false });
        g.moved = true;
        this.hoverSnap.set(sn.kind === 'point' ? sn : null);
        this.store.drag.set({ point: g.id, target: sn.kind === 'point' || sn.kind === 'grid' ? sn.at : at });
        if (e.pointerType === 'touch') this.loupe.set(s);
        return;
      }
      case 'shapes': {
        let dx = at.x - g.start.x, dy = at.y - g.start.y;
        if (this.store.gridEnabled() && this.store.snapEnabled()) { const gr = this.store.grid(); dx = Math.round(dx / gr) * gr; dy = Math.round(dy / gr) * gr; }
        g.moved = g.moved || dx !== 0 || dy !== 0;
        const moved = structuredClone(g.base);
        translatePoints(moved, g.points, { x: dx, y: dy });
        this.store.live.set(moved);
        return;
      }
      case 'marquee': {
        g.end = at;
        this.marquee.set({ x: Math.min(g.start.x, at.x), y: Math.min(g.start.y, at.y), w: Math.abs(at.x - g.start.x), h: Math.abs(at.y - g.start.y) });
        return;
      }
      case 'paint':
        this.paintMove(at);
        if (e.pointerType === 'touch') this.loupe.set(s);
        return;
      case 'draw': {
        if (Math.hypot(s.x - g.start.x, s.y - g.start.y) > 4) g.moved = true;
        const tool = this.store.tool();
        // Dragging while placing a curve node pulls out its handle.
        if (g.moved && (tool === 'curve' || tool === 'closedCurve')) {
          const anchors = [...this.anchors()];
          anchors[anchors.length - 1] = { ...anchors[anchors.length - 1], handle: at };
          this.anchors.set(anchors);
          return;
        }
        break;
      }
    }

    if (DRAWING_TOOLS.has(this.store.tool()) || this.store.tool() === 'hotspot') {
      const sn = this.snapAt(at);
      this.hoverSnap.set(sn);
      this.cursor.set(sn.at);
      if (g.kind === 'draw' && e.pointerType === 'touch') this.loupe.set(s);
    }
  }

  onPointerUp(e: PointerEvent): void {
    this.noSnap = e.altKey;
    this.pointers.delete(e.pointerId);
    const g = this.gesture;
    if (g.kind === 'pinch') {
      if (this.pointers.size === 0) this.gesture = { kind: 'none' };
      return;
    }
    this.gesture = { kind: 'none' };
    this.loupe.set(null);
    const at = this.design(this.screen(e));

    switch (g.kind) {
      case 'point': this.commitPointDrag(g); break;
      case 'shapes': this.commitShapeMove(g); break;
      case 'marquee': this.commitMarquee(g); break;
      case 'paint': this.commitPaint(g); break;
      case 'draw': {
        const tool = this.store.tool();
        const needed = anchorsNeeded(tool);
        const anchors = this.anchors();
        if (needed === 1) { this.finish(false); break; }
        // A drag draws a two-anchor shape in one go: the release is the second anchor.
        if (g.moved && needed === 2 && anchors.length === 1) {
          const sn = this.snapAt(at);
          this.anchors.set([...anchors, { at: sn.at, snap: sn }]);
        }
        if (needed !== null && this.anchors().length >= needed) this.finish(false);
        break;
      }
    }
  }

  onPointerCancel(e: PointerEvent): void {
    this.pointers.delete(e.pointerId);
    this.cancelGesture();
    this.gesture = { kind: 'none' };
    this.loupe.set(null);
  }

  onDoubleClick(e: MouseEvent): void {
    const tool = this.store.tool();
    if (tool === 'polyline' || tool === 'curve' || tool === 'polygon' || tool === 'closedCurve') {
      e.preventDefault();
      // The double-click's two clicks each added the same anchor: drop the duplicate.
      const anchors = this.anchors();
      if (anchors.length >= 2) {
        const [a, b] = anchors.slice(-2);
        if (Math.hypot(a.at.x - b.at.x, a.at.y - b.at.y) * this.zoom() < 6) this.anchors.set(anchors.slice(0, -1));
      }
      this.finish(false);
    }
  }

  onWheel(e: WheelEvent): void {
    e.preventDefault();
    if (e.ctrlKey || !e.shiftKey) this.zoomBy(Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0015)), this.screen(e));
    else this.pan.update(p => ({ x: p.x - e.deltaY, y: p.y }));
  }

  onLeave(): void {
    if (this.gesture.kind === 'none') { this.cursor.set(null); this.hoverSnap.set(null); }
  }

  private cancelGesture(): void {
    const g = this.gesture;
    if (g.kind === 'point') this.store.drag.set(null);
    if (g.kind === 'shapes') { this.store.live.set(null); this.store.hold.set([]); }
    if (g.kind === 'paint') this.store.paint.putPixels(this.store.scene(), g.layer, g.width, g.before);
    if (g.kind === 'draw') this.anchors.update(a => a.slice(0, -1));
    this.marquee.set(null);
    this.hoverSnap.set(null);
    this.gesture = { kind: 'none' };
  }

  // ------------------------------------------------------------------ select tool

  private selectDown(at: Vec, additive: boolean): void {
    const scene = this.store.scene(), pos = this.store.evaluated().positions;
    const pointId = hitPoint(pos, at, 8 / this.zoom(), this.pointViews().map(p => p.id));
    if (pointId) {
      const current = this.store.selectedPoints();
      const points = additive ? (current.includes(pointId) ? current.filter(p => p !== pointId) : [...current, pointId]) : current.includes(pointId) ? current : [pointId];
      this.store.select(additive ? this.store.selectedShapes() : [], points);
      if (!scene.points[pointId]?.derive && !this.store.readOnly()) this.gesture = { kind: 'point', id: pointId, start: at, moved: false };
      return;
    }
    const shapeId = hitShape(scene, pos, at, 6 / this.zoom(), topDownShapes(scene), this.store.fonts);
    if (shapeId) {
      const current = this.store.selectedShapes();
      const shapes = additive ? (current.includes(shapeId) ? current.filter(s => s !== shapeId) : [...current, shapeId]) : current.includes(shapeId) ? current : [shapeId];
      this.store.select(shapes, additive ? this.store.selectedPoints() : []);
      const points = [...new Set(shapes.flatMap(id => (scene.shapes[id] ? shapePoints(scene.shapes[id]) : [])))].filter(p => !scene.points[p]?.derive);
      this.store.hold.set(points);
      this.gesture = { kind: 'shapes', ids: shapes, points, start: at, base: scene, moved: false };
      return;
    }
    this.gesture = { kind: 'marquee', start: at, end: at, additive };
    if (!additive) this.store.clearSelection();
  }

  private commitPointDrag(g: Extract<Gesture, { kind: 'point' }>): void {
    const drag = this.store.drag();
    const target = this.hoverSnap();
    this.hoverSnap.set(null);
    if (!g.moved || !drag) { this.store.drag.set(null); return; }
    const solved = this.store.evaluated().positions;
    const frame = this.store.frame();
    this.store.drag.set(null);
    this.store.change(s => {
      if (frame == null) {
        applyPositions(s, solved);
        // Dropped on another point: they become one (D4).
        if (target?.kind === 'point' && target.id !== g.id && s.points[target.id]) mergePoints(s, g.id, target.id);
      } else {
        // In a frame, a drag keys the point there; the rest pose stays (D19).
        setPointKey(s, g.id, frame, solved[g.id] ?? drag.target);
      }
    });
    if (target?.kind === 'point' && frame == null) this.store.selectedPoints.set([target.id]);
    this.store.pruneSelection();
  }

  private commitShapeMove(g: Extract<Gesture, { kind: 'shapes' }>): void {
    const live = this.store.live();
    this.store.live.set(null);
    this.store.hold.set([]);
    if (!g.moved || !live) return;
    const solved = evaluate(live, { hold: g.points, text: this.store.fonts }).positions;
    const frame = this.store.frame();
    this.store.change(s => {
      if (frame == null) applyPositions(s, solved);
      else for (const p of g.points) setPointKey(s, p, frame, solved[p]);
    });
  }

  private commitMarquee(g: Extract<Gesture, { kind: 'marquee' }>): void {
    this.marquee.set(null);
    const minX = Math.min(g.start.x, g.end.x), maxX = Math.max(g.start.x, g.end.x);
    const minY = Math.min(g.start.y, g.end.y), maxY = Math.max(g.start.y, g.end.y);
    if ((maxX - minX) * this.zoom() < 3 && (maxY - minY) * this.zoom() < 3) return;
    const scene = this.store.scene(), pos = this.store.evaluated().positions;
    const inside = (id: Id) => { const p = pos[id]; return !!p && p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY; };
    const shapes = topDownShapes(scene).filter(id => {
      const layer = layerOf(scene, id);
      return layer && scene.layers[layer].visible && !scene.layers[layer].locked && shapePoints(scene.shapes[id]).every(inside);
    });
    const points = this.pointViews().map(p => p.id).filter(inside);
    if (g.additive) this.store.select([...new Set([...this.store.selectedShapes(), ...shapes])], [...new Set([...this.store.selectedPoints(), ...points])]);
    else this.store.select(shapes, points);
  }

  // ------------------------------------------------------------------ drawing tools

  /** Turns the anchors into a shape. `close` closes a path drawn with the open tool. */
  finish(close: boolean): void {
    const tool = this.store.tool();
    const anchors = this.anchors();
    this.anchors.set([]);
    if (this.store.readOnly() || anchors.length === 0) return;
    if (tool === 'point') {
      const a = anchors[0];
      if (a.snap.kind === 'point') return;
      let created: Id | null = null;
      this.store.change(s => { created = buildPoint(s, a); });
      if (created) this.store.select([], [created]);
      return;
    }
    const min = tool === 'polyline' || tool === 'curve' ? 2 : tool === 'polygon' || tool === 'closedCurve' ? 3 : anchorsNeeded(tool) ?? 2;
    if (anchors.length < min) return;
    // A zero-size circle, box or star is a mis-click.
    if ((tool === 'circle' || tool === 'ellipse' || tool === 'rect' || tool === 'regular' || tool === 'star') && Math.hypot(anchors[0].at.x - anchors[1].at.x, anchors[0].at.y - anchors[1].at.y) < 0.5) return;
    let created: Id | null = null;
    this.store.change(s => {
      const layer = shapeLayer(s, this.store.activeLayer());
      const shape = buildShape(s, tool, anchors, close, structuredClone(this.store.stroke()), structuredClone(this.store.fill()), this.store.sides());
      if (shape) created = addShape(s, layer, shape);
      this.store.activeLayerChoice.set(layer);
    });
    if (created) {
      this.store.select([created]);
      if (tool === 'text') this.store.tool.set('select');
    }
  }

  // ------------------------------------------------------------------ paint tools (D2)

  private paintTarget(): { layer: Id; width: number } | null {
    let layer: Id | null = this.store.activeLayer();
    const scene = this.store.scene();
    if (!layer || scene.layers[layer]?.kind !== 'paint') {
      // Painting needs a paint layer: make one above the shapes.
      // The callback's assignment is invisible to narrowing, which would type `created` as null after it.
      let created = null as Id | null;
      this.store.change(s => { created = addLayer(s, 'paint'); });
      layer = created as Id | null;
      this.store.activeLayerChoice.set(layer);
    }
    if (!layer || this.store.scene().layers[layer]?.locked) return null;
    return { layer, width: this.store.paintWidth() };
  }

  private bitmapPixel(at: Vec, width: number): { x: number; y: number; w: number; h: number } {
    const scene = this.store.scene();
    const c = this.store.paint.canvas(scene, this.store.activeLayer()!, width);
    return { x: Math.floor((at.x * c.width) / scene.width), y: Math.floor((at.y * c.height) / scene.height), w: c.width, h: c.height };
  }

  private paintDown(tool: ToolId, at: Vec): void {
    const target = this.paintTarget();
    if (!target) return;
    const scene = this.store.scene();
    const { layer, width } = target;
    const p = this.bitmapPixel(at, width);
    if (tool === 'picker') {
      const img = this.store.paint.pixels(scene, layer, width);
      const o = (p.y * img.width + p.x) * 4;
      if (p.x >= 0 && p.y >= 0 && p.x < img.width && p.y < img.height && img.data[o + 3] > 0) {
        this.store.paintColor.set(rgbToHex(img.data[o], img.data[o + 1], img.data[o + 2]));
        this.store.paintAlpha.set(Math.round((img.data[o + 3] / 255) * 100) / 100);
      }
      return;
    }
    const before = this.store.paint.pixels(scene, layer, width);
    if (tool === 'bucket') {
      const img = this.store.paint.pixels(scene, layer, width);
      if (floodFill(img, p.x, p.y, hexToRgba(this.store.paintColor(), this.store.paintAlpha()))) {
        this.store.paint.putPixels(scene, layer, width, img);
        this.store.recordPaint(layer, width, before);
      }
      return;
    }
    this.gesture = { kind: 'paint', layer, width, before, last: null };
    this.paintMove(at);
  }

  private paintMove(at: Vec): void {
    const g = this.gesture;
    if (g.kind !== 'paint') return;
    const scene = this.store.scene();
    const c = this.store.paint.canvas(scene, g.layer, g.width);
    const ctx = c.getContext('2d', { willReadFrequently: true })!;
    const p = this.bitmapPixel(at, g.width);
    const erase = this.store.tool() === 'eraser';
    const [r, gr, b, a] = hexToRgba(this.store.paintColor(), this.store.paintAlpha());
    ctx.fillStyle = `rgba(${r},${gr},${b},${a / 255})`;
    const plot = (x: number, y: number) => {
      if (erase) ctx.clearRect(x, y, 1, 1);
      else { ctx.clearRect(x, y, 1, 1); ctx.fillRect(x, y, 1, 1); }
    };
    if (g.last) plotLine(g.last.x, g.last.y, p.x, p.y, plot);
    else plot(p.x, p.y);
    g.last = { x: p.x, y: p.y };
    this.store.paint.touch();
  }

  private commitPaint(g: Extract<Gesture, { kind: 'paint' }>): void {
    this.store.recordPaint(g.layer, g.width, g.before);
  }

  // ------------------------------------------------------------------ keyboard

  @HostListener('document:keydown', ['$event'])
  onKeyDown(e: KeyboardEvent): void {
    const target = e.target as HTMLElement | null;
    if (target && (target.closest('input, textarea, select, [contenteditable="true"], bs-select') || target.isContentEditable)) return;
    const ctrl = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();
    if (ctrl && key === 'z') { e.preventDefault(); if (e.shiftKey) this.store.redo(); else this.store.undo(); return; }
    if (ctrl && key === 'y') { e.preventDefault(); this.store.redo(); return; }
    if (ctrl && key === 's') { e.preventDefault(); void this.store.saver.flush(); return; }
    if (ctrl && key === 'd') { e.preventDefault(); this.duplicate(); return; }
    if (ctrl && key === 'a') { e.preventDefault(); this.selectAll(); return; }
    if (ctrl) return;
    if (e.key === ' ') { this.spaceDown.set(true); e.preventDefault(); return; }
    if (e.key === 'Escape') { if (this.anchors().length) this.anchors.set([]); else this.store.clearSelection(); return; }
    if (e.key === 'Enter') { if (this.anchors().length) { e.preventDefault(); this.finish(false); } return; }
    if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault();
      if (this.anchors().length) this.anchors.update(a => a.slice(0, -1));
      else this.deleteSelection();
      return;
    }
    if (e.key.startsWith('Arrow')) { e.preventDefault(); this.nudge(e.key, e.shiftKey); return; }
    const tool = SHORTCUTS[key];
    if (tool && !this.store.readOnly()) this.store.tool.set(tool);
    else if (key === '0') this.fit();
    else if (key === '+' || key === '=') this.zoomBy(1.25);
    else if (key === '-') this.zoomBy(0.8);
  }

  @HostListener('document:keyup', ['$event'])
  onKeyUp(e: KeyboardEvent): void {
    if (e.key === ' ') this.spaceDown.set(false);
  }

  deleteSelection(): void {
    const shapes = this.store.selectedShapes(), points = this.store.selectedPoints();
    if (!shapes.length && !points.length) return;
    this.store.change(s => {
      deleteShapes(s, shapes);
      deletePoints(s, points.filter(p => s.points[p]));
    });
    this.store.clearSelection();
  }

  duplicate(): void {
    const shapes = this.store.selectedShapes();
    if (!shapes.length) return;
    let copies: Id[] = [];
    this.store.change(s => { copies = duplicateShapes(s, shapes, { x: this.store.grid(), y: this.store.grid() }); });
    this.store.select(copies);
  }

  selectAll(): void {
    const scene = this.store.scene();
    this.store.select(topDownShapes(scene).filter(id => { const l = layerOf(scene, id); return l && !scene.layers[l].locked && scene.layers[l].visible; }));
  }

  private nudge(key: string, big: boolean): void {
    const d = big ? this.store.grid() : 1;
    const delta = { x: key === 'ArrowLeft' ? -d : key === 'ArrowRight' ? d : 0, y: key === 'ArrowUp' ? -d : key === 'ArrowDown' ? d : 0 };
    const scene = this.store.scene();
    const points = new Set<Id>(this.store.selectedPoints());
    for (const id of this.store.selectedShapes()) if (scene.shapes[id]) for (const p of shapePoints(scene.shapes[id])) points.add(p);
    const free = [...points].filter(p => scene.points[p] && !scene.points[p].derive);
    if (!free.length) return;
    this.store.change(s => {
      translatePoints(s, free, delta);
      applyPositions(s, evaluate(s, { hold: free, text: this.store.fonts }).positions);
    });
  }

  protected isClosedShape(id: Id): boolean {
    const shape = this.store.shown().shapes[id];
    return !!shape && isClosed(shape);
  }
}

const SHORTCUTS: Record<string, ToolId> = {
  v: 'select', h: 'pan', p: 'point', l: 'line', y: 'polyline', b: 'curve', a: 'arc', g: 'polygon',
  c: 'circle', e: 'ellipse', r: 'rect', t: 'text', n: 'pencil', x: 'eraser', k: 'bucket', i: 'picker',
};

/** A lone point keeps its derivation when it snapped to a midpoint or an outline (D15). */
function buildPoint(scene: Scene, a: Anchor): Id {
  return resolveAnchor(scene, a);
}

/** The layer new shapes go into: the active one when it holds shapes, else the topmost shape layer, else a new one. */
function shapeLayer(scene: Scene, active: Id | null): Id {
  if (active && scene.layers[active]?.kind === 'shape' && !scene.layers[active].locked) return active;
  const existing = [...scene.layerOrder].reverse().find(id => scene.layers[id]?.kind === 'shape' && !scene.layers[id].locked);
  return existing ?? addLayer(scene, 'shape');
}
