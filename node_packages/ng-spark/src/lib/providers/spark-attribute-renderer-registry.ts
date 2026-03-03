import { InjectionToken, Provider, Type } from '@angular/core';

export interface SparkAttributeRendererRegistration {
  /** The renderer name (must match attr.renderer in model JSON) */
  name: string;
  /** Component for the PO detail page. Must implement SparkAttributeDetailRenderer. */
  detailComponent: Type<any>;
  /** Component for query-list column cells. Must implement SparkAttributeColumnRenderer. */
  columnComponent: Type<any>;
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
