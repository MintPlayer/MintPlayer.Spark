import { InjectionToken, Provider, Type } from '@angular/core';

export interface SparkAttributeRendererRegistration {
  /** The renderer name (must match attr.renderer in model JSON) */
  name: string;
  /** Component for the PO detail page. Must implement SparkAttributeDetailRenderer. Omit for a column/edit-only renderer. */
  detailComponent?: Type<any> | null;
  /** Component for query-list column cells. Must implement SparkAttributeColumnRenderer. Omit for a detail/edit-only renderer. */
  columnComponent?: Type<any> | null;
  /** Optional component for create/edit forms. Must implement SparkAttributeEditRenderer. When omitted, the default input is used. */
  editComponent?: Type<any>;
  /**
   * Relabels this column's values in a filter panel (#431).
   *
   * A renderer is a component that paints arbitrary DOM, and a distinct value has no row, so it
   * cannot be instantiated to produce text for a filter list. The label therefore defaults to the
   * server-computed cell text: a status rendered as a coloured pill lists `Active`, a rating
   * rendered as stars lists `3`. This is the escape for when that is too raw — a value stored as
   * `2` whose cell reads "High priority".
   *
   * ⚠️ A **pure function**: no DOM, no component instantiation, no row context. There is no row to
   * give it, which is the whole reason the renderer itself cannot be used here.
   */
  filterLabel?: (value: unknown) => string;
}

export const SPARK_ATTRIBUTE_RENDERERS = new InjectionToken<SparkAttributeRendererRegistration[]>(
  'SparkAttributeRenderers',
  { factory: () => [] }
);

/**
 * Register custom attribute renderers globally.
 *
 * @example
 * provideSparkAttributeRenderers([
 *   { name: 'video-player', detailComponent: VideoDetailComponent, columnComponent: VideoColumnComponent },
 *   { name: 'color-swatch', detailComponent: ColorDetailComponent, columnComponent: ColorColumnComponent },
 * ])
 */
export function provideSparkAttributeRenderers(
  renderers: SparkAttributeRendererRegistration[]
): Provider {
  return {
    provide: SPARK_ATTRIBUTE_RENDERERS,
    useValue: renderers,
  };
}
