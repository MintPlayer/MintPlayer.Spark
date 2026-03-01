import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '../models';

@Pipe({ name: 'asDetailType', standalone: true, pure: true })
export class AsDetailTypePipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, asDetailTypes: Record<string, EntityType>): EntityType | null {
    return asDetailTypes[attr.name] || null;
  }
}
