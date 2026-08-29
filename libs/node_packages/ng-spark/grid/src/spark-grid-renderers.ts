import { inject, Injectable, Type } from '@angular/core';
import {
  EntityAttributeDefinition,
  LookupReference,
  QueryColumn,
  QueryResultItem,
  SparkCellColumn,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { SPARK_ATTRIBUTE_RENDERERS, cellValue, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * Renderer lookup and lookup-reference loading for a Spark grid.
 *
 * This was extracted when `spark-query-list` and `spark-sub-query` were two components holding
 * byte-identical copies of it — around 120 lines between them. That duplication was not a
 * tidiness complaint: it produced drift. The two copies disagreed about `[indeterminate]`, about
 * resetting permission state, about whether a fetch failure surfaces or is swallowed, and about
 * virtual-scroll sizing — four user-visible bugs, each fixed on one side and not the other.
 *
 * There is now one grid, {@link SparkQueryGridComponent}, so the drift cannot recur. This stays
 * separate because it is stateless service-shaped logic rather than view state, and because it is
 * what a custom grid would need in order to render Spark cells at all.
 */
@Injectable({ providedIn: 'root' })
export class SparkGridRenderers {
  private readonly registry = inject(SPARK_ATTRIBUTE_RENDERERS);
  private readonly sparkService = inject(SparkService);

  /** The registered column component for a column, or null to fall back to the default cell. */
  columnComponentFor(column: SparkCellColumn): Type<any> | null {
    if (!column.renderer) return null;
    return this.registry.find(r => r.name === column.renderer)?.columnComponent ?? null;
  }

  /**
   * Inputs for a query-grid cell renderer, filtered to what the component actually declares —
   * `NgComponentOutlet` throws on an input the target does not have, which is what lets every
   * member of the renderer contract be optional.
   */
  columnInputsFor(component: Type<any>, item: QueryResultItem, column: QueryColumn): Record<string, any> {
    return this.cellInputsFor(component, cellValue(valueFor(item, column.name)), column, item);
  }

  /**
   * The same contract for a row that is not a query result — an AsDetail sub-table row, which is
   * a plain dictionary of embedded values with no id and no column metadata of its own, so its
   * value cannot be read the way {@link columnInputsFor} reads one.
   *
   * Only the extraction differs; the renderer contract is identical, and stating it once is what
   * stops the two from drifting the way the cell markup already had.
   */
  cellInputsFor(component: Type<any>, value: unknown, column: SparkCellColumn, item: unknown): Record<string, any> {
    return withDeclaredInputs(component, {
      value,
      column,
      options: column.rendererOptions,
      item,
    });
  }

  /**
   * Loads every lookup reference the visible columns need, in one pass.
   *
   * Returns an empty map rather than throwing when there are none, so callers never branch on it.
   */
  async loadLookupOptions(columns: readonly (SparkCellColumn | EntityAttributeDefinition)[]): Promise<Record<string, LookupReference>> {
    const lookupColumns = columns.filter(c => c.lookupReferenceType);
    if (lookupColumns.length === 0) return {};

    const names = [...new Set(lookupColumns.map(c => c.lookupReferenceType!))];
    const entries = await Promise.all(
      names.map(async name => [name, await this.sparkService.getLookupReference(name)] as const),
    );
    return entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>);
  }
}
