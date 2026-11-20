import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { evaluate } from '../core/evaluate';
import { newId, newOutput, STANDARD_SIZES, type Id, type Output, type StrokeMode } from '../core/model';
import { renderToImageData } from '../core/render';
import { EditorStore } from '../editor-store';

/**
 * The sizes an export writes (D1, D17), each with how its outlines scale, whether an ICO/CUR entry is PNG or BMP, and
 * a live preview at its real pixels. Preview turns the canvas into that size, pixel for pixel, which is also the size
 * the paint tools paint into.
 */
@Component({
  selector: 'app-outputs-panel',
  imports: [FormsModule, BsButtonTypeDirective, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsListGroupComponent, BsListGroupItemComponent, BsSelectComponent, BsSelectOption, SparkIconComponent],
  template: `
    <bs-list-group>
      @for (o of outputs(); track o.id) {
        <bs-list-group-item [active]="store.previewWidth() === o.width">
          <div class="item">
            <img class="thumb" [src]="previews()[o.id]" [alt]="o.width + ' px preview'" [style.width.px]="Math.min(36, o.width)" [style.height.px]="Math.min(36, o.height)" />
            <span class="grow">{{ o.width }} × {{ o.height }}</span>
            <button type="button" [color]="store.previewWidth() === o.width ? colors.primary : colors.light" [title]="store.previewWidth() === o.width ? 'Back to the design' : 'Preview at this size'"
                    (click)="store.previewWidth.set(store.previewWidth() === o.width ? null : o.width)">
              <spark-icon name="zoom-in" />
            </button>
          </div>
          @if (!store.readOnly()) {
            <div class="row-2 mt-1">
              <bs-select [ngModel]="o.strokeMode" (ngModelChange)="set(o.id, { strokeMode: $event })" aria-label="Outline width">
                <option [ngValue]="'scale'">Outlines scale</option>
                <option [ngValue]="'min'">At least {{ o.minStroke }} px</option>
                <option [ngValue]="'fixed'">Fixed width</option>
              </bs-select>
              @if (store.kind() === 'Icon' || store.kind() === 'Cursor') {
                <bs-select [ngModel]="o.encoding" (ngModelChange)="set(o.id, { encoding: $event })" aria-label="Encoding">
                  <option [ngValue]="'png'">PNG entry</option>
                  <option [ngValue]="'bmp'">BMP entry</option>
                </bs-select>
              }
            </div>
            <div class="d-flex align-items-center gap-2 mt-1">
              <bs-checkbox [type]="'checkbox'" [name]="'out-' + o.id" [checked]="o.enabled" (checkedChange)="set(o.id, { enabled: $event === true })">Export</bs-checkbox>
              <span class="flex-grow-1"></span>
              <button type="button" [color]="colors.light" title="Remove this size" aria-label="Remove this size" (click)="remove(o.id)"><spark-icon name="trash" /></button>
            </div>
          }
        </bs-list-group-item>
      } @empty {
        <bs-list-group-item [disabled]="true">No sizes yet.</bs-list-group-item>
      }
    </bs-list-group>

    @if (!store.readOnly()) {
      <div class="d-flex flex-wrap gap-1 mt-2">
        @for (size of missing(); track size) {
          <button type="button" [color]="colors.secondary" (click)="add(size, size)">+ {{ size }}</button>
        }
      </div>
      <bs-form>
        <div class="d-flex align-items-end gap-1 mt-2">
          <div><label for="custom-w">Width</label><input id="custom-w" type="number" min="1" max="1024" [ngModel]="customW()" (ngModelChange)="customW.set(+$event)" /></div>
          <div><label for="custom-h">Height</label><input id="custom-h" type="number" min="1" max="1024" [ngModel]="customH()" (ngModelChange)="customH.set(+$event)" /></div>
          <button type="button" [color]="colors.secondary" (click)="add(customW(), customH())">Add</button>
        </div>
      </bs-form>
      <p class="small text-body-secondary mt-2 mb-0">ICO and CUR entries go up to 256 px; larger sizes export as PNG, GIF or WebP.</p>

      @if (store.kind() === 'Cursor') {
        <hr />
        <h6>Hotspot</h6>
        <bs-form>
          <div class="d-flex gap-1">
            <div><label for="hot-x">X</label><input id="hot-x" type="number" [ngModel]="store.scene().hotspot.x" (ngModelChange)="setHotspot('x', +$event)" [ngModelOptions]="{ updateOn: 'blur' }" /></div>
            <div><label for="hot-y">Y</label><input id="hot-y" type="number" [ngModel]="store.scene().hotspot.y" (ngModelChange)="setHotspot('y', +$event)" [ngModelOptions]="{ updateOn: 'blur' }" /></div>
          </div>
        </bs-form>
        <p class="small text-body-secondary mt-1 mb-0">In design pixels; each size gets it scaled. The hotspot tool sets it by clicking.</p>
      }

      <hr />
      <h6>Design</h6>
      <bs-form>
        <div class="d-flex gap-1">
          <div><label for="design-w">Width</label><input id="design-w" type="number" min="8" max="4096" [ngModel]="store.scene().width" (ngModelChange)="setDesign('width', +$event)" [ngModelOptions]="{ updateOn: 'blur' }" /></div>
          <div><label for="design-h">Height</label><input id="design-h" type="number" min="8" max="4096" [ngModel]="store.scene().height" (ngModelChange)="setDesign('height', +$event)" [ngModelOptions]="{ updateOn: 'blur' }" /></div>
        </div>
      </bs-form>
      <p class="small text-body-secondary mt-1 mb-0">Shapes are drawn in design pixels (D16) and scaled to every size.</p>
    }
  `,
  styleUrl: './panels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OutputsPanelComponent {
  protected readonly store = inject(EditorStore);
  protected readonly colors = Color;
  protected readonly Math = Math;
  protected readonly customW = signal(96);
  protected readonly customH = signal(96);

  protected readonly outputs = computed(() => {
    const s = this.store.scene();
    return s.outputOrder.filter(id => s.outputs[id]).map(id => ({ id, ...s.outputs[id] }));
  });
  protected readonly missing = computed(() => STANDARD_SIZES.filter(size => !this.outputs().some(o => o.width === size && o.height === size)));

  /** Small live previews, rendered as each size really comes out. */
  protected readonly previews = computed(() => {
    const scene = this.store.scene();
    this.store.paint.version();
    this.store.fontsVersion();
    const { positions } = evaluate(scene, { text: this.store.fonts });
    const out: Record<Id, string> = {};
    for (const o of this.outputs()) {
      if (o.width > 256) continue;
      const img = renderToImageData(scene, { width: o.width, height: o.height, output: o, positions, paint: this.store.paint, text: this.store.fonts });
      const c = document.createElement('canvas');
      c.width = o.width;
      c.height = o.height;
      c.getContext('2d')!.putImageData(img, 0, 0);
      out[o.id] = c.toDataURL('image/png');
    }
    return out;
  });

  set(id: Id, patch: Partial<Output>): void {
    this.store.change(s => { if (s.outputs[id]) s.outputs[id] = { ...s.outputs[id], ...patch }; });
  }

  add(width: number, height: number): void {
    if (!(width >= 1 && width <= 1024 && height >= 1 && height <= 1024)) return;
    this.store.change(s => {
      const id = newId('o');
      s.outputs[id] = { ...newOutput(width), height };
      s.outputOrder.push(id);
      s.outputOrder.sort((a, b) => s.outputs[a].width - s.outputs[b].width);
    });
  }

  remove(id: Id): void {
    const width = this.store.scene().outputs[id]?.width;
    this.store.change(s => {
      delete s.outputs[id];
      s.outputOrder = s.outputOrder.filter(o => o !== id);
    });
    if (this.store.previewWidth() === width) this.store.previewWidth.set(null);
  }

  setHotspot(axis: 'x' | 'y', value: number): void {
    if (!Number.isFinite(value)) return;
    this.store.change(s => { s.hotspot = { ...s.hotspot, [axis]: Math.max(0, Math.min(axis === 'x' ? s.width : s.height, value)) }; });
  }

  /** Resizing the design keeps the shapes where they are (no scaling); the outputs keep their sizes. */
  setDesign(axis: 'width' | 'height', value: number): void {
    if (!(value >= 8 && value <= 4096)) return;
    this.store.change(s => { s[axis] = Math.round(value); });
  }

  protected strokeModes: StrokeMode[] = ['scale', 'min', 'fixed'];
}
