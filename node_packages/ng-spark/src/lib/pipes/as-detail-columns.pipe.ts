import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '../models';

@Pipe({ name: 'asDetailColumns', standalone: true, pure: true })
export class AsDetailColumnsPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, asDetailTypes: Record<string, EntityType>): EntityAttributeDefinition[] {
    const type = asDetailTypes[attr.name];
    if (!type) return [];
    return type.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order);
  }
}
