import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { addConstraint, applyPositions } from '../core/edit';
import { evaluate } from '../core/evaluate';
import { constraintPointIds, type Id, type Scene } from '../core/model';
import { EditorStore } from '../editor-store';
import { applicableConstraints, CONSTRAINT_LABELS, selectionParts, type ConstraintOption } from '../selection';

/**
 * Constraints (D14): the ones that fit the selection, and the ones the drawing has, with what the solver says about
 * them. A conflicting constraint is red and is not applied; a redundant one is grey (it repeats what others say).
 */
@Component({
  selector: 'app-constraints-panel',
  imports: [FormsModule, BsBadgeComponent, BsButtonTypeDirective, BsFormComponent, BsFormControlDirective, BsListGroupComponent, BsListGroupItemComponent, SparkIconComponent],
  template: `
    @if (!store.readOnly()) {
      <h6>Add</h6>
      @if (options().length) {
        <div class="d-flex flex-wrap gap-1">
          @for (o of options(); track o.label) {
            <button type="button" [color]="colors.secondary" (click)="add(o)">{{ o.label }}</button>
          }
        </div>
      } @else {
        <p class="small text-body-secondary">
          Select two points or a line (horizontal, vertical, distance), two lines (parallel, perpendicular, equal, angle),
          a point and a line, a point and a circle, a line and a circle, or two circles. Shift-click adds to the selection.
        </p>
      }
      <hr />
    }

    <h6>In this drawing <bs-badge [color]="'secondary'">{{ store.analysis().dof }} DOF left</bs-badge></h6>
    <bs-list-group>
      @for (c of constraints(); track c.id) {
        <bs-list-group-item [active]="c.involved">
          <div class="item">
            <span class="grow" role="button" tabindex="0" (click)="selectPoints(c.points)" (keydown.enter)="selectPoints(c.points)">
              {{ c.label }}
              @if (c.conflict) { <bs-badge [color]="'danger'">conflict</bs-badge> }
              @if (c.redundant) { <bs-badge [color]="'secondary'">redundant</bs-badge> }
            </span>
            @if (c.value !== null && !store.readOnly()) {
              <bs-form>
                <input type="number" class="value" [attr.aria-label]="c.label + ' value'" [ngModel]="c.value" (ngModelChange)="setValue(c.id, +$event)" [ngModelOptions]="{ updateOn: 'blur' }" />
              </bs-form>
            }
            @if (!store.readOnly()) {
              <button type="button" [color]="colors.light" title="Remove" aria-label="Remove constraint" (click)="remove(c.id)"><spark-icon name="trash" /></button>
            }
          </div>
        </bs-list-group-item>
      } @empty {
        <bs-list-group-item [disabled]="true">No constraints. Points that share an id already move together.</bs-list-group-item>
      }
    </bs-list-group>
  `,
  styleUrl: './panels.scss',
  styles: `.value { width: 5rem; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConstraintsPanelComponent {
  protected readonly store = inject(EditorStore);
  protected readonly colors = Color;

  protected readonly options = computed(() => {
    const parts = selectionParts(this.store.scene(), this.store.selectedShapes(), this.store.selectedPoints());
    return applicableConstraints(parts, this.store.evaluated().positions);
  });

  protected readonly constraints = computed(() => {
    const scene = this.store.scene(), analysis = this.store.analysis();
    const conflicts = new Set([...analysis.conflicts, ...this.store.evaluated().conflicts]);
    const redundant = new Set(analysis.redundant);
    const selected = new Set(this.store.selectedPoints());
    return Object.entries(scene.constraints).map(([id, c]) => {
      const points = constraintPointIds(c);
      return {
        id,
        label: CONSTRAINT_LABELS[c.type],
        points,
        value: c.type === 'distance' ? c.value : c.type === 'angle' ? Math.round((c.value * 180) / Math.PI * 10) / 10 : null,
        conflict: conflicts.has(id),
        redundant: redundant.has(id),
        involved: points.some(p => selected.has(p)),
      };
    });
  });

  add(option: ConstraintOption): void {
    const c = option.make();
    this.store.change(s => {
      addConstraint(s, c);
      this.settle(s);
    });
  }

  remove(id: Id): void {
    this.store.change(s => { delete s.constraints[id]; });
  }

  setValue(id: Id, value: number): void {
    if (!Number.isFinite(value)) return;
    this.store.change(s => {
      const c = s.constraints[id];
      if (c?.type === 'distance') c.value = Math.max(0, value);
      else if (c?.type === 'angle') c.value = (value * Math.PI) / 180;
      this.settle(s);
    });
  }

  selectPoints(points: Id[]): void {
    this.store.select([], points.filter(p => this.store.scene().points[p]));
  }

  /** Writes the solved positions back, so the rest pose satisfies the new constraint. */
  private settle(scene: Scene): void {
    applyPositions(scene, evaluate(scene, { text: this.store.fonts }).positions);
  }
}
