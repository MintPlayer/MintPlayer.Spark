import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType, ShowedOn, hasShowedOnFlag } from '@mintplayer/ng-spark/models';
import { SparkAttributeRendererRegistration, isRowRendered } from '@mintplayer/ng-spark/renderers';

/**
 * The columns of an AsDetail table: the row type's visible attributes, in order — minus those a
 * registered row renderer draws (`rowComponent`), when the host passes its renderer registry.
 *
 * An attribute the row type's model draws nowhere (`showedOn: None`, #264) is not a column. Columns are per
 * type, so they come from the model; a runtime `showedOn` on one row cannot add or remove a column.
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
      .filter(a => isDrawnSomewhere(a) && !isRowRendered(a, renderers))
      .sort((a, b) => a.order - b.order);
  }
}

function isDrawnSomewhere(attr: EntityAttributeDefinition): boolean {
  return hasShowedOnFlag(attr.showedOn, ShowedOn.Query) || hasShowedOnFlag(attr.showedOn, ShowedOn.PersistentObject);
}
