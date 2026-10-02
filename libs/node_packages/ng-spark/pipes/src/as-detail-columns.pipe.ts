import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { SparkAttributeRendererRegistration, isRowRendered } from '@mintplayer/ng-spark/renderers';

/**
 * The columns of an AsDetail table: the row type's visible attributes, in order — minus those a
 * registered row renderer draws (`rowComponent`), when the host passes its renderer registry.
 */
@Pipe({ name: 'asDetailColumns', standalone: true, pure: true })
export class AsDetailColumnsPipe implements PipeTransform {
  transform(
    attr: EntityAttributeDefinition,
    asDetailTypes: Record<string, EntityType>,
    renderers?: readonly SparkAttributeRendererRegistration[] | null,
  ): EntityAttributeDefinition[] {
    const type = asDetailTypes[attr.name];
    if (!type) return [];
    return type.attributes
      .filter(a => a.isVisible && !isRowRendered(a, renderers))
      .sort((a, b) => a.order - b.order);
  }
}
