import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { SparkAttributeRendererRegistration, SparkResolvedRowRenderer, resolveLeadingRenderer } from '@mintplayer/ng-spark/renderers';

/** The leading-cell renderer an AsDetail attribute's row type names (`leadingComponent`), or null. */
@Pipe({ name: 'asDetailLeadingRenderer', standalone: true, pure: true })
export class AsDetailLeadingRendererPipe implements PipeTransform {
  transform(
    attr: EntityAttributeDefinition,
    asDetailTypes: Record<string, EntityType>,
    renderers: readonly SparkAttributeRendererRegistration[] | null | undefined,
  ): SparkResolvedRowRenderer | null {
    return resolveLeadingRenderer(asDetailTypes[attr.name], renderers);
  }
}
