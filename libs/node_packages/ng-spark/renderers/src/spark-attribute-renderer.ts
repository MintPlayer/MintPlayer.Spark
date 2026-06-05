import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';

/**
 * Contract for detail-page renderers (spark-po-detail).
 * Displays a single attribute value in the PO detail view.
 */
export interface SparkAttributeDetailRenderer {
  /** The current attribute value */
  value: InputSignal<any>;
  /** The attribute definition metadata */
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options: InputSignal<Record<string, any> | undefined>;
  /** The full form data (for cross-field dependencies) */
  formData: InputSignal<Record<string, any>>;
}

/**
 * Contract for query-list column renderers (spark-query-list).
 * Displays a compact cell value in the list/grid view.
 */
export interface SparkAttributeColumnRenderer {
  /** The current attribute value */
  value: InputSignal<any>;
  /** The attribute definition metadata */
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options: InputSignal<Record<string, any> | undefined>;
}

/**
 * Contract for edit-form renderers (spark-po-form on create/edit pages).
 * Replaces the default <input> for this attribute.
 */
export interface SparkAttributeEditRenderer {
  /** The current attribute value */
  value: InputSignal<any>;
  /** The attribute definition metadata */
  attribute: InputSignal<EntityAttributeDefinition | undefined>;
  /** Renderer-specific options from rendererOptions */
  options: InputSignal<Record<string, any> | undefined>;
  /** Callback to notify parent form of value changes (since NgComponentOutlet doesn't support outputs) */
  valueChange: InputSignal<(value: any) => void>;
}
