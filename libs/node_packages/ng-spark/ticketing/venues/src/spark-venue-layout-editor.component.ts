import {
  afterNextRender, ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input, output, signal, untracked, viewChild,
} from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { BlockKind, LayoutBlock, LayoutCategory, LayoutChain, LayoutPlan, VenueLayoutPlanResponse, VenueLayoutProblem, VenueLayoutPublishResponse } from './generated/contracts';
import { csvBlob, importCsv } from './layout-editor/layout-csv';
import { emptyPlan, packPlan } from './layout-editor/plan-pack';
import {
  addBlock, addDecor, addToChain, LayoutHistory, moveBlocks, moveSeats, removeBlocks, removeDecor, removeSeats, reorderChain, seatKeys,
  setCategories, setChains, setRows, setSeatCategory, setSeatTag, snap, uniqueKey, updateBlock,
} from './layout-editor/plan-edits';
import { DEFAULT_GENERATOR, generateRows, SeatGeneratorOptions } from './layout-editor/seat-generator';
import { SeatMapController } from './seat-map/seat-map-controller';
import { blockAt, buildSeatMapModel, forEachSeatIn, hitTest, SeatMapModel, TAG_ACCESSIBLE, TAG_COMPANION, TAG_RESTRICTED_VIEW } from './seat-map/seat-map-model';
import { readSeatMapColors } from './seat-map/seat-map-theme';
import { SparkVenueSaveError, SparkVenuesService } from './spark-venues.service';

export type LayoutEditorTool = 'select' | 'block' | 'standing' | 'decor' | 'stage' | 'text';

/**
 * The layout editor (PRD §4.11, D32; commerce #521 M14): polygon blocks over an optional uploaded background, seat
 * generation (rows × seats; straight, angled or curved; labelling schemes), standing polygons with capacity, decor,
 * chain order by dragging, seat tags, price categories, CSV import and export, zoom, pan, undo, redo, snapping, touch
 * and keyboard, on the S7 canvas renderer with level of detail. It edits the caller's private draft; publishing goes
 * through Approvals where D8 says.
 *
 * `<spark-venue-layout-editor [layoutId]="id" (published)="onPublished($event)" />`
 */
@Component({
  selector: 'spark-venue-layout-editor',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-venue-layout-editor.component.html',
  styleUrl: './spark-venue-layout-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkVenueLayoutEditorComponent {
  private readonly venues = inject(SparkVenuesService);
  private readonly host = inject(ElementRef<HTMLElement>);

  layoutId = input.required<string>();
  published = output<VenueLayoutPublishResponse>();

  protected readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  protected readonly meta = signal<VenueLayoutPlanResponse | null>(null);
  protected readonly plan = signal<LayoutPlan>(emptyPlan());
  protected readonly revision = signal(0);
  protected readonly dirty = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly problems = signal<VenueLayoutProblem[]>([]);
  protected readonly tool = signal<LayoutEditorTool>('select');
  protected readonly snapGrid = signal(5);
  protected readonly selectedSeats = signal<ReadonlySet<string>>(new Set());
  protected readonly selectedBlock = signal<string | null>(null);
  protected readonly selectedDecor = signal<string | null>(null);
  protected readonly drawing = signal<number[]>([]);
  protected readonly textLabel = signal('');
  protected readonly generator = signal<Omit<SeatGeneratorOptions, 'blockKey'>>({ ...DEFAULT_GENERATOR });
  protected readonly canUndo = signal(false);
  protected readonly canRedo = signal(false);
  protected readonly draggedChainBlock = signal<{ chain: string; index: number } | null>(null);

  protected readonly block = computed(() => this.plan().blocks.find((b) => b.key === this.selectedBlock()) ?? null);
  protected readonly seatCount = computed(() => this.plan().blocks.reduce((n, b) => n + b.rows.reduce((m, r) => m + r.seats.length, 0), 0));
  protected readonly tags = { accessible: TAG_ACCESSIBLE, companion: TAG_COMPANION, restrictedView: TAG_RESTRICTED_VIEW };

  private readonly history = new LayoutHistory(emptyPlan());
  private controller: SeatMapController | null = null;
  private modelRef: SeatMapModel | null = null;
  private renderRevision = 0;
  private selectedMarks: Uint8Array | null = null;
  private drag: { kind: 'seats' | 'block' | 'box'; startX: number; startY: number; x: number; y: number; original?: Float32Array[] } | null = null;
  private background: HTMLImageElement | null = null;

  constructor() {
    afterNextRender(() => {
      this.controller = new SeatMapController(this.canvas().nativeElement, {
        options: () => ({
          selected: this.selectedMarks,
          revision: this.renderRevision,
          categoryColors: this.plan().categories.map((c) => c.color),
          backgroundImage: this.background,
          // The editor always shows seats (editing blocks by their shading would hide what is edited).
          lod: this.modelRef && this.controller && this.modelRef.pitch * this.controller.currentViewport.scale < 3 ? 'dots' : null,
        }),
        onPointerDown: (x, y, e) => this.pointerDown(x, y, e),
        onPointerMove: (x, y) => this.pointerMove(x, y),
        onPointerUp: (x, y) => this.pointerUp(x, y),
        onKey: (e) => this.key(e),
        overlay: (ctx, viewport) => this.overlay(ctx, viewport.scale),
      });
      this.controller.colors = readSeatMapColors(this.host.nativeElement);
      if (this.modelRef) this.controller.setModel(this.modelRef);
    });
    inject(DestroyRef).onDestroy(() => this.controller?.destroy());

    effect(() => {
      const id = this.layoutId();
      untracked(() => void this.load(id));
    });
    effect(() => {
      const plan = this.plan();
      untracked(() => this.rebuild(plan));
    });
    effect(() => {
      const selected = this.selectedSeats();
      if (!this.modelRef) return;
      this.selectedMarks = new Uint8Array(this.modelRef.count);
      for (const key of selected) {
        const i = this.modelRef.keyIndex.get(key);
        if (i !== undefined) this.selectedMarks[i] = 1;
      }
      this.renderRevision++;
      this.controller?.requestRender();
    });
  }

  // ---- loading, saving, publishing ------------------------------------------------------------------------

  async load(layoutId: string): Promise<void> {
    const response = await this.venues.plan(layoutId, 'draft');
    if (!response) { this.message.set('ticketingVenues.editor.notFound'); return; }
    this.meta.set(response);
    this.revision.set(response.revision);
    this.history.reset(response.plan);
    this.commit(response.plan, false);
    this.loadBackground(response.plan);
    this.controller?.fit();
  }

  async save(): Promise<boolean> {
    this.busy.set(true);
    this.problems.set([]);
    try {
      this.revision.set(await this.venues.saveDraft(this.layoutId(), this.revision(), this.plan()));
      this.dirty.set(false);
      this.message.set('ticketingVenues.editor.saved');
      return true;
    } catch (e) {
      this.fail(e);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async publish(): Promise<void> {
    if (this.dirty() && !(await this.save())) return;
    this.busy.set(true);
    try {
      const result = await this.venues.publish(this.layoutId(), this.revision());
      this.message.set(result.outcome === 'published' ? 'ticketingVenues.editor.publishedNow' : 'ticketingVenues.editor.publishPending');
      this.published.emit(result);
    } catch (e) {
      this.fail(e);
    } finally {
      this.busy.set(false);
    }
  }

  private fail(e: unknown): void {
    if (e instanceof SparkVenueSaveError) {
      this.problems.set(e.problems);
      this.message.set(e.kind === 'conflict' ? 'ticketingVenues.editor.conflict' : 'ticketingVenues.editor.invalid');
    } else {
      this.message.set('ticketingVenues.editor.failed');
    }
  }

  // ---- edits ----------------------------------------------------------------------------------------------

  /** Applies an edit through the undo history. */
  edit(change: (plan: LayoutPlan) => LayoutPlan): void {
    const before = this.history.present;
    const after = this.history.apply(change);
    if (after !== before) this.commit(after, true);
  }

  undo(): void { this.commit(this.history.undo(), true); }

  redo(): void { this.commit(this.history.redo(), true); }

  private commit(plan: LayoutPlan, dirty: boolean): void {
    this.plan.set(plan);
    this.canUndo.set(this.history.canUndo);
    this.canRedo.set(this.history.canRedo);
    if (dirty) this.dirty.set(true);
  }

  private rebuild(plan: LayoutPlan): void {
    const first = !this.modelRef;
    this.modelRef = buildSeatMapModel(packPlan(plan));
    const selected = this.selectedSeats();
    this.selectedMarks = new Uint8Array(this.modelRef.count);
    for (const key of selected) {
      const i = this.modelRef.keyIndex.get(key);
      if (i !== undefined) this.selectedMarks[i] = 1;
    }
    this.renderRevision++;
    this.controller?.setModel(this.modelRef, first);
  }

  setTool(tool: LayoutEditorTool): void {
    this.tool.set(tool);
    this.drawing.set([]);
    this.controller?.requestRender();
  }

  updateSelectedBlock(patch: Partial<LayoutBlock>): void {
    const key = this.selectedBlock();
    if (key) this.edit((p) => updateBlock(p, key, patch));
  }

  setBlockKind(kind: string): void {
    const value = kind as BlockKind;
    this.updateSelectedBlock(value === 'seatedNumbered' ? { kind: value } : { kind: value, rows: [], capacity: this.block()?.capacity || 100 });
  }

  setGenerator<K extends keyof Omit<SeatGeneratorOptions, 'blockKey'>>(name: K, value: Omit<SeatGeneratorOptions, 'blockKey'>[K]): void {
    this.generator.update((g) => ({ ...g, [name]: value }));
  }

  /** Generates the selected block's rows (replacing them), placed at the block's top-left corner unless moved. */
  generateSeats(): void {
    const block = this.block();
    if (!block) return;
    const others = seatKeys(this.plan());
    for (const r of block.rows) for (const s of r.seats) others.delete(s.key);
    const rows = generateRows({ ...this.generator(), blockKey: block.key }, others);
    this.edit((p) => setRows(updateBlock(p, block.key, { kind: 'seatedNumbered' }), block.key, rows));
  }

  /** Puts the generator's origin at the selected block's top-left corner (plus half a seat). */
  originFromBlock(): void {
    const block = this.block();
    if (!block || block.polygon.length < 2) return;
    let x = Infinity, y = Infinity;
    for (let i = 0; i + 1 < block.polygon.length; i += 2) { x = Math.min(x, block.polygon[i]); y = Math.min(y, block.polygon[i + 1]); }
    const g = this.generator();
    this.generator.set({ ...g, originX: Math.round(x + g.seatSpacing), originY: Math.round(y + g.rowSpacing) });
  }

  toggleTag(tag: number, on: boolean): void {
    const keys = this.selectedSeats();
    this.edit((p) => setSeatTag(p, keys, tag, on));
  }

  selectionHasTag(tag: number): boolean {
    const m = this.modelRef;
    if (!m) return false;
    for (const key of this.selectedSeats()) {
      const i = m.keyIndex.get(key);
      if (i === undefined || !(m.tags[i] & tag)) return false;
    }
    return this.selectedSeats().size > 0;
  }

  setSelectionCategory(category: string): void {
    const keys = this.selectedSeats();
    this.edit((p) => setSeatCategory(p, keys, category || null));
  }

  deleteSelection(): void {
    const seats = this.selectedSeats();
    const block = this.selectedBlock();
    const decor = this.selectedDecor();
    if (seats.size > 0) this.edit((p) => removeSeats(p, seats));
    else if (block) this.edit((p) => removeBlocks(p, new Set([block])));
    else if (decor) this.edit((p) => removeDecor(p, new Set([decor])));
    this.clearSelection();
  }

  clearSelection(): void {
    this.selectedSeats.set(new Set());
    this.selectedBlock.set(null);
    this.selectedDecor.set(null);
  }

  // Categories.

  addCategory(): void {
    const plan = this.plan();
    const key = uniqueKey(`cat${plan.categories.length + 1}`, plan.categories.map((c) => c.key));
    this.edit((p) => setCategories(p, [...p.categories, { key, name: `Category ${p.categories.length + 1}`, color: '#3b82f6' }]));
  }

  updateCategory(key: string, patch: Partial<LayoutCategory>): void {
    this.edit((p) => setCategories(p, p.categories.map((c) => (c.key === key ? { ...c, ...patch } : c))));
  }

  removeCategory(key: string): void {
    this.edit((p) => setCategories(p, p.categories.filter((c) => c.key !== key)));
  }

  // Chains (D25).

  addChain(): void {
    const plan = this.plan();
    const key = uniqueKey(`chain${plan.chains.length + 1}`, plan.chains.map((c) => c.key));
    this.edit((p) => setChains(p, [...p.chains, { key, name: `Chain ${p.chains.length + 1}`, blocks: [] }]));
  }

  updateChain(key: string, patch: Partial<LayoutChain>): void {
    this.edit((p) => setChains(p, p.chains.map((c) => (c.key === key ? { ...c, ...patch } : c))));
  }

  removeChain(key: string): void {
    this.edit((p) => setChains(p, p.chains.filter((c) => c.key !== key)));
  }

  addSelectedBlockToChain(chainKey: string): void {
    const block = this.block();
    if (block?.kind === 'seatedNumbered') this.edit((p) => addToChain(p, chainKey, block.key));
  }

  moveInChain(chainKey: string, from: number, to: number): void {
    this.edit((p) => reorderChain(p, chainKey, from, to));
  }

  setChainBlock(chainKey: string, index: number, patch: { backToFront?: boolean; reverseSeats?: boolean }): void {
    this.edit((p) => setChains(p, p.chains.map((c) => c.key !== chainKey ? c : { ...c, blocks: c.blocks.map((b, i) => (i === index ? { ...b, ...patch } : b)) })));
  }

  removeFromChain(chainKey: string, index: number): void {
    this.edit((p) => setChains(p, p.chains.map((c) => c.key !== chainKey ? c : { ...c, blocks: c.blocks.filter((_, i) => i !== index) })));
  }

  chainDragStart(chain: string, index: number, event: DragEvent): void {
    this.draggedChainBlock.set({ chain, index });
    event.dataTransfer?.setData('text/plain', `${chain}:${index}`);
  }

  chainDrop(chain: string, index: number, event: DragEvent): void {
    event.preventDefault();
    const dragged = this.draggedChainBlock();
    if (dragged && dragged.chain === chain) this.moveInChain(chain, dragged.index, index);
    this.draggedChainBlock.set(null);
  }

  blockName(key: string): string {
    return this.plan().blocks.find((b) => b.key === key)?.name ?? key;
  }

  // CSV (D32) and background.

  exportCsv(): void {
    const url = URL.createObjectURL(csvBlob(this.plan()));
    const a = document.createElement('a');
    a.href = url;
    a.download = `${this.meta()?.name ?? 'layout'}.csv`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  async importCsvFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    const result = importCsv(this.plan(), await file.text());
    this.problems.set(result.problems);
    if (result.plan) {
      const merged = result.plan;
      this.edit(() => merged);
      this.message.set('ticketingVenues.editor.imported');
    } else {
      this.message.set('ticketingVenues.editor.invalid');
    }
  }

  async uploadBackground(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.busy.set(true);
    try {
      const imageId = await this.venues.uploadBackground(this.layoutId(), file);
      const image = await createImageBitmap(file).catch(() => null);
      const plan = this.plan();
      const ratio = image ? image.height / image.width : plan.height / plan.width;
      this.edit((p) => ({ ...p, background: { imageId, x: 0, y: 0, width: p.width, height: Math.round(p.width * ratio), opacity: 0.5 } }));
      this.loadBackground(this.plan());
    } catch {
      this.message.set('ticketingVenues.editor.backgroundFailed');
    } finally {
      this.busy.set(false);
    }
  }

  setBackgroundOpacity(value: string): void {
    const opacity = Math.min(1, Math.max(0, Number(value)));
    this.edit((p) => (p.background ? { ...p, background: { ...p.background, opacity } } : p));
  }

  removeBackground(): void {
    this.edit((p) => ({ ...p, background: null }));
    this.background = null;
  }

  setPlanSize(width: number, height: number): void {
    if (width > 0 && height > 0) this.edit((p) => ({ ...p, width, height }));
  }

  private loadBackground(plan: LayoutPlan): void {
    if (!plan.background?.imageId) { this.background = null; return; }
    const image = new Image();
    image.onload = () => { this.background = image; this.renderRevision++; this.controller?.invalidate(); };
    image.src = this.venues.backgroundUrl(plan.background.imageId);
  }

  // ---- pointer and keyboard -------------------------------------------------------------------------------

  private snapped(value: number): number {
    return snap(value, this.snapGrid());
  }

  private pointerDown(x: number, y: number, event: PointerEvent): boolean {
    const tool = this.tool();
    const m = this.modelRef;
    if (tool === 'text') {
      const label = this.textLabel().trim();
      if (label) {
        const key = uniqueKey('text', this.plan().decor.map((d) => d.key));
        this.edit((p) => addDecor(p, { key, kind: 'text', points: [this.snapped(x), this.snapped(y)], label, color: null }));
      }
      return true;
    }
    if (tool !== 'select') {
      const points = this.drawing();
      // Clicking the first point again closes the polygon.
      if (points.length >= 6 && this.controller && Math.hypot(points[0] - x, points[1] - y) * this.controller.currentViewport.scale < 10) {
        this.closePolygon();
      } else {
        this.drawing.set([...points, this.snapped(x), this.snapped(y)]);
        this.controller?.requestRender();
      }
      return true;
    }
    if (!m) return false;
    const seat = hitTest(m, x, y, m.pitch * 0.6);
    if (seat >= 0) {
      const key = m.payload.seats.keys[seat];
      const current = this.selectedSeats();
      if (event.shiftKey || event.ctrlKey || event.metaKey) {
        const next = new Set(current);
        if (next.has(key)) next.delete(key); else next.add(key);
        this.selectedSeats.set(next);
      } else if (!current.has(key)) {
        this.selectedSeats.set(new Set([key]));
      }
      this.selectedBlock.set(m.payload.blocks[m.block[seat]]?.key ?? null);
      this.selectedDecor.set(null);
      this.drag = { kind: 'seats', startX: x, startY: y, x, y, original: [Float32Array.from(m.x), Float32Array.from(m.y)] };
      return true;
    }
    if (event.shiftKey) {
      this.drag = { kind: 'box', startX: x, startY: y, x, y };
      return true;
    }
    const block = blockAt(m, x, y);
    if (block >= 0) {
      this.selectedBlock.set(m.payload.blocks[block].key);
      this.selectedSeats.set(new Set());
      this.selectedDecor.set(null);
      this.drag = { kind: 'block', startX: x, startY: y, x, y };
      return true;
    }
    this.clearSelection();
    return false;
  }

  private pointerMove(x: number, y: number): void {
    const drag = this.drag;
    const m = this.modelRef;
    if (!drag || !m) return;
    drag.x = x;
    drag.y = y;
    if (drag.kind === 'seats' && drag.original) {
      // Preview in place: move the selected seats in the model; the plan changes on release.
      const dx = this.snapped(x - drag.startX), dy = this.snapped(y - drag.startY);
      for (const key of this.selectedSeats()) {
        const i = m.keyIndex.get(key);
        if (i === undefined) continue;
        m.x[i] = drag.original[0][i] + dx;
        m.y[i] = drag.original[1][i] + dy;
      }
      this.controller?.invalidate();
    } else {
      this.controller?.requestRender();
    }
  }

  private pointerUp(x: number, y: number): void {
    const drag = this.drag;
    this.drag = null;
    const m = this.modelRef;
    if (!drag || !m) return;
    const dx = this.snapped(x - drag.startX), dy = this.snapped(y - drag.startY);
    if (drag.kind === 'seats') {
      const keys = this.selectedSeats();
      if (dx !== 0 || dy !== 0) this.edit((p) => moveSeats(p, keys, dx, dy));
      else this.rebuild(this.plan());
    } else if (drag.kind === 'block') {
      const key = this.selectedBlock();
      if (key && (dx !== 0 || dy !== 0)) this.edit((p) => moveBlocks(p, new Set([key]), dx, dy));
    } else {
      const selected = new Set<string>();
      forEachSeatIn(m, Math.min(drag.startX, x), Math.min(drag.startY, y), Math.max(drag.startX, x), Math.max(drag.startY, y),
        (i) => selected.add(m.payload.seats.keys[i]));
      this.selectedSeats.set(selected);
      this.controller?.requestRender();
    }
  }

  private key(event: KeyboardEvent): boolean {
    const mod = event.ctrlKey || event.metaKey;
    if (mod && event.key.toLowerCase() === 'z') { if (event.shiftKey) this.redo(); else this.undo(); return true; }
    if (mod && event.key.toLowerCase() === 'y') { this.redo(); return true; }
    if (event.key === 'Escape') { this.drawing.set([]); this.clearSelection(); this.controller?.requestRender(); return true; }
    if (event.key === 'Enter' && this.drawing().length >= 6) { this.closePolygon(); return true; }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      if (this.selectedSeats().size === 0 && !this.selectedBlock() && !this.selectedDecor()) return false;
      this.deleteSelection();
      return true;
    }
    const step = this.snapGrid() || 1;
    const move: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    const delta = move[event.key];
    if (!delta) return false;
    const seats = this.selectedSeats();
    const block = this.selectedBlock();
    if (seats.size > 0) { this.edit((p) => moveSeats(p, seats, delta[0], delta[1])); return true; }
    if (block) { this.edit((p) => moveBlocks(p, new Set([block]), delta[0], delta[1])); return true; }
    return false;
  }

  closePolygon(): void {
    const points = this.drawing();
    this.drawing.set([]);
    if (points.length < 6) return;
    const tool = this.tool();
    const plan = this.plan();
    if (tool === 'block' || tool === 'standing') {
      const key = uniqueKey(`b${plan.blocks.length + 1}`, plan.blocks.map((b) => b.key));
      const block: LayoutBlock = {
        key, name: `Block ${plan.blocks.length + 1}`, kind: tool === 'standing' ? 'standing' : 'seatedNumbered',
        polygon: points, capacity: tool === 'standing' ? 100 : 0, category: plan.categories[0]?.key ?? null, rows: [],
      };
      this.edit((p) => addBlock(p, block));
      this.selectedBlock.set(key);
      this.originFromBlock();
    } else {
      const key = uniqueKey(tool, plan.decor.map((d) => d.key));
      this.edit((p) => addDecor(p, { key, kind: tool === 'stage' ? 'stage' : 'polygon', points, label: tool === 'stage' ? 'Stage' : null, color: null }));
    }
    this.tool.set('select');
  }

  private overlay(ctx: CanvasRenderingContext2D, scale: number): void {
    const px = 1 / scale;
    const block = this.block();
    if (block && block.polygon.length >= 6) {
      ctx.beginPath();
      ctx.moveTo(block.polygon[0], block.polygon[1]);
      for (let i = 2; i + 1 < block.polygon.length; i += 2) ctx.lineTo(block.polygon[i], block.polygon[i + 1]);
      ctx.closePath();
      ctx.strokeStyle = this.controller?.colors.selected ?? '#7c3aed';
      ctx.lineWidth = 2 * px;
      ctx.stroke();
    }
    const points = this.drawing();
    if (points.length >= 2) {
      ctx.beginPath();
      ctx.moveTo(points[0], points[1]);
      for (let i = 2; i + 1 < points.length; i += 2) ctx.lineTo(points[i], points[i + 1]);
      ctx.strokeStyle = this.controller?.colors.highlight ?? '#dc2626';
      ctx.lineWidth = 2 * px;
      ctx.stroke();
      for (let i = 0; i + 1 < points.length; i += 2) ctx.fillRect(points[i] - 3 * px, points[i + 1] - 3 * px, 6 * px, 6 * px);
    }
    const drag = this.drag;
    if (drag?.kind === 'box') {
      ctx.strokeStyle = this.controller?.colors.selected ?? '#7c3aed';
      ctx.lineWidth = px;
      ctx.strokeRect(Math.min(drag.startX, drag.x), Math.min(drag.startY, drag.y), Math.abs(drag.x - drag.startX), Math.abs(drag.y - drag.startY));
    }
  }

  protected fit(): void { this.controller?.fit(); }

  protected zoom(factor: number): void { this.controller?.zoomBy(factor); }

  protected number(value: string): number {
    const n = Number(value);
    return Number.isFinite(n) ? n : 0;
  }
}
