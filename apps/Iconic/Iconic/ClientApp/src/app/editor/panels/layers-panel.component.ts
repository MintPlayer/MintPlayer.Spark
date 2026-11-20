import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsRangeComponent } from '@mintplayer/ng-bootstrap/range';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { addLayer, moveLayer, removeLayer } from '../core/edit';
import type { Id, LayerKind } from '../core/model';
import { EditorStore } from '../editor-store';

/** Layers (D2): shape layers hold vector shapes, paint layers hold pixels per output size. Top of the list is on top. */
@Component({
  selector: 'app-layers-panel',
  imports: [FormsModule, BsButtonTypeDirective, BsFormComponent, BsFormControlDirective, BsListGroupComponent, BsListGroupItemComponent, BsRangeComponent, SparkIconComponent],
  template: `
    <bs-list-group>
      @for (layer of layers(); track layer.id) {
        <bs-list-group-item [active]="layer.id === store.activeLayer()">
          <div class="item">
            <button type="button" [color]="colors.light" [title]="layer.visible ? 'Hide' : 'Show'" [attr.aria-label]="layer.visible ? 'Hide layer' : 'Show layer'"
                    [disabled]="store.readOnly()" (click)="set(layer.id, 'visible', !layer.visible)">
              <spark-icon [name]="layer.visible ? 'eye' : 'eye-slash'" />
            </button>
            <span class="grow" role="button" tabindex="0" (click)="store.activeLayerChoice.set(layer.id)" (keydown.enter)="store.activeLayerChoice.set(layer.id)">
              <spark-icon [name]="layer.kind === 'paint' ? 'brush' : 'bezier2'" /> {{ layer.name }}
            </span>
            <button type="button" [color]="colors.light" [title]="layer.locked ? 'Unlock' : 'Lock'" [attr.aria-label]="layer.locked ? 'Unlock layer' : 'Lock layer'"
                    [disabled]="store.readOnly()" (click)="set(layer.id, 'locked', !layer.locked)">
              <spark-icon [name]="layer.locked ? 'lock' : 'unlock'" />
            </button>
          </div>
        </bs-list-group-item>
      }
    </bs-list-group>

    @if (!store.readOnly()) {
      <div class="d-flex flex-wrap gap-1 mt-2">
        <button type="button" [color]="colors.secondary" (click)="add('shape')"><spark-icon name="plus-lg" /> Shape layer</button>
        <button type="button" [color]="colors.secondary" (click)="add('paint')"><spark-icon name="plus-lg" /> Paint layer</button>
      </div>

      @if (active(); as layer) {
        <hr />
        <h6>{{ layer.name }}</h6>
        <bs-form>
          <label for="layer-name">Name</label>
          <input id="layer-name" type="text" [ngModel]="layer.name" (ngModelChange)="set(layer.id, 'name', $event)" [ngModelOptions]="{ updateOn: 'blur' }" />
        </bs-form>
        <bs-range class="mt-2" [min]="0" [max]="1" [step]="0.05" [label]="'Opacity ' + (layer.opacity * 100).toFixed(0) + '%'"
                  [value]="layer.opacity" (valueChange)="set(layer.id, 'opacity', $event)" />
        <div class="d-flex flex-wrap gap-1 mt-2">
          <button type="button" [color]="colors.secondary" (click)="move(layer.id, 1)" title="Move up"><spark-icon name="chevron-up" /></button>
          <button type="button" [color]="colors.secondary" (click)="move(layer.id, -1)" title="Move down"><spark-icon name="chevron-down" /></button>
          <button type="button" [color]="colors.danger" [disabled]="layers().length <= 1" (click)="remove(layer.id)"><spark-icon name="trash" /> Delete</button>
        </div>
        @if (layer.kind === 'paint') {
          <p class="small text-body-secondary mt-2 mb-0">
            Painted at: {{ layer.sizes.length ? layer.sizes.join(', ') + ' px' : 'nothing yet' }}. A size without its own pixels uses the nearest painted one.
          </p>
        }
      }
    }
  `,
  styleUrl: './panels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LayersPanelComponent {
  protected readonly store = inject(EditorStore);
  protected readonly colors = Color;

  protected readonly layers = computed(() => {
    const s = this.store.scene();
    return [...s.layerOrder].reverse().map(id => ({ id, ...s.layers[id], sizes: Object.keys(s.layers[id].bitmaps).map(Number).sort((a, b) => a - b) }));
  });
  protected readonly active = computed(() => this.layers().find(l => l.id === this.store.activeLayer()) ?? null);

  set(id: Id, field: 'visible' | 'locked' | 'name' | 'opacity', value: boolean | string | number): void {
    this.store.change(s => { if (s.layers[id]) (s.layers[id] as unknown as Record<string, unknown>)[field] = value; });
  }

  add(kind: LayerKind): void {
    let created: Id | null = null;
    const above = this.store.activeLayer() ?? undefined;
    this.store.change(s => { created = addLayer(s, kind, undefined, above); });
    this.store.activeLayerChoice.set(created);
  }

  move(id: Id, delta: number): void {
    this.store.change(s => moveLayer(s, id, delta));
  }

  remove(id: Id): void {
    const layer = this.store.scene().layers[id];
    if (layer && (layer.shapes.length || Object.keys(layer.bitmaps).length) && !confirm(`Delete "${layer.name}" and everything on it?`)) return;
    this.store.change(s => removeLayer(s, id));
    this.store.activeLayerChoice.set(null);
    this.store.pruneSelection();
  }
}
