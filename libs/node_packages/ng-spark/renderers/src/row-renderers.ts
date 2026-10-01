import { Type } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { SparkAttributeRendererRegistration } from './spark-attribute-renderer-registry';
import { withDeclaredInputs } from './renderer-inputs';

/** The row renderer of an AsDetail row type, resolved once per type (see `rowComponent`). */
export interface SparkResolvedRowRenderer {
  component: Type<any>;
  /** The row-type attributes carrying the renderer, in model order. */
  attributes: EntityAttributeDefinition[];
  /** `rendererOptions` of the first of them. */
  options: Record<string, any> | undefined;
}

/** Whether `attribute` is drawn by a row renderer (and therefore not as a column). */
export function isRowRendered(attribute: EntityAttributeDefinition, registry: readonly SparkAttributeRendererRegistration[] | null | undefined): boolean {
  if (!attribute.renderer || !registry?.length) return false;
  return registry.some(r => r.name === attribute.renderer && !!r.rowComponent);
}

/**
 * The first row renderer the row type's visible attributes name, or null. One per row: a row type
 * whose attributes name two different row renderers gets the first in model order.
 */
export function resolveRowRenderer(
  rowType: EntityType | null | undefined,
  registry: readonly SparkAttributeRendererRegistration[] | null | undefined,
): SparkResolvedRowRenderer | null {
  if (!rowType || !registry?.length) return null;
  const ordered = [...(rowType.attributes ?? [])].filter(a => a.isVisible !== false).sort((a, b) => a.order - b.order);
  for (const attribute of ordered) {
    const registration = attribute.renderer ? registry.find(r => r.name === attribute.renderer && r.rowComponent) : undefined;
    if (!registration) continue;
    const attributes = ordered.filter(a => a.renderer === registration.name);
    return { component: registration.rowComponent!, attributes, options: attributes[0]?.rendererOptions };
  }
  return null;
}

/** The input bag for a row renderer, filtered to what the component declares. */
export function rowRendererInputs(renderer: SparkResolvedRowRenderer, row: Record<string, any>, ownerId: string | undefined): Record<string, any> {
  return withDeclaredInputs(renderer.component, {
    row,
    attributes: renderer.attributes,
    options: renderer.options,
    ownerId,
  });
}
