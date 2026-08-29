import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CommonModule, NgComponentOutlet } from '@angular/common';
import { RouterModule } from '@angular/router';
import { EntityAttributeDefinition, SparkCellColumn } from '@mintplayer/ng-spark/models';
import { ReferenceChip } from '@mintplayer/ng-spark/pipes';
import { SparkGridRenderers } from './spark-grid-renderers';

/**
 * One cell of a Spark table, wherever that table is.
 *
 * ## Why this exists
 *
 * The cell markup was written out three times: twice in the query grids (since merged into
 * {@link SparkQueryGridComponent}) and once more in the AsDetail table on the PO detail page. The
 * third copy was never counted, and it had already drifted — a `boolean` column rendered the text
 * `"true"` there while the grid rendered a checkbox, a `color` rendered its hex string rather than
 * a swatch, and a custom renderer was dispatched by a second, hand-copied lookup.
 *
 * ## What it does and does not own
 *
 * It owns **presentation**: which control a `dataType` becomes, and dispatching a declared custom
 * renderer. It does not own **value resolution**, and deliberately so — the two callers read from
 * genuinely different row models. A query row is a `PersistentObject` with an attribute list and
 * an id; an AsDetail row is a plain dictionary of embedded values with neither. Trying to unify
 * that would put a second row-access path inside this component and buy nothing.
 *
 * So the caller resolves the value with whichever pipe fits its row model, and passes the result.
 *
 * ## The two links are not the same link, and only one is here
 *
 * `link` is for a cell whose own value points at another entity — an AsDetail reference column.
 * The query grid's first-column link is a different rule (it points at the row's own detail page,
 * gated on `Read`), and it stays in the grid, wrapping this component. Passing both would nest
 * anchors, which is invalid HTML; the grid therefore leaves `link` unset.
 */
@Component({
  selector: 'spark-grid-cell',
  imports: [CommonModule, NgComponentOutlet, RouterModule],
  templateUrl: './spark-grid-cell.component.html',
  styleUrl: './spark-grid-cell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkGridCellComponent {
  private readonly renderers = inject(SparkGridRenderers);

  /** The column being rendered. Its `dataType` and `renderer` decide everything below. */
  column = input.required<SparkCellColumn>();

  /**
   * What the caller's value pipe produced: the display text for most columns, and the raw value
   * for `boolean` and `color`, which need the value rather than a rendering of it.
   */
  display = input<unknown>(null);

  /**
   * The value handed to a declared custom renderer.
   *
   * Separate from {@link display} because they are not the same thing: a renderer receives the
   * underlying value — for AsDetail, the nested object itself — while `display` has already been
   * flattened to something printable.
   */
  rendererValue = input<unknown>(null);

  /** The row, passed through to a custom renderer as `item`. */
  item = input<unknown>(null);

  /**
   * Pre-resolved chips for a reference-array column, or empty when the column is not one.
   *
   * Resolved by the caller because the label source differs: a query row carries a per-id
   * breadcrumb map, while an AsDetail row's `__sparkBreadcrumbs` is keyed by column name only and
   * so cannot label the members of an array. Rather than render a row of raw ids, an AsDetail
   * caller passes nothing and the cell falls through to text.
   */
  chips = input<ReferenceChip[]>([]);

  /** Route for a cell whose value references another entity. See the class comment. */
  link = input<unknown[] | null>(null);

  protected readonly rendererComponent = computed(() => this.renderers.columnComponentFor(this.column()));

  protected readonly rendererInputs = computed(() => {
    const component = this.rendererComponent();
    return component
      ? this.renderers.cellInputsFor(component, this.rendererValue(), this.column(), this.item())
      : {};
  });

  /** Null and undefined are distinct from `false` here — see the template. */
  protected readonly isUnsetBoolean = computed(() => this.display() == null);
}
