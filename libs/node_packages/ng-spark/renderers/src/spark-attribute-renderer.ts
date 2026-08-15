import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';

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
   * The current attribute value. For an AsDetail attribute this is the nested
   * PersistentObject (single) / PersistentObject[] (array) in query-list and
   * sub-query grids, and the flattened value in AsDetail sub-table cells.
   */
  value?: InputSignal<any>;
  /** The attribute definition metadata */
  attribute?: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options?: InputSignal<Record<string, any> | undefined>;
  /**
   * The row this cell belongs to: a PersistentObject in query-list/sub-query
   * grids, a plain record (possibly including the reserved '__sparkBreadcrumbs'
   * key) in AsDetail sub-tables. Passed only when declared.
   */
  item?: InputSignal<PersistentObject | Record<string, any> | undefined>;
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
