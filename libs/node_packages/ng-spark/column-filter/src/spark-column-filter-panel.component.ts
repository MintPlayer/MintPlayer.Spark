import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { DistinctValue, DistinctValues, FilterContext } from '@mintplayer/ng-bootstrap/datatable';
import { QueryColumn } from '@mintplayer/ng-spark/models';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/**
 * The contents of one column's filter panel (#431).
 *
 * Spark supplies its own panel on **every** filterable column rather than using the datatable's
 * built-in one. Two reasons, and the first forces the second:
 *
 * - A `canListDistincts: false` column has no built-in mode that fits. `FilterMode` is
 *   `'values' | 'comparison'` and there is no `contains` operator, so a free-text filter cannot be
 *   expressed — and resolving `null` from the distinct source is not an escape either, because that
 *   means "fall back to the local pass", which is disabled for a `[fetch]`-bound grid. Such a column
 *   would render the built-in panel showing its "no values" string: a dead panel.
 * - Since some columns must nest regardless, mixing would put two visually different panels in one
 *   grid.
 *
 * What the datatable still owns, and this component must not reimplement: the async distinct source
 * and its debounce, request cancellation, the matching/remaining bucketing, the change event, the
 * document-root overlay, the focus trap and Escape. This is markup over {@link FilterContext}.
 *
 * ⚠️ **Styling is ours.** The panel is portalled out of the grid's subtree, so Bootstrap's
 * `.form-control` and `.btn` do not reach it — they are only styled inside `bs-*` components. Every
 * visual here is explicit.
 */
@Component({
  selector: 'spark-column-filter-panel',
  standalone: true,
  imports: [SparkIconComponent],
  templateUrl: './spark-column-filter-panel.component.html',
  styleUrl: './spark-column-filter-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkColumnFilterPanelComponent {
  readonly column = input.required<QueryColumn>();

  /** The datatable's bucketed list, or null while nothing has loaded. */
  readonly values = input<DistinctValues | null>(null);

  readonly ctx = input.required<FilterContext>();

  /**
   * Optional relabelling for a column with a custom cell renderer.
   *
   * A renderer is a component that paints arbitrary DOM, and a distinct value has no row, so it
   * cannot be instantiated to produce text. The label therefore defaults to the server-computed cell
   * text — a status rendered as a coloured pill lists `Active`, a rating rendered as stars lists `3`.
   * This hook is the escape for the case where that is too raw: a pure function, no DOM, no row.
   */
  readonly filterLabel = input<((value: unknown) => string) | null>(null);

  /** Whether this column offers a value list at all, or only free text. */
  protected readonly listsDistincts = computed(() => this.column().canListDistincts !== false);

  /** Selected values, by value. Identity is the value, never the label — two rows may share text. */
  private readonly selected = signal<DistinctValue[]>([]);

  protected readonly inverse = signal(false);

  protected readonly freeText = signal('');

  protected readonly hasSelection = computed(() => this.selected().length > 0);

  protected readonly matching = computed(() => this.values()?.matching ?? []);
  protected readonly remaining = computed(() => this.values()?.remaining ?? []);
  protected readonly hasMore = computed(() => this.values()?.hasMore === true);

  protected labelFor(value: DistinctValue): string {
    const relabel = this.filterLabel();
    return relabel ? relabel(value.value) : value.label;
  }

  protected isChecked(value: DistinctValue): boolean {
    return this.selected().some(v => sameValue(v.value, value.value));
  }

  protected toggle(value: DistinctValue, checked: boolean): void {
    const next = checked
      ? [...this.selected(), value]
      : this.selected().filter(v => !sameValue(v.value, value.value));

    this.selected.set(next);
    this.ctx().apply(next, this.inverse());
  }

  protected toggleInverse(): void {
    // Not a flag on the wire: the grid moves the selected values between `includes` and `excludes`.
    // Applying immediately keeps the panel's state and the grid's in step without an Apply button.
    this.inverse.update(v => !v);
    this.ctx().apply(this.selected(), this.inverse());
  }

  protected onSearch(term: string): void {
    // Debounced and cancelled by the datatable; re-queried only when it cannot answer locally.
    this.ctx().search(term);
  }

  /**
   * Applies the free-text filter of a column whose values may not be enumerated.
   *
   * It goes through the same `apply` as a checkbox list, carrying one synthetic entry, so the grid
   * has exactly one event shape to translate and no second wire form to maintain.
   */
  protected applyFreeText(): void {
    const text = this.freeText().trim();
    const next: DistinctValue[] = text ? [{ value: text, label: text }] : [];

    this.selected.set(next);
    this.ctx().apply(next, this.inverse());
  }

  protected clear(): void {
    this.selected.set([]);
    this.inverse.set(false);
    this.freeText.set('');
    this.ctx().clear();
  }
}

/**
 * SameValueZero, matching how the datatable keys its list.
 *
 * `NaN` must equal itself or a numeric column's "not a number" entry can be ticked but never
 * unticked, and `===` gets that wrong.
 */
function sameValue(a: unknown, b: unknown): boolean {
  return a === b || (Number.isNaN(a as number) && Number.isNaN(b as number));
}
