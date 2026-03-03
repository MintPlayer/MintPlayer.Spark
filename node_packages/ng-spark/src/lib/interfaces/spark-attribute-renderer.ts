import { InputSignal } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';

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
