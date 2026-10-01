import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { SparkAttributeRendererRegistration, SparkResolvedRowRenderer, resolveRowRenderer } from '@mintplayer/ng-spark/renderers';

/** The row renderer an AsDetail attribute's row type names (`rowComponent`), or null. */
@Pipe({ name: 'asDetailRowRenderer', standalone: true, pure: true })
export class AsDetailRowRendererPipe implements PipeTransform {
  transform(
    attr: EntityAttributeDefinition,
    asDetailTypes: Record<string, EntityType>,
    renderers: readonly SparkAttributeRendererRegistration[] | null | undefined,
  ): SparkResolvedRowRenderer | null {
    return resolveRowRenderer(asDetailTypes[attr.name], renderers);
  }
}
