import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject, QueryResultItem, SparkCellColumn } from '@mintplayer/ng-spark/models';

// All contract members are optional: the hosts filter the input bag down to what
// the component actually declares (withDeclaredInputs), so a renderer opts in to
// exactly the inputs it needs.

/**
 * Contract for detail-page renderers (spark-po-detail).
 * Displays a single attribute value in the PO detail view.
 */
export interface SparkAttributeDetailRenderer {
  /**
   * The current attribute value. For an AsDetail attribute this is the nested
   * PersistentObject (single) or PersistentObject[] (array).
   */
  value?: InputSignal<any>;
  /** The attribute definition metadata */
  attribute?: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options?: InputSignal<Record<string, any> | undefined>;
  /** The full form data (for cross-field dependencies); AsDetail keys carry the nested PO(s) */
  formData?: InputSignal<Record<string, any>>;
  /** The full PersistentObject being displayed — ids/breadcrumbs the flattened formData drops */
  item?: InputSignal<PersistentObject | undefined>;
}

/**
 * Contract for query-list column renderers (spark-query-list).
 * Displays a compact cell value in the list/grid view.
 */
export interface SparkAttributeColumnRenderer {
  /**
   * The cell value. For an AsDetail column this is the nested PersistentObject (single) /
   * PersistentObject[] (array) in an AsDetail sub-table, and the projected value in a query grid.
   */
  value?: InputSignal<any>;
  /**
   * The column being rendered.
   *
   * A query grid supplies a {@link SparkCellColumn} from the result own column metadata; an
   * AsDetail sub-table supplies its attribute definition, which satisfies the same shape. This
   * replaced an `attribute` input typed as the definition: a query result no longer carries
   * attribute metadata per row, so naming it `attribute` would promise something the grid cannot
   * deliver.
   */
  column?: InputSignal<SparkCellColumn | undefined>;
  /** Renderer-specific options from rendererOptions */
  options?: InputSignal<Record<string, any> | undefined>;
  /**
   * The row this cell belongs to: a {@link QueryResultItem} in a query grid, a plain record
   * (possibly including the reserved '__sparkBreadcrumbs' key) in AsDetail sub-tables. Passed
   * only when declared.
   */
  item?: InputSignal<QueryResultItem | Record<string, any> | undefined>;
}

/**
 * Contract for edit-form renderers (spark-po-form on create/edit pages).
 * Replaces the default <input> for this attribute.
 */
export interface SparkAttributeEditRenderer {
  /** The current attribute value */
  value?: InputSignal<any>;
  /** The attribute definition metadata */
  attribute?: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options?: InputSignal<Record<string, any> | undefined>;
  /**
   * Callback to notify parent form of value changes (since NgComponentOutlet
   * doesn't support outputs). Not declaring it disables write-back.
   */
  valueChange?: InputSignal<(value: any) => void>;
}
