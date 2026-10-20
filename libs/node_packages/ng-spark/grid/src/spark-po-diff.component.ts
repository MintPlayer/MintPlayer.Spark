import { ChangeDetectionStrategy, Component, Type, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { QueryCellValuePipe, QueryReferenceChipsPipe, ReferenceChip, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { cellValue, rendererValue } from '@mintplayer/ng-spark/renderers';
import {
  EntityAttributeDefinition, LookupReference, PersistentObject, QueryColumn, QueryResultItem, SparkRevisionChange, valueFor,
} from '@mintplayer/ng-spark/models';
import { SparkGridRenderers } from './spark-grid-renderers';
import { SparkGridCellComponent } from './spark-grid-cell.component';

/** One side of one attribute, ready for `<spark-grid-cell>`. */
interface DiffCell {
  column: QueryColumn;
  display: unknown;
  rendererValue: unknown;
  chips: ReferenceChip[];
}

/** One row of the diff: either a registered diff renderer, or the generic before → after cells. */
interface DiffRow {
  change: SparkRevisionChange;
  component: Type<any> | null;
  inputs: Record<string, any>;
  before: DiffCell;
  after: DiffCell;
}

const cellValuePipe = new QueryCellValuePipe();
const chipsPipe = new QueryReferenceChipsPipe();

/**
 * A per-attribute diff of two reads of an object (commerce #521): Approvals' proposal diff, the current
 * row against the proposed one. Each changed attribute is drawn by its renderer's `diffComponent` when one
 * is registered, else as the generic before → after view, each side rendered by the same
 * `<spark-grid-cell>` as a query grid (a reference as its label, a date in the viewer's zone).
 *
 * `changes` is a list such as `diffRevision(entityType, before, after)` answers; `before` / `after` are the
 * two objects, passed through to cell renderers as `item`. With `showBefore` false (a creation) the
 * "before" column is left out and only the proposed values are drawn.
 */
@Component({
  selector: 'spark-po-diff',
  imports: [NgComponentOutlet, BsTableComponent, SparkGridCellComponent, ResolveTranslationPipe],
  templateUrl: './spark-po-diff.component.html',
  styleUrl: './spark-po-diff.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoDiffComponent {
  private readonly gridRenderers = inject(SparkGridRenderers);

  changes = input.required<SparkRevisionChange[]>();
  before = input<PersistentObject | null>(null);
  after = input<PersistentObject | null>(null);
  /** Draws the "before" column; false for a creation, which has nothing before. */
  showBefore = input(true);
  /** Column headers, already translated by the caller. */
  fieldLabel = input('');
  beforeLabel = input('');
  afterLabel = input('');

  /** Lookup-reference labels for the changed attributes, so a lookup value reads as its label. */
  private readonly lookupOptions = signal<Record<string, LookupReference>>({});

  protected readonly rows = computed((): DiffRow[] => {
    const before = this.before();
    const after = this.after();
    const lookups = this.lookupOptions();
    return this.changes().map(change => {
      const component = this.gridRenderers.diffComponentFor(change.attribute);
      return {
        change,
        component,
        inputs: component
          ? this.gridRenderers.diffInputsFor(component, rendererValue(change.revision), rendererValue(change.current), change.attribute)
          : {},
        before: this.cell(change.attribute, before, lookups),
        after: this.cell(change.attribute, after, lookups),
      };
    });
  });

  constructor() {
    effect(() => {
      const attributes = this.changes().map(c => c.attribute);
      untracked(() => {
        this.gridRenderers.loadLookupOptions(attributes).then(o => this.lookupOptions.set(o), () => this.lookupOptions.set({}));
      });
    });
  }

  private cell(attribute: EntityAttributeDefinition, po: PersistentObject | null, lookups: Record<string, LookupReference>): DiffCell {
    const column = attribute as unknown as QueryColumn;
    const row = po as unknown as QueryResultItem | null;
    const display = po ? cellValuePipe.transform(column, row, lookups) : null;
    return {
      column,
      // The detail page's placeholder for an empty value; a boolean keeps null (an indeterminate box).
      display: attribute.dataType !== 'boolean' && (display == null || display === '') ? '-' : display,
      rendererValue: po ? cellValue(valueFor(row, attribute.name)) : null,
      chips: po ? chipsPipe.transform(column, row) : [],
    };
  }
}
