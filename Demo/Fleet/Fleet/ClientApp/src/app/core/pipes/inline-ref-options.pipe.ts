import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '../models';

@Pipe({ name: 'inlineRefOptions', standalone: true, pure: true })
export class InlineRefOptionsPipe implements PipeTransform {
  transform(parentAttr: EntityAttributeDefinition, col: EntityAttributeDefinition, asDetailReferenceOptions: Record<string, Record<string, PersistentObject[]>>): PersistentObject[] {
    return asDetailReferenceOptions[parentAttr.name]?.[col.name] || [];
  }
}
