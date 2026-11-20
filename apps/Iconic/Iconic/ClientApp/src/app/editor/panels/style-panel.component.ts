import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsRangeComponent } from '@mintplayer/ng-bootstrap/range';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { IconicApi, apiError } from '../../api/iconic-api';
import { mergePoints, reorderShape, splitPoint, shapesUsing } from '../core/edit';
import { newId, type FillStyle, type FillType, type Id, type Shape, type StrokeStyle, type TextShape } from '../core/model';
import { DEFAULT_FONT } from '../core/text';
import { toDataUrlBytes } from '../files';
import { EditorStore } from '../editor-store';
import { PAINT_TOOLS } from '../tool-shapes';

const DASHES: { label: string; value: number[] }[] = [
  { label: 'Solid', value: [] },
  { label: 'Dashed', value: [12, 8] },
  { label: 'Dotted', value: [1, 8] },
];

/**
 * Style of the selection, or of the next shape when nothing is selected. Also the point actions: Fix, Split (D15)
 * and Merge (D4), and the paint colour for the paint tools.
 */
@Component({
  selector: 'app-style-panel',
  imports: [FormsModule, BsButtonTypeDirective, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsRangeComponent, BsSelectComponent, BsSelectOption, SparkIconComponent],
  templateUrl: './style-panel.component.html',
  styleUrl: './panels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StylePanelComponent {
  protected readonly store = inject(EditorStore);
  private readonly api = inject(IconicApi);
  protected readonly colors = Color;
  protected readonly dashes = DASHES;
  protected readonly fillTypes: FillType[] = ['none', 'solid', 'linear', 'radial'];
  protected readonly caps: CanvasLineCap[] = ['round', 'butt', 'square'];
  protected readonly joins: CanvasLineJoin[] = ['round', 'miter', 'bevel'];
  protected readonly fontError = signal<string | null>(null);
  private copied: { stroke: StrokeStyle; fill: FillStyle } | null = null;
  protected readonly hasCopied = signal(false);

  protected readonly painting = computed(() => PAINT_TOOLS.has(this.store.tool()));
  protected readonly shapes = computed(() => {
    const s = this.store.scene();
    return this.store.selectedShapes().map(id => s.shapes[id]).filter((x): x is Shape => !!x);
  });
  /** The style shown: the first selected shape's, else the defaults for new shapes. */
  protected readonly stroke = computed(() => this.shapes()[0]?.stroke ?? this.store.stroke());
  protected readonly fill = computed(() => this.shapes()[0]?.fill ?? this.store.fill());
  protected readonly text = computed(() => (this.shapes().length === 1 && this.shapes()[0].type === 'text' ? (this.shapes()[0] as TextShape) : null));
  protected readonly rect = computed(() => { const s = this.shapes(); return s.length === 1 && s[0].type === 'rect' ? s[0] : null; });
  protected readonly dashIndex = computed(() => Math.max(0, DASHES.findIndex(d => d.value.join() === this.stroke().dash.join())));
  protected readonly fonts = computed(() => [{ id: DEFAULT_FONT, name: 'Inter (default)' }, ...Object.entries(this.store.scene().fonts).map(([id, f]) => ({ id, name: f.name }))]);

  protected readonly points = computed(() => this.store.selectedPoints().filter(p => this.store.scene().points[p]));
  protected readonly allFixed = computed(() => this.points().length > 0 && this.points().every(p => this.store.scene().points[p].fixed));
  protected readonly splittable = computed(() => {
    const s = this.store.scene();
    return this.points().filter(p => shapesUsing(s, p).length > 1 && !s.points[p].derive);
  });
  protected readonly derived = computed(() => this.points().filter(p => this.store.scene().points[p].derive));

  setStroke(patch: Partial<StrokeStyle>): void {
    if (this.shapes().length === 0) { this.store.stroke.update(s => ({ ...s, ...patch })); return; }
    this.store.change(s => { for (const id of this.store.selectedShapes()) if (s.shapes[id]) s.shapes[id].stroke = { ...s.shapes[id].stroke, ...patch }; });
  }

  setFill(patch: Partial<FillStyle>): void {
    if (this.shapes().length === 0) { this.store.fill.update(f => ({ ...f, ...patch })); return; }
    this.store.change(s => { for (const id of this.store.selectedShapes()) if (s.shapes[id]) s.shapes[id].fill = { ...s.shapes[id].fill, ...patch }; });
  }

  setText(patch: Partial<TextShape>): void {
    const id = this.store.selectedShapes()[0];
    this.store.change(s => { const t = s.shapes[id]; if (t?.type === 'text') Object.assign(t, patch); });
  }

  setRadius(radius: number): void {
    const id = this.store.selectedShapes()[0];
    this.store.change(s => { const r = s.shapes[id]; if (r?.type === 'rect') r.radius = Math.max(0, radius); });
  }

  setFlag(flag: 'guide' | 'hidden', value: boolean): void {
    this.store.change(s => { for (const id of this.store.selectedShapes()) if (s.shapes[id]) s.shapes[id][flag] = value || undefined; });
  }

  reorder(delta: number): void {
    this.store.change(s => { for (const id of this.store.selectedShapes()) reorderShape(s, id, delta); });
  }

  /** The format painter: copy one shape's style, paste it onto others. */
  copyStyle(): void {
    this.copied = { stroke: structuredClone(this.stroke()), fill: structuredClone(this.fill()) };
    this.hasCopied.set(true);
  }

  pasteStyle(): void {
    const copied = this.copied;
    if (!copied) return;
    if (this.shapes().length === 0) { this.store.stroke.set(structuredClone(copied.stroke)); this.store.fill.set(structuredClone(copied.fill)); return; }
    this.store.change(s => {
      for (const id of this.store.selectedShapes()) if (s.shapes[id]) { s.shapes[id].stroke = structuredClone(copied.stroke); s.shapes[id].fill = structuredClone(copied.fill); }
    });
  }

  toggleFixed(): void {
    const fixed = !this.allFixed();
    this.store.change(s => { for (const p of this.points()) if (s.points[p]) s.points[p].fixed = fixed || undefined; });
  }

  /** Split (D15): each selected shape (or every shape but the first) gets its own copy of the shared point. */
  split(): void {
    const selectedShapes = new Set(this.store.selectedShapes());
    this.store.change(s => {
      for (const p of this.splittable()) {
        const users = shapesUsing(s, p);
        const targets = users.filter(u => selectedShapes.has(u));
        for (const shape of targets.length && targets.length < users.length ? targets : users.slice(1)) splitPoint(s, p, shape);
      }
    });
    this.store.pruneSelection();
  }

  /** Merge (D4): the selected points become the first one. */
  merge(): void {
    const [into, ...rest] = this.points().filter(p => !this.store.scene().points[p].derive);
    if (!into || rest.length === 0) return;
    this.store.change(s => { for (const p of rest) mergePoints(s, p, into); });
    this.store.selectedPoints.set([into]);
  }

  /** Turns a derived point into a free one where it is now. */
  detach(): void {
    const pos = this.store.evaluated().positions;
    this.store.change(s => {
      for (const p of this.derived()) if (s.points[p]) { s.points[p] = { x: pos[p]?.x ?? s.points[p].x, y: pos[p]?.y ?? s.points[p].y }; }
    });
  }

  /** A font upload (D23): parsed here so a bad file is refused at once, stored as an attachment (or inline in a draft). */
  async uploadFont(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.fontError.set(null);
    const bytes = await file.arrayBuffer();
    const id: Id = newId('f');
    let name: string;
    try {
      this.store.fonts.add(id, bytes);
      name = this.store.fonts.familyName(id) ?? file.name.replace(/\.[^.]+$/, '');
    } catch {
      this.fontError.set('This file is not a font Iconic can read (TTF, OTF or WOFF).');
      return;
    }
    const ext = (/\.(ttf|otf|woff)$/i.exec(file.name)?.[1] ?? 'ttf').toLowerCase();
    let ref: string;
    if (this.store.mode() === 'owned' && this.store.key()) {
      ref = `fonts/${id}.${ext}`;
      try {
        const result = await this.api.putFile(this.store.key()!, ref, new Uint8Array(bytes), 'font/' + (ext === 'otf' ? 'otf' : ext === 'woff' ? 'woff' : 'ttf'));
        this.store.saver.usage.set(result.usage);
      } catch (e) {
        this.fontError.set(apiError(e).message);
        return;
      }
    } else {
      ref = toDataUrlBytes(new Uint8Array(bytes), 'font/' + ext);
    }
    this.store.change(s => {
      s.fonts[id] = { name, file: ref };
      for (const sid of this.store.selectedShapes()) { const t = s.shapes[sid]; if (t?.type === 'text') t.font = id; }
    });
  }

  protected degrees(v: number): number {
    return Math.round(v);
  }
}
