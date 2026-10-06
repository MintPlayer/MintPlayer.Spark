import { InjectionToken, Provider, Type } from '@angular/core';
import { SparkParagraphRendererComponent } from './paragraph-renderer.component';

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
   * A **row** renderer for AsDetail tables (the PO detail page and the edit form). Attributes of a row
   * type that carry this renderer are not drawn as columns; instead the component is drawn ONCE per
   * row, under the row's first cell, given all of them. Must implement {@link SparkAttributeRowRenderer}.
   *
   * For facts about a row rather than columns of it — contributions' "by Alice · 3 days ago ·
   * History (4)" line collapses three read-only attributes into one. `rendererOptions` are taken from
   * the first such attribute (a library writes the same options on each).
   */
  rowComponent?: Type<any>;
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
  /**
   * The detail page draws the renderer across the whole row, with no label: for text blocks such
   * as the core `paragraph` renderer, which are body text rather than a labelled value.
   */
  fullWidth?: boolean;
}

/**
 * The renderers ng-spark ships itself, available in every app without registration. An app's own
 * registration of the same name wins (it comes first in the list).
 */
export const sparkCoreRenderers: readonly SparkAttributeRendererRegistration[] = [
  { name: 'paragraph', detailComponent: SparkParagraphRendererComponent, fullWidth: true },
];

export const SPARK_ATTRIBUTE_RENDERERS = new InjectionToken<SparkAttributeRendererRegistration[]>(
  'SparkAttributeRenderers',
  { factory: () => [...sparkCoreRenderers] }
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
    // The app's first, so `find` by name lets it replace a core renderer.
    useValue: [...renderers, ...sparkCoreRenderers],
  };
}
